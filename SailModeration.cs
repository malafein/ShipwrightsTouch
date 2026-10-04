using System;
using System.Collections.Generic;
using malafein.Valheim.Shared;

namespace malafein.Valheim.ShipwrightsTouch
{
    public enum ModerationAction : byte
    {
        Approve,

        // Hidden from everyone but its uploader, and remembered so it can't be shared again. The
        // file stays on the server, so a denial can be undone.
        Deny,

        // Forgotten: file and record deleted, the uploader's slot freed, and it may be shared again.
        Remove
    }

    // One player texture as moderators see it. Only sent to players with the Moderate permission;
    // the uploader and everyone else see just the status, never who decided.
    public class ModerationEntry
    {
        public string Hash;
        public string Name;
        public TextureStatus Status;
        public string UploaderName = "";
        public string Date = "";
        public string DecidedBy = "";
        public string DecidedDate = "";
        public int ShipsUsing;
        public int Width;
        public int Height;
        public int Bytes;

        // Whether the server still has the image (a denial loaded without its file has none).
        public bool HasImage;

        public void Write(ZPackage package)
        {
            package.Write(Hash);
            package.Write(Name);
            package.Write((byte)Status);
            package.Write(UploaderName);
            package.Write(Date);
            package.Write(DecidedBy);
            package.Write(DecidedDate);
            package.Write(ShipsUsing);
            package.Write(Width);
            package.Write(Height);
            package.Write(Bytes);
            package.Write(HasImage);
        }

        public static ModerationEntry Read(ZPackage package)
        {
            return new ModerationEntry
            {
                Hash = package.ReadString(),
                Name = package.ReadString(),
                Status = (TextureStatus)package.ReadByte(),
                UploaderName = package.ReadString(),
                Date = package.ReadString(),
                DecidedBy = package.ReadString(),
                DecidedDate = package.ReadString(),
                ShipsUsing = package.ReadInt(),
                Width = package.ReadInt(),
                Height = package.ReadInt(),
                Bytes = package.ReadInt(),
                HasImage = package.ReadBool()
            };
        }
    }

    // Client side of moderation: asks the server for the list of player textures, sends
    // decisions, and keeps the last list for the moderation panel. The server checks the Moderate
    // permission on every message (SailServer.RPC_GetModeration / RPC_Moderate). A hosting player
    // goes through the same messages; routed RPCs to this game's own ID are handled locally.
    public static class SailModeration
    {
        private static readonly Dictionary<string, ModerationEntry> s_entries = new Dictionary<string, ModerationEntry>();

        public static IReadOnlyCollection<ModerationEntry> Entries => s_entries.Values;

        // Raised whenever a new list arrives.
        public static event Action Changed;

        public static bool CanModerate => SailNetwork.Mode != ServerMode.Vanilla && !ZNet.IsSinglePlayer && ServerRoles.Has(ServerRoles.Moderate);

        public static void StartSession()
        {
            s_entries.Clear();
            Changed?.Invoke();
        }

        public static ModerationEntry Find(string hash)
        {
            return hash != null && s_entries.TryGetValue(hash, out ModerationEntry entry) ? entry : null;
        }

        public static void RequestList()
        {
            if (!CanModerate || ServerPeer() == 0L) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), SailNetwork.GetModerationRpc);
        }

        public static void Send(string hash, ModerationAction action)
        {
            if (!CanModerate || ServerPeer() == 0L) return;
            var package = new ZPackage();
            package.Write(hash);
            package.Write((byte)action);
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), SailNetwork.ModerateRpc, package);
        }

        // 0 when there's no server connection (no peer has that ID).
        private static long ServerPeer()
        {
            ZNet net = ZNet.instance;
            if (net == null) return 0L;
            return net.IsServer() ? ZNet.GetUID() : net.GetServerPeer()?.m_uid ?? 0L;
        }

        public static void RPC_Moderation(long sender, ZPackage package)
        {
            if (sender == 0L || sender != ServerPeer()) return;

            s_entries.Clear();
            int count = package.ReadInt();
            for (int i = 0; i < count; i++)
            {
                ModerationEntry entry = ModerationEntry.Read(package);
                if (SailDownloads.IsHash(entry.Hash)) s_entries[entry.Hash] = entry;
            }
            Log.Debug($"Moderation list: {s_entries.Count} player texture(s).");
            Changed?.Invoke();
        }
    }
}
