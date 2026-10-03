using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.malafein.shipwrightstouch";
        public const string ModName = "Shipwright's Touch";
        public const string ModVersion = "1.1.1";
        
        public const string ZdoOwnerIdKey = "shipwrightstouch.builder_id";
        public const string ZdoOwnerNameKey = "shipwrightstouch.builder_name";
        public const string ZdoNameKey = "custom_ship_name";
        public const string ZdoStyleKey = "custom_sail_style";
        public const string ZdoTextureKey = "shipwrightstouch.sail_texture";

        public static ConfigEntry<bool> AllowShipDeconstruction;
        public static ConfigEntry<bool> AssignBuilderIdentity;
        public static ConfigEntry<KeyboardShortcut> RenameShipKey;
        public static ConfigEntry<KeyboardShortcut> SailColorShipKey;
        public static ConfigEntry<KeyboardShortcut> SailColorPlacingKey;

        private readonly Harmony harmony = new Harmony(ModGUID);

        private void Awake()
        {
            Log.Init(Logger);

            AllowShipDeconstruction = Config.Bind("General", "AllowShipDeconstruction", false, "Allow deconstructing ships with the hammer (middle-mouse button).");
            AssignBuilderIdentity = Config.Bind("General", "AssignBuilderIdentity", true, "Automatically assign your character as the owner when constructing a ship, restricting modifications (renaming, recoloring, deconstruction) to yourself.");

            RenameShipKey = Config.Bind(
                "Controls",
                "RenameShip",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift),
                "Rename the ship you are looking at (rudder, seats, mast, or hull)."
            );

            SailColorShipKey = Config.Bind(
                "Controls",
                "ChangeSailColor",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftAlt),
                "Cycle the sail color of the ship you are looking at."
            );

            SailColorPlacingKey = Config.Bind(
                "Controls",
                "ChangeSailColorWhilePlacing",
                new KeyboardShortcut(KeyCode.E),
                "Cycle the sail color of a ship while placing it with the hammer."
            );

            // "Use" is cancelled by NamingPatches.Prefix_PlayerInteract over ship parts and has
            // nothing to interact with while placing. TabLeft/TabRight only act inside tabbed
            // menus, where Player.TakeInput() is false and none of our shortcuts run.
            Keybinds.Init(Config, "Use", "TabLeft", "TabRight");
            Keybinds.Add(RenameShipKey);
            Keybinds.Add(SailColorShipKey);

            // Only active while placing a ship, so it can't collide with the other two.
            Keybinds.Add(SailColorPlacingKey, "Placing");


            SailTextures.Refresh();

            Log.Info($"{ModName} {ModVersion} is loading...");
            try
            {
                harmony.PatchAll();
            }
            catch (System.Exception e)
            {
                Log.Error($"Failed to apply some patches: {e}");
            }
            Log.Info($"{ModName} loaded!");
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
