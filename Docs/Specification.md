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

The Klotho engine layer is designed to be **fully independent of the Unity engine**.
Direct dependencies on the Unity API (`MonoBehaviour`, `UnityEngine.*`) are excluded, so that the same simulation core can be **executed unchanged on the server side (.NET console / ASP.NET)**.

| Use Case | Description |
| ---- | ---- |
| Authoritative Server | Run the same simulation on the server for cheat prevention and result verification |
| Replay Verification Server | Re-execute replay data on the server to verify result integrity |
| Headless Testing | Run simulation tests in CI/CD pipelines without the Unity Editor |
| Matchmaking Simulation | Drive AI matches or perform balance tests on the server |

**Implementation Principles**:

- The engine core (`KlothoEngine`, `ISimulation`, `InputBuffer`, `FP64`, `Frame`, etc.) is pure C# — references to the `UnityEngine` namespace are forbidden
- Unity integration (rendering, input collection, MonoBehaviour lifecycle) is handled in a separate **adapter/bridge layer**
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
│  Prediction · Rollback · Snapshots · Det. │     Unity-independent, server-shared
└───────────────────────────────────────────┘
```

Simulation transport (UDP) and game services (RPC) are separated so each layer uses the optimal protocol and delivery method.
The Klotho engine layer is pure C#, so the same binary can be shared by client and server.

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
    ├──── (ECS) Frame ──── EntityManager + ComponentStorage[]
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
Assets/Klotho/
├── Runtime/
│   ├── Core/           KlothoEngine, KlothoSession, KlothoSessionSetup,
│   │                   IKlothoEngine, IKlothoSession,
│   │                   ISimulationCallbacks, IViewCallbacks,
│   │                   ISimulationConfig, ISessionConfig, SimulationConfig, SessionConfig,
│   │                   NetworkMode, ICommand, CommandFactory, CommandRegistry,
│   │                   ICommandSender, WarmupRegistry, DedicatedServerLoop,
│   │                   Pool(CommandPool, EventPool, ListPool, DictionaryPool, StreamPool),
│   │                   SimulationEvent, EventBuffer, EventCollector, EventDispatcher
│   ├── Input/          IInputBuffer, IInputPredictor, IInputHandler, InputBuffer
│   ├── Network/        IKlothoNetworkService, IServerDrivenNetworkService, INetworkTransport,
│   │                   KlothoNetworkService, ServerDrivenClientService, ServerNetworkService,
│   │                   ISpectatorService, SpectatorService,
│   │                   Room, RoomManager, RoomRouter, RoomScopedTransport, ServerLoop,
│   │                   ServerInputCollector, NetworkMessages, SharedTimeClock
│   ├── State/          IStateSnapshot, IStateSnapshotManager, RingSnapshotManager
│   ├── Serialization/  SpanWriter, SpanReader, SerializationBuffer, ISpanSerializable
│   ├── Replay/         IReplaySystem, ReplayRecorder, ReplayPlayer, ReplayData
│   │                   (LZ4 compression — K4os.Compression.LZ4)
│   ├── ECS/            Frame, EntityManager, ComponentStorage, ComponentStorageRegistry,
│   │                   EntityPrototypeRegistry, IEntityPrototype, SystemRunner,
│   │                   FrameRingBuffer, EcsStateSnapshot, EcsSimulation, FixedString32/64,
│   │                   ISystem/IInitSystem/ICommandSystem/ISyncEventSystem/ISignal family
│   │                   DataAsset/ (IDataAsset, DataAssetRegistry, DataAssetRef,
│   │                               DataAssetReader/Writer, [KlothoDataAsset(typeId)],
│   │                               Json/ — xpTURN.Klotho.DataAsset.Json assembly)
│   └── Deterministic/  FP64, FPVector2/3/4, FPQuaternion, FPMatrix, FPPhysicsWorld,
│                       FPStaticCollider, FPStaticBVH, FPStaticColliderSerializer,
│                       FPNavMesh, FPNavMeshSerializer, NavAgentComponent, FPNavAgentSystem,
│                       DeterministicRandom, FPAnimationCurve, etc.
├── Unity/          UKlothoBehaviour, USimulationConfig, EcsDebugBridge,
│                   View/ (EntityView, EntityViewComponent, EntityViewFactory,
│                          EntityViewUpdater, IEntityViewPool, DefaultEntityViewPool,
│                          BindBehaviour, ViewFlags, VerifiedFrameInterpolator,
│                          UpdatePositionParameter, ErrorVisualState, BindBehaviour.cs)
│                   FPStaticColliderOverride, FPStaticColliderVisualizer
├── Editor/         NavMesh/ (FPNavMeshExporter, Visualizer Window/Overlay/Simulator/Interaction)
│                   Physics/ (FPStaticColliderExporterWindow, FPStaticColliderConverter)
│                   ECS/ (EntityComponentVisualizerWindow, FrameHeapBenchmarkWindow)
│                   FSM/ (HFSMVisualizerWindow)
│                   DataAsset/ (JsonToBytesConverter)
├── Gameplay/       built-in component / system / command / event reference implementations
├── LiteNetLib/     LiteNetLibTransport — INetworkTransport implementation (xpTURN.Klotho.LiteNetLib)
├── Samples/        Brawler (fighting-game sample)
└── Tests/          unit tests
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
| Running | Simulation in progress |
| Paused | Paused (waiting for input or manual) |
| Finished | Game over or manually stopped |

### 2.2 Default Configuration Values

Configuration is split into two layers.
- **`SimulationConfig` / `ISimulationConfig`** — Simulation parameters (affect determinism, identical across all peers). Injected via `KlothoSessionSetup.SimulationConfig`.
- **`SessionConfig` / `ISessionConfig`** — Session operation parameters (decided by the host, propagated via GameStart / LateJoinAccept / SpectatorAccept messages).

#### SimulationConfig Defaults

| Field | Default | Unit | Description |
| ---- | ---- | ---- | ---- |
| TickIntervalMs | 25 | ms | Tick interval (= 40 ticks/sec). Range: 1 or greater (typically 16~50ms) |
| InputDelayTicks | 4 | ticks | Local-input delay shift. Effective input delay = TickIntervalMs × InputDelayTicks (= 100 ms). Range: 0 or greater (typically 2~6) |
| MaxRollbackTicks | 50 | ticks | Maximum rollback range. Determines snapshot ring buffer + input-buffer retention. Must be ≥ SyncCheckInterval |
| SyncCheckInterval | 30 | ticks | State-hash verification period. Must be ≤ MaxRollbackTicks |
| UsePrediction | true | bool | Whether input prediction is enabled. False → engine waits for all inputs (Paused) |
| MaxEntities | 256 | entities | ECS entity capacity (EntityManager array size) |
| Mode | P2P | NetworkMode | Network topology (P2P / ServerDriven). Discriminator for SD-only fields |
| HardToleranceMs | 0 | ms | (SD) Server cmd-acceptance wall-clock deadline. **0 = auto** ((effectiveLeadTicks + InputDelayTicks + 1) × TickIntervalMs + RTT/2 + 20ms jitter); manual values for advanced tuning |
| InputResendIntervalMs | 25 | ms | (SD) Interval at which the client resends unacknowledged inputs |
| MaxUnackedInputs | 30 | count | (SD) Cap on accumulated unacknowledged inputs (warning emitted on overflow) |
| ServerSnapshotRetentionTicks | 0 | ticks | (SD) Server snapshot ring-buffer slots. **0 = auto** (TickRate × 10). Independent of MaxRollbackTicks — used for FullStateRequest replies |
| SDInputLeadTicks | 0 | ticks | (SD) Initial client lead ticks at game start. **0 = auto (default 10)**. Reused on LateJoin/Reconnect. Additive with InputDelayTicks; reflected in HardToleranceMs auto-calc |
| EnableErrorCorrection | false | bool | Enable Error Correction (default off). Enable selectively in high-latency / multiplayer scenarios |
| InterpolationDelayTicks | 3 | ticks | View-layer snapshot interpolation delay (used by RenderClock.VerifiedBaseTick = LastVerifiedTick - InterpolationDelayTicks). Recommended [1, 3]. SD: upper bound for AdaptiveRenderClock |
| LateJoinDelaySafety | 2 | ticks | Safety margin added to RTT-based extra-delay computation on Sync / LateJoin / Reconnect. Also used as the standalone fallback when avgRtt is invalid / out of the sane range |
| RttSanityMaxMs | 240 | ms | Upper bound for accepting avgRtt as a sane measurement. Samples exceeding this fall back to `LateJoinDelaySafety` only |
| QuorumMissDropTicks | 20 | ticks | (P2P) Quorum-miss watchdog threshold. If a remote peer's input is missing at `_lastVerifiedTick + 1` for at least this many ticks, the peer is presumed-dropped and reactive empty-fill activates before the transport-level DisconnectTimeout. 0 disables. Safe range 10~80 |
| EventDispatchWarnMs | 5 | ms | Warning threshold for OnEvent* handler execution time (DEVELOPMENT_BUILD / UNITY_EDITOR only). 0 or less = disabled |
| TickDriftWarnMultiplier | 2 | × | Tick-loop drift warning multiplier (warns if actual interval > TickIntervalMs × multiplier). 0 or less = disabled |

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
| CountdownDurationMs | 3000 | ms | Game-start countdown length |
| CatchupMaxTicksPerFrame | 200 | ticks | Max ticks per frame during catchup |
| Old-data cleanup threshold | CurrentTick - MaxRollbackTicks - 10 | ticks | Threshold for discarding old data |

### 2.3 Events

| Event | Signature | Fired |
| ---- | ---- | ---- |
| OnTickExecuted | `Action<int>` | After every tick is executed (passes tick number) |
| OnTickExecutedWithState | `Action<int, FrameState>` | After every tick (tick, Predicted/Verified) |
| OnFrameVerified | `Action<int>` | On Predicted → Verified transition |
| OnChainAdvanceBreak | `Action` | Verified-chain advance failed at the next tick (P2P: pending input for an active player). Drives reactive empty-fill / dynamic-delay escalation |
| OnDesyncDetected | `Action<long, long>` | State-hash mismatch detected (localHash, remoteHash). The network-service variant (§9.5) is the extended `Action<int,int,long,long>` |
| OnRollbackExecuted | `Action<int, int>` | Rollback completed (fromTick, toTick) |
| OnRollbackFailed | `Action<int, string>` | Rollback failed (requestedTick, reason) |
| OnCommandRejected | `Action<int, int, RejectionReason>` | (SD client) Server rejected a client input (tick, cmdTypeId, reason) |
| OnExtraDelayChanged | `Action<int>` | Recommended extra InputDelay (ticks) changed. Fired on `ApplyExtraDelay` (Sync / LateJoin / Reconnect / DynamicPush) and `EscalateExtraDelay` (reactive escalation) |
| OnEventPredicted | `Action<int, SimulationEvent>` | Event raised on a Predicted tick |
| OnEventConfirmed | `Action<int, SimulationEvent>` | First firing of a Regular event that was confirmed without prediction (verified-direct, replay, new on rollback) |
| OnEventCanceled | `Action<int, SimulationEvent>` | Predicted event canceled by rollback |
| OnSyncedEvent | `Action<int, SimulationEvent>` | Synced event — fired only on verified ticks |
| OnResyncCompleted | `Action<int>` | Full state resync completed (restoredTick) |
| OnResyncFailed | `Action` | Failed after exceeding max resync retries |
| OnDisconnectedInputNeeded | `Action<int>` | Empty-input request for a disconnected player (playerId) |
| OnCatchupComplete | `Action` | Late-join catchup completed |
| OnVerifiedInputBatchReady | `Action<int, int, byte[], int>` | Verified input batch ready for spectators (startTick, tickCount, data, length) |

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
  1. SystemRunner.RunCommandSystems(frame, cmd) — PreUpdate: apply commands
  2. SystemRunner.RunUpdateSystems(frame)       — Update → PostUpdate → LateUpdate
  3. frame.Tick++
```

