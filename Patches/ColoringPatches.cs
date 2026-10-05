using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;
using BepInEx.Configuration;

namespace malafein.Valheim.ShipwrightsTouch
{
    [HarmonyPatch]
    public static class ColoringPatches
    {
        private static int m_selectedStyle = 0;

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
                m_selectedStyle = (m_selectedStyle + 1) % SailStyle.Presets.Length;
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Sail Color: <color=yellow>{SailStyle.PresetNames[m_selectedStyle]}</color>");
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

        // While the customization panel is open on a ship, it shows the panel's unsaved choice
        // instead of the stored one.
        internal static void UpdateSailAppearance(Ship ship)
        {
            if (CustomizePanel.TryGetPreview(ship, out Color previewColor, out Texture2D previewTexture))
            {
                SailAppearance.Apply(ship, previewColor, previewTexture);
                return;
            }

            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            ZDO zdo = nview.GetZDO();
            if (!SailStyle.TryGetColor(zdo, out Color color)) return;

            // A server that turns custom textures off shows the vanilla sail on every ship.
            Texture2D texture = SailNetwork.Policy.AllowCustomTextures ? SailTextures.Get(zdo.GetString(Plugin.ZdoTextureKey)) : null;
            SailAppearance.Apply(ship, color, texture);
        }

        // Placement ghosts have no ZDO; they preview the selected color only.
        private static void ApplyStyle(Ship ship, int style)
        {
            if (style < 0 || style >= SailStyle.Presets.Length) return;

            SailAppearance.Apply(ship, SailStyle.Presets[style], null);
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
                    // A custom color counts as its nearest preset, so cycling continues from there.
                    int currentStyle = nview.GetZDO().GetInt(Plugin.ZdoStyleKey, 0);
                    int nextStyle = (currentStyle + 1) % SailStyle.Presets.Length;
                    SailStyle.SetPreset(nview.GetZDO(), nextStyle);
                    UpdateSailAppearance(ship);
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Sail Color: <color=yellow>{SailStyle.PresetNames[nextStyle]}</color>");
                }
            }
            else
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Only {ownerName} can change this ship's sail color.");
            }
        }
    }
}
