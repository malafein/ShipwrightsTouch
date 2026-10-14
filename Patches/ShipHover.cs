using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // The hull has no hover component of its own (only the rudder, ladder, seats and chests do). The
    // game still makes it the hovered object (the ship's rigidbody, Player.FindHoverObject), so the
    // ship's shortcuts already worked there, but the HUD only shows text from a Hoverable, so looking
    // at the hull showed nothing. This one gives it the ship's name, builder and prompts, and the
    // rescue prompt when it's capsized. Added to every ship that lacks one.
    public class ShipHover : MonoBehaviour, Hoverable
    {
        public string GetHoverText()
        {
            string text = "";
            HoverTextPatches.ApplyNamingHover(this, ref text);
            CapsizePatches.AppendRescuePrompt(this, ref text);
            return text;
        }

        public string GetHoverName()
        {
            ZNetView nview = GetComponent<ZNetView>();
            return nview != null && nview.IsValid() ? Plugin.PlainText(nview.GetZDO().GetString(Plugin.ZdoNameKey)) : "";
        }

        public float GetHoverOffset() => 0f;

        [HarmonyPatch]
        private static class Patches
        {
            [HarmonyPatch(typeof(Ship), "Awake")]
            [HarmonyPostfix]
            private static void Postfix_ShipAwake(Ship __instance)
            {
                if (__instance.GetComponent<Hoverable>() == null) __instance.gameObject.AddComponent<ShipHover>();
            }
        }
    }
}
