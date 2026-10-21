using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ParelVR.Volt.Core.Net;
using UnityEngine;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// Multi-client play mode: several clients of the same world on one machine, each in its own editor window
    /// (Unity's Multiplayer Play Mode editor instances, or a second editor on the same project). They meet on a
    /// local port; the first one to enter play mode keeps the meeting point (the relay) and the others join it.
    /// Every client runs its own copy of the world with its own network session, exactly as two players would.
    /// </summary>
    public sealed class VoltMultiClientOptions
    {
        public const int DefaultPort = 47821;

        /// <summary>Loopback port the clients meet on.</summary>
        public int Port = DefaultPort;
        /// <summary>Extra one-way delay for every message, to see how scripts behave on a slow connection.</summary>
        public int LatencyMs;
        /// <summary>Share of unreliable messages that are dropped, 0 to 50.</summary>
        public float LossPercent;
        /// <summary>Hash of the module, so clients running different builds of the scripts are told.</summary>
        public string ModuleHash;
    }

    /// <summary>What the other clients see of a player: where it stands and where its head and hands are.</summary>
    internal struct SimulatedPose
    {
        public Vector3 Feet;
        public float Yaw;
        public Vector3 Velocity;
        public Vector3 Head;
        public Quaternion HeadRotation;
        public Vector3 LeftHand;
        public Quaternion LeftRotation;
        public Vector3 RightHand;
        public Quaternion RightRotation;
        public float EyeHeight;
        public bool Grounded, InVR;
        public int Fps;

        public void Write(BinaryWriter w)
        {
            V(w, Feet); w.Write(Yaw); V(w, Velocity);
            V(w, Head); Q(w, HeadRotation);
            V(w, LeftHand); Q(w, LeftRotation);
            V(w, RightHand); Q(w, RightRotation);
            w.Write(EyeHeight);
            w.Write((byte)((Grounded ? 1 : 0) | (InVR ? 2 : 0)));
            w.Write((short)Mathf.Clamp(Fps, 0, short.MaxValue));
        }

        public static SimulatedPose Read(BinaryReader r)
        {
            var p = new SimulatedPose
            {
                Feet = V(r), Yaw = r.ReadSingle(), Velocity = V(r),
                Head = V(r), HeadRotation = Q(r),
                LeftHand = V(r), LeftRotation = Q(r),
                RightHand = V(r), RightRotation = Q(r),
                EyeHeight = r.ReadSingle()
            };
            byte flags = r.ReadByte();
            p.Grounded = (flags & 1) != 0;
            p.InVR = (flags & 2) != 0;
            p.Fps = r.ReadInt16();
            return p;
        }

        private static void V(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static void Q(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        private static Vector3 V(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static Quaternion Q(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    /// <summary>The frames clients and the relay exchange: a length, a type, a payload.</summary>
    internal static class SimulatedWire
    {
        public const string Magic = "PVR-VOLT-MC";
        public const int Protocol = 1;
        public const int MaxFrame = 1 << 20;

        // client -> relay
        public const byte Hello = 1, Data = 2, Pose = 3;
        // relay -> client
        public const byte Welcome = 10, Joined = 11, Left = 12, DataFrom = 13, PoseFrom = 14, Rejected = 15;

        public static byte[] Frame(byte type, Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                w.Write(0);
                w.Write(type);
                body?.Invoke(w);
                w.Flush();
                byte[] frame = stream.ToArray();
                int length = frame.Length - 4;
                frame[0] = (byte)length;
                frame[1] = (byte)(length >> 8);
                frame[2] = (byte)(length >> 16);
                frame[3] = (byte)(length >> 24);
                return frame;
            }
        }

        /// <summary>Reads one frame; null when the connection closed.</summary>
        public static byte[] ReadFrame(Stream stream)
        {
            var header = new byte[4];
            if (!Fill(stream, header, 4)) return null;
            int length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length < 1 || length > MaxFrame) throw new IOException("Bad frame length " + length + ".");
            var frame = new byte[length];
            return Fill(stream, frame, length) ? frame : null;
        }

        private static bool Fill(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }

    /// <summary>
    /// The meeting point: hands out player ids and passes messages between clients. It runs on background threads
    /// of whichever client bound the port first and knows nothing about worlds or scripts.
    /// </summary>
    internal sealed class SimulatedRelay : IDisposable
    {
        private sealed class Peer
        {
            public int Id;
            public string Name;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
        }

        private readonly TcpListener _listener;
        private readonly List<Peer> _peers = new List<Peer>();
        private readonly int _epoch = Environment.TickCount;
        private string _moduleHash;
        private int _nextId = 1;
        private volatile bool _closed;

        private SimulatedRelay(TcpListener listener)
        {
            _listener = listener;
            new Thread(AcceptLoop) { IsBackground = true, Name = "Volt multi-client relay" }.Start();
        }

        /// <summary>Starts the relay on the port, or returns null when another client already has it.</summary>
        public static SimulatedRelay TryStart(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
            }
            catch (SocketException)
            {
                return null;
            }
            return new SimulatedRelay(listener);
        }

        private void AcceptLoop()
        {
            while (!_closed)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                client.NoDelay = true;
                new Thread(() => Serve(client)) { IsBackground = true, Name = "Volt multi-client peer" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            Peer peer = null;
            try
            {
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 5000;
                byte[] hello = SimulatedWire.ReadFrame(stream);
                if (hello == null || hello[0] != SimulatedWire.Hello) return;
                string name, hash;
                using (var r = new BinaryReader(new MemoryStream(hello, 1, hello.Length - 1), Encoding.UTF8))
                {
                    if (r.ReadString() != SimulatedWire.Magic || r.ReadInt32() != SimulatedWire.Protocol)
                    {
                        Write(stream, null, SimulatedWire.Frame(SimulatedWire.Rejected, w => w.Write("The other client runs a different ParelVR SDK version.")));
                        return;
                    }
                    name = r.ReadString();
                    hash = r.ReadString();
                }
                stream.ReadTimeout = Timeout.Infinite;

                Peer[] others;
                string firstHash;
                lock (_peers)
                {
                    peer = new Peer { Id = _nextId++, Client = client, Stream = stream };
                    peer.Name = string.IsNullOrEmpty(name) ? "Player " + peer.Id : name;
                    others = _peers.ToArray();
                    _peers.Add(peer);
                    if (_moduleHash == null) _moduleHash = hash ?? string.Empty;
                    firstHash = _moduleHash;
                }

                Peer self = peer;
                Write(stream, peer.WriteLock, SimulatedWire.Frame(SimulatedWire.Welcome, w =>
                {
                    w.Write(self.Id);
                    w.Write(_epoch);
                    w.Write(firstHash);
                    w.Write(others.Length);
                    foreach (Peer other in others)
                    {
                        w.Write(other.Id);
                        w.Write(other.Name);
                    }
                }));
                Broadcast(peer, SimulatedWire.Frame(SimulatedWire.Joined, w => { w.Write(self.Id); w.Write(self.Name); }));

                while (!_closed)
                {
                    byte[] frame = SimulatedWire.ReadFrame(stream);
                    if (frame == null) break;
                    switch (frame[0])
                    {
                        case SimulatedWire.Data:
                        {
                            int target = BitConverter.ToInt32(frame, 1);
                            // frame: type, target, reliable, payload
                            int payload = frame.Length - 6;
                            byte[] forward = SimulatedWire.Frame(SimulatedWire.DataFrom, w =>
                            {
                                w.Write(self.Id);
                                w.Write(payload);
                                w.Write(frame, 6, payload);
                            });
                            if (target == 0) Broadcast(peer, forward);
                            else SendTo(target, forward);
                            break;
                        }
                        case SimulatedWire.Pose:
                        {
                            byte[] forward = SimulatedWire.Frame(SimulatedWire.PoseFrom, w =>
                            {
                                w.Write(self.Id);
                                w.Write(frame, 1, frame.Length - 1);
                            });
                            Broadcast(peer, forward);
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A client that goes away mid-frame is simply gone.
            }
            finally
            {
                if (peer != null)
                {
                    bool removed;
                    lock (_peers) removed = _peers.Remove(peer);
                    int id = peer.Id;
                    if (removed && !_closed) Broadcast(null, SimulatedWire.Frame(SimulatedWire.Left, w => w.Write(id)));
                }
                try { client.Close(); } catch (Exception) { }
            }
        }

        private void Broadcast(Peer except, byte[] frame)
        {
            Peer[] peers;
            lock (_peers) peers = _peers.ToArray();
            foreach (Peer p in peers)
                if (p != except) Write(p.Stream, p.WriteLock, frame);
        }

        private void SendTo(int id, byte[] frame)
        {
            Peer target = null;
            lock (_peers)
                foreach (Peer p in _peers)
                    if (p.Id == id) { target = p; break; }
            if (target != null) Write(target.Stream, target.WriteLock, frame);
        }

        private static void Write(NetworkStream stream, object gate, byte[] frame)
        {
            try
            {
                if (gate == null) stream.Write(frame, 0, frame.Length);
                else lock (gate) stream.Write(frame, 0, frame.Length);
            }
            catch (Exception)
            {
                // Its reader notices and removes it.
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _listener.Stop(); } catch (Exception) { }
            lock (_peers)
            {
                foreach (Peer p in _peers)
                    try { p.Client.Close(); } catch (Exception) { }
                _peers.Clear();
            }
        }
    }

    /// <summary>
    /// One client's connection to the relay, and the world's transport. Messages arrive on a background thread
    /// and are handed to the world on the main thread by <see cref="Poll"/>, after the simulated delay.
    /// </summary>
    internal sealed class SimulatedNetwork : IVoltNetTransport, IDisposable
    {
        public enum Kind { Joined, Left, Data, Pose, Lost }

        public struct Incoming
        {
            public Kind Kind;
            public int Player;
            public string Name;
            public byte[] Data;
            public SimulatedPose Pose;
            public double Due;
        }

        private readonly VoltMultiClientOptions _options;
        private readonly ConcurrentQueue<Incoming> _queue = new ConcurrentQueue<Incoming>();
        private readonly List<Incoming> _waiting = new List<Incoming>();
        private readonly HashSet<int> _present = new HashSet<int>();
        private readonly object _writeLock = new object();
        private readonly System.Random _loss = new System.Random();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private SimulatedRelay _relay;
        private TcpClient _client;
        private NetworkStream _stream;
        private int _epoch;
        private volatile bool _connected;

        public int LocalId { get; private set; }
        public bool IsRelayHost => _relay != null;
        /// <summary>Players that were in the instance before this client joined: id and name.</summary>
        public readonly List<KeyValuePair<int, string>> Existing = new List<KeyValuePair<int, string>>();
        public string RemoteModuleHash { get; private set; }

        private SimulatedNetwork(VoltMultiClientOptions options) { _options = options; }

        /// <summary>Joins (or opens) the meeting point. Throws when no connection could be made.</summary>
        public static SimulatedNetwork Connect(VoltMultiClientOptions options, string name)
        {
            var network = new SimulatedNetwork(options);
            try
            {
                network.Open(name);
                return network;
            }
            catch
            {
                network.Dispose();
                throw;
            }
        }

        private void Open(string name)
        {
            _relay = SimulatedRelay.TryStart(_options.Port);
            _client = new TcpClient { NoDelay = true };
            IAsyncResult connecting = _client.BeginConnect(IPAddress.Loopback, _options.Port, null, null);
            if (!connecting.AsyncWaitHandle.WaitOne(3000) || !_client.Connected)
                throw new IOException("Nothing answered on port " + _options.Port + ".");
            _client.EndConnect(connecting);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 5000;
            Send(SimulatedWire.Frame(SimulatedWire.Hello, w =>
            {
                w.Write(SimulatedWire.Magic);
                w.Write(SimulatedWire.Protocol);
                w.Write(name ?? string.Empty);
                w.Write(_options.ModuleHash ?? string.Empty);
            }));
            byte[] welcome = SimulatedWire.ReadFrame(_stream);
            if (welcome == null) throw new IOException("The relay closed the connection.");
            using (var r = new BinaryReader(new MemoryStream(welcome, 1, welcome.Length - 1), Encoding.UTF8))
            {
                if (welcome[0] == SimulatedWire.Rejected) throw new IOException(r.ReadString());
                if (welcome[0] != SimulatedWire.Welcome) throw new IOException("Something else is listening on port " + _options.Port + ".");
                LocalId = r.ReadInt32();
                _epoch = r.ReadInt32();
                RemoteModuleHash = r.ReadString();
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int id = r.ReadInt32();
                    Existing.Add(new KeyValuePair<int, string>(id, r.ReadString()));
                    _present.Add(id);
                }
            }
            _stream.ReadTimeout = Timeout.Infinite;
            _connected = true;
            new Thread(ReadLoop) { IsBackground = true, Name = "Volt multi-client" }.Start();
        }

        private void ReadLoop()
        {
            try
            {
                while (_connected)
                {
                    byte[] frame = SimulatedWire.ReadFrame(_stream);
                    if (frame == null) break;
                    using (var r = new BinaryReader(new MemoryStream(frame, 1, frame.Length - 1), Encoding.UTF8))
                    {
                        switch (frame[0])
                        {
                            case SimulatedWire.Joined: Enqueue(new Incoming { Kind = Kind.Joined, Player = r.ReadInt32(), Name = r.ReadString() }); break;
                            case SimulatedWire.Left: Enqueue(new Incoming { Kind = Kind.Left, Player = r.ReadInt32() }); break;
                            case SimulatedWire.DataFrom:
                            {
                                int sender = r.ReadInt32();
                                int length = r.ReadInt32();
                                Enqueue(new Incoming { Kind = Kind.Data, Player = sender, Data = r.ReadBytes(length) });
                                break;
                            }
                            case SimulatedWire.PoseFrom:
                            {
                                int sender = r.ReadInt32();
                                Enqueue(new Incoming { Kind = Kind.Pose, Player = sender, Pose = SimulatedPose.Read(r) });
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Closed by Dispose, or the relay went away.
            }
            if (_connected) Enqueue(new Incoming { Kind = Kind.Lost });
            _connected = false;
        }

        private void Enqueue(Incoming item)
        {
            // Joins and leaves keep their place among the messages: everything is delayed alike.
            item.Due = _clock.Elapsed.TotalSeconds + Mathf.Max(0, _options.LatencyMs) / 1000.0;
            _queue.Enqueue(item);
        }

        /// <summary>Hands over everything whose delay has passed, in the order it arrived. Main thread.</summary>
        public void Poll(Action<Incoming> handle)
        {
            while (_queue.TryDequeue(out Incoming item)) _waiting.Add(item);
            double now = _clock.Elapsed.TotalSeconds;
            int done = 0;
            for (; done < _waiting.Count; done++)
            {
                Incoming item = _waiting[done];
                if (item.Due > now) break;
                if (item.Kind == Kind.Joined) _present.Add(item.Player);
                else if (item.Kind == Kind.Left) _present.Remove(item.Player);
                else if (item.Kind == Kind.Lost) _present.Clear();
                handle(item);
            }
            if (done > 0) _waiting.RemoveRange(0, done);
        }

        private void Send(byte[] frame)
        {
            NetworkStream stream = _stream;
            if (stream == null) return;
            try
            {
                lock (_writeLock) stream.Write(frame, 0, frame.Length);
            }
            catch (Exception)
            {
                // The read loop reports the lost connection.
            }
        }

        public void SendPose(in SimulatedPose pose)
        {
            if (!_connected) return;
            SimulatedPose copy = pose;
            Send(SimulatedWire.Frame(SimulatedWire.Pose, w => copy.Write(w)));
        }

        private void SendData(int target, byte[] data, int length, bool reliable)
        {
            if (!_connected || data == null || length <= 0) return;
            if (!reliable && _options.LossPercent > 0f && _loss.NextDouble() * 100.0 < Mathf.Min(_options.LossPercent, 50f)) return;
            Send(SimulatedWire.Frame(SimulatedWire.Data, w =>
            {
                w.Write(target);
                w.Write(reliable ? (byte)1 : (byte)0);
                w.Write(data, 0, length);
            }));
        }

        // ── the world's transport ────────────────────────────────────────────

        public bool IsConnected => true; // offline after a lost connection still runs the world alone
        public int LocalPlayerId => LocalId;
        public int MasterPlayerId
        {
            get
            {
                int master = LocalId;
                foreach (int id in _present)
                    if (id < master) master = id;
                return master;
            }
        }
        public int ServerTimeMs => unchecked(Environment.TickCount - _epoch);
        public int PingMs => Mathf.Max(0, _options.LatencyMs) * 2;
        public bool Online => _connected;
        public int PlayerCount => _present.Count + 1;

        public void SendToOthers(byte[] data, int length, bool reliable) => SendData(0, data, length, reliable);

        public void SendToPlayer(int playerId, byte[] data, int length, bool reliable)
        {
            if (playerId != LocalId && _present.Contains(playerId)) SendData(playerId, data, length, reliable);
        }

        public void Dispose()
        {
            _connected = false;
            try { _client?.Close(); } catch (Exception) { }
            _stream = null;
            _relay?.Dispose();
            _relay = null;
        }
    }
}
