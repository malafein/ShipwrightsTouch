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

    // One texture the server knows about. The catalog carries no image data: thumbnails and full
    // images are fetched by hash (SailDownloads).
    public class CatalogEntry
    {
        public const int MaxNameLength = 48;

        public string Hash;
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

        public bool CountsTowardLimit => Source == TextureSource.Player && Status != TextureStatus.Denied;

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
            package.Write(UploaderId != "" && UploaderId == viewerId);
        }

        public static CatalogEntry Read(ZPackage package)
        {
            return new CatalogEntry
            {
                Hash = package.ReadString(),
                Name = package.ReadString(),
                Source = (TextureSource)package.ReadByte(),
                Status = (TextureStatus)package.ReadByte(),
                UploaderName = package.ReadString(),
                Width = package.ReadInt(),
                Height = package.ReadInt(),
                Bytes = package.ReadInt(),
                Mine = package.ReadBool()
            };
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
