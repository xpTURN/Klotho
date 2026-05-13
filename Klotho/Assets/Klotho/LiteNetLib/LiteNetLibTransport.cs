using System;

using LiteNetLib;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using ZLogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using DeliveryMethod = xpTURN.Klotho.Network.DeliveryMethod;
using DisconnectReason = xpTURN.Klotho.Network.DisconnectReason;

namespace xpTURN.Klotho.LiteNetLib
{
    /// <summary>
    /// INetworkTransport implementation. Handles UDP-based network transport.
    /// </summary>
    public class LiteNetLibTransport : INetworkTransport, ILiteNetEventListener
    {
        ILogger _logger;
        LiteNetManager _netManager;
        readonly LiteNetLibPeerMap _peerMap;
        bool _isConnected;
        int _localPeerId;
        bool _isServer;
        public const string DefaultConnectionKey = "xpTURN.Klotho";
        string _connectionKey;
        int _maxConnections;

#if KLOTHO_FAULT_INJECTION
        // RTT emulation. Each transport instance delays its incoming dispatch by rtt/2 so a
        // round-trip across two transports totals EmulatedRttMs.
        readonly System.Collections.Generic.List<(long releaseAtMs, int peerId, byte[] data, int length)> _delayedRecvMessages
            = new System.Collections.Generic.List<(long, int, byte[], int)>();
        bool _rttLogged;
#endif

        public LiteNetLibTransport(ILogger logger, NetLogLevel[] levels = null, string connectionKey = DefaultConnectionKey)
        {
            _logger = logger;
            if (string.IsNullOrEmpty(connectionKey))
            {
                logger?.ZLogWarning($"[LiteNetLibTransport] connectionKey null/empty — falling back to DefaultConnectionKey");
                _connectionKey = DefaultConnectionKey;
            }
            else
            {
                _connectionKey = connectionKey;
            }
            NetDebug.Logger ??= new LiteNetLibNetLogger(logger, levels);
            _peerMap = new LiteNetLibPeerMap(logger);
        }

        public bool IsServer => _isServer;
        public bool IsConnected => _isConnected;

        public int LocalPeerId => _localPeerId;

        public string RemoteAddress { get; private set; }
        public int RemotePort { get; private set; }

        // INetworkTransport events
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;
        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;

        private bool _useIPv6 = false;

        // INetworkTransport methods
        public bool Listen(string address, int port, int maxConnections)
        {
            _maxConnections = maxConnections;
            string resolvedIp = IPv6Helper.Resolve(address);
            _useIPv6 = IPv6Helper.IsIPv6(resolvedIp);
            _isServer = true;
            _localPeerId = 0;
            _netManager = new LiteNetManager(this);
            _netManager.IPv6Enabled = _useIPv6;
            if (!_netManager.Start(port))
            {
                _logger?.ZLogError($"[LiteNetLibTransport] Server start failed — unable to bind port {port} (already in use?)");
                return false;
            }
            _logger?.ZLogInformation($"[LiteNetLibTransport] Server listening: port {port}");
            return true;
        }

        public bool Connect(string address, int port)
        {
            RemoteAddress = address;
            RemotePort = port;
            string resolvedIp = IPv6Helper.Resolve(address);
            _useIPv6 = IPv6Helper.IsIPv6(resolvedIp);
            _isServer = false;
            // Tear down any previous _netManager so its background _logicThread and UDP socket
            // are released. Without this, retried Connect calls (reconnect state machine, ~1s
            // interval while IsConnected stays false) leak threads/sockets and the abandoned
            // managers keep retransmitting Connection Requests in the background — the server
            // ends up accepting multiple sockets from the same client (zombie peers).
            _netManager?.Stop();
            _netManager = new LiteNetManager(this);
            _netManager.IPv6Enabled = _useIPv6;
            if (!_netManager.Start())
            {
                _logger?.ZLogError($"[LiteNetLibTransport] Client socket start failed");
                return false;
            }
            _netManager.Connect(resolvedIp, port, _connectionKey);
            _logger?.ZLogInformation($"[LiteNetLibTransport] Connecting: {address}:{port}");
            return true;
        }

