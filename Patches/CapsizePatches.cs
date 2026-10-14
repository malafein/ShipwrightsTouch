#if DEBUG
using BepInEx.Configuration;
#endif
using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Safe Harbor, RescueCapsizedShips. Vanilla damages a capsized ship every second until it breaks
    // (Ship.UpdateUpsideDmg), and nothing can turn it back over. With the setting on:
    // - capsize damage still ticks, so the player sees the urgency, but never takes the ship below
    //   HealthFloor of its maximum; other damage (monsters, rocks) is untouched;
    // - holding the interact key on the hull for HoldSeconds turns it upright (ShipRighting).
    // Both run on the game that owns the ship; righting claims ownership first, so it doesn't depend
    // on the current owner having the mod.
    [HarmonyPatch]
    public static class CapsizePatches
    {
        private const float HealthFloor = 0.1f;
        private const float HoldSeconds = 2f;

        // Righting is offered once the ship leans this far: up.y below 0.25 is about 75 degrees, so a
        // ship stuck on its side counts too. Capsize damage keeps vanilla's test (upside down).
        private const float CapsizedUpY = 0.25f;

        // The ship the interact key is being held on, and for how long.
        private static Ship s_holdShip;
        private static float s_holdTime;

        [HarmonyPatch(typeof(Ship), "UpdateUpsideDmg")]
        [HarmonyPrefix]
        private static bool Prefix_UpdateUpsideDmg(Ship __instance, float dt, ref float ___m_upsideDownDmgTimer, IDestructible ___m_destructible)
        {
            if (!SailNetwork.RescueCapsizedShips) return true;

            // Vanilla's own timing, with the damage capped at what's left above the floor.
            if (__instance.transform.up.y >= 0f) return false;
            ___m_upsideDownDmgTimer += dt;
            if (___m_upsideDownDmgTimer <= __instance.m_upsideDownDmgInterval) return false;
            ___m_upsideDownDmgTimer = 0f;

            var hit = new HitData();
            hit.m_damage.m_blunt = __instance.m_upsideDownDmg;
            hit.m_point = __instance.transform.position;
            hit.m_dir = Vector3.up;
            if (!CapToFloor(__instance, hit)) return false;
            ___m_destructible.Damage(hit);
            return false;
        }

        // Scales the hit so it can't take the ship below the floor, counting the ship's damage
        // modifiers the way WearNTear.RPC_Damage will. False when there's nothing left to take.
        private static bool CapToFloor(Ship ship, HitData hit)
        {
            WearNTear wnt = ship.GetComponent<WearNTear>();
            ZNetView nview = ship.GetComponent<ZNetView>();
            if (wnt == null || nview == null || !nview.IsValid()) return true;

            float health = nview.GetZDO().GetFloat(ZDOVars.s_health, wnt.m_health);
            // Under 1: what's left of a capped hit's rounding, not worth another tick.
            float room = health - wnt.m_health * HealthFloor;
            if (room < 1f) return false;

            HitData expected = hit.Clone();
            expected.ApplyResistance(wnt.m_damages, out _);
            float damage = expected.GetTotalDamage();
            if (damage > room)
            {
                hit.m_damage.m_blunt *= room / damage;
                Log.Debug($"Capsize damage stopped at {HealthFloor:P0} of the ship's health ({wnt.m_health * HealthFloor:F0} of {wnt.m_health:F0}).");
            }
            return true;
        }

        public static bool IsCapsized(Ship ship) => ship.transform.up.y < CapsizedUpY;

        // The capsized ship whose hull the player is looking at, if it can be righted. Parts with
        // their own interaction (rudder, ladder, chests) keep it; the hull is the ship's root.
        private static Ship HoveredCapsizedShip(Player player)
        {
            if (!SailNetwork.RescueCapsizedShips) return null;
            GameObject hover = player.GetHoverObject();
            if (hover == null || hover.GetComponent<Interactable>() != null) return null;
            Ship ship = hover.GetComponent<Ship>();
            if (ship == null || !IsCapsized(ship) || ship.GetComponent<ShipRighting>() != null) return null;
            ZNetView nview = ship.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() ? ship : null;
        }

        // Added to the hull's hover text (ShipHover).
        public static void AppendRescuePrompt(Component hull, ref string result)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;
            Ship ship = HoveredCapsizedShip(player);
            if (ship == null || ship.gameObject != hull.gameObject) return;

            string line = s_holdShip == ship && s_holdTime > 0f
                ? $"Righting the ship... {Mathf.FloorToInt(s_holdTime / HoldSeconds * 100f)}%"
                : Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>] Hold to right the ship");
            result = string.IsNullOrEmpty(result) ? line : result + "\n" + line;
        }

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            Ship ship = HoveredCapsizedShip(__instance);
            bool holding = ship != null
                && (ZInput.GetButton("Use") || ZInput.GetButton("JoyUse"))
                && !Hud.InRadial()
                && Keybinds.CanTakeInput(__instance);
            if (!holding || ship != s_holdShip)
            {
                s_holdShip = holding ? ship : null;
                s_holdTime = 0f;
                if (!holding) return;
            }

            s_holdTime += Time.deltaTime;
            if (s_holdTime < HoldSeconds) return;
            s_holdShip = null;
            s_holdTime = 0f;
            Log.Info("Righting a capsized ship.");
            ShipRighting.Begin(ship);
        }

