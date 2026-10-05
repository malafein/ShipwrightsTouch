using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using malafein.Valheim.Shared;
using UnityEngine;

namespace malafein.Valheim.ShipwrightsTouch
{
    // Client side of sharing: a texture from the player's own folder is offered to the server the
    // first time it goes on a ship (submit on first use). The server answers the offer (send it,
    // already have it, or refused with a reason), then takes the bytes in paced chunks and answers
    // again once it has checked them. One upload at a time; the rest wait their turn.
    public static class SailUploads
    {
        // No answer from the server for this long gives up on the upload (the server drops a
        // silent upload after 60 s).
        private const float AnswerTimeoutSeconds = 90f;

        private class Upload
        {
            public string Hash;
            public string Name;
            public byte[] Data;
            public int NextChunk = -1;
            public int ChunkCount;
            public float LastActivity;
        }

        private static readonly Queue<SailTextures.Entry> s_queue = new Queue<SailTextures.Entry>();
        private static readonly Dictionary<string, string> s_refusals = new Dictionary<string, string>();
        private static Upload s_current;
        private static Coroutine s_pump;

        // Raised when an upload starts, finishes or is refused.
        public static event Action Changed;

        public static void StartSession()
        {
            s_queue.Clear();
            s_refusals.Clear();
            s_current = null;
            s_pump = null;
        }

        public static bool IsUploading(string hash)
        {
            return (s_current != null && s_current.Hash == hash) || s_queue.Any(e => e.Hash == hash);
        }

        // Why the last attempt this session wasn't shared, or null.
        public static string RefusalOf(string hash)
        {
            return hash != null && s_refusals.TryGetValue(hash, out string reason) ? reason : null;
        }

        // Whether putting this texture on a ship would offer it to the server now.
        public static bool WouldSubmit(string hash)
        {
            return SailNetwork.Mode == ServerMode.Modded
                   && SailNetwork.Policy.AllowCustomTextures
                   && SailNetwork.Policy.AllowPlayerTextures
                   && Plugin.ShareTextures.Value
                   && SailTextures.IsLocal(hash)
                   && SailTextures.Get(hash) != null
                   && SailDownloads.Find(hash) == null
                   && !IsUploading(hash);
        }

        public static void Submit(string hash)
        {
            if (!WouldSubmit(hash)) return;
            SailTextures.Entry entry = SailTextures.Entries.FirstOrDefault(e => e.Hash == hash);
            if (entry == null) return;

            // The server checks all of this again; checking here first skips a pointless upload
            // and tells the player at once.
            string refusal = LocalProblem(entry.Hash);
            if (refusal != null)
            {
                Refuse(hash, entry.Name, refusal);
                return;
            }

            s_refusals.Remove(hash);
            s_queue.Enqueue(entry);
            Changed?.Invoke();
            StartNext();
        }

        // Why the server would refuse one of the player's own textures, as far as this client can
        // tell from the policy it was sent; null if it looks fine. Also used by the panel, to warn
        // before Apply.
        public static string LocalProblem(string hash)
        {
            SailTextures.Entry entry = SailTextures.Entries.FirstOrDefault(e => e.Hash == hash);
            if (entry == null) return null;

            // See SailServer.CheckImage.
            string extension = Path.GetExtension(entry.Path).ToLowerInvariant();
            if (extension == ".jpg" || extension == ".jpeg") return "only PNG images can be shared";

            SailPolicy policy = SailNetwork.Policy;
            long size = new FileInfo(entry.Path).Length;
            if (size > policy.MaxFileKilobytes * 1024L) return $"it's over the server's {policy.MaxFileKilobytes} KB limit";

            Texture2D texture = SailTextures.Get(entry.Hash);
            if (texture != null && (texture.width > policy.MaxDimension || texture.height > policy.MaxDimension))
                return $"it's {texture.width}x{texture.height}, over the server's {policy.MaxDimension}x{policy.MaxDimension} limit";

            int shared = SailDownloads.SharedCount();
            if (shared >= policy.MaxTexturesPerPlayer) return $"you're sharing {shared} of {policy.MaxTexturesPerPlayer} textures already";

            return null;
        }

