# Klotho Framework Feature List

A deterministic multiplayer simulation framework for Unity and Godot (.NET).
Supports client-side prediction, rollback, and frame synchronization.

---

## Core

- **Tick-based simulation loop** — runs at a default 50 ms interval (20 ticks/sec)
- **ICommand-based input system** — serializable command interface (MoveCommand, ActionCommand, SkillCommand, etc.)
  - **ISystemCommand** — interface for system-only commands (PlayerJoinCommand, etc.)
  - **CommandBase** — abstract base class for commands
  - **StopCommand** — explicit "no movement, no action" intent emitted by clients during the `EndGracePolicy.Pause` grace window; SD/P2P unified
- **CommandFactory / CommandRegistry** — command type registration / construction / deserialization, integrated with the source generator
- **Client-side prediction** — predict missing inputs and execute without delay
- **Rollback & re-simulation** — ring-snapshot based; configurable max rollback ticks
- **Event system** — Predicted → Confirmed/Canceled lifecycle for SimulationEvent
  - Regular mode: emitted immediately
  - Synced mode: emitted only on verified ticks
  - EventBuffer / EventCollector / EventDispatcher — internal collection / dispatch
- **Hash-based desync detection** — engine-level local/remote hash comparison
  - **StateHashBreakdown** — per-component-type split of the frame hash, so a mismatch names the component type instead of just a number. `LogComponentHashes` dumps `count` + `hash` per typeId for log diffing
  - **StateHashRing** — rolling per-tick, per-type hash history (`ISimulationConfig.DiagnosticHistoryTicks`, default 60, `0` = off and free) dumped on detection via `FlushHashHistory`, so the *first* diverged tick is visible rather than the tick you noticed
  - **Layout fingerprint** — the component registry folds `maxEntities`, the sorted typeId set, type names, slot capacity (`MaxCount`) and each type's `[KlothoCleanup]` mode into one value. The registered type set is itself state-hash input, so this is the check to read *first*: a peer loading an extra assembly's components diverges from tick 0 while every per-component number looks like a symptom
  - **Environment fingerprint** — static colliders XOR navmesh XOR a game-owned slot (`IGameFingerprintSource`), for data that must be identical across builds but is deliberately outside the state hash (a shape catalog, a tuning table, an asset-id map). Each source is reported separately, so a game-data difference does not read as a NavMesh problem
  - See **[DesyncDiagnostics.md](DesyncDiagnostics.md)** for the symptom table and the diagnostic funnel
- **SyncTestRunner** — GGPO-style determinism verification (snapshot → run → rollback → re-run → hash compare, no network)
- **DeterminismAnalyzer (build-time)** — Roslyn diagnostic analyzer shipped in `KlothoGenerator.dll` that flags determinism hazards at compile time, before they surface as replay/rollback desync. Warnings (category `KlothoGenerator.Determinism`): `KLOTHO_DET002` float/double in a deterministic context · `KLOTHO_DET003` non-deterministic API/type (`Mathf`, `Random`, `System.Math`, `DateTime`, float-backed `UnityEngine.Vector2/3/4`/`Quaternion`/`Matrix4x4`) · `KLOTHO_DET004` `UnityEngine.Time` (wall-clock). Scoped to deterministic-context types (those implementing a deterministic interface / inheriting a deterministic base, plus ref-`Frame` helper methods); the FP64 conversion boundary (`FromFloat`/`FromDouble`/`ToFloat`/`ToDouble`/`ToFP64`) is exempt; test / tool assemblies are skipped
- **SimulationConfig / ISimulationConfig** — tick interval, input delay, max rollback, sync-check interval, prediction toggle. Also the peer-local diagnostic knobs (`DiagnosticHistoryTicks`, `SystemPerfMonitoring`) and the two determinism-input memory knobs (`ComponentMaxCountOverrides`, `PrunedComponentTypeIds` via `SetRuntimePrunedComponentTypeIds` — authority-set, wire-propagated to joining peers)
- **SessionConfig / ISessionConfig** — session-init parameters (network mode, player info, etc.)
- **KlothoSession / IKlothoSession** — session lifecycle management (a wrapper around KlothoEngine)
  - **KlothoSessionSetup** — session-construction helper. Field-injected:
    - `CredentialsStore` (IReconnectCredentialsStore) — warm-reconnect save/clear, formerly wired by the game after Create
    - `LifecycleObserver` (IKlothoSessionObserver) — bulk-subscribed at Create and bulk-unsubscribed at Stop (replaces per-event `+=` wiring)
    - `AppVersion` / `DeviceIdProvider` — reconnect credential issuance inputs
  - **KlothoSession.CreateSpectator** — spectator-mode factory (takes `SpectatorSessionSetup` + `CallbacksFactory` that runs after server config arrives)
  - **SpectatorSessionSetup / SpectatorCallbacks** — spectator-only setup; no `SessionConfig`/`CredentialsStore` (server-authoritative arrival via `SpectatorAcceptMessage`)
  - **IKlothoSessionObserver** — aggregated session-level lifecycle callbacks (`OnPlayerDisconnected/Reconnected`, `OnReconnecting/Failed/Reconnected`, `OnCatchupComplete`, `OnResyncCompleted`, `OnGameStart`, `OnMatchAborted/Ended/Reset`, `OnSessionStopped`)
  - **ReconnectFailedException** — thrown by `KlothoConnectionAsync.ReconnectAsync` / `KlothoConnection.Reconnect` on server reject; carries the rejection `Reason` (a `ReconnectRejectReason` enum)
  - **ISimulationCallbacks** — engine-lifecycle callback interface
  - **Replay initial-state snapshot auto-inject** — `Engine.StartReplay` automatically replays `InitialStateSnapshot` from the metadata, removing the game-side `OnGameStart += InjectInitialStateSnapshot` wiring
  - **Pause-grace StopCommand auto-inject** — during the `EndGracePolicy.Pause` grace window, the engine emits the per-tick `StopCommand` automatically; games no longer hand-roll the grace-window command stream
  - **DynamicInputDelayPolicy** — built-in client-reactive PastTick + rollback-burst escalation policy (formerly hand-rolled in the sample); thresholds sourced from `SimulationConfig`, attached automatically on non-host sessions
  - **IKlothoSessionObserver state-change callbacks** — `OnStateChanged` / `OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged`. Replaces per-frame status polling — the game implements them once on the observer. Backed by `KlothoEngine.OnStateChanged` and `IKlothoNetworkService.OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` (forwarded from both network-service and spectator-service paths)
  - **KlothoSession.PlayerCount** — unified read-only getter (`NetworkService → SpectatorService → 0` fallback) so host / guest / spectator all expose the same player-count surface
