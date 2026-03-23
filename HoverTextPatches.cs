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
                    string renamePrompt = $"[<color=yellow><b>Shift + E</b></color>] {NamingPatches.GetRenameTitle()}";
                    if (result != null && !result.Contains("Shift + E"))
                        result += "\n" + renamePrompt;

                    string colorPrompt = $"[<color=yellow><b>Shift + G</b></color>] Change Sail Color";
                    if (result != null && !result.Contains("Shift + G"))
                        result += "\n" + colorPrompt;
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
    }
}
