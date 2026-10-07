using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Custom sail textures from the local folder, and from ShipwrightsTouch-Sails folders under
    // BepInEx/plugins (the ones bundled with this mod, and sail packs). A texture is identified by the SHA-256 of its
    // file bytes, never by file name: two players' red.png must not collide, and renaming a file
    // must not break the ships that use it. Ships store only the hash (Plugin.ZdoTextureKey).
    public static class SailTextures
    {
        public class Entry
        {
            public string Hash;
            public string Name;
            public string Path;

            // From a ShipwrightsTouch-Sails folder (included with the mod, or a sail pack), not the
            // player's own folder.
            public bool Bundled;
        }

        // Vanilla sails are 128-256 px, so this is generous; it bounds memory and, later, the
        // bytes sent between players.
        internal const int MaxDimension = 2048;
        internal const int MaxFileBytes = 4 * 1024 * 1024;

        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        private static readonly List<Entry> s_entries = new List<Entry>();
        private static readonly Dictionary<string, Entry> s_byHash = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, Texture2D> s_loaded = new Dictionary<string, Texture2D>();

        // Hashes that failed to load, and why (shown in the panel), so a bad file isn't decoded
        // again every frame.
        private static readonly Dictionary<string, string> s_failed = new Dictionary<string, string>();

        public static string Folder => System.IO.Path.Combine(Paths.ConfigPath, "ShipwrightsTouch", "sails");

        // Folders with this name anywhere under BepInEx/plugins hold bundled sails: this mod's own
        // and separately installed sail packs, which mod managers can only install into plugins.
        public const string BundleFolderName = "ShipwrightsTouch-Sails";

        // Found once per game run; mod managers only add plugins while the game is closed.
        private static string[] s_bundleFolders;

        private static IEnumerable<string> Folders()
        {
            yield return Folder;
            if (s_bundleFolders == null)
            {
                try
                {
                    s_bundleFolders = Directory.GetDirectories(Paths.PluginPath, BundleFolderName, SearchOption.AllDirectories)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Log.Warn($"Could not look for bundled sails under {Paths.PluginPath}: {e.Message}");
                    s_bundleFolders = new string[0];
                }
                foreach (string folder in s_bundleFolders) Log.Info($"Using bundled sail textures from {folder}");
            }
            foreach (string folder in s_bundleFolders) yield return folder;
        }

        public static IReadOnlyList<Entry> Entries => s_entries;

        // Rescans the folder. Cheap enough to call whenever the player opens a texture choice, so
        // files dropped in while the game runs show up without a restart.
        public static void Refresh()
        {
            Directory.CreateDirectory(Folder);

            s_entries.Clear();
            s_byHash.Clear();
            // The player's own folder first: a file also bundled keeps the player's name for it.
            IEnumerable<string> files = Folders().SelectMany(f => Directory.GetFiles(f).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            foreach (string path in files)
            {
                if (!Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) continue;

                long size = new FileInfo(path).Length;
                if (size > MaxFileBytes)
                {
                    Log.Warn($"Skipping sail texture {System.IO.Path.GetFileName(path)}: {size / 1024} KB is over the {MaxFileBytes / 1024} KB limit.");
                    continue;
                }

                string hash = HashOf(File.ReadAllBytes(path));
                if (s_byHash.ContainsKey(hash)) continue;

                var entry = new Entry
                {
                    Hash = hash,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    Bundled = !System.IO.Path.GetDirectoryName(path).Equals(Folder, StringComparison.OrdinalIgnoreCase)
                };
                s_entries.Add(entry);
                s_byHash[hash] = entry;
            }
            Log.Debug($"Found {s_entries.Count} sail texture(s) in {Folder} and bundled folders");
        }

        // Returns the texture for a hash, loading it on first use, or null if this client doesn't
        // have it or may not show it (the sail then shows the vanilla texture). Looks in the local
        // folder, then the download cache; a texture in the server's catalog but not here yet is
        // requested, and shows up on a later call once it has arrived. Called every frame per
        // ship, so every path stays cheap.
        public static Texture2D Get(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;

            // A local file first, also when the ship holds another hash for the same texture (a
            // shared texture's alias). Someone else's texture only while the server allows it (a
            // later denial hides it even if it's already loaded or cached). A hosting player reads
            // uploads straight from the server's own files. Loaded textures are keyed by the image
            // (the local file's hash, or the server's canonical one), not by whichever of its
            // hashes a ship holds.
            Entry entry = LocalEntryFor(hash);
            string key;
            string serverPath = null;
            if (entry != null)
            {
                key = entry.Hash;
            }
            else if ((serverPath = SailServer.PathOf(hash, out key)) == null)
            {
                if (!SailDownloads.MayShow(hash)) return null;
                key = SailDownloads.Canonical(hash);
            }

            if (s_loaded.TryGetValue(key, out Texture2D texture)) return texture;
            if (s_failed.ContainsKey(key)) return null;

            if (serverPath != null)
            {
                entry = new Entry { Hash = key, Name = key.Substring(0, 8), Path = serverPath };
            }
            else if (entry == null && SailDownloads.IsCached(key))
            {
                entry = new Entry
                {
                    Hash = key,
                    Name = SailDownloads.Find(key)?.Name ?? key.Substring(0, 8),
                    Path = SailDownloads.CachedPath(key)
                };
            }
            if (entry == null)
            {
                SailDownloads.Request(key);
                return null;
            }

            texture = Load(entry, out string problem);
            if (texture == null)
            {
                s_failed[key] = problem;
                return null;
            }

            s_loaded[key] = texture;
            return texture;
        }

        // The player's own file for a texture: by its hash, or by any hash the server's catalog
        // lists for the same texture.
        private static Entry LocalEntryFor(string hash)
        {
            if (s_byHash.TryGetValue(hash, out Entry entry)) return entry;
            CatalogEntry shared = SailDownloads.Find(hash);
            if (shared == null) return null;
            if (s_byHash.TryGetValue(shared.Hash, out entry)) return entry;
            foreach (string alias in shared.Aliases)
            {
                if (s_byHash.TryGetValue(alias, out entry)) return entry;
            }
            return null;
        }

        // Whether the player has this texture in their own folders under any of its hashes.
        public static bool HasLocalCopy(string hash) => hash != null && LocalEntryFor(hash) != null;

        public static string NameOf(string hash)
        {
            if (hash == null) return null;
            return LocalEntryFor(hash)?.Name ?? SailDownloads.Find(hash)?.Name;
        }

        public static bool IsLocal(string hash) => hash != null && s_byHash.ContainsKey(hash);

        // Included with the mod or a sail pack, under any of its hashes.
        public static bool IsBundled(string hash) => hash != null && LocalEntryFor(hash)?.Bundled == true;

        // Why a texture couldn't be loaded ("unreadable", "too large", ...), or null. Known once
        // Get has tried it.
        public static string ProblemOf(string hash)
        {
            if (hash == null) return null;
            string key = LocalEntryFor(hash)?.Hash ?? SailDownloads.Canonical(hash);
            return s_failed.TryGetValue(key, out string problem) ? problem : null;
        }

        private static Texture2D Load(Entry entry, out string problem)
        {
            problem = null;
            byte[] bytes = File.ReadAllBytes(entry.Path);
            if (HashOf(bytes) != entry.Hash)
            {
                Log.Warn($"Sail texture {entry.Name} changed on disk since it was scanned; skipping it until the next refresh.");
                problem = "changed";
                return null;
            }

            // Checked before decoding (see SailThumbnails.TryReadSize).
            if (!SailThumbnails.TryReadSize(bytes, out int claimedWidth, out int claimedHeight))
            {
                Log.Warn($"Could not read sail texture {entry.Name}: not a valid PNG or JPG.");
                problem = "unreadable";
                return null;
            }
            if (claimedWidth > MaxDimension || claimedHeight > MaxDimension)
            {
                Log.Warn($"Skipping sail texture {entry.Name}: {claimedWidth}x{claimedHeight} is over the {MaxDimension}x{MaxDimension} limit.");
                problem = $"over {MaxDimension}x{MaxDimension}";
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(bytes))
            {
                Log.Warn($"Could not read sail texture {entry.Name}: not a valid PNG or JPG.");
                UnityEngine.Object.Destroy(texture);
                problem = "unreadable";
                return null;
            }

            if (texture.width > MaxDimension || texture.height > MaxDimension)
            {
                Log.Warn($"Skipping sail texture {entry.Name}: {texture.width}x{texture.height} is over the {MaxDimension}x{MaxDimension} limit.");
                UnityEngine.Object.Destroy(texture);
                problem = $"over {MaxDimension}x{MaxDimension}";
                return null;
            }

            texture.name = $"ShipwrightsTouch_{entry.Name}";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 4;

            // Mipmaps are generated on upload; dropping the CPU copy halves the memory cost.
            // Compressed (block-compressed like the game's own textures) it takes about a quarter
            // of the video memory, at the cost of a one-time step on load and slight artifacts on
            // hard edges and smooth gradients. Block compression needs sizes in multiples of 4.
            if (Plugin.CompressTextures.Value && texture.width % 4 == 0 && texture.height % 4 == 0)
            {
                texture.Apply(true, false);
                texture.Compress(true);
                texture.Apply(false, true);
            }
            else
            {
                texture.Apply(true, true);
            }
            return texture;
        }

        internal static string HashOf(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
