using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Server side of the texture catalog: what the server knows about, its thumbnails, sending
    // texture bytes to the clients that ask, and taking player uploads. The catalog is the server's
    // own sails folder plus the uploads folder, whose index file remembers each upload's uploader
    // and status (denied ones too, so they can't be sent again).
    public static class SailServer
    {
        // Small chunks, sent only while the peer's send queue is nearly empty, so world updates
        // (ZDOMan sends only below 10 KB queued) keep flowing during a transfer. Uploads use the
        // same size and pacing in the other direction.
        internal const int ChunkBytes = 32 * 1024;
        internal const int MaxQueuedBytes = 8 * 1024;

        // An upload with no new chunk for this long is dropped.
        private const float UploadTimeoutSeconds = 60f;

        private const string IndexFileName = "index.txt";
        private const string IndexHeader = "# hash\tfile\tname\tstatus\tuploader id\tuploader name\tdate\tdecided by id\tdecided by name\tdecided date";

        // Every ZDO the server holds, for counting the ships that use a texture. Private in ZDOMan.
        private static readonly FieldInfo s_objectsById = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
        private static readonly int s_textureKey = Plugin.ZdoTextureKey.GetStableHashCode();

        private class Transfer
        {
            public long Peer;
            public string Hash;
            public byte[] Data;
            public int NextChunk;
            public int ChunkCount;
        }

        private static readonly Dictionary<string, CatalogEntry> s_entries = new Dictionary<string, CatalogEntry>();
        private static readonly Dictionary<string, byte[]> s_thumbnails = new Dictionary<string, byte[]>();
        private static readonly Dictionary<string, string> s_paths = new Dictionary<string, string>();
        private static readonly List<Transfer> s_transfers = new List<Transfer>();
        private static Coroutine s_pump;

        private class Upload
        {
            public string Hash;
            public string Name;
            public int Size;
            public byte[][] Chunks;
            public int Received;
            public float LastActivity;
        }

        // At most one upload in progress per peer.
        private static readonly Dictionary<long, Upload> s_uploads = new Dictionary<long, Upload>();

        // Index lines that couldn't be loaded (file missing or changed), written back unchanged
        // so a fixable problem doesn't erase the record.
        private static readonly List<string> s_unloadedIndexLines = new List<string>();

        public static string UploadsFolder => Path.Combine(Paths.ConfigPath, "ShipwrightsTouch", "uploads");

        // Called on the server at session start.
        public static void StartSession(ZNet net)
        {
            s_entries.Clear();
            s_thumbnails.Clear();
            s_paths.Clear();
            s_transfers.Clear();
            s_uploads.Clear();
            s_unloadedIndexLines.Clear();
            s_pump = null;
            if (ZNet.IsSinglePlayer) return;

            SailTextures.Refresh();
            foreach (SailTextures.Entry entry in SailTextures.Entries)
            {
                byte[] bytes = File.ReadAllBytes(entry.Path);
                byte[] thumbnail = SailThumbnails.Make(bytes, out int width, out int height, out string error);
                if (thumbnail == null)
                {
                    Log.Warn($"Skipping server sail texture {entry.Name}: {error}.");
                    continue;
                }

                s_entries[entry.Hash] = new CatalogEntry
                {
                    Hash = entry.Hash,
                    Name = entry.Name,
                    Source = TextureSource.Server,
                    Status = TextureStatus.Approved,
                    Width = width,
                    Height = height,
                    Bytes = bytes.Length
                };
                s_thumbnails[entry.Hash] = thumbnail;
                s_paths[entry.Hash] = entry.Path;
            }
            Log.Info($"Serving {s_entries.Count} sail texture(s) from the server's sails folder.");

            LoadUploads();
            SailDownloads.SetHostCatalog(VisibleTo(ZNet.GetUID()));

            s_pump = net.StartCoroutine(Pump());
        }

        // What a peer may see: the server's textures and approved uploads; pending ones only by
        // their uploader and moderators; denied ones only by their uploader (to show the status).
        private static List<CatalogEntry> VisibleTo(long peer)
        {
            if (!SailPolicy.AllowCustomTexturesConfig.Value) return new List<CatalogEntry>();

            string viewerId = ServerRoles.PeerId(peer);
            bool moderator = ServerRoles.PeerHas(peer, ServerRoles.Moderate);
            return s_entries.Values
                .Where(e =>
                {
                    bool mine = e.UploaderId != "" && e.UploaderId == viewerId;
                    switch (e.Status)
                    {
                        case TextureStatus.Approved: return true;
                        case TextureStatus.Pending: return mine || moderator;
                        default: return mine;
                    }
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void SendCatalog(long peer)
        {
            var package = new ZPackage();
            CatalogEntry.WriteList(package, VisibleTo(peer), ServerRoles.PeerId(peer));
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, SailNetwork.CatalogRpc, package);
        }

        // Every modded peer, and this game's own panel when it's hosting (a host gets no catalog
        // over the network, being the server).
        public static void SendCatalogToAll()
        {
            foreach (long peer in SailNetwork.ModdedPeers) SendCatalog(peer);
            SailDownloads.SetHostCatalog(VisibleTo(ZNet.GetUID()));
        }

        // A thumbnail this server made, for the hosting player's panel. Null on clients.
        public static byte[] ThumbnailOf(string hash)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || hash == null) return null;
            return s_thumbnails.TryGetValue(hash, out byte[] png) ? png : null;
        }

        // The file behind a texture this server may show, for when this game is also a player
        // (hosting): uploads aren't in its own sails folder. Null on clients.
        public static string PathOf(string hash)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || hash == null) return null;
            if (!s_entries.TryGetValue(hash, out CatalogEntry entry) || entry.Status == TextureStatus.Denied) return null;
            return s_paths.TryGetValue(hash, out string path) ? path : null;
        }

        public static void RPC_GetThumbnails(long sender, ZPackage request)
        {
            if (!FromModdedPeer(sender)) return;

            // Moderators also get the textures only they review (others' denied ones).
            var visible = new HashSet<string>(VisibleTo(sender).Select(e => e.Hash));
            if (ServerRoles.PeerHas(sender, ServerRoles.Moderate))
                visible.UnionWith(s_entries.Values.Where(e => e.Source == TextureSource.Player).Select(e => e.Hash));
            int count = Math.Min(request.ReadInt(), 256);
            var reply = new ZPackage();
            var hashes = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string hash = request.ReadString();
                if (visible.Contains(hash) && s_thumbnails.ContainsKey(hash)) hashes.Add(hash);
            }

            reply.Write(hashes.Count);
            foreach (string hash in hashes)
            {
                reply.Write(hash);
                reply.Write(s_thumbnails[hash]);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, SailNetwork.ThumbnailsRpc, reply);
        }

        public static void RPC_GetTexture(long sender, string hash)
        {
            if (!FromModdedPeer(sender)) return;
            bool visible = VisibleTo(sender).Any(e => e.Hash == hash) || IsModeratedBy(sender, hash);
            if (!visible || !s_paths.TryGetValue(hash, out string path)) return;
            if (s_transfers.Any(t => t.Peer == sender && t.Hash == hash)) return;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException e)
            {
                Log.Warn($"Could not read sail texture {path}: {e.Message}");
                return;
            }

            s_transfers.Add(new Transfer
            {
                Peer = sender,
                Hash = hash,
                Data = data,
                ChunkCount = Math.Max(1, (data.Length + ChunkBytes - 1) / ChunkBytes)
            });
            Log.Debug($"Sending sail texture {hash.Substring(0, 8)} ({data.Length} bytes) to peer {sender}.");
        }

        public static void PeerLeft(long peer)
        {
            s_transfers.RemoveAll(t => t.Peer == peer);
            s_uploads.Remove(peer);
        }

        // ── Uploads ──────────────────────────────────────────────────────

        // A player offers a texture before sending it, so a refusal costs one small message.
        public static void RPC_OfferUpload(long sender, ZPackage package)
        {
            if (!SailNetwork.IsModdedPeer(sender)) return;

            string hash = package.ReadString();
            string name = CatalogEntry.CleanName(package.ReadString());
            int size = package.ReadInt();
            if (!SailDownloads.IsHash(hash)) return;

            string refusal = CheckUpload(sender, hash, size, out CatalogEntry existing);
            if (existing != null)
            {
                Answer(sender, hash, UploadAnswer.Shared, existing.Status);
                return;
            }
            if (refusal != null)
            {
                Answer(sender, hash, UploadAnswer.Refused, refusal: refusal);
                return;
            }

            // Replaces any unfinished upload from this peer.
            s_uploads[sender] = new Upload
            {
                Hash = hash,
                Name = name,
                Size = size,
                Chunks = new byte[ChunkCount(size)][],
                LastActivity = Time.time
            };
            Answer(sender, hash, UploadAnswer.Send);
        }

        public static void RPC_UploadChunk(long sender, ZPackage package)
        {
            if (!s_uploads.TryGetValue(sender, out Upload upload)) return;

            string hash = package.ReadString();
            int index = package.ReadInt();
            byte[] data = package.ReadByteArray();
            if (hash != upload.Hash || index < 0 || index >= upload.Chunks.Length || upload.Chunks[index] != null) return;

            int expected = index < upload.Chunks.Length - 1 ? ChunkBytes : upload.Size - index * ChunkBytes;
            if (data.Length != expected)
            {
                s_uploads.Remove(sender);
                Answer(sender, hash, UploadAnswer.Refused, refusal: "the upload arrived damaged");
                return;
            }

            upload.Chunks[index] = data;
            upload.LastActivity = Time.time;
            if (++upload.Received < upload.Chunks.Length) return;

            s_uploads.Remove(sender);
            FinishUpload(sender, upload);
        }

        private static void FinishUpload(long sender, Upload upload)
        {
            var bytes = new byte[upload.Size];
            for (int i = 0; i < upload.Chunks.Length; i++)
            {
                Buffer.BlockCopy(upload.Chunks[i], 0, bytes, i * ChunkBytes, upload.Chunks[i].Length);
            }

            string hash = upload.Hash;
            if (SailTextures.HashOf(bytes) != hash)
            {
                Answer(sender, hash, UploadAnswer.Refused, refusal: "the upload arrived damaged");
                return;
            }

            // Checked again: the policy, the player's count or the texture's status may have
            // changed while it was on its way.
            string refusal = CheckUpload(sender, hash, bytes.Length, out CatalogEntry existing);
            if (existing != null)
            {
                Answer(sender, hash, UploadAnswer.Shared, existing.Status);
                return;
            }
            string extension = null;
            byte[] thumbnail = null;
            int width = 0;
            int height = 0;
            if (refusal == null)
            {
                refusal = CheckImage(
                    bytes,
                    out extension,
                    out thumbnail,
                    out width,
                    out height);
            }
            if (refusal != null)
            {
                Answer(sender, hash, UploadAnswer.Refused, refusal: refusal);
                return;
            }

            string fileName = $"{upload.Name.Replace(' ', '_')}-{hash.Substring(0, 8)}{extension}";
            string path = Path.Combine(UploadsFolder, fileName);
            try
            {
                Directory.CreateDirectory(UploadsFolder);
                File.WriteAllBytes(path, bytes);
            }
            catch (IOException e)
            {
                Log.Warn($"Could not save an uploaded sail texture to {path}: {e.Message}");
                Answer(sender, hash, UploadAnswer.Refused, refusal: "the server couldn't save it");
                return;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            var entry = new CatalogEntry
            {
                Hash = hash,
                Name = upload.Name,
                Source = TextureSource.Player,
                Status = SailPolicy.RequireApprovalConfig.Value ? TextureStatus.Pending : TextureStatus.Approved,
                UploaderName = IndexField(Plugin.PlainText(peer?.m_playerName)),
                UploaderId = ServerRoles.PeerId(sender),
                FileName = fileName,
                Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                Width = width,
                Height = height,
                Bytes = bytes.Length
            };
            s_entries[hash] = entry;
            s_thumbnails[hash] = thumbnail;
            s_paths[hash] = path;
            SaveIndex();

            Log.Info($"{ServerRoles.PeerName(sender)} shared sail texture {fileName} ({entry.Status}).");
            Answer(sender, hash, UploadAnswer.Shared, entry.Status);
            SendCatalogToAll();
        }

        // Null if the server takes this texture from this player. Sets existing (and returns null)
        // when the server already has it, so there's nothing to send.
        private static string CheckUpload(long sender, string hash, int size, out CatalogEntry existing)
        {
            existing = null;
            if (!SailPolicy.AllowCustomTexturesConfig.Value) return "this server doesn't allow custom sail textures";

            if (s_entries.TryGetValue(hash, out CatalogEntry known))
            {
                if (known.Status == TextureStatus.Denied) return "it was denied on this server";
                existing = known;
                return null;
            }

            if (!SailPolicy.AllowPlayerTexturesConfig.Value) return "this server doesn't take player textures";

            int maxKilobytes = SailPolicy.MaxFileKilobytesConfig.Value;
            if (size <= 0 || size > maxKilobytes * 1024) return $"it's over the server's {maxKilobytes} KB limit";

            int max = Math.Max(1, SailPolicy.MaxTexturesPerPlayerConfig.Value);
            int shared = SharedCount(ServerRoles.PeerId(sender));
            if (shared >= max) return $"you're sharing {shared} of {max} textures already";

            return null;
        }

        // Decodes the image: a real PNG or JPG, within the size limit.
        private static string CheckImage(
            byte[] bytes,
            out string extension,
            out byte[] thumbnail,
            out int width,
            out int height)
        {
            thumbnail = null;
            width = 0;
            height = 0;
            extension = ImageExtension(bytes);
            if (extension == null) return "it isn't a PNG or JPG file";

            // JPGs from phones and cameras can carry where the photo was taken; shared files go to
            // every player byte for byte, so only PNGs are shared. JPGs still work locally.
            if (extension != ".png") return "only PNG images can be shared";

            thumbnail = SailThumbnails.Make(bytes, out width, out height, out string error);
            if (thumbnail == null) return "the server couldn't read the image";

            int max = SailPolicy.MaxDimensionConfig.Value;
            if (width > max || height > max) return $"it's {width}x{height}, over the server's {max}x{max} limit";

            return null;
        }

        private static int SharedCount(string uploaderId)
        {
            if (string.IsNullOrEmpty(uploaderId)) return 0;
            return s_entries.Values.Count(e => e.CountsTowardLimit && e.UploaderId == uploaderId);
        }

        // From the file's first bytes, never its name.
        private static string ImageExtension(byte[] bytes)
        {
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            return null;
        }

        private static int ChunkCount(int size) => Math.Max(1, (size + ChunkBytes - 1) / ChunkBytes);

        private static void Answer(
            long peer,
            string hash,
            UploadAnswer answer,
            TextureStatus status = TextureStatus.Approved,
            string refusal = "")
        {
            if (answer == UploadAnswer.Refused) Log.Info($"Refused sail texture {hash.Substring(0, 8)} from {ServerRoles.PeerName(peer)}: {refusal}.");

            var package = new ZPackage();
            package.Write(hash);
            package.Write((byte)answer);
            package.Write((byte)status);
            package.Write(refusal);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, SailNetwork.UploadAnswerRpc, package);
        }

        // ── Moderation ───────────────────────────────────────────────────

        // A modded client, or this game itself when hosting (routed RPCs to its own ID arrive here).
        private static bool FromModdedPeer(long sender)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer()) return false;
            return sender == ZNet.GetUID() || SailNetwork.IsModdedPeer(sender);
        }

        private static bool FromModerator(long sender, string request)
        {
            if (!FromModdedPeer(sender)) return false;
            if (ServerRoles.PeerHas(sender, ServerRoles.Moderate)) return true;
            Log.Info($"Refused {request} from {ServerRoles.PeerName(sender)}: not a moderator.");
            return false;
        }

        // A player texture a moderator may look at in full, whatever its status.
        private static bool IsModeratedBy(long peer, string hash)
        {
            return s_entries.TryGetValue(hash, out CatalogEntry entry)
                && entry.Source == TextureSource.Player
                && ServerRoles.PeerHas(peer, ServerRoles.Moderate);
        }

        // For a hosting moderator's preview: the file behind any player texture, denied included.
        public static string ModerationPathOf(string hash)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || hash == null) return null;
            return s_paths.TryGetValue(hash, out string path) ? path : null;
        }

        public static void RPC_GetModeration(long sender)
        {
            if (FromModerator(sender, "a moderation list request")) SendModeration(sender);
        }

        public static void RPC_Moderate(long sender, ZPackage package)
        {
            if (!FromModerator(sender, "a sail texture decision")) return;

            string hash = package.ReadString();
            var action = (ModerationAction)package.ReadByte();
            if (!s_entries.TryGetValue(hash, out CatalogEntry entry) || entry.Source != TextureSource.Player)
            {
                // Someone else removed it first; the fresh list shows that.
                SendModeration(sender);
                return;
            }

            switch (action)
            {
                case ModerationAction.Approve:
                    if (!s_paths.ContainsKey(hash))
                    {
                        Log.Info($"Can't approve sail texture {entry.FileName}: the server no longer has its file.");
                        SendModeration(sender);
                        return;
                    }
                    Decide(entry, TextureStatus.Approved, sender);
                    break;
                case ModerationAction.Deny:
                    Decide(entry, TextureStatus.Denied, sender);
                    s_transfers.RemoveAll(t => t.Hash == hash);
                    break;
                case ModerationAction.Remove:
                    Remove(entry);
                    break;
                default:
                    return;
            }

            Log.Info($"{ServerRoles.PeerName(sender)}: {action} sail texture {entry.Name} ({entry.FileName}) by {entry.UploaderName}.");
            SaveIndex();
            SendCatalogToAll();
            SendModerationToModerators();
        }

        private static void Decide(CatalogEntry entry, TextureStatus status, long moderator)
        {
            entry.Status = status;
            entry.DecidedById = ServerRoles.PeerId(moderator);
            string name = moderator == ZNet.GetUID() ? Player.m_localPlayer?.GetPlayerName() : ZNet.instance.GetPeer(moderator)?.m_playerName;
            entry.DecidedByName = IndexField(Plugin.PlainText(name));
            entry.DecidedDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        // Forgets an upload entirely: it frees the uploader's slot and may be shared again.
        private static void Remove(CatalogEntry entry)
        {
            s_entries.Remove(entry.Hash);
            s_thumbnails.Remove(entry.Hash);
            s_paths.Remove(entry.Hash);
            s_transfers.RemoveAll(t => t.Hash == entry.Hash);

            string path = Path.Combine(UploadsFolder, entry.FileName);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException e)
            {
                Log.Warn($"Could not delete {path}: {e.Message}");
            }
        }

        private static void SendModeration(long peer)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, SailNetwork.ModerationRpc, ModerationPackage());
        }

        // The same list for every moderator; ShipsUsing walks every ZDO, so build it once.
        private static void SendModerationToModerators()
        {
            List<long> peers = SailNetwork.ModdedPeers.Where(p => ServerRoles.PeerHas(p, ServerRoles.Moderate)).ToList();
            if (!ZNet.instance.IsDedicated()) peers.Add(ZNet.GetUID());
            if (peers.Count == 0) return;

            ZPackage package = ModerationPackage();
            foreach (long peer in peers) ZRoutedRpc.instance.InvokeRoutedRPC(peer, SailNetwork.ModerationRpc, package);
        }

        private static ZPackage ModerationPackage()
        {
            Dictionary<string, int> shipsUsing = ShipsUsing();
            List<CatalogEntry> entries = s_entries.Values
                .Where(e => e.Source == TextureSource.Player)
                .OrderBy(e => e.Status == TextureStatus.Pending ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var package = new ZPackage();
            package.Write(entries.Count);
            foreach (CatalogEntry e in entries)
            {
                shipsUsing.TryGetValue(e.Hash, out int ships);
                new ModerationEntry
                {
                    Hash = e.Hash,
                    Name = e.Name,
                    Status = e.Status,
                    UploaderName = e.UploaderName,
                    Date = e.Date,
                    DecidedBy = e.DecidedByName,
                    DecidedDate = e.DecidedDate,
                    ShipsUsing = ships,
                    Width = e.Width,
                    Height = e.Height,
                    Bytes = e.Bytes,
                    HasImage = s_paths.ContainsKey(e.Hash)
                }.Write(package);
            }
            return package;
        }

        // Ships (any ZDO) per texture hash, across the whole world the server holds.
        private static Dictionary<string, int> ShipsUsing()
        {
            var counts = new Dictionary<string, int>();
            if (!(s_objectsById?.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> zdos))
            {
                Log.Warn("Could not read ZDOMan.m_objectsByID; ship counts show 0.");
                return counts;
            }

            foreach (ZDO zdo in zdos.Values)
            {
                string hash = zdo.GetString(s_textureKey);
                if (hash.Length == 0) continue;
                counts.TryGetValue(hash, out int count);
                counts[hash] = count + 1;
            }
            return counts;
        }

        // ── Upload index ─────────────────────────────────────────────────

        // One line per upload: hash, file, name, status, uploader id, uploader name, date
        // (tab-separated). A file moved from uploads/ into sails/ becomes one of the server's own.
        private static void LoadUploads()
        {
            string indexPath = Path.Combine(UploadsFolder, IndexFileName);
            if (!File.Exists(indexPath)) return;

            int loaded = 0;
            foreach (string line in File.ReadAllLines(indexPath))
            {
                if (line.Length == 0 || line.StartsWith("#")) continue;

                string[] fields = line.Split('\t');
                if (fields.Length < 7
                    || !SailDownloads.IsHash(fields[0])
                    || !Enum.TryParse(fields[3], out TextureStatus status)
                    || Path.GetFileName(fields[1]) != fields[1])
                {
                    Log.Warn($"Ignoring a damaged line in {indexPath}: {line}");
                    s_unloadedIndexLines.Add(line);
                    continue;
                }

                string hash = fields[0];
                if (s_entries.ContainsKey(hash)) continue;

                var entry = new CatalogEntry
                {
                    Hash = hash,
                    FileName = fields[1],
                    Name = CatalogEntry.CleanName(fields[2]),
                    Source = TextureSource.Player,
                    Status = status,
                    UploaderId = fields[4],
                    UploaderName = fields[5],
                    Date = fields[6]
                };
                if (fields.Length >= 10)
                {
                    entry.DecidedById = fields[7];
                    entry.DecidedByName = fields[8];
                    entry.DecidedDate = fields[9];
                }

                string path = Path.Combine(UploadsFolder, entry.FileName);
                byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                byte[] thumbnail = bytes != null && SailTextures.HashOf(bytes) == hash
                    ? SailThumbnails.Make(bytes, out entry.Width, out entry.Height, out _)
                    : null;

                // A denial is remembered even without its image (moderators then can't approve it).
                if (thumbnail == null && status == TextureStatus.Denied)
                {
                    s_entries[hash] = entry;
                    continue;
                }
                if (thumbnail == null)
                {
                    Log.Warn($"Skipping uploaded sail texture {entry.FileName}: missing, changed or unreadable.");
                    s_unloadedIndexLines.Add(line);
                    continue;
                }

                entry.Bytes = bytes.Length;
                s_entries[hash] = entry;
                s_thumbnails[hash] = thumbnail;
                s_paths[hash] = path;
                loaded++;
            }
            Log.Info($"Serving {loaded} sail texture(s) shared by players.");
        }

        private static void SaveIndex()
        {
            var text = new StringBuilder();
            text.Append(IndexHeader).Append('\n');
            foreach (CatalogEntry e in s_entries.Values.Where(e => e.Source == TextureSource.Player).OrderBy(e => e.Date).ThenBy(e => e.Name))
            {
                text.Append(string.Join(
                    "\t",
                    e.Hash,
                    e.FileName,
                    e.Name,
                    e.Status,
                    e.UploaderId,
                    e.UploaderName,
                    e.Date,
                    e.DecidedById,
                    e.DecidedByName,
                    e.DecidedDate)).Append('\n');
            }
            foreach (string line in s_unloadedIndexLines) text.Append(line).Append('\n');

            string indexPath = Path.Combine(UploadsFolder, IndexFileName);
            try
            {
                Directory.CreateDirectory(UploadsFolder);
                File.WriteAllText(indexPath, text.ToString());
            }
            catch (IOException e)
            {
                Log.Warn($"Could not save {indexPath}: {e.Message}");
            }
        }

        // Player names go into a tab-separated file.
        private static string IndexField(string value)
        {
            return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        // At most one chunk per peer per frame, only while that peer's connection has room and
        // within MaxTransferKBPerSecond. Also drops uploads that stopped arriving.
        private static IEnumerator Pump()
        {
            while (true)
            {
                foreach (long peer in s_uploads.Where(u => Time.time - u.Value.LastActivity > UploadTimeoutSeconds).Select(u => u.Key).ToList())
                {
                    string hash = s_uploads[peer].Hash;
                    s_uploads.Remove(peer);
                    // Tells the uploader now, rather than after their remaining chunks and the
                    // client's own answer timeout.
                    if (ZNet.instance.GetPeer(peer) != null)
                    {
                        Answer(peer, hash, UploadAnswer.Refused, refusal: "the upload stalled");
                    }
                    else
                    {
                        Log.Debug($"Dropped an unfinished sail texture upload from peer {peer}.");
                    }
                }

                for (int i = s_transfers.Count - 1; i >= 0; i--)
                {
                    Transfer transfer = s_transfers[i];
                    ZNetPeer peer = ZNet.instance.GetPeer(transfer.Peer);
                    if (peer == null)
                    {
                        s_transfers.RemoveAt(i);
                        TransferPacing.Forget(transfer.Peer);
                        continue;
                    }
                    if (peer.m_socket.GetSendQueueSize() > MaxQueuedBytes || !TransferPacing.CanSend(transfer.Peer)) continue;

                    int offset = transfer.NextChunk * ChunkBytes;
                    int length = Math.Min(ChunkBytes, transfer.Data.Length - offset);
                    var chunk = new byte[length];
                    Buffer.BlockCopy(transfer.Data, offset, chunk, 0, length);

                    var package = new ZPackage();
                    package.Write(transfer.Hash);
                    package.Write(transfer.NextChunk);
                    package.Write(transfer.ChunkCount);
                    package.Write(chunk);
                    ZRoutedRpc.instance.InvokeRoutedRPC(transfer.Peer, SailNetwork.TextureChunkRpc, package);
                    TransferPacing.Sent(transfer.Peer, length);

                    if (++transfer.NextChunk >= transfer.ChunkCount) s_transfers.RemoveAt(i);
                }
                yield return null;
            }
        }
    }
}
