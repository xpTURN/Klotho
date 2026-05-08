using System;
using System.Collections.Generic;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Network transport layer for local testing.
    /// Allows testing the Klotho system without a real network.
    /// </summary>
    public class TestTransport : INetworkTransport
    {
        private static TestTransport _hostInstance;
        private static Dictionary<int, TestTransport> _peers = new Dictionary<int, TestTransport>();
        private static int _nextPeerId = 1;
        private static HashSet<int> _blockedPeers = new HashSet<int>();

        private Queue<(int peerId, byte[] data)> _incomingMessages = new Queue<(int, byte[])>();
#if KLOTHO_FAULT_INJECTION
        // RTT emulation: messages held until releaseAtMs has elapsed.
        private List<(long releaseAtMs, int peerId, byte[] data)> _delayedMessages = new List<(long, int, byte[])>();
        private static bool _rttLogged;
#endif
        private bool _isHost;
        private bool _isConnected;
        private int _peerId;

        public bool IsConnected => _isConnected;
        public int LocalPeerId => _peerId;
        public bool IsHost => _isHost;
        public string RemoteAddress { get; private set; }
        public int RemotePort { get; private set; }

        /// <summary>
        /// Number of times DisconnectPeer has been called (test instrumentation).
        /// </summary>
        public int DisconnectPeerCallCount { get; private set; }

        // INetworkTransport events
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;
        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;

        public bool Connect(string address, int port)
        {
            _isHost = false;
            _peerId = _nextPeerId++;
            _peers[_peerId] = this;
            _isConnected = true;

            // Notify host of connection
            _hostInstance?.NotifyPeerConnected(_peerId);

            OnConnected?.Invoke();
            return true;
        }

        /// <summary>
        /// Start as host (test extension method).
        /// </summary>
        public bool Listen(string address, int port, int maxConnections)
        {
            _isHost = true;
            _peerId = 0;
            _hostInstance = this;
            _isConnected = true;
            OnConnected?.Invoke();
            return true;
        }

        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod)
        {
            // Ignore DeliveryMethod in local tests (always reliable)
            if (peerId == 0 && _hostInstance != null)
            {
                _hostInstance.ReceiveMessage(_peerId, data);
            }
            else if (_peers.TryGetValue(peerId, out var target))
            {
                target.ReceiveMessage(_peerId, data);
            }
        }

        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            byte[] exact = new byte[length];
            Array.Copy(data, exact, length);
            Send(peerId, exact, deliveryMethod);
        }

        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod)
        {
            if (_isHost)
            {
                // Host → distribute directly to all clients (excluding self)
                foreach (var kvp in _peers)
                    kvp.Value.ReceiveMessage(_peerId, data);
            }
            else
            {
                // Client → forward to Host (Host relays)
                _hostInstance?.ReceiveAndBroadcast(_peerId, data);
            }
        }

        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            byte[] exact = new byte[length];
            Array.Copy(data, exact, length);
            Broadcast(exact, deliveryMethod);
        }

        public void PollEvents()
        {
#if KLOTHO_FAULT_INJECTION
            if (_delayedMessages.Count > 0)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int write = 0;
                for (int read = 0; read < _delayedMessages.Count; read++)
                {
                    var entry = _delayedMessages[read];
                    if (entry.releaseAtMs <= now)
                        _incomingMessages.Enqueue((entry.peerId, entry.data));
                    else
                        _delayedMessages[write++] = entry;
                }
                if (write != _delayedMessages.Count)
                    _delayedMessages.RemoveRange(write, _delayedMessages.Count - write);
            }
