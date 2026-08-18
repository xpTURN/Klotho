using System;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Network
{
    /// <summary>Thrown when a room-manager config fails build-time validation.</summary>
    public sealed class RoomManagerConfigValidationException : ArgumentException
    {
        public RoomManagerConfigValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Fluent assembler for <see cref="RoomManagerConfig"/>. The game-specific dependency
    /// (CallbacksFactory) is a constructor argument (compile-time). The simulation/session
    /// config sources are exposed as value (shared across rooms) or factory (fresh per room)
    /// overloads, and the EcsSimulation is derived from the simulation config. Build()
    /// validates that every required factory is present, then returns the config.
    /// Object-initializer construction of RoomManagerConfig remains supported as an escape hatch.
    /// Build() runs once at server startup — not a per-room/per-frame path.
    /// </summary>
    public sealed class RoomManagerConfigBuilder
    {
        private readonly RoomManagerConfig _config = new RoomManagerConfig();

        /// <param name="callbacksFactory">Required. Builds the per-room ISimulationCallbacks
        /// (RegisterSystems + game logic) from the room logger. This is the only game-unique factory.</param>
        public RoomManagerConfigBuilder(Func<IKLogger, ISimulationCallbacks> callbacksFactory)
        {
            _config.CallbacksFactory = callbacksFactory
                ?? throw new ArgumentNullException(nameof(callbacksFactory));
        }

        /// <param name="callbacksFactory">Required. Context-aware variant — builds the per-room
        /// ISimulationCallbacks from the resolved <see cref="MatchConfigContext"/> (stage-specific world
        /// setup) plus the room logger. Use this ctor for multi-stage games.</param>
        public RoomManagerConfigBuilder(Func<MatchConfigContext, IKLogger, ISimulationCallbacks> callbacksFactory)
        {
            _config.CallbacksFactoryForMatch = callbacksFactory
                ?? throw new ArgumentNullException(nameof(callbacksFactory));
        }

        /// <summary>Sets the room/player/spectator limits. Optional — RoomManagerConfig defaults
        /// apply if omitted (MaxRooms=4, MaxPlayersPerRoom=4, MaxSpectatorsPerRoom=0).</summary>
        public RoomManagerConfigBuilder WithRoomLimits(int maxRooms, int maxPlayersPerRoom, int maxSpectatorsPerRoom = 0)
        {
            _config.MaxRooms = maxRooms;
            _config.MaxPlayersPerRoom = maxPlayersPerRoom;
            _config.MaxSpectatorsPerRoom = maxSpectatorsPerRoom;
            return this;
        }

        /// <summary>Uses a single SimulationConfig instance shared across all rooms.</summary>
        public RoomManagerConfigBuilder WithSimulationConfig(SimulationConfig shared)
        {
            if (shared == null) throw new ArgumentNullException(nameof(shared));
            _config.SimulationConfigFactory = () => shared;
            return this;
        }

        /// <summary>Creates a fresh SimulationConfig per room via the supplied factory.
        /// <para><inheritdoc cref="WithSimulationConfig(Func{MatchConfigContext, SimulationConfig})" path="/summary/para[1]"/></para></summary>
        public RoomManagerConfigBuilder WithSimulationConfig(Func<SimulationConfig> perRoom)
        {
            _config.SimulationConfigFactory = perRoom ?? throw new ArgumentNullException(nameof(perRoom));
            return this;
        }

        /// <summary>Creates a fresh SimulationConfig per room from the resolved match context
        /// (stage-specific). Takes precedence over the plain factory when set.
        ///
        /// <para><b>Three fields must be identical in every config this returns:</b>
        /// <c>MaxEntities</c>, <c>ComponentMaxCountOverrides</c> and the reservation-prune set.
        /// They are not per-room settings — they define the PROCESS-global component layout
        /// (<see cref="ECS.ComponentStorageRegistry.EnsureLayoutComputed(int, System.Collections.Generic.IReadOnlyDictionary{int, int}, System.Collections.Generic.IReadOnlyCollection{int})"/>),
        /// which is frozen by the first room and cannot change while other rooms are live. Vary
        /// anything else freely — stage id, match payload, tick interval, bot count.</para>
        ///
        /// <para>A room whose config disagrees is REFUSED at creation (the peer gets
        /// RoomNotFound) rather than allowed to reach the registry, because the registry's own
        /// answers — recompute in editor/test builds, throw in release — are both fatal to a
        /// process that already has rooms ticking. So the failure mode is a stage that never
        /// starts, which is quiet: source these three once at bootstrap.</para></summary>
        public RoomManagerConfigBuilder WithSimulationConfig(Func<MatchConfigContext, SimulationConfig> perMatch)
        {
            _config.SimulationConfigFactoryForMatch = perMatch ?? throw new ArgumentNullException(nameof(perMatch));
            return this;
        }

        /// <summary>Uses a single SessionConfig instance shared across all rooms.</summary>
        public RoomManagerConfigBuilder WithSessionConfig(SessionConfig shared)
        {
            if (shared == null) throw new ArgumentNullException(nameof(shared));
            _config.SessionConfigFactory = () => shared;
            return this;
        }

        /// <summary>Creates a fresh SessionConfig per room via the supplied factory.</summary>
        public RoomManagerConfigBuilder WithSessionConfig(Func<SessionConfig> perRoom)
        {
            _config.SessionConfigFactory = perRoom ?? throw new ArgumentNullException(nameof(perRoom));
            return this;
        }

        /// <summary>Creates a fresh SessionConfig per room from the resolved match context
        /// (stage-specific). Takes precedence over the plain factory when set.</summary>
        public RoomManagerConfigBuilder WithSessionConfig(Func<MatchConfigContext, SessionConfig> perMatch)
        {
            _config.SessionConfigFactoryForMatch = perMatch ?? throw new ArgumentNullException(nameof(perMatch));
            return this;
        }

        /// <summary>Supplies the inputs from which each room's EcsSimulation is derived: the shared
        /// asset registry and the rollback tick budget (defaults to 1, the server-driven no-rollback
        /// convention). maxEntities and deltaTimeMs are read from the simulation config at room-create
        /// time, honoring the fresh/shared choice; the per-room EcsSimulation logs through the room logger.</summary>
        public RoomManagerConfigBuilder WithDerivedSimulation(IDataAssetRegistry registry, int maxRollbackTicks = 1)
        {
            _config.AssetRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
            _config.SimulationMaxRollbackTicks = maxRollbackTicks;
            return this;
        }

        /// <summary>Sets the per-room match config source (multi-stage). Omit for single-stage open
        /// creation. When set, CreateRoom refuses rooms the source declines (peer gets RoomNotFound).</summary>
        public RoomManagerConfigBuilder WithMatchConfigSource(IMatchConfigSource source)
        {
            _config.MatchConfigSource = source ?? throw new ArgumentNullException(nameof(source));
            return this;
        }

        /// <summary>Sets a context-aware callbacks factory (stage-specific world setup) from the resolved
        /// match context, overriding any ctor-supplied plain factory. Takes precedence when set.</summary>
        public RoomManagerConfigBuilder WithCallbacks(Func<MatchConfigContext, IKLogger, ISimulationCallbacks> perMatch)
        {
            _config.CallbacksFactoryForMatch = perMatch ?? throw new ArgumentNullException(nameof(perMatch));
            return this;
        }

        /// <summary>
        /// Validates that every required input is present and returns the assembled config.
        /// Hard errors (always throw):
        ///   • No SimulationConfig / SessionConfig factory (plain or context-aware) set — the room manager
        ///     calls one unconditionally; a missing one would NRE at first room creation.
        ///   • No callbacks factory (plain or context-aware) set.
        ///   • AssetRegistry not set — the room manager derives each room's EcsSimulation from the
        ///     simulation config plus this registry; a missing one would NRE at first room creation.
        /// Advisory (strict=true throws; otherwise silent — the builder holds no logger):
        ///   • MaxRooms &lt;= 0 or MaxPlayersPerRoom &lt;= 0 — a server that can host no room/player.
        /// </summary>
        public RoomManagerConfig Build(bool strict = false)
        {
            if (_config.SimulationConfigFactory == null && _config.SimulationConfigFactoryForMatch == null)
                throw new RoomManagerConfigValidationException(
                    "SimulationConfig factory not set — call WithSimulationConfig(value | factory | match-factory).");
            if (_config.SessionConfigFactory == null && _config.SessionConfigFactoryForMatch == null)
                throw new RoomManagerConfigValidationException(
                    "SessionConfig factory not set — call WithSessionConfig(value | factory | match-factory).");
            if (_config.CallbacksFactory == null && _config.CallbacksFactoryForMatch == null)
                throw new RoomManagerConfigValidationException(
                    "Callbacks factory not set — use a constructor overload or WithCallbacks(match-factory).");
            if (_config.AssetRegistry == null)
                throw new RoomManagerConfigValidationException(
                    "Simulation source not set — call WithDerivedSimulation(registry, ...).");

            if (strict && (_config.MaxRooms <= 0 || _config.MaxPlayersPerRoom <= 0))
                throw new RoomManagerConfigValidationException(
                    $"RoomManagerConfig has non-positive limits (MaxRooms={_config.MaxRooms}, MaxPlayersPerRoom={_config.MaxPlayersPerRoom}) — server can host no room/player.");

            return _config;
        }
    }
}
