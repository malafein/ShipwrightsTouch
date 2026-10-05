using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using malafein.Valheim.Shared;

namespace malafein.Valheim.ShipwrightsTouch
{
    public enum ServerMode
    {
        // Singleplayer, or this game is the server (dedicated or hosting): the local config rules.
        Local,

        // Connected to a server without this mod, or with an incompatible version of it, or the
        // handshake hasn't been answered yet. Custom sail textures stay visible only to their owner.
        Vanilla,

        // Connected to a server running a compatible version of this mod; its policy applies.
        Modded
    }

    // The mod's own channel between client and server, over Valheim's routed RPCs. A vanilla peer
    // has no handler for these names and drops them silently (ZRoutedRpc.HandleRoutedRPC), so they
    // are safe to send to anyone.
    //
    // Handshake: once the connection to the server is up, the client sends Hello with its protocol
    // version. A modded server answers with Policy (its protocol version, this player's permissions
    // (ServerRoles), and the texture policy) and resends it whenever its config changes. No answer means a
    // vanilla server. The first value of every Policy message is the protocol version, in every
    // version, so a mismatch can always be detected.
    //
    // Live editing: while connected, the client's [Server] settings mirror the server's (so
    // Configuration Manager shows what's in effect) and are read-only unless the server grants this
    // player ChangeSettings (admins). An admin's edit is sent as SetPolicy; the server checks its admin list
    // against the sending connection, applies and saves it, and the change goes out to everyone as
    // a normal Policy. A refused edit gets the server's values sent back. The client's own values
    // are put back when the session ends, since they only matter when this game hosts.
    public static class SailNetwork
    {
        // Client and server must match exactly. 1 is the first released protocol; bump it only when
        // a message changes after a release (unreleased builds are always deployed together).
        public const int ProtocolVersion = 1;

        private const string HelloRpc = "ShipwrightsTouch_Hello";
        private const string PolicyRpc = "ShipwrightsTouch_Policy";
        private const string SetPolicyRpc = "ShipwrightsTouch_SetPolicy";
        private const string RefreshRpc = "ShipwrightsTouch_Refresh";
        internal const string CatalogRpc = "ShipwrightsTouch_Catalog";
        internal const string GetThumbnailsRpc = "ShipwrightsTouch_GetThumbnails";
        internal const string ThumbnailsRpc = "ShipwrightsTouch_Thumbnails";
        internal const string GetTextureRpc = "ShipwrightsTouch_GetTexture";
        internal const string TextureChunkRpc = "ShipwrightsTouch_TextureChunk";
        internal const string OfferUploadRpc = "ShipwrightsTouch_OfferUpload";
        internal const string UploadChunkRpc = "ShipwrightsTouch_UploadChunk";
        internal const string UploadAnswerRpc = "ShipwrightsTouch_UploadAnswer";
        internal const string GetModerationRpc = "ShipwrightsTouch_GetModeration";
        internal const string ModerationRpc = "ShipwrightsTouch_Moderation";
        internal const string ModerateRpc = "ShipwrightsTouch_Moderate";

        // Server: peers that completed the handshake with a matching protocol.
        private static readonly HashSet<long> s_moddedPeers = new HashSet<long>();

        // Client: this game's own [Server] values, saved before the server's are mirrored in.
        private static SailPolicy s_localValues;

        // Set while this code writes the [Server] entries itself, so those writes aren't treated
        // as the player editing them.
        private static bool s_writingConfig;

        public static ServerMode Mode { get; private set; } = ServerMode.Local;

        // The policy in effect for this game. Vanilla mode has no server policy: local only.
        public static SailPolicy Policy { get; private set; } = SailPolicy.Unrestricted;

        // Whether the server lets this player change its settings (always true when this game is the
        // server). Moderation is checked with ServerRoles.Has(ServerRoles.Moderate).
        public static bool CanChangeSettings => ServerRoles.Has(ServerRoles.ChangeSettings);

        // Raised on the main thread whenever Mode, Policy or this player's permissions may have changed.
        public static event Action Changed;

        public static void Init(ConfigFile config)
        {
            config.SettingChanged += SettingChanged;
        }

