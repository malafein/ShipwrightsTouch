using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MagicaCloth2;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Applies sail color and custom texture through a MaterialPropertyBlock, so the shared sail
    // material is never instanced or modified. The sail shader (Custom/Vegetation) multiplies
    // _MainTex by _Color, so a color tints a custom texture and white leaves it as-is.
    public static class SailAppearance
    {
        private class SailPart
        {
            public Renderer Renderer;
            public Texture VanillaTexture;
            public Vector4 VanillaScaleOffset;
            public Vector4 CustomScaleOffset;
        }

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexScaleOffsetId = Shader.PropertyToID("_MainTex_ST");

        private static readonly ConditionalWeakTable<Ship, SailPart[]> s_parts = new ConditionalWeakTable<Ship, SailPart[]>();
        private static readonly MaterialPropertyBlock s_block = new MaterialPropertyBlock();

        public static void Apply(Ship ship, Color color, Texture2D texture)
        {
            foreach (SailPart part in s_parts.GetValue(ship, FindParts))
            {
                if (part.Renderer == null) continue;

                part.Renderer.GetPropertyBlock(s_block);
                s_block.SetColor(ColorId, color);

                // A property block can't unset a property, so going back to vanilla means
                // writing the material's own texture and tiling back explicitly.
                if (texture != null)
                {
                    s_block.SetTexture(MainTexId, texture);
                    s_block.SetVector(MainTexScaleOffsetId, part.CustomScaleOffset);
                }
                else if (part.VanillaTexture != null)
                {
                    s_block.SetTexture(MainTexId, part.VanillaTexture);
                    s_block.SetVector(MainTexScaleOffsetId, part.VanillaScaleOffset);
                }

                part.Renderer.SetPropertyBlock(s_block);
            }
        }

        // Every vanilla ship's sail is a MagicaCloth, and the cloth lists exactly the renderers it
        // drives (which leaves out e.g. the ropes under the drakkar's sail). Ships without one
        // (modded ships, placement ghosts if the cloth isn't set up yet) fall back to the name
        // match the coloring has always used.
        private static SailPart[] FindParts(Ship ship)
        {
            IEnumerable<Renderer> renderers;
            MagicaCloth cloth = ship.m_sailCloth;
            if (cloth != null && cloth.SerializeData.sourceRenderers.Any(r => r != null))
            {
                renderers = cloth.SerializeData.sourceRenderers.Where(r => r != null);
            }
            else
            {
                renderers = ship.GetComponentsInChildren<Renderer>(true).Where(r =>
                {
                    string name = r.gameObject.name.ToLower();
                    return name.Contains("sail") || name.Contains("cloth") || name.Contains("flag");
                });
            }

            return renderers.Select(CreatePart).ToArray();
        }

        private static SailPart CreatePart(Renderer renderer)
        {
            var part = new SailPart
            {
                Renderer = renderer,
                CustomScaleOffset = FitToUvBounds(MeshOf(renderer))
            };

            Material material = renderer.sharedMaterial;
            if (material != null && material.HasProperty(MainTexId))
            {
                part.VanillaTexture = material.GetTexture(MainTexId);
                Vector2 scale = material.GetTextureScale(MainTexId);
                Vector2 offset = material.GetTextureOffset(MainTexId);
                part.VanillaScaleOffset = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            return part;
        }

        // The vanilla sails only use part of their texture (u 0.01-0.99, v 0.15-0.85 on all four
        // ships), so stretch a custom image over exactly the UV area the mesh covers. Every ship
        // then shows the whole image; the drakkar's sail just has one corner cut off.
        private static Vector4 FitToUvBounds(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return new Vector4(1f, 1f, 0f, 0f);

            Vector2[] uvs = mesh.uv;
            if (uvs.Length == 0) return new Vector4(1f, 1f, 0f, 0f);

            float minU = uvs.Min(uv => uv.x);
            float maxU = uvs.Max(uv => uv.x);
            float minV = uvs.Min(uv => uv.y);
            float maxV = uvs.Max(uv => uv.y);
            if (maxU - minU < 0.001f || maxV - minV < 0.001f) return new Vector4(1f, 1f, 0f, 0f);

            // The shader samples at uv * scale + offset; map [min, max] onto [0, 1].
            float scaleU = 1f / (maxU - minU);
            float scaleV = 1f / (maxV - minV);
            return new Vector4(scaleU, scaleV, -minU * scaleU, -minV * scaleV);
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
