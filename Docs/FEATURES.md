# Klotho Framework Feature List

A deterministic multiplayer simulation framework for Unity.
Supports client-side prediction, rollback, and frame synchronization.

---

## Core

- **Tick-based simulation loop** — runs at a default 50 ms interval (20 ticks/sec)
- **ICommand-based input system** — serializable command interface (MoveCommand, ActionCommand, SkillCommand, etc.)
  - **ISystemCommand** — interface for system-only commands (PlayerJoinCommand, etc.)
  - **CommandBase** — abstract base class for commands
- **CommandFactory / CommandRegistry** — command type registration / construction / deserialization, integrated with the source generator
- **Client-side prediction** — predict missing inputs and execute without delay
- **Rollback & re-simulation** — ring-snapshot based; configurable max rollback ticks
- **Event system** — Predicted → Confirmed/Canceled lifecycle for SimulationEvent
  - Regular mode: emitted immediately
  - Synced mode: emitted only on verified ticks
  - EventBuffer / EventCollector / EventDispatcher — internal collection / dispatch
- **Hash-based desync detection** — engine-level local/remote hash comparison
- **SyncTestRunner** — GGPO-style determinism verification (snapshot → run → rollback → re-run → hash compare, no network)
- **SimulationConfig / ISimulationConfig** — tick interval, input delay, max rollback, sync-check interval, prediction toggle
- **SessionConfig / ISessionConfig** — session-init parameters (network mode, player info, etc.)
- **KlothoSession / IKlothoSession** — session lifecycle management (a wrapper around KlothoEngine)
  - **KlothoSessionSetup** — session-construction helper
  - **ISimulationCallbacks** — engine-lifecycle callback interface
- **KlothoEngine / IKlothoEngine** — engine state machine (Idle, WaitingForPlayers, Running, Paused, Finished)
  - **NetworkMode** — selectable P2P / ServerDriven topology
  - Partials: Rollback, TimeSync, ErrorCorrection, FullStateResync, LateJoin, Reconnect, Spectator, ServerDriven, ServerDrivenClient, Replay, FrameVerification, SyncTest, EventHelpers
- **DedicatedServerLoop** — dedicated-server loop (for standalone server processes)
- **Object pooling** — ListPool, DictionaryPool, StreamPool, CommandPool, EventPool (GC avoidance)
- **WarmupRegistry** — JIT-warmup pre-registration (command / event / message types)
- **Logging** — built on the standard `Microsoft.Extensions.Logging.ILogger<T>` interface. Implementation: ZLogger (`ZLogger.Unity`) + `Microsoft.Extensions.Logging.Abstractions`

## Deterministic Math

- **FP64** — 32.32 fixed-point number (64-bit)
  - Arithmetic with overflow protection
  - Math functions: Abs, Min, Max, Sqrt, Pow
  - Trigonometry: Sin, Cos, Tan, Asin, Acos, Atan2
- **FPVector2 / FPVector3 / FPVector4** — fixed-point vectors; Dot, Cross, Distance, Angle, Normalize
- **FPQuaternion** — fixed-point quaternion; Euler conversion, Slerp
- **FPMatrix2x2 / 3x3 / 4x4** — transform matrices, inverse, transpose
- **FPBounds2 / FPBounds3** — AABB bounding boxes
- **FPRay2 / FPRay3** — rays for raycasting
- **FPPlane / FPCapsule / FPSphere** — geometric primitives
- **FPHash** — FNV-1a deterministic hashing
- **FPAnimationCurve** — deterministic animation curves based on baked keyframes
- **DeterministicRandom** — seeded RNG
- **Unity conversions** — extension methods such as FPVector3 ↔ Vector3

## Deterministic Physics

- **FPPhysicsWorld** — physics-engine main loop
  - Apply gravity → sync colliders → broadphase → narrowphase → constraint solve → velocity integration
- **FPRigidBody** — mass, velocity, angular velocity, damping, restitution / friction; Dynamic / Static / Kinematic
- **FPPhysicsBody** — physics-body state wrapper (separate from FPRigidBody)
- **FPCollider** — union of Box, Sphere, Capsule, Mesh shapes
  - FPBoxShape / FPSphereShape / FPCapsuleShape / FPMeshShape — individual shape types
