using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace malafein.Valheim.ShipwrightsTouch
{
    // What a server allows: custom sail textures, Safe Harbor (enemies ignoring unoccupied ships,
    // rescuing capsized ships) and whether players need this mod. The server's own config is the
    // source; clients receive a copy in the handshake (SailNetwork) and never use their local values
    // for this.
    public class SailPolicy
    {
        private const string Section = "Server";
        private const string SectionNote = " Used when this game is the server (a dedicated server, or the player hosting). While connected to a server with this mod, these show the server's values, and its admins can change them here.";

        public static ConfigEntry<bool> AllowCustomTexturesConfig;
        public static ConfigEntry<bool> AllowPlayerTexturesConfig;
        public static ConfigEntry<bool> RequireApprovalConfig;
        public static ConfigEntry<int> MaxFileKilobytesConfig;
        public static ConfigEntry<int> MaxDimensionConfig;
        public static ConfigEntry<int> MaxTexturesPerPlayerConfig;
        public static ConfigEntry<bool> AutoDenyUnexpectedContentConfig;
        public static ConfigEntry<bool> RequireMatchingModConfig;
        public static ConfigEntry<bool> EnemiesIgnoreUnoccupiedShipsConfig;
        public static ConfigEntry<bool> RescueCapsizedShipsConfig;

        public bool AllowCustomTextures;
        public bool AllowPlayerTextures;
        public bool RequireApproval;
        public int MaxFileKilobytes;
        public int MaxDimension;
        public int MaxTexturesPerPlayer;
        public bool AutoDenyUnexpectedContent;
        public bool RequireMatchingMod;
        public bool EnemiesIgnoreUnoccupiedShips;
        public bool RescueCapsizedShips;

        // The Safe Harbor settings run on players' games (whoever runs the monster or the ship), so
        // they only hold when every player has the mod.
        public bool NeedsMatchingMod => EnemiesIgnoreUnoccupiedShips || RescueCapsizedShips;

        // Singleplayer: nothing is shared, so nothing is restricted beyond the local folder's limits.
        // Safe Harbor comes from the local config there (SailNetwork.EnemiesIgnoreUnoccupiedShips).
        public static readonly SailPolicy Unrestricted = new SailPolicy
        {
            AllowCustomTextures = true,
            AllowPlayerTextures = true,
            RequireApproval = false,
            MaxFileKilobytes = SailTextures.MaxFileBytes / 1024,
            MaxDimension = SailTextures.MaxDimension,
            MaxTexturesPerPlayer = int.MaxValue,
            AutoDenyUnexpectedContent = false
        };

        // Each entry's tag for Configuration Manager (which reads any tag class with this name): an
        // Order, so the section lists them in the order they're bound here (sail textures, then the
        // mod requirement and Safe Harbor) instead of alphabetically, and ReadOnly, set on all of
        // them together.
        private static readonly List<ConfigurationManagerAttributes> s_attributes = new List<ConfigurationManagerAttributes>();

        public static ConfigEntryBase[] Entries { get; private set; }

        public static void Bind(ConfigFile config)
        {
            AllowCustomTexturesConfig = config.Bind(Section, "AllowCustomSailTextures", true,
                Describe("Allow custom sail textures on this server at all. When off, every ship shows the vanilla sail to everyone, including a player's own textures on their own screen."));
            AllowPlayerTexturesConfig = config.Bind(Section, "AllowPlayerSailTextures", true,
                Describe("Allow players to share their own sail textures with everyone. When off, nothing new is shared: players still see their own textures on their own ships, everyone else sees the server's textures, textures approved earlier, or the vanilla sail."));
            RequireApprovalConfig = config.Bind(Section, "RequireApproval", true,
                Describe("New player textures stay visible only to the player who shared them until a moderator or admin approves them. When off, they're shown to everyone right away; moderators can still deny one later."));
            MaxFileKilobytesConfig = config.Bind(Section, "MaxTextureFileKB", 2048,
                Describe("Largest texture a player may share, in KB, measured after it's converted to PNG for sharing (a JPG or a carefully compressed PNG can grow). This sets download time and the server's disk use; video memory depends only on MaxTextureSize.",
                    new AcceptableValueRange<int>(64, SailTextures.MaxFileBytes / 1024)));
            MaxDimensionConfig = config.Bind(Section, "MaxTextureSize", 1024,
                Describe("Largest width or height, in pixels, of a texture a player may share.",
                    new AcceptableValueRange<int>(64, SailTextures.MaxDimension)));
            MaxTexturesPerPlayerConfig = config.Bind(Section, "MaxTexturesPerPlayer", 10,
                Describe("How many textures each player may share (at least 1)."));
            AutoDenyUnexpectedContentConfig = config.Bind(Section, "AutoDenyUnexpectedContent", false,
                Describe("Automatically deny an upload that held more than image data (moderators see it flagged as \"unexpected content\" either way). The game never sends such files itself, so it means a modified game or a hand-made file. Players are only ever sent the server's own clean copy, so this is about keeping such uploads off ships, not about safety. The uploader sees it as auto-denied, and a moderator can still approve it."));
            RequireMatchingModConfig = config.Bind(Section, "RequireMatchingMod", false,
                Describe("Only let players join who have a version of Shipwright's Touch this server can talk to. Others are turned away with the game's \"incompatible version\" message; players already connected without it get a 10 second warning, then are disconnected (their character is saved). Turned on automatically by EnemiesIgnoreUnoccupiedShips and RescueCapsizedShips, and stays on if those are turned off again."));
            EnemiesIgnoreUnoccupiedShipsConfig = config.Bind(Section, "EnemiesIgnoreUnoccupiedShips", false,
                Describe("Safe Harbor: monsters don't go after a ship with nobody aboard. One already attacking keeps at it until you come back and deal with it, and a ship next to a fight can still be hit. Players' games run the monsters, so this turns RequireMatchingMod on."));
            RescueCapsizedShipsConfig = config.Bind(Section, "RescueCapsizedShips", false,
                Describe("Safe Harbor: a capsized ship still takes damage, but never below 10% of its health, and anyone can turn it upright by holding the interact key on it. Players' games run the ships, so this turns RequireMatchingMod on."));

            Entries = new ConfigEntryBase[]
            {
                AllowCustomTexturesConfig,
                AllowPlayerTexturesConfig,
                RequireApprovalConfig,
                MaxFileKilobytesConfig,
                MaxDimensionConfig,
                MaxTexturesPerPlayerConfig,
                AutoDenyUnexpectedContentConfig,
                RequireMatchingModConfig,
                EnemiesIgnoreUnoccupiedShipsConfig,
                RescueCapsizedShipsConfig
            };
        }

        private static ConfigDescription Describe(string text, AcceptableValueBase range = null)
        {
            // Higher Order shows first.
            var attributes = new ConfigurationManagerAttributes { Order = -s_attributes.Count };
            s_attributes.Add(attributes);
            return new ConfigDescription(text + SectionNote, range, attributes);
        }

        // Greys the settings out in Configuration Manager (it rereads tags when its window opens).
        public static void SetReadOnly(bool readOnly)
        {
            foreach (ConfigurationManagerAttributes attributes in s_attributes) attributes.ReadOnly = readOnly;
        }

        // Writes a policy into the local config entries (saving the config file).
        public void ApplyToConfig()
        {
            AllowCustomTexturesConfig.Value = AllowCustomTextures;
            AllowPlayerTexturesConfig.Value = AllowPlayerTextures;
            RequireApprovalConfig.Value = RequireApproval;
            MaxFileKilobytesConfig.Value = MaxFileKilobytes;
            MaxDimensionConfig.Value = MaxDimension;
            MaxTexturesPerPlayerConfig.Value = MaxTexturesPerPlayer;
            AutoDenyUnexpectedContentConfig.Value = AutoDenyUnexpectedContent;
            RequireMatchingModConfig.Value = RequireMatchingMod;
            EnemiesIgnoreUnoccupiedShipsConfig.Value = EnemiesIgnoreUnoccupiedShips;
            RescueCapsizedShipsConfig.Value = RescueCapsizedShips;
        }

        public static SailPolicy FromConfig()
        {
            return new SailPolicy
            {
                AllowCustomTextures = AllowCustomTexturesConfig.Value,
                AllowPlayerTextures = AllowPlayerTexturesConfig.Value,
                RequireApproval = RequireApprovalConfig.Value,
                MaxFileKilobytes = MaxFileKilobytesConfig.Value,
                MaxDimension = MaxDimensionConfig.Value,
                MaxTexturesPerPlayer = Math.Max(1, MaxTexturesPerPlayerConfig.Value),
                AutoDenyUnexpectedContent = AutoDenyUnexpectedContentConfig.Value,
                RequireMatchingMod = RequireMatchingModConfig.Value,
                EnemiesIgnoreUnoccupiedShips = EnemiesIgnoreUnoccupiedShipsConfig.Value,
                RescueCapsizedShips = RescueCapsizedShipsConfig.Value
            };
        }

        public void Write(ZPackage package)
        {
            package.Write(AllowCustomTextures);
            package.Write(AllowPlayerTextures);
            package.Write(RequireApproval);
            package.Write(MaxFileKilobytes);
            package.Write(MaxDimension);
            package.Write(MaxTexturesPerPlayer);
            package.Write(AutoDenyUnexpectedContent);
            package.Write(RequireMatchingMod);
            package.Write(EnemiesIgnoreUnoccupiedShips);
            package.Write(RescueCapsizedShips);
        }

        public static SailPolicy Read(ZPackage package)
        {
            return new SailPolicy
            {
                AllowCustomTextures = package.ReadBool(),
                AllowPlayerTextures = package.ReadBool(),
                RequireApproval = package.ReadBool(),
                MaxFileKilobytes = package.ReadInt(),
                MaxDimension = package.ReadInt(),
                MaxTexturesPerPlayer = package.ReadInt(),
                AutoDenyUnexpectedContent = package.ReadBool(),
                RequireMatchingMod = package.ReadBool(),
                EnemiesIgnoreUnoccupiedShips = package.ReadBool(),
                RescueCapsizedShips = package.ReadBool()
            };
        }

        public override string ToString()
        {
            return $"custom={AllowCustomTextures} player={AllowPlayerTextures} approval={RequireApproval} " +
                   $"maxKB={MaxFileKilobytes} maxSize={MaxDimension} perPlayer={MaxTexturesPerPlayer} autoDeny={AutoDenyUnexpectedContent} " +
                   $"requireMod={RequireMatchingMod} enemiesIgnore={EnemiesIgnoreUnoccupiedShips} rescueCapsized={RescueCapsizedShips}";
        }
    }

    // Read by Configuration Manager by class name; only the fields it knows are looked at.
    internal class ConfigurationManagerAttributes
    {
        public bool? ReadOnly;
        public int? Order;
    }
}