- **INetworkServiceReceiver** — opt-in marker interface for `ISimulationCallbacks` implementations that need the `IKlothoNetworkService` handle on host/guest entry. `KlothoSessionFlow.FireOnSessionCreated` dispatches `SetNetworkService` just before invoking the observer's `OnSessionCreated` (kind-gated to Host/Guest, callbacks-non-null, `is INetworkServiceReceiver recv`). Implementations that don't need the handle simply omit the interface — no empty-body `SetNetworkService` required
- **KlothoSessionFlow / KlothoFlowSetup** — recommended session construction layer
  - Mode-dispatched entry points: `StartHost` / `StartHostAndListen` (P2P host) · `JoinAsync(strategy, transport, host, port, roomId, sessionCfg, ct)` — the unified guest entry, with `JoinP2PAsync` / `JoinServerDrivenAsync` convenience overloads that delegate to it · `ReconnectAsync` · `SpectateAsync` · `StartReplayFromFile`. Multi-mode games call `JoinAsync` with `KlothoModeStrategy.Resolve(simCfg)` and never branch on mode at the join site
  - **StartHostAndListen** — single-entry host bootstrap that folds `StartHost` + `HostGame` + `Transport.Listen` into one call (reads `MaxPlayers` from `sessionConfig`), mirroring the guest path's single-call symmetry. Returns the running session, or `null` on listen-bind failure (session already torn down); on other failures (e.g. `HostGame`/`CreateRoom`) it `Stop()`s then rethrows, so a half-started session is never orphaned. The low-level `StartHost` remains as an escape hatch for custom ordering / multi-transport / tests
  - **Single role-bearing session-created callback** — `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)`; branch on `kind` (`Host` / `Guest` / `Replay` / `Spectator`). Removes the `engine.IsReplayMode` / `engine.IsSpectatorMode` 2-flag mode-by-flag branching from game-side dispatch (the former per-mode `KlothoSessionFlow.On*SessionCreated` events are removed)
  - **`InitialPlayerConfigFactory`** — auto `SendPlayerConfig` on guest / reconnect paths (skipped on spectator / replay). Invoked per-session so it always observes the latest user selection
  - **`SpectatorTransportFactory`** — invoked from `SpectateAsync(host, port, roomId, ct)` so the library owns the transport instance. The transport-injection overload remains as the escape hatch
  - **`StartReplayFromFile(path)`** — 1-call file-to-session entry (throws `xpTURN.Klotho.Replay.ReplayLoadException` on load failure). Replaces game-side `ReplaySystem.LoadFromFile` + `simConfig.Validate()` + `StartReplay` boilerplate
