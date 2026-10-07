using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Client side of the texture catalog: the server's list, thumbnails and full images fetched by
    // hash, and a cache on disk so each image is downloaded once, ever. A cached file is named by
    // its content hash and checked against it on arrival, so it can never be stale or swapped.
    public static class SailDownloads
    {
        // A request with no new chunk for this long may be sent again.
        private const float RetrySeconds = 60f;

        private class Download
        {
            public byte[][] Chunks;
            public int Received;
            public float LastActivity;
        }

        private static readonly Dictionary<string, CatalogEntry> s_catalog = new Dictionary<string, CatalogEntry>();

        // Alias hash -> the catalog hash it stands for (CatalogEntry.Aliases).
        private static readonly Dictionary<string, string> s_aliases = new Dictionary<string, string>();
        private static readonly Dictionary<string, Texture2D> s_thumbnails = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> s_thumbnailsRequested = new HashSet<string>();
        private static readonly Dictionary<string, Download> s_downloads = new Dictionary<string, Download>();
        private static HashSet<string> s_cached;

        public static string CacheFolder => Path.Combine(Paths.ConfigPath, "ShipwrightsTouch", "cache");
        private static string ThumbnailFolder => Path.Combine(CacheFolder, "thumbnails");

        public static IReadOnlyCollection<CatalogEntry> Catalog => s_catalog.Values;

        // Raised when the catalog changes or a thumbnail or texture arrives.
        public static event Action Changed;

        public static void StartSession()
        {
            s_catalog.Clear();
            s_aliases.Clear();
            s_thumbnailsRequested.Clear();
            s_downloads.Clear();
            Changed?.Invoke();
        }

        // The hash the server stores a texture under, for a hash a ship may hold (an alias), or
        // the hash itself when it isn't one.
        public static string Canonical(string hash)
        {
            return hash != null && s_aliases.TryGetValue(hash, out string canonical) ? canonical : hash;
        }

        // By its hash or any of its aliases.
        public static CatalogEntry Find(string hash)
        {
            return hash != null && s_catalog.TryGetValue(Canonical(hash), out CatalogEntry entry) ? entry : null;
        }

        // Whether a texture from the server may be shown and downloaded: in its catalog, not
        // denied, and not another player's when the player turned those off.
        public static bool MayShow(string hash)
        {
            CatalogEntry entry = Find(hash);
            if (entry == null || entry.Status == TextureStatus.Denied) return false;
            return entry.Source == TextureSource.Server || entry.Mine || Plugin.ShowOtherPlayersTextures.Value;
        }

        // Textures this player has shared that count toward the server's per-player limit.
        public static int SharedCount() => s_catalog.Values.Count(e => e.Mine && e.CountsTowardLimit);

        // ── Full images ──────────────────────────────────────────────────

        public static string CachedPath(string hash) => Path.Combine(CacheFolder, Canonical(hash));

        public static bool IsCached(string hash)
        {
            hash = Canonical(hash);
            if (s_cached == null)
            {
                s_cached = new HashSet<string>();
                if (Directory.Exists(CacheFolder))
                {
                    foreach (string path in Directory.GetFiles(CacheFolder)) s_cached.Add(Path.GetFileName(path));
                }
            }
            return s_cached.Contains(hash);
        }

        // Asks the server for a texture in its catalog, unless it's already on its way. Cheap enough
        // to call every frame for the same hash. A moderator may also fetch textures on the
        // moderation list (for the panel's preview; ships still show only what MayShow allows).
        public static void Request(string hash, bool forModeration = false)
        {
            if (SailNetwork.Mode != ServerMode.Modded) return;
            hash = Canonical(hash);
            if (!MayShow(hash) && !(forModeration && SailModeration.Find(hash) != null)) return;
            if (s_downloads.TryGetValue(hash, out Download pending) && Time.time - pending.LastActivity < RetrySeconds) return;

            s_downloads[hash] = new Download { LastActivity = Time.time };
            Log.Debug($"Requesting sail texture {hash.Substring(0, 8)} from the server.");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, SailNetwork.GetTextureRpc, hash);
        }

        // ── Thumbnails ───────────────────────────────────────────────────

        // From memory, the server itself when hosting, or the disk cache; otherwise requested (see
        // RequestThumbnails) and null.
        public static Texture2D Thumbnail(string hash)
        {
            hash = Canonical(hash);
            if (s_thumbnails.TryGetValue(hash, out Texture2D texture)) return texture;

            byte[] png = SailServer.ThumbnailOf(hash);
            if (png == null)
            {
                string path = Path.Combine(ThumbnailFolder, hash + ".png");
                if (!File.Exists(path)) return null;
                png = File.ReadAllBytes(path);
            }

            texture = LoadThumbnail(png);
            if (texture != null) s_thumbnails[hash] = texture;
            return texture;
        }

        // Asks for every catalog thumbnail not cached yet, in one message. Skips textures the player
        // has in their own folder (the panel shows those from the image itself).
        public static void RequestThumbnails()
        {
            if (SailNetwork.Mode != ServerMode.Modded) return;

            var missing = new List<string>();
            foreach (string hash in s_catalog.Keys)
            {
                if (!MayShow(hash) || SailTextures.HasLocalCopy(hash)) continue;
                if (!s_thumbnailsRequested.Contains(hash) && Thumbnail(hash) == null) missing.Add(hash);
            }
            SendThumbnailRequest(missing);
        }

        // For the moderation panel: thumbnails of textures outside this player's catalog.
        public static void RequestThumbnails(IEnumerable<string> hashes)
        {
            if (SailNetwork.Mode != ServerMode.Modded) return;
            SendThumbnailRequest(hashes.Where(h => !s_thumbnailsRequested.Contains(h) && Thumbnail(h) == null).ToList());
        }

        private static void SendThumbnailRequest(List<string> missing)
        {
            if (missing.Count == 0) return;

            var package = new ZPackage();
            package.Write(missing.Count);
            foreach (string hash in missing)
            {
                package.Write(hash);
                s_thumbnailsRequested.Add(hash);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, SailNetwork.GetThumbnailsRpc, package);
        }

        // ── Messages from the server ─────────────────────────────────────

        // When this game hosts: the server hands its catalog over directly.
        public static void SetHostCatalog(List<CatalogEntry> entries)
        {
            SetCatalog(entries);
            Changed?.Invoke();
        }

        private static void SetCatalog(IEnumerable<CatalogEntry> entries)
        {
            s_catalog.Clear();
            s_aliases.Clear();
            foreach (CatalogEntry entry in entries)
            {
                if (!IsHash(entry.Hash)) continue;
                s_catalog[entry.Hash] = entry;
                foreach (string alias in entry.Aliases) s_aliases[alias] = entry.Hash;
            }
        }

        public static void RPC_Catalog(long sender, ZPackage package)
        {
            if (!FromServer(sender)) return;

            SetCatalog(CatalogEntry.ReadList(package));
            Log.Debug($"Server sail catalog: {s_catalog.Count} texture(s).");
            Changed?.Invoke();
        }

        public static void RPC_Thumbnails(long sender, ZPackage package)
        {
            if (!FromServer(sender)) return;

            int count = package.ReadInt();
            Directory.CreateDirectory(ThumbnailFolder);
            for (int i = 0; i < count; i++)
            {
                string hash = package.ReadString();
                byte[] png = package.ReadByteArray();
                if (!s_catalog.ContainsKey(hash) && SailModeration.Find(hash) == null) continue;

                Texture2D texture = LoadThumbnail(png);
                if (texture == null) continue;
                s_thumbnails[hash] = texture;
                File.WriteAllBytes(Path.Combine(ThumbnailFolder, hash + ".png"), png);
            }
            Changed?.Invoke();
        }

        public static void RPC_TextureChunk(long sender, ZPackage package)
        {
            if (!FromServer(sender)) return;

            string hash = package.ReadString();
            int index = package.ReadInt();
            int count = package.ReadInt();
            byte[] data = package.ReadByteArray();

            if (!s_downloads.TryGetValue(hash, out Download download)) return;
            int bytes = Find(hash)?.Bytes ?? SailModeration.Find(hash)?.Bytes ?? 0;
            int maxChunks = bytes / 1024 + 2;
            if (count <= 0 || count > maxChunks || index < 0 || index >= count) return;

            if (download.Chunks == null) download.Chunks = new byte[count][];
            if (download.Chunks.Length != count || download.Chunks[index] != null) return;

            download.Chunks[index] = data;
            download.Received++;
            download.LastActivity = Time.time;
            if (download.Received < count) return;

            s_downloads.Remove(hash);
            Complete(hash, download.Chunks);
        }

        private static void Complete(string hash, byte[][] chunks)
        {
            int length = 0;
            foreach (byte[] chunk in chunks) length += chunk.Length;
            var bytes = new byte[length];
            int offset = 0;
            foreach (byte[] chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length);
                offset += chunk.Length;
            }

            if (SailTextures.HashOf(bytes) != hash)
            {
                Log.Warn($"Discarded a downloaded sail texture: its content doesn't match its hash {hash.Substring(0, 8)}.");
                return;
            }

            Directory.CreateDirectory(CacheFolder);
            File.WriteAllBytes(CachedPath(hash), bytes);
            IsCached(hash);
            s_cached.Add(hash);
            Log.Debug($"Downloaded sail texture {hash.Substring(0, 8)} ({length} bytes).");
            Changed?.Invoke();
        }

        private static bool FromServer(long sender)
        {
            ZNet net = ZNet.instance;
            return net != null && !net.IsServer() && SailNetwork.Mode == ServerMode.Modded && sender == net.GetServerPeer()?.m_uid;
        }

        // Hashes become file names, so only accept exactly what SailTextures.HashOf produces.
        internal static bool IsHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char c in hash)
            {
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            }
            return true;
        }

        private static Texture2D LoadThumbnail(byte[] png)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture.LoadImage(png) && texture.width <= SailThumbnails.Size && texture.height <= SailThumbnails.Size)
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                return texture;
            }
            UnityEngine.Object.Destroy(texture);
            return null;
        }
    }
}
