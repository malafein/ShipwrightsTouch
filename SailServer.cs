using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
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
        private const string IndexHeader = "# hash\tfile\tname\tstatus\tuploader id\tuploader name\tdate";

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
        // their uploader and admins; denied ones only by their uploader (to show the status).
        private static List<CatalogEntry> VisibleTo(long peer)
        {
            if (!SailPolicy.AllowCustomTexturesConfig.Value) return new List<CatalogEntry>();

            string viewerId = SailNetwork.PeerId(peer);
            bool admin = SailNetwork.IsPeerAdmin(peer);
            return s_entries.Values
                .Where(e =>
                {
                    bool mine = e.UploaderId != "" && e.UploaderId == viewerId;
                    switch (e.Status)
                    {
                        case TextureStatus.Approved: return true;
                        case TextureStatus.Pending: return mine || admin;
                        default: return mine;
                    }
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void SendCatalog(long peer)
        {
            var package = new ZPackage();
            CatalogEntry.WriteList(package, VisibleTo(peer), SailNetwork.PeerId(peer));
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
            if (!SailNetwork.IsModdedPeer(sender)) return;

            var visible = new HashSet<string>(VisibleTo(sender).Select(e => e.Hash));
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
            if (!SailNetwork.IsModdedPeer(sender)) return;
            if (!VisibleTo(sender).Any(e => e.Hash == hash) || !s_paths.TryGetValue(hash, out string path)) return;
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
                UploaderName = IndexField(peer?.m_playerName),
                UploaderId = SailNetwork.PeerId(sender),
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

            Log.Info($"{SailNetwork.PeerName(sender)} shared sail texture {fileName} ({entry.Status}).");
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
                if (known.Status == TextureStatus.Denied) return "an admin denied it";
                existing = known;
                return null;
            }

            if (!SailPolicy.AllowPlayerTexturesConfig.Value) return "this server doesn't take player textures";

            int maxKilobytes = SailPolicy.MaxFileKilobytesConfig.Value;
            if (size <= 0 || size > maxKilobytes * 1024) return $"it's over the server's {maxKilobytes} KB limit";

            int max = Math.Max(1, SailPolicy.MaxTexturesPerPlayerConfig.Value);
            int shared = SharedCount(SailNetwork.PeerId(sender));
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
            if (answer == UploadAnswer.Refused) Log.Info($"Refused sail texture {hash.Substring(0, 8)} from {SailNetwork.PeerName(peer)}: {refusal}.");

            var package = new ZPackage();
            package.Write(hash);
            package.Write((byte)answer);
            package.Write((byte)status);
            package.Write(refusal);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, SailNetwork.UploadAnswerRpc, package);
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

                // A denial is remembered without the image.
                if (status == TextureStatus.Denied)
                {
                    s_entries[hash] = entry;
                    continue;
                }

                string path = Path.Combine(UploadsFolder, entry.FileName);
                byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                byte[] thumbnail = bytes != null && SailTextures.HashOf(bytes) == hash
                    ? SailThumbnails.Make(bytes, out entry.Width, out entry.Height, out _)
                    : null;
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
                text.Append(string.Join("\t", e.Hash, e.FileName, e.Name, e.Status, e.UploaderId, e.UploaderName, e.Date)).Append('\n');
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

        // One chunk per transfer per frame, and only while that peer's connection has room. Also
        // drops uploads that stopped arriving.
        private static IEnumerator Pump()
        {
            while (true)
            {
                foreach (long peer in s_uploads.Where(u => Time.time - u.Value.LastActivity > UploadTimeoutSeconds).Select(u => u.Key).ToList())
                {
                    Log.Debug($"Dropped an unfinished sail texture upload from peer {peer}.");
                    s_uploads.Remove(peer);
                }

                for (int i = s_transfers.Count - 1; i >= 0; i--)
                {
                    Transfer transfer = s_transfers[i];
                    ZNetPeer peer = ZNet.instance.GetPeer(transfer.Peer);
                    if (peer == null)
                    {
                        s_transfers.RemoveAt(i);
                        continue;
                    }
                    if (peer.m_socket.GetSendQueueSize() > MaxQueuedBytes) continue;

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

                    if (++transfer.NextChunk >= transfer.ChunkCount) s_transfers.RemoveAt(i);
                }
                yield return null;
            }
        }
    }
}
