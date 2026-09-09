using System;
using xpTURN.Klotho.Logging;

using LiteNetLib;

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
        IKLogger _logger;
        LiteNetManager _netManager;
        // Serializes manager teardown. LiteNetManager.Stop() is check-then-act (if(!_isRunning) return;)
        // with no internal lock, and the manager can be stopped from two threads at once (e.g. a server
        // loop's Run() finally on its loop thread + a caller's Disconnect() on another). Without this
        // lock the two stops interleave, one skips the thread-join, and the manager's native receive
        // thread is orphaned (spins at high CPU forever).
        readonly object _stopLock = new object();
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

        public LiteNetLibTransport(IKLogger logger, NetLogLevel[] levels = null, string connectionKey = DefaultConnectionKey)
        {
            _logger = logger;
            if (string.IsNullOrEmpty(connectionKey))
            {
                logger?.KWarning($"[LiteNetLibTransport] connectionKey null/empty — falling back to DefaultConnectionKey");
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

        // Payload byte from the most recent remote disconnect (-1 = none). Set just before OnDisconnected.
        private int _lastDisconnectPayload = -1;
        public int LastDisconnectPayload => _lastDisconnectPayload;

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
            // Tear down any previous manager before binding. Listen() is a per-session entry point
            // (KlothoSessionFlow.StartHostAndListen) and the session driver keeps one transport across
            // sessions, so hosting a second time lands here with the first manager still running.
            // Overwriting the field instead of stopping it left that manager's receive/logic threads
            // and its bound socket with no reference that could ever stop them — the new Start(port)
            // then failed with AddressAlreadyInUse, and every later attempt failed the same way.
            // The stopped manager's peer registrations are dead too, so clear them for the new one.
            //
            // Stop() runs INSIDE the lock here — the one place that departs from the rule Connect()
            // and Disconnect() follow. Two reasons it has to. The port is fixed, so the old socket
            // must be released before the new bind, which rules out their start-new-then-stop-old
            // order; and doing it in two lock regions would leave the field null in between, where a
            // Disconnect() would see null, no-op, and be silently undone by the manager this call
            // then installs. Affordable because Listen() is a session-lifecycle call, not a hot path.
            // This holds only while events stay synced: turning UnsyncedEvents/UnsyncedReceiveEvent on
            // would let the receive thread reach a handler that calls Disconnect(), which would then
            // block on this lock while Stop() waits to join that same thread. Revisit it if that
            // changes.
            LiteNetManager manager;
            lock (_stopLock)
            {
                _netManager?.Stop();
                _peerMap.Clear();

                // Publish only after a successful Start: the field must never hold a manager that is
                // not running, or Broadcast/PollEvents would silently drive a dead one.
                manager = new LiteNetManager(this);
                manager.IPv6Enabled = _useIPv6;
                _netManager = manager.Start(port) ? manager : null;
            }

            if (_netManager == null)
            {
                _logger?.KError($"[LiteNetLibTransport] Server start failed — unable to bind port {port} (already in use?)");
                return false;
            }
            _logger?.KInformation($"[LiteNetLibTransport] Server listening: port {port}");
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
            //
            // Capture-then-null under the same lock Disconnect() uses so this stop can't race a
            // concurrent Disconnect() (which would double-Stop one manager and skip the thread-join).
            // The stopped manager's peer registrations are dead, so clear them under the lock too: a
            // fresh connection to the same endpoint must re-register its peer id — otherwise a
            // connection kept across a stop (no Disconnect) leaves a stale id and OnPeerConnected fails
            // to register the new peer.
            //
            // The replacement is built and STARTED under the same lock. Publishing an unstarted
            // manager and starting it afterwards leaves a window where a concurrent Disconnect()
            // takes the reference, finds _isRunning still false, no-ops, and nulls the field — then
            // this call finishes Start() and the now-running manager is referenced by nobody and can
            // never be stopped. Start() only binds a socket and creates threads (it joins nothing,
            // and those threads never enter listener code), so holding the lock across it is safe.
            LiteNetManager previous, fresh;
            bool started;
            lock (_stopLock)
            {
                previous = _netManager;
                _peerMap.Clear();
                _isConnected = false;

                fresh = new LiteNetManager(this);
                fresh.IPv6Enabled = _useIPv6;
                started = fresh.Start();
                _netManager = started ? fresh : null;
            }
            previous?.Stop();   // the join stays outside the lock

            if (!started)
            {
                _logger?.KError($"[LiteNetLibTransport] Client socket start failed");
                return false;
            }
            // Drive the local, not the field: a concurrent Disconnect() may have nulled it already,
            // and re-reading is how the NRE above gets in.
            fresh.Connect(resolvedIp, port, _connectionKey);
            _logger?.KTrace($"[LiteNetLibTransport] Connecting: {address}:{port}");
            return true;
        }

        public void Disconnect()
        {
            // Capture-then-null the manager under the lock so exactly ONE caller owns the reference and
            // drives Stop() to completion; a concurrent/repeat Disconnect sees mgr == null and no-ops.
            // Stop() is called OUTSIDE the lock — it joins the receive/logic threads (CloseSocket), which
            // must not block the lock (and the receive thread never re-enters Disconnect). This makes
            // Disconnect idempotent and safe against the ServerLoop-thread vs caller-thread teardown race.
            LiteNetManager mgr;
            bool wasConnected;
            lock (_stopLock)
            {
                mgr = _netManager;
                _netManager = null;
                wasConnected = _isConnected;
                _isConnected = false;
                _peerMap.Clear();

#if KLOTHO_FAULT_INJECTION
                _delayedRecvMessages.Clear();
                _rttLogged = false;
#endif
            }

            mgr?.Stop();

            if (!_isServer && wasConnected)
                OnDisconnected?.Invoke(DisconnectReason.LocalDisconnect);
        }

        public void DisconnectPeer(int peerId)
        {
            if (_peerMap.TryGetPeer(peerId, out var peer))
                peer.Disconnect();
        }

        public void DisconnectPeer(int peerId, byte[] data)
        {
            if (_peerMap.TryGetPeer(peerId, out var peer))
                peer.Disconnect(data);
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
            _logger?.KWarning($"[LiteNetLibTransport] Peer disconnected — reason: {disconnectInfo.Reason}, socketError: {disconnectInfo.SocketErrorCode}");

            if (_peerMap.TryGetId(peer, out int peerId))
            {
                _peerMap.Unregister(peer);
                OnPeerDisconnected?.Invoke(peerId);
            }

            if (!_isServer)
            {
                _isConnected = false;
                // Extract the reject reason byte the server attached to the disconnect packet, if any.
                // AdditionalData is only valid here (recycled after this callback), so read it now.
                var additional = disconnectInfo.AdditionalData;
                _lastDisconnectPayload = additional.AvailableBytes >= 1 ? additional.GetByte() : -1;
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
                    _logger?.KWarning($"[FaultInjection] LiteNetLibTransport: RTT emulation active ({rtt}ms round-trip)");
                }
                // Pooled buffer cannot survive across PollEvents — allocate dedicated copy for the delay queue.
                byte[] copy = new byte[length];
                reader.GetBytes(copy, length);
                reader.Recycle();
                long releaseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (rtt / 2);

                // Preserve per-peer FIFO under variable EmulatedRttMs.
                // Without this clamp, a later-arriving message can be released before an earlier one
                // when EmulatedRttMs drops mid-flight — producing artificial ReliableOrdered violations
                // the real transport never exhibits.
                for (int i = _delayedRecvMessages.Count - 1; i >= 0; i--)
                {
                    if (_delayedRecvMessages[i].peerId == peerId)
                    {
                        if (releaseAt < _delayedRecvMessages[i].releaseAtMs)
                        {
                            long clamped = _delayedRecvMessages[i].releaseAtMs;
                            _logger?.KDebug($"[FaultInjection][FIFO] clamp peerId={peerId} naturalMs={releaseAt} clampedMs={clamped} addedDelayMs={clamped - releaseAt}");
                            releaseAt = clamped;
                        }
                        break;
                    }
                }

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

        void ILiteNetEventListener.OnConnectionRequest(LiteConnectionRequest request)
        {
            if (_isServer)
            {
                // The only listener callback that reads _netManager, and callbacks dispatch on the
                // PollEvents() thread — so a Disconnect() from another thread can null the field
                // while this one is mid-dispatch. Capture once, and reject when it is gone: the
                // request carries its own manager reference, so a null here decides the
                // max-connections verdict only, and accepting would build a peer on a manager that
                // is being thrown away.
                var mgr = _netManager;
                if (mgr == null)
                {
                    _logger?.KWarning($"[LiteNetLibTransport] Connection rejected: {request.RemoteEndPoint} — transport is shutting down");
                    request.Reject();
                    return;
                }

                if (_maxConnections > 0 && mgr.ConnectedPeersCount >= _maxConnections)
                {
                    _logger?.KWarning($"[LiteNetLibTransport] Connection rejected: {request.RemoteEndPoint} — max connections ({_maxConnections}) reached");
                    request.Reject();
                    return;
                }

                var accepted = request.AcceptIfKey(_connectionKey);
                if (accepted != null)
                    _logger?.KInformation($"[LiteNetLibTransport] Connection accepted: {request.RemoteEndPoint}");
                else
                    _logger?.KWarning($"[LiteNetLibTransport] Connection rejected: {request.RemoteEndPoint} — key mismatch or already used");
            }
            else
            {
                _logger?.KWarning($"[LiteNetLibTransport] OnConnectionRequest called on client — unexpected. Rejecting.");
                request.Reject();
            }
        }

        void ILiteNetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            _logger?.KError($"[LiteNetLibTransport] Network error: {endPoint}: {socketError}");
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
