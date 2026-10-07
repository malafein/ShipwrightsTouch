using System;
using System.Collections.Generic;
using System.Text;

namespace malafein.Valheim.ShipwrightsTouch
{
    public enum TextureSource : byte
    {
        // In the server's own sails folder: available to everyone, never moderated.
        Server,

        // Shared by a player.
        Player
    }

    public enum TextureStatus : byte
    {
        Approved,
        Pending,
        Denied
    }

    // The server's answer to an upload offer, and to the finished upload.
    public enum UploadAnswer : byte
    {
        // Go ahead and send the bytes.
        Send,

        // The server has it now (or already had it); its status follows.
        Shared,

        // Not taken; a reason for the player follows.
        Refused
    }

    // What the server noticed when it converted an upload (SailImages). Neither stops the upload:
    // the shared copy is the server's own conversion either way. Shown to moderators.
    [Flags]
    public enum UploadFlags : byte
    {
        None = 0,

        // The client's converted file didn't match the server's conversion. Usually a platform
        // difference in the encoder; harmless.
        ConvertedDifferently = 1,

        // The uploaded file held more than image data. A client that converts first never sends
        // that, so it came from a modified client or was made by hand.
        UnexpectedContent = 2
    }

    // One texture the server knows about. The catalog carries no image data: thumbnails and full
    // images are fetched by hash (SailDownloads).
    public class CatalogEntry
    {
        public const int MaxNameLength = 48;

        public const string AutoDeciderId = "auto";

        // The hash of the server's converted PNG: what's stored, sent and cached.
        public string Hash;

        // Other hashes that mean this texture: the original file a player put on a ship, the
        // uploader's own conversion, or a server sail's file. Ships keep whichever hash they were
        // given, so these are how a ship finds the shared copy.
        public List<string> Aliases = new List<string>();

        public string Name;
        public TextureSource Source;
        public TextureStatus Status;
        public string UploaderName = "";
        public int Width;
        public int Height;
        public int Bytes;

        // Client: shared by this player. Worked out per receiver when the server sends the list.
        public bool Mine;

        // Server only, never sent: the uploader's platform ID, the upload's file in the uploads
        // folder, the day it arrived, and who last approved or denied it and when (moderators see
        // those through ModerationEntry; the uploader never does).
        public string UploaderId = "";
        public string FileName = "";
        public string Date = "";
        public string DecidedById = "";
        public string DecidedByName = "";
        public string DecidedDate = "";
        public UploadFlags Flags;

        // Size of the file as the player sent it, before the server's conversion.
        public int UploadedBytes;

        // Denied by the server's AutoDenyUnexpectedContent setting rather than a moderator. The
        // uploader is told (the catalog sends it only to them), so an honest upload caught by
        // mistake can be taken to a moderator. On the server it's kept as DecidedById = "auto".
        public bool AutoDenied;

        // Denied ones too: their files stay on the server (so a denial can be undone), and
        // otherwise a player could fill its disk by having upload after upload denied. A
        // moderator's Remove frees the slot.
        public bool CountsTowardLimit => Source == TextureSource.Player;

        public void Write(ZPackage package, string viewerId)
        {
            package.Write(Hash);
            package.Write(Name);
            package.Write((byte)Source);
            package.Write((byte)Status);
            package.Write(UploaderName);
            package.Write(Width);
            package.Write(Height);
            package.Write(Bytes);
            bool mine = UploaderId != "" && UploaderId == viewerId;
            package.Write(mine);
            package.Write(mine && AutoDenied);
            package.Write(Aliases.Count);
            foreach (string alias in Aliases) package.Write(alias);
        }

        public static CatalogEntry Read(ZPackage package)
        {
            var entry = new CatalogEntry
            {
                Hash = package.ReadString(),
                Name = package.ReadString(),
                Source = (TextureSource)package.ReadByte(),
                Status = (TextureStatus)package.ReadByte(),
                UploaderName = Plugin.PlainText(package.ReadString()),
                Width = package.ReadInt(),
                Height = package.ReadInt(),
                Bytes = package.ReadInt(),
                Mine = package.ReadBool(),
                AutoDenied = package.ReadBool()
            };
            entry.Aliases = ReadAliases(package);
            return entry;
        }

        // Hashes become file names on the client, so anything malformed is dropped.
        public static List<string> ReadAliases(ZPackage package)
        {
            int count = package.ReadInt();
            var aliases = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string alias = package.ReadString();
                if (SailDownloads.IsHash(alias) && i < 64) aliases.Add(alias);
            }
            return aliases;
        }

        public static void WriteList(ZPackage package, IReadOnlyCollection<CatalogEntry> entries, string viewerId)
        {
            package.Write(entries.Count);
            foreach (CatalogEntry entry in entries) entry.Write(package, viewerId);
        }

        public static List<CatalogEntry> ReadList(ZPackage package)
        {
            int count = package.ReadInt();
            var entries = new List<CatalogEntry>(count);
            for (int i = 0; i < count; i++) entries.Add(Read(package));
            return entries;
        }

        // A display name that is also safe inside a file name: letters, digits, spaces and a few
        // marks, at most MaxNameLength characters. Never empty.
        public static string CleanName(string name)
        {
            var clean = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')') clean.Append(c);
            }
            string result = clean.ToString().Trim();
            if (result.Length > MaxNameLength) result = result.Substring(0, MaxNameLength).TrimEnd();
            return result.Length == 0 ? "texture" : result;
        }
    }
}
