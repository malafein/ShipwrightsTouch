#if DEBUG
using BepInEx.Configuration;
#endif
using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    [HarmonyPatch]
    public static class NamingPatches
    {

        public static Ship GetParentShip(Component component)
        {
            if (component == null) return null;
            if (component is Ship ship) return ship;
            
            Ship found = component.GetComponentInParent<Ship>();
            if (found != null) return found;

            // Robust fallback for boat parts in complex hierarchies (looking up to root and then down)
            Transform root = component.transform.root;
            if (root != null)
            {
                return root.GetComponentInChildren<Ship>();
            }
            
            return null;
        }

        // The rudder and seats are interactable, so vanilla reacts to the Use key itself (take
        // the helm, sit down). When one of our ship shortcuts is pressed over a ship part, cancel
        // that; the actions themselves run in the Player.Update postfixes, which treat every ship
        // part the same way.
        [HarmonyPatch(typeof(Player), "Interact")]
        [HarmonyPrefix]
        private static bool Prefix_PlayerInteract(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (hold || go == null) return true;

            if (!Keybinds.IsDown(Plugin.CustomizeShipKey.Value) && !Keybinds.IsDown(Plugin.SailColorShipKey.Value)) return true;

            // Let containers handle their own interaction (opening storage/quick-stacking)
            if (go.GetComponentInParent<Container>() != null) return true;

            return GetParentShip(go.GetComponent<Component>()) == null;
        }

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer || TextInput.IsVisible()) return;
            if (!Keybinds.IsDown(Plugin.CustomizeShipKey.Value) || !Keybinds.CanTakeInput(__instance)) return;

            GameObject hoverGO = __instance.GetHoverObject();
            if (hoverGO == null) return;

            // Stop if looking at a container to let it open normally
            if (hoverGO.GetComponentInParent<Container>() != null) return;

            Ship ship = GetParentShip(hoverGO.GetComponent<Component>());
            if (ship == null) return;

            if (Plugin.CanModifyShip(ship, out string ownerName))
            {
                CustomizePanel.Open(ship);
            }
            else if (SailModeration.CanModerate && IsPlayerTexture(ship))
            {
                // Someone else's ship: a moderator can't customize it, but can moderate its texture.
                ModerationPanel.Open(ship.GetComponent<ZNetView>().GetZDO().GetString(Plugin.ZdoTextureKey));
            }
            else
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Only {ownerName} can customize this ship.");
            }
        }

#if DEBUG
        // Test aid: gives the ship under the cursor a made-up owner, to test what other players'
        // ships allow (e.g. moderating from a ship). Debug builds only; pressing it again on the
        // same ship gives it back to the local player. F8: vanilla uses F9 (controller layout), and
        // with a modifier KDE takes Ctrl+F9 (window overview) and the game misses the Ctrl release.
        private static readonly KeyboardShortcut DebugFakeOwnerKey = new KeyboardShortcut(KeyCode.F8);
        private const long DebugFakeOwnerId = 1L;

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate_DebugFakeOwner(Player __instance)
        {
            if (__instance != Player.m_localPlayer || TextInput.IsVisible()) return;
            if (!Keybinds.IsDown(DebugFakeOwnerKey) || !Keybinds.CanTakeInput(__instance)) return;

            GameObject hoverGO = __instance.GetHoverObject();
            Ship ship = hoverGO != null ? GetParentShip(hoverGO.GetComponent<Component>()) : null;
            ZNetView nview = ship != null ? ship.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) return;

            ZDO zdo = nview.GetZDO();
            bool fake = zdo.GetLong(Plugin.ZdoOwnerIdKey, 0L) == DebugFakeOwnerId;
            zdo.Set(Plugin.ZdoOwnerIdKey, fake ? __instance.GetPlayerID() : DebugFakeOwnerId);
            zdo.Set(Plugin.ZdoOwnerNameKey, fake ? __instance.GetPlayerName() : "Bjorn");
            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, fake ? "Debug: ship is yours again." : "Debug: ship now belongs to Bjorn.");
        }
#endif

        // A texture that isn't one of the server's own: possibly shared by a player, so moderated.
        private static bool IsPlayerTexture(Ship ship)
        {
            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            string hash = nview.GetZDO().GetString(Plugin.ZdoTextureKey);
            return hash != "" && SailDownloads.Find(hash)?.Source != TextureSource.Server;
        }
    }
}
