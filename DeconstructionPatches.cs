using HarmonyLib;
using UnityEngine;

namespace ValheimBoatCustomizer
{
    [HarmonyPatch]
    public static class DeconstructionPatches
    {
        [HarmonyPatch(typeof(Player), "CheckCanRemovePiece")]
        [HarmonyPrefix]
        private static void Prefix_CheckCanRemovePiece(ref Piece piece)
        {
            if (piece == null || !Plugin.AllowShipDeconstruction.Value) return;

            Ship ship = piece.GetComponentInParent<Ship>();
            if (ship == null) 
            {
                // Also check if it's a ship piece by name as a fallback
                if (piece.m_name.Contains("karve") || piece.m_name.Contains("longship") || piece.m_name.Contains("raft"))
                {
                    ZLog.LogWarning($"[ValheimBoatMod] Found ship piece {piece.m_name} but no Ship component in parent.");
                }
                return;
            }

            if (!piece.m_canBeRemoved)
            {
                piece.m_canBeRemoved = true;
                ZLog.LogWarning($"[ValheimBoatMod] Force-allowing deconstruction of ship piece: {piece.m_name}");
            }
        }

        [HarmonyPatch(typeof(Player), "RemovePiece")]
        [HarmonyPrefix]
        private static bool Prefix_RemovePiece(Player __instance)
        {
            if (!Plugin.AllowShipDeconstruction.Value) return true;

            GameObject hover = __instance.GetHoverObject();
            if (hover == null) return true;

            Ship ship = hover.GetComponentInParent<Ship>();
            if (ship == null) return true;

            ItemDrop.ItemData currentItem = __instance.GetCurrentWeapon();
            if (currentItem == null || currentItem.m_shared.m_buildPieces == null) return true;

            Piece shipPiece = ship.GetComponent<Piece>();
            if (shipPiece == null) return true;

            if (!shipPiece.m_canBeRemoved)
            {
                shipPiece.m_canBeRemoved = true;
            }

            return true;
        }
    }
}
