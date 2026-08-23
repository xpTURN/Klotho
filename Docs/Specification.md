# xpTURN.Klotho Engine Specification

> The deterministic Klotho simulation framework.
> Includes prediction/rollback-based multiplayer synchronization, fixed-point math, a replay system, and ECS.

---

## Core Philosophy

### Deterministic Simulation

All clients are guaranteed **same input → same result**.
Floating-point (`float`) is excluded; the simulation is built solely on 32.32 fixed-point (`FP64`) and a deterministic RNG (`Xorshift128+`), ensuring full reproducibility across platforms and compilers.

### Speculative Execution

The same principle as CPU pipeline branch prediction is applied to networked games.

| Concept | CPU Pipeline | Klotho |
| ---- | ---- | ---- |
| Prediction target | Conditional branch outcome | Remote player input |
| Prediction strategy | Branch history statistics | Repeat last input |
| Predicted execution | Pipeline advances on the predicted result | Simulation advances on predicted input |
| Prediction hit | Result is committed as-is | No rollback needed |
| Prediction miss | Pipeline flush + re-execution | Snapshot restore + re-simulation (rollback) |

The simulation **advances immediately** with predicted input rather than waiting for remote input to arrive; on misprediction, accuracy is recovered via snapshot restore + re-simulation.

### Distributed Authority

In a server-less P2P topology, **every client holds equal simulation authority**.
Each client only generates its own input and independently simulates the entire world state.
State consistency is verified by periodic hash comparison (`SyncCheck`); on mismatch (desync), an event is raised.

### Minimal Bandwidth

Only **inputs (commands)** are transmitted over the network.
World state is never fully synchronized, so bandwidth is independent of entity count.
Input delay (`InputDelayTicks`) absorbs network round-trip time, minimizing prediction/rollback frequency.

### Zero GC Allocation

Runtime GC allocation is minimized to prevent frame spikes.
Strategies applied: object pooling (`DictionaryPoolHelper`, `ListPoolHelper`, `PooledMemoryStream`), cached fields, no LINQ, avoidance of closure captures, etc.

### Engine Independence

The Klotho engine layer is designed to be **fully engine-independent** (no Unity or Godot API).
Direct dependencies on any engine API (`MonoBehaviour` / `UnityEngine.*`, `Node` / `Godot.*`) are excluded, so that the same simulation core can be **executed unchanged under the Unity and Godot (.NET) adapters and on the server side (.NET console / ASP.NET)**.

| Use Case | Description |
| ---- | ---- |
| Authoritative Server | Run the same simulation on the server for cheat prevention and result verification |
| Replay Verification Server | Re-execute replay data on the server to verify result integrity |
| Headless Testing | Run simulation tests in CI/CD pipelines without the Unity Editor |
| Matchmaking Simulation | Drive AI matches or perform balance tests on the server |

**Implementation Principles**:

- The engine core (`KlothoEngine`, `ISimulation`, `InputBuffer`, `FP64`, `Frame`, etc.) is pure C# — references to the `UnityEngine` namespace are forbidden
- Engine integration (rendering, input collection, MonoBehaviour / Node lifecycle) is handled in separate **adapter/bridge layers** — `Unity/` for Unity, `Godot~/Adapters/` for Godot
- External dependencies such as `INetworkTransport` and `ILogger` are isolated behind interface abstractions

### Network Layer Separation

"Network" (netcode) here means not just socket I/O but the **entire infrastructure** involved in multiplayer synchronization.

```text
┌───────────────────────────────────────────┐
│         Game Service Layer                │  ← gRPC RPC
│  Lobby · Matchmaking · Auth · Chat        │     TCP-based, reliability-focused
├───────────────────────────────────────────┤
│         Simulation Transport Layer        │  ← LiteNetLib (UDP)
│  Input · InputAck · SyncCheck · Handshake │     low latency, per-channel delivery
├───────────────────────────────────────────┤
│         Klotho Engine Layer               │  ← xpTURN.Klotho (pure C#)
│  Prediction · Rollback · Snapshots · Det. │     engine-independent, server-shared
└───────────────────────────────────────────┘
```

Simulation transport (UDP) and game services (RPC) are separated so each layer uses the optimal protocol and delivery method.
The Klotho engine layer is pure C#, so the same binary can be shared by client and server.
The lobby→session credential handoff that crosses this boundary (ticket carriage, validation hooks, identity propagation) is specified in Player Identity Handoff (§9.6).

---

## 1. Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Game Application                         │
│   (ISimulation impl: EcsSimulation or custom Simulation)    │
└───────────────────────────┬─────────────────────────────────┘
                            │
         ┌──────────────────┼──────────────────┐
         ▼                  ▼                  ▼
  ┌──────────────┐   ┌──────────────┐   ┌─────────────────┐
  │ KlothoEngine │   │ReplaySystem  │   │KlothoNetwork    │
  │(orchestrator)│◄  │(record/play) │   │   Service       │
  └──────┬───────┘   └──────────────┘   └────────┬────────┘
         │                                       │
    ┌────┴────┐                            ┌─────┴──────┐
    ▼         ▼                            ▼            ▼
┌────────┐ ┌──────────┐            ┌────────────┐ ┌──────────┐
│ISimula-│ │InputBuf- │            │INetwork    │ │Network   │
│tion    │ │fer       │            │Transport   │ │Messages  │
└───┬────┘ └──────────┘            └────────────┘ └──────────┘
    │
    ├──── (ECS) Frame ──── EntityManager + one byte[] heap of ComponentStorageFlat views
    ├──── SystemRunner ──── ISystem[]
    ├──── FrameRingBuffer (ECS snapshots)
    └──── RingSnapshotManager (IStateSnapshot)

  ┌──────────────────────────────────┐
  │       Math (Deterministic)       │
  │  FP64 · FPVector2 · FPVector3    │
  └──────────────────────────────────┘
```

### Directory Layout

```
<repo root>/                            ← this repo
├── com.xpturn.klotho/                 ← ★ framework package (UPM `?path=`)
│   ├── package.json
│   ├── Runtime/
│   │   ├── Core/           KlothoEngine, KlothoSession, KlothoSessionSetup,
│   │   │                   IKlothoEngine, IKlothoSession,
│   │   │                   ISimulationCallbacks, IViewCallbacks,
│   │   │                   ISimulationConfig, ISessionConfig, SimulationConfig, SessionConfig,
│   │   │                   NetworkMode, ICommand, CommandFactory, CommandRegistry,
│   │   │                   ICommandSender, WarmupRegistry, DedicatedServerLoop,
│   │   │                   Pool(CommandPool, EventPool, ListPool, DictionaryPool, StreamPool),
│   │   │                   SimulationEvent, EventBuffer, EventCollector, EventDispatcher,
│   │   │                   ModuleInitializerHelper
│   │   ├── Logging/        xpTURN.Klotho.Logging (IKLogger, KLoggerFactory, KLogBuilder,
│   │   │                   KLogHandlers — interpolated handlers, Sinks)
│   │   ├── Gameplay/       built-in component / system / command / event reference implementations
│   │   │                   (xpTURN.Klotho.Gameplay, noEngineReferences)
│   │   ├── Diagnostics/    FaultInjection, FaultInjectionLoader, RttSpikeMetricsCollector
│   │   ├── LowLevel/       KUnsafe (unsafe span/pointer helpers)
│   │   ├── Input/          IInputBuffer, IInputPredictor, IInputHandler, InputBuffer
│   │   ├── Network/        IKlothoNetworkService, IServerDrivenNetworkService, INetworkTransport,
│   │   │                   KlothoNetworkService, ServerDrivenClientService, ServerNetworkService,
│   │   │                   ISpectatorService, SpectatorService,
│   │   │                   Room, RoomManager, RoomRouter, RoomScopedTransport, ServerLoop,
│   │   │                   ServerInputCollector, NetworkMessages, MessageRegistry, SharedTimeClock
│   │   ├── State/          IStateSnapshot, IStateSnapshotManager, RingSnapshotManager
│   │   ├── Serialization/  SpanWriter, SpanReader, SerializationBuffer, ISpanSerializable
│   │   ├── Replay/         IReplaySystem, ReplayRecorder, ReplayPlayer, ReplayData
│   │   ├── ECS/            Core/ (Frame, EntityManager, EntityRef, ComponentStorageFlat,
│   │   │                          ComponentStorageRegistry, StorageLayout, EntityPrototypeRegistry,
│   │   │                          IEntityPrototype, FixedString32/64, StateHashBreakdown,
│   │   │                          StateHashRing, IStorageReflector)
│   │   │                   Attributes/ ([KlothoComponent], [KlothoSingletonComponent],
│   │   │                                [KlothoCleanup]+CleanupMode, [KlothoCoreComponent])
│   │   │                   System/ (ISystem/IInitSystem/IDestroySystem/ICommandSystem/
│   │   │                            ISyncEventSystem/IEntityCreated·DestroyedSystem,
│   │   │                            ISignal family, ISnapshotParticipant, Filter, SystemRunner)
│   │   │                   Snapshot/ (FrameRingBuffer) · Components/ · Systems/ (EventSystem)
│   │   │                   Diagnostics/ (ComponentMemoryReport, ComponentMemoryPeakSampler,
│   │   │                                 SystemPerfMonitor) · FSM/ (HFSM) · EcsSimulation
│   │   │                   DataAsset/ (IDataAsset, DataAssetRegistry, DataAssetRef,
│   │   │                               DataAssetReader/Writer, [KlothoDataAsset(typeId)],
│   │   │                               Json/ — xpTURN.Klotho.DataAsset.Json assembly)
│   │   ├── Deterministic/  Math/ (FP64, FPVector2/3/4, FPQuaternion, FPMatrix)
│   │   │                   Geometry/ (FPBounds, FPRay, FPPlane, FPCapsule, FPSphere)
│   │   │                   Physics/ (FPPhysicsWorld, FPStaticCollider, FPStaticBVH,
│   │   │                             FPStaticColliderSerializer, solver / narrowphase / sweep)
│   │   │                   Navigation/ (FPNavMesh, FPNavMeshSerializer, FPNavMeshBuildPipeline,
│   │   │                                NavAgentComponent, FPNavAgentSystem, FPNavAvoidance,
│   │   │                                FPNavMeshObstacleExtractor, and the runtime rebaker —
│   │   │                                FPNavMeshRebaker, FPNavMeshRebakeDriver,
│   │   │                                FPNavMeshPlacementValidator, FPNavAgentInstaller,
│   │   │                                FPBuildingShapeCatalog, FPConstrainedDelaunay)
│   │   │                   Random/ (DeterministicRandom) · Curve/ (FPAnimationCurve)
│   │   ├── LiteNetLib/     LiteNetLibTransport — INetworkTransport implementation
│   │   │                   (xpTURN.Klotho.LiteNetLib, noEngineReferences)
│   │   └── ThirdParty/     vendored: LiteNetLib.v2.1.4 (UDP networking)
│   ├── Unity/              Unity adapter — USimulationConfig, USessionConfig, EcsDebugBridge,
│   │                       KlothoSessionDriver, KlothoSessionFlowAsync, KlothoConnectionAsync,
│   │                       KlothoAutoReconnect, PlayerPrefsReconnectCredentialsStore,
│   │                       UnityDeviceIdProvider, KlothoLogger,
│   │                       KlothoFlowSetupBuilderUnityExtensions (WithUnityDefaults)
│   │                       View/ (EntityView, EntityViewComponent, EntityViewFactory,
│   │                              EntityViewUpdater, IEntityViewPool, DefaultEntityViewPool,
│   │                              BindBehaviour, ViewFlags, VerifiedFrameInterpolator,
│   │                              UpdatePositionParameter, ErrorVisualState)
│   │                       Physics/ (FPStaticColliderOverride, FPStaticColliderVisualizer)
│   │                       Deterministic/ · Diagnostics/ · Prefabs/
│   │                       Logging/ (UnityDebugSink, KLogBuilderUnityExtensions)
│   │   └── Editor/         NavMesh/ (FPNavMeshExporter, Visualizer Window/Overlay/Simulator/Interaction)
│   │                       Physics/ (FPStaticColliderExporterWindow, FPStaticColliderConverter,
│   │                                 FPPhysicsWorldVisualizerEditor)
│   │                       ECS/ (EntityComponentVisualizerWindow, ComponentReflectionCache)
│   │                       FSM/ (HFSMVisualizerWindow, HFSMStateTreeRenderer, HFSMReflectionCache)
│   │                       DataAsset/ (JsonToBytesConverter)
│   ├── Godot~/             Godot (.NET) adapter — Adapters/ (EntityViewNode, EntityViewUpdaterNode,
│   │                       GodotSessionDriver, GodotConnectionAsync, GodotSessionFlowAsync,
│   │                       GodotFlowSetupBuilderExtensions (WithGodotDefaults),
│   │                       GodotSimulationConfig/GodotSessionConfig (Resource), GodotDebugSink/GodotLogSink,
│   │                       GodotKlothoLogger (CreateDefault), GodotDeviceIdProvider,
│   │                       GodotReconnectCredentialsStore,
│   │                       Deterministic/ FPRay3·FPPlane·FPBounds3 Godot conversion helpers)
│   │                       · Packaging/ · plugin.cfg/plugin.gd
│   │   └── Adapters/Editor/  Godot editor tools — NavMesh (GodotFPNavMeshExporter, Visualizer/Dock/Overlay/Simulator/Interaction),
│   │                       StaticCollider (GodotFPStaticColliderExporter/Converter/Viewer),
│   │                       DataAsset (KlothoDataAssetConvertTool, KlothoJsonContextMenu) · plugin.gd EditorPlugin
│   │   └── Adapters/Physics/ GodotFPPhysicsWorldVisualizer, GodotFPPhysicsDebugPanel,
│   │                       GodotFPPhysicsImmediateDrawer
│   ├── Plugins/Analyzers/  KlothoGenerator.dll (Roslyn source generator, RoslynAnalyzer label)
│   ├── Plugins~/Logging.Mel/  opt-in MEL interop adapter (UPM "Import Sample" → MEL Logging Plugin)
│   └── Server~/            dedicated-server build assets (per-assembly csproj mirroring client
│                           asmdefs + KlothoServer/: KlothoServerBootstrap, ConfigPathResolver,
│                           Session/SimulationConfigLoader)
│
├── Samples/                ← standalone sample projects (each consumes the package via `file:`)
│   ├── Brawler/            4-player fighting-game sample (+ dedicated server, NavMesh, EditMode tests)
│   ├── P2pSample/          minimal P2P sample (Unity)
│   ├── SdSample/           minimal ServerDriven sample (Unity client + .NET 8 dedicated server)
│   ├── GodotP2pSample/     minimal P2P sample (Godot .NET)
│   ├── GodotSdSample/      minimal ServerDriven sample (Godot client) + GodotSdSampleServer/
│   ├── GodotPolySample/    Godot sample exercising the runtime NavMesh rebake / placement path
│   ├── GodotNetCheck/ · GodotDeterminismCheck/   Godot cross-runtime harnesses
│   ├── DevLobbyServer/     reference lobby stack (+ DevLobbyServer.Tests)
│   ├── IdentityP2pRef/ · IdentitySdRef/          player-identity reference services
│   ├── Klotho.Runtime.Tests/  the main engine-agnostic NUnit suite (net8.0, plain `dotnet test`)
│   ├── Unity2022.Tests/    Unity 2022.3 compile/EditMode compatibility project
│   └── LoggingMelConsole/  .NET console sample (IKLogger → Microsoft.Extensions.Logging)
│
├── Docs/                   ← documentation
├── dist/                   ← packed Godot addon output
└── Tools/                  ← .NET tooling (not redistributed)
    ├── KlothoGenerator/         Roslyn source generator + analyzers (built by gen.sh)
    ├── KlothoGenerator.Tests/   generator / analyzer unit tests
    ├── DeterminismVerification/ determinism verification (.NET console)
    ├── PhysicsDeterminismProbe/ cross-platform FP determinism probe
    ├── gen.sh                   generator build script
    ├── pack-godot-addon.sh      packs com.xpturn.klotho into dist/addons/klotho
    ├── deploy-addon-to-samples.sh  syncs the packed addon into the Godot samples
    └── run-all-tests.sh         full-suite runner
