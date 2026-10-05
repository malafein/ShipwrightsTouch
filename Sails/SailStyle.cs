using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Sail colors. A ship stores a preset index (Plugin.ZdoStyleKey, read by every version) and,
    // when the color isn't a preset, its exact RGB (Plugin.ZdoColorKey). The preset is then the
    // nearest one, so older versions still show something close.
    public static class SailStyle
    {
        public static readonly Color[] Presets =
        {
            Color.white,
            Color.red,
            Color.blue,
            Color.green,
            Color.yellow,
            new Color(0.2f, 0.2f, 0.2f)
        };

        public static readonly string[] PresetNames =
        {
            "White",
            "Red",
            "Blue",
            "Green",
            "Yellow",
            "Black"
        };

        private const int NoCustomColor = -1;
        public const int MaxRecentColors = 8;

        private static List<Color> s_recent;

        private static string RecentColorsPath => Path.Combine(Paths.ConfigPath, "ShipwrightsTouch", "recent-colors.txt");

        // False for a preset index from a newer version that this one doesn't know.
        public static bool TryGetColor(ZDO zdo, out Color color)
        {
            int rgb = zdo.GetInt(Plugin.ZdoColorKey, NoCustomColor);
            if (rgb != NoCustomColor)
            {
                color = FromRgb(rgb);
                return true;
            }

            int style = zdo.GetInt(Plugin.ZdoStyleKey, 0);
            color = style >= 0 && style < Presets.Length ? Presets[style] : Color.white;
            return style >= 0 && style < Presets.Length;
        }

        public static void SetPreset(ZDO zdo, int style)
        {
            zdo.Set(Plugin.ZdoStyleKey, style);
            zdo.Set(Plugin.ZdoColorKey, NoCustomColor);
        }

        public static void SetColor(ZDO zdo, Color color)
        {
            int preset = PresetIndexOf(color);
            if (preset >= 0)
            {
                SetPreset(zdo, preset);
                return;
            }

            zdo.Set(Plugin.ZdoStyleKey, NearestPreset(color));
            zdo.Set(Plugin.ZdoColorKey, ToRgb(color));
        }

        // -1 when the color isn't exactly a preset (compared at 8 bits per channel).
        public static int PresetIndexOf(Color color)
        {
            int rgb = ToRgb(color);
            for (int i = 0; i < Presets.Length; i++)
            {
                if (ToRgb(Presets[i]) == rgb) return i;
            }
            return -1;
        }

        public static string ToHex(Color color) => "#" + ColorUtility.ToHtmlStringRGB(color);

        public static bool TryParseHex(string text, out Color color)
        {
            text = text?.Trim() ?? "";
            if (!text.StartsWith("#")) text = "#" + text;
            if (text.Length == 7 && ColorUtility.TryParseHtmlString(text, out color))
            {
                color.a = 1f;
                return true;
            }
            color = Color.white;
            return false;
        }

        // Recently used non-preset colors, newest first. Kept on this computer only.
        public static IReadOnlyList<Color> RecentColors
        {
            get
            {
                LoadRecent();
                return s_recent;
            }
        }

        public static void AddRecent(Color color)
        {
            if (PresetIndexOf(color) >= 0) return;
            LoadRecent();

            int rgb = ToRgb(color);
            s_recent.RemoveAll(c => ToRgb(c) == rgb);
            s_recent.Insert(0, color);
            if (s_recent.Count > MaxRecentColors) s_recent.RemoveRange(MaxRecentColors, s_recent.Count - MaxRecentColors);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecentColorsPath));
                File.WriteAllLines(RecentColorsPath, s_recent.Select(ToHex));
            }
            catch (IOException e)
            {
                Log.Warn($"Could not save recent sail colors: {e.Message}");
            }
        }

        private static void LoadRecent()
        {
            if (s_recent != null) return;
            s_recent = new List<Color>();
            if (!File.Exists(RecentColorsPath)) return;

            try
            {
                foreach (string line in File.ReadAllLines(RecentColorsPath))
                {
                    if (TryParseHex(line, out Color color)) s_recent.Add(color);
                    if (s_recent.Count == MaxRecentColors) break;
                }
            }
            catch (IOException e)
            {
                Log.Warn($"Could not read recent sail colors: {e.Message}");
            }
        }

        private static int NearestPreset(Color color)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Presets.Length; i++)
            {
                Color p = Presets[i];
                float distance = (p.r - color.r) * (p.r - color.r) + (p.g - color.g) * (p.g - color.g) + (p.b - color.b) * (p.b - color.b);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        private static int ToRgb(Color color)
        {
            Color32 c = color;
            return (c.r << 16) | (c.g << 8) | c.b;
        }

        private static Color FromRgb(int rgb)
        {
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }
    }
}
