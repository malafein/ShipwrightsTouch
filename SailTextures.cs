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
        private const int MaxDimension = 2048;
        private const long MaxFileBytes = 4 * 1024 * 1024;

        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        private static readonly List<Entry> s_entries = new List<Entry>();
        private static readonly Dictionary<string, Texture2D> s_loaded = new Dictionary<string, Texture2D>();

        // Hashes that failed to load, so a bad file isn't decoded again every frame.
        private static readonly HashSet<string> s_failed = new HashSet<string>();

        public static string Folder => System.IO.Path.Combine(Paths.ConfigPath, "ShipwrightsTouch", "sails");

        public static IReadOnlyList<Entry> Entries => s_entries;

        // Rescans the folder. Cheap enough to call whenever the player opens a texture choice, so
        // files dropped in while the game runs show up without a restart.
        public static void Refresh()
        {
            Directory.CreateDirectory(Folder);

            s_entries.Clear();
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
                if (s_entries.Any(e => e.Hash == hash)) continue;

                s_entries.Add(new Entry
                {
                    Hash = hash,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path
                });
            }
            Log.Debug($"Found {s_entries.Count} sail texture(s) in {Folder}");
        }

        // Returns the texture for a hash, loading it on first use, or null if this client doesn't
        // have it (the sail then shows the vanilla texture).
        public static Texture2D Get(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            if (s_loaded.TryGetValue(hash, out Texture2D texture)) return texture;
            if (s_failed.Contains(hash)) return null;

            Entry entry = s_entries.FirstOrDefault(e => e.Hash == hash);
            if (entry == null) return null;

            texture = Load(entry);
            if (texture == null)
            {
                s_failed.Add(hash);
                return null;
            }

            s_loaded[hash] = texture;
            return texture;
        }

        public static string NameOf(string hash)
        {
            return s_entries.FirstOrDefault(e => e.Hash == hash)?.Name;
        }

        private static Texture2D Load(Entry entry)
        {
            byte[] bytes = File.ReadAllBytes(entry.Path);
            if (HashOf(bytes) != entry.Hash)
            {
                Log.Warn($"Sail texture {entry.Name} changed on disk since it was scanned; skipping it until the next refresh.");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(bytes))
            {
                Log.Warn($"Could not read sail texture {entry.Name}: not a valid PNG or JPG.");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            if (texture.width > MaxDimension || texture.height > MaxDimension)
            {
                Log.Warn($"Skipping sail texture {entry.Name}: {texture.width}x{texture.height} is over the {MaxDimension}x{MaxDimension} limit.");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.name = $"ShipwrightsTouch_{entry.Name}";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 4;

            // Mipmaps are generated on upload; dropping the CPU copy halves the memory cost.
            texture.Apply(true, true);
            return texture;
        }

        private static string HashOf(byte[] bytes)
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
