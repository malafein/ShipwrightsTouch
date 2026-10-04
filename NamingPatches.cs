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
            else
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Only {ownerName} can customize this ship.");
            }
        }
    }
}
