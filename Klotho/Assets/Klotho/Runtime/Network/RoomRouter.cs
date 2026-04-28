using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Unassigned peer info.
    /// </summary>
    internal struct UnroutedPeerInfo
    {
        public int PeerId;
        public long ConnectedTimeMs;
    }

    /// <summary>
    /// peerId → Room routing for a multi-room server.
    /// Subscribes to the shared Transport's events and enqueues messages into per-room inbound queues.
    ///
    /// Protocol contract:
    /// - An unassigned peer sends RoomHandshakeMessage as the first message → roomId obtained → assignment.
    /// - From the second message onwards (PlayerJoin/SpectatorJoin, etc.), normal routing applies.
    /// </summary>
    public class RoomRouter
    {
        private const int UNROUTED_PEER_TIMEOUT_MS = 5000;


        private readonly INetworkTransport _transport;
        private readonly ILogger _logger;
        private RoomManager _roomManager;

        // peerId → roomId mapping
        private readonly Dictionary<int, int> _peerToRoom = new Dictionary<int, int>();
        // Peers not yet assigned to a room
        private readonly Dictionary<int, UnroutedPeerInfo> _unroutedPeers = new Dictionary<int, UnroutedPeerInfo>();

        // roomId → Room mapping (registered/unregistered by RoomManager)
        private readonly Dictionary<int, Room> _rooms = new Dictionary<int, Room>();

        // For sending JoinReject
        private readonly MessageSerializer _serializer = new MessageSerializer();
        private readonly JoinRejectMessage _rejectCache = new JoinRejectMessage();

        // Flag for rejecting new connections (Graceful Shutdown)
        private bool _accepting = true;

        public void SetRoomManager(RoomManager roomManager) => _roomManager = roomManager;

        public RoomRouter(INetworkTransport transport, ILogger logger)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logger = logger;

            _transport.OnPeerConnected += OnPeerConnected;
            _transport.OnDataReceived += OnDataReceived;
            _transport.OnPeerDisconnected += OnPeerDisconnected;
        }

        public void Dispose()
        {
            _transport.OnPeerConnected -= OnPeerConnected;
            _transport.OnDataReceived -= OnDataReceived;
            _transport.OnPeerDisconnected -= OnPeerDisconnected;

            _peerToRoom.Clear();
            _unroutedPeers.Clear();
        }

        /// <summary>
        /// Graceful Shutdown: switch to rejecting new connections.
        /// </summary>
        public void StopAccepting()
        {
            _accepting = false;
        }

        // ── Room register/unregister (called from RoomManager) ──

        public void RegisterRoom(Room room)
        {
            _rooms[room.RoomId] = room;
        }

        public void UnregisterRoom(int roomId)
        {
            _rooms.Remove(roomId);
        }

        // ── Transport event handlers (Main Thread) ──

        private void OnPeerConnected(int peerId)
        {
            if (!_accepting)
            {
                _transport.DisconnectPeer(peerId);
                return;
            }

            _unroutedPeers[peerId] = new UnroutedPeerInfo
            {
                PeerId = peerId,
                ConnectedTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private void OnDataReceived(int peerId, byte[] data, int length)
        {
            if (_unroutedPeers.ContainsKey(peerId))
            {
                // First message: extract roomId → assign to room
                RouteFirstMessage(peerId, data, length);
            }
            else if (_peerToRoom.TryGetValue(peerId, out int roomId))
            {
                // Assignment complete: enqueue into the room's queue
                if (_rooms.TryGetValue(roomId, out Room room)
                    && room.State == RoomState.Active)
                {
                    EnqueueData(room, peerId, data, length);
                }
                // Ignore packets for Draining/Disposing rooms
            }
            // Unknown peerId → ignore (already cleaned up)
        }

        private void OnPeerDisconnected(int peerId)
        {
            if (_unroutedPeers.Remove(peerId))
            {
                // Disconnected before room assignment — just clean up
                return;
            }

            if (_peerToRoom.TryGetValue(peerId, out int roomId))
            {
                if (_rooms.TryGetValue(roomId, out Room room))
                {
                    room.InboundQueue.Enqueue(new InboundEntry
                    {
                        PeerId = peerId,
                        Type = InboundEventType.Disconnected,
                        Buffer = null,
                        Length = 0
                    });
                    room.Transport.RemovePeer(peerId);
                }
                _peerToRoom.Remove(peerId);
            }
        }

        // ── First message routing ──

        private void RouteFirstMessage(int peerId, byte[] data, int length)
        {
            var msg = _serializer.Deserialize(data, length) as RoomHandshakeMessage;
            if (msg == null)
            {
                _logger?.ZLogWarning($"[RoomRouter] Peer {peerId}: first message is not RoomHandshakeMessage, disconnecting");
                RejectAndDisconnect(peerId, 0); // Unknown
                return;
            }

            int roomId = msg.RoomId;
            Room room = ValidateAndResolveRoom(peerId, roomId);
            if (room == null) return;

            AssignPeerToRoom(peerId, roomId, room);
            _logger?.ZLogInformation($"[RoomRouter] Peer {peerId} → Room {roomId}");
        }

        /// <summary>
        /// Validate room: check existence, state, capacity. On failure, reject and disconnect, then return null.
        /// </summary>
        private Room ValidateAndResolveRoom(int peerId, int roomId)
        {
            if (!_rooms.TryGetValue(roomId, out Room room))
            {
                if (_roomManager != null)
                {
                    room = _roomManager.CreateRoom(roomId);
                }

                if (room == null)
                {
                    _logger?.ZLogWarning($"[RoomRouter] Peer {peerId}: room {roomId} not found");
                    RejectAndDisconnect(peerId, 1); // RoomNotFound
                    return null;
                }
            }

            if (room.State != RoomState.Active)
            {
                _logger?.ZLogWarning($"[RoomRouter] Peer {peerId}: room {roomId} is {room.State}");
                RejectAndDisconnect(peerId, 5); // RoomClosing
                return null;
            }

            if (room.Transport.PeerCount >= room.NetworkService.MaxPlayersPerRoom)
            {
                _logger?.ZLogWarning($"[RoomRouter] Peer {peerId}: room {roomId} is full");
                RejectAndDisconnect(peerId, 2); // RoomFull
                return null;
            }

            return room;
        }

        /// <summary>
        /// Assign the peer to the room and enqueue a Connected event.
        /// </summary>
        private void AssignPeerToRoom(int peerId, int roomId, Room room)
        {
            _unroutedPeers.Remove(peerId);
            _peerToRoom[peerId] = roomId;
            room.Transport.AddPeer(peerId);

            room.InboundQueue.Enqueue(new InboundEntry
            {
                PeerId = peerId,
                Type = InboundEventType.Connected,
                Buffer = null,
                Length = 0
            });
        }

        // ── Utilities ──

        private void EnqueueData(Room room, int peerId, byte[] data, int length)
        {
            byte[] buf = StreamPool.GetBuffer(length);
            Buffer.BlockCopy(data, 0, buf, 0, length);

            room.InboundQueue.Enqueue(new InboundEntry
            {
                PeerId = peerId,
                Type = InboundEventType.Data,
                Buffer = buf,
                Length = length
            });
        }

        private void RejectAndDisconnect(int peerId, byte reason)
        {
            _unroutedPeers.Remove(peerId);

            _rejectCache.Reason = reason;
            using var msg = _serializer.SerializePooled(_rejectCache);
            _transport.Send(peerId, msg.Data, msg.Length, DeliveryMethod.Reliable);
            _transport.DisconnectPeer(peerId);
        }

        /// <summary>
        /// Clean up unrouted peer timeouts. Called periodically at the end of ServerLoop stage 1.
        /// </summary>
        public void CleanupUnroutedPeers()
        {
            if (_unroutedPeers.Count == 0) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Collect removal targets first (to avoid mutating during iteration)
            List<int> expired = null;

            foreach (var kvp in _unroutedPeers)
            {
                if (now - kvp.Value.ConnectedTimeMs > UNROUTED_PEER_TIMEOUT_MS)
                {
                    expired ??= new List<int>();
                    expired.Add(kvp.Key);
                }
            }

            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    int peerId = expired[i];
                    _logger?.ZLogWarning($"[RoomRouter] Unrouted peer {peerId} timed out, disconnecting");
                    _unroutedPeers.Remove(peerId);
                    _transport.DisconnectPeer(peerId);
                }
            }
        }

        /// <summary>
        /// Remove all peer mappings for a specific room. Called from RoomManager.CleanupDisposingRooms().
        /// </summary>
        public void RemoveRoomPeers(int roomId)
        {
            List<int> toRemove = null;
            foreach (var kvp in _peerToRoom)
            {
                if (kvp.Value == roomId)
                {
                    toRemove ??= new List<int>();
                    toRemove.Add(kvp.Key);
                }
            }

            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                    _peerToRoom.Remove(toRemove[i]);
            }
        }
    }
}
