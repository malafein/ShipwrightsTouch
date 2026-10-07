using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // The PNGs that are shared: an image decoded and written again by the game's own encoder, so a
    // shared file carries pixels and nothing else (no metadata, no hidden data). The client converts
    // before uploading and the server converts again on receipt, with this same code, so for an
    // honest client both produce the same bytes. Converting is lossless for 8-bit images (16-bit
    // ones drop to 8-bit) and gives the same bytes when repeated.
    public static class SailImages
    {
        // Chunk types a game encoder may write that carry nothing but image data or color
        // information. Anything else (text, EXIF, private chunks) is unexpected in an upload from a
        // client that converted it first.
        private static readonly HashSet<string> s_plainChunks = new HashSet<string>
        {
            "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "sRGB", "gAMA", "cHRM", "pHYs"
        };

        private static readonly byte[] s_pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // A PNG or JPG converted to a shared PNG; null (with a reason) if it isn't readable or its
        // header claims more than MaxDimension.
        public static byte[] ToSharedPng(
            byte[] bytes,
            out int width,
            out int height,
            out string error)
        {
            width = 0;
            height = 0;
            error = null;

            // Never decode an image whose header claims a huge size (see SailThumbnails.Make).
            if (!SailThumbnails.TryReadSize(bytes, out int claimedWidth, out int claimedHeight))
            {
                error = "not a valid PNG or JPG";
                return null;
            }
            if (claimedWidth > SailTextures.MaxDimension || claimedHeight > SailTextures.MaxDimension)
            {
                error = $"{claimedWidth}x{claimedHeight} is over the {SailTextures.MaxDimension}x{SailTextures.MaxDimension} limit";
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(bytes, false))
                {
                    error = "not a valid PNG or JPG";
                    return null;
                }
                width = texture.width;
                height = texture.height;
                return Encode(texture);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        // PNGs decode to RGBA, so an opaque image (most sails) would gain an alpha channel and grow
        // by about half; it's written without one. Converting the result again gives the same
        // bytes either way.
        private static byte[] Encode(Texture2D texture)
        {
            if (texture.format != TextureFormat.RGBA32 && texture.format != TextureFormat.ARGB32) return texture.EncodeToPNG();

            Color32[] pixels = texture.GetPixels32();
            foreach (Color32 pixel in pixels)
            {
                if (pixel.a != 255) return texture.EncodeToPNG();
            }

            var opaque = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            try
            {
                opaque.SetPixels32(pixels);
                return opaque.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.Destroy(opaque);
            }
        }

        public static bool IsPng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < s_pngSignature.Length) return false;
            for (int i = 0; i < s_pngSignature.Length; i++)
            {
                if (bytes[i] != s_pngSignature[i]) return false;
            }
            return true;
        }

        // Whether a PNG holds anything besides plain image data: a chunk type outside the list
        // above, bytes after the end chunk, or a broken chunk structure. Names what it found, for
        // the log; null if nothing.
        public static string UnexpectedContent(byte[] png)
        {
            if (!IsPng(png)) return "not a PNG";

            int i = s_pngSignature.Length;
            while (true)
            {
                // Length, type, data, CRC.
                if (i + 12 > png.Length) return "a cut-off chunk";
                long length = ((long)png[i] << 24) | ((long)png[i + 1] << 16) | ((long)png[i + 2] << 8) | png[i + 3];
                string type = ChunkType(png, i + 4);
                if (length > png.Length - i - 12) return $"a cut-off {type} chunk";
                if (!s_plainChunks.Contains(type)) return $"a {type} chunk";

                i += 12 + (int)length;
                if (type == "IEND")
                {
                    return i == png.Length ? null : $"{png.Length - i} bytes after the image's end";
                }
            }
        }

        // Chunk types are four ASCII letters in a valid file; anything else is shown as hex.
        private static string ChunkType(byte[] png, int offset)
        {
            for (int i = offset; i < offset + 4; i++)
            {
                byte c = png[i];
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')) return BitConverter.ToString(png, offset, 4);
            }
            return Encoding.ASCII.GetString(png, offset, 4);
        }
    }
}