        private static void StartSession(ZNet net)
        {
            RestoreLocalValues();
            s_moddedPeers.Clear();
            ServerRoles.StartSession(net);

            ZRoutedRpc rpc = ZRoutedRpc.instance;
            rpc.Register<int>(HelloRpc, RPC_Hello);
            rpc.Register<ZPackage>(PolicyRpc, RPC_Policy);
            rpc.Register<ZPackage>(SetPolicyRpc, RPC_SetPolicy);
            rpc.Register(RefreshRpc, RPC_Refresh);
            rpc.Register<ZPackage>(CatalogRpc, SailDownloads.RPC_Catalog);
            rpc.Register<ZPackage>(GetThumbnailsRpc, SailServer.RPC_GetThumbnails);
            rpc.Register<ZPackage>(ThumbnailsRpc, SailDownloads.RPC_Thumbnails);
            rpc.Register<string>(GetTextureRpc, SailServer.RPC_GetTexture);
            rpc.Register<ZPackage>(TextureChunkRpc, SailDownloads.RPC_TextureChunk);
            rpc.Register<ZPackage>(OfferUploadRpc, SailServer.RPC_OfferUpload);
            rpc.Register<ZPackage>(UploadChunkRpc, SailServer.RPC_UploadChunk);
            rpc.Register<ZPackage>(UploadAnswerRpc, SailUploads.RPC_UploadAnswer);
            rpc.Register(GetModerationRpc, SailServer.RPC_GetModeration);
            rpc.Register<ZPackage>(ModerationRpc, SailModeration.RPC_Moderation);
            rpc.Register<ZPackage>(ModerateRpc, SailServer.RPC_Moderate);
            SailDownloads.StartSession();
            SailUploads.StartSession();
            SailModeration.StartSession();

            if (net.IsServer())
            {
                Mode = ServerMode.Local;
                Policy = ZNet.IsSinglePlayer ? SailPolicy.Unrestricted : SailPolicy.FromConfig();
                SailServer.StartSession(net);
            }
            else
            {
                // Until the server answers, assume it doesn't have the mod.
                Mode = ServerMode.Vanilla;
                Policy = SailPolicy.Unrestricted;
                rpc.m_onNewPeer = (Action<long>)Delegate.Combine(rpc.m_onNewPeer, new Action<long>(SendHello));
            }
            SailPolicy.SetReadOnly(false);
            Changed?.Invoke();
        }

        private static void SettingChanged(object sender, SettingChangedEventArgs args)
        {
            if (s_writingConfig || Array.IndexOf(SailPolicy.Entries, args.ChangedSetting) < 0) return;

            ZNet net = ZNet.instance;
            if (net == null) return;

            if (net.IsServer())
                SendPolicyToAll();
            else if (Mode == ServerMode.Modded)
                RequestPolicyChange();
        }

        // ── Client ───────────────────────────────────────────────────────

        // A client's only peer is the server.
        private static void SendHello(long serverPeer)
        {
            Log.Debug($"Sending handshake to the server (protocol {ProtocolVersion}).");
            ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer, HelloRpc, ProtocolVersion);
        }

        private static void RPC_Policy(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            if (net == null || net.IsServer() || sender != net.GetServerPeer()?.m_uid) return;

            int serverProtocol = package.ReadInt();
            if (serverProtocol != ProtocolVersion)
            {
                Log.Warn($"The server runs a different version of {Plugin.ModName} (protocol {serverProtocol}, this game {ProtocolVersion}). Custom sail textures stay visible only to you until both match.");
                return;
            }

            bool firstAnswer = Mode != ServerMode.Modded;
            ServerRoles.Read(package);
            Policy = SailPolicy.Read(package);
            Mode = ServerMode.Modded;

            MirrorServerValues();

            if (firstAnswer) Log.Info($"The server runs {Plugin.ModName}; its sail texture settings apply.");
            Log.Debug($"Server sail policy: {Policy} settings={CanChangeSettings} moderate={ServerRoles.Has(ServerRoles.Moderate)}");
            Changed?.Invoke();
        }