- **CollisionTests** — AABB, sphere, capsule, and mesh intersection tests
- **NarrowphaseDispatch** — per-shape-pair narrowphase dispatcher
- **FPCollisionResponse** — collision response (restitution / friction impulses)
- **FPPhysicsIntegration** — physics integrator (velocity / position update)
- **FPSweepTests** — CCD (Continuous Collision Detection)
- **FPConstraintSolver** — iterative impulse-based constraint solver
- **FPDistanceJoint / FPHingeJoint** — joint constraints
- **FPTriggerSystem** — trigger Enter / Stay / Exit callbacks
- **FPSpatialGrid** — grid-based spatial partitioning (broadphase, dynamic objects)
- **FPStaticCollider** — static colliders (immovable terrain / obstacles)
- **FPStaticBVH / FPBVHNode** — BVH (Bounding Volume Hierarchy) acceleration for static objects
- **FPStaticColliderSerializer** — serialization / deserialization for static-collider data

## Deterministic Navigation

- **FPNavMesh** — deterministic navmesh (baked from Unity NavMesh)
  - Vertex / triangle arrays, adjacency, grid acceleration
- **FPNavMeshSerializer** — navmesh-data serialization / deserialization
- **FPNavAgent** — agent state (speed, radius, stopping distance, path, etc.)
- **FPNavMeshPathfinder** — A* search (with FPNavMeshBinaryHeap)
- **FPNavMeshFunnel** — funnel-algorithm path smoothing
- **FPNavMeshPathLinearizer** — path post-processing (drops redundant waypoints)
- **FPNavMeshPath** — path data structure
- **FPNavMeshQuery** — triangle-containment test (barycentric)
- **FPNavAvoidance** — ORCA collision avoidance
- **FPNavAgentSystem** — batch agent update (path request → steering → avoidance → movement → navmesh constraint)

## Input

- **IInputHandler** — local input capture, command conversion
- **IInputBuffer** — per-tick / per-player command storage (ring buffer)
- **IInputPredictor** — missing-input prediction with accuracy tracking

## Network

- **INetworkTransport** — transport abstraction (Connect, Disconnect, Send, Receive)
- **IKlothoNetworkService / KlothoNetworkService** — P2P client-session management
  - Session phases: None → Lobby → Syncing → Synchronized → Countdown → Playing → Disconnected
  - Room create / join / leave, ready state, player info