```

---

## 2. Core Engine

### 2.1 State Machine

```
         Initialize()
            │
            ▼
  ┌──── [ Idle ] ◄──── Stop()
  │         │
  │     Start()
  │         │
  │         ▼
  │  [WaitingForPlayers]
  │         │
  │     AllPlayersReady
  │         │
  │         ▼
  │    [ Running ] ◄──── Resume()
  │      │      │
  │  Pause()  Stop()
  │      │      │
  │      ▼      ▼
  │  [Paused] [Finished]
  │      │
  │   Resume()
  │      │
  └──────┘
```

| State | Description |
| ---- | ---- |
| Idle | Initial state. Engine not started |
| WaitingForPlayers | Waiting for all players to connect / be ready |
| BootstrapPending | (SD server only) Awaiting all players' bootstrap-ready acks (or timeout) before the first tick. Blocks `UpdateServerTick` via the existing `State == Running` gate |
| Running | Simulation in progress |
| Paused | Paused (waiting for input or manual) |
| Ending | Transient: tick advance frozen while transport keepalives continue. Reached when `EndGracePolicy.Pause` is selected at match end — `ExecuteTick` is blocked, input is buffered but not processed |
| Finished | Game over or manually stopped |
| Aborted | Match aborted before completion (chain stall timeout, catastrophic divergence, etc.). Distinct from `Finished` so replay-save / score-aggregation / normal-end UI can branch. Pair with `AbortReason` via `OnMatchAborted`. Use `KlothoStateExtensions.IsEnded()` for terminal-state check (covers both `Finished` and `Aborted`) |

### 2.2 Default Configuration Values

Configuration is split into two layers.
- **`SimulationConfig` / `ISimulationConfig`** — Simulation parameters (affect determinism, identical across all peers). Injected via `KlothoSessionSetup.SimulationConfig`. Editor-authorable via `USimulationConfig` ScriptableObject (Unity) or `GodotSimulationConfig` Resource (Godot, `[Export]` fields) — both implement `ISimulationConfig`.
- **`SessionConfig` / `ISessionConfig`** — Session operation parameters (decided by the host, propagated via GameStart / LateJoinAccept / SpectatorAccept messages). Injected via `KlothoSessionSetup.SessionConfig` — replaces the previous per-field mirror set (RandomSeed/MaxPlayers/MinPlayers/AllowLateJoin/LateJoinDelayTicks/ReconnectTimeoutMs/ReconnectMaxRetries/LateJoinDelaySafety/RttSanityMaxMs/MinStallAbortTicks/CountdownDurationMs). Editor-authorable via `USessionConfig` ScriptableObject (Unity) or `GodotSessionConfig` Resource (Godot); `Create()` copies the values into the engine-owned `SessionConfig` (assets are never mutated).

#### SimulationConfig Defaults

| Field | Default | Unit | Description |
| ---- | ---- | ---- | ---- |
| TickIntervalMs | 25 | ms | Tick interval (= 40 ticks/sec). Range: 1 or greater (typically 16~50ms) |
| InputDelayTicks | 4 | ticks | Local-input delay shift. Effective input delay = TickIntervalMs × InputDelayTicks (= 100 ms). Range: 0 or greater (typically 2~6) |
| MaxRollbackTicks | 50 | ticks | Maximum rollback range. Determines snapshot ring buffer + input-buffer retention. Must be ≥ SyncCheckInterval (≥ 2× recommended) |
| SyncCheckInterval | 20 | ticks | State-hash verification period. ≤ MaxRollbackTicks/2 recommended — values above are clamped to the effective interval at runtime so a desync rollback to the last matched anchor stays within the rollback window |
| UsePrediction | true | bool | Whether input prediction is enabled. False → engine waits for all inputs (Paused) |
| MaxEntities | 256 | entities | ECS entity capacity (EntityManager array size) |
| Mode | P2P | NetworkMode | Network topology (P2P / ServerDriven). Discriminator for SD-only fields |
| HardToleranceMs | 0 | ms | (SD) **Deprecated — no effect.** The effective server deadline is the tick's execution moment: inputs missing at execution are substituted with `EmptyCommand`, later arrivals are past-tick rejected, and chronic lateness self-corrects via client lead escalation. Property and wire fields retained for serialized-asset / message compatibility only |
| InputResendIntervalMs | 25 | ms | (SD) Interval at which the client resends unacknowledged inputs |
| MaxUnackedInputs | 30 | count | (SD) Cap on accumulated unacknowledged inputs (warning emitted on overflow) |
| ServerSnapshotRetentionTicks | 0 | ticks | (SD) Server snapshot ring-buffer slots. **0 = auto** (TickRate × 10). Independent of MaxRollbackTicks — used for FullStateRequest replies |
| SDInputLeadTicks | 0 | ticks | (SD) Initial client lead ticks at game start. **0 = auto (default 10)**. Reused on LateJoin/Reconnect. Additive with InputDelayTicks |
| EnableErrorCorrection | false | bool | Enable Error Correction (default off). Enable selectively in high-latency / multiplayer scenarios |
| InterpolationDelayTicks | 3 | ticks | View-layer snapshot interpolation delay (used by RenderClock.VerifiedBaseTick = LastVerifiedTick - InterpolationDelayTicks). Recommended [1, 3]. Fixed value — applied as-is by the live render clock, no dynamic adjustment |
| LateJoinDelaySafety | 2 | ticks | Safety margin added to RTT-based extra-delay computation on Sync / LateJoin / Reconnect. Also used as the standalone fallback when avgRtt is invalid / out of the sane range |
| RttSanityMaxMs | 240 | ms | Upper bound for accepting avgRtt as a sane measurement. Samples exceeding this fall back to `LateJoinDelaySafety` only |
| QuorumMissDropTicks | 20 | ticks | (P2P) Quorum-miss watchdog threshold. If a remote peer's input is missing at `_lastVerifiedTick + 1` for at least this many ticks, the peer is presumed-dropped and reactive empty-fill activates before the transport-level DisconnectTimeout. 0 disables. Safe range 10~80 |
| MinStallAbortTicks | 600 | ticks | (P2P) Chain-stall watchdog threshold (peer-local). Aborts the match via `AbortMatch(AbortReason.ChainStallTimeout)` when `CurrentTick - LastVerifiedTick` exceeds `max(SessionConfig.ReconnectTimeoutMs / TickIntervalMs + 100, MinStallAbortTicks)`. Default 600 = 30s @ 50ms TickInterval. Guards against `ReconnectTimeoutMs` misconfiguration shorter than recovery floor |
| EventDispatchWarnMs | 5 | ms | Warning threshold for OnEvent* handler execution time (dev-diagnostic — see note below). 0 or less = disabled |
| TickDriftWarnMultiplier | 2 | × | Tick-loop drift warning multiplier (warns if actual interval > TickIntervalMs × multiplier). 0 or less = disabled |
| StageId | 0 | int | Match stage/content selector, set per-match by the authority and propagated to all peers. 0 = default single stage. The game maps it to stage assets; the engine only carries it |
| MatchConfigData | null | byte[] | Opaque, game-defined per-match config (game mode / rules / difficulty), set per-match by the authority and propagated. null/empty = none. The engine carries only the bytes — the game owns the codec |
| ComponentMaxCountOverrides | empty | map | Per-typeId slot-cap overrides for the frame heap, for types whose source you cannot edit (the in-source form is `[KlothoComponent(id, MaxCount = n)]`). **Determinism input** — it changes the heap layout, so it is folded into the layout fingerprint and propagated authority → joining peers. See §7.6 / ECSMemoryOptimization.md |
| PrunedComponentTypeIds | empty | list | **Denylist** of component typeIds to skip reserving entirely — anything unlisted stays reserved, which is the fail-safe default. `[KlothoCoreComponent]` types are force-excluded, so listing one cannot drop it. Set via `SetRuntimePrunedComponentTypeIds`; authority-owned and wire-propagated. Also a determinism input |

##### Dynamic InputDelay (client-reactive policy)

These drive `DynamicInputDelayPolicy` (§9.6) and are server-authoritative.

| Field | Default | Unit | Description |
| ---- | ---- | ---- | ---- |
| ReactiveWindowTicks | 80 | ticks | Sliding window over which non-spawn `CommandRejected(PastTick)` events are counted |
| ReactiveEscalateThreshold | 3 | count | PastTick rejects within the window that trigger an escalation |
| ReactiveStep | 4 | ticks | Extra-delay increment applied per escalation |
| ReactiveMax | 40 | ticks | Ceiling for reactive extra delay. Clamped at `Validate()` to `MaxRollbackTicks / 2` with a warning |
| ServerPushGraceTicks | 40 | ticks | Both triggers ignore events within this many ticks of the last authoritative `RecommendedExtraDelayUpdate` push, so the reactive path never double-counts against it |
| ReactiveEscalateCooldownTicks | 80 | ticks | Minimum spacing between rollback-triggered escalations |
| ReactiveDeEscalateStableTicks | 160 | ticks | Quiet period required before reactive delay is walked back down |
| RollbackBurstCount | 3 | count | Rollbacks within `RollbackWindowTicks` that trigger an escalation. Primary trigger for P2P guests, which receive no `CommandRejected` |
| RollbackWindowTicks | 200 | ticks | Sliding window for the rollback-burst trigger |

##### Peer-local diagnostics (never wire-propagated, no effect on determinism)

| Field | Default | Unit | Description |
| ---- | ---- | ---- | ---- |
| DiagnosticHistoryTicks | 60 | ticks | Rolling per-tick / per-type state-hash history (`StateHashRing`) dumped on desync detection, so the *first* diverged tick is visible rather than the tick it was noticed. `0` disables — no subscription, zero cost |
| ComponentMemoryPeakSampling | false | bool | Samples per-component-type live peaks at tick boundaries for the `[Mem]` report, so `MaxCount` can be sized against measurement instead of a guess |
| SystemPerfMonitoring | false | bool | Arms the per-system elapsed-time / per-tick-allocation monitor. Hard-gated: when off, no `Stopwatch` or GC call runs at all |

> **Dev-diagnostic build symbols (cross-engine note)**: the core gates many diagnostics — including the `EventDispatchWarnMs` warning — behind `#if DEVELOPMENT_BUILD || UNITY_EDITOR`. Both are **Unity-defined** symbols. On Unity they are active in the Editor / development builds automatically. On **Godot (.NET) and the .NET dedicated server** neither symbol is defined by default, so these diagnostics are **compiled out** — define `DEVELOPMENT_BUILD` in your game `.csproj` (`<DefineConstants>`) to enable them.