        public void Disconnect()
        {
            bool wasConnected = _isConnected;
            _netManager?.Stop();
            _netManager = null;
            _peerMap.Clear();
            _isConnected = false;

#if KLOTHO_FAULT_INJECTION
            _delayedRecvMessages.Clear();
            _rttLogged = false;
#endif

            if (!_isServer && wasConnected)
                OnDisconnected?.Invoke(DisconnectReason.LocalDisconnect);
        }

        public void DisconnectPeer(int peerId)
        {
            if (_peerMap.TryGetPeer(peerId, out var peer))
                peer.Disconnect();
        }

        public System.Collections.Generic.IEnumerable<int> GetConnectedPeerIds() => _peerMap.GetAllPeerIds();

        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod)
        {
            if (!_peerMap.TryGetPeer(peerId, out var peer))
                return;
            peer.Send(data, ToLiteDelivery(deliveryMethod));
        }

        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            if (!_peerMap.TryGetPeer(peerId, out var peer))
                return;
            peer.Send(data, 0, length, ToLiteDelivery(deliveryMethod));
        }

        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod)
        {
            // Client → Host → all clients (relay)
            _netManager?.SendToAll(data, ToLiteDelivery(deliveryMethod));
        }

        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            _netManager?.SendToAll(data, 0, length, ToLiteDelivery(deliveryMethod));
        }

        public void PollEvents()
        {
            _netManager?.PollEvents();

#if KLOTHO_FAULT_INJECTION
            if (_delayedRecvMessages.Count > 0)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int write = 0;
                for (int read = 0; read < _delayedRecvMessages.Count; read++)
                {
                    var entry = _delayedRecvMessages[read];
                    if (entry.releaseAtMs <= now)
                        OnDataReceived?.Invoke(entry.peerId, entry.data, entry.length);
                    else
                        _delayedRecvMessages[write++] = entry;
                }
                if (write != _delayedRecvMessages.Count)
                    _delayedRecvMessages.RemoveRange(write, _delayedRecvMessages.Count - write);
            }
