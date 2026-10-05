using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using malafein.Valheim.Shared;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Who may do what on a server running this mod. A role is a named set of permissions; a player
    // holds a role when their platform ID is in the role's list file. Admins (the server's
    // adminlist.txt) hold every permission, so moderators get the moderation part of being an admin
    // without the console, kick and ban powers that come with adminlist.txt.
    //
    // Kept free of anything sail-specific: it's the first piece of a shared network library, to be
    // extracted when a second mod needs it. There, each mod defines its own permissions and roles.
    //
    // Server: PeerHas checks the list files against the peer's connection, never anything the client
    // claims about itself. Client: Has answers from what the server last granted this player
    // (Write/Read travel inside the mod's handshake), and is true for everything when this game is
    // the server.
    public static class ServerRoles
    {
        public const string Moderate = "moderate";
        public const string ChangeSettings = "settings";

        private static readonly string[] s_allPermissions = { Moderate, ChangeSettings };

        private sealed class Role
        {
            public string Name;
            public string FileName;
            public string FileComment;
            public string[] Permissions;
            public SyncedList List;
        }

        private static readonly Role[] s_roles =
        {
            new Role
            {
                Name = "moderator",
                FileName = "moderators.txt",
                FileComment = $"{Plugin.ModName} moderators: may approve, deny and remove shared sail textures. List player IDs ONE per line, as in adminlist.txt",
                Permissions = new[] { Moderate },
            },
        };

        public static string RolesFolder => Path.Combine(Paths.ConfigPath, "ShipwrightsTouch");

        // ZNet's own ID matching for adminlist.txt (with or without the platform prefix). Private,
        // so reached by reflection; a plain Contains is the fallback.
        private static readonly MethodInfo s_listContainsId = AccessTools.Method(typeof(ZNet), "ListContainsId");

        private static HashSet<string> s_granted = new HashSet<string>(s_allPermissions);

        public static void StartSession(ZNet net)
        {
            s_granted = net.IsServer() ? new HashSet<string>(s_allPermissions) : new HashSet<string>();
            if (s_listContainsId == null) Log.Warn("Could not find ZNet.ListContainsId; role files must use the full player ID.");
        }

        // ── Client ───────────────────────────────────────────────────────

        public static bool Has(string permission) => s_granted.Contains(permission);

        public static void Read(ZPackage package)
        {
            int count = package.ReadInt();
            var granted = new HashSet<string>();
            for (int i = 0; i < count; i++) granted.Add(package.ReadString());
            s_granted = granted;
        }

        // ── Server ───────────────────────────────────────────────────────

        public static void Write(ZPackage package, long peer)
        {
            List<string> granted = s_allPermissions.Where(p => PeerHas(peer, p)).ToList();
            package.Write(granted.Count);
            foreach (string permission in granted) package.Write(permission);
        }

        // Server only. The list files are re-read when they change (SyncedList checks every 10 s),
        // so this is current without a restart.
        public static bool PeerHas(long peer, string permission)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer()) return false;
            if (peer == ZNet.GetUID()) return true;

            string id = PeerId(peer);
            if (id == "") return false;
            if (net.IsAdmin(id)) return true;

            foreach (Role role in s_roles)
            {
                if (Array.IndexOf(role.Permissions, permission) >= 0 && InList(net, role, id)) return true;
            }
            return false;
        }

        private static bool InList(ZNet net, Role role, string id)
        {
            if (role.List == null)
            {
                // Created on first use, so only servers that check a role get its file.
                Directory.CreateDirectory(RolesFolder);
                string path = Path.Combine(RolesFolder, role.FileName);
                role.List = new SyncedList(new FileHelpers.FileLocation(FileHelpers.FileSource.Local, path), role.FileComment);
                Log.Info($"Loaded {role.List.Count()} {role.Name}(s) from {path}.");
            }

            if (s_listContainsId != null) return (bool)s_listContainsId.Invoke(net, new object[] { role.List, id });
            return role.List.Contains(id);
        }

        // Server only. The peer's platform ID as its connection reports it (what adminlist.txt
        // holds); empty if unknown.
        public static string PeerId(long peer)
        {
            return ZNet.instance?.GetPeer(peer)?.m_socket?.GetHostName() ?? "";
        }

        public static string PeerName(long peer)
        {
            ZNetPeer znetPeer = ZNet.instance?.GetPeer(peer);
            return znetPeer != null ? $"{znetPeer.m_playerName} ({znetPeer.m_socket?.GetHostName()})" : peer.ToString();
        }
    }
}
