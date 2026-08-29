# Brawler Appendix H — BrawlerDedicatedServer (Headless Dedicated Server)

> Related: [Brawler.md](Brawler.md) §2, §11 (Klotho feature map · callbacks) · [Docs/Specification.md](../Specification.md) §9 (ServerDriven mode)
> Target: `Samples/Brawler/Server/` — a Brawler-specific server that runs as a .NET 8 console app with no Unity dependency

> ⚠️ This document describes the `Samples/Brawler/Server/` implementation as it exists in the repository. Some code-block examples have been tidied (comments / whitespace) for readability — the actual source's line layout and log strings may not match exactly.

---

## H-1. Role

- A `.NET 8` console executable that runs **only the simulation** — no Unity, MonoBehaviour, scene, or rendering
- The **authoritative server** for ServerDriven mode (`Mode = ServerDriven`) — clients send only input commands after `Connect`
- Three execution modes: single-room (`MaxRooms = 1` over the same RoomManager infrastructure) / multi-room (`--multi`, `MaxRooms = N`) / E2E test runner (`--test`)
- Uses **the same system set** (`BrawlerSimSetup.RegisterSystems`) as the Brawler client → determinism is guaranteed automatically
- Logging is `IKLogger` console + daily rolling file (`Logs/Server_*.log`)

The headless server is **optional**. If your sample only uses P2P or host mode, you can skip this document.

### Using this document as a reference template

This appendix describes `Samples/Brawler/Server/` as it exists in this repo — concrete code, paths, and CLI for the Brawler sample. **If you are building your own game's dedicated server, copy this folder as a starting point** and adapt the Brawler-specific parts:

| What's Brawler-specific | Replace with |
| ---- | ---- |
| `Samples/Brawler/Server/` folder location | Your game's server folder (e.g. `<yourGame>/Tools/MyDedicatedServer/` or anywhere with a `.csproj`) |
| `BrawlerServerCallbacks.cs` (extends `ISimulationCallbacks`) | Your `MyGameServerCallbacks.cs` — wire your game's systems via `RegisterSystems` |
| Tier 4 `Compile Include` in csproj — `Assets/Brawler/Scripts/{DataAssets,ECS}/**` | Your game's deterministic code path (e.g. `Assets/MyGame/Scripts/{DataAssets,ECS}/**`) |
| Tier 4 `Content Include` — `Assets/Brawler/Data/*.bytes` | Your game's exported `.bytes` (static colliders / navmesh / data assets) |
| `simulationconfig.json` / `sessionconfig.json` values | Tuned for your game's tick rate, latency budget, max players, etc. |
| `Program.cs` CLI flags (`--multi`, `--test`, etc.) | Your operational requirements |

