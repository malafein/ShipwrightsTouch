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
    }
}
