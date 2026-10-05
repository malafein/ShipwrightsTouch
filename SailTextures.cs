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
    // Custom sail textures from the local folder. A texture is identified by the SHA-256 of its
    // file bytes, never by file name: two players' red.png must not collide, and renaming a file
    // must not break the ships that use it. Ships store only the hash (Plugin.ZdoTextureKey).
    public static class SailTextures
    {
        public class Entry
        {
            public string Hash;
            public string Name;
            public string Path;
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

        public static IReadOnlyList<Entry> Entries => s_entries;

        // Rescans the folder. Cheap enough to call whenever the player opens a texture choice, so
        // files dropped in while the game runs show up without a restart.
        public static void Refresh()
        {
            Directory.CreateDirectory(Folder);

            s_entries.Clear();
            s_byHash.Clear();
            foreach (string path in Directory.GetFiles(Folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
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
                    Path = path
                };
                s_entries.Add(entry);
                s_byHash[hash] = entry;
            }
            Log.Debug($"Found {s_entries.Count} sail texture(s) in {Folder}");
        }

        // Returns the texture for a hash, loading it on first use, or null if this client doesn't
        // have it or may not show it (the sail then shows the vanilla texture). Looks in the local
        // folder, then the download cache; a texture in the server's catalog but not here yet is
        // requested, and shows up on a later call once it has arrived. Called every frame per
        // ship, so every path stays cheap.
        public static Texture2D Get(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;

            // Someone else's texture only while the server allows it (a later denial hides it even
            // if it's already loaded or cached). A hosting player reads uploads straight from the
            // server's uploads folder.
            s_byHash.TryGetValue(hash, out Entry entry);
            string serverPath = entry == null ? SailServer.PathOf(hash) : null;
            if (entry == null && serverPath == null && !SailDownloads.MayShow(hash)) return null;

            if (s_loaded.TryGetValue(hash, out Texture2D texture)) return texture;
            if (s_failed.ContainsKey(hash)) return null;

            if (serverPath != null)
            {
                entry = new Entry { Hash = hash, Name = hash.Substring(0, 8), Path = serverPath };
            }
            else if (entry == null && SailDownloads.IsCached(hash))
            {
                entry = new Entry
                {
                    Hash = hash,
                    Name = SailDownloads.Find(hash)?.Name ?? hash.Substring(0, 8),
                    Path = SailDownloads.CachedPath(hash)
                };
            }
            if (entry == null)
            {
                SailDownloads.Request(hash);
                return null;
            }

            texture = Load(entry, out string problem);
            if (texture == null)
            {
                s_failed[hash] = problem;
                return null;
            }

            s_loaded[hash] = texture;
            return texture;
        }

        public static string NameOf(string hash)
        {
            return hash != null && s_byHash.TryGetValue(hash, out Entry entry) ? entry.Name : SailDownloads.Find(hash)?.Name;
        }

        public static bool IsLocal(string hash) => hash != null && s_byHash.ContainsKey(hash);

        // Why a texture couldn't be loaded ("unreadable", "too large", ...), or null. Known once
        // Get has tried it.
        public static string ProblemOf(string hash)
        {
            return hash != null && s_failed.TryGetValue(hash, out string problem) ? problem : null;
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