Tiers 1–3 (Runtime, LiteNetLib, Transport, Logging, Server utils, Gameplay) are the **engine-agnostic Klotho assemblies under [`com.xpturn.klotho/Server~/`](../../com.xpturn.klotho/Server~/)** (mirroring the client asmdef structure) — your csproj `<ProjectReference>`s them and adds the `KlothoGenerator` analyzer for its own game types. See [Installation — Unity → Dedicated server](../Installation.Unity.md#2-dedicated-server-optional) for the install patterns (git submodule vs PackageCache) and the correct reference depth for your csproj location.

---

## H-2. File Layout

```
Samples/Brawler/Server/
├── BrawlerDedicatedServer.csproj   — net8.0 Exe; ProjectReferences the Server~ Klotho assemblies + includes Brawler game sources
├── BrawlerDedicatedServer.sln
├── Program.cs                      — CLI entry (single-room · multi-room · test branches)
├── BrawlerServerCallbacks.cs       — ISimulationCallbacks impl (no view callbacks)
├── MultiRoomTests.cs               — MockTransport-based E2E tests (Test08–Test15, 8 tests)
├── SingleRoomLifecycleTests.cs     — MockTransport-based single-room lifecycle tests (SR1–SR4, 4 tests)
├── simulationconfig.json           — Mode/TickIntervalMs/MaxRollback/Prediction/etc.
├── sessionconfig.json              — AllowLateJoin/ReconnectTimeoutMs/MinPlayers/etc.
├── faultinjectionconfig.json       — FaultInjectionLoader schema (KLOTHO_FAULT_INJECTION builds only)
├── build.sh                        — dotnet build -c Debug
├── publish.sh                      — dotnet publish -c Release -r osx-arm64 --self-contained
└── Logs/                           — created at runtime
```

### H-2-1. Why project references (mirroring the client asmdef structure)

`BrawlerDedicatedServer.csproj` `<ProjectReference>`s the engine-agnostic Klotho assemblies under `Server~/` — the same asmdef boundaries the Unity client uses — and compiles only the Brawler game sources (`Brawler.ECS` / `DataAssets` `.cs`) into the exe.

Registration works across those assembly boundaries: `CommandFactory` / `MessageSerializer` live in `xpTURN.Klotho.Runtime`, so KlothoGenerator fills their `RegisterGeneratedTypes` **partial method** for that assembly's own types, and emits `[ModuleInitializer]` + `CommandRegistry`/`MessageRegistry` registration for every other assembly (Gameplay, plus the game types compiled into this exe). `KlothoServerBootstrap.Initialize("Brawler")` in `Program.cs` force-loads the referenced assemblies and runs warmups at startup, so every registration is applied before the first room constructs a factory. (An earlier build source-linked the whole Runtime tree into one assembly to keep registration in a single compilation unit; that is no longer required.)

The referenced / compiled tiers:

| Tier | Assembly / source | Provided by |
|---|---|---|
| 1 Runtime | `xpTURN.Klotho.Runtime` (Core, ECS, Network, Serialization, Replay, Diagnostics — excludes `Unity/`, `**/Json/`) | `<ProjectReference>` |
| 1 LiteNetLib | `LiteNetLib` (vendored third-party transport lib) | `<ProjectReference>` (transitive) |
| 1 Transport | `xpTURN.Klotho.LiteNetLib` (`LiteNetLibTransport`, etc.) | `<ProjectReference>` |
| 1 Logging | `xpTURN.Klotho.Logging` | `<ProjectReference>` |
| 2 Server utils | `KlothoServer` (`SimulationConfigLoader`, `SessionConfigLoader`, `ConfigPathResolver`, `KlothoServerBootstrap`) | `<ProjectReference>` |
| 3 Gameplay | `xpTURN.Klotho.Gameplay` | `<ProjectReference>` |
| 4 Game | `Assets/Brawler/Scripts/DataAssets/**` + `.../ECS/**` | `<Compile Include>` into the exe |

> Tiers 1–3 are the per-assembly projects under [`com.xpturn.klotho/Server~/`](../../com.xpturn.klotho/Server~/); `BrawlerDedicatedServer.csproj` references them plus the `KlothoGenerator` analyzer (for the tier-4 types compiled into the exe). Tier-4 includes are specified directly in the csproj.

### H-2-2. Bundled Data Files

`Assets/Brawler/Data/*.bytes` is copied under `Data/` of the build output as `PreserveNewest`:

```xml
<Content Include="..\..\Assets\Brawler\Data\*.bytes">
  <Link>Data\%(Filename)%(Extension)</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

The three files Program.cs reads at runtime from `AppContext.BaseDirectory + "Data/"`:

| File | How to Generate | Purpose |
|---|---|---|
| `BrawlerScene.StaticColliders.bytes` | Unity Editor: `Tools > Klotho > Export Static Colliders` | `FPStaticColliderSerializer.Load` → static colliders |
| `BrawlerScene.NavMeshData.bytes` | Unity Editor: `Tools > Klotho > Export NavMesh` | `FPNavMeshSerializer.Deserialize` → bot navmesh |
| `BrawlerAssets.bytes` | Unity Editor: `Tools > Klotho > Convert > DataAsset JsonToBytes` | `DataAssetReader.LoadMixedCollectionFromBytes` → 9 DataAssets |

> The server alone cannot generate these three. Running the sample requires **exporting once from the Unity Editor** before building `Samples/Brawler/Server/`.

---

## H-3. Configuration Files

### H-3-1. simulationconfig.json

The actual file, verbatim:

```json
{
  "Mode": "ServerDriven",
  "TickIntervalMs": 25,
  "InputDelayTicks": 4,
  "MaxRollbackTicks": 50,
  "SyncCheckInterval": 20,
  "UsePrediction": false,
  "MaxEntities": 256,

  "HardToleranceMs": 0,
  "InputResendIntervalMs": 150,
  "MaxUnackedInputs": 30,
  "ServerSnapshotRetentionTicks": 0,
  "SDInputLeadTicks": 4,
  "LateJoinDelaySafety": 2,
  "RttSanityMaxMs": 240,

  "EnableErrorCorrection": true,
  "InterpolationDelayTicks": 3,

  "EventDispatchWarnMs": 5,
  "TickDriftWarnMultiplier": 3
}
```

Key choices:
- `Mode = ServerDriven` — the server collects, orders, and redistributes input
- `TickIntervalMs = 25` → 40 Hz. **The host (server) propagates this value to guests via `SimulationConfigMessage`**, so clients simulate at the same tick rate using the server's value regardless of their local `USimulationConfig` ([SimulationConfigMessage.cs](../../com.xpturn.klotho/Runtime/Network/Messages/SimulationConfigMessage.cs); for spectators, `SimulationConfig` + `SessionConfig` ride along in [SpectatorAcceptMessage.cs](../../com.xpturn.klotho/Runtime/Network/Messages/SpectatorAcceptMessage.cs) so spectators enter with the same simulation / session parameters).
- `UsePrediction = false` — the server doesn't need prediction
- `SDInputLeadTicks = 4` — server-relative lead ticks; the buffer that absorbs client input latency
- `LateJoinDelaySafety = 2`, `RttSanityMaxMs = 240` — feed the server-side `RecommendedExtraDelayCalculator` (Sync / LateJoin / Reconnect seed + mid-match push). See Specification.md §2.2.
- `InterpolationDelayTicks = 3` — SD clients render this many ticks behind the newest Verified tick. 3 is the lowest value whose frame-time budget `(delay − 2) × TickIntervalMs` is positive at this 25 ms tick; at 2 the render clock clamped on 7–9% of frames. The client asset carries the same 25 ms / 3 pair, so switching the sample to P2P renders the same way

### H-3-2. sessionconfig.json

```json
{
  "AllowLateJoin": true,
  "ReconnectTimeoutMs": 60000,
  "ReconnectMaxRetries": 3,
  "MinPlayers": 2,
  "MaxSpectators": 4
}
```

- `MinPlayers` — minimum number of players required to start the game. Range: 1 or greater, must satisfy `MinPlayers <= MaxPlayers`. Out-of-range values are clamped at load time with a warning log. SD runtime gate clamps to `MaxPlayersPerRoom`; clamp also fires once with a warning if `MinPlayers > MaxPlayersPerRoom`.
- `MaxSpectators` — maximum spectators per session. The transport listen capacity becomes `MaxPlayers + MaxSpectators` (single-room) or `maxRooms * (MaxPlayersPerRoom + MaxSpectatorsPerRoom)` (multi-room). 0 means spectators are not admitted.

### H-3-3. Path-Resolution Rules

`SimulationConfigLoader.Load(args, logger)` / `SessionConfigLoader.Load(args, logger)` look for the config file in this order:

1. `--config-dir <dir>` CLI option
2. Current working directory (CWD)
3. Build output directory (`AppContext.BaseDirectory`)

During development, placing config JSON in CWD lets you tweak values and re-run without rebuilding.

---

## H-4. ISimulationCallbacks Implementation

`BrawlerServerCallbacks.cs` in full:

```csharp
public class BrawlerServerCallbacks : ISimulationCallbacks
{
    private readonly ILogger _logger;
    private readonly List<FPStaticCollider> _staticColliders;
    private readonly FPNavMesh _navMesh;
    private readonly List<IDataAsset> _dataAssets;
    private readonly int _maxPlayers;
    private readonly int _botCount;

    public BrawlerServerCallbacks(ILogger logger,
                                    List<FPStaticCollider> staticColliders,
                                    FPNavMesh navMesh,
                                    int maxPlayers,
                                    int botCount,
                                    List<IDataAsset> dataAssets = null)
    { /* store fields */ }

    public void RegisterSystems(EcsSimulation simulation)
    {
        var query       = new FPNavMeshQuery(_navMesh, _logger);
        var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, _logger);
        var funnel      = new FPNavMeshFunnel(_navMesh, query, _logger);
        var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, _logger);
        agentSystem.SetAvoidance(new FPNavAvoidance());

        var botFSMSystem = new BotFSMSystem(agentSystem);
        botFSMSystem.SetQuery(query);

        BrawlerSimSetup.RegisterSystems(simulation, _logger, _dataAssets, _staticColliders, botFSMSystem);
    }

    public void OnInitializeWorld(IKlothoEngine engine)
        => BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        // no-op: ServerInputCollector gathers network input
    }

    // A late-joiner enters the world at its join tick — seed its entitlement loadout the same way
    // tick-0 players are seeded (OnInitializeWorld), so a restricted (e.g. "guest") late-joiner is
    // gated in-match. The server's verified entitlement is authoritative; clients receive the same
    // bytes via the late-join propagation, so every node seeds identical state at the join tick.
    public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
    {
        BrawlerSimSetup.SeedOneLoadout(ref frame, engine, playerId);
    }
}
```

Key points:
- `IViewCallbacks` is not implemented (the server has no view).
- `OnPollInput` is empty — **the server itself never produces input**. Commands sent by clients are gathered every tick by `ServerInputCollector` and injected via `ICommandSender`.
- `RegisterSystems` calls the same `BrawlerSimSetup.RegisterSystems` as the client — **for determinism**, the rule is to use the same systems, the same order, and the same `EventSystem` instance.

---

## H-5. Single-Room Mode (Phase 1)

### H-5-1. Run

```bash
# Dev build
./build.sh

