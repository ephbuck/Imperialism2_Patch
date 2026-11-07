using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Imp2Net
{
    // Version 3 also carries the host's original lobby world mode.
    // These are transport types, not the opcodes inside the game's DATA messages.
    internal static class WorldProtocol
    {
        public const int Version = 3;
        public const byte Begin = 15, Chunk = 16, Commit = 17, Ack = 18;
        public const byte Stored = 1, Loaded = 2, Failed = 3;
        public const int ChunkSize = 64 * 1024;
        public const long MaxWorldBytes = 32 * 1024 * 1024;
        public const int TransferTimeoutMs = 30000;
        public const int MaxQueuedBytes = 8 * 1024 * 1024;

        public static bool IsInitialOptions(byte[] data)
        {
            return data != null && data.Length == 160 &&
                BitConverter.ToUInt32(data, 0) == 0x000E &&
                BitConverter.ToUInt32(data, 12) == 160 &&
                BitConverter.ToUInt32(data, 16) == 0x74696D65; // engine's 'time' header
        }

        public static bool IsSnapshotReconnectMessage(byte[] data)
        {
            if (data == null || data.Length < 40 || BitConverter.ToInt32(data, 12) != data.Length) return false;
            uint opcode = BitConverter.ToUInt32(data, 0);
            if (opcode == 0x25 && data.Length == 52) return true;
            if (opcode != 0x1F || data.Length != 40) return false;
            uint command = BitConverter.ToUInt32(data, 28);
            return command == 0x7265706F || command == 0x6E616D65; // 'repo', 'name'
        }

        public static byte[] DataFrame(uint from, uint to, byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(from); bw.Write(to); bw.Write(data.Length); bw.Write(data);
                return ms.ToArray();
            }
        }

        public static byte[] AckFrame(Guid transfer, byte state)
        {
            byte[] bytes = new byte[17];
            Buffer.BlockCopy(transfer.ToByteArray(), 0, bytes, 0, 16);
            bytes[16] = state;
            return bytes;
        }

        public static void WriteFrame(NetworkStream stream, byte type, byte[] payload)
        {
            byte[] header = new byte[9];
            Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.MAGIC), 0, header, 0, 4);
            header[4] = type;
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 5, 4);
            stream.Write(header, 0, header.Length);
            if (payload.Length != 0) stream.Write(payload, 0, payload.Length);
        }

        public static Guid ReadGuid(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(16);
            if (bytes.Length != 16) throw new InvalidDataException("Truncated world identifier.");
            return new Guid(bytes);
        }
    }

    internal sealed class WorldSnapshot : IDisposable
    {
        public readonly string PathName;
        public readonly long Length;
        public readonly byte[] Hash;
        public readonly int SaveId;
        private readonly bool m_deleteOnDispose;
        private int m_users = 1;

        public WorldSnapshot(string path, int saveId, bool deleteOnDispose)
        {
            PathName = path; SaveId = saveId; m_deleteOnDispose = deleteOnDispose;
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                Length = file.Length;
                if (Length <= 0 || Length > WorldProtocol.MaxWorldBytes)
                    throw new InvalidDataException("World snapshot size is outside the supported limit.");
                Hash = sha.ComputeHash(file);
            }
        }

        public void Retain() { Interlocked.Increment(ref m_users); }
        public void Dispose()
        {
            if (Interlocked.Decrement(ref m_users) == 0 && m_deleteOnDispose)
            {
                try { File.Delete(PathName); } catch { }
            }
        }
    }

    internal sealed class WorldOffer : IDisposable
    {
        public readonly Guid TransferId = Guid.NewGuid();
        public Guid SessionId;
        public uint From, To;
        public int OriginalMode;
        public byte[] InitialOptions;
        public WorldSnapshot Snapshot;
        private readonly ManualResetEvent m_stored = new ManualResetEvent(false);
        private readonly ManualResetEvent m_loaded = new ManualResetEvent(false);
        private readonly object m_lock = new object();
        private byte m_state;
        private bool m_disposed;

        public void Acknowledge(byte state)
        {
            lock (m_lock)
            {
                if (m_disposed) return;
                if (state == WorldProtocol.Failed) m_state = state;
                else if (state == WorldProtocol.Stored && m_state == 0) m_state = state;
                else if (state == WorldProtocol.Loaded && m_state == WorldProtocol.Stored) m_state = state;
                else throw new InvalidDataException("Unexpected world acknowledgment.");
                m_stored.Set();
                if (m_state != WorldProtocol.Stored) m_loaded.Set();
            }
        }

        public bool IsLoaded { get { lock (m_lock) { return m_state == WorldProtocol.Loaded; } } }
        public bool WaitStored()
        {
            if (!m_stored.WaitOne(WorldProtocol.TransferTimeoutMs)) return false;
            lock (m_lock) { return m_state == WorldProtocol.Stored || m_state == WorldProtocol.Loaded; }
        }
        public bool WaitLoaded()
        {
            if (!m_loaded.WaitOne(WorldProtocol.TransferTimeoutMs)) return false;
            return IsLoaded;
        }

        public void Send(ClientPeer peer)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(SessionId.ToByteArray()); bw.Write(TransferId.ToByteArray());
                bw.Write(Snapshot.SaveId); bw.Write(From); bw.Write(To);
                bw.Write(Snapshot.Length); bw.Write(Snapshot.Hash); bw.Write(OriginalMode);
                WorldProtocol.WriteFrame(peer.Stream, WorldProtocol.Begin, ms.ToArray());
            }
            using (FileStream file = new FileStream(Snapshot.PathName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                byte[] buffer = new byte[WorldProtocol.ChunkSize];
                long offset = 0;
                int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (peer.Closed) throw new IOException("World transfer cancelled.");
                    using (MemoryStream ms = new MemoryStream())
                    using (BinaryWriter bw = new BinaryWriter(ms))
                    {
                        bw.Write(TransferId.ToByteArray()); bw.Write(offset); bw.Write(buffer, 0, count);
                        WorldProtocol.WriteFrame(peer.Stream, WorldProtocol.Chunk, ms.ToArray());
                    }
                    offset += count;
                }
                if (offset != Snapshot.Length) throw new IOException("World snapshot changed during transfer.");
            }
            WorldProtocol.WriteFrame(peer.Stream, WorldProtocol.Commit, TransferId.ToByteArray());
            if (!WaitStored()) throw new IOException("Client did not commit the world snapshot.");
            WorldProtocol.WriteFrame(peer.Stream, DPlayConstants.PKT_TCP_DATA_MSG,
                WorldProtocol.DataFrame(From, To, InitialOptions));
            // The reader continues processing acknowledgments while this peer's writer waits.
            // No session lock or game thread is held. Other peers have independent writers.
            if (!WaitLoaded()) throw new IOException("Client did not load the world snapshot.");
        }

        public void Dispose()
        {
            lock (m_lock)
            {
                if (m_disposed) return;
                m_disposed = true;
                m_state = WorldProtocol.Failed;
                m_stored.Set(); m_loaded.Set();
                // Wait handles are intentionally not closed here: a writer may still be waiting.
                // They are reclaimed with the offer after the writer has stopped using it.
                Snapshot.Dispose();
            }
        }
    }

    internal sealed class OutboundPacket
    {
        public byte Type;
        public byte[] Payload;
        public WorldOffer World;
    }

    internal sealed partial class ClientPeer
    {
        private readonly Queue<OutboundPacket> m_writes = new Queue<OutboundPacket>();
        private readonly List<WorldOffer> m_worlds = new List<WorldOffer>();
        private int m_queuedBytes;
        private bool m_writerStarted;
        private WorldOffer m_activeWorld;
        private bool m_requiresWorld;

        public bool HasUnloadedWorld
        {
            get { lock (WriteLock) { return m_requiresWorld || m_worlds.Count != 0; } }
        }

        public void RequireWorld() { lock (WriteLock) { m_requiresWorld = true; } }

        public bool QueueFrame(byte type, byte[] payload)
        {
            return Queue(new OutboundPacket { Type = type, Payload = payload ?? new byte[0] });
        }

        public bool QueueWorld(WorldOffer offer)
        {
            return Queue(new OutboundPacket { World = offer });
        }

        private bool Queue(OutboundPacket packet)
        {
            bool overflow = false;
            lock (WriteLock)
            {
                if (Closed) return false;
                int size = packet.Payload != null ? packet.Payload.Length : 160;
                if (m_queuedBytes + size > WorldProtocol.MaxQueuedBytes || (packet.World != null && m_worlds.Count >= 2))
                    overflow = true;
                else
                {
                    m_queuedBytes += size;
                    if (packet.World != null) { m_worlds.Add(packet.World); m_requiresWorld = true; }
                    m_writes.Enqueue(packet);
                    if (!m_writerStarted)
                    {
                        Client.SendTimeout = 10000;
                        m_writerStarted = true;
                        Thread writer = new Thread(WriteLoop);
                        writer.IsBackground = true; writer.Name = "Imp2PeerWriter"; writer.Start();
                    }
                    Monitor.PulseAll(WriteLock);
                }
            }
            if (overflow) Close();
            return !overflow;
        }

        private void WriteLoop()
        {
            try
            {
                while (true)
                {
                    OutboundPacket packet;
                    lock (WriteLock)
                    {
                        while (!Closed && m_writes.Count == 0) Monitor.Wait(WriteLock);
                        if (Closed) return;
                        packet = m_writes.Dequeue();
                        m_queuedBytes -= packet.Payload != null ? packet.Payload.Length : 160;
                        m_activeWorld = packet.World;
                    }
                    if (packet.World != null)
                    {
                        try { packet.World.Send(this); }
                        finally
                        {
                            lock (WriteLock)
                            {
                                if (packet.World.IsLoaded) { m_worlds.Remove(packet.World); m_requiresWorld = false; }
                                m_activeWorld = null;
                            }
                            packet.World.Dispose();
                        }
                    }
                    else WorldProtocol.WriteFrame(Stream, packet.Type, packet.Payload);
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("Peer writer stopped: {0}", ex.Message);
                Close();
            }
        }

        public void AcknowledgeWorld(byte[] payload)
        {
            if (payload.Length != 17) throw new InvalidDataException("Invalid world acknowledgment.");
            byte[] id = new byte[16]; Buffer.BlockCopy(payload, 0, id, 0, 16);
            lock (WriteLock)
            {
                if (m_activeWorld == null || m_activeWorld.TransferId != new Guid(id))
                    throw new InvalidDataException("Stale world acknowledgment.");
                m_activeWorld.Acknowledge(payload[16]);
            }
        }

        private void CancelWrites()
        {
            lock (WriteLock)
            {
                if (Closed) return;
                Closed = true;
                foreach (WorldOffer offer in m_worlds) offer.Dispose();
                m_worlds.Clear(); m_writes.Clear(); m_queuedBytes = 0;
                Monitor.PulseAll(WriteLock);
            }
        }
    }

    internal sealed partial class DPlaySessionEngine
    {
        private string m_worldRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Save", "NetSync");
        private WorldReceiver m_incomingWorld;
        private bool m_worldOptionsReceived, m_worldLoaded, m_loadingWorld;
        private bool m_savedWorldResumed;
        private readonly List<byte[]> m_loadMessages = new List<byte[]>();
        private int m_loadMessagesBytes;
        private Timer m_worldDeadline;

        public bool WorldsLoaded
        {
            get
            {
                lock (m_lock)
                {
                    foreach (ClientPeer peer in m_peers)
                        if (!peer.Closed && peer.OwnedPlayers.Count != 0 && peer.HasUnloadedWorld) return false;
                    return true;
                }
            }
        }

        private int CaptureAndSendWorld(uint from, uint to, byte[] options)
        {
            lock (m_lock)
            {
                if (m_peers.Count == 0) return DPlayConstants.DP_OK;
            }
            try
            {
                // DP_Send runs on the game thread, after the host's world exists.
                // Serialize that world rather than selecting an old save or regenerating it locally.
                using (WorldSnapshot snapshot = NativeWorldBridge.Capture(options, m_worldRoot))
                    return SendWorldSnapshot(from, to, options, snapshot);
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("WorldSync: capture failed: {0}", ex);
                lock (m_lock)
                {
                    foreach (PlayerRecord player in m_players.Values)
                        if (!player.IsLocal && (to == 0 || to == player.PlayerId) && player.Connection != null)
                            player.Connection.Close();
                }
                return DPlayConstants.DPERR_GENERIC;
            }
        }

        internal int SendWorldSnapshot(uint from, uint to, byte[] options, WorldSnapshot snapshot)
        {
            if (!WorldProtocol.IsInitialOptions(options)) return DPlayConstants.DPERR_INVALIDPARAM;
            lock (m_lock)
            {
                PlayerRecord sender;
                if (!m_isOpen || !m_isHost || !m_players.TryGetValue(from, out sender) || !sender.IsLocal)
                    return DPlayConstants.DPERR_INVALIDPLAYER;
                List<PlayerRecord> destinations = new List<PlayerRecord>();
                foreach (PlayerRecord player in m_players.Values)
                    if (!player.IsLocal && (to == 0 || to == player.PlayerId)) destinations.Add(player);
                if (to != 0 && destinations.Count == 0) return DPlayConstants.DPERR_INVALIDPLAYER;
                foreach (PlayerRecord player in destinations)
                {
                    byte[] initial = (byte[])options.Clone();
                    snapshot.Retain();
                    WorldOffer offer = new WorldOffer { SessionId = m_guidInstance, From = from,
                        To = player.PlayerId, InitialOptions = initial, Snapshot = snapshot,
                        OriginalMode = BitConverter.ToInt32(options, 0x64) };
                    if (player.Connection == null || !player.Connection.QueueWorld(offer))
                    {
                        offer.Dispose();
                        return DPlayConstants.DPERR_SESSIONLOST;
                    }
                }
                return DPlayConstants.DP_OK;
            }
        }

        private void ReceiveWorldFrame(ClientPeer host, byte type, byte[] payload)
        {
            lock (m_lock)
            {
                if (m_hostConn != host || !m_running) throw new InvalidDataException("Stale world connection.");
                if (type == WorldProtocol.Begin)
                {
                    if (m_incomingWorld != null && !m_worldLoaded)
                        throw new InvalidDataException("A world initialization is already pending.");
                    WorldReceiver next = new WorldReceiver(payload, m_guidInstance, m_worldRoot);
                    PlayerRecord sender, recipient;
                    if (!m_players.TryGetValue(next.From, out sender) || sender.IsLocal ||
                        !m_players.TryGetValue(next.To, out recipient) || !recipient.IsLocal)
                    {
                        next.Dispose(); throw new InvalidDataException("World recipient mismatch.");
                    }
                    if (m_incomingWorld != null) m_incomingWorld.Dispose();
                    m_incomingWorld = next;
                    m_worldOptionsReceived = false; m_worldLoaded = false;
                    m_savedWorldResumed = false;
                    if (m_worldDeadline != null) m_worldDeadline.Dispose();
                    m_worldDeadline = new Timer(delegate(object ignored)
                    {
                        lock (m_lock)
                        {
                            if (m_hostConn != host || m_incomingWorld != next || m_worldLoaded) return;
                            if ((DateTime.UtcNow - next.LastProgressUtc).TotalMilliseconds < WorldProtocol.TransferTimeoutMs) return;
                            DPlayConfig.Log("WorldSync: receive/load deadline expired for {0}", next.TransferId);
                            host.Close();
                        }
                    }, null, 1000, 1000);
                }
                else
                {
                    if (m_incomingWorld == null) throw new InvalidDataException("World data arrived without a manifest.");
                    if (type == WorldProtocol.Chunk) m_incomingWorld.AddChunk(payload);
                    else
                    {
                        m_incomingWorld.Commit(payload);
                        SendFrame(host, WorldProtocol.Ack, WorldProtocol.AckFrame(m_incomingWorld.TransferId, WorldProtocol.Stored));
                    }
                }
            }
        }

        private void ValidateWorldInitialization(uint from, uint to, byte[] data)
        {
            if (!WorldProtocol.IsInitialOptions(data)) return;
            lock (m_lock)
            {
                if (m_incomingWorld == null || !m_incomingWorld.Committed || m_worldOptionsReceived ||
                    from != m_incomingWorld.From || to != m_incomingWorld.To ||
                    BitConverter.ToInt32(data, 0x60) != m_incomingWorld.SaveId ||
                    BitConverter.ToInt32(data, 0x64) != m_incomingWorld.OriginalMode)
                    throw new InvalidDataException("World initialization does not match a committed snapshot.");
                m_worldOptionsReceived = true;
            }
        }

        internal bool LoadReceivedWorld(int saveId)
        {
            WorldReceiver incoming;
            lock (m_lock)
            {
                incoming = m_incomingWorld;
                if (incoming != null && incoming.Committed && m_worldOptionsReceived && m_worldLoaded && incoming.SaveId == saveId)
                    return true; // Shared lounge continuation also checks already-loaded full worlds.
                if (incoming == null || !incoming.Committed || !m_worldOptionsReceived ||
                    incoming.SaveId != saveId || m_loadingWorld) return false;
                m_loadingWorld = true;
            }
            bool success = false;
            try { success = NativeWorldBridge.LoadFile(incoming.FinalPath, incoming.OriginalMode); return success; }
            finally { ReportWorldLoaded(saveId, success); }
        }

        internal void ReportWorldLoaded(int saveId, bool success)
        {
            lock (m_lock)
            {
                if (m_incomingWorld == null || m_incomingWorld.SaveId != saveId || !m_worldOptionsReceived || m_worldLoaded)
                    throw new InvalidOperationException("No matching world is awaiting engine ingestion.");
                m_loadingWorld = false; m_worldLoaded = success;
                if (m_worldDeadline != null) { m_worldDeadline.Dispose(); m_worldDeadline = null; }
                // Native deserialization can send status updates. The host must first learn that
                // loading returned successfully, then see those statuses in their original order.
                SendFrame(m_hostConn, WorldProtocol.Ack, WorldProtocol.AckFrame(m_incomingWorld.TransferId,
                    success ? WorldProtocol.Loaded : WorldProtocol.Failed));
                if (success)
                    foreach (byte[] message in m_loadMessages) SendFrame(m_hostConn, DPlayConstants.PKT_TCP_DATA_MSG, message);
                m_loadMessages.Clear(); m_loadMessagesBytes = 0;
            }
        }

        // Poll only from the game's Receive call, after the native ownership
        // handler has applied the host's claim confirmation. Never resume from
        // a network reader, or while a different country merely appears selected.
        internal bool ResumeOwnedSavedWorld()
        {
            WorldReceiver incoming;
            lock (m_lock)
            {
                incoming = m_incomingWorld;
                if (incoming == null || incoming.OriginalMode != 0x6C6F6164 ||
                    !m_worldLoaded || m_loadingWorld || m_savedWorldResumed) return false;
            }
            int country;
            try { country = NativeWorldBridge.OwnedSavedCountry(this, incoming.SaveId, incoming.To); }
            catch (Exception ex)
            {
                DPlayConfig.Log("WorldSync: saved-country validation failed: {0}", ex);
                lock (m_lock) { if (m_hostConn != null) m_hostConn.Close(); }
                return true;
            }
            if (country < 0) return false;
            lock (m_lock)
            {
                if (m_incomingWorld != incoming || m_loadingWorld || m_savedWorldResumed) return false;
                m_loadingWorld = true;
            }
            bool success = false;
            try { success = NativeWorldBridge.LoadFile(incoming.FinalPath, incoming.OriginalMode, country); }
            catch (Exception ex) { DPlayConfig.Log("WorldSync: saved-country resume failed: {0}", ex); }
            finally
            {
                lock (m_lock)
                {
                    m_loadingWorld = false; m_savedWorldResumed = success;
                    if (success)
                        foreach (byte[] message in m_loadMessages) SendFrame(m_hostConn, DPlayConstants.PKT_TCP_DATA_MSG, message);
                    else if (m_hostConn != null) m_hostConn.Close();
                    m_loadMessages.Clear(); m_loadMessagesBytes = 0;
                }
            }
            return true;
        }

        private void ClearWorldTransfer()
        {
            lock (m_lock)
            {
                if (m_worldDeadline != null) { m_worldDeadline.Dispose(); m_worldDeadline = null; }
                if (m_incomingWorld != null) { m_incomingWorld.Dispose(); m_incomingWorld = null; }
                m_worldOptionsReceived = false; m_worldLoaded = false; m_loadingWorld = false;
                m_savedWorldResumed = false;
                m_loadMessages.Clear(); m_loadMessagesBytes = 0;
            }
        }
    }

    internal sealed class WorldReceiver : IDisposable
    {
        public readonly Guid TransferId;
        public readonly int SaveId;
        public readonly int OriginalMode;
        public readonly uint From, To;
        public readonly string FinalPath;
        private readonly string m_partialPath;
        private readonly long m_length;
        private readonly byte[] m_hash;
        private FileStream m_file;
        private long m_received;
        public bool Committed { get; private set; }
        public DateTime LastProgressUtc { get; private set; }

        public WorldReceiver(byte[] payload, Guid session, string saveRoot)
        {
            if (payload.Length != 88) throw new InvalidDataException("Invalid world manifest.");
            using (BinaryReader br = new BinaryReader(new MemoryStream(payload)))
            {
                if (WorldProtocol.ReadGuid(br) != session) throw new InvalidDataException("Stale world session.");
                TransferId = WorldProtocol.ReadGuid(br); SaveId = br.ReadInt32();
                From = br.ReadUInt32(); To = br.ReadUInt32();
                m_length = br.ReadInt64(); m_hash = br.ReadBytes(32); OriginalMode = br.ReadInt32();
            }
            if (TransferId == Guid.Empty || From == 0 || To == 0 || m_length <= 0 || m_length > WorldProtocol.MaxWorldBytes)
                throw new InvalidDataException("Invalid world transfer parameters.");
            if (OriginalMode != 0x6C6F6164 && OriginalMode != 0x72616E64 &&
                (OriginalMode < 0x73636E30 || OriginalMode > 0x73637A39))
                throw new InvalidDataException("Invalid host world mode.");
            Directory.CreateDirectory(saveRoot);
            // The remote peer never supplies a filesystem path.
            FinalPath = Path.Combine(saveRoot, "world-" + session.ToString("N") + "-" + TransferId.ToString("N") + ".imp");
            m_partialPath = FinalPath + ".part";
            m_file = new FileStream(m_partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            LastProgressUtc = DateTime.UtcNow;
        }

        public void AddChunk(byte[] payload)
        {
            if (Committed || payload.Length <= 24 || payload.Length > 24 + WorldProtocol.ChunkSize)
                throw new InvalidDataException("Invalid world chunk.");
            using (BinaryReader br = new BinaryReader(new MemoryStream(payload)))
            {
                if (WorldProtocol.ReadGuid(br) != TransferId || br.ReadInt64() != m_received)
                    throw new InvalidDataException("Unexpected world chunk identifier or offset.");
            }
            int count = payload.Length - 24;
            if (count > m_length - m_received) throw new InvalidDataException("World chunk exceeds advertised size.");
            m_file.Write(payload, 24, count); m_received += count;
            LastProgressUtc = DateTime.UtcNow;
        }

        public void Commit(byte[] payload)
        {
            if (Committed || payload.Length != 16 || new Guid(payload) != TransferId || m_received != m_length)
                throw new InvalidDataException("Incomplete world transfer.");
            m_file.Flush(true); m_file.Position = 0;
            byte[] actual;
            using (SHA256 sha = SHA256.Create()) actual = sha.ComputeHash(m_file);
            int difference = 0;
            for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ m_hash[i];
            if (difference != 0) throw new InvalidDataException("World snapshot checksum mismatch.");
            m_file.Dispose(); m_file = null;
            File.Move(m_partialPath, FinalPath); // Same directory/volume; never exposes a partial final file.
            Committed = true; LastProgressUtc = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (m_file != null) { m_file.Dispose(); m_file = null; }
            try { File.Delete(m_partialPath); } catch { }
            try { File.Delete(FinalPath); } catch { }
        }
    }

    // Full host saves contain AI/host-only extensions after each great power.
    // Client countries are TRemoteGreatPower and read only TGreatPower's record.
    // Override just WriteTo in private table copies during the synchronous save;
    // preserve every other virtual method and restore the original tables on unwind.
    internal sealed class NativeCountryWriteScope : IDisposable
    {
        private readonly List<IntPtr> m_objects = new List<IntPtr>();
        private readonly List<IntPtr> m_originals = new List<IntPtr>();
        private readonly List<IntPtr> m_tables = new List<IntPtr>();

        public NativeCountryWriteScope(IntPtr[] countries, IntPtr writer, int tableBytes)
            : this(countries, writer, tableBytes, 0x14) { }

        public NativeCountryWriteScope(IntPtr[] countries, IntPtr writer, int tableBytes, int methodOffset)
        {
            try
            {
                foreach (IntPtr country in countries)
                {
                    if (country == IntPtr.Zero || m_objects.Contains(country)) continue;
                    IntPtr original = Marshal.ReadIntPtr(country);
                    byte[] bytes = new byte[tableBytes];
                    Marshal.Copy(original, bytes, 0, bytes.Length);
                    IntPtr table = Marshal.AllocHGlobal(bytes.Length);
                    m_tables.Add(table);
                    Marshal.Copy(bytes, 0, table, bytes.Length);
                    Marshal.WriteIntPtr(table, methodOffset, writer);
                    m_objects.Add(country); m_originals.Add(original);
                    Marshal.WriteIntPtr(country, table);
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            for (int i = m_objects.Count - 1; i >= 0; i--)
                Marshal.WriteIntPtr(m_objects[i], m_originals[i]);
            m_objects.Clear(); m_originals.Clear();
            foreach (IntPtr table in m_tables) Marshal.FreeHGlobal(table);
            m_tables.Clear();
        }
    }

    // Select the local country before its native object is constructed during
    // saved-world loading. The file belongs to the host, whose local-country
    // role must not become the joining client's local country.
    internal sealed class NativeCountryRoleScope : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void InitializeCountry(IntPtr self, int country, int initialize);
        private readonly InitializeCountry m_original, m_override;
        private readonly NativeCountryWriteScope m_scope;
        public NativeCountryRoleScope(IntPtr simulation, int selected, IntPtr currentCountry)
        {
            if (selected < 0 || selected >= 6) throw new ArgumentOutOfRangeException("selected");
            m_original = (InitializeCountry)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(simulation), 0x2C), typeof(InitializeCountry));
            m_override = delegate(IntPtr self, int country, int initialize)
            {
                if (self != simulation || country < 0 || country >= 6)
                    throw new InvalidOperationException("Unexpected native country initialization.");
                Marshal.WriteInt16(self, 0x134 + country * 2, (short)(country == selected ? 1 : 4));
                Marshal.WriteInt16(currentCountry, (short)selected);
                m_original(self, country, initialize);
            };
            m_scope = new NativeCountryWriteScope(new IntPtr[] { simulation },
                Marshal.GetFunctionPointerForDelegate(m_override), 0x88, 0x2C);
        }
        public void Dispose() { m_scope.Dispose(); GC.KeepAlive(m_override); GC.KeepAlive(m_original); }
    }

    // Only called from the native game's DP_Send / patched loader on its game thread.
    // Network readers never invoke the engine, its serializer, or any UI methods.
    internal static class NativeWorldBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void StringConstructor(IntPtr self, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void StringDestructor(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate byte FileOperation(IntPtr self, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateNativeObject();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte OriginalWorldLoader(int saveId, IntPtr prefix);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void InitializeHandleStream(IntPtr self, IntPtr handle, int growth);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void StreamOperation(IntPtr self, IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void RefreshSimulation(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr DeleteNativeObject(IntPtr self, int flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr handle);
        private static DPlaySessionEngine s_client, s_host;
        private static bool s_loadedIntoLobby;

        public static void SetClient(DPlaySessionEngine engine) { s_client = engine; }
        public static void SetHost(DPlaySessionEngine engine) { s_host = engine; }
        public static void Clear(DPlaySessionEngine engine)
        {
            Interlocked.CompareExchange(ref s_client, null, engine);
            Interlocked.CompareExchange(ref s_host, null, engine);
        }

        private static void CheckBytes(int address, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(new IntPtr(address + i)) != expected[i])
                    throw new InvalidOperationException("Unsupported game executable for native world synchronization.");
        }

        private static void VerifyGame()
        {
            ProcessModule module = Process.GetCurrentProcess().MainModule;
            if (IntPtr.Size != 4 || module.BaseAddress.ToInt64() != 0x400000 || module.ModuleMemorySize < 0x3B0000)
                throw new InvalidOperationException("World capture requires the patched Imperialism II v1.03 process.");
            int peOffset = Marshal.ReadInt32(module.BaseAddress, 0x3C);
            if (peOffset < 0x40 || peOffset > 0x1000 ||
                Marshal.ReadInt32(module.BaseAddress, peOffset + 8) != 0x3757FA06)
                throw new InvalidOperationException("Unsupported game executable timestamp.");
            // Validate the actual release, not the older linker MAP addresses.
            CheckBytes(0x679FD0, new byte[] { 0x64, 0xA1, 0, 0, 0, 0, 0x6A, 0xFF });
            CheckBytes(0x67A0E0, new byte[] { 0x8B, 0x54, 0x24, 4, 0x8B, 0x0D, 0x8C, 0x98, 0x75, 0 });
            CheckBytes(0x504620, new byte[] { 0x51, 0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x14, 0x57, 0x8B, 0xF9 });
        }

        private static NativeCountryWriteScope ClientCountryRecords()
        {
            IntPtr[] countries = new IntPtr[6];
            for (int i = 0; i < countries.Length; i++)
            {
                countries[i] = Marshal.ReadIntPtr(new IntPtr(0x7606A0 + i * 4));
                if (countries[i] == IntPtr.Zero) continue;
                int table = Marshal.ReadInt32(countries[i]);
                // v1.03 AI, local multiplayer, host, client, and remote countries.
                if (table != 0x6FE508 && table != 0x705E70 && table != 0x706280 && table != 0x706690 && table != 0x706AA0)
                    throw new InvalidOperationException(String.Format("Unsupported native country type 0x{0:X8} for client world capture.", table));
            }
            return new NativeCountryWriteScope(countries, new IntPtr(0x504620), 0x410);
        }

        private static bool Operate(string path, int address)
        {
            VerifyGame();
            if (Encoding.Default.GetString(Encoding.Default.GetBytes(path)) != path)
                throw new IOException("The game cannot represent the world snapshot path in its current code page.");
            IntPtr text = Marshal.StringToHGlobalAnsi(path);
            IntPtr nativeString = Marshal.AllocHGlobal(4);
            bool constructed = false;
            try
            {
                StringConstructor ctor = (StringConstructor)Marshal.GetDelegateForFunctionPointer(new IntPtr(0x6A75AB), typeof(StringConstructor));
                ctor(nativeString, text); constructed = true;
                FileOperation operation = (FileOperation)Marshal.GetDelegateForFunctionPointer(new IntPtr(address), typeof(FileOperation));
                return operation(Marshal.ReadIntPtr(new IntPtr(0x75A608)), nativeString) != 0;
            }
            finally
            {
                if (constructed)
                {
                    StringDestructor dtor = (StringDestructor)Marshal.GetDelegateForFunctionPointer(new IntPtr(0x6A753D), typeof(StringDestructor));
                    dtor(nativeString);
                }
                Marshal.FreeHGlobal(nativeString); Marshal.FreeHGlobal(text);
            }
        }

        public static WorldSnapshot Capture(byte[] options, string saveRoot)
        {
            VerifyGame();
            Directory.CreateDirectory(saveRoot);
            string path = Path.Combine(saveRoot, "host-" + Guid.NewGuid().ToString("N") + ".imp");
            try
            {
                if (BitConverter.ToInt32(options, 0x64) != 0x6C6F6164)
                {
                    byte[] map = TransferMap(null);
                    using (BinaryWriter file = new BinaryWriter(File.Create(path)))
                    {
                        file.Write(0x4D4E3249); // I2NM: native pre-launch map stream
                        file.Write(BitConverter.ToInt32(options, 0x60)); file.Write(map.Length); file.Write(map);
                    }
                    return new WorldSnapshot(path, BitConverter.ToInt32(options, 0x60), true);
                }
                using (ClientCountryRecords())
                    if (!Operate(path, 0x679FD0)) throw new IOException("The game could not save the active world.");
                return new WorldSnapshot(path, BitConverter.ToInt32(options, 0x60), true);
            }
            catch { try { File.Delete(path); } catch { } throw; }
        }

        private static byte[] TransferMap(byte[] source)
        {
            CheckBytes(0x49D9E0, new byte[] { 0x6A,0xFF,0x68,0xF2,0x29,0x6D,0 });
            CheckBytes(0x54B210, new byte[] { 0x53,0x55,0x56,0x8B,0x74,0x24,0x10,0x57,0x8B,0xD9 });
            IntPtr map = Marshal.ReadIntPtr(new IntPtr(0x7606F4));
            if (map == IntPtr.Zero || Marshal.ReadInt32(map) != 0x703848)
                throw new InvalidOperationException("The native staging map is unavailable.");
            int capacity = source == null ? 1024 * 1024 : source.Length;
            IntPtr handle = GlobalAlloc(2, new UIntPtr((uint)capacity)), stream = IntPtr.Zero;
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            int version = Marshal.ReadInt32(new IntPtr(0x747C94));
            try
            {
                if (source != null)
                {
                    IntPtr data = GlobalLock(handle);
                    if (data == IntPtr.Zero) throw new IOException("Could not access native map storage.");
                    try { Marshal.Copy(source, 0, data, source.Length); } finally { GlobalUnlock(handle); }
                }
                stream = ((CreateNativeObject)Marshal.GetDelegateForFunctionPointer(new IntPtr(0x49D9E0), typeof(CreateNativeObject)))();
                if (stream == IntPtr.Zero) throw new OutOfMemoryException();
                ((InitializeHandleStream)Marshal.GetDelegateForFunctionPointer(new IntPtr(0x49DBB0), typeof(InitializeHandleStream)))(stream, handle, 65536);
                Marshal.WriteInt32(new IntPtr(0x747C94), 0x61);
                ((StreamOperation)Marshal.GetDelegateForFunctionPointer(new IntPtr(source == null ? 0x54B210 : 0x54B040), typeof(StreamOperation)))(map, stream);
                handle = Marshal.ReadIntPtr(stream, 0x64);
                int count = Marshal.ReadInt32(stream, 0x68);
                if (count <= 0 || count > 1024 * 1024 || (source != null && count != source.Length))
                    throw new InvalidDataException("Invalid native map stream length.");
                if (source != null) return source;
                byte[] result = new byte[count]; IntPtr bytes = GlobalLock(handle);
                if (bytes == IntPtr.Zero) throw new IOException("Could not access native map snapshot.");
                try { Marshal.Copy(bytes, result, 0, result.Length); } finally { GlobalUnlock(handle); }
                return result;
            }
            finally
            {
                Marshal.WriteInt32(new IntPtr(0x747C94), version);
                if (stream != IntPtr.Zero)
                {
                    handle = Marshal.ReadIntPtr(stream, 0x64);
                    IntPtr table = Marshal.ReadIntPtr(stream);
                    ((DeleteNativeObject)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, 4), typeof(DeleteNativeObject)))(stream, 1);
                }
                GlobalFree(handle);
            }
        }

        public static int OwnedSavedCountry(DPlaySessionEngine client, int saveId, uint playerId)
        {
            if (s_client != client) return -1;
            VerifyGame();
            IntPtr multiplayer = Marshal.ReadIntPtr(new IntPtr(0x7606E8));
            int selected = Marshal.ReadByte(multiplayer, 0xDC);
            if (Marshal.ReadIntPtr(multiplayer, 0x40) == IntPtr.Zero || selected >= 6 ||
                Marshal.ReadInt32(multiplayer, 0x60) != saveId ||
                unchecked((uint)Marshal.ReadInt32(multiplayer, 0x48 + 4 * selected)) != playerId) return -1;
            return selected;
        }

        public static bool LoadFile(string path, int originalMode) { return LoadFile(path, originalMode, -1); }

        public static bool LoadFile(string path, int originalMode, int resumeCountry)
        {
            VerifyGame();
            IntPtr multiplayer = Marshal.ReadIntPtr(new IntPtr(0x7606E8));
            IntPtr lounge = Marshal.ReadIntPtr(multiplayer, 0x40);
            s_loadedIntoLobby = false;
            if (originalMode != 0x6C6F6164)
            {
                using (BinaryReader file = new BinaryReader(File.OpenRead(path)))
                {
                    if (file.ReadInt32() != 0x4D4E3249 || file.ReadInt32() != Marshal.ReadInt32(multiplayer, 0x60))
                        throw new InvalidDataException("Invalid staging map snapshot.");
                    int count = file.ReadInt32();
                    if (count < 0x3F480 || count > 1024 * 1024 || file.BaseStream.Length != count + 12)
                        throw new InvalidDataException("Invalid staging map size.");
                    TransferMap(file.ReadBytes(count));
                    return true;
                }
            }
            if (lounge == IntPtr.Zero) return Operate(path, 0x67A0E0);

            IntPtr simulation = Marshal.ReadIntPtr(new IntPtr(0x75A5B8));
            if (Marshal.ReadInt32(simulation) != 0x70E388)
                throw new InvalidOperationException("Unsupported native simulation type for lobby ingestion.");
            CheckBytes(0x5FB196, new byte[] { 0xC3 });
            if (resumeCountry >= 0)
                CheckBytes(0x5F7120, new byte[] { 0x6A,0xFF,0x68,0xAC,0x05,0x6E,0 });
            int phase = Marshal.ReadInt32(simulation, 4);
            short country = Marshal.ReadInt16(new IntPtr(0x760710));
            byte[] owners = new byte[24]; Marshal.Copy(IntPtr.Add(multiplayer, 0x48), owners, 0, owners.Length);
            byte[] statuses = new byte[24]; Marshal.Copy(IntPtr.Add(multiplayer, 0xC0), statuses, 0, statuses.Length);
            int networkPhase = Marshal.ReadInt32(multiplayer, 0xD8);
            byte selectedCountry = Marshal.ReadByte(multiplayer, 0xDC);
            bool loaded = false;
            // ReadFrom ends by entering the saved planning phase. A joining player
            // still needs the lounge's country selection, so defer that refresh.
            using (IDisposable roles = resumeCountry >= 0 ? new NativeCountryRoleScope(simulation, resumeCountry, new IntPtr(0x760710)) : null)
            using (new NativeCountryWriteScope(new IntPtr[] { simulation }, new IntPtr(0x5FB196), 0x88, 0x44))
                try { loaded = Operate(path, 0x67A0E0); }
                finally
                {
                    Marshal.WriteInt32(simulation, 4, phase);
                    Marshal.WriteInt16(new IntPtr(0x760710), country);
                    // Save files describe their previous players. Live ownership
                    // arrives in the following 0x0009 records for this connection.
                    Marshal.Copy(owners, 0, IntPtr.Add(multiplayer, 0x48), owners.Length);
                    Marshal.WriteInt32(multiplayer, 0xD8, networkPhase);
                    Marshal.WriteByte(multiplayer, 0xDC, selectedCountry);
                    Marshal.WriteInt32(multiplayer, 0xE0, originalMode);
                }
            if (loaded && resumeCountry >= 0)
            {
                Marshal.Copy(statuses, 0, IntPtr.Add(multiplayer, 0xC0), statuses.Length);
                Marshal.WriteInt16(new IntPtr(0x760710), (short)resumeCountry);
                Marshal.WriteByte(multiplayer, 0xDC, (byte)resumeCountry);
                Marshal.WriteInt32(multiplayer, 0xD8, 0x676F696E); // native 'goin' phase
                Marshal.WriteIntPtr(multiplayer, 0x40, IntPtr.Zero);
                Marshal.WriteInt32(simulation, 4, 4); // native post-load UI entry; does not generate a world
                ((RefreshSimulation)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(simulation), 0x44), typeof(RefreshSimulation)))(simulation);
                return true;
            }
            s_loadedIntoLobby = loaded;
            return loaded;
        }
        public static int Load(int saveId, IntPtr prefix)
        {
            DPlaySessionEngine client = s_client;
            // The native options handler also initializes the host's lounge.
            // It has no incoming snapshot: retain its original file loader, and
            // leave its already-generated map intact at the pre-preview hook.
            try
            {
                if (client == null && s_host != null)
                {
                    if (prefix == IntPtr.Zero) return 1;
                    VerifyGame();
                    return ((OriginalWorldLoader)Marshal.GetDelegateForFunctionPointer(
                        new IntPtr(0x5C19C0), typeof(OriginalWorldLoader)))(saveId, prefix);
                }
                return client != null && client.LoadReceivedWorld(saveId) ? (s_loadedIntoLobby ? 2 : 1) : 0;
            }
            catch (Exception ex) { DPlayConfig.Log("WorldSync: native load failed: {0}", ex); return 0; }
        }
        public static int CanStart()
        {
            DPlaySessionEngine host = s_host;
            return host == null || host.WorldsLoaded ? 1 : 0;
        }
    }
}