### 2.7 KlothoSession & Callback Interfaces

The entry point connecting game code to the engine. Callbacks are split into deterministic-common and view-only.

```
ISimulationCallbacks (game-implemented — common to deterministic side)
  RegisterSystems(EcsSimulation)          ← register systems (before Initialize)
  OnInitializeWorld(IKlothoEngine)        ← create initial entities (before SaveSnapshot(0))
  OnPollInput(playerId, tick, sender)     ← per-tick input polling

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
  Update(float dt)   ← called from UKlothoBehaviour.Update
  Stop()

KlothoSessionSetup (Create input)
  Logger · SimulationCallbacks · ViewCallbacks
  Transport (host) / Connection (guest)
  SimulationConfig · AssetRegistry
  RandomSeed · MaxPlayers · AllowLateJoin · Reconnect/LateJoin/Resync/Countdown parameters
```

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
  ComponentStorage<T>[] (source-generated, single byte[] heap layout)

  // entity lifecycle
  EntityRef  CreateEntity()
  EntityRef  CreateEntity(int prototypeId)  // delegates to Prototypes.Create
  void       DestroyEntity(EntityRef)

  // component access
  ref T          Get<T>(EntityRef)
  ref readonly T GetReadOnly<T>(EntityRef)
  bool           Has<T>(EntityRef)
  void           Add<T>(EntityRef, T)
  void           Remove<T>(EntityRef)

  // queries (ref struct, GC 0)
  Filter<T1..T5>             Filter<T1..T5>()
  FilterWithout<T1..T5, TEx> FilterWithout<T1..T5, TEx>()

  // hash / snapshot
  ulong  CalculateHash()    // FNV-1a (Tick + EntityCount + ComponentStorages)
  void   CopyFrom(Frame)    // restore the entire heap with a single Buffer.BlockCopy