# Run (port=7777, botCount=0, logLevel=Warning)
dotnet run --project BrawlerDedicatedServer.csproj -- 7777 0 Information

# With RTT metrics (match-identification telemetry)
dotnet run --project BrawlerDedicatedServer.csproj -- 7777 0 Information --rtt-metrics
```

Argument order: `<port> <botCount> [logLevel]`. Defaults when omitted: `7777 / 0 / Warning`.

Optional flag — `--rtt-metrics`: toggles `ServerNetworkService.RttMetricsEnabled`, enabling per-sample `[Metrics][RttSample]` and per-match `[Metrics][RttMatch]` JSON-line emit. Off by default (zero overhead).

### H-5-2. Bootstrap Flow — `RunSingleRoom(args, rttMetricsEnabled)`

Single-room now reuses the same `RoomManager` + `RoomRouter` infrastructure as multi-room with `MaxRooms = 1`. The room is created lazily on the first `RoomHandshakeMessage` (`RoomId=0`).

```csharp
// Excerpt from Program.cs RunSingleRoom (comments are explanatory)
const int maxRooms = 1;

var simConfig     = SimulationConfigLoader.Load(args, logger);
var sessionConfig = SessionConfigLoader.Load(args, logger);
#if KLOTHO_FAULT_INJECTION
FaultInjectionLoader.TryLoadAndApply(
    ConfigPathResolver.Resolve(FaultInjectionLoader.DefaultFileName, args), logger);