#### SessionConfig Defaults

| Field | Default | Unit | Description |
| ---- | ---- | ---- | ---- |
| RandomSeed | 0 | int | If 0, auto-generated via `Environment.TickCount` (host) |
| MaxPlayers | 4 | count | Max players in a room |
| MaxSpectators | 0 | count | Max spectators allowed in the session. Combined with MaxPlayers as the transport-level capacity (`MaxPlayersPerRoom + MaxSpectatorsPerRoom`) for spectator admission. 0 means spectators are not admitted. |
| MinPlayers | 2 | count | Min players required to start. Range: 1 ≤ MinPlayers ≤ MaxPlayers (clamped at SessionConfigLoader.Load and KlothoSession.Create with a warning log; SD start gate also clamps to MaxPlayersPerRoom at runtime). |
| AllowLateJoin | true | bool | Whether mid-game join is allowed |
| ReconnectTimeoutMs | 60000 | ms | Reconnect timeout |
| ReconnectMaxRetries | 3 | tries | Max reconnect attempts |
| LateJoinDelayTicks | 10 | ticks | Late-join activation delay |
| ResyncMaxRetries | 3 | tries | Max resync attempts |
| DesyncThresholdForResync | 3 | count | Desync count that triggers resync |
| CorrectiveResetCooldownMs | 5000 | ms | (P2P, host-only) Minimum interval between consecutive corrective-reset broadcasts. Prevents broadcast storms when persistent hash divergence fires `OnHashMismatch` repeatedly |
| CorrectiveResetMaxAttempts | 2 | tries | (P2P, host-only) Corrective-reset attempts the host spends per divergence episode (recovery ladder rung 3), fed by guest `ResyncFailureReport` messages. Attempts decay to zero after a quiet period of `CorrectiveResetCooldownMs × 2` without failure reports. Host-local — not propagated via `SimulationConfigMessage` |
| AutoAbortOnRecoveryExhausted | true | bool | (P2P, host-only) When corrective-reset attempts are exhausted (rung 4), the host broadcasts `MatchAbort` and aborts locally with `AbortReason.StateDivergence`. `false` logs an error and leaves the decision to the game layer. Host-local |
| CountdownDurationMs | 3000 | ms | Game-start countdown length |
| CatchupMaxTicksPerFrame | 200 | ticks | Max ticks per frame during catchup |
| AbortGraceMs | 1500 | ms | Post-match grace duration on abort. Time between `OnMatchAborted` fire and `Room.State` transition to `Draining`, giving clients time to display the error dialog and the server side time for abort logging |
| EndGracePolicy | Continue | enum | Simulation behavior during the post-match grace window. `Continue` keeps the simulation running (input / heartbeat / replay continuity). `Pause` **also stays in `Running` and keeps advancing ticks** — it does *not* transition to `Ending`; instead the engine auto-injects a per-tick `StopCommand` on the deterministic input stream in place of game input, so characters halt while transport keepalives are preserved. Use it when post-result physics drift or stray events would clutter the result screen |
| EndGraceMs | 5000 | ms | Post-match grace duration on normal end. Time between `OnMatchEnded` fire and `Room.State` transition to `Draining`, giving clients time to display the result screen and the server side time for any post-processing hook. Range: 0 (immediate drain, debug/integration only) or greater |
| ClientShutdownGraceMs | 4500 | ms | Client-side grace duration on normal end. Time between `OnMatchEnded` on the client and the client's self-initiated session shutdown, so the result screen plays out before the chain-stall warning storm begins. Must stay below `EndGraceMs` — inversion risks chain-stall warnings |
| Old-data cleanup threshold | CurrentTick - MaxRollbackTicks - 10 | ticks | Threshold for discarding old data |

### 2.3 Events

| Event | Signature | Fired |
| ---- | ---- | ---- |
| OnTickExecuted | `Action<int>` | After every tick is executed (passes tick number) |
| OnTickExecutedWithState | `Action<int, FrameState>` | After every tick (tick, Predicted/Verified) |
| OnFrameVerified | `Action<int>` | On Predicted → Verified transition |
| OnChainAdvanceBreak | `Action` | Verified-chain advance failed at the next tick (P2P: pending input for an active player). Drives reactive empty-fill / dynamic-delay escalation |
| OnDesyncDetected | `Action<long, long>` | State-hash mismatch detected (localHash, remoteHash). The network-service variant (§9.5) is the extended `Action<int,int,long,long>` |
| OnHashMismatch | `Action<int, long, long>` | Hash gate detected a mismatch after `ApplyFullState` (tick, localHash, remoteHash). Wired in P2P to trigger `TryCorrectiveReset` (host only). Fires from all 5 `ApplyFullState` entry points (LateJoin / InitialFullState / ResyncRequest / CorrectiveReset / Reconnect) |
| OnRollbackExecuted | `Action<int, int>` | Rollback completed (fromTick, toTick) |
| OnRollbackFailed | `Action<int, string>` | Rollback failed (requestedTick, reason) |
| OnPendingWipe | `Action<int, int, WipeKind>` | A pending input or Synced event was wiped during cleanup before the chain advanced past it (tick, playerId, kind). `playerId = -1` for `WipeKind.SyncedEvent` |
| OnCommandRejected | `Action<int, int, RejectionReason>` | (SD client) Server rejected a client input (tick, cmdTypeId, reason). For commands issued via `engine.IssueOnce(Func<ICommand>, ReliabilityPolicy)`, the framework `ReliableCommandTracker` intercepts this internally and applies the policy: `TreatDuplicateAsAck=true` resolves the handle on `Duplicate`; `TreatPastTickAsEscalation=true` advances `CurrentExtraDelay` by `ExtraDelayStep` (capped at `ExtraDelayMax`) and clears the retry cooldown for an immediate re-issue. Game-side `OnCommandRejected` subscribers still receive the event for non-handle commands (game-specific telemetry / response). A command marked `IReliableCommand` takes the ServerDriven authoritative-placement path instead (server assigns the execution tick via `ReliableCommandSubmit`, no PastTick/Duplicate reject loop) — the retry/escalation described here applies to the legacy path (plain `IssueOnce` commands, and P2P). |
| OnExtraDelayChanged | `Action<int>` | Recommended extra InputDelay (ticks) changed. Fired on `ApplyExtraDelay` (Sync / LateJoin / Reconnect / DynamicPush) and `EscalateExtraDelay` (reactive escalation) |
| OnEventPredicted | `Action<int, SimulationEvent>` | Event raised on a Predicted tick |
| OnEventConfirmed | `Action<int, SimulationEvent>` | First firing of a Regular event that was confirmed without prediction (verified-direct, replay, new on rollback) |
| OnEventCanceled | `Action<int, SimulationEvent>` | Predicted event canceled by rollback |
| OnSyncedEvent | `Action<int, SimulationEvent>` | Synced event — fired only on verified ticks |
| OnResyncCompleted | `Action<int>` | Full state resync completed (restoredTick) |
| OnResyncFailed | `Action` | Failed after exceeding max resync retries |
| OnMatchAborted | `Action<AbortReason>` | Match terminated mid-play via `AbortMatch` (state transitions to `Aborted`). Reasons: `ChainStallTimeout` / `StateDivergence` / `ReconnectFailed` / `Unknown` |
| OnMatchReset | `Action<ResetReason>` | Corrective reset applied — state restored, match continues (`Running` preserved). Sibling to `OnMatchAborted` (terminal). Reasons: `StateDivergence` / `ManualResync` / `Unknown` |
| OnMatchEnded | `Action<int, IMatchEndEvent>` | Normal match-end signaled by a verified `IMatchEndEvent` (Synced). Fires exactly once per match on first verification (tick, event). Drives the grace-driven `Room` drain (`EndGraceMs` / `ClientShutdownGraceMs`) and, when `EndGracePolicy.Pause` is set, the `Running → Ending` transition. The event may additionally implement `IMatchResultProvider`, exposing a game-authored opaque result payload (`MatchResultData : byte[]`, null = none) for the server authority to read here — the engine carries only the bytes, the game owns the codec |
| OnDisconnectedInputNeeded | `Action<int>` | Empty-input request for a disconnected player (playerId) |
| OnCatchupComplete | `Action` | Late-join catchup completed |
| OnVerifiedInputBatchReady | `Action<int, int, byte[], int>` | Verified input batch ready for spectators (startTick, tickCount, data, length) |
| OnStateChanged | `Action<KlothoState>` | `KlothoEngine.State` transitioned (`Initial → Ready → Running → Ending → Finished` / `Aborted`). Surfaced to the game as `IKlothoSessionObserver.OnStateChanged` so game code does not poll the engine each frame |

**FrameState**:

| Value | Description |
| ---- | ---- |
| Predicted | Tick executed with at least one predicted input |
| Verified | All player inputs confirmed and all prior ticks verified |

### 2.4 Main Game Loop

```
KlothoEngine.Update(float deltaTime):
│
├─ NetworkService.Update()               // network receive
├─ _accumulator += deltaTime * 1000f     // accumulate, converted to ms
│
└─ while _accumulator >= TickIntervalMs:
   │
   ├─ if InputBuffer.HasAllCommands(tick, playerCount):
   │     ExecuteTick()                    // execute with confirmed inputs
   │
   ├─ elif UsePrediction:
   │     ExecuteTickWithPrediction()      // execute with predicted inputs
   │
   └─ _accumulator -= TickIntervalMs
```

### 2.5 Tick Execution Flow

```
ExecuteTick():
├─ commands = InputBuffer.GetCommandList(CurrentTick)
├─ if ReplaySystem.IsRecording:
│     ReplaySystem.RecordTick(CurrentTick, commands)
├─ SaveSnapshot(CurrentTick)              // snapshot every tick
├─ EventCollector.BeginTick(CurrentTick)
├─ Simulation.Tick(commands)              // run simulation
├─ store collected events into EventBuffer
├─ if CurrentTick % SyncCheckInterval == 0:
│     hash = Simulation.GetStateHash()
│     _localHashes[CurrentTick] = hash
│     NetworkService.SendSyncHash(CurrentTick, hash)
├─ CurrentTick++
├─ OnTickExecuted(executedTick)
└─ OnTickExecutedWithState(executedTick, FrameState.Verified)
```

### 2.6 Simulation Tick Execution Order (EcsSimulation)

```
EcsSimulation.Tick(commands):
  1. for each command: SystemRunner.RunCommandSystems(frame, cmd)   — ICommandSystem
  2. SystemRunner.RunUpdateSystems(frame):
       a. SaveAllPreviousTransforms(frame)      — built-in, ahead of the first PreUpdate system
                                                  (TransformComponent.Previous* for view interpolation)
       b. ISystem.Update for every registered system
                                                  — PreUpdate → Update → PostUpdate → LateUpdate,
                                                    registration order within a phase
       c. frame.RunCleanupClear()               — built-in, [KlothoCleanup(RemoveComponent)] storages
       d. frame.RunCleanupDestroy(buffer)       — built-in, [KlothoCleanup(DestroyEntity)] carriers
                                                  (c/d skipped entirely when nothing declares the attribute)
  3. frame.Tick++
```

Both built-in passes live **inside** `RunUpdateSystems`, and the caller's `SaveSnapshot` / `GetStateHash`
run after `Tick` returns — so the state that is snapshotted and hashed is the **post-cleanup** state, and
rollback / re-simulation reproduce it exactly. When `SystemPerfMonitoring` is on, each built-in pass gets its
own report slot (`(builtin) SavePrevTransforms` / `CleanupClear` / `CleanupDestroy`) alongside the per-system rows.

### 2.7 KlothoSession & Callback Interfaces

The entry point connecting game code to the engine. Callbacks are split into deterministic-common and view-only.