        // Asks a modded server for the current policy and catalog, so a panel opens on fresh
        // values even if something changed without a broadcast (an adminlist.txt edit).
        public static void RequestRefresh()
        {
            if (Mode != ServerMode.Modded) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, RefreshRpc);
        }

        // The player edited a [Server] setting while connected to a modded server.
        private static void RequestPolicyChange()
        {
            if (!CanChangeSettings)
            {
                // Configuration Manager shows them read-only, but the file or another tool can still
                // change them. The server would refuse anyway; snap back without asking it.
                MirrorServerValues();
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Only server admins can change the server's sail settings.");
                return;
            }

            var package = new ZPackage();
            SailPolicy.FromConfig().Write(package);
            Log.Debug("Sending a server sail setting change to the server.");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, SetPolicyRpc, package);
        }

        private static void MirrorServerValues()
        {
            if (s_localValues == null) s_localValues = SailPolicy.FromConfig();
            WriteConfig(Policy);
            SailPolicy.SetReadOnly(!CanChangeSettings);
        }

        private static void RestoreLocalValues()
        {
            if (s_localValues == null) return;
            WriteConfig(s_localValues);
            s_localValues = null;
            SailPolicy.SetReadOnly(false);
        }

        private static void WriteConfig(SailPolicy policy)
        {
            s_writingConfig = true;
            try
            {
                policy.ApplyToConfig();
            }
            finally
            {
                s_writingConfig = false;
            }
        }

        // ── Server ───────────────────────────────────────────────────────

        private static void RPC_Hello(long sender, int clientProtocol)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer()) return;

            // Answer even on a mismatch, so the client can tell the player why sharing is off.
            if (clientProtocol == ProtocolVersion)
                s_moddedPeers.Add(sender);
            else
                Log.Debug($"Peer {sender} runs protocol {clientProtocol}, this server {ProtocolVersion}; not sharing textures with it.");

            SendPolicy(sender);
            if (clientProtocol == ProtocolVersion) SailServer.SendCatalog(sender);
        }

        public static bool IsModdedPeer(long peer) => s_moddedPeers.Contains(peer);

        private static void RPC_Refresh(long sender)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || !s_moddedPeers.Contains(sender)) return;
            SendPolicy(sender);
            SailServer.SendCatalog(sender);
        }

        public static IEnumerable<long> ModdedPeers => s_moddedPeers;

        private static void RPC_SetPolicy(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || !s_moddedPeers.Contains(sender)) return;

            if (!ServerRoles.PeerHas(sender, ServerRoles.ChangeSettings))
            {
                Log.Info($"Refused a server sail setting change from {ServerRoles.PeerName(sender)}: not an admin.");
                SendPolicy(sender);
                return;
            }

            SailPolicy requested = SailPolicy.Read(package);
            requested.MaxTexturesPerPlayer = Math.Max(1, requested.MaxTexturesPerPlayer);
            Log.Info($"{ServerRoles.PeerName(sender)} changed the server sail settings.");
            Log.Debug($"New server sail policy: {requested}");

            // Saves the config file; range limits clamp out-of-range values. Then one broadcast
            // for all six values, the sender included.
            WriteConfig(requested);
            SendPolicyToAll();
        }

        private static void SendPolicyToAll()
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || ZNet.IsSinglePlayer) return;

            Policy = SailPolicy.FromConfig();
            foreach (long peer in s_moddedPeers) SendPolicy(peer);

            // What each peer may see depends on the policy.
            SailServer.SendCatalogToAll();
            Changed?.Invoke();
        }

        private static void SendPolicy(long peer)
        {
            var package = new ZPackage();
            package.Write(ProtocolVersion);
            ServerRoles.Write(package, peer);
            SailPolicy.FromConfig().Write(package);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, PolicyRpc, package);
        }

        [HarmonyPatch]
        private static class Patches
        {
            // ZNet.Awake creates the routed RPC instance; it runs once per session (world or
            // server connection), so per-session state is reset here.
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix_ZNetAwake(ZNet __instance)
            {
                StartSession(__instance);
            }

            // Leaving a server (or quitting) puts this game's own [Server] values back.
            [HarmonyPatch(typeof(ZNet), "OnDestroy")]
            [HarmonyPostfix]
            private static void Postfix_ZNetOnDestroy()
            {
                RestoreLocalValues();
            }

            [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RemovePeer))]
            [HarmonyPostfix]
            private static void Postfix_RemovePeer(ZNetPeer peer)
            {
                if (peer == null) return;
                s_moddedPeers.Remove(peer.m_uid);
                SailServer.PeerLeft(peer.m_uid);
            }
        }
    }
}