#endif
var maxPlayersPerRoom    = sessionConfig.MaxPlayers;
var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;

ServerNetworkService.RttMetricsEnabled = rttMetricsEnabled;

// 1. Pre-load data — shared across rooms
var staticColliders = FPStaticColliderSerializer.Load(staticColliderPath);
var navMeshBytes    = File.ReadAllBytes(navMeshPath);              // deserialize fresh per room
var dataAssets      = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);

IDataAssetRegistryBuilder builder = new DataAssetRegistry();
builder.RegisterRange(dataAssets);
var sharedRegistry = builder.Build();

// 2. Single Transport
var transport = new LiteNetLibTransport(logger, connectionKey: KLOTHO_CONNECTION_KEY);
transport.Listen("0.0.0.0", port, maxRooms * (maxPlayersPerRoom + maxSpectatorsPerRoom));

// 3. RoomRouter + RoomManager (MaxRooms=1, lazy CreateRoom on first RoomHandshakeMessage)
var router = new RoomRouter(transport, logger);
var roomManager = new RoomManager(transport, router, loggerFactory, new RoomManagerConfig
{
    MaxRooms             = maxRooms,
    MaxPlayersPerRoom    = maxPlayersPerRoom,
    MaxSpectatorsPerRoom = maxSpectatorsPerRoom,
    // EcsSimulation is now derived from the simulation config (maxEntities) via RoomManagerConfigBuilder.WithDerivedSimulation — no SimulationFactory.
    SimulationConfigFactory = () => simConfig,
    SessionConfigFactory    = () => sessionConfig,
    CallbacksFactory        = (roomLogger) => new BrawlerServerCallbacks(
        roomLogger,
        staticColliders,
        FPNavMeshSerializer.Deserialize(navMeshBytes),
        maxPlayersPerRoom,
        botCount),
});

