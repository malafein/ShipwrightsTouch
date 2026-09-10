using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoatCustomizer
{
    [HarmonyPatch]
    public static class HoverTextPatches
    {
        [HarmonyPatch(typeof(HoverText), "GetHoverText")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.VeryLow)]
        private static void Postfix_HoverText(HoverText __instance, ref string __result)
        {
            ApplyNamingHover(__instance, ref __result);
        }

        [HarmonyPatch(typeof(ShipControlls), "GetHoverText")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.VeryLow)]
        private static void Postfix_RudderHoverText(ShipControlls __instance, ref string __result)
        {
            ApplyNamingHover(__instance, ref __result);
        }

        [HarmonyPatch(typeof(Container), "GetHoverText")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.VeryLow)]
        private static void Postfix_ContainerHoverText(Container __instance, ref string __result)
        {
            ApplyNamingHover(__instance, ref __result);
        }

        // Seats/Stools use Chair component
        [HarmonyPatch(typeof(Chair), "GetHoverText")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.VeryLow)]
        private static void Postfix_ChairHoverText(Chair __instance, ref string __result)
        {
            ApplyNamingHover(__instance, ref __result);
        }

        private static void ApplyNamingHover(Component instance, ref string result)
        {
            Ship ship = NamingPatches.GetParentShip(instance);
            if (ship == null) return;

            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            string shipName = nview.GetZDO().GetString(Plugin.ZdoNameKey);
            string builderName = nview.GetZDO().GetString(Plugin.ZdoOwnerNameKey);
            
            if (!string.IsNullOrEmpty(shipName))
            {
                string header = $"<color=yellow>{shipName}</color>";
                // Always prepend as first line if not already there
                if (result == null || !result.StartsWith(header))
                {
                    result = string.IsNullOrEmpty(result) ? header : (header + "\n" + result);
                }
            }

            // Add naming prompt ONLY to non-container ship parts to avoid interaction conflicts
            if (!(instance is Container))
            {
                bool canModify = Plugin.CanModifyShip(ship, out _);
                if (canModify)
                {
                    AppendPrompt(ref result, Plugin.RenameShipKey.Value, NamingPatches.GetRenameTitle());
                    AppendPrompt(ref result, Plugin.SailColorShipKey.Value, "Change Sail Color");
                }

                if (!string.IsNullOrEmpty(builderName))
                {
                    string builderText = $"<size=80%><color=#C0C0C0>Built by: {builderName}</color></size>";
                    if (result != null && !result.Contains("Built by:"))
                    {
                        result += "\n" + builderText;
                    }
                }
            }
        }

        // Prompts show the configured shortcut, and are omitted when the shortcut is unbound.
        private static void AppendPrompt(ref string result, KeyboardShortcut shortcut, string label)
        {
            if (result == null || shortcut.MainKey == KeyCode.None) return;

            string prompt = $"[<color=yellow><b>{Keybinds.Format(shortcut)}</b></color>] {label}";
            if (!result.Contains(prompt))
                result += "\n" + prompt;
        }
    }
}