#endif
            while (_incomingMessages.Count > 0)
            {
                var (peerId, data) = _incomingMessages.Dequeue();
                OnDataReceived?.Invoke(peerId, data, data.Length);
            }
        }

        public void FlushSendQueue() { }

        public void DisconnectPeer(int peerId)
        {
            DisconnectPeerCallCount++;
            if (!_isHost) return;
            if (_peers.TryGetValue(peerId, out var client))
            {
                client._isConnected = false;
                // NetworkFailure (not RemoteDisconnect) — simulates client-side detection of connection loss,
                // matching the test intent of triggering the client's reconnect flow.
                client.OnDisconnected?.Invoke(DisconnectReason.NetworkFailure);
                _peers.Remove(peerId);
                NotifyPeerDisconnected(peerId);
            }
        }

        public void Disconnect()
        {
            _isConnected = false;
            if (_isHost)
            {
                // Notify all clients of disconnection
                foreach (var kvp in _peers)
                {
                    kvp.Value.OnPeerDisconnected?.Invoke(0);
                }
                _hostInstance = null;
                _peers.Clear();
            }
            else
            {
                _peers.Remove(_peerId);
                // Notify host of disconnection
                _hostInstance?.NotifyPeerDisconnected(_peerId);
            }
            // NetworkFailure (not LocalDisconnect) — test fault-injection convention.
            // Tests use Disconnect() to simulate client-side connection loss and expect the
            // reconnect flow to engage. For genuine local-leave semantics, use SimulateDisconnect(LocalDisconnect).
            OnDisconnected?.Invoke(DisconnectReason.NetworkFailure);
        }

        /// <summary>
        /// Test helper: simulate a disconnect with the given reason (fault injection).
        /// </summary>
        public void SimulateDisconnect(DisconnectReason reason)
        {
            _isConnected = false;
            OnDisconnected?.Invoke(reason);
        }

        /// <summary>
        /// Packet drop probability (0.0 ~ 1.0). 0 means no drops.
        /// Simulates unreliable transport: messages destined for this transport are randomly discarded.
        /// </summary>
        public float PacketDropRate { get; set; }

        private static Random _dropRng = new Random(42);

        /// <summary>
        /// Resets the packet drop RNG seed (ensures test reproducibility).
        /// </summary>
        public static void ResetDropRng(int seed = 42) => _dropRng = new Random(seed);

        private void ReceiveMessage(int fromPeerId, byte[] data)
        {
            if (_blockedPeers.Contains(fromPeerId) || _blockedPeers.Contains(_peerId))
                return;
            if (PacketDropRate > 0f && _dropRng.NextDouble() < PacketDropRate)
                return; // Packet dropped
#if KLOTHO_FAULT_INJECTION
            int rtt = xpTURN.Klotho.Diagnostics.FaultInjection.EmulatedRttMs;
            if (rtt > 0)
            {
                if (!_rttLogged)
                {
                    _rttLogged = true;
                    System.Console.WriteLine($"[FaultInjection] TestTransport: RTT emulation active ({rtt}ms round-trip)");
                }
                long releaseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (rtt / 2);
                _delayedMessages.Add((releaseAt, fromPeerId, data));
                return;
            }
#endif
            _incomingMessages.Enqueue((fromPeerId, data));
        }

        private void ReceiveAndBroadcast(int fromPeerId, byte[] data)
        {
            // Host also receives
            if (!_blockedPeers.Contains(fromPeerId) && !_blockedPeers.Contains(_peerId))
                _incomingMessages.Enqueue((fromPeerId, data));

            // Forward to other clients (excluding sender)
            foreach (var kvp in _peers)
            {
                if (kvp.Key != fromPeerId)
                {
                    kvp.Value.ReceiveMessage(fromPeerId, data);
                }
            }
        }

        private void NotifyPeerConnected(int peerId)
        {
            OnPeerConnected?.Invoke(peerId);
        }

        private void NotifyPeerDisconnected(int peerId)
        {
            OnPeerDisconnected?.Invoke(peerId);
        }

        /// <summary>
        /// Blocks message send/receive for a specific peer (for fault injection).
        /// </summary>
        public static void BlockPeer(int peerId) => _blockedPeers.Add(peerId);

        /// <summary>
        /// Unblocks message send/receive for a specific peer.
        /// </summary>
        public static void UnblockPeer(int peerId) => _blockedPeers.Remove(peerId);

        /// <summary>
        /// Resets all instances (for tests).
        /// </summary>
        public static void Reset()
        {
            _hostInstance = null;
            _peers.Clear();
            _nextPeerId = 1;
            _blockedPeers.Clear();
#if KLOTHO_FAULT_INJECTION
            _rttLogged = false;
#endif
        }

        /// <summary>
        /// Number of connected peers (host only).
        /// </summary>
        public int PeerCount => _isHost ? _peers.Count : 0;
    }
}