- **IKlothoModeStrategy** — per-mode dispatcher interface with P2P / ServerDriven implementations and a `KlothoModeStrategy.Resolve(simCfg)` static factory. Game code branches on the strategy rather than inspecting `simCfg.Mode` directly
- **Idempotent teardown** — `KlothoSessionDriver.DetachAndStop` is re-entrant-safe via an internal guard (`_stopping`); a duplicate teardown call is a no-op, so game code routes all teardown through it instead of carrying per-game `_isStopping` / `_teardownInvoked` flags. The `OnSessionStopped` observer callback fires regardless of which entry path (Driver.DetachAndStop / Session.Stop direct) initiated teardown
- **Reconnect-credentials teardown opt-out** — `KlothoSession.Stop` / `KlothoSessionDriver.DetachAndStop` / `IKlothoNetworkService.LeaveRoom` accept `bool keepReconnectCredentials = false`. Default `false` discards persisted cold-start credentials on graceful session end (user-intent leave, match end, failed bootstrap). Process-exit entry points pass `true`: `KlothoSessionDriver.OnDestroy` does this internally and game code mirrors it in `OnApplicationQuit` / `OnDestroy`. Restores cold-start Reconnect across normal app quits — previously every quit silently wiped the store
- **LateJoinNotificationMessage** — host (P2P) / server (SD) broadcasts to existing peers and spectators on mid-match late-join so they update `OnPlayerJoined` / `PlayerCount` without polling. Forged-sender guards (P2P `!IsHost`, SD `peerId != 0`) + idempotency against the local roster. NetworkMessageType=75
- **FaultInjection macro-agnostic surface** — `FaultInjectionRuntime.AttachToSession` / `FaultInjectionLoader.TryLoadAndApply` / `FaultInjection` static collections are callable without `#if KLOTHO_FAULT_INJECTION` guard. Undefined builds return null / false / empty stub. Library-internal reader bodies retain their macro guards — release cost stays at zero
- **KlothoEngine / IKlothoEngine** — engine state machine (Idle, WaitingForPlayers, BootstrapPending, Running, Paused, Ending, Finished, Aborted)
  - **NetworkMode** — selectable P2P / ServerDriven topology
  - Partials: Rollback, TimeSync, ErrorCorrection, FullStateResync, LateJoin, Reconnect, Spectator, ServerDriven, ServerDrivenClient, Replay, FrameVerification, SyncTest, EventHelpers
  - **`IKlothoEngine.IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null) → IReliableCommandHandle`** — framework-owned reliable-command transaction. The tracker (`ReliableCommandTracker`) handles duplicate / past-tick reject escalation, retry-interval cooldown, empty-move collision avoidance, and `OnResyncCompleted` reset. Handle surface: `WouldCollideAt(tick)` / `Confirm()` / `Cancel()` / `OnRejected` / `OnResolved` / `OutstandingTargetTick`. `ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches the prior Brawler spawn invariant; games can supply a custom policy for other reliable-input scenarios
  - **Interface surface (`IKlothoEngine`)** — `PredictedFrame` / `RenderClock` / `OnEventPredicted` / `OnEventConfirmed` / `OnEventCanceled` / `Logger` now live on the interface (previously concrete-only). `IKlothoSession.Engine` exposes the engine as `IKlothoEngine` (the concrete `KlothoSession.Engine` returns `KlothoEngine`). Games depend on the interface, not the concrete class
- **DedicatedServerLoop** — dedicated-server loop (for standalone server processes)
- **Object pooling** — ListPool, DictionaryPool, StreamPool, CommandPool, EventPool (GC avoidance)
- **WarmupRegistry** — JIT-warmup pre-registration (command / event / message types)
- **Logging** — built on `xpTURN.Klotho.Logging` (`IKLogger`) — in-house structured logging with zero external dependencies. Optional MEL interop via the `Plugins~/Logging.Mel` adapter (consumer-provided `Microsoft.Extensions.Logging.Abstractions.dll`)

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
- **Engine conversions** — extension methods such as `FPVector3 ↔ Vector3` (same method names `ToVector3()` / `ToFPVector3()` on both engines; `FP*.Unity.cs` → `UnityEngine.Vector3`, `FP*.Godot.cs` → `Godot.Vector3`). Geometry adapters: `FPRay3` (tuple decomposition + `ToRayQuery` → `PhysicsRayQueryParameters3D` on Godot; `ToRay` → `UnityEngine.Ray` on Unity), `FPPlane` (`ToPlane`/`ToFPPlane` — sign inversion on Godot: `D = −distance`), `FPBounds3` (`ToAabb`/`ToFPBounds3` on Godot; `ToBounds`/`ToFPBounds3` on Unity)

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

- **FPNavMesh** — deterministic navmesh (engine-agnostic runtime; the `.bytes` data is baked from a Unity NavMesh via the Editor exporter, then loaded on either engine)
  - Vertex / triangle arrays, adjacency, grid acceleration
- **FPNavMeshSerializer** — navmesh-data serialization / deserialization
- **NavAgentComponent** — ECS agent component (speed, radius, stopping distance, corridor, status)
- **FPNavMeshPathfinder** — A* search (with FPNavMeshBinaryHeap)
- **FPNavMeshFunnel** — funnel-algorithm (SSFA) path smoothing
- **NavCorridorHelper** — corridor / path-following helpers
- **FPNavMeshTriangle** — triangle struct (adjacency, packed portal-flip bits, area, cost)
- **FPNavMeshQuery** — triangle-containment test (barycentric)
- **FPNavAvoidance** — ORCA collision avoidance, agent-to-agent **and** agent-to-static via obstacle lines
- **FPNavMeshObstacleExtractor** — extracts ORCA static-obstacle rings from a navmesh boundary (edges with `neighbor == -1`); load/build-time only. Second source: convex blocks supplied by a generator, so a game can feed both
- **FPNavMeshBuildPipeline** — engine-agnostic geometry pipeline (weld → grid-snap → T-junction split → triangulate → adjacency) shared by the Unity `FPNavMeshExporter` and the Godot exporter
- **FPNavAgentSystem** — batch agent update (path request → steering → avoidance → movement → navmesh constraint); also the `INavFingerprintSource` for the cross-peer environment check

### Runtime rebake — the NavMesh changes mid-match

- **FPNavMeshRebaker** — deterministic runtime re-carve: each building footprint is cut out of the walkable region and the remainder re-triangulated with exact integer predicates over a 1/1024-metre placement grid (from-scratch constrained Delaunay), so Unity / Godot / the .NET server produce a **bit-identical** mesh from the same command. `CreateSnapshot` (per stage, immutable, shared read-only) + `CreateContext` (per room, owns the work buffers); `TryRebake` / `TryRebakePlacements` return `false` plus a typed reason instead of throwing
- **Placement validation is the same code path as the rebake** — `TryValidateOne` / `TryValidateOnePlacement` answer a placement-UI ghost without cutting a triangle, so a preview that says yes over a rebake that says no cannot happen. Allocation-free in steady state, so it can follow the cursor every frame
- **FPBoundaryPlacementPolicy** — `Reject` (0, historical: any boundary contact refuses), `Touch` (1: flush against a wall — this is what lets a chain of buildings seal a corridor), `ClipOverlap` (2: a footprint may hang past a wall at any angle; the clipped shape is what gets carved)
- **FPBuildingShapeCatalog / FPBuildingShapeExpansion** — footprints are entries in a per-stage table (rectangle, oriented box, hexagon), not arbitrary geometry. Every footprint is widened by the bake agent radius before carving, so the expansion table hands back the tiling delta / lattice snap instead of leaving the game to guess the spacing that leaves two buildings flush
- **FPNavMeshRebakeDriver** — register it as a system and it keeps the installed mesh equal to what the frame says it should be, every tick. This is the part a rollback game cannot get right on its own: a mesh installed on a *predicted* tick is not rewound by the rollback. Two game-supplied seams — `IFPNavMeshPlacementSource` (where placements live in frame state) and `IFPNavMeshInstaller`
- **FPNavMeshPlacementValidator** — builds the active placement set from frame state, hands out the next placement number, and answers "does this set bake?" identically on every peer
- **FPNavAgentInstaller** — the install-and-reseed pair as two *named* calls on purpose: installing is derived state and may be skipped, reseeding writes hashed frame state and must run on **every** execution of a tick. Fusing them hides the reseed behind a peer-local comparison that does not roll back
- **Deterministic time-slicing** — a rebake can be spread across the frames between a placement and the tick it takes effect on (peer-local budget, default 20,000 work units/frame), byte-identical however many slices it took; missing the deadline just completes the remainder on the spot. Cut the worst single frame from 9.33 ms to 2.13 ms on a 204,800-triangle stage
- **FPNavMeshRebakeBufferPool** — pooled work buffers recycled across swaps, so installing a rebaked mesh allocates nothing
- **The mesh stays out of the state hash** — peers compare one small value instead (`INavFingerprintSource` → the environment fingerprint), so a diverged mesh surfaces at join / resync rather than several ticks later as an unexplained agent-position desync
- Full guide: **[Navigation.Rebake.md](Navigation.Rebake.md)**

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
- **Setup validation before the first tick** — `PlayerReadyMessage` carries the layout + environment fingerprints, and because the host relays a ready verbatim every peer compares against every other, so a cold-start match that never delivers a FullState is still checked. A **layout** difference is state-hash input and therefore fatal: the side with transport control refuses the peer (`JoinFailReason.LayoutMismatch`, wire code 12) and a peer without it leaves (`AbortReason.LayoutMismatch`); an **environment** difference stays a warning and is only compared before the match runs, since runtime rebakes move it legitimately. `KlothoEngine.CheckReadyFingerprints` → `ReadyFingerprintVerdict`; `KlothoSessionSetup.AllowLayoutMismatch` / `SpectatorSessionSetup.AllowLayoutMismatch` (default `false`) is the per-peer dev escape hatch — deliberately on the setup and not in `ISimulationConfig`, because a guest runs the config it received over the wire. The refusal message names both sanctioned fixes (match the assembly sets, or prune the difference authority-side). A spectator gets the check for free from the relayed readies
- **Bootstrap handshake (SD)** — server-driven first-tick alignment: BootstrapBegin → PlayerBootstrapReady (replaces implicit start tick)
- **Reconnect protocol** — ReconnectRequest → ReconnectAccept/Reject
- **Late-join protocol** — FullStateRequest → FullStateResponse → LateJoinAccept
- **Dynamic InputDelay / RecommendedExtraDelay** — RTT-driven extra InputDelay seeded on Sync / LateJoin / Reconnect (via `RecommendedExtraDelayCalculator`) and pushed mid-match (`RecommendedExtraDelayUpdate`, asymmetric UP/DOWN threshold, rate-limited per peer); applied via engine `ApplyExtraDelay` / `EscalateExtraDelay` / `OnExtraDelayChanged`
- **Quorum-miss watchdog (P2P)** — presumed-drop a peer whose input is missing at the verified head for `QuorumMissDropTicks`; reactive empty-fill activates before transport DisconnectTimeout. False-positive rollback on late real input
- **InputBuffer seal (P2P relay)** — sealed `(tick, playerId)` placeholders suppress relay of late real packets after the chain has advanced, preventing host↔guest divergence. Host-side relay block surfaced via `_relaySealDropCount` telemetry
- **Hash gate (post-`ApplyFullState`)** — every `ApplyFullState` entry point (LateJoin / InitialFullState / ResyncRequest / CorrectiveReset / Reconnect) verifies the post-restore hash and fires `OnHashMismatch(tick, localHash, remoteHash)`
- **Corrective reset (P2P, host-only)** — `OnHashMismatch` triggers host `TryCorrectiveReset` → `BroadcastFullState(..., FullStateKind.CorrectiveReset)` → host self-apply + guest apply with `ApplyReason.CorrectiveReset`. Cooldown via `CorrectiveResetCooldownMs` prevents broadcast storms. Match continues; `OnMatchReset(ResetReason.StateDivergence)` fires only when the post-restore hash matches (mismatch retries via the mid-match desync pipeline)
- **Chain stall watchdog (peer-local)** — `AbortMatch(AbortReason.ChainStallTimeout)` when `CurrentTick - LastVerifiedTick` exceeds `max(ReconnectTimeoutMs/TickIntervalMs + 100, MinStallAbortTicks)`. Distinct terminal state `KlothoState.Aborted` (see `KlothoStateExtensions.IsEnded()`)
- **Normal-end lifecycle** — `IMatchEndEvent` (Synced marker, e.g. `GameOverEvent`) fires `OnMatchEnded(tick, evt)` exactly once on first verification. The event may additionally implement `IMatchResultProvider` to expose a game-authored opaque result blob (`MatchResultData : byte[]`, null = none) the server authority reads at that point. `EndGracePolicy.Continue` keeps the simulation running through the grace window; `EndGracePolicy.Pause` also stays in `Running` but auto-injects a per-tick `StopCommand` on the deterministic input stream (characters halt; transport keepalives preserved) — it does not transition to `Ending`. Grace durations: `EndGraceMs` (server `Room` drain), `ClientShutdownGraceMs` (client self-shutdown — must stay below `EndGraceMs`). `EndReason.MatchEnded` / `MatchAborted` classifies the drain trigger
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
- **Multi-room server** — Room, RoomManager, RoomManagerConfig, RoomManagerConfigBuilder, RoomRouter, RoomScopedTransport
  - ServerLoop — server main loop coordinating multiple rooms
  - ServerInputCollector — server-side input collector
- **Per-room match config** — `IMatchConfigSource` resolves each room's stage (`StageId`) and an opaque per-match payload (`MatchConfigData`) at creation time; `StaticMatchConfigSource` (a lobbyless room→stage table) or a lobby-backed source. Declining a room refuses creation (client turned away with room-not-found). `RoomManagerConfigBuilder` gained a match-aware callbacks constructor + `WithMatchConfigSource` + per-match `WithSimulationConfig`/`WithSessionConfig`/`WithCallbacks` overloads
  - Carried to every peer on the existing config channel, so **one dedicated server can host rooms on different stages**; the game selects its stage assets from the received `StageId` and decodes `MatchConfigData` for per-match knobs (game mode / rules / difficulty). Empty by default (single stage, no-op)
- **Match result propagation (server → lobby)** — the mirror of per-room match config: the match-end event implements `IMatchResultProvider` (opaque game-authored blob, read server-side at `OnMatchEnded`; opt-in), and `RoomManagerConfig.OnRoomCreated` / `OnRoomDraining` give a host per-room subscription seams (creation-time attach; drain notification fired once on the room's own update thread)
  - The reference SD lobby stack ships the blob plus a verified identity roster (leavers preserved) to the lobby as `MatchResult`/`MatchResultAck` — at-least-once delivery (ack + resend + crash-backstop journal), idempotent by a lobby-minted per-match instance key, with abort / abandoned-match notifications. See [LobbyIntegrationGuide §4-F](LobbyIntegrationGuide.md#4-f-match-result-reporting-sd-multi-room)
- **ITimeSyncService** — RTT measurement, clock-offset sync
- **SharedTimeClock** — shared game time

## Serialization

- **SpanWriter / SpanReader** — ref-struct, GC-free binary serialization (little-endian)
  - primitives (byte, bool, int/uint 16/32/64, string [UTF-8], byte[]); FP types via `FPSpanExtensions` (FP64, FPVector2/3/4, FPQuaternion); ECS handles (EntityRef, DataAssetRef); composite physics (FPRigidBody, FPCollider). **No float/double** — fixed-point only, for determinism
- **ISpanSerializable** — Span-based serialization interface
- **SerializationBuffer** — managed byte buffer (pooled, IDisposable)
- **[KlothoSerializable(typeId)]** — type-registration attribute for the source generator (Entity / Command / Message / Event categories, inferred from the base class)
- **[KlothoSerializableStruct]** — marks an `unmanaged partial struct` as a reusable inline field bundle (nested codec; no wire id / factory) embeddable in components and other serializable types
- **[KlothoOrder]** — specifies field serialization order
- **[KlothoIgnore]** — excludes a field from serialization
- **[KlothoHashIgnore]** — excludes a field from hash computation
- See **[Serialization.md](Serialization.md)** for the full codec, supported-type table, and diagnostics reference

## DataAsset

- **IDataAsset** — data-asset marker interface (`AssetId`)
- **IDataAssetSerializable** — data-asset serialization interface
- **DataAssetRef** — asset-ID reference wrapper (for component fields)
- **IDataAssetRegistry / DataAssetRegistry** — global data-asset registry
  - **`Get<T>()` / `TryGet<T>(out T)`** — typed lookup that auto-resolves the `AssetId` named-arg on `[KlothoDataAsset]`; throws `InvalidOperationException` when the asset omits `AssetId` (avoids silent failures). The existing `Get<T>(int id)` / `TryGet<T>(int id, out T)` overloads remain for multi-instance fan-out (e.g. `Get<BotDifficultyAsset>(1700 + slotIndex)`)
  - **`GetByKey<T>(string)` / `TryGetByKey<T>(string, out T)`** — concrete-type lookup via the `Key` named-arg on `[KlothoDataAsset]`; backed by a `(Type, string)` tuple index built at `Register` time
- **IDataAssetRegistryBuilder** — registry builder (register / lookup)
- **DataAssetTypeRegistry** — type-metadata registry
- **DataAssetReader / DataAssetWriter** — binary read / write
- **DataAssetRegistryExtensions** — lookup / register extension methods
- **[KlothoDataAsset(typeId, AssetId = ..., Key = ...)]** — data-asset type-registration attribute (source-generator integration). Positional `typeId` is the wire-stable type discriminator; named-arg `AssetId` is the runtime instance id (separate plane); named-arg `Key` is an optional string handle for `GetByKey<T>`. The generator emits a `private readonly int _assetId` backing field, a `public int AssetId => _assetId` expression-bodied property, a `ctor(int)`, and — when `AssetId` is provided — a parameterless `ctor() : this(AssetIdFromAttribute)`
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
- **ComponentStorageFlat\<T\>** — sparse-set view over a slice of the frame heap (`unmanaged` constraint, O(1) Add/Remove/Has)
- **ComponentStorageRegistry** — assembly-scan-based automatic component-type registration
- **Frame** — ECS world state (EntityManager + a set of ComponentStorages, Tick, hash, snapshots / rollback)
  - `Get<T>`, `Has<T>`, `Add<T>`, `Remove<T>`, `CreateEntity`, `DestroyEntity`
  - `Filter<T1..T5>` / `FilterWithout<T1..T5, TExclude>` — ref-struct, zero-GC queries (iterates the smallest storage first)
  - `CalculateHash()` — FNV-1a deterministic hash
  - `CopyFrom()` — BlockCopy-based snapshot / restore
- **IComponent** — `unmanaged` component marker interface
- **IEntityPrototype / EntityPrototypeRegistry** — entity-prototype interface and registry (data-driven entity creation)
- **[KlothoComponent(typeId, MaxCount = n)]** — component-type-registration attribute (source-generator integration; `UserMinId=100`). The optional `MaxCount` caps the type's reserved heap slots at `min(MaxCount, maxEntities)` instead of one per entity — a determinism input, so every peer must agree. `[StructLayout(LayoutKind.Sequential, Pack = 4)]` is required alongside it (`KLOTHO_STRUCT_LAYOUT_MISSING`, error)
- **[KlothoSingletonComponent]** — marks a component type as singleton (exactly one carrier entity per frame). `Frame.Add<T>` throws on a second carrier; read via `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>`. Source generator emits an `IsSingleton` flag onto `ComponentStorageRegistry.TypeIdCache<T>`
- **[KlothoCleanup(CleanupMode)]** — declares a component that lives exactly one tick. `RemoveComponent` empties the storage through the type-erased clear dispatch (O(sparse), no iteration); `DestroyEntity` destroys the carrier instead (pre-allocated buffer, `IsAlive`-guarded, so two markers destroy once). The engine runs both passes after every system and before the tick advances, so the cleaned state is what gets hashed and snapshotted — no cleanup system to write and no mutate-during-iteration trap. The mode is folded into the layout fingerprint. Analyzer rules: `KLSG_ECS007` (cleanup on a core component, warning) · `KLSG_ECS008` (`DestroyEntity` on a singleton, error) · `KLSG_ECS009` (undefined `CleanupMode` value, error)
- **[KlothoCoreComponent]** — marks a component type engine-essential: force-excluded from the pruning denylist, so listing one by mistake cannot drop it. Orthogonal to lifetime
- **[Primitive]** — renders a value struct on one line via `ToString()` in the ECS inspector instead of recursing into its fields. Editor display only — the source generator never reads it, so serialization / hashing / the wire are unaffected. Applied to `FixedString32` / `FixedString64`
- **[FrameData]** — declared, but nothing consumes it yet (no generator or runtime reader)
- **SystemPhase** — PreUpdate / Update / PostUpdate / LateUpdate
- **ISystem** — `Update(ref Frame)` system interface
- **IInitSystem / IDestroySystem** — init / destroy system interfaces
- **ICommandSystem** — `OnCommand(ref Frame, ICommand)` command-system interface
- **ISyncEventSystem** — system interface that processes events only on synced ticks
- **IEntityCreatedSystem / IEntityDestroyedSystem** — entity-lifecycle system interfaces
- **ISignalOnComponentAdded\<T\> / ISignalOnComponentRemoved\<T\>** — component-change signals, called by `Frame.Add`/`Remove`, by `DestroyEntity` per component, and by the `[KlothoCleanup]` clear per entity. Free when no system implements them (per-typeId gate). Rules in [ECS.md](ECS.md) §6
- **ISignal / SignalInvoker\<TSignal\> / SystemRunner.Signal\<TSignal\>** — a separate, general broadcast to systems implementing a game's own `ISignal`-derived interface. Unrelated to the component signals above (which do not derive from `ISignal`), and the invocation delegate allocates per call — not for per-tick paths
- **SystemRunner** — system registration and phase-ordered execution (`AddSystem` → auto-sorted). Also owns the two built-in passes: previous-transform capture ahead of `PreUpdate`, and the `[KlothoCleanup]` clear/destroy passes after the last system. `AddSystem(sys, phase, group: "combat")` takes an optional **perf-report label** — diagnostic only, never a sort key, never folded into the layout fingerprint
- **ISnapshotParticipant** — for state a system owns *outside* components (a physics broadphase, an accumulator). `GetSnapshotSize` / `SaveSnapshot` / `RestoreSnapshot`; the ring buffer captures and restores it alongside the frame, wired automatically at `AddSystem`
- **Frame-heap memory tuning** — per-type slot caps (`MaxCount` on the attribute, or `ISimulationConfig.ComponentMaxCountOverrides`) and **pruning** (`SetRuntimePrunedComponentTypeIds` — a *denylist*, so anything unlisted stays reserved; `[KlothoCoreComponent]` types are force-excluded). Both are determinism inputs, set before the first tick and propagated authority → joining peers. **ComponentMemoryReport / ComponentMemoryPeakSampler** produce the `[Mem]` per-type reservation + live-peak report to measure against — see [ECSMemoryOptimization.md](ECSMemoryOptimization.md)
- **SystemPerfMonitor** — opt-in per-system elapsed time + per-tick allocation, plus the two builtin passes. Hard-gated: with it off, no `Stopwatch` or GC call runs at all. `EcsSimulation.EnableSystemPerfMonitor(warmupExecutions)` / `AppendSystemPerfLog()`; rows print as `group/SystemName` with trailing `= group` totals (additive columns only — per-system peaks landed on different ticks), never-executed rows omitted, and `ICommandSystem` work is not measured
- **FrameRingBuffer** — Frame ring buffer (ECS-specific snapshot / rollback)
- **StateSnapshot** — `IStateSnapshot` implementation (full-state byte buffer, FNV-1a hash)
- **HFSMBuilder / HFSMRoot** — fluent hierarchical-FSM assembler (`Default` / `State` / `OnEnter`·`OnUpdate`·`OnExit` / `To` / `Build`). `Build()` validates the graph at registration (duplicate / dangling / non-dense state ids, default-not-set), runs a reachability BFS, stably sorts each state's transitions by descending priority (the runtime evaluates them in array order), and registers an `HFSMRoot` (ticked via `HFSMRoot.Get(id).Tick(...)`). Advisory findings (unreachable / duplicate priority / self-transition) warn via `IKLogger`; `Build(strict: true)` promotes them to throws
- **EcsSimulation** — ISimulation implementation (owns Frame + SystemRunner; pluggable into KlothoEngine)
  - **`GetSystem<T>()` / `TryGetSystem<T>(out T)` / `GetSystems<T>(List<T> buffer)`** — type-match lookup over registered systems (`T : class`). Returns the first registration in `AddSystem` order; `GetSystems` appends every match into a caller-owned buffer (alloc-free for the lookup itself). Lets a callback boundary expose a registered system's secondary interface (e.g. `PhysicsSystem` → `IFPPhysicsWorldProvider`) without process-wide static slots
- **FixedString32 / FixedString64** — `unmanaged` fixed-size UTF-8 strings (for component fields)
- **Engine components** (`Runtime/ECS/Components/`) — the engine itself reads or writes these; all but `OwnerComponent` are `[KlothoCoreComponent]`:
  - TransformComponent (1), OwnerComponent (2), ErrorCorrectionTargetComponent (3, marker for the error-smoothing path), SessionParticipantComponent (4 — engine writes one per active player at `Start()` as a deterministic all-participants-spawned gate), RandomSeedComponent (5, singleton — engine-injected at session start; restored on LateJoin / Reconnect / Spectator / Replay via FullState), MatchEndStateComponent (26, singleton — `Ended` / `WinnerPlayerId` for the end-of-match ladder)
- **Optional gameplay components** (`Runtime/Gameplay/Components/`) — reference implementations with no engine privileges, safe to ignore entirely: HealthComponent (21), VelocityComponent (22), MovementComponent (23), CombatComponent (24), PhysicsBodyComponent (25). NavAgentComponent (11) belongs to the navigation module
- **TransformComponent prev-snapshot** — `PreviousPosition` / `PreviousRotation` / `PreviousInitialized` marker. Engine auto-initializes Previous* on first `Frame.Add` and via a PreUpdate `SavePrev` pass. Use `Frame.RefreshPreviousTransform(entity)` after a post-Add ref-set to keep Previous* in lockstep with `Position` (suppresses unwanted one-frame interpolation; see GameDevAPI §4.1)
- **Built-in systems** — every one is opt-in via `AddSystem`; the engine registers nothing on your behalf. `EventSystem` (`Runtime/ECS/Systems/`) batch-publishes enqueued simulation events in `LateUpdate`; `MovementSystem` / `PhysicsSystem` / `CombatSystem` and the `ICommandSystem` `CommandSystem` (`Runtime/Gameplay/Systems/`) are reference implementations over the gameplay components above; `FPNavMeshRebakeDriver` (navigation) owns the runtime-rebake install

## Replay

- **IReplayRecorder** — recording (start / record-tick / stop)
- **IReplayPlayer** — playback (load / play / pause / resume / stop / seek)
  - Playback speeds: 0.25x, 0.5x, 1x, 2x, 4x
- **IReplaySystem** — recording + playback combined, file save / load
- **IReplayData** — metadata + per-tick command-data serialization
- **File format** — `RPLY` magic (uncompressed, self-framed)
- **Implementations** — ReplayRecorder, ReplayPlayer, ReplaySystem, ReplayData

## Editor Tooling

> Both engines ship an editor toolchain (Unity: `com.xpturn.klotho/Unity/Editor/` · Godot: `com.xpturn.klotho/Godot~/Adapters/Editor/`), and the artifacts they produce (`.bytes` navmesh / static-collider data) load on **either** engine at runtime. The geometry pipeline behind the two navmesh exporters is the shared, engine-agnostic `FPNavMeshBuildPipeline`, so an asset baked on one engine is byte-identical to one baked on the other.

- **NavMesh export** — **FPNavMeshExporter** (Unity NavMesh → FPNavMesh: weld → grid-snap → triangle bake → grid build) · **GodotFPNavMeshExporter** (the Godot counterpart)
- **NavMesh visualizer** — **FPNavMeshVisualizerWindow** + **FPNavMeshSceneOverlay** + **FPNavMeshAgentSimulator** (agent-movement test) + **FPNavMeshInteraction** (click-to-navigate) · Godot: **GodotFPNavMeshVisualizerDock** + **GodotFPNavMeshOverlay** + **GodotFPNavMeshAgentSimulator** + **GodotFPNavMeshInteraction**
- **Static collider tools** — **FPStaticColliderExporterWindow** + **FPStaticColliderConverter** (Unity `Collider` → `FPStaticCollider`) · Godot: **GodotFPStaticColliderExporter** + **GodotFPStaticColliderConverter** + **GodotFPStaticColliderViewer**
  - The exporter window also **lists what the export will leave out**: colliders tagged neither `FPStatic` nor `FPTrigger`, and tagged-but-inactive objects (`FindGameObjectsWithTag` returns only active objects, so a tag alone was never enough). Counted in the window, listed in a warning, and logged on export. The collection path that produces the bytes is untouched — it derives collider ids from enumeration order — so the export stays byte-identical
- **Physics visualization** — **FPPhysicsWorldVisualizerEditor** (Unity) · Godot: **GodotFPPhysicsWorldVisualizer** + **GodotFPPhysicsDebugPanel** + **GodotFPPhysicsImmediateDrawer** (in-editor overlay of the deterministic physics world)
- **ECS inspector** *(Unity-only)* — **EntityComponentVisualizerWindow** + **ComponentReflectionCache**: live per-entity component values, driven by `Frame.TryGetReflectableStorage`. Value structs marked `[Primitive]` print on one line via `ToString()` instead of being recursed into
- **HFSM visualizer** *(Unity-only)* — **HFSMVisualizerWindow** + **HFSMStateTreeRenderer** + **HFSMReflectionCache**: state-tree view of a running hierarchical FSM
- **DataAsset conversion** — **JsonToBytesConverter** (Unity) · Godot: **KlothoDataAssetConvertTool** + **KlothoJsonContextMenu** (FileSystem-dock context menu)

## Unity Integration

> Most of the View / session-driving surface below mirrors 1:1 on Godot — see **[Godot Integration](#godot-integration)**. Items with no Godot counterpart are marked *(Unity-only)*.

- **USimulationConfig** — ScriptableObject SimulationConfig (inspector-editable, implements `ISimulationConfig`)
- **USessionConfig** — ScriptableObject SessionConfig (inspector-editable, implements `ISessionConfig`). All 16 session-level fields (MaxPlayers/MinPlayers/MaxSpectators, late-join/reconnect policy, chain-stall watchdog, countdown, match-end grace) author in one asset; `KlothoSessionSetup.SessionConfig` replaces the previous mirror-field set (RandomSeed/MaxPlayers/MinPlayers/AllowLateJoin/…)
- **EcsDebugBridge** — editor debug bridge *(Unity-only)*
- **View layer**
  - **EntityView / EntityViewComponent** — entity-view base class and view-component interface (`EntityViewComponent` is *Unity-only*; Godot's counterpart is `EntityViewNode`)
    - **Standard transform pipeline** — `EntityView` performs lerp + `ApplyTransform` + `UpdatePositionParameter` populate in `InternalLateUpdateView` (fused with `_errorVisual.Tick`), so tick-rate < frame-rate environments reflect every per-frame `PredictedAlpha` change in the transform without stale-lerp stutter. `UpdatePositionParameter` zeros `ErrorVisualVector` / `ErrorVisualQuaternion` when `EnableSnapshotInterpolation` is set (verified-frame interpolation path no longer double-corrects the rollback delta). Games override `OnUpdateView` / `OnLateUpdateView` for game-data cache + visual feedback; the transform pipeline itself is base-delegated
    - **EngineEventOneShot.Subscribe\<TEvent\>(engine, filter, onPlay, onCancel, lateGuard) → EngineEventSubscription** — sealed `IDisposable` helper that wraps `OnEventPredicted` + `OnEventConfirmed` + `OnEventCanceled` 3-channel subscription with a late-dispatch guard. Predicted+Confirmed dispatch `onPlay` when `filter` + `lateGuard` pass; Canceled dispatches `onCancel` when `filter` passes. `Dispose()` unsubscribes from all three channels and nullifies handlers (multi-dispose safe). Scope is limited to Predicted/Confirmed/Canceled — verified-time fallback events (e.g. `ActionCompletedEvent`) keep using the `OnSyncedEvent` channel
  - **EntityViewFactory / IEntityViewPool / DefaultEntityViewPool** — view creation / pooling. The base `EntityViewFactory` resolves `BindBehaviour` / `ViewFlags` from a 5-flag decision (rolls up `RequiresBindBehaviour`, `HasViewComponentInterpolation`, `RequiresErrorCorrection`, `RequiresSnapshotInterpolation`, `RequiresViewComponentBinding`) — games override only when a sample-specific override is required
  - **EntityViewUpdater** — simulation state → view sync; owns the built-in **PlayerViewRegistry\<TView\>** (lifted from sample). EVU drives `Register` / `Unregister` automatically from `OwnerComponent` add/remove; game code uses `Get(playerId)` for lookup and subscribes to `OnViewRegistered` / `OnLocalViewRegistered` / `OnLocalViewUnregistered` for player-view event hooks
  - **KlothoSessionFlow / KlothoSessionFlowAsync** — recommended 5-entry-point builder for session creation (`StartHost` / `JoinAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplay`). Sync primitives in Runtime.Core, UniTask wrappers in Runtime.Unity. `KlothoConnectionAsync` (Runtime.Unity) remains as an escape-hatch primitive — Flow consumes it internally.
  - **KlothoSessionDriver** — MonoBehaviour adapter that drives `KlothoSession.Update` / `Stop` through Unity's Update loop; exposes `PreSessionUpdate` / `PostSessionUpdate` / `Stopping` hooks for game-side input capture and cleanup, plus `BindTransport` to own the main transport's idle pumping + disconnect routing (`IKlothoSessionObserver.OnIdleDisconnected`)
  - **KlothoAutoReconnect / KlothoLogger** — cold-start credentials gate + IKLogger + Rolling File sink (Runtime.Unity helpers)
  - **VerifiedFrameInterpolator** — interpolation based on verified frames
  - **BindBehaviour / ViewFlags** — view-binding enums (Verified / NonVerified; snapshot-interpolation flags). Present on both engines (Godot defines them in `ViewEnums.cs`)
  - **UpdatePositionParameter / ErrorVisualState** — auxiliary types (`UpdatePositionParameter` is *Unity-only*; `ErrorVisualState` exists on both)
- **FPStaticColliderOverride** — MonoBehaviour for overriding static-collider parameters *(Unity-only)*
- **FPStaticColliderVisualizer** — MonoBehaviour for scene visualization of static colliders *(Unity-only; Godot covers the same ground with the editor-side `GodotFPStaticColliderViewer` / `GodotFPPhysicsWorldVisualizer`)*

## Godot Integration

The Godot (.NET) adapter (`com.xpturn.klotho/Godot~/Adapters/`) mirrors the Unity adapter on the same engine-agnostic core. It compiles as a single assembly `xpTURN.Klotho.Runtime.Godot` against the consumer's GodotSharp (no UniTask; standard `Task`).

- **GodotSimulationConfig / GodotSessionConfig** — `Resource` configs (`[GlobalClass]` + `[Export]` fields, author as `.tres`), implement `ISimulationConfig` / `ISessionConfig`
- **View layer**
  - **EntityViewNode** — entity-view base (`Node3D`); same lifecycle callbacks as Unity's `EntityView` (`OnInitialize` / `OnActivate` / `OnUpdateView` / `OnLateUpdateView` / `OnDeactivate`)
  - **EntityViewUpdaterNode** — simulation state → view sync (`Node`, `_Process` with `ProcessPriority = 1000`); owns **GodotPlayerViewRegistry\<EntityViewNode\>**
  - **EntityViewFactory** — abstract factory (`ResolvePrefab → PackedScene`, `ShouldRender`, **synchronous** `Create` — no async wrapper); same `TryGetBindBehaviour` / `GetViewFlags` decision API as Unity
  - **DefaultGodotEntityViewPool / VerifiedFrameInterpolator / EngineEventOneShot / ErrorVisualState / ViewEnums** — pooling, interpolation, one-shot event subscription, error-visual smoothing, `BindBehaviour`/`ViewFlags` enums
- **GodotSessionDriver** — `Node` adapter that drives `KlothoSession.Update` / `Stop` through `_Process`; same `BindTransport` / idle-pump / `OnIdleDisconnected` semantics as `KlothoSessionDriver`
- **GodotConnectionAsync / GodotSessionFlowAsync** — `Task`-based connect / join helpers (`JoinP2PAsync` / `JoinServerDrivenAsync` / `ReconnectAsync`); host start uses the core `KlothoSessionFlow.StartHostAndListen`
- **GodotFlowSetupBuilderExtensions** — `WithGodotDefaults()`: reads AppVersion from `ProjectSettings` + injects `GodotDeviceIdProvider` via `WithHandshake` in one call; falls back to `"0.0.0"` when no version is set. Mirrors `WithUnityDefaults()` on the Unity side
- **GodotKlothoLogger** — `CreateDefault()`: `GodotLogSink` + `RollingFileSink` combined, defaulting to `ProjectSettings.GlobalizePath("user://logs")` (required for exported apps where relative paths are not writable). Mirrors `KlothoLogger.CreateDefault()` on the Unity side
- **GodotAutoReconnect / GodotReconnectCredentialsStore / GodotDeviceIdProvider** — cold-start reconnect (`user://` credential store, `OS.GetUniqueId()`)
- **GodotDebugSink / GodotLogSink / GodotKLoggerFactory** — console sinks (`GD.Print` / `GD.PushError`); compose with the core `KLoggerFactory` + `AddRollingFile`
- **Editor tooling** — navmesh exporter / visualizer dock / agent simulator, static-collider exporter + viewer, physics-world visualizer + debug panel, DataAsset JSON convert tool. See **[Editor Tooling](#editor-tooling)** for the Unity ↔ Godot pairing

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
  - **Components** — BotComponent, CharacterComponent, GameTimerStateComponent (singleton), ItemComponent, KnockbackComponent, PlatformComponent, SkillCooldownComponent, SpawnMarkerComponent (the sample's `GameSeedComponent` was replaced by the engine-provided singleton `RandomSeedComponent`)
  - **Events** — ActionCompletedEvent, AttackActionEvent, AttackHitEvent, CharacterKilledEvent, CharacterSpawnedEvent, DashEvent, GameOverEvent, GroundSlamEvent, ItemPickedUpEvent, JumpEvent, RoundTimerEvent, SkillActionEvent, TrapTriggeredEvent
  - **Systems** — ActionLockSystem, BotFSMSystem, BoundaryCheckSystem, CombatSystem, GameOverSystem, GroundClampSystem, ItemSpawnSystem, KnockbackSystem, ObstacleMovementSystem, PlatformerCommandSystem, RespawnSystem, SkillCooldownSystem, TimerSystem, TopdownMovementSystem, TrapTriggerSystem (the sample's `SavePreviousTransformSystem` was removed — `TransformComponent.PreviousPosition/Rotation` is engine-maintained)
  - **Bot HFSM** — BotHFSMRoot, BotActions, BotDecisions, BotFSMHelper (hierarchical-FSM-based bot AI, assembled via the fluent `HFSMBuilder`)
  - **Prototypes** — `IEntityPrototype` implementations (KnightPrototype, MagePrototype, RoguePrototype, WarriorPrototype, ItemPickupPrototype, MovingPlatformPrototype)
  - **View** — CharacterView, CharacterAnimatorViewComponent, CharacterActionVfxViewComponent, ItemView, PlatformView, BrawlerCameraController, GameHUD, GameMenu, ResultScreen

## Tests

> Where they live: **`Samples/Klotho.Runtime.Tests`** (net8.0 NUnit — the bulk of the suite, engine-agnostic and runnable with plain `dotnet test`) · **`Tools/KlothoGenerator.Tests`** (source-generator + analyzer diagnostics) · **`Samples/DevLobbyServer.Tests`** (the reference lobby stack) · **`Samples/Brawler/Assets/Tests`** (Unity EditMode — for what genuinely needs the editor/runtime: asmdef wiring, editor compilation, engine-specific paths).

- **Core** — Command serialization, SyncTestRunner, FullStateResync
- **Integration** — late-join integration, server-driven-mode integration / benchmarks, replay integration (ReplayIntegrationTests), SD late-join connection
- **Network** — Handshake, Reconnect, Spectator, LateJoin, ServerDriven unit tests; ready-exchange setup fingerprints; message serialization; LiteNetLib integration
- **ECS** — EntityManager, ComponentStorageFlat, Frame, Filter, SystemRunner, FrameRingBuffer, StateSnapshot, EcsSimulation; component signal wiring and `[KlothoCleanup]` passes; per-system perf monitor; built-in systems (movement / combat / physics / nav / command / event); SourceGenerator validation; OOP hash comparison
- **Deterministic** — Math (FP64 / Vector / Quaternion / Matrix); Geometry (Bounds / Ray / Plane / Capsule / Sphere); Physics (RigidBody / Collider / Shape / Broadphase / Narrowphase / Sweep / Constraint / StaticBVH / PhysicsWorld); Navigation (Pathfinder / Funnel / Linearizer / Avoidance / Query / Serializer, plus the runtime rebaker — placement rules, boundary policies, clip stage, slicing determinism, and allocation gates on the real baked asset); Random; Curve
- **DeterminismVerification** — determinism stress-verification framework (ArithmeticStressSystem, EntityLifecycleSystem, RandomStressSystem, TrigStressSystem, DeterminismVerificationRunner, ServerDrivenDeterminismRunner)
- **State** — RingSnapshotManager
- **Input** — InputBuffer
- **Helpers** — KlothoTestHarness, TestTransport, TestSimulation

---

*Last updated: 2026-08-23*
