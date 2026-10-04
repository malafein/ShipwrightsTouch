using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Server side of the texture catalog: what the server knows about, its thumbnails, and sending
    // texture bytes to the clients that ask. For now the catalog is the server's own sails folder;
    // player uploads join it later.
    public static class SailServer
    {
        // Small chunks, sent only while the peer's send queue is nearly empty, so world updates
        // (ZDOMan sends only below 10 KB queued) keep flowing during a transfer.
        private const int ChunkBytes = 32 * 1024;
        private const int MaxQueuedBytes = 8 * 1024;

        private class Transfer
        {
            public long Peer;
            public string Hash;
            public byte[] Data;
            public int NextChunk;
            public int ChunkCount;
        }

        private static readonly Dictionary<string, CatalogEntry> s_entries = new Dictionary<string, CatalogEntry>();
        private static readonly Dictionary<string, byte[]> s_thumbnails = new Dictionary<string, byte[]>();
        private static readonly Dictionary<string, string> s_paths = new Dictionary<string, string>();
        private static readonly List<Transfer> s_transfers = new List<Transfer>();
        private static Coroutine s_pump;

        // Called on the server at session start.
        public static void StartSession(ZNet net)
        {
            s_entries.Clear();
            s_thumbnails.Clear();
            s_paths.Clear();
            s_transfers.Clear();
            s_pump = null;
            if (ZNet.IsSinglePlayer) return;

            SailTextures.Refresh();
            foreach (SailTextures.Entry entry in SailTextures.Entries)
            {
                byte[] bytes = File.ReadAllBytes(entry.Path);
                byte[] thumbnail = SailThumbnails.Make(bytes, out int width, out int height, out string error);
                if (thumbnail == null)
                {
                    Log.Warn($"Skipping server sail texture {entry.Name}: {error}.");
                    continue;
                }

                s_entries[entry.Hash] = new CatalogEntry
                {
                    Hash = entry.Hash,
                    Name = entry.Name,
                    Source = TextureSource.Server,
                    Status = TextureStatus.Approved,
                    Width = width,
                    Height = height,
                    Bytes = bytes.Length
                };
                s_thumbnails[entry.Hash] = thumbnail;
                s_paths[entry.Hash] = entry.Path;
            }
            Log.Info($"Serving {s_entries.Count} sail texture(s) from the server's sails folder.");

            s_pump = net.StartCoroutine(Pump());
        }

        // What a peer may see. Everything is approved server textures for now.
        private static List<CatalogEntry> VisibleTo(long peer)
        {
            if (!SailPolicy.AllowCustomTexturesConfig.Value) return new List<CatalogEntry>();
            return s_entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void SendCatalog(long peer)
        {
            var package = new ZPackage();
            CatalogEntry.WriteList(package, VisibleTo(peer));
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, SailNetwork.CatalogRpc, package);
        }

        public static void RPC_GetThumbnails(long sender, ZPackage request)
        {
            if (!SailNetwork.IsModdedPeer(sender)) return;

            var visible = new HashSet<string>(VisibleTo(sender).Select(e => e.Hash));
            int count = Math.Min(request.ReadInt(), 256);
            var reply = new ZPackage();
            var hashes = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string hash = request.ReadString();
                if (visible.Contains(hash) && s_thumbnails.ContainsKey(hash)) hashes.Add(hash);
            }

            reply.Write(hashes.Count);
            foreach (string hash in hashes)
            {
                reply.Write(hash);
                reply.Write(s_thumbnails[hash]);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, SailNetwork.ThumbnailsRpc, reply);
        }

        public static void RPC_GetTexture(long sender, string hash)
        {
            if (!SailNetwork.IsModdedPeer(sender)) return;
            if (!VisibleTo(sender).Any(e => e.Hash == hash) || !s_paths.TryGetValue(hash, out string path)) return;
            if (s_transfers.Any(t => t.Peer == sender && t.Hash == hash)) return;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException e)
            {
                Log.Warn($"Could not read sail texture {path}: {e.Message}");
                return;
            }

            s_transfers.Add(new Transfer
            {
                Peer = sender,
                Hash = hash,
                Data = data,
                ChunkCount = Math.Max(1, (data.Length + ChunkBytes - 1) / ChunkBytes)
            });
            Log.Debug($"Sending sail texture {hash.Substring(0, 8)} ({data.Length} bytes) to peer {sender}.");
        }

        public static void PeerLeft(long peer) => s_transfers.RemoveAll(t => t.Peer == peer);

        // One chunk per transfer per frame, and only while that peer's connection has room.
        private static IEnumerator Pump()
        {
            while (true)
            {
                for (int i = s_transfers.Count - 1; i >= 0; i--)
                {
                    Transfer transfer = s_transfers[i];
                    ZNetPeer peer = ZNet.instance.GetPeer(transfer.Peer);
                    if (peer == null)
                    {
                        s_transfers.RemoveAt(i);
                        continue;
                    }
                    if (peer.m_socket.GetSendQueueSize() > MaxQueuedBytes) continue;

                    int offset = transfer.NextChunk * ChunkBytes;
                    int length = Math.Min(ChunkBytes, transfer.Data.Length - offset);
                    var chunk = new byte[length];
                    Buffer.BlockCopy(transfer.Data, offset, chunk, 0, length);

                    var package = new ZPackage();
                    package.Write(transfer.Hash);
                    package.Write(transfer.NextChunk);
                    package.Write(transfer.ChunkCount);
                    package.Write(chunk);
                    ZRoutedRpc.instance.InvokeRoutedRPC(transfer.Peer, SailNetwork.TextureChunkRpc, package);

                    if (++transfer.NextChunk >= transfer.ChunkCount) s_transfers.RemoveAt(i);
                }
                yield return null;
            }
        }
    }
}
