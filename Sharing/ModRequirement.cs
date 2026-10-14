using System.Collections.Generic;
using System.Linq;
#if DEBUG
using BepInEx.Configuration;
#endif
using HarmonyLib;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // [Server] RequireMatchingMod: the server turns away players without a version of this mod it can
    // talk to (ServerSync's pattern). The routed Hello in SailNetwork comes too late for that: it is
    // sent once the player has joined. So the client also sends its protocol over the raw connection
    // the moment it connects, which arrives before its PeerInfo (the vanilla join request); the server
    // refuses a PeerInfo from a connection that didn't send a matching one, the way vanilla refuses a
    // different game version ("Error" with ErrorVersion; the client then disconnects itself).
    //
    // Turned on while players are connected: those who never passed get a countdown through the
    // vanilla ShowMessage RPC (vanilla clients have it too), a profile save request, then the same
    // error. A client that loses its connection logs out with a save (Game.FixedUpdate), so their
    // character keeps what they had.
    public static class ModRequirement
    {
        private const string VersionRpc = "ShipwrightsTouch_Version";
        private const string RequiredRpc = "ShipwrightsTouch_Required";
        private const int CountdownSeconds = 10;

        // After the error, the server closes the connection itself if the client hasn't by then.
        private const float DisconnectDelay = 2f;

        private class Countdown
        {
            public float End;
            public int LastShown = -1;
            public bool ErrorSent;
        }

        // Server: connections whose client sent a matching protocol.
        private static readonly HashSet<ZRpc> s_validated = new HashSet<ZRpc>();

        // Server: players being warned before they're disconnected.
        private static readonly Dictionary<ZNetPeer, Countdown> s_countdowns = new Dictionary<ZNetPeer, Countdown>();

        // Client: the protocol the server said it requires when it turned this game away, 0 if it didn't.
        private static int s_serverRequired;

#if DEBUG
        // Test aid: this game doesn't send its version, so a server requiring the mod treats it as a
        // game without it (refused, or counted down and disconnected), while everything else keeps
        // working, admin settings included. Debug builds only; applies from the next connection.
        private static ConfigEntry<bool> s_debugPretendNoMod;

        public static void BindDebug(ConfigFile config)
        {
            s_debugPretendNoMod = config.Bind("Debug", "PretendNoMod", false,
                "Debug builds only: don't send this game's version when connecting, so a server that requires the mod treats it as a game without it.");
        }
#endif

        private static bool Required
        {
            get
            {
                ZNet net = ZNet.instance;
                return net != null && net.IsServer() && !ZNet.IsSinglePlayer && SailPolicy.RequireMatchingModConfig.Value;
            }
        }

        // Server: called whenever the policy may have changed, to warn connected players that don't
        // pass, or to call off the warnings if the requirement was turned off.
        public static void PolicyChanged()
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer()) return;

            if (!Required)
            {
                if (s_countdowns.Count == 0) return;
                Log.Info($"{nameof(SailPolicy.RequireMatchingMod)} is off again: nobody is disconnected.");
                foreach (ZNetPeer peer in s_countdowns.Keys.Where(p => !s_countdowns[p].ErrorSent).ToList())
                    s_countdowns.Remove(peer);
                return;
            }

            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer.m_uid == 0 || peer.m_rpc == null) continue;
                if (s_validated.Contains(peer.m_rpc) || s_countdowns.ContainsKey(peer)) continue;
                Log.Info($"{PeerName(peer)} has no matching {Plugin.ModName}, which this server now requires: disconnecting them in {CountdownSeconds} s.");
                s_countdowns[peer] = new Countdown { End = Time.time + CountdownSeconds };
            }
        }

        private static string PeerName(ZNetPeer peer)
        {
            string host = peer.m_socket != null ? peer.m_socket.GetHostName() : "?";
            return string.IsNullOrEmpty(peer.m_playerName) ? host : $"{peer.m_playerName} ({host})";
        }

        private static void UpdateCountdowns()
        {
            if (s_countdowns.Count == 0) return;

            foreach (KeyValuePair<ZNetPeer, Countdown> entry in s_countdowns.ToList())
            {
                ZNetPeer peer = entry.Key;
                Countdown countdown = entry.Value;
                float remaining = countdown.End - Time.time;

                if (!countdown.ErrorSent && remaining > 0f)
                {
                    int seconds = Mathf.CeilToInt(remaining);
                    if (seconds == countdown.LastShown) continue;
                    countdown.LastShown = seconds;
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)MessageHud.MessageType.Center,
                        $"This server now requires {Plugin.ModName}.\nDisconnecting in {seconds}...");
                    continue;
                }

                if (!countdown.ErrorSent)
                {
                    Log.Info($"Disconnecting {PeerName(peer)}: no matching {Plugin.ModName}.");
                    peer.m_rpc.Invoke("SavePlayerProfile");
                    TurnAway(peer.m_rpc);
                    countdown.ErrorSent = true;
                    countdown.End = Time.time + DisconnectDelay;
                    continue;
                }

                if (remaining > 0f) continue;
                s_countdowns.Remove(peer);
                ZNet.instance.Disconnect(peer);
            }
        }

        private static void TurnAway(ZRpc rpc)
        {
            rpc.Invoke(RequiredRpc, SailNetwork.ProtocolVersion);
            rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
        }

        private static void RPC_Version(ZRpc rpc, int clientProtocol)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            if (clientProtocol == SailNetwork.ProtocolVersion)
                s_validated.Add(rpc);
            else
                Log.Debug($"A connecting client runs protocol {clientProtocol}, this server {SailNetwork.ProtocolVersion}.");
        }

        private static void RPC_Required(ZRpc rpc, int serverProtocol)
        {
            ZNet net = ZNet.instance;
            if (net == null || net.IsServer()) return;
            s_serverRequired = serverProtocol;
            Log.Warn($"The server requires a version of {Plugin.ModName} matching its own (protocol {serverProtocol}, this game {SailNetwork.ProtocolVersion}).");
        }

        [HarmonyPatch]
        private static class Patches
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix_ZNetAwake()
            {
                s_validated.Clear();
                s_countdowns.Clear();
            }

            // Prefix, so the client's protocol goes out ahead of anything vanilla sends on this
            // connection. Unknown names are ignored by a vanilla peer (ZRpc.HandlePackage).
            [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
            [HarmonyPrefix]
            private static void Prefix_OnNewConnection(ZNet __instance, ZNetPeer peer)
            {
                peer.m_rpc.Register<int>(VersionRpc, RPC_Version);
                peer.m_rpc.Register<int>(RequiredRpc, RPC_Required);
                if (__instance.IsServer()) return;

                s_serverRequired = 0;
#if DEBUG
                if (s_debugPretendNoMod.Value)
                {
                    Log.Warn("Debug: not sending this game's version to the server (PretendNoMod).");
                    return;
                }
#endif
                peer.m_rpc.Invoke(VersionRpc, SailNetwork.ProtocolVersion);
            }

            [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
            [HarmonyPrefix]
            private static bool Prefix_RPC_PeerInfo(ZNet __instance, ZRpc rpc)
            {
                if (!__instance.IsServer() || !Required || s_validated.Contains(rpc)) return true;

                string host = rpc.GetSocket() != null ? rpc.GetSocket().GetHostName() : "?";
                Log.Info($"Turned away {host}: no matching {Plugin.ModName}, which this server requires.");
                TurnAway(rpc);
                return false;
            }

            [HarmonyPatch(typeof(ZNet), "Update")]
            [HarmonyPostfix]
            private static void Postfix_ZNetUpdate(ZNet __instance)
            {
                if (__instance.IsServer()) UpdateCountdowns();
            }

            [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
            [HarmonyPrefix]
            private static void Prefix_Disconnect(ZNetPeer peer)
            {
                if (peer == null) return;
                s_countdowns.Remove(peer);
                if (peer.m_rpc != null) s_validated.Remove(peer.m_rpc);
            }

            // The connection-failed panel only says "incompatible version"; say which mod.
            [HarmonyPatch(typeof(FejdStartup), "ShowConnectError")]
            [HarmonyPostfix]
            private static void Postfix_ShowConnectError(FejdStartup __instance)
            {
                if (s_serverRequired == 0 || __instance.m_connectionFailedError == null) return;
                if (!__instance.m_connectionFailedPanel.activeSelf || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.ErrorVersion) return;

                string which = s_serverRequired > SailNetwork.ProtocolVersion ? "a newer version of "
                    : s_serverRequired < SailNetwork.ProtocolVersion ? "an older version of "
                    : "";
                __instance.m_connectionFailedError.text += $"\nThis server requires {which}{Plugin.ModName}.";
            }
        }
    }
}