        private static void StartNext()
        {
            ZNet net = ZNet.instance;
            if (s_current != null || net == null) return;

            while (s_queue.Count > 0)
            {
                SailTextures.Entry entry = s_queue.Dequeue();
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(entry.Path);
                }
                catch (IOException e)
                {
                    Log.Warn($"Could not read sail texture {entry.Name} to share it: {e.Message}");
                    continue;
                }
                if (SailTextures.HashOf(data) != entry.Hash) continue;

                s_current = new Upload
                {
                    Hash = entry.Hash,
                    Name = CatalogEntry.CleanName(entry.Name),
                    Data = data,
                    ChunkCount = Math.Max(1, (data.Length + SailServer.ChunkBytes - 1) / SailServer.ChunkBytes),
                    LastActivity = Time.time
                };

                var package = new ZPackage();
                package.Write(s_current.Hash);
                package.Write(s_current.Name);
                package.Write(data.Length);
                Log.Debug($"Offering sail texture {s_current.Name} ({data.Length} bytes) to the server.");
                ZRoutedRpc.instance.InvokeRoutedRPC(net.GetServerPeer().m_uid, SailNetwork.OfferUploadRpc, package);

                if (s_pump == null) s_pump = net.StartCoroutine(Pump());
                return;
            }
        }

        public static void RPC_UploadAnswer(long sender, ZPackage package)
        {
            ZNet net = ZNet.instance;
            if (net == null || net.IsServer() || sender != net.GetServerPeer()?.m_uid) return;

            string hash = package.ReadString();
            var answer = (UploadAnswer)package.ReadByte();
            var status = (TextureStatus)package.ReadByte();
            string refusal = package.ReadString();
            if (s_current == null || s_current.Hash != hash) return;

            string name = s_current.Name;
            switch (answer)
            {
                case UploadAnswer.Send:
                    s_current.NextChunk = 0;
                    s_current.LastActivity = Time.time;
                    return;

                case UploadAnswer.Shared:
                    s_current = null;
                    Log.Info($"Shared sail texture {name} ({status}).");
                    ShowMessage(status == TextureStatus.Pending
                        ? $"Sail texture {name} shared: waiting for approval."
                        : $"Sail texture {name} shared.");
                    break;

                default:
                    s_current = null;
                    Refuse(hash, name, refusal);
                    break;
            }
            Changed?.Invoke();
            StartNext();
        }

        private static void Refuse(string hash, string name, string reason)
        {
            s_refusals[hash] = reason;
            Log.Info($"Sail texture {name} wasn't shared: {reason}.");
            ShowMessage($"Sail texture {name} not shared: {reason}. Only you see it.");
            Changed?.Invoke();
        }

        private static void ShowMessage(string text)
        {
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }

        // One chunk per frame, only while the connection to the server has room, so the world
        // keeps updating during an upload.
        private static IEnumerator Pump()
        {
            while (true)
            {
                Upload upload = s_current;
                ZNetPeer server = ZNet.instance?.GetServerPeer();
                if (upload != null && server != null)
                {
                    if (Time.time - upload.LastActivity > AnswerTimeoutSeconds)
                    {
                        s_current = null;
                        Refuse(upload.Hash, upload.Name, "the server didn't answer");
                        StartNext();
                    }
                    else if (upload.NextChunk >= 0 && upload.NextChunk < upload.ChunkCount
                             && server.m_socket.GetSendQueueSize() <= SailServer.MaxQueuedBytes)
                    {
                        int offset = upload.NextChunk * SailServer.ChunkBytes;
                        int length = Math.Min(SailServer.ChunkBytes, upload.Data.Length - offset);
                        var chunk = new byte[length];
                        Buffer.BlockCopy(upload.Data, offset, chunk, 0, length);

                        var package = new ZPackage();
                        package.Write(upload.Hash);
                        package.Write(upload.NextChunk);
                        package.Write(chunk);
                        ZRoutedRpc.instance.InvokeRoutedRPC(server.m_uid, SailNetwork.UploadChunkRpc, package);

                        upload.NextChunk++;
                        upload.LastActivity = Time.time;
                    }
                }
                yield return null;
            }
        }
    }
}