#if DEBUG
        // Test aid: flips the hovered ship upside down. Debug builds only. F7: unbound in vanilla.
        private static readonly KeyboardShortcut DebugFlipKey = new KeyboardShortcut(KeyCode.F7);

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix_PlayerUpdate_DebugFlip(Player __instance)
        {
            if (__instance != Player.m_localPlayer || TextInput.IsVisible()) return;
            if (!Keybinds.IsDown(DebugFlipKey) || !Keybinds.CanTakeInput(__instance)) return;

            GameObject hover = __instance.GetHoverObject();
            Ship ship = hover != null ? NamingPatches.GetParentShip(hover.GetComponent<Component>()) : null;
            ZNetView nview = ship != null ? ship.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) return;

            nview.ClaimOwnership();
            Rigidbody body = ship.GetComponent<Rigidbody>();
            Quaternion flipped = Quaternion.AngleAxis(180f, ship.transform.forward) * ship.transform.rotation;
            Vector3 lifted = ship.transform.position + Vector3.up;
            if (body != null)
            {
                body.position = lifted;
                body.rotation = flipped;
                body.angularVelocity = Vector3.zero;
            }
            ship.transform.SetPositionAndRotation(lifted, flipped);
            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, "Debug: ship flipped.");
        }
#endif
    }

    // Turns a capsized ship upright over a moment: keeps its heading, levels it and lifts it a little
    // so the hull clears the water or the shore. Runs on the owning game, which the player who righted
    // it became.
    public class ShipRighting : MonoBehaviour
    {
        private const float Duration = 1.5f;
        private const float Lift = 1f;

        private ZNetView _nview;
        private Rigidbody _body;
        private Quaternion _from;
        private Quaternion _to;
        private Vector3 _start;
        private float _time;

        public static void Begin(Ship ship)
        {
            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            nview.ClaimOwnership();
            ship.gameObject.AddComponent<ShipRighting>();
        }

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _body = GetComponent<Rigidbody>();
            _from = transform.rotation;
            _start = transform.position;

            // Heading from the bow, or from the keel's direction if the bow points straight up or down.
            Vector3 heading = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (heading.sqrMagnitude < 0.01f) heading = Vector3.ProjectOnPlane(-transform.up, Vector3.up);
            if (heading.sqrMagnitude < 0.01f) heading = Vector3.forward;
            _to = Quaternion.LookRotation(heading.normalized, Vector3.up);
        }

        private void FixedUpdate()
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner() || _body == null)
            {
                Destroy(this);
                return;
            }

            _time += Time.fixedDeltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_time / Duration));
            _body.MoveRotation(Quaternion.Slerp(_from, _to, t));
            _body.MovePosition(new Vector3(_body.position.x, Mathf.Max(_body.position.y, _start.y + Lift * t), _body.position.z));
            _body.angularVelocity = Vector3.zero;
            if (_time >= Duration) Destroy(this);
        }
    }
}
