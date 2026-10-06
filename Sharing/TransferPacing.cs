using System.Collections.Generic;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Optional per-connection speed limit for texture transfers (MaxTransferKBPerSecond), on top
    // of the send-queue check in the pumps. The game itself limits each Steam connection to
    // 150 KB/s for everything it sends, so this can only slow transfers down, leaving more of
    // that room to world updates. Uploads use it toward the server, the server toward each player.
    internal static class TransferPacing
    {
        private static readonly Dictionary<long, float> s_nextSend = new Dictionary<long, float>();

        public static bool CanSend(long peer)
        {
            return !s_nextSend.TryGetValue(peer, out float next) || Time.time >= next;
        }

        public static void Sent(long peer, int bytes)
        {
            int limit = Plugin.MaxTransferKBPerSecond.Value;
            if (limit <= 0)
            {
                s_nextSend.Remove(peer);
                return;
            }
            s_nextSend[peer] = Time.time + bytes / (limit * 1024f);
        }

        public static void Forget(long peer) => s_nextSend.Remove(peer);
    }
}
