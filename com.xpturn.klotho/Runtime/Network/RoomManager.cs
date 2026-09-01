using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

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
        /// Asset registry shared across all rooms; used to build each room's EcsSimulation.
        /// </summary>
        public IDataAssetRegistry AssetRegistry { get; set; }

        /// <summary>
        /// Rollback tick budget for each room's EcsSimulation (server-driven default = 1).
        /// Intentionally distinct from SimulationConfig.MaxRollbackTicks (netcode rollback depth):
        /// the server hardcodes the simulation buffer to 1 regardless of the loaded config.
        /// </summary>
        public int SimulationMaxRollbackTicks { get; set; } = 1;

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
        public Func<IKLogger, ISimulationCallbacks> CallbacksFactory { get; set; }

        /// <summary>
        /// Optional per-room match config source. null = no per-room resolution: every room uses the
        /// default context (StageId 0, no payload) and room creation stays open (current behaviour).
        /// When set, CreateRoom resolves each room's context and refuses (returns null → RoomNotFound)
        /// rooms the source declines. Called synchronously on the room-creation thread (non-blocking).
        /// </summary>
        public IMatchConfigSource MatchConfigSource { get; set; }

        /// <summary>
        /// Optional context-aware factories — take precedence over the plain ones when set. Let a game
        /// build a fully stage-specific SimulationConfig / SessionConfig / callbacks from the resolved
        /// <see cref="MatchConfigContext"/>. When null, the plain factory is used; the resolved
        /// StageId/MatchConfigData are stamped onto the SimulationConfig regardless so they propagate to
        /// joiners via SimulationConfigMessage.
        /// </summary>
        public Func<MatchConfigContext, SimulationConfig> SimulationConfigFactoryForMatch { get; set; }
        public Func<MatchConfigContext, SessionConfig> SessionConfigFactoryForMatch { get; set; }
        public Func<MatchConfigContext, IKLogger, ISimulationCallbacks> CallbacksFactoryForMatch { get; set; }

        /// <summary>
        /// Authority-side ticket validator, shared across all rooms (server-global; lobby redeem is a
        /// server-wide concern). null = no validation (behaviour unchanged). Each BeginValidate call
        /// is independent (carries its own SessionMagic/peer context). Injected into every room's
        /// ServerNetworkService at creation.
        /// </summary>
        public IPlayerIdentityValidator IdentityValidator { get; set; }

        /// <summary>
        /// Authority-side player-config entitlement guard, shared across all rooms.
        /// null = no cross-check (client selections pass through unchanged). Injected into every room's
        /// ServerNetworkService at creation, alongside <see cref="IdentityValidator"/>.
        /// </summary>
        public IPlayerConfigEntitlementGuard PlayerConfigEntitlementGuard { get; set; }

        /// <summary>
        /// Authority-side gate for client-submitted in-match reliable commands, shared across all rooms.
        /// null = no cross-check (every client reliable command is accepted). Injected into every room's
        /// ServerNetworkService at creation, alongside <see cref="PlayerConfigEntitlementGuard"/>.
        /// </summary>
        public IReliableCommandEntitlementGate ReliableCommandEntitlementGate { get; set; }

        /// <summary>
        /// Optional callback invoked once for each room right after it is fully constructed (engine
        /// handlers attached) and before it transitions to Active — i.e. before any player joins.
        /// null = no-op. Gives a host the seam to subscribe to per-room engine / network-service events
        /// (the room is created inside the core, so this is the only external subscription point).
        /// Called synchronously on the room-creation thread.
        /// </summary>
        public Action<Room> OnRoomCreated { get; set; }

        /// <summary>
        /// Optional callback invoked once per room the tick it transitions to Draining (either branch of
        /// Room.Update — normal-end/abort grace expiry OR all-peers-gone). null = no-op. Fires on the room's
        /// OWN update thread (NOT the main loop) so the subscriber's work — e.g. a journal fsync — cannot stall
        /// the server tick. The core carries no "abandon" semantics: the subscriber discriminates
        /// via Room.EndRequestedAtUtc / Room.DrainPhase. Symmetric with <see cref="OnRoomCreated"/>.
        /// </summary>
        public Action<Room> OnRoomDraining { get; set; }
    }

    /// <summary>
    /// Manages room lifecycles for a multi-room server.
    /// Handles creation, activation, and Draining/Disposing transitions.
    /// </summary>
    public class RoomManager
    {
        private readonly INetworkTransport _sharedTransport;
        private readonly IKLoggerFactory _loggerFactory;
        private readonly RoomManagerConfig _config;
        private readonly IKLogger _logger;
        private readonly RoomRouter _router;

        private readonly Room[] _rooms;
        private int _activeRoomCount;

        // One-shot latch for the shared-seed warning below. Plain field: rooms are created on a
        // single thread (RoomRouter), same convention as _activeRoomCount. Room *updates* run on
        // workers; creation does not.
        private bool _sharedSeedWarned;

        // Drain metrics — indexed by (int)SessionPhase (size = SessionPhase enum value count + safety margin)
        private const int DRAIN_PHASE_BUCKETS = 8;
        private readonly long[] _drainTotals = new long[DRAIN_PHASE_BUCKETS];
        private long _lastDrainLifetimeMs;

        public RoomRouter Router => _router;
        public int ActiveRoomCount => _activeRoomCount;
        public int MaxRooms => _config.MaxRooms;

        /// <summary>
        /// Total number of rooms drained while in the given phase.
        /// </summary>
        public long GetDrainTotal(SessionPhase phase) => _drainTotals[(int)phase];

        /// <summary>
        /// Lifetime of the most recently disposed room, in ms.
        /// </summary>
        public long LastDrainLifetimeMs => _lastDrainLifetimeMs;

        public RoomManager(
            INetworkTransport sharedTransport,
            RoomRouter router,
            IKLoggerFactory loggerFactory,
            RoomManagerConfig config)
        {
            _sharedTransport = sharedTransport ?? throw new ArgumentNullException(nameof(sharedTransport));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _loggerFactory = loggerFactory;
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = loggerFactory?.CreateLogger("RoomManager");

            // One-time coherence check (startup): an entitlement guard without an identity validator means no
            // redeem ever populates PlayerInfo.Entitlement, so GetPlayerEntitlement is always null and the guard
            // cannot enforce ownership (typically passes). Log rather than throw — a multi-room server should
            // surface the misconfig, not fail to boot. The reliable-command gate has the same property.
            if (_config.PlayerConfigEntitlementGuard != null && _config.IdentityValidator == null)
                _logger?.KError(
                    $"[RoomManager] PlayerConfigEntitlementGuard set without an IdentityValidator — player entitlement is always null, so the guard cannot enforce ownership (typically passes, enforcement effectively OFF).");

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
                _logger?.KWarning($"[RoomManager] No empty slot available (max={_config.MaxRooms})");
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
                _logger?.KWarning($"[RoomManager] Invalid roomId={roomId} (max={_config.MaxRooms - 1})");
                return null;
            }

            if (_rooms[roomId] != null && _rooms[roomId].State != RoomState.Empty)
            {
                _logger?.KWarning($"[RoomManager] Room {roomId} already exists (state={_rooms[roomId].State})");
                return null;
            }

            return CreateRoomAt(roomId);
        }

        private Room CreateRoomAt(int roomId)
        {
            var roomLogger = _loggerFactory?.CreateLogger($"Room-{roomId}");

            // Resolve the per-room match config (stage selector + opaque payload). Synchronous, non-blocking.
            // Source unset → default context (StageId 0), open creation. Source set + declines → refuse
            // creation (return null) so RoomRouter rejects the peer with RoomNotFound.
            var matchCtx = MatchConfigContext.Default(roomId);
            if (_config.MatchConfigSource != null && !_config.MatchConfigSource.TryResolve(roomId, out matchCtx))
            {
                _logger?.KWarning($"[RoomManager] Room {roomId} declined by match config source — creation refused");
                return null;
            }

            // Create each component via its factory (context-aware overload preferred when set).
            var simConfig = _config.SimulationConfigFactoryForMatch != null
                ? _config.SimulationConfigFactoryForMatch(matchCtx)
                : _config.SimulationConfigFactory();
            // Stamp the resolved stage/payload so they reach joiners via SimulationConfigMessage even when
            // the game factory doesn't set them — the core guarantees propagation (not the game code).
            // Only when a source is active (multi-stage); stamp onto a per-room copy so a shared config
            // instance (WithSimulationConfig(value)) is not clobbered across rooms with different stages.
            if (_config.MatchConfigSource != null)
            {
                simConfig = simConfig.Clone();
                simConfig.StageId = matchCtx.StageId;
                simConfig.MatchConfigData = matchCtx.MatchConfigData;
            }

            var sessionConfig = _config.SessionConfigFactoryForMatch != null
                ? _config.SessionConfigFactoryForMatch(matchCtx)
                : _config.SessionConfigFactory();

            // Two live rooms with the SAME pinned seed run the same match. Warn, don't refuse: pinning a
            // seed is legitimate, and a single-room server never trips this — the test is a live
            // co-tenant, not "the factory returned this before", so a room recreated after its
            // match ended (the normal single-room cycle) stays silent.
            //
            // The test is the seed VALUE, not the instance. It used to be ReferenceEquals, which read the
            // symptom off its commonest cause — WithSessionConfig(value) handing every room one object.
            // But a per-match factory that clones a pinned config produces different instances carrying
            // the same seed: identical matches, and the reference test would have gone quiet exactly when
            // a server adopted the factory this warning recommends.
            if (!_sharedSeedWarned && sessionConfig != null && sessionConfig.RandomSeed != 0)
            {
                for (int i = 0; i < _rooms.Length; i++)
                {
                    var other = _rooms[i];
                    if (other == null || other.State == RoomState.Empty) continue;
                    if (other.SessionConfig == null || other.SessionConfig.RandomSeed != sessionConfig.RandomSeed) continue;

                    _sharedSeedWarned = true;
                    _logger?.KWarning(
                        $"[RoomManager] Room {roomId} runs with the same pinned RandomSeed ({sessionConfig.RandomSeed}) " +
                        $"as live room {other.RoomId} — every such room plays the SAME match. Give each room its own " +
                        $"seed from the per-match SessionConfig factory (WithSessionConfig(Func<MatchConfigContext, ...>)), " +
                        $"or leave RandomSeed at 0 to have one drawn per match.");
                    break;
                }
            }

            // maxEntities / maxCount overrides / prune-set are PROCESS-global layout inputs, not
            // per-room ones — but the config that carries them is per-room (the match factory is
            // free to return a different one per stage). Ask before constructing: handing a
            // disagreeing set to the registry recomputes the layout in editor/test builds — pulling
            // _layouts and the type-id cache out from under rooms already ticking on workers — and
            // throws in release, where nothing between here and ServerLoop.Run catches it, so one
            // mismatched room would take the whole process and every healthy room with it.
            // Refusing here costs this room only: the peer gets RoomNotFound, the server lives.
            if (ComponentStorageRegistry.LayoutInputsConflict(
                    simConfig.MaxEntities, simConfig.ComponentMaxCountOverrides,
                    simConfig.PrunedComponentTypeIds, out string layoutConflict))
            {
                _logger?.KError(
                    $"[RoomManager] Room {roomId} refused — per-room simulation config disagrees with the " +
                    $"process layout. {layoutConflict} Source these three from one place at bootstrap.");
                return null;
            }

            var sim = new EcsSimulation(
                maxEntities: simConfig.MaxEntities,
                maxRollbackTicks: _config.SimulationMaxRollbackTicks,
                deltaTimeMs: simConfig.TickIntervalMs,
                logger: roomLogger,
                assetRegistry: _config.AssetRegistry,
                maxCountOverrides: simConfig.ComponentMaxCountOverrides,
                prunedComponentTypeIds: simConfig.PrunedComponentTypeIds);
            var callbacks = _config.CallbacksFactoryForMatch != null
                ? _config.CallbacksFactoryForMatch(matchCtx, roomLogger)
                : _config.CallbacksFactory(roomLogger);
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
            networkService.SetRoomId(roomId);
            networkService.MaxSpectatorsPerRoom = _config.MaxSpectatorsPerRoom;
            networkService.SetIdentityValidator(_config.IdentityValidator); // server-global, injected before the room goes Active
            networkService.SetPlayerConfigEntitlementGuard(_config.PlayerConfigEntitlementGuard); // entitlement guard, same lifetime as the validator
            networkService.SetReliableCommandEntitlementGate(_config.ReliableCommandEntitlementGate); // in-match reliable-command gate, same lifetime

            var room = new Room(
                roomId, simConfig, sessionConfig, sim, commandFactory,
                transport, networkService, engine, callbacks, roomLogger);

            // Hook room drain into engine match-end / match-abort events.
            // Normal end (OnMatchEnded) → EndGraceMs grace, then Draining (reason="match-ended").
            //   Pause policy halts characters via client-issued StopCommand on the deterministic stream;
            //   the engine keeps ticking through the grace window so the StopCommand reaches verified state.
            // Abort (OnMatchAborted) → AbortGraceMs grace, then Draining (reason="match-aborted").
            // first-write-wins inside Room.RequestEnd handles abort-during-grace.
            room.AttachEngineHandlers(
                onMatchEnded: (tick, endEvt) =>
                {
                    room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(sessionConfig.EndGraceMs));
                },
                onMatchAborted: reason =>
                {
                    room.RequestEnd(EndReason.MatchAborted, TimeSpan.FromMilliseconds(sessionConfig.AbortGraceMs));
                });

            room.CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            room.OnDraining = _config.OnRoomDraining; // additive drain hook; fires on the room's own update thread

            // Room fully constructed (engine handlers attached) but not yet Active — the host's seam to
            // subscribe to per-room engine / network-service events before any player joins (null = no-op).
            _config.OnRoomCreated?.Invoke(room);

            room.State = RoomState.Active;
            _rooms[roomId] = room;
            _activeRoomCount++;

            _router.RegisterRoom(room);

            _logger?.KInformation($"[RoomManager] Room {roomId} created (active={_activeRoomCount})");
            return room;
        }

        /// <summary>
        /// Clears IsStraggler for straggler rooms whose ThreadPool Update has completed
        /// (UpdateComplete set by the worker). Invoked every cycle on the main thread of ServerLoop,
        /// before the Draining/Disposing transitions so a just-completed straggler can be processed
        /// in the same cycle. Touches IsStraggler only — the straggle history decays on a room's own
        /// CLEAN cycle (Room.NoteCleanCycle), never on recovery. That distinction is the whole design:
        /// recovery follows every straggle, so decaying here would return the count to 0 regardless of
        /// how often the room actually misses, and force-close would never fire. This replaced a
        /// cumulative policy, which never forgot an old straggle and so drifted toward closing a
        /// room that had long since recovered.
        /// </summary>
        public void RecoverCompletedStragglers()
        {
            for (int i = 0; i < _rooms.Length; i++)
            {
                var room = _rooms[i];
                if (room != null && room.IsStraggler && room.UpdateComplete)
                    room.IsStraggler = false;
            }
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

                // Straggler's ThreadPool Update may still be running — defer teardown until it is
                // recovered (IsStraggler cleared once UpdateComplete). Avoids Dispose racing the worker.
                if (room.IsStraggler)
                    continue;

                long lifetimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - room.CreatedAtMs;
                int phaseIdx = (int)room.DrainPhase;
                if ((uint)phaseIdx < (uint)DRAIN_PHASE_BUCKETS)
                    _drainTotals[phaseIdx]++;
                _lastDrainLifetimeMs = lifetimeMs;

                _router.UnregisterRoom(room.RoomId);
                _router.RemoveRoomPeers(room.RoomId);
                room.Dispose();
                _activeRoomCount--;

                _logger?.KInformation($"[RoomManager] Room {room.RoomId} disposed (active={_activeRoomCount}, drain_phase={room.DrainPhase}, lifetime_ms={lifetimeMs})");
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

                // Straggler's ThreadPool Update may still be running — defer Engine.Stop until recovered.
                if (room.IsStraggler)
                    continue;

                // From Draining: Engine.Stop + transition to Disposing
                room.Engine.Stop();
                room.State = RoomState.Disposing;

                _logger?.KInformation($"[RoomManager] Room {room.RoomId} → Disposing");
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

                // Skip the entire teardown for straggler rooms — their ThreadPool Update may still be
                // running and LeaveRoom/Unregister/State writes would race it. The process is exiting
                // (GracefulShutdown → Disconnect, with the hard-timeout thread as backstop), and the
                // shared transport's Disconnect tears down all peers regardless.
                if (room.IsStraggler)
                {
                    _logger?.KWarning($"[RoomManager] Room {room.RoomId} still running at shutdown — skipping teardown");
                    continue;
                }

                try
                {
                    using var msg = serializer.SerializePooled(shutdownMsg);
                    room.Transport.Broadcast(msg.Data, msg.Length, DeliveryMethod.Reliable);
                    room.Engine.Stop();
                }
                catch (Exception ex)
                {
                    _logger?.KError($"[RoomManager] Room {room.RoomId} shutdown broadcast failed: {ex.Message}");
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