// 4. Main loop (graceful shutdown included)
var loop = new ServerLoop(transport, roomManager, simConfig.TickIntervalMs, logger);
loop.Run();          // Ctrl+C (SIGINT) → graceful shutdown
```

Notes:
- The legacy direct path (`new ServerNetworkService()` + `new KlothoEngine()` constructed inline) has been removed. Per-room instances are produced by `RoomManager` via the injected factories — single-room is just the `MaxRooms = 1` case of the same path.
- The client must send `RoomHandshakeMessage` with `RoomId = 0` for single-room (vs `1..maxRooms-1` for multi-room slots).
- `#if KLOTHO_FAULT_INJECTION` block loads `faultinjectionconfig.json` from the configured config dir before `ServerNetworkService.RttMetricsEnabled` is set.

### H-5-3. The Main Loop — `ServerLoop`

Defined in `com.xpturn.klotho/Runtime/Network/ServerLoop.cs`. Per iteration:

- `transport.PollEvents()` — socket receive (single port, all rooms)
- `roomManager.Tick(dt)` — accumulate / execute ticks per room
- SIGINT (`Ctrl+C`) detection → drain in-flight ticks → graceful shutdown → `loop.Run()` returns

The previous `DedicatedServerLoop` (single-engine variant) has been retired in favor of `ServerLoop`, which works uniformly for `MaxRooms = 1` and multi-room configurations.

---

## H-6. Multi-Room Mode (Phase 2)

### H-6-1. Run

```bash
# Single port, maxRooms=4, 0 bots
dotnet run --project BrawlerDedicatedServer.csproj -- --multi 7777 4 0 Information

# With RTT metrics
dotnet run --project BrawlerDedicatedServer.csproj -- --multi 7777 4 0 Information --rtt-metrics
```

Argument order: `--multi <port> <maxRooms> <botCount> [logLevel]`. The `--rtt-metrics` flag (position-independent) is the same toggle as in single-room mode.

### H-6-2. Key Difference — `RoomManager` + `RoomRouter`