```
ISimulationCallbacks (game-implemented — common to deterministic side)
  RegisterSystems(EcsSimulation)          ← register systems (before Initialize)
  OnInitializeWorld(IKlothoEngine)        ← create initial entities (before SaveSnapshot(0))
  OnPollInput(playerId, tick, sender)     ← per-tick input polling
  OnPlayerJoinedWorld(engine, frame, playerId) ← seed a late-joiner's world at its join tick (late-join analog of OnInitializeWorld)

IViewCallbacks (game-implemented — client view only, non-determinism allowed)
  OnGameStart(IKlothoEngine)              ← once at game start
  OnTickExecuted(tick)                    ← view update
  OnLateJoinActivated(IKlothoEngine)      ← once after late-join catchup completes

KlothoSession.Create(KlothoSessionSetup) → KlothoSession
  Engine          : KlothoEngine
  Simulation      : EcsSimulation
  NetworkService  : IKlothoNetworkService
  CommandFactory  : CommandFactory
  HostGame(roomName, maxPlayers)
  JoinGame(roomName)
  LeaveRoom()
  SendPlayerConfig(PlayerConfigBase)
  SetReady(bool)
  Update(float dt)                              ← pumped by the driver: Unity MonoBehaviour.Update / Godot Node._Process
  Stop(keepReconnectCredentials = false)        ← keep=true on process-exit paths to preserve cold-start credentials
  IsStopped                                     ← teardown completed
  PlayerCount        ← unified getter: NetworkService → SpectatorService → 0 fallback
  StateChanged           : event Action<KlothoState>
  PhaseChanged           : event Action<SessionPhase>
  PlayerCountChanged     : event Action<int>
  AllPlayersReadyChanged : event Action<bool>

KlothoSessionFlow (recommended construction layer)
  StartHost(simCfg, sessionCfg)                                              → P2P host (sync)
  JoinP2PAsync(transport, host, port, sessionCfg, ct)                        → P2P guest
  JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)       → SD client
  ReconnectAsync(transport, creds, sessionConfigSeed, ct)                    → cold-start reconnect (creds: PersistedReconnectCredentials)
  SpectateAsync(host, port, roomId, ct)                                      → spectator (factory transport)
  StartReplayFromFile(path)                                                  → file → KlothoSession (throws ReplayLoadException)
  (session creation is observed via IKlothoSessionObserver.OnSessionCreated(session, kind) — branch on kind)

KlothoSessionDriver (MonoBehaviour adapter — Runtime.Unity) / GodotSessionDriver (Node adapter — Godot~/Adapters)
  PreSessionUpdate / PostSessionUpdate / Stopping                            → lifecycle hooks
  BindTransport(transport, observer, flow)                                   → driver owns idle transport pumping + idle-disconnect routing → IKlothoSessionObserver.OnIdleDisconnected
  Attach(session) / DetachAndStop(keepReconnectCredentials = false)          → ownership transfer; OnDestroy passes keep=true; DetachAndStop is idempotent (internal re-entry guard)
  (Unity drives via MonoBehaviour.Update; Godot drives via Node._Process — same Session.Update underneath)

KlothoSessionSetup (Create input — direct path)
  Logger · SimulationCallbacks · ViewCallbacks
  Transport (host) / Connection (guest)
  SimulationConfig · AssetRegistry
  RandomSeed · MaxPlayers · AllowLateJoin · Reconnect/LateJoin/Resync/Countdown parameters

KlothoFlowSetup (Flow input — bundles long-lived dependencies)
  Logger · Transport · AssetRegistry · CredentialsStore · AppVersion · DeviceIdProvider
  LifecycleObserver (IKlothoSessionObserver) · CallbacksFactory(simCfg, sessionCfg)
  InitialPlayerConfigFactory : Func<PlayerConfigBase>   ← auto SendPlayerConfig on guest / reconnect
  SpectatorTransportFactory  : Func<INetworkTransport>  ← invoked from SpectateAsync(host,port,roomId,ct)
```

**Teardown invariant**: `IKlothoSessionObserver.OnSessionStopped` is invoked from both teardown entry paths (Driver.DetachAndStop → Session.Stop and direct Session.Stop). Re-entry is made idempotent by library-level guards (`KlothoSession._stopped`, `KlothoSessionDriver._stopping`), so game code routes all teardown through `KlothoSessionDriver.DetachAndStop` — a re-entrant call is a no-op — rather than carrying its own `_isStopping` flag.

**Reconnect-credentials teardown policy**: `KlothoSession.Stop` / `KlothoSessionDriver.DetachAndStop` / `IKlothoNetworkService.LeaveRoom` all accept `bool keepReconnectCredentials = false`. Default `false` matches a user-intent leave (graceful session end → persisted cold-start credentials are discarded). Process-exit paths (`KlothoSessionDriver.OnDestroy`, game-side `OnApplicationQuit` / `OnDestroy`) must pass `true` so the persisted credentials survive into the next launch — otherwise a normal app quit silently wipes them and the next cold start cannot Reconnect. Explicit cancel / reject paths still clear credentials directly via `IReconnectCredentialsStore.Clear()`.

**NetworkMode**:

| Value | Description |
| ---- | ---- |
| `P2P` | Peer-to-peer — all clients hold equal authority (default) |
| `ServerDriven` | Server-driven — the server collects/verifies inputs, clients only execute |

---

## 3. Prediction and Rollback

### 3.1 Input Prediction Algorithm

**Strategy**: repeat the last input (Temporal Coherence Prediction)

```
SimpleInputPredictor.PredictInput(playerId, tick, previousCommands):
│
├─ search previousCommands for the latest command
├─ if found:
│     → reuse the latest command (update tick field)
└─ else:
      → return EmptyCommand (cached)
```

**Accuracy tracking**:

- `_correctPredictions` / `_totalPredictions` counters
- When real input arrives, accuracy is judged by comparing `CommandTypeId`

### 3.2 Rollback Flow

```
Real input arrives (OnCommandReceived):
│
├─ compare against the predicted input
├─ if CommandTypeId mismatch:
│     Rollback(targetTick)
│     ├─ look up snapshot via RingSnapshotManager.GetNearestSnapshot(targetTick)
│     │   (ECS: FrameRingBuffer.RestoreFrame)
│     ├─ Simulation.Rollback(snapshotTick)
│     ├─ InputBuffer.ClearAfter(snapshotTick)
│     ├─ re-simulate from snapshotTick → currentTick
│     └─ raise OnRollbackExecuted(fromTick, snapshotTick)
│
└─ else: prediction correct → no rollback needed
```

### 3.3 Snapshot Management

| Item | Value |
| ---- | ---- |
| Save period | Every tick (ExecuteTick, ExecuteTickWithPrediction, and each tick during re-simulation) |
| Data structure | `RingSnapshotManager` — ring buffer (fixed capacity = MaxRollbackTicks + 2) |
| Insert / lookup | O(1) |
| GC | 0 (ring array preallocated) |
| ECS path | `FrameRingBuffer.SaveFrame(tick, frame)` — Frame.CopyFrom (BlockCopy) |
| Lookup | `GetNearestSnapshot(tick)` — finds the largest tick ≤ target |

---

## 4. Input System

### 4.1 InputBuffer

**Data structure**:

```
Dictionary<int, Dictionary<int, ICommand>>
           │              │         │
         tick         playerId   command
```

| Item | Detail |
| ---- | ---- |
| Pooling | Inner dictionaries reused via `DictionaryPoolHelper` |
| Caches | `_commandListCache` (List), `_ticksToRemoveCache` (List) |
| Range tracking | `_oldestTick`, `_newestTick` |

**Key methods**:

| Method | Complexity | Description |
| ---- | ---- | ---- |
| `AddCommand(cmd)` | O(1) | Insert by tick + playerId |
| `GetCommand(tick, playerId)` | O(1) | Lookup a specific input |
| `HasCommandForTick(tick)` | O(1) | Whether at least one command exists for the tick |
| `HasCommandForTick(tick, playerId)` | O(1) | Whether a specific player's command exists |
| `HasAllCommands(tick, playerCount)` | O(1) | Whether all players' inputs were received |
| `GetCommandList(tick)` | O(n) | All commands for the tick (cache reused) |
| `ClearBefore(tick)` | O(k) | Drop old inputs |
| `ClearAfter(tick)` | O(k) | Drop future inputs on rollback |

### 4.2 Input Delay Compensation

```
Player input occurs at tick T
├─ InputDelayTicks = 2
├─ Command.Tick = CurrentTick + 2
├─ broadcast over the network
└─ executed at tick T+2

Effect: gives other players' inputs time to arrive → reduces the need for prediction
```

---

## 5. Command System

### 5.1 Interface

```
ICommand:
  int PlayerId            // issuing player
  int Tick                // target execution tick
  int CommandTypeId       // command kind identifier
  void Serialize(ref SpanWriter writer)
  void Deserialize(ref SpanReader reader)
  int GetSerializedSize()
```

### 5.2 Built-in Command Types

| Type | Location | Purpose |
| ---- | ---- | ---- |
| EmptyCommand | Core | Fallback for prediction (cached) |
| StopCommand | Core | Explicit "no movement, no action" intent. Sent by clients during the `EndGracePolicy.Pause` grace window to deterministically halt all characters; concrete semantics decided by game-side `ICommandSystem` |
| PlayerJoinCommand | Core | Player-join system command (`ISystemCommand`) |
| MoveCommand | Gameplay | Move command (TargetX/Y/Z — FP64 raw) |
| ActionCommand | Gameplay (Sample) | Action command |
| SkillCommand | Gameplay (Sample) | Skill use |

### 5.3 Command Serialization / Deserialization

- `ICommandFactory.DeserializeCommand(ref SpanReader)` — CommandTypeId-based factory, includes length prefix
- `ICommandFactory.DeserializeCommandRaw(ref SpanReader)` — raw deserialization without length prefix
- `SpanWriter/SpanReader`-based — GC-free, ref struct

---

## 6. State Management

### 6.1 IStateSnapshot / IStateSnapshotManager

```csharp
IStateSnapshot:
  int    Tick
  byte[] Serialize()
  void   Deserialize(byte[])
  ulong  CalculateHash()

IStateSnapshotManager:
  void           SaveSnapshot(int tick, IStateSnapshot snapshot)
  IStateSnapshot GetSnapshot(int tick)
  bool           HasSnapshot(int tick)
  void           ClearSnapshotsAfter(int tick)
  void           ClearAll()
  IEnumerable<int> SavedTicks
```

### 6.2 RingSnapshotManager

- Ring-buffer based, fixed capacity = `MaxRollbackTicks + 2`
- O(1) insert / lookup, GC 0 (preallocated array)
- `GetNearestSnapshot(tick)` — finds the latest tick ≤ target

---

## 7. ECS

### 7.1 Frame — ECS World State

```csharp
Frame:
  int    Tick
  int    DeltaTimeMs
  ISimulationEventRaiser   EventRaiser      // injected by the engine each tick (EventCollector)
  EntityManager            Entities
  EntityPrototypeRegistry  Prototypes       // not part of CopyFrom — rollback-safe
  IDataAssetRegistry       AssetRegistry    // global DataAsset lookup (locked after Frame.Add internal layout fixed)
  Action<EntityRef>        OnEntityCreated / OnEntityDestroyed
  int    MaxEntities { get; }               // fixed capacity specified at creation
  ComponentStorageFlat<T> per registered type, all backed by ONE byte[] heap
                                            // layout computed at first use by ComponentStorageRegistry

  // entity lifecycle
  EntityRef  CreateEntity()
  EntityRef  CreateEntity(int prototypeId)          // delegates to Prototypes.Create
  EntityRef  CreateEntity<TProto>(in TProto proto)  // typed, registry-free (see §7.7)
  void       DestroyEntity(EntityRef)

  // component access
  ref T          Get<T>(EntityRef)          // UNCHECKED — absent component indexes sparse[-1]
  ref readonly T GetReadOnly<T>(EntityRef)
  bool           Has<T>(EntityRef)
  void           Add<T>(EntityRef, T)       // throws: duplicate / slot cap / singleton already carried
  void           Remove<T>(EntityRef)
  int            GetLiveCount(int typeId)   // carrier count by typeId, no generic parameter

  // singletons ([KlothoSingletonComponent])
  ref T          GetSingleton<T>()          // throws when absent
  ref readonly T GetReadOnlySingleton<T>()
  bool           TryGetSingleton<T>(out EntityRef carrier)

  // queries (ref struct, GC 0)
  Filter<T1..T5>             Filter<T1..T5>()
  FilterWithout<T1..T5, TEx> FilterWithout<T1..T5, TEx>()

  // built-in transform hook
  void   RefreshPreviousTransform(EntityRef)  // re-sync Previous* after a post-Add ref-set

  // hash / snapshot / full state
  ulong  CalculateHash()                      // FNV-1a (Tick + EntityCount + ComponentStorages)
  ulong  CalculateHash(StateHashBreakdown)    // same value, plus the per-typeId split
  void   CopyFrom(Frame)                      // restore the entire heap with a single Buffer.BlockCopy
  void   Clear()                              // zero the heap, reset entities, Tick = 0
  byte[] SerializeTo() / void DeserializeFrom(byte[])   // full state (resync / late-join / spectator / replay)
```

