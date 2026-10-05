#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Development-only probe for the custom sail texture work (dev/custom-sails.md). Once per
    // ship prefab per session, writes a report of every renderer on the ship (shader, texture
    // properties, mesh UV bounds) and dumps the vanilla textures and each mesh's UV layout as
    // PNGs, to BepInEx/ShipwrightsTouch-probe/. Compiled out of Release builds.
    //
    // Off by default. Set Enabled to true in a Debug build to run it again, e.g. to inspect a
    // modded ship. Results of the 2026-10-03 run on the vanilla ships are in
    // dev/research-notes.md, with the raw output in dev/probe-2026-10-03/.
    public static class SailProbe
    {
        internal static bool Enabled = false;

        private const int UvImageSize = 1024;

        private static readonly HashSet<string> s_probed = new HashSet<string>();

        [HarmonyPatch]
        private static class Patches
        {
            [HarmonyPatch(typeof(Ship), "Start")]
            [HarmonyPostfix]
            private static void Postfix_ShipStart(Ship __instance)
            {
                if (Enabled) ProbeOnce(__instance);
            }
        }

        private static void ProbeOnce(Ship ship)
        {
            string prefab = Utils.GetPrefabName(ship.gameObject);
            if (!s_probed.Add(prefab)) return;

            try
            {
                Probe(ship, prefab);
            }
            catch (Exception e)
            {
                Log.Warn($"Sail probe failed for {prefab}: {e}");
            }
        }

        private static void Probe(Ship ship, string prefab)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "ShipwrightsTouch-probe", prefab);
            Directory.CreateDirectory(dir);

            var report = new StringBuilder();
            report.AppendLine($"Ship prefab: {prefab}");
            report.AppendLine($"m_sailObject: {PathOf(ship.m_sailObject, ship.transform)}");
            report.AppendLine($"m_mastObject: {PathOf(ship.m_mastObject, ship.transform)}");
            report.AppendLine($"m_sailCloth: {Traverse.Create(ship).Field("m_sailCloth").GetValue()?.ToString() ?? "null"}");
            report.AppendLine();

            var dumped = new HashSet<Texture>();
            foreach (Renderer renderer in ship.GetComponentsInChildren<Renderer>(true))
            {
                DescribeRenderer(ship, renderer, report, dir, dumped);
            }

            File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
            Log.Info($"Sail probe: wrote {prefab} report to {dir}");
        }

        private static void DescribeRenderer(Ship ship, Renderer renderer, StringBuilder report, string dir, HashSet<Texture> dumped)
        {
            string name = renderer.gameObject.name;
            string lower = name.ToLower();
            bool colorMatch = lower.Contains("sail") || lower.Contains("cloth") || lower.Contains("flag");
            bool underSail = ship.m_sailObject != null && renderer.transform.IsChildOf(ship.m_sailObject.transform);

            report.AppendLine($"== Renderer: {PathOf(renderer.gameObject, ship.transform)}");
            report.AppendLine($"   type: {renderer.GetType().Name}, enabled: {renderer.enabled}, active: {renderer.gameObject.activeInHierarchy}");
            report.AppendLine($"   matched by color code: {colorMatch}, under m_sailObject: {underSail}");

            Mesh mesh = GetMesh(renderer);
            if (mesh != null)
            {
                report.AppendLine($"   mesh: {mesh.name}, vertices: {mesh.vertexCount}, readable: {mesh.isReadable}, bounds: {mesh.bounds.size}");
                if (mesh.isReadable && mesh.uv.Length > 0)
                {
                    Vector2 min = new Vector2(mesh.uv.Min(uv => uv.x), mesh.uv.Min(uv => uv.y));
                    Vector2 max = new Vector2(mesh.uv.Max(uv => uv.x), mesh.uv.Max(uv => uv.y));
                    report.AppendLine($"   uv0 range: {min} .. {max}");
                    if (colorMatch || underSail)
                    {
                        string file = $"uv_{Safe(name)}.png";
                        WriteUvLayout(mesh, Path.Combine(dir, file));
                        report.AppendLine($"   uv layout image: {file}");
                    }
                }
            }

            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null) continue;

                Shader shader = material.shader;
                report.AppendLine($"   material: {material.name}, shader: {shader?.name}");
                if (shader == null) continue;

                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    string prop = shader.GetPropertyName(i);
                    ShaderPropertyType type = shader.GetPropertyType(i);
                    string value = DescribeProperty(material, prop, type);
                    report.AppendLine($"     {prop} ({type}): {value}");

                    if (type != ShaderPropertyType.Texture || !(colorMatch || underSail)) continue;
                    Texture texture = material.GetTexture(prop);
                    if (texture == null || !dumped.Add(texture)) continue;

                    string file = $"tex_{Safe(texture.name)}{prop}.png";
                    if (DumpTexture(texture, Path.Combine(dir, file)))
                        report.AppendLine($"       dumped: {file}");
                }
            }
            report.AppendLine();
        }

        private static string DescribeProperty(Material material, string prop, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    return material.GetColor(prop).ToString();
                case ShaderPropertyType.Vector:
                    return material.GetVector(prop).ToString();
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return material.GetFloat(prop).ToString();
                case ShaderPropertyType.Texture:
                    Texture t = material.GetTexture(prop);
                    if (t == null) return "none";
                    return $"{t.name} {t.width}x{t.height} scale {material.GetTextureScale(prop)} offset {material.GetTextureOffset(prop)}";
                default:
                    return "?";
            }
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // Textures in game assets usually aren't CPU-readable, so copy through a RenderTexture.
        private static bool DumpTexture(Texture texture, string path)
        {
            RenderTexture rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
                File.WriteAllBytes(path, copy.EncodeToPNG());
                UnityEngine.Object.Destroy(copy);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"Sail probe could not dump {texture.name}: {e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Draws the mesh's triangles in UV space, so the layout can be laid over the dumped
        // texture. UV (0,0) is the image's bottom-left, matching how Unity samples textures.
        private static void WriteUvLayout(Mesh mesh, string path)
        {
            var image = new Texture2D(UvImageSize, UvImageSize, TextureFormat.RGBA32, false);
            var pixels = new Color32[UvImageSize * UvImageSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);

            Vector2[] uvs = mesh.uv;
            int[] triangles = mesh.triangles;
            var line = new Color32(255, 255, 255, 255);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                DrawLine(pixels, uvs[triangles[i]], uvs[triangles[i + 1]], line);
                DrawLine(pixels, uvs[triangles[i + 1]], uvs[triangles[i + 2]], line);
                DrawLine(pixels, uvs[triangles[i + 2]], uvs[triangles[i]], line);
            }

            image.SetPixels32(pixels);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
        }

        private static void DrawLine(Color32[] pixels, Vector2 a, Vector2 b, Color32 color)
        {
            Vector2 pa = a * (UvImageSize - 1);
            Vector2 pb = b * (UvImageSize - 1);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(pa, pb)));
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = Vector2.Lerp(pa, pb, (float)s / steps);
                int x = Mathf.RoundToInt(p.x);
                int y = Mathf.RoundToInt(p.y);
                if (x < 0 || y < 0 || x >= UvImageSize || y >= UvImageSize) continue;
                pixels[y * UvImageSize + x] = color;
            }
        }

        private static string PathOf(GameObject go, Transform root)
        {
            if (go == null) return "null";
            var parts = new List<string>();
            for (Transform t = go.transform; t != null && t != root; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }
    }
}
#endif