```csharp
// Excerpt from Program.cs RunMultiRoom

// 1. Pre-load data — shared across rooms (read-only)
var maxPlayersPerRoom    = sessionConfig.MaxPlayers;
var maxSpectatorsPerRoom = sessionConfig.MaxSpectators;
var staticColliders = FPStaticColliderSerializer.Load(staticColliderPath);
var navMeshBytes    = File.ReadAllBytes(navMeshPath);        // Deserialize fresh per room
var dataAssets      = DataAssetReader.LoadMixedCollectionFromBytes(assetPath);
var sharedRegistry  = new DataAssetRegistry().RegisterRange(dataAssets).Build();

// 2. Ensure ThreadPool minimum threads
ThreadPool.SetMinThreads(Math.Max(Environment.ProcessorCount, maxRooms + 2),
                         Environment.ProcessorCount);

// 3. Single Transport (one port shared by all rooms)
var transport = new LiteNetLibTransport(logger);
transport.Listen("0.0.0.0", port, maxRooms * (maxPlayersPerRoom + maxSpectatorsPerRoom));

// 4. RoomRouter + RoomManager
var router = new RoomRouter(transport, logger);
var roomManager = new RoomManager(transport, router, loggerFactory, new RoomManagerConfig
{
    MaxRooms             = maxRooms,
    MaxPlayersPerRoom    = maxPlayersPerRoom,
    MaxSpectatorsPerRoom = maxSpectatorsPerRoom,
    // EcsSimulation is now derived from the simulation config (maxEntities) via RoomManagerConfigBuilder.WithDerivedSimulation — no SimulationFactory.
    SimulationConfigFactory = () => simConfig,
    SessionConfigFactory    = () => sessionConfig,
    CallbacksFactory        = (roomLogger) => new BrawlerServerCallbacks(
        roomLogger,
        staticColliders,
        FPNavMeshSerializer.Deserialize(navMeshBytes),   // independent NavMesh instance per room
        maxPlayersPerRoom,
        botCount),
});

// 5. Loop
var loop = new ServerLoop(transport, roomManager, tickIntervalMs, logger);
loop.Run();
```

Key points:
- **One Transport, N rooms** — a single port handles every room. `RoomRouter` reads the roomId from incoming packets and routes them to the corresponding room queue inside `RoomManager`.
- **Factory injection** — `CallbacksFactory` is passed as a delegate to construct per-room `BrawlerServerCallbacks` instances; `EcsSimulation` is derived from the simulation config via `RoomManagerConfigBuilder.WithDerivedSimulation` rather than an explicit `SimulationFactory`.
- **DataAsset shared, NavMesh deserialized per room** — NavMesh has internal state that mutates during queries, so it cannot be shared. Static colliders are now selected per stage (below), not shared.
- `ThreadPool.SetMinThreads` — secures workers proportional to the room count (avoids warmup latency).

### H-6-3. Per-Room Stage & Match Config

The multi-room server assigns each room a **stage** and a **per-match config**, showing how one server hosts rooms on different maps:

- **`StaticMatchConfigSource` maps room → stage** (`.WithMatchConfigSource(...)`) — the sample alternates rooms across `Stage01` / `Stage02` (`stageId = 1 + roomId % 2`). The callbacks factory takes the *match-aware* form `(MatchConfigContext matchCtx, IKLogger) => …` and selects that stage's baked colliders + navmesh from `matchCtx.StageId`; `CreateRoomAt` stamps the stage onto the room's `SimulationConfig`, which carries it to the clients (each client builds the same stage — verified by matching `[Physics][StaticGeometry]` fingerprints). A room the source declines is refused (room-not-found).
- **Per-match bot count rides `MatchConfigData`** — the CLI `botCount` is encoded into each room's `MatchConfigData` (via `BrawlerMatchConfig`, a `[KlothoSerializableStruct]` payload) instead of injected straight into the callback, so it propagates like the stage. The context factory decodes it back with `BrawlerMatchConfig.Decode(matchCtx.MatchConfigData)`.
- **Single-room mode is unchanged** — no `IMatchConfigSource`, so it runs the default stage with the CLI bot count as before.
- **Lobby-driven** — with `--lobby`, the lobby (not the CLI) picks each room's stage and match config and reserves it on the server before the client connects; see [LobbyIntegrationGuide §4-E](../LobbyIntegrationGuide.md#4-e-per-room-match-config--reservation-sd-multi-room). Framework-level API: [GameDevAPI §3.6](../GameDevAPI.md#36-per-match-config--stageid--matchconfigdata).
- **Match results reported back** — also with `--lobby`, each room's verified result is pushed to the lobby at match end: `GameOverSystem` assembles a `BrawlerMatchResult` blob (per-player placement / stocks / knockback / acquisitions, keyed by `PlayerId`) onto `GameOverEvent` (an `IMatchResultProvider`), and the room reporter ships it — together with the verified identity roster and abort/abandoned notifications — with at-least-once delivery. Both single-room and multi-room paths emit. See [LobbyIntegrationGuide §4-F](../LobbyIntegrationGuide.md#4-f-match-result-reporting-sd-multi-room); framework-level API: [GameDevAPI §3.7](../GameDevAPI.md#37-match-result--imatchresultprovider).

