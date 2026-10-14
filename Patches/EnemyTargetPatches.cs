using HarmonyLib;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Safe Harbor, EnemiesIgnoreUnoccupiedShips: monsters don't choose a ship with nobody aboard.
    //
    // Monsters pick buildings to attack in BaseAI.FindClosestStaticPriorityTarget and
    // FindRandomStaticTarget, the only callers of these two checks (Valheim 1.0). A ship is a Piece
    // on the ship's root, reached from its colliders through GetComponentInParent. Only the choice is
    // filtered: a monster that already has the ship as its target keeps at it (MonsterAI keeps its
    // static target until a creature, damage, fire or a timeout draws it off), so leaving the ship
    // doesn't make it safe mid-fight. Runs on whichever game owns the monster.
    [HarmonyPatch]
    public static class EnemyTargetPatches
    {
        [HarmonyPatch(typeof(Piece), nameof(Piece.IsPriorityTarget))]
        [HarmonyPostfix]
        private static void Postfix_IsPriorityTarget(Piece __instance, ref bool __result)
        {
            if (__result && IsUnoccupiedShip(__instance)) __result = false;
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.IsRandomTarget))]
        [HarmonyPostfix]
        private static void Postfix_IsRandomTarget(Piece __instance, ref bool __result)
        {
            if (__result && IsUnoccupiedShip(__instance)) __result = false;
        }

        // Only asked during target searches (every few seconds per monster), so no caching.
        private static bool IsUnoccupiedShip(Piece piece)
        {
            if (!SailNetwork.EnemiesIgnoreUnoccupiedShips) return false;
            Ship ship = piece.GetComponent<Ship>();
            // CanBeRemoved: no player in the ship's trigger volume, kept on every game.
            return ship != null && ship.CanBeRemoved();
        }
    }
}