```

### 7.2 EntityManager

- Generational index + free-list slot reuse
- Fixed capacity (specified at creation), runtime GC 0
- `IsAlive(EntityRef)` — verifies Index + Version together to prevent dangling references

### 7.3 ComponentStorage\<T\>

- Sparse-set implementation: `_sparse[entityIndex] → denseIndex`, `_dense[denseIndex] → entityIndex`
- `unmanaged` constraint — value types only
- `Add/Remove/Has` O(1); `Remove` uses swap-with-last to keep the dense array contiguous
- `DenseSpan` / `DenseToSparse` — ReadOnlySpan-based iteration, GC 0

### 7.4 System Interfaces

| Interface | Method | Description |
| ---- | ---- | ---- |
| `ISystem` | `Update(ref Frame)` | Per-tick update |
| `IInitSystem` | `OnInit(ref Frame)` | Initialization |
| `IDestroySystem` | `OnDestroy(ref Frame)` | Teardown |
| `ICommandSystem` | `OnCommand(ref Frame, ICommand)` | Command handling |
| `ISyncEventSystem` | `EmitSyncEvents(ref Frame)` | Emit sync events when a verified tick is finalized |
| `IEntityCreatedSystem` | `OnEntityCreated(ref Frame, EntityRef)` | Entity-created callback |
| `IEntityDestroyedSystem` | `OnEntityDestroyed(ref Frame, EntityRef)` | Entity-destroyed callback |
| `ISignalOnComponentAdded<T>` | `OnAdded(ref Frame, EntityRef, ref T)` | Component-added signal |
| `ISignalOnComponentRemoved<T>` | `OnRemoved(ref Frame, EntityRef, T)` | Component-removed signal |

**SystemPhase**: `PreUpdate → Update → PostUpdate → LateUpdate` (phase specified at AddSystem; auto-sorted)

### 7.5 EcsSimulation

`ISimulation` implementation.

```csharp
// constructor
new EcsSimulation(
    int maxEntities,
    int maxRollbackTicks = 10,
    int deltaTimeMs = 50,
    ILogger logger = null,
    IDataAssetRegistryBuilder registryBuilder = null,
    IDataAssetRegistry assetRegistry = null);

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
```

When attached to the engine:

```csharp
KlothoEngine.Initialize(
    ISimulation simulation,
    IKlothoNetworkService networkService,
    ILogger logger,
    ISimulationCallbacks simulationCallbacks,
    IViewCallbacks viewCallbacks = null);