- **IServerDrivenNetworkService / ServerDrivenClientService** — server-driven-mode client service
- **ServerNetworkService** — server-side network service (input collection, frame verification, state broadcast)
- **Handshake protocol** — SyncRequest → SyncReply → SyncComplete → Ready → GameStart
- **Bootstrap handshake (SD)** — server-driven first-tick alignment: BootstrapBegin → PlayerBootstrapReady (replaces implicit start tick)
- **Reconnect protocol** — ReconnectRequest → ReconnectAccept/Reject
- **Late-join protocol** — FullStateRequest → FullStateResponse → LateJoinAccept
- **Dynamic InputDelay / RecommendedExtraDelay** — RTT-driven extra InputDelay seeded on Sync / LateJoin / Reconnect (via `RecommendedExtraDelayCalculator`) and pushed mid-match (`RecommendedExtraDelayUpdate`, asymmetric UP/DOWN threshold, rate-limited per peer); applied via engine `ApplyExtraDelay` / `EscalateExtraDelay` / `OnExtraDelayChanged`
- **Quorum-miss watchdog (P2P)** — presumed-drop a peer whose input is missing at the verified head for `QuorumMissDropTicks`; reactive empty-fill activates before transport DisconnectTimeout. False-positive rollback on late real input
- **InputBuffer seal (P2P relay)** — sealed `(tick, playerId)` placeholders suppress relay of late real packets after the chain has advanced, preventing host↔guest divergence. Host-side relay block surfaced via `_relaySealDropCount` telemetry
- **Hash gate (post-`ApplyFullState`)** — every `ApplyFullState` entry point (LateJoin / InitialFullState / ResyncRequest / CorrectiveReset / Reconnect) verifies the post-restore hash and fires `OnHashMismatch(tick, localHash, remoteHash)`
- **Corrective reset (P2P, host-only)** — `OnHashMismatch` triggers host `TryCorrectiveReset` → `BroadcastFullState(..., FullStateKind.CorrectiveReset)` → host self-apply + guest apply with `ApplyReason.CorrectiveReset`. Cooldown via `CorrectiveResetCooldownMs` prevents broadcast storms. Match continues; `OnMatchReset(ResetReason.StateDivergence)` fires only when the post-restore hash matches (mismatch retries via the mid-match desync pipeline)
- **Chain stall watchdog (peer-local)** — `AbortMatch(AbortReason.ChainStallTimeout)` when `CurrentTick - LastVerifiedTick` exceeds `max(ReconnectTimeoutMs/TickIntervalMs + 100, MinStallAbortTicks)`. Distinct terminal state `KlothoState.Aborted` (see `KlothoStateExtensions.IsEnded()`)
- **RTT spike measurement** — `RttSpikeMetricsCollector` records per-spike windowed `chainBreak`, `rollbackDepth` mean/p95, `chainResumeLatencyMs`. Emitted at match-end via `[Metrics][RttSpike]`
- **PlayerRttSmoother** — 5-sample sliding median per player (≈5s window) feeding the dynamic-delay push decision
- **Command rejection feedback (SD)** — server unicast `CommandRejected` (PeerMismatch / PastTick / ToleranceExceeded / Duplicate) surfaced as engine `OnCommandRejected`
- **Match-end metrics** — JSON-line emit (`[Metrics][RttMatch]`, `[Metrics][BurstDuration]`, `[Metrics][PresumedDrop]`, `[Metrics][DynamicDelay]`, `[Metrics][LateJoin/Reconnect/Sync]`, `[Metrics][LagReductionLatency]`)
- **Spectator protocol** — SpectatorJoin → SpectatorAccept → SpectatorInput/Leave
- **ISpectatorService / SpectatorService** — spectator-entry / state-sync management
- **Message types**
  - Basic: PlayerReady, GameStart, Command, CommandAck, SyncHash, FullStateRequest/Response, Ping/Pong, JoinReject, ServerShutdown
  - Handshake: SyncRequest, SyncReply, SyncComplete, PlayerJoin, RoomHandshake
  - Reconnect: ReconnectRequest, ReconnectAccept, ReconnectReject
  - Late join: LateJoinAccept
  - Dynamic delay: RecommendedExtraDelayUpdate
  - Spectator: SpectatorJoin, SpectatorAccept, SpectatorInput, SpectatorLeave
  - Server-driven: ClientInput, ClientInputBundle, VerifiedState, InputAck, PlayerBootstrapReady, BootstrapBegin, CommandRejected
- **Multi-room server** — Room, RoomManager, RoomManagerConfig, RoomRouter, RoomScopedTransport
  - ServerLoop — server main loop coordinating multiple rooms
  - ServerInputCollector — server-side input collector
- **ITimeSyncService** — RTT measurement, clock-offset sync
- **SharedTimeClock** — shared game time

## Serialization

- **SpanWriter / SpanReader** — ref-struct, GC-free binary serialization
  - byte, bool, int16/32/64, float, double, string, FP64, FPVector3, etc.
- **ISpanSerializable** — Span-based serialization interface
- **SerializationBuffer** — managed byte buffer (pooled, IDisposable)
- **[KlothoSerializable(typeId)]** — type-registration attribute for the source generator
- **[KlothoOrder]** — specifies field serialization order
- **[KlothoIgnore]** — excludes a field from serialization
- **[KlothoHashIgnore]** — excludes a field from hash computation

## DataAsset

- **IDataAsset** — data-asset marker interface (`AssetId`)
- **IDataAssetSerializable** — data-asset serialization interface
- **DataAssetRef** — asset-ID reference wrapper (for component fields)
- **IDataAssetRegistry / DataAssetRegistry** — global data-asset registry
- **IDataAssetRegistryBuilder** — registry builder (register / lookup)
- **DataAssetTypeRegistry** — type-metadata registry
- **DataAssetReader / DataAssetWriter** — binary read / write
- **DataAssetRegistryExtensions** — lookup / register extension methods
- **[KlothoDataAsset(typeId)]** — data-asset type-registration attribute (source-generator integration)
- **JSON serialization** — `xpTURN.Klotho.DataAsset.Json` assembly (built on Newtonsoft.Json)
  - DataAssetJsonSerializer, DataAssetContractResolver, DataAssetSerializationBinder
  - Converters: FP64JsonConverter, FPVector2/3JsonConverter, DataAssetRefJsonConverter

## State

- **IStateSnapshot** — snapshot interface (Tick, Serialize/Deserialize, CalculateHash)
- **IStateSnapshotManager** — snapshot save / restore / lookup interface
- **RingSnapshotManager** — ring-buffer snapshot management (fixed capacity, O(1) insert / lookup, GC 0)

