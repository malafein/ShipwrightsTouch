using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using malafein.Valheim.Shared;
using malafein.Valheim.SharedUI;
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
        public const string ZdoColorKey = "shipwrightstouch.sail_color";

        private const string ControlsSection = "Controls";

        public static ConfigEntry<bool> AllowShipDeconstruction;
        public static ConfigEntry<bool> AssignBuilderIdentity;
        public static ConfigEntry<KeyboardShortcut> CustomizeShipKey;
        public static ConfigEntry<KeyboardShortcut> SailColorShipKey;
        public static ConfigEntry<KeyboardShortcut> SailColorPlacingKey;
        public static ConfigEntry<float> ScrollSensitivity;
        public static ConfigEntry<bool> ShareTextures;
        public static ConfigEntry<bool> ShowOtherPlayersTextures;

        private readonly Harmony harmony = new Harmony(ModGUID);

        private void Awake()
        {
            Log.Init(Logger);

            AllowShipDeconstruction = Config.Bind("General", "AllowShipDeconstruction", false, "Allow deconstructing ships with the hammer (middle-mouse button).");
            AssignBuilderIdentity = Config.Bind("General", "AssignBuilderIdentity", true, "Automatically assign your character as the owner when constructing a ship, restricting modifications (renaming, recoloring, deconstruction) to yourself.");

            ScrollSensitivity = Config.Bind("General", "ScrollSensitivity", UIBuilder.DefaultScrollSensitivity, "Mouse-wheel scroll speed in the customization panel's texture list. Higher scrolls faster. Applied each time the panel opens.");
            ShareTextures = Config.Bind("Sharing", "ShareMyTextures", true, "On a server with this mod, share a texture from your sails folder with the server the first time you put it on a ship, so other players can see it (if the server allows it, possibly after an admin's approval). When off, your textures show only for you.");
            ShowOtherPlayersTextures = Config.Bind("Sharing", "ShowOtherPlayersTextures", true, "Show and download the sail textures other players have shared on the server. When off, their ships show the vanilla sail to you. The server's own textures still show.");

            MigrateRenameKey();

            CustomizeShipKey = Config.Bind(
                ControlsSection,
                "CustomizeShip",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift),
                "Open the customization panel (name, sail color, sail texture) for the ship you are looking at (rudder, seats, mast, or hull)."
            );

            SailColorShipKey = Config.Bind(
                ControlsSection,
                "ChangeSailColor",
                KeyboardShortcut.Empty,
                "Quickly cycle the sail color of the ship you are looking at through the presets. Unbound by default; the customization panel covers it."
            );

            SailColorPlacingKey = Config.Bind(
                ControlsSection,
                "ChangeSailColorWhilePlacing",
                new KeyboardShortcut(KeyCode.E),
                "Cycle the sail color of a ship while placing it with the hammer."
            );

            // "Use" is cancelled by NamingPatches.Prefix_PlayerInteract over ship parts and has
            // nothing to interact with while placing. TabLeft/TabRight only act inside tabbed
            // menus, where Player.TakeInput() is false and none of our shortcuts run.
            Keybinds.Init(Config, "Use", "TabLeft", "TabRight");
            Keybinds.Add(CustomizeShipKey);
            Keybinds.Add(SailColorShipKey);

            // Only active while placing a ship, so it can't collide with the other two.
            Keybinds.Add(SailColorPlacingKey, "Placing");


            SailPolicy.Bind(Config);
            SailNetwork.Init(Config);
            SailTextures.Refresh();

            Log.Info($"{ModName} {ModVersion} is loading...");
            try
            {
                harmony.PatchAll();
            }
            catch (Exception e)
            {
                Log.Error($"Failed to apply some patches: {e}");
            }
            Log.Info($"{ModName} loaded!");
        }

        // Up to 1.1.1 the panel's shortcut was "RenameShip". BepInEx keeps settings it has no
        // binding for in a private orphan table and loads a key's value from there when it's
        // bound, so moving the saved value to the new key before binding carries it over. The
        // old key is dropped on the next save.
        private void MigrateRenameKey()
        {
            try
            {
                var orphans = (Dictionary<ConfigDefinition, string>)AccessTools
                    .Property(typeof(ConfigFile), "OrphanedEntries")
                    .GetValue(Config, null);

                var oldKey = new ConfigDefinition(ControlsSection, "RenameShip");
                var newKey = new ConfigDefinition(ControlsSection, "CustomizeShip");
                if (!orphans.TryGetValue(oldKey, out string value)) return;

                orphans.Remove(oldKey);
                if (!orphans.ContainsKey(newKey)) orphans[newKey] = value;
            }
            catch (Exception e)
            {
                Log.Warn($"Could not carry over the rename shortcut saved by an older version: {e.Message}");
            }
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