#endif
        }

        public void FlushSendQueue()
        {
            _netManager?.TriggerUpdate();
        }

        // ILiteNetEventListener implementation
        void ILiteNetEventListener.OnPeerConnected(LiteNetPeer peer)
        {
            int peerId = _peerMap.Register(peer);
            if (peerId < 0)
                return;

            if (!_isServer)
            {
                _localPeerId = peerId;
                _isConnected = true;
                OnConnected?.Invoke();
            }

            OnPeerConnected?.Invoke(peerId);
        }

        void ILiteNetEventListener.OnPeerDisconnected(LiteNetPeer peer, DisconnectInfo disconnectInfo)
        {
            _logger?.ZLogWarning($"[LiteNetLibTransport] Peer disconnected — reason: {disconnectInfo.Reason}, socketError: {disconnectInfo.SocketErrorCode}");

            if (_peerMap.TryGetId(peer, out int peerId))
            {
                _peerMap.Unregister(peer);
                OnPeerDisconnected?.Invoke(peerId);
            }

            if (!_isServer)
            {
                _isConnected = false;
                OnDisconnected?.Invoke(MapReason(disconnectInfo.Reason));
            }
        }

        void ILiteNetEventListener.OnNetworkReceive(LiteNetPeer peer, NetPacketReader reader, global::LiteNetLib.DeliveryMethod deliveryMethod)
        {
            if (!_peerMap.TryGetId(peer, out int peerId))
                return;

            int length = reader.AvailableBytes;
            
#if KLOTHO_FAULT_INJECTION
            int rtt = xpTURN.Klotho.Diagnostics.FaultInjection.EmulatedRttMs;
            if (rtt > 0)
            {
                if (!_rttLogged)
                {
                    _rttLogged = true;
                    _logger?.ZLogWarning($"[FaultInjection] LiteNetLibTransport: RTT emulation active ({rtt}ms round-trip)");
                }
                // Pooled buffer cannot survive across PollEvents — allocate dedicated copy for the delay queue.
                byte[] copy = new byte[length];
                reader.GetBytes(copy, length);
                reader.Recycle();
                long releaseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (rtt / 2);
                _delayedRecvMessages.Add((releaseAt, peerId, copy, length));
                return;
            }
#endif
            byte[] data = StreamPool.GetBuffer(length);
            reader.GetBytes(data, length);
            reader.Recycle();

            OnDataReceived?.Invoke(peerId, data, length);

            StreamPool.ReturnBuffer(data);
        }

        void ILiteNetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (_isServer)
            {
                if (_maxConnections > 0 && _netManager.ConnectedPeersCount >= _maxConnections)
                {
                    _logger?.ZLogWarning($"[LiteNetLibTransport] Connection rejected: {request.RemoteEndPoint} — max connections ({_maxConnections}) reached");
                    request.Reject();
                    return;
                }

                var accepted = request.AcceptIfKey(_connectionKey);
                if (accepted != null)
                    _logger?.ZLogInformation($"[LiteNetLibTransport] Connection accepted: {request.RemoteEndPoint}");
                else
                    _logger?.ZLogWarning($"[LiteNetLibTransport] Connection rejected: {request.RemoteEndPoint} — key mismatch or already used");
            }
            else
            {
                _logger?.ZLogWarning($"[LiteNetLibTransport] OnConnectionRequest called on client — unexpected. Rejecting.");
                request.Reject();
            }
        }

        void ILiteNetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            _logger?.ZLogError($"[LiteNetLibTransport] Network error: {endPoint}: {socketError}");
        }

        static global::LiteNetLib.DeliveryMethod ToLiteDelivery(DeliveryMethod method) => method switch
        {
            DeliveryMethod.Unreliable => global::LiteNetLib.DeliveryMethod.Unreliable,
            DeliveryMethod.Reliable => global::LiteNetLib.DeliveryMethod.ReliableUnordered,
            DeliveryMethod.ReliableOrdered => global::LiteNetLib.DeliveryMethod.ReliableOrdered,
            DeliveryMethod.Sequenced => global::LiteNetLib.DeliveryMethod.Sequenced,
            _ => global::LiteNetLib.DeliveryMethod.ReliableOrdered,
        };

        static DisconnectReason MapReason(global::LiteNetLib.DisconnectReason reason) => reason switch
        {
            global::LiteNetLib.DisconnectReason.DisconnectPeerCalled   => DisconnectReason.LocalDisconnect,
            global::LiteNetLib.DisconnectReason.RemoteConnectionClose  => DisconnectReason.RemoteDisconnect,
            global::LiteNetLib.DisconnectReason.ConnectionRejected     => DisconnectReason.ConnectionRejected,
            global::LiteNetLib.DisconnectReason.InvalidProtocol        => DisconnectReason.ConnectionRejected,
            global::LiteNetLib.DisconnectReason.ConnectionFailed       => DisconnectReason.NetworkFailure,
            global::LiteNetLib.DisconnectReason.Timeout                => DisconnectReason.NetworkFailure,
            global::LiteNetLib.DisconnectReason.HostUnreachable        => DisconnectReason.NetworkFailure,
            global::LiteNetLib.DisconnectReason.NetworkUnreachable     => DisconnectReason.NetworkFailure,
            global::LiteNetLib.DisconnectReason.UnknownHost            => DisconnectReason.NetworkFailure,
            global::LiteNetLib.DisconnectReason.Reconnect              => DisconnectReason.ReconnectRequested,
            _                                                          => DisconnectReason.Unknown,
        };
    }
}
