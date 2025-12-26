using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;

namespace ValheimBoatCustomizer
{
    [HarmonyPatch]
    public static class ColoringPatches
    {
        private const string ZdoStyleKey = "custom_sail_style";
        private static int m_selectedStyle = 0;
        private const int MaxStyles = 6; 

        private static readonly Color[] SailColors = new Color[]
        {
            Color.white,      // Default
            Color.red,        // Red
            Color.blue,       // Blue
            Color.green,      // Green
            Color.yellow,     // Yellow
            new Color(0.2f, 0.2f, 0.2f) // Black/Dark
        };

        private static readonly string[] ColorNames = new string[]
        {
            "White",
            "Red",
            "Blue",
            "Green",
            "Yellow",
            "Black"
        };

        private static bool m_isPlacing = false;

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        [HarmonyPrefix]
        private static void Prefix_UpdatePlacement(Player __instance, bool takeInput, float dt, GameObject ___m_placementGhost)
        {
            if (___m_placementGhost == null) return;

            // Try to get Ship or Piece to identify if it's a boat
            Ship ship = ___m_placementGhost.GetComponent<Ship>();
            if (ship == null) return;

            // Apply selected style to ghost immediately
            ApplyStyle(ship, m_selectedStyle);

            // Change style with 'G' key - Only if taking input
            if (takeInput && Input.GetKeyDown(KeyCode.G))
            {
                m_selectedStyle = (m_selectedStyle + 1) % MaxStyles;
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Sail Color: <color=yellow>{ColorNames[m_selectedStyle]}</color>");
            }
        }

        [HarmonyPatch(typeof(Player), "PlacePiece")]
        [HarmonyPrefix]
        private static void Prefix_PlacePiece(Player __instance, Piece piece) 
        { 
            if (piece != null && piece.GetComponent<Ship>() != null)
            {
                m_isPlacing = true; 
            }
        }

        [HarmonyPatch(typeof(Player), "PlacePiece")]
        [HarmonyPostfix]
        private static void Postfix_PlacePiece() 
        { 
            if (m_isPlacing)
            {
                m_isPlacing = false; 
            }
        }

        [HarmonyPatch(typeof(Ship), "Awake")]
        [HarmonyPostfix]
        private static void Postfix_ShipAwake(Ship __instance)
        {
            ZNetView nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            if (m_isPlacing && nview.IsOwner())
            {
                ZLog.LogWarning($"[ValheimBoatMod] Setting initial sail style {m_selectedStyle} for new ship: {__instance.gameObject.name}");
                nview.GetZDO().Set(ZdoStyleKey, m_selectedStyle);
            }

            UpdateSailAppearance(__instance);
        }

        [HarmonyPatch(typeof(Ship), "Start")]
        [HarmonyPostfix]
        private static void Postfix_ShipStart(Ship __instance)
        {
            UpdateSailAppearance(__instance);
        }

        private static void UpdateSailAppearance(Ship ship)
        {
            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            int style = nview.GetZDO().GetInt(ZdoStyleKey, 0);
            ApplyStyle(ship, style);
        }

        private static void ApplyStyle(Ship ship, int style)
        {
            if (style < 0 || style >= SailColors.Length) return;

            Color targetColor = SailColors[style];
            
            Renderer[] renderers = ship.GetComponentsInChildren<Renderer>(true);
            int count = 0;
            foreach (var renderer in renderers)
            {
                string name = renderer.gameObject.name.ToLower();
                if (name.Contains("sail") || name.Contains("cloth") || name.Contains("flag"))
                {
                    count++;
                    MaterialPropertyBlock propBlock = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(propBlock);
                    propBlock.SetColor("_Color", targetColor);
                    renderer.SetPropertyBlock(propBlock);
                }
            }
            
            if (count > 0)
            {
                // ZLog.LogWarning($"[ValheimBoatMod] Applied style {style} to {count} renderers on {ship.gameObject.name}");
            }
        }

        [HarmonyPatch(typeof(Ship), "UpdateSail")]
        [HarmonyPostfix]
        private static void Postfix_UpdateSail(Ship __instance) 
        { 
            UpdateSailAppearance(__instance); 
        }

        [HarmonyPatch(typeof(Ship), "UpdateSailSize")]
        [HarmonyPostfix]
        private static void Postfix_UpdateSailSize(Ship __instance) 
        { 
            UpdateSailAppearance(__instance); 
        }
    }
}
