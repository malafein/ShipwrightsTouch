using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace malafein.Valheim.ShipwrightsTouch
{
    [HarmonyPatch]
    public static class ColoringPatches
    {
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

            // Cycle the style with the configured key, only while taking input
            if (takeInput && Keybinds.IsDown(Plugin.SailColorPlacingKey.Value))
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
                Log.Debug($"Setting initial sail style {m_selectedStyle} for new ship: {__instance.gameObject.name}");
                nview.GetZDO().Set(Plugin.ZdoStyleKey, m_selectedStyle);

                if (Plugin.AssignBuilderIdentity.Value && Player.m_localPlayer != null)
                {
                    nview.GetZDO().Set(Plugin.ZdoOwnerIdKey, Player.m_localPlayer.GetPlayerID());
                    nview.GetZDO().Set(Plugin.ZdoOwnerNameKey, Player.m_localPlayer.GetPlayerName());
                }
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

            ZDO zdo = nview.GetZDO();
            int style = zdo.GetInt(Plugin.ZdoStyleKey, 0);
            if (style < 0 || style >= SailColors.Length) return;

            SailAppearance.Apply(ship, SailColors[style], SailTextures.Get(zdo.GetString(Plugin.ZdoTextureKey)));
        }

        // Placement ghosts have no ZDO; they preview the selected color only.
        private static void ApplyStyle(Ship ship, int style)
        {
            if (style < 0 || style >= SailColors.Length) return;

            SailAppearance.Apply(ship, SailColors[style], null);
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

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer || TextInput.IsVisible()) return;
            if (!Keybinds.IsDown(Plugin.SailColorShipKey.Value) || !Keybinds.CanTakeInput(__instance)) return;

            GameObject hoverGO = __instance.GetHoverObject();
            if (hoverGO == null) return;

            if (hoverGO.GetComponentInParent<Container>() != null) return;

            Ship ship = NamingPatches.GetParentShip(hoverGO.GetComponent<Component>());
            if (ship == null) return;

            if (Plugin.CanModifyShip(ship, out string ownerName))
            {
                ZNetView nview = ship.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    int currentStyle = nview.GetZDO().GetInt(Plugin.ZdoStyleKey, 0);
                    int nextStyle = (currentStyle + 1) % MaxStyles;
                    nview.GetZDO().Set(Plugin.ZdoStyleKey, nextStyle);
                    UpdateSailAppearance(ship);
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Sail Color: <color=yellow>{ColorNames[nextStyle]}</color>");
                }
            }
            else
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Only {ownerName} can change this ship's sail color.");
            }
        }

#if DEBUG
        // Temporary way to try custom textures until the customization panel exists: cycles the
        // hovered ship through the textures in SailTextures.Folder, then back to vanilla.
        private static readonly KeyboardShortcut DebugCycleTextureKey = new KeyboardShortcut(KeyCode.Y, KeyCode.RightControl);

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate_DebugTexture(Player __instance)
        {
            if (__instance != Player.m_localPlayer || TextInput.IsVisible()) return;
            if (!Keybinds.IsDown(DebugCycleTextureKey) || !Keybinds.CanTakeInput(__instance)) return;

            GameObject hoverGO = __instance.GetHoverObject();
            if (hoverGO == null) return;

            Ship ship = NamingPatches.GetParentShip(hoverGO.GetComponent<Component>());
            if (ship == null || !Plugin.CanModifyShip(ship, out _)) return;

            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            SailTextures.Refresh();
            var entries = SailTextures.Entries;
            string current = nview.GetZDO().GetString(Plugin.ZdoTextureKey);
            int index = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Hash == current) index = i;
            }

            string next = index + 1 < entries.Count ? entries[index + 1].Hash : "";
            nview.GetZDO().Set(Plugin.ZdoTextureKey, next);
            UpdateSailAppearance(ship);

            string label = next == "" ? "Vanilla" : SailTextures.NameOf(next);
            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Sail Texture: <color=yellow>{label}</color> ({entries.Count} in folder)");
        }
#endif
    }
}