---

## H-7. Test Mode (`--test`)

```bash
dotnet run --project BrawlerDedicatedServer.csproj -- --test
```

Runs every server-side suite — `MultiRoomTests`, `SingleRoomLifecycleTests`, `NormalEndLifecycleTests`, `BrawlerMatchConfigTests`, `BrawlerMatchResultTests` — verifying the server components only, with **MockTransport**, no real network or game logic. Each suite is wrapped by a `SafeRunSuite` shim that catches and reports crashes independently, so one suite's blow-up does not mask failures in the others. The exit code is the sum of failures across suites.

**MultiRoomTests** — multi-room behavior under `MaxRooms > 1`.

| # | Name | Verifies |
|---|---|---|
| #8 | TwoRoomsSimultaneous | Two rooms simultaneously: peer distribution, message-queue separation |
| #9 | RoomIsolation | A room's messages never reach another room's queue |
| #10 | RoomCreationDestruction | Active → Draining → Disposing → recreate cycle |
| #11 | TickIntervalStability | Tick stability when targeting dt 50 ms |
| #12 | FullRoomReject | Rejects connections when capacity is exceeded |
| #13 | NonExistentRoomReject | Rejects connections to a non-existent roomId |
| #14 | GracefulShutdown | Blocks new connections during shutdown; in-flight rooms run to completion |
| #15 | ThreadSafety | Concurrent `RoomManager` operations from multiple threads |

**SingleRoomLifecycleTests** — single-room (MaxRooms=1) lazy-create / drain / recreate cycle.

| # | Name | Verifies |
|---|---|---|
| SR1 | LazyCreateOnFirstHandshake | Room is created on the first `RoomHandshakeMessage`, not at server boot |
| SR2 | LobbyDrainAndRecreate | After all peers leave during Lobby → Draining → Disposing → fresh recreate on next handshake |
| SR3 | ShouldDrainTriggerCondition | Drain triggers only when the configured condition is satisfied |
| SR4 | DrainPhaseCapturedAtTransition | The phase captured at the drain-trigger boundary is the one that owns the transition |

The exit code is the sum of `failed` counts across both suites — CI can use the `exit status` directly to determine pass/fail. Each suite is independently wrapped by `SafeRunSuite` so an exception in one suite does not mask failures in the other.

---

## H-8. Build · Deploy

### Development (Debug, local run)

```bash
cd Samples/Brawler/Server
./build.sh                                 # = dotnet build -c Debug
dotnet run --project . -- 7777 0
```

### Release Deploy (self-contained, macOS arm64)

```bash
./publish.sh    # = dotnet publish -c Release -r osx-arm64 --self-contained
# Output: bin/Release/net8.0/osx-arm64/publish/
```

Changing `publish.sh` to a different RID (e.g., `linux-x64`, `win-x64`) produces a build for that platform. Because the `.csproj` sets `PublishReadyToRun=true`, R2R compilation is included.

---

## H-9. Client / Server Responsibility Split

