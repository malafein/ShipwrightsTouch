using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoatCustomizer
{
    // Valheim 1.0 runs on Unity 6's Input System, so legacy UnityEngine.Input and BepInEx's
    // KeyboardShortcut.IsDown() no longer see key presses. Shortcuts are read through ZInput.
    [HarmonyPatch]
    public static class Keybinds
    {
        // Vanilla buttons that share a key with our shortcuts on purpose, so they are never
        // reported as conflicts. "Use" is cancelled by NamingPatches.Prefix_PlayerInteract over
        // ship parts and has nothing to interact with while placing. TabLeft/TabRight only act
        // inside tabbed menus, where Player.TakeInput() is false and none of our shortcuts run.
        private static readonly HashSet<string> IgnoredVanillaButtons = new HashSet<string>
        {
            "Use",
            "TabLeft",
            "TabRight"
        };

        private static readonly MethodInfo TakeInputMethod = AccessTools.Method(typeof(Player), "TakeInput");
        private static readonly FieldInfo ButtonsField = AccessTools.Field(typeof(ZInput), "m_buttons");
        private static readonly MethodInfo KeyCodeToPathMethod = AccessTools.Method(typeof(ZInput), "KeyCodeToPath");

        private static List<string> s_lastConflicts = new List<string>();

        public static bool IsDown(KeyboardShortcut shortcut)
        {
            KeyCode mainKey = shortcut.MainKey;
            if (mainKey == KeyCode.None || !ZInput.IsKeyCodeValid(mainKey)) return false;
            if (!ZInput.GetKeyDown(mainKey, false)) return false;

            return ModifierMatches(shortcut, KeyCode.LeftShift, KeyCode.RightShift)
                && ModifierMatches(shortcut, KeyCode.LeftControl, KeyCode.RightControl)
                && ModifierMatches(shortcut, KeyCode.LeftAlt, KeyCode.RightAlt);
        }

        // Player.TakeInput() is false while chat, menus, or the inventory have focus. The Input
        // System reads raw keys regardless of UI focus, so without this gate typing a capital E
        // in chat would trigger Shift+E.
        public static bool CanTakeInput(Player player)
        {
            return (bool)TakeInputMethod.Invoke(player, null);
        }

        public static string Format(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None) return "Not set";

            var parts = new List<string>();
            if (HasModifier(shortcut, KeyCode.LeftControl, KeyCode.RightControl)) parts.Add("Ctrl");
            if (HasModifier(shortcut, KeyCode.LeftShift, KeyCode.RightShift)) parts.Add("Shift");
            if (HasModifier(shortcut, KeyCode.LeftAlt, KeyCode.RightAlt)) parts.Add("Alt");
            parts.Add(shortcut.MainKey.ToString());
            return string.Join(" + ", parts);
        }

        // Either side satisfies a modifier, and a modifier the shortcut doesn't list must not be
        // held, so Shift+E, Alt+E, and plain E stay distinct.
        private static bool ModifierMatches(KeyboardShortcut shortcut, KeyCode left, KeyCode right)
        {
            bool held = ZInput.GetKey(left, false) || ZInput.GetKey(right, false);
            return HasModifier(shortcut, left, right) == held;
        }

        private static bool HasModifier(KeyboardShortcut shortcut, KeyCode left, KeyCode right)
        {
            return shortcut.Modifiers.Any(m => m == left || m == right);
        }

        // Some gamepad layouts register placeholder buttons through ZInput.AddUnusedButton
        // (JoyTabLeft, JoyTabRight) with an empty InputAction. ButtonDef.GetActionPath() indexes
        // bindings[0] unguarded, so it throws for them. They have no key, so they can't conflict.
        private static string GetBoundPath(ZInput.ButtonDef button)
        {
            try
            {
                return button.GetActionPath();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        // ZInput.Load applies saved rebinds at startup, Save runs when the player saves the
        // controls menu, and ResetToDefault restores vanilla bindings. Re-check after each.
        // The startup Load is called from inside the ZInput constructor, before ZInput.instance
        // is assigned, so the hooks pass along the instance being patched.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.Load))]
        [HarmonyPostfix]
        private static void Postfix_ZInputLoad(ZInput __instance)
        {
            CheckConflicts(__instance);
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.Save))]
        [HarmonyPostfix]
        private static void Postfix_ZInputSave(ZInput __instance)
        {
            CheckConflicts(__instance);
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.ResetToDefault))]
        [HarmonyPostfix]
        private static void Postfix_ZInputResetToDefault(ZInput __instance)
        {
            CheckConflicts(__instance);
        }

        // Warns when a shortcut's main key is also bound to a vanilla action (vanilla mostly
        // ignores modifiers, so Shift+F would still fire the Forsaken power on F), when it uses a
        // key Valheim cannot read, or when two shortcuts that run in the same context are
        // identical. Only logs when the set of conflicts changes.
        public static void CheckConflicts(ZInput input = null)
        {
            // These checks run inside vanilla ZInput.Load/Save/ResetToDefault and config change
            // events. A failure here must never escape into the game's input handling, where it
            // could stop saved key bindings from loading.
            try
            {
                FindAndLogConflicts(input ?? ZInput.instance);
            }
            catch (Exception e)
            {
                ZLog.LogWarning($"[ShipwrightsTouch] Keybinding conflict check failed: {e.Message}");
            }
        }

        private static void FindAndLogConflicts(ZInput input)
        {
            if (input == null || ButtonsField == null || KeyCodeToPathMethod == null) return;
            if (!(ButtonsField.GetValue(input) is Dictionary<string, ZInput.ButtonDef> buttons)) return;

            var conflicts = new List<string>();
            var shortcuts = new[]
            {
                Plugin.RenameShipKey,
                Plugin.SailColorShipKey,
                Plugin.SailColorPlacingKey
            };

            foreach (ConfigEntry<KeyboardShortcut> entry in shortcuts)
            {
                if (entry == null) continue;

                KeyboardShortcut shortcut = entry.Value;
                KeyCode mainKey = shortcut.MainKey;
                if (mainKey == KeyCode.None) continue;

                string name = entry.Definition.Key;
                if (!ZInput.IsKeyCodeValid(mainKey))
                {
                    conflicts.Add($"{name} ({Format(shortcut)}) uses {mainKey}, which Valheim cannot read. The shortcut will never fire.");
                    continue;
                }

                string path = (string)KeyCodeToPathMethod.Invoke(null, new object[] { mainKey, false });
                IEnumerable<string> clashes = buttons.Values
                    .Where(b => !IgnoredVanillaButtons.Contains(b.Name))
                    .Where(b => string.Equals(GetBoundPath(b), path, StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.Name)
                    .Distinct();

                foreach (string button in clashes)
                {
                    conflicts.Add($"{name} ({Format(shortcut)}) shares {mainKey} with the game's \"{button}\" binding. Both will trigger.");
                }
            }

            if (Plugin.RenameShipKey != null && Plugin.SailColorShipKey != null
                && Format(Plugin.RenameShipKey.Value) == Format(Plugin.SailColorShipKey.Value)
                && Plugin.RenameShipKey.Value.MainKey != KeyCode.None)
            {
                conflicts.Add($"RenameShip and ChangeSailColor are both {Format(Plugin.RenameShipKey.Value)}. Both will trigger at once.");
            }

            if (conflicts.SequenceEqual(s_lastConflicts)) return;

            foreach (string conflict in conflicts)
            {
                ZLog.LogWarning($"[ShipwrightsTouch] Keybinding conflict: {conflict}");
            }
            if (conflicts.Count == 0)
            {
                ZLog.Log("[ShipwrightsTouch] Keybinding conflicts resolved.");
            }
            s_lastConflicts = conflicts;
        }
    }
}
