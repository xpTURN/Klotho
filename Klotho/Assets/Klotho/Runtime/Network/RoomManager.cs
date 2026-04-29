using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Configuration for the room creation factories.
    /// </summary>
    public class RoomManagerConfig
    {
        public int MaxRooms { get; set; } = 4;
        public int MaxPlayersPerRoom { get; set; } = 4;
        public int MaxSpectatorsPerRoom { get; set; } = 0;

        /// <summary>
        /// Factory that creates the EcsSimulation for each room.
        /// System registration is performed by CallbacksFactory's RegisterSystems.
        /// </summary>
        public Func<EcsSimulation> SimulationFactory { get; set; }

        /// <summary>
        /// Factory that creates the SimulationConfig for each room.
        /// </summary>
        public Func<SimulationConfig> SimulationConfigFactory { get; set; }

        /// <summary>
        /// Factory that creates the SessionConfig for each room.
        /// </summary>
        public Func<SessionConfig> SessionConfigFactory { get; set; }

        /// <summary>
        /// Factory that creates the ISimulationCallbacks for each room.
        /// </summary>
        public Func<ILogger, ISimulationCallbacks> CallbacksFactory { get; set; }
    }

    /// <summary>
    /// Manages room lifecycles for a multi-room server.
    /// Handles creation, activation, and Draining/Disposing transitions.
    /// </summary>
    public class RoomManager
    {
        private readonly INetworkTransport _sharedTransport;
        private readonly ILoggerFactory _loggerFactory;
        private readonly RoomManagerConfig _config;
        private readonly ILogger _logger;
        private readonly RoomRouter _router;

        private readonly Room[] _rooms;
        private int _activeRoomCount;

        public RoomRouter Router => _router;
        public int ActiveRoomCount => _activeRoomCount;
        public int MaxRooms => _config.MaxRooms;

        public RoomManager(
            INetworkTransport sharedTransport,
            RoomRouter router,
            ILoggerFactory loggerFactory,
            RoomManagerConfig config)
        {
            _sharedTransport = sharedTransport ?? throw new ArgumentNullException(nameof(sharedTransport));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _loggerFactory = loggerFactory;
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = loggerFactory?.CreateLogger("RoomManager");

            _rooms = new Room[config.MaxRooms];
            _router.SetRoomManager(this);
        }

        /// <summary>
        /// Looks up a room by roomId. Returns null if it does not exist or is Empty.
        /// </summary>
        public Room GetRoom(int roomId)
        {
            if (roomId < 0 || roomId >= _rooms.Length) return null;
            var room = _rooms[roomId];
            return room?.State != RoomState.Empty ? room : null;
        }

        /// <summary>
        /// Creates a new room and transitions it to the Active state.
        /// </summary>
        /// <returns>The created Room, or null if no slot is available.</returns>
        public Room CreateRoom()
        {
            int slot = FindEmptySlot();
            if (slot < 0)
            {
                _logger?.ZLogWarning($"[RoomManager] No empty slot available (max={_config.MaxRooms})");
                return null;
            }

            return CreateRoomAt(slot);
        }

        /// <summary>
        /// Creates a room with the specified roomId.
        /// </summary>
        public Room CreateRoom(int roomId)
        {
            if (roomId < 0 || roomId >= _rooms.Length)
            {
                _logger?.ZLogWarning($"[RoomManager] Invalid roomId={roomId} (max={_config.MaxRooms - 1})");
                return null;
            }

            if (_rooms[roomId] != null && _rooms[roomId].State != RoomState.Empty)
            {
                _logger?.ZLogWarning($"[RoomManager] Room {roomId} already exists (state={_rooms[roomId].State})");
                return null;
            }

            return CreateRoomAt(roomId);
        }

        private Room CreateRoomAt(int roomId)
        {
            var roomLogger = _loggerFactory?.CreateLogger($"Room-{roomId}");

            // Create each component via its factory
            var simConfig = _config.SimulationConfigFactory();
            var sessionConfig = _config.SessionConfigFactory();
            var sim = _config.SimulationFactory();
            var callbacks = _config.CallbacksFactory(roomLogger);
            callbacks.RegisterSystems(sim);
            sim.LockAssetRegistry();

            var commandFactory = new CommandFactory();
            var transport = new RoomScopedTransport(_sharedTransport);
            var networkService = new ServerNetworkService();
            networkService.Initialize(transport, commandFactory, roomLogger);

            var engine = new KlothoEngine(simConfig, sessionConfig);
            engine.Initialize(sim, networkService, roomLogger, callbacks);
            networkService.SubscribeEngine(engine);

            networkService.CreateRoom($"room-{roomId}", _config.MaxPlayersPerRoom);
            networkService.MaxSpectatorsPerRoom = _config.MaxSpectatorsPerRoom;

            var room = new Room(
                roomId, simConfig, sessionConfig, sim, commandFactory,
                transport, networkService, engine, callbacks, roomLogger);

            room.State = RoomState.Active;
            _rooms[roomId] = room;
            _activeRoomCount++;

            _router.RegisterRoom(room);

            _logger?.ZLogInformation($"[RoomManager] Room {roomId} created (active={_activeRoomCount})");
            return room;
        }

        /// <summary>
        /// Cleans up rooms in the Disposing state and transitions them to Empty.
        /// Invoked every cycle on the main thread of ServerLoop.
        /// </summary>
        public void CleanupDisposingRooms()
        {
            for (int i = 0; i < _rooms.Length; i++)
            {
                var room = _rooms[i];
                if (room == null || room.State != RoomState.Disposing)
                    continue;

                _router.UnregisterRoom(room.RoomId);
                _router.RemoveRoomPeers(room.RoomId);
                room.Dispose();
                _activeRoomCount--;

                _logger?.ZLogInformation($"[RoomManager] Room {room.RoomId} disposed (active={_activeRoomCount})");
            }
        }

        /// <summary>
        /// Collects the Active rooms targeted by this cycle's stage-2 update.
        /// Excludes stragglers that have not completed from the previous cycle.
        /// </summary>
        public void GetReadyRooms(List<Room> result)
        {
            result.Clear();
            for (int i = 0; i < _rooms.Length; i++)
            {
                var room = _rooms[i];
                if (room == null) continue;

                if ((room.State == RoomState.Active || room.State == RoomState.Draining)
                    && !room.IsStraggler)
                {
                    result.Add(room);
                }
            }
        }

        /// <summary>
        /// Transitions rooms that have finished draining into the Disposing state.
        /// Invoked by ServerLoop after Room.Update().
        /// </summary>
        public void TransitionDrainingRooms()
        {
            for (int i = 0; i < _rooms.Length; i++)
            {
                var room = _rooms[i];
                if (room == null || room.State != RoomState.Draining)
                    continue;

                // From Draining: Engine.Stop + transition to Disposing
                room.Engine.Stop();
                room.State = RoomState.Disposing;

                _logger?.ZLogInformation($"[RoomManager] Room {room.RoomId} → Disposing");
            }
        }

        /// <summary>
        /// Graceful shutdown: broadcasts ServerShutdown to every Active room and tears them down.
        /// </summary>
        public void ShutdownAllRooms()
        {
            var shutdownMsg = new ServerShutdownMessage { Reason = 1 }; // ServerClosing
            var serializer = new MessageSerializer();

            for (int i = 0; i < _rooms.Length; i++)
            {
                var room = _rooms[i];
                if (room == null || room.State == RoomState.Empty)
                    continue;

                // Skip the broadcast for straggler rooms — their threads may still be running
                if (!room.IsStraggler)
                {
                    try
                    {
                        using var msg = serializer.SerializePooled(shutdownMsg);
                        room.Transport.Broadcast(msg.Data, msg.Length, DeliveryMethod.Reliable);
                        room.Engine.Stop();
                    }
                    catch (Exception ex)
                    {
                        _logger?.ZLogError($"[RoomManager] Room {room.RoomId} shutdown broadcast failed: {ex.Message}");
                    }
                }

                room.NetworkService.LeaveRoom();
                _router.UnregisterRoom(room.RoomId);
                _router.RemoveRoomPeers(room.RoomId);
                room.State = RoomState.Empty;
            }

            _activeRoomCount = 0;
        }

        private int FindEmptySlot()
        {
            for (int i = 0; i < _rooms.Length; i++)
            {
                if (_rooms[i] == null || _rooms[i].State == RoomState.Empty)
                    return i;
            }
            return -1;
        }
    }
}
