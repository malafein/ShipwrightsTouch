using HarmonyLib;
using UnityEngine;

namespace ValheimBoatCustomizer
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

        internal static string GetRenameTitle()
        {
            try
            {
                string title = Localization.instance.Localize("$text_rename");
                if (string.IsNullOrEmpty(title) || title.StartsWith("$") || title.ToLower().Contains("text_rename")) 
                    return "Rename Ship";
                return title;
            }
            catch { return "Rename Ship"; }
        }



        [HarmonyPatch(typeof(Player), "Interact")]
        [HarmonyPrefix]
        private static bool Prefix_PlayerInteract(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (hold || go == null) return true;

            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                // Let containers handle their own interaction (opening storage/quick-stacking)
                if (go.GetComponentInParent<Container>() != null) return true;

                Ship ship = GetParentShip(go.GetComponent<Component>());
                if (ship != null)
                {
                    if (Plugin.CanModifyShip(ship, out string ownerName))
                    {
                        TextInput.instance.RequestText(new ShipNameTextReceiver(ship), GetRenameTitle(), 20);
                    }
                    else
                    {
                        MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Only {ownerName} can rename this ship.");
                    }
                    return false; // Intercept vanilla rudder/seat interaction
                }
            }
            return true;
        }

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer || TextInput.IsVisible()) return;

            if (Input.GetKeyDown(KeyCode.E) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                GameObject hoverGO = __instance.GetHoverObject();
                if (hoverGO == null) return;

                // Stop if looking at a container to let it open normally
                if (hoverGO.GetComponentInParent<Container>() != null) return;

                // Handle hull/masts which are usually not interactable
                if (hoverGO.GetComponentInParent<Interactable>() == null)
                {
                    Ship ship = GetParentShip(hoverGO.GetComponent<Component>());
                    if (ship != null)
                    {
                        if (Plugin.CanModifyShip(ship, out string ownerName))
                        {
                            TextInput.instance.RequestText(new ShipNameTextReceiver(ship), GetRenameTitle(), 20);
                        }
                        else
                        {
                            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"Only {ownerName} can rename this ship.");
                        }
                    }
                }
            }
        }
    
        private class ShipNameTextReceiver : TextReceiver
        {
            private readonly Ship m_ship;
            public ShipNameTextReceiver(Ship ship) => m_ship = ship;

            public string GetText()
            {
                ZNetView nview = m_ship.GetComponent<ZNetView>();
                return (nview != null && nview.IsValid()) ? nview.GetZDO().GetString(Plugin.ZdoNameKey) : "";
            }

            public void SetText(string text)
            {
                ZNetView nview = m_ship.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    nview.GetZDO().Set(Plugin.ZdoNameKey, text);
                }
                // No synchronization to container ZDOs to avoid conflicts/duplication.
                // Hover header is handled dynamically in ApplyNamingHover via ship lookup.
            }
        }
    }
}