## ECS

- **EntityRef** — lightweight entity reference (8 bytes, generational index prevents dangling)
- **EntityManager** — entity-lifecycle management (generational index + free-list slot reuse, fixed capacity)
- **ComponentStorage\<T\>** — sparse-set component storage (`unmanaged` constraint, O(1) Add/Remove/Has)
- **ComponentStorageRegistry** — assembly-scan-based automatic component-type registration
- **Frame** — ECS world state (EntityManager + a set of ComponentStorages, Tick, hash, snapshots / rollback)
  - `Get<T>`, `Has<T>`, `Add<T>`, `Remove<T>`, `CreateEntity`, `DestroyEntity`
  - `Filter<T1..T5>` / `FilterWithout<T1..T5, TExclude>` — ref-struct, zero-GC queries (iterates the smallest storage first)
  - `CalculateHash()` — FNV-1a deterministic hash
  - `CopyFrom()` — BlockCopy-based snapshot / restore
- **IComponent** — `unmanaged` component marker interface
- **IEntityPrototype / EntityPrototypeRegistry** — entity-prototype interface and registry (data-driven entity creation)
- **[KlothoComponent(typeId)]** — component-type-registration attribute (source-generator integration; UserMinId=100)
- **[FrameData]** — frame-data field-serialization attribute
- **SystemPhase** — PreUpdate / Update / PostUpdate / LateUpdate
- **ISystem** — `Update(ref Frame)` system interface
- **IInitSystem / IDestroySystem** — init / destroy system interfaces
- **ICommandSystem** — `OnCommand(ref Frame, ICommand)` command-system interface
- **ISyncEventSystem** — system interface that processes events only on synced ticks
- **IEntityCreatedSystem / IEntityDestroyedSystem** — entity-lifecycle system interfaces
- **ISignal / ISignalOnComponentAdded\<T\> / ISignalOnComponentRemoved\<T\>** — component-change signal interfaces
- **SystemRunner** — system registration and phase-ordered execution (AddSystem → auto-sorted)
- **FrameRingBuffer** — Frame ring buffer (ECS-specific snapshot / rollback)
- **EcsStateSnapshot** — IStateSnapshot adapter (built on Frame.CopyFrom)
- **EcsSimulation** — ISimulation implementation (owns Frame + SystemRunner; pluggable into KlothoEngine)
- **FixedString32 / FixedString64** — `unmanaged` fixed-size UTF-8 strings (for component fields)
- **Built-in components** — TransformComponent, VelocityComponent, MovementComponent, HealthComponent, CombatComponent, OwnerComponent, PhysicsBodyComponent, NavigationComponent
- **Built-in systems** — MovementSystem, CombatSystem, PhysicsSystem, NavigationSystem, CommandSystem, EventSystem

## Replay

- **IReplayRecorder** — recording (start / record-tick / stop)
- **IReplayPlayer** — playback (load / play / pause / resume / stop / seek)
  - Playback speeds: 0.25x, 0.5x, 1x, 2x, 4x
- **IReplaySystem** — recording + playback combined, file save / load
- **IReplayData** — metadata + per-tick command-data serialization
- **File format** — `RPLY` magic (uncompressed) / LZ4-compressed stream (K4os.Compression.LZ4)
- **Implementations** — ReplayRecorder, ReplayPlayer, ReplaySystem, ReplayData

## Editor

- **FPNavMeshExporter** — Unity NavMesh → FPNavMesh conversion (triangle baking + grid build)
- **NavMesh Visualizer** — editor visualization tool
  - FPNavMeshVisualizerWindow — editor window
  - FPNavMeshSceneOverlay — scene-overlay rendering
  - FPNavMeshAgentSimulator — agent-movement test
  - FPNavMeshInteraction — click-to-navigate test
- **Static Collider Tools** — editor tooling for static colliders
  - FPStaticColliderExporterWindow — static-collider exporter window
  - FPStaticColliderConverter — Unity Collider → FPStaticCollider conversion

## Unity Integration

