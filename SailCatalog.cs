using System.Collections.Generic;

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

    // One texture the server knows about. The catalog carries no image data: thumbnails and full
    // images are fetched by hash (SailDownloads).
    public class CatalogEntry
    {
        public string Hash;
        public string Name;
        public TextureSource Source;
        public TextureStatus Status;
        public string UploaderName = "";
        public int Width;
        public int Height;
        public int Bytes;

        public void Write(ZPackage package)
        {
            package.Write(Hash);
            package.Write(Name);
            package.Write((byte)Source);
            package.Write((byte)Status);
            package.Write(UploaderName);
            package.Write(Width);
            package.Write(Height);
            package.Write(Bytes);
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
                Bytes = package.ReadInt()
            };
        }

        public static void WriteList(ZPackage package, IReadOnlyCollection<CatalogEntry> entries)
        {
            package.Write(entries.Count);
            foreach (CatalogEntry entry in entries) entry.Write(package);
        }

        public static List<CatalogEntry> ReadList(ZPackage package)
        {
            int count = package.ReadInt();
            var entries = new List<CatalogEntry>(count);
            for (int i = 0; i < count; i++) entries.Add(Read(package));
            return entries;
        }
    }
}