| Concern | Brawler Client (Unity) | Brawler Server (this tool) |
|---|---|---|
| Process form | Unity Editor / build (MonoBehaviour) | .NET 8 console (`Program.Main`) |
| Simulation systems | Same `BrawlerSimSetup.RegisterSystems(...)` | Same `BrawlerSimSetup.RegisterSystems(...)` |
| Callbacks | `ISimulationCallbacks` + `IViewCallbacks` | `ISimulationCallbacks` only (`BrawlerServerCallbacks`) |
| Input | `BrawlerInputCapture` → `OnPollInput` → command send | `OnPollInput` no-op; client commands gathered by `ServerInputCollector` |
| View / UI | `EntityViewUpdater` + `CharacterView`, etc. | None |
| NavMesh / StaticCollider | Editor export loaded via Addressables / TextAsset | Editor export shipped alongside as `.bytes` |
| DataAsset loading | `USimulationConfig` · `DataAssetRegistry.Build` (Unity bootstrap) | `DataAssetReader.LoadMixedCollectionFromBytes` → `DataAssetRegistry` |
| Network | `KlothoSessionFlow` (5 entry points: `StartHost` / `JoinAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplay`; Flow internally calls `KlothoSession.Create` / `KlothoConnectionAsync`) | `RoomManager + RoomRouter` directly — single-room is `MaxRooms = 1`, multi-room is `MaxRooms = N`. Per-room `ServerNetworkService` is built by `RoomManager` via the injected factories. Server has no `KlothoSessionFlow` — Flow is a Unity client/host concern only |

The core of determinism is **"do not configure the system set differently"**. Both client and server go through `BrawlerSimSetup`, so as long as that contract is preserved, DesyncDetector and SyncTest work correctly.

---

## H-10. Caveats / Limitations

- **Initial export requires the Unity Editor** — the three `Data/*.bytes` files cannot be generated without Unity. Each map change requires re-export → rebuild `Samples/Brawler/Server/`.
- **No view callbacks** — if the server needs `OnTickExecuted` / `OnGameStart`, those are not part of `ISimulationCallbacks`, so a separate subscription is required (the current Brawler server doesn't use them).
- **Single shared port (multi-room)** — convenient from a firewall / NAT / port-scan perspective, but to isolate a particular room, running multiple single-room server processes is simpler.
- **`MaxRollbackTicks = 1`** — the server is its own authority, so the rollback buffer is minimized. Increasing this only wastes memory.
- **Default log level `Warning`** — during development, pass `Information` / `Debug` as the 3rd positional argument (`args[2]`). E.g., `dotnet run -- 7777 0 Information`. The `--rtt-metrics` flag is position-independent and parsed separately.

---

## H-11. Sample-Extension Checklist

When building **a dedicated server for your own game** modeled after Brawler:

1. Create a new `Tools/MyGameDedicatedServer/` folder; copy `BrawlerDedicatedServer.csproj`.
2. Keep the `<ProjectReference>`s to the `Server~` assemblies and the `KlothoGenerator` analyzer, plus the `KlothoServerBootstrap.Initialize("YourGamePrefix")` startup call — reuses the shared tier 1–3 assemblies.
3. Rename `<BrawlerRoot>` to `<MyGameRoot>` and replace the tier-4 include paths with your game's `DataAssets` / `ECS` folders.
4. Replace the `.bytes` paths in `<Content Include="..."/>` with your game's Data folder.
5. Author `MyGameServerCallbacks : ISimulationCallbacks` mirroring `BrawlerServerCallbacks` (call `MyGameSimSetup.RegisterSystems` from `RegisterSystems`).
6. `Program.cs` is mostly reusable — adjust only game-specific parameters (botCount, etc.).
7. Copy `sessionconfig.json` / `simulationconfig.json` and tune `Mode` · `TickIntervalMs` · `MaxEntities` to your game.
8. To activate the `dotnet run -- --test` test suites, copy both `MultiRoomTests.cs` (multi-room behavior) and `SingleRoomLifecycleTests.cs` (single-room lazy-create / drain / recreate). Each suite is independently wrapped by `SafeRunSuite` in `Program.cs`, so a crash in one does not mask failures in the other.