```

### 7.6 [KlothoComponent(typeId)] Attribute

```csharp
[KlothoComponent(100)]
public struct PlayerComponent : IComponent { ... }
```

- `typeId` 1–99: reserved for the framework; 100+: for game developers
- The source generator emits `Frame.Components.g.cs` (InitComponentStorages, CopyComponentStorages, etc.)

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
  EntityPrototypeRegistry Prototypes   // registry (not part of CopyFrom — rollback-safe)
  EntityRef CreateEntity(int prototypeId)  // overload: delegates to Prototypes.Create
```

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

**Distribution methods**:

| Method | Returns | Algorithm |
| ---- | ---- | ---- |
| `NextInt()` | 0 ~ 2^31-1 | `NextUInt64() & 0x7FFFFFFF` |
| `NextInt(min, max)` | [min, max) | Modular arithmetic |
| `NextFP64()` | [0, 1) FP64 | `NextUInt64() & 0xFFFFFFFF` → fixed-point |
| `NextBool()` | true/false | LSB check |
| `NextChance(percent)` | bool | Percentage chance |
| `NextWeighted(weights)` | int (index) | Weighted selection |
| `NextInsideUnitCircleFP()` | FPVector2 | [-1,1]² rejection sampling |
| `NextInsideUnitSphereFP()` | FPVector3 | [-1,1]³ rejection sampling |
| `NextDirection2DFP()` | FPVector2 | Uniform angle: `NextFP64() * TwoPi` |
| `NextDirection3DFP()` | FPVector3 | Uniform spherical distribution (θ, z parameters) |
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
| PlayerReady | 12 | Player ready state |
| GameStart | 13 | Game start + config delivery |
| PlayerJoin | 14 | Player-join notification |
| JoinReject | 15 | Room-join rejection |
| **Command** | | |
| Command | 20 | Player input command |
| CommandAck | 21 | Command receipt ack |
| CommandRequest | 22 | Re-request a missing command |
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
| **Server-Driven Mode** | | |
| ClientInput | 80 | Client → server input |
| VerifiedState | 81 | Server → client verified state |
| InputAck | 82 | Server's input-receipt ack |
| ClientInputBundle | 83 | Bundled input transmission |
| PlayerBootstrapReady | 84 | Client → server: bootstrap completed (player ready) — bootstrap handshake |
| BootstrapBegin | 85 | Server → client: open bootstrap window (FirstTick, TickStartTimeMs) |
| CommandRejected | 86 | Server → client unicast on input rejection (tick, cmdTypeId, RejectionReason). Surfaces as engine `OnCommandRejected` (§2.3) on the originating client |
| **Config Layer** | | |
| SimulationConfig | 90 | Simulation-parameter payload |
| PlayerConfig | 91 | Per-player config payload |
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
  [int  MinPlayers]
  [bool AllowLateJoin]
  [int  ReconnectTimeoutMs]
  [int  ReconnectMaxRetries]
  [int  LateJoinDelayTicks]
  [int  ResyncMaxRetries]
  [int  DesyncThresholdForResync]
  [int  CountdownDurationMs]
  [int  CatchupMaxTicksPerFrame]
  [int  PlayerIdCount] [int[] PlayerIds]