- **UKlothoBehaviour** — MonoBehaviour-based Klotho integration base class
- **USimulationConfig** — ScriptableObject SimulationConfig (inspector-editable, implements `ISimulationConfig`)
- **EcsDebugBridge** — editor debug bridge
- **View layer** (IMP24)
  - **EntityView / EntityViewComponent** — entity-view base class and view-component interface
  - **EntityViewFactory / IEntityViewPool / DefaultEntityViewPool** — view creation / pooling
  - **EntityViewUpdater** — simulation state → view sync
  - **VerifiedFrameInterpolator** — interpolation based on verified frames
  - **BindBehaviour** — component-binding MonoBehaviour
  - **UpdatePositionParameter / ViewFlags / ErrorVisualState** — auxiliary types
- **FPStaticColliderOverride** — MonoBehaviour for overriding static-collider parameters
- **FPStaticColliderVisualizer** — MonoBehaviour for scene visualization of static colliders

## Samples

- **Brawler** — fighting-game sample
  - **BrawlerGameController** — host / client init, session management
  - **BrawlerSimSetup** — ECS simulation composition (system / component registration)
  - **BrawlerInputCapture** — player-input capture and command conversion
  - **BrawlerCallbacks** — `ISimulationCallbacks` implementation (game-event handling)
  - **BrawlerViewSync / BrawlerEntityViewFactory** — simulation-state → Unity-view sync; view factory
  - **BrawlerCharacterViewRegistry** — character entity → view mapping
  - **BrawlerPlayerConfig / BrawlerReplayConfig** — sample configuration
  - **CombatHelper** — combat helper
  - **Commands** — AttackCommand, MoveInputCommand, SpawnCharacterCommand, UseSkillCommand
  - **Components** — BotComponent, CharacterComponent, GameSeedComponent, GameTimerStateComponent, ItemComponent, KnockbackComponent, PlatformComponent, SkillCooldownComponent, SpawnMarkerComponent
  - **Events** — ActionCompletedEvent, AttackActionEvent, AttackHitEvent, CharacterKilledEvent, CharacterSpawnedEvent, DashEvent, GameOverEvent, GroundSlamEvent, ItemPickedUpEvent, JumpEvent, RoundTimerEvent, SkillActionEvent, TrapTriggeredEvent
  - **Systems** — ActionLockSystem, BotFSMSystem, BoundaryCheckSystem, CombatSystem, GameOverSystem, GroundClampSystem, ItemSpawnSystem, KnockbackSystem, ObstacleMovementSystem, PlatformerCommandSystem, RespawnSystem, SavePreviousTransformSystem, SkillCooldownSystem, TimerSystem, TopdownMovementSystem, TrapTriggerSystem
  - **Bot HFSM** — BotHFSMRoot, BotActions, BotDecisions, BotFSMHelper (hierarchical-FSM-based bot AI)
  - **Prototypes** — `IEntityPrototype` implementations (KnightPrototype, MagePrototype, RoguePrototype, WarriorPrototype, ItemPickupPrototype, MovingPlatformPrototype)
  - **View** — CharacterView, CharacterAnimatorViewComponent, CharacterActionVfxViewComponent, ItemView, PlatformView, BrawlerCameraController, GameHUD, GameMenu, ResultScreen

## Tests

- **Core** — Command serialization, SyncTestRunner, FullStateResync
- **Integration** — late-join integration, server-driven-mode integration / benchmarks, replay integration (ReplayIntegrationTests), SD late-join connection
- **Network** — Handshake, Reconnect, Spectator, LateJoin, ServerDriven unit tests; message serialization; LiteNetLib integration
- **ECS** — EntityManager, ComponentStorage, Frame, Filter, SystemRunner, FrameRingBuffer, EcsStateSnapshot, EcsSimulation; built-in systems (movement / combat / physics / nav / command / event); SourceGenerator validation; OOP hash comparison
- **Deterministic** — Math (FP64 / Vector / Quaternion / Matrix); Geometry (Bounds / Ray / Plane / Capsule / Sphere); Physics (RigidBody / Collider / Shape / Broadphase / Narrowphase / Sweep / Constraint / StaticBVH / PhysicsWorld); Navigation (Pathfinder / Funnel / Linearizer / Avoidance / Query / Serializer); Random; Curve
- **DeterminismVerification** — determinism stress-verification framework (ArithmeticStressSystem, EntityLifecycleSystem, RandomStressSystem, TrigStressSystem, DeterminismVerificationRunner, ServerDrivenDeterminismRunner)
- **State** — RingSnapshotManager
- **Input** — InputBuffer
- **Helpers** — KlothoTestHarness, TestTransport, TestSimulation

---

*Last updated: 2026-04-24*