Not copied by `CopyFrom`: `EventRaiser`, `OnEntityCreated` / `OnEntityDestroyed`, the component-signal sink
and masks (only the executing frame ever has them, so ring slots and the sync-test buffer fire nothing),
`AssetRegistry` and `Prototypes` (session-wide shared read-only references).

### 7.2 EntityManager

- Generational index + free-list slot reuse
- Fixed capacity (specified at creation), runtime GC 0
- `IsAlive(EntityRef)` — verifies Index + Version together to prevent dangling references

### 7.3 ComponentStorageFlat\<T\>

A typed *view* over one slice of the frame's single `byte[]` heap — not an owner of arrays.

- Sparse-set implementation: `sparse[entityIndex] → denseIndex`, `dense[denseIndex] → entityIndex`
- Per-type heap slice: `[ count(4) ][ sparse(maxEntities×4) ][ dense(slotCapacity×4) ][ components(slotCapacity×memSize) ]`
- `slotCapacity` defaults to `maxEntities`, or the type's `MaxCount` when one is declared (§7.6)
- `unmanaged` constraint — value types only
- `Add/Remove/Has` O(1); `Remove` uses swap-with-last to keep the dense array contiguous. `Add` throws on a duplicate and on exceeding `slotCapacity`
- `DenseSpan` / `DenseToSparse` — ReadOnlySpan-based iteration, GC 0
- Because every storage lives in the same heap, snapshot / restore is one `Buffer.BlockCopy` and the hash is a fixed-order walk over the same bytes — no per-type allocation on any of the three paths

### 7.4 System Interfaces

| Interface | Method | Description |
| ---- | ---- | ---- |
| `ISystem` | `Update(ref Frame)` | Per-tick update |
| `IInitSystem` | `OnInit(ref Frame)` | Initialization |
| `IDestroySystem` | `OnDestroy(ref Frame)` | Teardown |
| `ICommandSystem` | `OnCommand(ref Frame, ICommand)` | Command handling |
| `ISyncEventSystem` | `EmitSyncEvents(ref Frame)` | Emit sync events when a verified tick is finalized |
| `IEntityCreatedSystem` | `OnEntityCreated(ref Frame, EntityRef)` | Fires from bare `CreateEntity()`, **before** any component is added (both the registry and typed prototype paths) |
| `IEntityDestroyedSystem` | `OnEntityDestroyed(ref Frame, EntityRef)` | Fires during `DestroyEntity`, **after every component has been removed**. `IsAlive` is still true; `Has<T>` is false and `Get<T>` throws |
| `ISignalOnComponentAdded<T>` | `OnAdded(ref Frame, EntityRef, ref T)` | Fires **after** the insert, with a `ref` into the storage slot — a listener may adjust what was just added |
| `ISignalOnComponentRemoved<T>` | `OnRemoved(ref Frame, EntityRef, T)` | Fires **before** the removal, **by value**. Also fires per component on `DestroyEntity` (ascending typeId) and per carrying entity on a `[KlothoCleanup]` clear |
| `ISnapshotParticipant` | `GetSnapshotSize` / `SaveSnapshot` / `RestoreSnapshot` | For deterministic state a system owns *outside* components; captured and restored alongside the frame by the ring buffer |

**SystemPhase**: `PreUpdate → Update → PostUpdate → LateUpdate` (phase specified at AddSystem; auto-sorted).
`AddSystem(system, phase, group)` takes an optional group label — a **perf-report** label only: never a sort
key, never written to the frame or the wire, and deliberately not folded into the layout fingerprint, so peers
that label their systems differently still play together.

#### Component signals — the invariants

1. **They are not events.** They fire on every *execution* of a tick, not once per tick — a re-simulating client
   may execute the same tick many times. A listener that only writes to the frame is reproduced exactly; one that
   accumulates *outside* the frame over-counts. Frame-external one-shots belong in `ISyncEventSystem`.
2. **A listener must not add or remove components.** `OnAdded` holds a `ref` into a storage slot, and a
   same-type removal swap-backs another entity into it.
3. **The destroy path interleaves.** Each component is removed immediately after its own `OnRemoved`, in
   ascending typeId order, so inside a listener only *higher* typeIds are still readable — and by the time
   `IEntityDestroyedSystem` runs the entity carries nothing.
4. **The `[KlothoCleanup]` clear fires them too**, per carrying entity in dense order, before the storage is
   emptied. This is the one place the cost is visible: a cleanup type nobody listens to is emptied with a single
   dispatch call (O(sparse)), one with a listener is walked entity by entity (O(n)).
5. **A throwing listener propagates** — nothing catches a tick exception, so it ends the match.

A per-typeId mask gates the dispatch, so a project that registers no listener pays one field read per
`Frame.Add`. Once a type *has* a listener, each `Add` of it walks the system list to find the implementers.

`ISignal` / `SignalInvoker<TSignal>` / `SystemRunner.Signal<TSignal>` is a **separate** mechanism — a general
broadcast to systems implementing a game-defined `ISignal`-derived interface. The two component-signal
interfaces deliberately do *not* derive from `ISignal`, so `Signal<TSignal>` cannot dispatch them; and its
invoker delegate allocates per call, which rules it out for per-tick paths.

### 7.5 EcsSimulation

`ISimulation` implementation.

```csharp
// constructor
new EcsSimulation(
    int maxEntities,
    int maxRollbackTicks = 10,
    int deltaTimeMs = 50,
    IKLogger logger = null,
    IDataAssetRegistryBuilder registryBuilder = null,   // mutually exclusive with assetRegistry
    IDataAssetRegistry assetRegistry = null,
    IReadOnlyDictionary<int, int> maxCountOverrides = null,      // per-typeId slot caps (§2.2)
    IReadOnlyCollection<int> prunedComponentTypeIds = null);     // denylist (§2.2)

// internal state
EcsSimulation:
  Frame           _frame
  SystemRunner    _systemRunner
  FrameRingBuffer _ringBuffer    // Frame ring buffer for rollback

  void   Initialize()
  void   Tick(List<ICommand>)
  void   Rollback(int targetTick)
  long   GetStateHash()
  void   SaveSnapshot()          // calls FrameRingBuffer.SaveFrame
  void   AddSystem(object system, SystemPhase phase, string group = null)
  void   LockAssetRegistry()     // freezes the DataAssets; called for you on every engine path

  // registered-system lookup (T : class) — lets a callback boundary reach a system's
  // secondary interface without a process-wide static
  T      GetSystem<T>()  /  bool TryGetSystem<T>(out T)  /  int GetSystems<T>(List<T>)

  // diagnostics (peer-local, opt-in — see §2.2)
  void   LogComponentHashes(...) / LogStateBreakdown(...) / LogStaticFingerprint(...)
  void   SetHashHistoryCapacity(int) / RecordHashHistory(int) / FlushHashHistory(logger, dumpTick)
  void   EnableSystemPerfMonitor(int warmupExecutions = 0)  /  string AppendSystemPerfLog()
```

When attached to the engine:

```csharp
KlothoEngine.Initialize(
    ISimulation simulation,
    IKlothoNetworkService networkService,
    IKLogger logger,
    ISimulationCallbacks simulationCallbacks,
    IViewCallbacks viewCallbacks = null);
```

### 7.6 Component Attributes

```csharp
[KlothoComponent(100)]                          // 1–99 framework-reserved; 100+ (UserMinId) for games
[StructLayout(LayoutKind.Sequential, Pack = 4)] // REQUIRED — omitting it is a compile error
public partial struct PlayerComponent : IComponent { ... }   // `partial` also required
```

| Attribute | Effect |
| ---- | ---- |
| `[KlothoComponent(typeId)]` | Registers the type. `typeId` is the discriminator `CalculateHash` walks in ascending order — **never renumber a shipped id**; it is a hash-and-wire break. The id plane is independent of `[KlothoSerializable]` and `[KlothoDataAsset]` |
| `MaxCount = n` *(named arg)* | Caps the type's reserved slots at `min(n, maxEntities)` instead of one per entity. Exceeding it throws rather than growing. Ignored on a singleton (`KLSG_ECS006`, warning) |
| `[KlothoSingletonComponent]` | At most one carrier per frame; `Frame.Add<T>` throws on a second. Read via `GetSingleton` / `GetReadOnlySingleton` / `TryGetSingleton` |
| `[KlothoCleanup(CleanupMode)]` | One-tick lifetime. `RemoveComponent` empties the storage via the type-erased clear dispatch (O(sparse), no iteration); `DestroyEntity` destroys the carrier (pre-allocated buffer, `IsAlive`-guarded, so two markers destroy once). Both passes run at §2.6 step 2c/2d |
| `[KlothoCoreComponent]` | Engine-essential: force-excluded from the pruning denylist. Orthogonal to lifetime — combining it with `[KlothoCleanup]` is `KLSG_ECS007` (warning) |

**Determinism inputs.** `maxEntities`, the sorted typeId set, type names, each type's slot capacity
(`MaxCount`) and each type's `CleanupMode` are folded into the **layout fingerprint** that peers exchange
before the first tick (§9.4). Two builds that disagree about any of them are refused at the ready exchange
rather than diverging from tick 0.

**What the generator emits.** One `{TypeName}.g.cs` per component, containing the type's
`Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` bodies and a `TYPE_ID` constant, plus a
`ComponentStorageRegistry.Register<T>(TYPE_ID, isSingleton:/maxCount:/core:/cleanup:)` call carrying the
attribute metadata. The heap layout itself is **not** generated — `ComponentStorageRegistry` computes it at
runtime from the registered set, which is why adding an assembly changes the layout fingerprint.

Analyzer rules on these attributes: `KLOTHO_STRUCT_LAYOUT_MISSING` (error) · `KLSG_ECS006` (MaxCount on a
singleton, warning) · `KLSG_ECS007` (cleanup on a core component, warning) · `KLSG_ECS008` (`DestroyEntity`
on a singleton, error) · `KLSG_ECS009` (undefined `CleanupMode` value, error).

### 7.7 Entity Prototype System

A pattern for registering an initial entity composition (a combination of components) in code and reusing it via a single ID.

#### Interface

```csharp
public interface IEntityPrototype
{
    void Apply(Frame frame, EntityRef entity);
}
```

#### Registry

```csharp
public class EntityPrototypeRegistry
{
    void Register(int prototypeId, IEntityPrototype prototype);  // duplicate ID → InvalidOperationException
    internal EntityRef Create(int prototypeId, Frame frame);
}
```

#### Frame Integration

```csharp
Frame:
  EntityPrototypeRegistry Prototypes            // registry (not part of CopyFrom — rollback-safe)
  EntityRef CreateEntity(int prototypeId)       // registry lookup; delegates to Prototypes.Create
  EntityRef CreateEntity<TProto>(in TProto p)   // typed: no registration, no dictionary lookup,
                                                // and the prototype instance carries per-spawn data
```

Both paths call bare `CreateEntity()` first and then `Apply`, so `OnEntityCreated` fires **before** any
component is added either way. Use the typed overload when the spawn needs parameters — it avoids the
"create, then `ref`-set, then `RefreshPreviousTransform`" sequence entirely (§7.1).

#### Usage

```csharp
// register (during game initialization)
frame.Prototypes.Register(1, new PlayerPrototype());

// create (from a system or command handler)
EntityRef player = frame.CreateEntity(1);
```

#### Design Principles

| Item | Description |
| ---- | ---- |
| Code-only | No editor / asset dependency |
| GC 0 | `Apply` allocates nothing, only `frame.Add<T>()` |
| Rollback-safe | `Prototypes` is not copied by `CopyFrom` (immutable registry) |
| ID collision detection | `Register` throws on duplicate ID |

---

## 8. Deterministic Math

### 8.1 FP64 — 32.32 Fixed-Point

| Item | Value |
| ---- | ---- |
| Format | Upper 32 bits: integer part; lower 32 bits: fractional part |
| Scaling factor (ONE) | `1L << 32` = 4,294,967,296 |
| Precision (Epsilon) | 2^-32 ≈ 2.33 × 10^-10 |
| Representable range | ±2,147,483,647.999... (int32 range) |
| Internal storage | `long _rawValue` (64-bit signed) |

**Constants**:

| Name | Value |
| ---- | ---- |
| Zero | 0 |
| One | 4294967296 |
| Half | 2147483648 |
| Pi | 3.14159265358979... (FP64) |
| TwoPi | 6.28318530717959... (FP64) |
| HalfPi | 1.57079632679490... (FP64) |
| Deg2Rad | 0.01745329251994... (FP64) |
| Rad2Deg | 57.2957795130823... (FP64) |
| Epsilon | 1 (raw) = 2^-32 |

**Arithmetic**:

| Operation | Implementation |
| ---- | ---- |
| Add / Sub | Direct `long` arithmetic |
| Multiply | Fast path `(a × b) >> 32`; on overflow, Hi/Lo 4-mul decomposition (zero GC) |
| Divide | Fast path `(a << 32) / b`; on overflow, shift-and-divide (zero GC) |
| Sqrt | 2-pass binary-restoring square root (64-bit arithmetic only, zero GC) |
| Compare | Direct `long` comparison |

**Trigonometry**:

| Function | Algorithm | Detail |
| ---- | ---- | ---- |
| Sin / Cos | LUT (default) | ~1572 entries, 0.001 rad spacing, linear interpolation, [0, π/2] → quadrant expansion |
| Sin / Cos | CORDIC (alternative) | 32 iterations, precomputed atan(2^-i) table, K ≈ 1.6467 |
| Atan2 | CORDIC vectoring | 32 iterations, quadrant handling, special cases (0,0)(0,±)(±,0) |
| Acos | Composition | `atan2(sqrt(1 - x²), x)` |
| Tan | Composition | `Sin(a) / Cos(a)` |

**Conversions**:

| Method | Direction |
| ---- | ---- |
| `FromInt(int)` | int → FP64 (left-shift 32) |
| `FromFloat(float)` | float → FP64 (× ONE) |
| `FromDouble(double)` | double → FP64 (× ONE) |
| `FromRaw(long)` | raw → FP64 (direct assignment) |
| `ToInt()` | FP64 → int (right-shift 32) |
| `ToFloat()` | FP64 → float (÷ ONE) |
| `ToDouble()` | FP64 → double (÷ ONE) |

### 8.2 FPVector2

```csharp
public struct FPVector2 : IFixedVector2, IEquatable<FPVector2>
{
    public FP64 x, y;
}
```

**Static constants**: Zero, One, Up, Down, Left, Right

**Operations**:

| Operation | Description |
| ---- | ---- |
| Magnitude | `max * sqrt((x/max)² + (y/max)²)` — scaled by max component to avoid overflow |
| SqrMagnitude | `x * x + y * y` (FP64 arithmetic) |
| Normalized | `this / Magnitude` (zero-vector check) |
| Dot | `(aX·bX + aY·bY) >> 32` |
| Cross (2D) | `aX·bY - aY·bX` |
| Distance / SqrDistance | Magnitude of the difference vector |
| Lerp / MoveTowards | Linear interpolation / max-distance-limited move |
| Reflect | Reflection vector |
| Angle / SignedAngle | Angle between two vectors |
| ClampMagnitude | Clamp magnitude |

### 8.3 FPVector3

```csharp
public struct FPVector3 : IFixedVector3, IEquatable<FPVector3>
{
    public FP64 x, y, z;
}
```

**Static constants**: Zero, One, Up, Down, Left, Right, Forward, Back

