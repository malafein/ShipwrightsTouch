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
        public const string ModVersion = "1.3.0";
        
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
        public static ConfigEntry<KeyboardShortcut> ModerationKey;
        public static ConfigEntry<float> ScrollSensitivity;
        public static ConfigEntry<bool> CompressTextures;
        public static ConfigEntry<bool> ShareTextures;
        public static ConfigEntry<bool> ShowOtherPlayersTextures;
        public static ConfigEntry<int> MaxTransferKBPerSecond;

        private readonly Harmony harmony = new Harmony(ModGUID);

        private void Awake()
        {
            Log.Init(Logger);

            AllowShipDeconstruction = Config.Bind("General", "AllowShipDeconstruction", false, "Allow deconstructing ships with the hammer (middle-mouse button).");
            AssignBuilderIdentity = Config.Bind("General", "AssignBuilderIdentity", true, "Automatically assign your character as the owner when constructing a ship, restricting modifications (renaming, recoloring, deconstruction) to yourself.");

            ScrollSensitivity = Config.Bind("General", "ScrollSensitivity", UIBuilder.DefaultScrollSensitivity, "Mouse-wheel scroll speed in the customization and moderation panels' lists. Higher scrolls faster. Applied each time a panel opens.");
            CompressTextures = Config.Bind("General", "CompressTextures", true, "Compress custom sail textures in video memory, like the game's own textures: about a quarter of the memory each, with slight artifacts on hard edges and smooth gradients. Turn off for exact images if you have memory to spare. Applies to textures loaded after the change (rejoin the world to reload them).");
            ShareTextures = Config.Bind("Sharing", "ShareMyTextures", true, "On a server with this mod, share a texture from your sails folder with the server the first time you put it on a ship, so other players can see it (if the server allows it, possibly after a moderator's approval). When off, your textures show only for you.");
            ShowOtherPlayersTextures = Config.Bind("Sharing", "ShowOtherPlayersTextures", true, "Show and download the sail textures other players have shared on the server. When off, their ships show the vanilla sail to you. The server's own textures still show.");
            MaxTransferKBPerSecond = Config.Bind("Sharing", "MaxTransferKBPerSecond", 0, new ConfigDescription("Speed limit for sending sail textures, in KB per second: on a player's game for sharing a texture, on the server for sending textures to each player. 0 means no limit, as fast as the connection allows. The game already limits each connection to 150 KB/s, shared with world updates, so this can only slow transfers down, leaving more room for the world while a texture is sent. Choices: 0, 8, 16, 32, 64, 128; any other value counts as 0.", new AcceptableValueList<int>(0, 8, 16, 32, 64, 128)));

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

            ModerationKey = Config.Bind(
                ControlsSection,
                "OpenModeration",
                KeyboardShortcut.Empty,
                "Open the sail texture moderation panel (server moderators and admins only). Unbound by default; the customization panel has a button for it."
            );

            // "Use" is cancelled by NamingPatches.Prefix_PlayerInteract over ship parts and has
            // nothing to interact with while placing. TabLeft/TabRight only act inside tabbed
            // menus, where Player.TakeInput() is false and none of our shortcuts run.
            Keybinds.Init(Config, "Use", "TabLeft", "TabRight");
            Keybinds.Add(CustomizeShipKey);
            Keybinds.Add(SailColorShipKey);
            Keybinds.Add(ModerationKey);

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

        // Text another player chose (player and ship names), made safe to show in rich text: a
        // name like "Bjorn <color=green>approved" must not restyle what follows it.
        public static string PlainText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("<", "").Replace(">", "");
        }

        public static bool CanModifyShip(Ship ship, out string ownerName)
        {
            ownerName = string.Empty;
            if (ship == null) return true;

            ZNetView nview = ship.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return true;

            long ownerId = nview.GetZDO().GetLong(ZdoOwnerIdKey, 0L);
            ownerName = PlainText(nview.GetZDO().GetString(ZdoOwnerNameKey));

            return ownerId == 0L || ownerId == Player.m_localPlayer?.GetPlayerID();
        }
    }
}