```

**Ping/Pong** (ID=40/41):

```text
[byte 40|41] [long Timestamp] [int Sequence]
```

### 9.4 Desync Detection

```text
Every SyncCheckInterval (30 ticks):
├─ compute local state hash (ISimulation.GetStateHash → FNV-1a 64-bit)
├─ _localHashes[tick] = hash
├─ broadcast SyncHashMessage (Unreliable)
├─ on remote-hash receipt, compare
└─ on mismatch → raise OnDesyncDetected(localHash, remoteHash)
```

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
| OnFullStateReceived | `Action<int, byte[], long>` | Full state received (tick, data, hash) |
| OnPlayerDisconnected | `Action<IPlayerInfo>` | Player disconnected (awaiting reconnect) |
| OnPlayerReconnected | `Action<IPlayerInfo>` | Player reconnected (Host) |
| OnReconnecting | `Action` | Reconnect in progress (Guest) |
| OnReconnectFailed | `Action<string>` | Reconnect failed (Guest) |
| OnReconnected | `Action` | Reconnect completed (Guest) |
| OnLateJoinPlayerAdded | `Action<int, int>` | Late-join player added (playerId, joinTick) |

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

A file may be stored in either of two formats. The loader (`ReplaySystem`) distinguishes uncompressed/compressed by the leading 4 bytes `RPLY` magic.

- **Uncompressed**: leading 4 bytes `RPLY` (0x52504C59) followed by the payload below
- **LZ4-compressed**: the same payload wrapped by `K4os.Compression.LZ4.LZ4Pickler` (no RPLY magic — Pickler header instead)

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

*Last updated: 2026-04-27*