**Additional operations** (in addition to FPVector2's):

| Operation | Description |
| ---- | ---- |
| Cross (3D) | Standard vector cross product |
| Project / ProjectOnPlane | Vector projection |
| Scale | Component-wise multiplication |
| ToXY() / ToXZ() | 2D conversion |

### 8.4 DeterministicRandom

**Algorithm**: Xorshift128+

**Seed initialization** (SplitMix64):

```
z = seed
z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL
_state0 = z ^ (z >> 31)

z = _state0 + 0x9E3779B97F4A7C15     // golden-ratio constant
(repeat the same process)
_state1 = z ^ (z >> 31)
```

**Xorshift128+ step**:

```
s1 = _state0;  s0 = _state1;  result = s0 + s1
_state0 = s0
s1 ^= s1 << 23
_state1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5)
return result
```

**Seeding / state**:

| Method | Purpose |
| ---- | ---- |
| `new DeterministicRandom(int seed)` / `SetSeed(int seed)` | Initialize / reseed from an int seed (`Seed` property reads it back). |
| `FromSeed(ulong worldSeed, ulong featureKey, ulong index = 0)` | Derive an independent stream per feature from a shared world seed. |
| `SetFullState(ulong state0, ulong state1)` | Restore exact internal state (snapshot / rollback / resync). |

**Distribution methods**:

| Method | Returns | Algorithm |
| ---- | ---- | ---- |
| `NextInt()` | 0 ~ 2^31-1 | `NextUInt64() & 0x7FFFFFFF` |
| `NextInt(min, max)` | [min, max) | Modular arithmetic |
| `NextIntInclusive(min, max)` | [min, max] | Modular arithmetic |
| `NextFixed()` | [0, 1) FP64 | `NextUInt64() & 0xFFFFFFFF` → fixed-point |
| `NextFixed(min, max)` | [min, max) FP64 | Scaled `NextFixed()` |
| `NextFixedInclusive()` / `NextFixedInclusive(min, max)` | [0, 1] / [min, max] FP64 | Inclusive-bound variants |
| `NextBool()` | true/false | LSB check |
| `NextChance(percent)` | bool | Percentage chance |
| `NextWeighted(int[] weights)` | int (index) | Weighted selection |
| `NextInsideUnitCircle()` | FPVector2 | [-1,1]² rejection sampling |
| `NextInsideUnitSphere()` | FPVector3 | [-1,1]³ rejection sampling |
| `NextDirection2D()` | FPVector2 | Uniform angle: `NextFixed() * TwoPi` |
| `NextDirection3D()` | FPVector3 | Uniform spherical distribution (θ, z parameters) |
| `NextRotation()` | FPQuaternion | Uniform random orientation |
| `Shuffle<T>(array)` | void | Fisher-Yates shuffle |

---

## 9. Network

### 9.1 Transport Abstraction

```
INetworkTransport:
  Connect(address, port)
  Disconnect()
  Send(peerId, data, deliveryMethod)
  Broadcast(data, deliveryMethod)
  PollEvents()

  OnDataReceived:       Action<int, byte[]>    (peerId, data)
  OnPeerConnected:      Action<int>            (peerId)
  OnPeerDisconnected:   Action<int>            (peerId)
  OnConnected:          Action
  OnDisconnected:       Action
```

**DeliveryMethod**:

| Mode | Ordered | Reliable |
| ---- | ---- | ---- |
| Unreliable | X | X |
| Reliable | X | O |
| ReliableOrdered | O | O |
| Sequenced | O | X (drops stale packets) |

### 9.2 Message Types (`NetworkMessageType : byte`)

| Type | ID | Purpose |
| ---- | ---- | ---- |
| **Room** | | |
| RoomHandshake | 1 | Handshake for multi-room server routing |
| JoinRoom | 10 | Join a room |
| LeaveRoom | 11 | Leave a room |
| PlayerReady | 12 | Player ready state. Also carries the two **setup fingerprints** (`LayoutFingerprint`, `EnvironmentFingerprint`, appended after the original fields) — the host relays a ready verbatim, so every peer ends up comparing against every other and a cold-start match that never delivers a FullState is still checked (§9.4.4) |
| GameStart | 13 | Game start + config delivery |
| PlayerJoin | 14 | Player-join notification. Carries the optional lobby `Ticket` (opaque base64url credential) and an unverified `ClaimedDisplayName` (no-lobby nickname) — see Player Identity Handoff (§9.6) |
| JoinReject | 15 | Room-join rejection. `Reason` is a wire byte: room codes 1~5, identity codes 6~11 — distinct from the client-local `JoinFailReason` enum (mapped by `FromJoinReject`); see §9.6 |
| **Command** | | |
| Command | 20 | Player input command |
| CommandAck | 21 | Command receipt ack |
| CommandRequest | 22 | Re-request a missing command |
| ReliableCommandSubmit | 23 | Reliable command submit (`IReliableCommand`) — client→authority, tick-less; the authority assigns the execution tick. ServerDriven path; P2P uses the legacy path (see API §3.4) |
| **Sync** | | |
| SyncHash | 30 | State-hash sync verification |
| SyncHashAck | 31 | SyncHash receipt ack |
| FullState | 32 | Full state transmission |
| FullStateRequest | 33 | Request full state |
| **Connection** | | |
| Ping | 40 | Latency measurement |
| Pong | 41 | Ping response |
| Disconnect | 42 | Disconnect |
| **Handshake** | | |
| SyncRequest | 50 | Sync request |
| SyncReply | 51 | Sync reply |
| SyncComplete | 52 | Sync complete |
| **Spectator** | | |
| SpectatorJoin | 60 | Spectator join request |
| SpectatorAccept | 61 | Spectator join accept |
| SpectatorInput | 62 | Verified inputs delivered to spectator |
| SpectatorLeave | 63 | Spectator leave |
| **Reconnect / Late Join** | | |
| ReconnectRequest | 70 | Reconnect request |
| ReconnectAccept | 71 | Reconnect accept |
| ReconnectReject | 72 | Reconnect reject |
| LateJoinAccept | 73 | Late-join accept |
| RecommendedExtraDelayUpdate | 74 | Dynamic InputDelay push (server → client) when smoothed RTT change crosses the asymmetric UP/DOWN threshold. Seed value also carried inline on SyncComplete / LateJoinAccept / ReconnectAccept |
| LateJoinNotification | 75 | Host (P2P) / server (SD) → existing peers and spectators when a late-joiner is admitted. Recipients update their player list (`OnPlayerJoined` / `PlayerCount`) so mid-match joins propagate without a poll. Forged-sender guards: P2P rejects when `IsHost` is true; SD rejects when `peerId != 0`. Idempotent — duplicate notifications for the same player are dropped against the local roster |
| ResyncFailureReport | 76 | Guest → host: a resync attempt failed. Feeds the recovery ladder's rung-3 corrective-reset attempt budget (`CorrectiveResetMaxAttempts`) |
| MatchAbort | 77 | Host → guests: corrective resets are exhausted (rung 4). Broadcast when `AutoAbortOnRecoveryExhausted` is true, paired with a local `AbortReason.StateDivergence` |
| PlayerStateNotification | 78 | In-game roster change — host (P2P) → existing guests on a confirmed disconnect / reconnect / leave, so guests exclude a departed peer from the timing vote |
| PlayerJoinNotification | 79 | Pre-game (lobby) roster add — host (P2P) / server (SD) → existing peers when a player completes the normal-join handshake, so every peer's roster is consistent before StartGame |
| **Server-Driven Mode** | | |
| ClientInput | 80 | Client → server input |
| VerifiedState | 81 | Server → client verified state |
| InputAck | 82 | Server's input-receipt ack |
| ClientInputBundle | 83 | Bundled input transmission |
| PlayerBootstrapReady | 84 | Client → server: bootstrap completed (player ready) — bootstrap handshake |
| BootstrapBegin | 85 | Server → client: open bootstrap window (FirstTick, TickStartTimeMs) |
| CommandRejected | 86 | Server → client unicast on input rejection (tick, cmdTypeId, RejectionReason). Surfaces as engine `OnCommandRejected` (§2.3) on the originating client |
| PlayerLeaveNotification | 87 | Pre-game (lobby) roster removal — the counterpart of `PlayerJoinNotification` for a player leaving before StartGame (in-game leave uses `PlayerStateNotification`) |
| **Config Layer** | | |
| SimulationConfig | 90 | Simulation-parameter payload |
| PlayerConfig | 91 | Per-player config payload |
| ReactiveExtraDelayReport | 92 | Client → authority report of the client's *effective* extra delay (baseline + reactive), so the authority folds the client's reactive correction into its authoritative baseline. SD: client → server; P2P: guest → host (star topology, peerId 0) |
| **User-Defined Reservation** | | |
| UserDefined_Start | 200 | Values beyond this can be cast and used freely by sample/game code (prevents inverting Runtime enum dependency direction) |

### 9.3 Message Serialization

All messages use `SpanWriter/SpanReader`-based GC-free serialization.
The first byte is the `NetworkMessageType` value.

**CommandMessage** (ID=20):

```text
[byte 20] [int Tick] [int PlayerId] [int SenderTick] [int DataLength] [byte[] CommandData]
```

**SyncHashMessage** (ID=30):

```text
[byte 30] [int Tick] [long Hash] [int PlayerId]
```

**GameStartMessage** (ID=13):

Carries the SessionConfig payload + StartTime + PlayerIds together (a separated SessionConfig propagation path).

```text
[byte 13]
  [long StartTime]                    // absolute game-start time, in SharedNow
  [int  RandomSeed]
  [int  MaxPlayers]
  [int  MaxSpectators]
  [int  MinPlayers]
  [bool AllowLateJoin]
  [int  ReconnectTimeoutMs]
  [int  ReconnectMaxRetries]
  [int  LateJoinDelayTicks]
  [int  ResyncMaxRetries]
  [int  DesyncThresholdForResync]
  [int  CorrectiveResetCooldownMs]
  [int  CountdownDurationMs]
  [int  CatchupMaxTicksPerFrame]
  [int  AbortGraceMs]
  [int  EndGracePolicy]               // EndGracePolicy enum (Continue/Pause)
  [int  EndGraceMs]
  [int  ClientShutdownGraceMs]
  [int  PlayerIdCount] [int[] PlayerIds]
```

The same SessionConfig payload is also propagated via `LateJoinAcceptMessage` and `ReconnectAcceptMessage` so a joiner ends up with byte-identical session parameters regardless of the join path.

**Ping/Pong** (ID=40/41):

```text
[byte 40|41] [long Timestamp] [int Sequence]
```

### 9.4 Desync Detection

```text
Every SyncCheckInterval (20 ticks):
├─ compute local state hash (ISimulation.GetStateHash → FNV-1a 64-bit)
├─ _localHashes[tick] = hash
├─ broadcast SyncHashMessage (Unreliable)
├─ on remote-hash receipt, compare
└─ on mismatch → raise OnDesyncDetected(localHash, remoteHash)
```

**Localizing a mismatch.** The single 64-bit value says *that* peers diverged, not *what* or *when*.
Two peer-local aids narrow it down (both configured in §2.2, both off-by-default-free):

- **`StateHashBreakdown`** — the per-component-type split of the same fold, so a mismatch names the component
  type. `LogComponentHashes` dumps `count` + `hash` per typeId in ascending order; diffing a client log against
  the server's at the suspect tick identifies the type without a debugger.
- **`StateHashRing`** — a rolling window of per-tick, per-type hashes (`DiagnosticHistoryTicks`, default 60)
  recorded as the match runs and dumped by `FlushHashHistory(logger, dumpTick)` on detection, so the **first**
  diverged tick is visible rather than the tick the check happened to notice.

Read the two **fingerprints** (§9.4.4) before either of these: a component-registry difference diverges from
tick 0, which makes every per-component number a symptom rather than a cause.

### 9.4.1 Hash Gate (post-`ApplyFullState`)

```text
ApplyFullState(tick, data, hash, ApplyReason):
├─ restore from data
├─ localHash = ISimulation.GetStateHash()
├─ compare localHash vs advertised stateHash
└─ on mismatch → raise OnHashMismatch(tick, localHash, remoteHash)
```

The hash gate fires on every `ApplyFullState` entry point (`LateJoin` / `InitialFullState` / `ResyncRequest` / `CorrectiveReset` / `Reconnect`). In P2P, `OnHashMismatch` is wired to `HandleHashMismatchForCorrectiveReset` → `TryCorrectiveReset` (host-only). In SD / Spectator the event still fires but the corrective path is a no-op (host-only guard).

### 9.4.2 Corrective Reset (P2P, host-only)

```text
OnHashMismatch (host) ─► HandleHashMismatchForCorrectiveReset
                          └─► TryCorrectiveReset(divergenceTick):
                                ├─ if (!IsHost) return
                                ├─ if (now - _lastCorrectiveResetMs < CorrectiveResetCooldownMs) return
                                ├─ serialize current state (cached if same tick)
                                ├─ BroadcastFullState(CurrentTick, data, hash, FullStateKind.CorrectiveReset)
                                └─ host self-apply (host does not receive its own broadcast)

ApplyFullState (ApplyReason.CorrectiveReset):
└─ on successful hash match → raise OnMatchReset(ResetReason.StateDivergence)
```

The cooldown (`CorrectiveResetCooldownMs`, default 5000ms) prevents broadcast storms under persistent divergence. Guests receive `FullStateKind.CorrectiveReset` via `OnFullStateReceived` and apply with `ApplyReason.CorrectiveReset` (retreat allowed). `OnMatchReset` only fires when the post-restore hash matches the broadcast hash — on mismatch, the mid-match desync pipeline retries.

### 9.4.3 Chain Stall Watchdog (peer-local)

```text
Every Update():
└─ CheckChainStallTimeout():
     ├─ if (Phase != Playing || engine ended) return
     ├─ lag = CurrentTick - LastVerifiedTick
     ├─ threshold = max(SessionConfig.ReconnectTimeoutMs / TickIntervalMs + 100, SimulationConfig.MinStallAbortTicks)
     ├─ if (lag < threshold) return
     └─ AbortMatch(AbortReason.ChainStallTimeout) → state = Aborted, OnMatchAborted raised
```

Both host and guest peers run the watchdog locally — either can self-abort when its verified chain stalls past the threshold. Distinct from `Finished` so abort-handling UI (replay save / score aggregation) can branch via `KlothoStateExtensions.IsEnded()`.

### 9.4.4 Setup Fingerprints (pre-first-tick build check)

The desync pipeline above catches divergence *after* it happens. Two fingerprints catch the most common cause
*before* the first tick, on the one pre-game message every peer sends.

| Fingerprint | Folds | Severity |
| ---- | ---- | ---- |
| **Layout** | `maxEntities`, the sorted registered typeId set, type names, per-type slot capacity (`MaxCount`) and per-type `CleanupMode` | **Fatal.** The registered type set is state-hash input, so the peers would diverge from tick 0 |
| **Environment** | static colliders XOR navmesh XOR the game's own slot (`IGameFingerprintSource`); registry deliberately excluded so the two halves stay orthogonal | **Warning.** Outside the state hash, and runtime rebakes move it legitimately — compared only before the match runs |

```text
PlayerReady (own fields + LayoutFingerprint + EnvironmentFingerprint)
└─ receiver: KlothoEngine.CheckReadyFingerprints(playerId, remoteLayout, remoteEnv, compareEnvironment)
     └─ ReadyFingerprintVerdict.LayoutMismatch and !AllowLayoutMismatch:
          ├─ transport control (dedicated server / P2P host) → disconnect with
          │                                                    JoinFailReason.LayoutMismatch (wire code 12)
          └─ no transport control (guest / spectator)        → AbortMatch(AbortReason.LayoutMismatch)
```

- `0` is the "not provided" sentinel on both fields, and a difference counts only when **both** sides supplied
  a value — so an unwired or older peer is never refused on this path.
- The **server-driven client compares nothing**: the server relays other clients' readies, so this client holds
  *their* fingerprints and never the server's (the server has no local player and sends no ready). Comparing
  client-to-client would either stay silent when both differ from the server but agree with each other, or make
  both report without either knowing which matches the authority. The server is the judge; the client learns its
  verdict from the reject reason or the initial FullState comparison.
- A **spectator** sends no ready but receives every player's, so it gets the layout check for free.
- The refusal message names both sanctioned fixes: load the same assembly set on both sides (the usual cause is
  an Editor session registering Editor-only test-assembly components against a player / server build), or prune
  the difference via `SetRuntimePrunedComponentTypeIds` — an **authority-side** action, since the prune set is
  host/server authoritative. When the type *counts* agree the difference is component metadata rather than the
  type set, and the message says so by printing `cleanup=` next to `types=`.
- `KlothoSessionSetup.AllowLayoutMismatch` / `SpectatorSessionSetup.AllowLayoutMismatch` (default `false`)
  downgrade the refusal to a log for development. Deliberately on the setup and not in `ISimulationConfig`: a
  guest runs the config it received over the wire, so a config-borne flag would read its default on exactly the
  peer that needs it off.
- The combined value the FullState path has always carried is still `layout ^ environment`, bit for bit, with the
  `0 → 1` sentinel normalization in exactly one place — splitting the fold changed no wire value.

### 9.5 IKlothoNetworkService Events

| Event | Signature | Description |
| ---- | ---- | ---- |
| OnGameStart | `Action` | Game start |
| OnCountdownStarted | `Action<long>` | Countdown started (startTime) |
| OnPlayerJoined | `Action<IPlayerInfo>` | Player joined |
| OnPlayerLeft | `Action<IPlayerInfo>` | Player left |
| OnCommandReceived | `Action<ICommand>` | Command received |
| OnDesyncDetected | `Action<int, int, long, long>` | Desync detected (playerId, tick, localHash, remoteHash) |
| OnFrameAdvantageReceived | `Action<int, int>` | Remote frame-advantage received (peerId, advantageTicks) |
| OnLocalPlayerIdAssigned | `Action<int>` | Local player ID assignment completed |
| OnFullStateRequested | `Action<int, int>` | Full-state request received (peerId, requestTick) |
| OnFullStateReceived | `Action<int, byte[], long, FullStateKind>` | Full state received (tick, data, hash, kind). `kind` distinguishes `Unicast` (resync / late-join reply), `CorrectiveReset` (P2P host-broadcast on hash divergence), `InitialState` (SD session-start broadcast) |
| OnPlayerDisconnected | `Action<IPlayerInfo>` | Player disconnected (awaiting reconnect) |
| OnPlayerReconnected | `Action<IPlayerInfo>` | Player reconnected (Host) |
| OnReconnecting | `Action` | Reconnect in progress (Guest) |
| OnReconnectFailed | `Action<byte>` | Reconnect failed (Guest). The byte is a `ReconnectRejectReason` value (`InvalidMagic`, `InvalidPlayer`, `TimedOut`, `AlreadyConnected`, `DeviceMismatch`, `TransportStartFailed`, `MaxRetries`, `Unknown`). Use `ReconnectRejectReason.ToName(reason)` for a symbolic name, `ReconnectRejectReason.RequiresUserChoice(reason)` to detect `AlreadyConnected`. Cold-start paths surface the same reason via `ReconnectFailedException.Reason` |
| OnReconnected | `Action` | Reconnect completed (Guest) |
| OnLateJoinPlayerAdded | `Action<int, int>` | Late-join player added (playerId, joinTick) |
| OnPhaseChanged | `Action<SessionPhase>` | Session phase transitioned (`Lobby → Countdown → Running → Ended`). Surfaced to the game as `IKlothoSessionObserver.OnPhaseChanged` so game code does not poll the service each frame |
| OnPlayerCountChanged | `Action<int>` | Active player count changed (joins, leaves, late-joins). Surfaced to the game as `IKlothoSessionObserver.OnPlayerCountChanged`. `ISpectatorService.OnPlayerCountChanged` is the parallel surface for spectator sessions; the session forwards either source to the same observer callback |
| OnAllPlayersReadyChanged | `Action<bool>` | All-players-ready gate flipped (true when every active player has ready-signaled; false on subsequent join). Surfaced to the game as `IKlothoSessionObserver.OnAllPlayersReadyChanged` |

`IPlayerInfo` (the argument to the player events above) exposes `PlayerId`, `DisplayName`, `Account`, `IsReady`, `Ping`, `ConnectionState`. `DisplayName` / `Account` are the authoritative identity — see Player Identity Handoff (§9.6).

### 9.6 Extended Network Subsystems

#### Spectator System

A spectator receives only verified state without input and reproduces the simulation without synchronization.

```
SpectatorJoin(60) ──► host
host ──► SpectatorAccept(61) [SimulationConfig + SessionConfig + SpectatorStartInfo (FullState + startTick + tickInterval)]
Every SPECTATOR_INPUT_INTERVAL ticks:
  host ──► SpectatorInput(62) [startTick, tickCount, verified input batch]
```

- `ISpectatorService / SpectatorService` — manages spectator-side connection and input receipt
- `SpectatorService.OnSimulationConfigReceived` / `OnSessionConfigReceived` — fired on Accept receipt. The spectator client creates Engine/Simulation with the server-authoritative values only after both events arrive (deferred-Engine-creation pattern).
- `SpectatorService.SetEngine(engine)` — deferred-Engine injection API used in the pattern above
- `engine.StartSpectator(SpectatorStartInfo)` — start the engine in spectator mode
- `engine.IsSpectatorMode` — whether spectator mode is active (prediction/rollback disabled)
- Capacity gate — spectator admission is bounded by `SessionConfig.MaxSpectators`, exposed at the network layer as `ServerNetworkService.MaxSpectatorsPerRoom`. The transport listen capacity is `MaxPlayersPerRoom + MaxSpectatorsPerRoom`, and `RoomRouter` keeps the player capacity gate independent of the spectator slots.

#### Reconnect Protocol

A player disconnected during Playing can reconnect.

```
Guest ──► ReconnectRequest(70)
Host  ──► ReconnectAccept(71) + FullState
       or ReconnectReject(72) (timeout / session expired)
```

While awaiting reconnect, the host fills that player's input with empty commands (`OnDisconnectedInputNeeded`).

#### Late Join Protocol

When `AllowLateJoin = true`, a player can join an in-progress game.

```
Guest ──► JoinRoom(10) → after handshake completes
Host  ──► FullState(32) + the command stream so far
Guest: enters CatchingUp state, catches up at up to CatchupMaxTicksPerFrame ticks/frame
On catchup completion → OnCatchupComplete → IViewCallbacks.OnLateJoinActivated
```

#### Server-Driven Mode

Under `NetworkMode.ServerDriven`, the server holds input authority.

```
Client ──► ClientInput(80) / ClientInputBundle(83)
Server ──► InputAck(82) + VerifiedState(81) [confirmed state/hash included]
```

- `ServerNetworkService` — server side: input collection, frame verification, state broadcast
- `ServerDrivenClientService` — client side: input transmission, server-state receipt
- `Room / RoomManager / RoomRouter` — multi-room: a single server managing multiple independent game sessions

#### Dynamic InputDelay (client-reactive policy)

Non-host sessions attach `DynamicInputDelayPolicy` automatically. It escalates `engine.RecommendedExtraDelay`
when the authoritative push (`RecommendedExtraDelayUpdate`, 74) has not yet caught up with what the client is
actually experiencing. Thresholds are server-authoritative (§2.2).

| Trigger | Signal | Notes |
| ---- | ---- | ---- |
| **A — PastTick reject window** | non-spawn `CommandRejected(PastTick)` count within `ReactiveWindowTicks` crosses `ReactiveEscalateThreshold` | SD only — a P2P guest receives no `CommandRejected` |
| **B — rollback burst** | rollback count within `RollbackWindowTicks` reaches `RollbackBurstCount` | the primary trigger for P2P guests |

- Escalation calls `engine.EscalateExtraDelay(ReactiveStep, ReactiveMax)`; `ReactiveMax` is clamped at
  `SimulationConfig.Validate()` to `MaxRollbackTicks / 2`.
- **Grace gate**: both triggers ignore events within `ServerPushGraceTicks` of the last authoritative push
  (tracked via `OnExtraDelayChanged`), so the reactive path never double-counts against it.
- **Cooldown / de-escalation**: rollback-triggered escalations are spaced by `ReactiveEscalateCooldownTicks`,
  and the reactive component walks back down after `ReactiveDeEscalateStableTicks` of quiet.
- The client reports its **effective** delay (baseline + reactive) back with `ReactiveExtraDelayReport` (92) so
  the authority folds the correction into its own baseline instead of fighting it.
- Games normally do not subscribe to `OnCommandRejected` / `OnRollbackExecuted` for delay control — only for
  game-specific responses such as shaping a spawn-command retry.
- Smoothing input for the authoritative push side: `PlayerRttSmoother` (a 5-sample sliding median per player,
  ≈5 s) feeds the asymmetric UP/DOWN threshold decision, rate-limited per peer.

#### Player Identity Handoff

Carries a lobby-issued credential into the session, validates it at the trust boundary, and propagates the authoritative identity to every peer. Fully **opt-in**: with no provider/validator configured the path is inert and behavior is unchanged (no-lobby / LAN / prototype). The core only **carries, hooks, and propagates** — ticket signing, lobby redeem, and crypto live in the Game Service Layer (reference implementations under `Samples/IdentityP2pRef` / `Samples/IdentitySdRef`), per the layer separation (§Network Layer Separation).

Identity surface on `IPlayerInfo`: `Account` (stable id, invariant across sessions) and `DisplayName` (authoritative display name) — both default to `""` when no lobby is present, and are distinct from `DeviceId` (reconnect fingerprint; never interchanged).

**Carriage** — `PlayerJoinMessage` (14):
- `Ticket` — opaque base64url credential (the core never parses it), presented to the host (P2P) / server (SD).
- `ClaimedDisplayName` — unverified client-claimed nickname; adopted **only when no validator is present** (LAN/dev). A validator always ignores it (spoof-proof); `Account` is never claimable.

**Validation hook** — `IPlayerIdentityValidator.BeginValidate(in IdentityValidationRequest)` is invoked at `CompletePeerSync`, **before** slot reservation (a reject consumes no slot). It returns an `IIdentityValidation` handle the core polls per tick (`IsComplete` → `Outcome`), so an online redeem runs off the network loop without blocking it.

- **SD (online)** — local first-pass (signature + expiry) rejects obvious forgeries, then a lobby `redeem` round-trip is the authority (nonce consume, real-time ban, match binding).
- **P2P (offline)** — the host verifies the lobby signature + expiry + `sessionId` + a session-scoped nonce; guests trust the host's verdict (**semi-trust**: the host is the single verifier and the original ticket is not propagated).
- **Host self** — the host validates its own ticket; a host-self reject downgrades to the `"Host"` fallback (the host cannot kick itself).

`IdentityValidationOutcome.Accept(account, displayName)` overlays the authoritative identity; `Reject(wireCode)` sends `JoinReject` then disconnects.

**Reject reason codes** — the disconnect-payload **wire byte** and the client-local `JoinFailReason` enum are **distinct numbering schemes**, mapped by `JoinFailReason.FromJoinReject`:

| Meaning | Wire byte | `JoinFailReason` enum |
| ---- | ---- | ---- |
| (room reasons) | 1~5 | 7~11 |
| IdentityInvalid (bad signature / format / empty-or-oversize account) | 6 | 12 |
| IdentityExpired | 7 | 13 |
| IdentitySessionMismatch (cross-match / wrong room) | 8 | 14 |
| IdentityRejected (redeem deny / ban / consumed nonce) | 9 | 15 |
| IdentityRequired (validator present, no ticket) | 10 | 16 |
| IdentityValidationFailed (transport fault / redeem timeout) | 11 | 17 |
| unmapped | 0 / other | Unknown |

A redeem-returned code outside 6~11 is clamped to 9 (`IdentityRejected`) — a trust boundary so a buggy lobby cannot make the client misread the disconnect as a retryable room reason.

**Propagation** — the authoritative `Account` / `DisplayName` ride the existing roster path as a unified `RosterEntry` (`PlayerId / ConnectionState / ReadyState / Account / DisplayName`), which **replaces** the former index-parallel lists on `SyncComplete` (52), `LateJoinAccept` (73), `ReconnectAccept` (71), `SpectatorAccept` (61) and the in-memory `ConnectionResult`. Per-join notifications `PlayerJoinNotification` / `LateJoinNotification` (75) carry scalar `Account` / `DisplayName`. The name fields are encoded as `FixedString64` (62 UTF-8 bytes); a longer value is truncated at a char boundary by `RosterEntry.ToFixedName` with a build warning + dev assert (not expected — the validators reject an empty or >62-byte account at the source).

**Reconnect** — identity is **not** re-validated on reconnect (the nonce is one-time and the ticket may have expired). The authority caches `{account, displayName}` keyed by session slot at first validation and restores it after the reconnect auth gate (`SessionMagic`) passes; the cache is evicted at session end.

**Lobby protocol (Game Service Layer, recommended)** — the core does not implement this; the reference validators follow it:

```
issueTicket  (lobby → client, on login / match):  req {authToken, matchId} → res {ticket, endpoint, roomId, mode}
redeemTicket (SD server → lobby):                 req {ticket, sessionId, serverId, roomId}
                                                  → res {ok, account, displayName} | {ok:false, reason}
```

- Ticket = signed payload `{account, displayName, sessionId, issuedAt, expiresAt, nonce}` + signature (Ed25519; the lobby public key is distributed to clients/servers). `issuedAt` is carried but not checked — the operative time bound is `expiresAt > now`.
- SD redeem is authoritative: the nonce is consumed with an **idempotency window** (a repeat within the window returns the cached result, so a player who passed validation but dropped before slot reservation recovers on rejoin; beyond the window it is a replay reject). Match binding uses the ticket's own `sessionId` as authority (the `sessionId` arg is a cross-check hint); the lobby rejects a match not assigned to the redeeming `serverId`.
- **roomId trust (SD multi-room)** — on a server hosting several rooms, `roomId` is a **client-asserted routing hint** (carried in `RoomHandshakeMessage`, sent pre-join with no access control — a client can route to any in-range room, and an unknown one is lazily created), **not** an authority. Trust comes from the signed `sessionId` being bound to a `(serverId, roomId)` in the lobby's assignment ledger: the validator carries the *routed* room (`IdentityValidationRequest.RoomId`) into the redeem, and the lobby cross-checks it against the bound room — a mismatch is `IdentitySessionMismatch` (wire 8), with **no slot consumed** (the hook runs before slot reservation). The binding is **per match** (`sessionId` = matchId; all players of a match share one room). Single-room SD binds to room `0`; P2P is not multi-room and passes a `-1` sentinel the validator ignores (the `roomId` field is append-only opt-in — a validator that does not read it is unaffected).
- P2P uses signature verification only (offline, no network) — no real-time ban, so a short `expiresAt` is recommended.

**Builder** — guest `WithLobbyIdentity(IPlayerIdentityProvider)` supplies the ticket via `GetTicket()`; host/server `WithIdentityValidator(IPlayerIdentityValidator)`. These are independent opt-in features (no hard inter-dependency); `Build()` emits an advisory when a validator has no host/server entry point or a provider has no guest entry point.

---

## 10. Replay System

### 10.1 States

```
Idle → Recording → (Stopped) → Idle
Idle → Playing → Paused → Playing
                         → Finished → Idle
```

| State | Description |
| ---- | ---- |
| Idle | Inactive |
| Recording | Recording in progress |
| Playing | Playback in progress |
| Paused | Playback paused |
| Finished | Playback finished |

### 10.2 ReplayData File Format

The file is stored uncompressed, self-framed by the leading 4 bytes `RPLY` magic; the loader (`ReplaySystem`) requires it.

- **Format**: leading 4 bytes `RPLY` (0x52504C59) followed by the payload below

Payload:

```
┌──────────────────────────────────────┐
│ uint   MagicNumber = 0x52504C59      │  "RPLY" (uncompressed path only)
├──────────────────────────────────────┤
│ Metadata:                            │
│   int    Version (currently 1)       │
│   string SessionId (GUID)            │
│   long   RecordedAt (DateTime ticks) │
│   long   DurationMs                  │
│   int    TotalTicks                  │
│   int    PlayerCount                 │
│   int    TickIntervalMs              │
│   int    RandomSeed                  │
├──────────────────────────────────────┤
│ int TickCount                        │
│ for each tick:                       │
│   int Tick                           │
│   int CommandCount                   │
│   for each command:                  │
│     int    CommandDataLength         │
│     byte[] CommandData               │
└──────────────────────────────────────┘
```

### 10.3 Playback Features

| Feature | Method |
| ---- | ---- |
| Play / pause / resume / stop | `Play()`, `Pause()`, `Resume()`, `Stop()` |
| Seek by tick | `SeekToTick(int tick)` |
| Seek by progress | `SeekToProgress(float 0~1)` |
| Step forward/backward by frame | `StepForward()`, `StepBackward()` |
| Progress query | `Progress` (0.0 ~ 1.0) |

**Playback speed** (`ReplaySpeed` enum, value = multiplier × 100):

| Speed | Multiplier | Enum Value |
| ---- | ---- | ---- |
| Quarter | 0.25x | 25 |
| Half | 0.5x | 50 |
| Normal | 1.0x | 100 |
| Double | 2.0x | 200 |
| Quadruple | 4.0x | 400 |

### 10.4 Events

| Event | Signature |
| ---- | ---- |
| OnTickPlayed | `Action<int, IReadOnlyList<ICommand>>` |
| OnPlaybackFinished | `Action` |
| OnSeekCompleted | `Action<int>` |
| OnRecordingStarted | `Action` |
| OnRecordingStopped | `Action<IReplayData>` |

---

## 11. GC Optimization Strategies

### 11.1 Object Pooling

| Pool | Target | Used By |
| ---- | ---- | ---- |
| `DictionaryPoolHelper` | `Dictionary<int, T>` | InputBuffer's inner dictionaries |
| `ListPool` | `List<T>` | Command lists, etc. |
| `StreamPool` | `MemoryStream` | Serialization (using pattern) |

### 11.2 Cached Fields

| Location | Field | Purpose |
| ---- | ---- | ---- |
| KlothoEngine | `_tickCommandsCache` | Per-tick command collection |
| KlothoEngine | `_previousCommandsCache` | Previous-command collection for prediction |
| KlothoEngine | `_hashKeysToRemoveCache` | Hash-cleanup key collection |
| InputBuffer | `_commandListCache` | GetCommandList return value |
| InputBuffer | `_ticksToRemoveCache` | Removal keys for ClearBefore/After |
| SimpleInputPredictor | `EmptyCommand` (cached) | Empty-command reuse |

### 11.3 Coding Rules

- No LINQ — manual `for` loops
- Avoid lambda / closure captures
- ECS Filter — `ref struct`-based, no heap allocation
- `SpanWriter/SpanReader` — ref-struct serialization minimizes array allocation

---

## 12. Precision and Range Summary

| Component | Type | Precision | Range |
| ---- | ---- | ---- | ---- |
| FP64 | 32.32 fixed-point | 2^-32 ≈ 2.33 × 10^-10 | ±2,147,483,647.999 |
| Position / velocity | FPVector3 (FP64 × 3) | same | same |
| Rotation | FP64 (degrees) | same | same |
| State hash | ulong (FNV-1a) | 64-bit | 0 ~ 2^64-1 |
| Timestamp | long | 1 DateTime tick | .NET DateTime range |
| Random seed | int | 32-bit | -2^31 ~ 2^31-1 |
| Tick interval | int (ms) | 1 ms | 1 ~ ∞ |

---

*Last updated: 2026-08-23 — setup fingerprints on the ready exchange (§9.4.4), `[KlothoCleanup]` tick-end passes (§2.6 / §7.6), component signals wired (§7.4), runtime NavMesh rebake + memory / perf diagnostics in the layout and config tables*
