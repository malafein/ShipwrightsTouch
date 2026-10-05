using System;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Small PNG previews of sail textures for the customization panel's list, so browsing a
    // server's catalog doesn't download every full image. Made from the image itself (on the
    // server, once verified to work headless), so a thumbnail always matches its texture.
    public static class SailThumbnails
    {
        public const int Size = 64;

        // Decodes the image, shrinks it to fit Size x Size (aspect kept) with a box filter on the
        // CPU, and encodes the result as PNG. Never touches the GPU copy, so it doesn't depend on
        // the graphics device. Also reports the source image's size. Null if the bytes aren't a
        // readable PNG or JPG.
        public static byte[] Make(
            byte[] imageBytes,
            out int sourceWidth,
            out int sourceHeight,
            out string error)
        {
            error = null;
            sourceWidth = 0;
            sourceHeight = 0;

            // Never decode an image whose header claims a huge size: a small file can describe
            // gigabytes of pixels.
            if (!TryReadSize(imageBytes, out int claimedWidth, out int claimedHeight))
            {
                error = "not a valid PNG or JPG";
                return null;
            }
            if (claimedWidth > SailTextures.MaxDimension || claimedHeight > SailTextures.MaxDimension)
            {
                error = $"{claimedWidth}x{claimedHeight} is over the {SailTextures.MaxDimension}x{SailTextures.MaxDimension} limit";
                return null;
            }

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D thumbnail = null;
            try
            {
                if (!source.LoadImage(imageBytes, false))
                {
                    error = "not a valid PNG or JPG";
                    return null;
                }

                sourceWidth = source.width;
                sourceHeight = source.height;
                float scale = Math.Min(1f, (float)Size / Math.Max(sourceWidth, sourceHeight));
                int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

                Color32[] sourcePixels = source.GetPixels32();
                var pixels = new Color32[width * height];
                for (int y = 0; y < height; y++)
                {
                    int y0 = y * sourceHeight / height;
                    int y1 = Math.Max(y0 + 1, (y + 1) * sourceHeight / height);
                    for (int x = 0; x < width; x++)
                    {
                        int x0 = x * sourceWidth / width;
                        int x1 = Math.Max(x0 + 1, (x + 1) * sourceWidth / width);
                        pixels[y * width + x] = Average(
                            sourcePixels,
                            source.width,
                            x0,
                            x1,
                            y0,
                            y1);
                    }
                }

                thumbnail = new Texture2D(width, height, TextureFormat.RGBA32, false);
                thumbnail.SetPixels32(pixels);
                return thumbnail.EncodeToPNG();
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
                if (thumbnail != null) UnityEngine.Object.Destroy(thumbnail);
            }
        }

        // Alpha-weighted, so transparent pixels don't darken the edges of cut-out shapes.
        private static Color32 Average(Color32[] pixels, int stride, int x0, int x1, int y0, int y1)
        {
            long r = 0, g = 0, b = 0, a = 0;
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    Color32 p = pixels[row + x];
                    r += p.r * p.a;
                    g += p.g * p.a;
                    b += p.b * p.a;
                    a += p.a;
                    count++;
                }
            }
            if (a == 0) return new Color32(0, 0, 0, 0);
            return new Color32((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)(a / count));
        }

        // An image's size from its header alone, without decoding it: PNG's IHDR chunk, or a JPEG
        // start-of-frame marker. False if it's neither or the header is cut short.
        public static bool TryReadSize(byte[] bytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (bytes == null) return false;

            // PNG: 8-byte signature, then the IHDR chunk (length, "IHDR", width, height).
            if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[12] == 0x49 && bytes[13] == 0x48 && bytes[14] == 0x44 && bytes[15] == 0x52)
            {
                width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return width > 0 && height > 0;
            }

            // JPEG: walk the markers to the first start-of-frame (SOF0..SOF15, except DHT, JPG and
            // DAC, which share the range).
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
            int i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF) return false;
                byte marker = bytes[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }
                int length = (bytes[i + 2] << 8) | bytes[i + 3];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = (bytes[i + 5] << 8) | bytes[i + 6];
                    width = (bytes[i + 7] << 8) | bytes[i + 8];
                    return width > 0 && height > 0;
                }
                if (marker == 0xDA || length < 2) return false;
                i += 2 + length;
            }
            return false;
        }
    }
}
