using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoatCustomizer
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.malafein.shipwrightstouch";
        public const string ModName = "Shipwright's Touch";
        public const string ModVersion = "1.1.0";
        
        public const string ZdoOwnerIdKey = "shipwrightstouch.builder_id";
        public const string ZdoOwnerNameKey = "shipwrightstouch.builder_name";
        public const string ZdoNameKey = "custom_ship_name";
        public const string ZdoStyleKey = "custom_sail_style";

        public static ConfigEntry<bool> AllowShipDeconstruction;
        public static ConfigEntry<bool> AssignBuilderIdentity;
        public static ConfigEntry<KeyboardShortcut> RenameShipKey;
        public static ConfigEntry<KeyboardShortcut> SailColorShipKey;
        public static ConfigEntry<KeyboardShortcut> SailColorPlacingKey;

        private readonly Harmony harmony = new Harmony(ModGUID);

        private void Awake()
        {
            AllowShipDeconstruction = Config.Bind("General", "AllowShipDeconstruction", false, "Allow deconstructing ships with the hammer (middle-mouse button).");
            AssignBuilderIdentity = Config.Bind("General", "AssignBuilderIdentity", true, "Automatically assign your character as the owner when constructing a ship, restricting modifications (renaming, recoloring, deconstruction) to yourself.");

            RenameShipKey = Config.Bind(
                "Controls",
                "RenameShip",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift),
                "Rename the ship you are looking at (rudder, seats, mast, or hull). Either Shift, Ctrl, or Alt key satisfies a modifier."
            );

            SailColorShipKey = Config.Bind(
                "Controls",
                "ChangeSailColor",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftAlt),
                "Cycle the sail color of the ship you are looking at. Either Shift, Ctrl, or Alt key satisfies a modifier."
            );

            SailColorPlacingKey = Config.Bind(
                "Controls",
                "ChangeSailColorWhilePlacing",
                new KeyboardShortcut(KeyCode.E),
                "Cycle the sail color of a ship while placing it with the hammer."
            );

            RenameShipKey.SettingChanged += OnKeybindChanged;
            SailColorShipKey.SettingChanged += OnKeybindChanged;
            SailColorPlacingKey.SettingChanged += OnKeybindChanged;
            
            Logger.LogInfo($"{ModName} {ModVersion} is loading...");
            try 
            {
                harmony.PatchAll();
                Logger.LogInfo($"{ModName} patches applied successfully.");
            }
            catch (System.Exception e)
            {
                Logger.LogError($"{ModName} failed to apply some patches: {e}");
            }
            Logger.LogInfo($"{ModName} loaded!");

            // No-op until ZInput exists; the ZInput.Load postfix covers the normal startup order.
            Keybinds.CheckConflicts();
        }

        private static void OnKeybindChanged(object sender, System.EventArgs e)
        {
            Keybinds.CheckConflicts();
        }

        public static bool CanModifyShip(Ship ship, out string ownerName)
        {
            ownerName = string.Empty;
            if (ship == null) return true;

            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return true;

            long ownerId = nview.GetZDO().GetLong(ZdoOwnerIdKey, 0L);
            ownerName = nview.GetZDO().GetString(ZdoOwnerNameKey);
            
            return ownerId == 0L || ownerId == Player.m_localPlayer?.GetPlayerID();
        }
    }
}
