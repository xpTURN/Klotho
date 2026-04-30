using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Wrapper that isolates INetworkTransport per room on a multi-room server.
    /// Send/DisconnectPeer delegate directly to the shared Transport (thread-safe).
    /// PollEvents is a no-op — RoomRouter delivers receives into the inbound queue,
    /// and events are raised directly from DrainInboundQueue().
    /// </summary>
    public class RoomScopedTransport : INetworkTransport
    {
        private readonly INetworkTransport _shared;
        private readonly HashSet<int> _roomPeerIds = new HashSet<int>();
        private readonly object _peerLock = new object();

        public RoomScopedTransport(INetworkTransport sharedTransport)
        {
            _shared = sharedTransport ?? throw new ArgumentNullException(nameof(sharedTransport));
        }

        // ── Room peer management (called from RoomRouter/RoomManager) ──

        public void AddPeer(int peerId)
        {
            lock (_peerLock) _roomPeerIds.Add(peerId);
        }

        public void RemovePeer(int peerId)
        {
            lock (_peerLock) _roomPeerIds.Remove(peerId);
        }

        public bool ContainsPeer(int peerId)
        {
            lock (_peerLock) return _roomPeerIds.Contains(peerId);
        }

        public int PeerCount
        {
            get { lock (_peerLock) return _roomPeerIds.Count; }
        }

        // ── INetworkTransport properties ──

        public bool IsConnected => _shared.IsConnected;
        public int LocalPeerId => _shared.LocalPeerId;
        public string RemoteAddress => _shared.RemoteAddress;
        public int RemotePort => _shared.RemotePort;

        // ── Send: delegated directly to the shared Transport (peer.Send is thread-safe) ──

        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod)
        {
            _shared.Send(peerId, data, deliveryMethod);
        }

        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            _shared.Send(peerId, data, length, deliveryMethod);
        }

        /// <summary>
        /// Broadcast only to peers belonging to this room.
        /// Iterates room peers and sends individually instead of using the shared Transport's SendToAll.
        /// </summary>
        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod)
        {
            lock (_peerLock)
            {
                foreach (int peerId in _roomPeerIds)
                    _shared.Send(peerId, data, deliveryMethod);
            }
        }

        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            lock (_peerLock)
            {
                foreach (int peerId in _roomPeerIds)
                    _shared.Send(peerId, data, length, deliveryMethod);
            }
        }

        public void DisconnectPeer(int peerId)
        {
            _shared.DisconnectPeer(peerId);
        }

        // ── no-op: receive/connection lifetime is managed by RoomRouter/RoomManager ──

        public void PollEvents() { /* no-op */ }
        public void FlushSendQueue() { /* no-op — ServerLoop calls directly on the shared Transport */ }
        public bool Listen(string address, int port, int maxConnections) => true; /* no-op */
        public bool Connect(string address, int port) => true; /* no-op */
        public void Disconnect() { /* no-op */ }

        // ── Events: raised directly from DrainInboundQueue() ──

        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
#pragma warning disable CS0067
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;
#pragma warning restore CS0067

        /// <summary>
        /// Forwards data dequeued from the inbound queue to ServerNetworkService.
        /// Called from Room.DrainInboundQueue().
        /// </summary>
        public void RaiseDataReceived(int peerId, byte[] data, int length)
        {
            OnDataReceived?.Invoke(peerId, data, length);
        }

        public void RaisePeerConnected(int peerId)
        {
            OnPeerConnected?.Invoke(peerId);
        }

        public void RaisePeerDisconnected(int peerId)
        {
            OnPeerDisconnected?.Invoke(peerId);
        }
    }
}
