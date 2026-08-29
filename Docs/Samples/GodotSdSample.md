# GodotSdSample — Standalone Server-Driven Sample on Godot

> Engine: **Godot 4.6.3 mono (.NET)** client · **.NET 8 console** dedicated server · top-down 3D
> Purpose: the Godot port of [SdSample](SdSample.md) — proves the engine-agnostic `com.xpturn.klotho` core runs unchanged under Godot in **Server-Driven** mode, with an authoritative dedicated server and thin rendering clients.
> Audience: developers building a Godot (.NET) Server-Driven game on the Klotho core, or porting an existing Unity Klotho SD game to Godot.
> Source: [`<repo>/Samples/GodotSdSample/`](../../Samples/GodotSdSample/)

> Verified headless (1 server + 2 `-- join` clients): both clients Synchronized → Ready → Countdown → Playing, `viewNodes≥1` at tick 120, `=== SD STANDALONE OK ===`, exit 0.

---

## 1. Game Overview

| Item | Description |
|---|---|
| Genre | Top-down "Sumo" (push opponents off) — identical rules to SdSample/P2pSample |
| Mode | **Server-Driven**: an authoritative dedicated server simulates; clients send input + render |
| Players | 2 clients + 1 dedicated server (server reserves id 0; players are 1-based) |
| Match | fall = −1 score, respawn at center; timer + countdown are server-authoritative |
| Win condition | Highest score at timeout. Tie → DRAW. |
| Visuals | Godot built-in primitives (BoxMesh + PlaneMesh); P1 = blue, P2 = red |
| Controls | WASD / arrows (XZ move). 3 buttons: Join / Ready / Stop (no Host) |
| Distribution | **Standalone**: one instance = one client (run the server + two clients to play) |

How to run: see [`<repo>/Samples/GodotSdSample/README.md`](../../Samples/GodotSdSample/README.md). This document explains how the port is structured.

---

## 2. Relationship to SdSample (what's shared, what's new)

The deterministic game logic is **identical** to SdSample (and to P2pSample — same sumo sim). Only the engine-facing client layer is rewritten for Godot; the dedicated server is owned in-tree.

| Layer | SdSample (Unity) | GodotSdSample |
|---|---|---|
| Sim (`PlayerComponent`/`MoveCommand`/`MovementSystem`/`ScoreSystem`/`RespawnSystem`/`GameOverEvent`/`PlayerStatsAsset`/`SdSimSetup`) | `Sim/` asmdef | **Copied verbatim** into `Sim/` |
| `SdSimulationCallbacks` (client) | `View/` | **Copied** into `Game/` |
| Dedicated server | `Server/` console project | **Owned** in the sibling `GodotSdSampleServer/` (RoomRouter + RoomManager, single room) |
| Data asset (`SdAssets.bytes`) | baked in-project | **Copied** into `Data/`, loaded via Godot `FileAccess` (client) + next to the server exe |
| Bootstrap | `SdGameController : MonoBehaviour` + `KlothoSessionDriver` | `SdGameNode : Node` + `GodotSessionDriver` |
| Join | `KlothoSessionFlow` SD-join helper | `GodotSessionFlowAsync.JoinServerDrivenAsync` (Task) |
| View pooling | `DefaultEntityViewPool` | `DefaultGodotEntityViewPool` (Prewarm `MaxPlayers`) |
| Input | `SdInputCapture` (Unity InputSystem) | `SdInputCapture` (`Input.IsPhysicalKeyPressed`) |
| View callbacks / HUD / menu | uGUI MonoBehaviours | Godot `Control` nodes |
| Entity view | `EntityView` + prefab | `EntityViewNode`(adapter) + `player.tscn` |

> **Single-source tradeoff**: SdSample normally references the core/shared sim as a single source. GodotSdSample deliberately **copies** the sim + callbacks + data + server so it is a fully self-contained, independent sample (per request). The *core* (`Runtime/**`) is still shared/unchanged — only the game-specific sim is duplicated. Namespaces are kept as `xpTURN.Samples.SdSample` so the copied callbacks and the `.bytes` asset (AssetId-based, generator-driven) load unchanged across client and server.

---

## 3. Architecture (shared adapter+core · Godot client · dedicated server)

```
com.xpturn.klotho/Godot~/                         (shared, unchanged) — client only
  ├─ xpTURN.Klotho.Runtime         core (Microsoft.NET.Sdk, source-links Runtime/**)
  └─ xpTURN.Klotho.Runtime.Godot   adapter (Godot.NET.Sdk):
       View:  EntityViewNode / EntityViewUpdaterNode / EntityViewFactory / VerifiedFrameInterpolator
              DefaultGodotEntityViewPool / GodotPlayerViewRegistry / EngineEventOneShot / ErrorVisualState
       Flow:  GodotSessionDriver / GodotSessionFlowAsync / GodotConnectionAsync
       Misc:  GodotDebugSink / GodotLogSink / GodotKlothoLogger / FP*.Godot.cs incl. FPRay3·FPPlane·FPBounds3 (+ reconnect & Resource-config helpers)

Samples/GodotSdSample/        (Godot.NET.Sdk client; ProjectReference → adapter → core)
  Sim/   (copied)             PlayerComponent / MoveCommand / Movement·Score·RespawnSystem / GameOverEvent / PlayerStatsAsset / SdSimSetup
  Game/  (copied)             SdSimulationCallbacks
  Client-new (Godot):
    ├─ SdGameNode : Node              single session + menu + GodotSessionDriver + pooled views + 3D view + logging
    ├─ SdInputCapture                 WASD/arrows → FP64 H/V
    ├─ GodotSdViewCallbacks           IViewCallbacks → HUD
    ├─ GodotSdMenu : Control          Join/Ready/Stop + IP/Port  (no Host)
    ├─ GodotSdHud : Control           state / score×2 / timer / result (reads server-authoritative frame)
    ├─ SdEntityViewFactory            player entity → player.tscn
    └─ SdPlayerView : EntityViewNode  tints mesh by PlayerId (1-based)

Samples/GodotSdSampleServer/   (Microsoft.NET.Sdk, net8.0 console — NO Godot; sibling of the client, references the core directly)
    ├─ Program.cs                     bind transport, RoomRouter + RoomManager (1 room), ServerLoop
    ├─ SdServerCallbacks.cs           RegisterSystems + OnInitializeWorld (authoritative world spawn)
    ├─ simulationconfig.json          server-authoritative sim config
    └─ sessionconfig.json             server-authoritative session config
```

The client adapter is on `Godot.NET.Sdk` (same SDK as the client game) so `GodotSharp` resolves consistently for everyone in the client chain (see §6–§7). The server is a plain `net8.0` console app — it references the **core** (`xpTURN.Klotho.Runtime`) and the **copied sim**, never the Godot adapter.

---

## 4. Deterministic side (Sim)

Identical to SdSample/P2pSample — see [P2pSample.md §4](P2pSample.md) for the component/command/event/asset/system details. Summary of what the copied code does:

- `PlayerComponent` (score + input cache) + builtin `TransformComponent` / `PhysicsBodyComponent`.
- `MoveCommand` (`IsContinuousInput`, FP64 H/V), `GameOverEvent` (Synced, `WinnerPlayerId`).
- `PlayerStatsAsset`: MoveSpeed / MatchDuration / FallThresholdY / SpawnPoint / InitialSpawnOffsetX / PlayerMass / PlayerHalfExtent.
- `MovementSystem` (`velocity.x = H`, `velocity.z = V`), `RespawnSystem`, `ScoreSystem` (`GameOverEvent` at `matchEndTick`).
- `SdSimSetup` exposes `RegisterSystems` + `InitializeWorld` so **both the server and the client** register the same systems (the client also registers them for prediction-free local replay of authoritative state).

Because this is pure core/ECS code, it compiles and runs under both Godot's .NET (client) and a plain console host (server) with **no engine reference** — the whole point of the port.

---

## 5. Client & server bootstrap

### 5-1. Client bootstrap (`SdGameNode : Node`)

A `GodotSessionDriver` child node pumps the transport every frame — even before a session attaches, so the connect handshake completes — and pumps the session each frame once attached; the `EntityViewUpdaterNode` self-drives view interpolation from its own `_Process`. The game node only wires the menu, resolves the async join, and mirrors the phase to the HUD:

```
_Ready  → WarmupRegistry.RunAll(); CreateLogger (console + rolling file)
        → LoadAssetRegistry (FileAccess on res://Data/SdAssets.bytes)
        → KlothoFlowSetupBuilder(callbacksFactory).WithLogger(...).WithTransport(...).WithAssetRegistry(...).WithGodotDefaults().Build()
              (sets AppVersion from ProjectSettings + GodotDeviceIdProvider via WithGodotDefaults)
        → SdEntityViewFactory(player.tscn); DefaultGodotEntityViewPool.Prewarm(player.tscn, MaxPlayers)
        → EntityViewUpdaterNode + GodotSessionDriver (added as children)
        → driver.BindTransport(transport); driver.PreSessionUpdate += capture input when Running
        → wire menu buttons (Join/Ready/Stop); SetupView3D()

Join  → flow.JoinServerDrivenAsync(transport, host, port, roomId:0, sessCfg)            (async Task; resolved in _Process) → OnSessionReady
OnSessionReady → view.Initialize(session.Engine, factory, pool); driver.Attach(session); enable Ready/Stop
Ready → _session.SetReady(true)
Stop  → driver.DetachAndStop(); view.Cleanup(); viewCallbacks.Cleanup()

_Process → if joinTask completed → _session = result; OnSessionReady   (faulted → log; headless → quit 1)
           if _session != null → HUD ← _session.Phase (polled)
           (transport+session pump and view interpolation run in the driver / updater node, not here)
```

`JoinServerDrivenAsync` does the SD two-step internally: a `RoomHandshakeMessage` pre-join (`ConnectAsync`) then `CreateForConnection(roomId)`. The session is taken from the join `Task.Result`; `State`/`Phase` are polled (`KlothoState.Running`, `SessionPhase`). The local `SessionConfig` only needs `MaxPlayers`/`MinPlayers` to match for the view/HUD — **the server is authoritative** for sim/session config and countdown/timing.

### 5-2. Per-frame view drive — self-driven

The adapter is a `Godot.NET.Sdk` project, so its `Node`-derived classes get source-generated lifecycle dispatch: `EntityViewUpdaterNode._Process` runs every frame and drives the per-frame interpolation itself, with `ProcessPriority = 1000` so it runs **after** the `GodotSessionDriver` (interpolation reads the frame the driver just advanced). `Engine.OnTickExecuted` (a C# event) drives reconcile/spawn. `ProcessViews()` / `Cleanup()` remain exposed for explicit/headless drive, but the game node no longer pumps them each frame.

> **Server-Driven view note**: with `UsePrediction=false` the **predicted frame holds the authoritative server state** (there is no local prediction to reconcile), and `InterpolationDelayTicks=4` smooths between received snapshots (4 because this sample ticks at 16 ms, where the frame-time budget `(delay − 2) × TickIntervalMs` needs the validated maximum). `EnableErrorCorrection=true` is set server-side, so the adapter's **`ErrorVisualState`** path is active on the client — view position/yaw blend smoothly across any rollback-induced correction instead of snapping (gated entirely off when error correction is disabled).

### 5-3. Input (`SdInputCapture`)

WASD + arrows via `Input.IsPhysicalKeyPressed`. The V axis is **flipped** (W/↑ → −V): `MovementSystem` maps `V → world +Z`, but the top-down camera's screen-up is world −Z, so without the flip W/S feel inverted. H (A/D → ∓X) is unchanged. Input is captured from the driver's `PreSessionUpdate` hook when `State==Running`, and Server-Driven sends it to the server (`SDInputLeadTicks=2`, `InputResendIntervalMs=150`).

### 5-4. Rendering & 3D view

- `SdEntityViewFactory` returns `player.tscn` (root = `SdPlayerView : EntityViewNode`, child `MeshInstance3D` with a `BoxMesh`) for any entity with `PlayerComponent`.
- `SdPlayerView.OnActivate` reads `PlayerComponent.PlayerId` and sets a `MaterialOverride` — P1 (id 1) blue, P2 (id 2) red. Server-Driven assigns 1-based ids (the server reserves id 0).
- `SetupView3D()` (in code, via `LookAt` to avoid hand-written basis matrices): top-down `Camera3D` over the origin, a background+ambient `Environment`, an angled `DirectionalLight3D`, plus a `PlaneMesh` ground aligned to the physics collider's top face (y=0).

### 5-5. HUD / menu (`GodotSdHud` / `GodotSdMenu`, `Control`)

Menu = 3 `Button`s (Join/Ready/Stop — **no Host**) + 2 `LineEdit` (IP/Port) wired to C# events. HUD = state / P1·P2 score / timer / result panel (WIN/LOSE/DRAW on `GameOverEvent`). The HUD reads the **server-authoritative** state from `engine.PredictedFrame` (`Filter<PlayerComponent>`, polled per `OnTickExecuted`); players are 1-based. Because the client project **is** `Godot.NET.Sdk`, its node callbacks/signals work normally.

### 5-6. Logging

`SdGameNode` logs through `IKLogger` to **both** the Godot console (`GodotLogSink`) and a rolling file under `user://logs`, via `GodotKlothoLogger.CreateDefault(filePrefix: "Sd", categoryName: "Sd")`. The default directory resolves to `ProjectSettings.GlobalizePath("user://logs")` — an absolute path writable in both the editor and exported apps.

### 5-7. Dedicated server (`GodotSdSampleServer/`)

A plain `net8.0` console app (no Godot, references the core + copied sim directly):

```
KlothoServerBootstrap.Initialize("SdSample", "xpTURN.Samples")   // force-load split assemblies + JIT warmups
CLI: dotnet run -- [port] [logLevel]                              // default 7777 / Information
load simulationconfig.json + sessionconfig.json (server-authoritative)
load Data/SdAssets.bytes (copied next to the exe), build the shared DataAssetRegistry
transport.Listen("0.0.0.0", port, maxRooms*maxPlayers)
RoomRouter (consumes RoomHandshakeMessage, routes peers) + RoomManager (1 room; wires
   EcsSimulation / ServerNetworkService / KlothoEngine / CommandFactory via SdServerCallbacks)
ServerLoop(transport, roomManager, tickIntervalMs).Run()
```

`SdServerCallbacks.OnInitializeWorld` performs the **authoritative** world spawn (`SdSimSetup.InitializeWorld`, `MaxPlayers` players ± `InitialSpawnOffsetX` + the ground collider); `OnPollInput` is a no-op (the server injects client input messages, it produces none locally). The server runs a single room (`maxRooms=1`), so the standard routing path is exercised without multi-room.

### 5-8. Server project file (`GodotSdSampleServer/SdSampleServer.csproj`)

> The server is a plain `net8.0` console `.csproj` on **`Microsoft.NET.Sdk`** — **not** `Godot.NET.Sdk`, and it uses the **core** (`xpTURN.Klotho.Runtime`), never the Godot adapter. The client adapter (GP1) is the only thing that needs `Godot.NET.Sdk`; the server has no Godot dependency at all. It is a **sibling** project (`Samples/GodotSdSampleServer/`), kept outside the client's project tree so the `Godot.NET.Sdk` glob never pulls in `Program.cs`; its paths reach back into the client folder for the shared addon, sim, and data.

The server consumes the **same `addons/klotho/` distribution as the client**, via its dedicated **`Klotho.Server.props`** — a single `<Import>` replaces any explicit framework references. The csproj then only adds the shared sim + data:

```xml
<Project Sdk="Microsoft.NET.Sdk">           <!-- core console host — NOT Godot.NET.Sdk -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>          <!-- ECS uses unsafe / fixed buffers -->
    <Nullable>disable</Nullable>
    <ServerGarbageCollection>true</ServerGarbageCollection> <!-- throughput GC for the tick loop -->
  </PropertyGroup>

  <!-- (1) Framework — one import. Klotho.Server.props references the prebuilt
       xpTURN.Klotho.Runtime.dll + KlothoServer.dll, adds the generator analyzer, declares the
       three NuGet deps, and Removes the bundled Godot adapter sources (a server has no GodotSharp). -->
  <Import Project="../GodotSdSample/addons/klotho/Klotho.Server.props" />

  <!-- (2) Shared deterministic sim — the SAME .cs the Godot client compiles, built into this exe.
       Reaches back into the sibling client folder (GodotSdSample/Sim). -->
  <PropertyGroup>
    <SdSimRoot>../GodotSdSample/Sim</SdSimRoot>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(SdSimRoot)\**\*.cs" Link="Game\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- (3) Runtime data next to the exe — the .bytes owned by GodotSdSample/Data + config JSON.
       The server reads these from its output dir (the client reads its copy via Godot FileAccess). -->
  <ItemGroup>
    <Content Include="..\GodotSdSample\Data\*.bytes">
      <Link>Data\%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include="simulationconfig.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
    <Content Include="sessionconfig.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
  </ItemGroup>
</Project>
```

Key points:

- **One import, addon DLLs.** `Klotho.Server.props` (shipped in `addons/klotho/`, beside the client's `Klotho.props`) brings the prebuilt **core + `KlothoServer.dll`** + analyzer + NuGet deps and excludes the adapter sources. The Server-Driven framework (`RoomRouter` / `RoomManager` / `ServerLoop` / `ServerNetworkService`) is in the core DLL; `KlothoServer.dll` adds the bootstrap + config-loader helpers. (This differs from the Unity server, which `<ProjectReference>`s the `Server~` projects — the Godot path consumes the packaged addon instead. See [SdSample.md §3.1](SdSample.md#31-server-project-file-serversdsampleservercsproj) for the Unity form.)
- **Generator still required.** `Klotho.Server.props` declares the `KlothoGenerator` analyzer so cross-assembly `[ModuleInitializer]` registration is emitted for your sim types; `KlothoServerBootstrap.Initialize("SdSample", "xpTURN.Samples")` force-loads everything at startup before the first room.
- **Same `.bytes` on both sides.** `..\GodotSdSample\Data\*.bytes` is the copy the client also loads (the client via `Godot.FileAccess`, the server from its output dir) — identical AssetId-based asset, namespace kept `xpTURN.Samples.SdSample`.
- **Build / run.** `dotnet build GodotSdSampleServer/SdSampleServer.csproj` then `dotnet run --project GodotSdSampleServer -- 7777`. The server build is independent of the Godot client build (§7).

---

## 6. Server-Driven specifics & common pitfalls

SdSample's framework notes and P2pSample's Godot-port pitfalls (G1–G7 / GP1–GP7 in [P2pSample.md §6](P2pSample.md)) still apply. SD- and Godot-port-specific points:

| # | Point | Symptom | Fix |
|---|---|---|---|
| GS1 | Server is authoritative for config | Client `SessionConfig` values ignored except `MaxPlayers`/`MinPlayers` for local view | Author the real config in `GodotSdSampleServer/simulationconfig.json` + `sessionconfig.json`; the client seed only mirrors player counts |
| GS2 | `MinPlayers=2` with one client | Joins, but the match never starts | Run **two** clients and Ready both — the server starts the countdown only at `MinPlayers` |
| GS3 | Server can't read its data asset | `SdAssets.bytes` not next to the server exe | The server reads `Data/SdAssets.bytes` from its output dir; the `.csproj` copies it on build |
| GS4 | `UsePrediction=false` + `EnableErrorCorrection=true` | n/a — desync correction is visible-smoothed | The predicted frame carries authoritative state; `ErrorVisualState` (adapter) blends corrections — active on this sample |
| GP1 | Adapter on plain `Microsoft.NET.Sdk` + `GodotSharp` PackageReference | `CS0400 'Godot' not found` | Client adapter on `Godot.NET.Sdk` (+ `Compile Include="Adapters/**"`) |
| GP2 | Godot needs a classic `.sln`; `dotnet 10` makes `.slnx` | Export: "EditorPlugin build callback failed → Aborting" | Commit a hand-written `<assembly_name>.sln` |
| GP3 | `System.IO.File` can't read `res://` inside an exported `.pck` | `.app` quits: `SdAssets.bytes not found` | Client reads with `Godot.FileAccess.GetFileAsBytes("res://…")`; `include_filter="*.bytes"` packs it |
| GP4 | Assuming the adapter's `Node` subclasses need manual pumping | Redundantly calling `ProcessViews()` from the game node | Because the adapter is `Godot.NET.Sdk` (GP1), `EntityViewUpdaterNode._Process` self-drives interpolation; `ProcessViews()`/`Cleanup()` remain only for explicit/headless drive |
| GP5 | macOS export-time signing blocks local runs | `.app` won't launch without manual `codesign` | `export_presets.cfg`: `codesign/codesign=0` + `disable_library_validation=true` |
| GP6 | Top-down camera screen-up (−Z) vs `MovementSystem` V→+Z | W/S inverted on screen | Flip V in `SdInputCapture` (W/↑ → −V) |

---

## 7. Build / Export notes

- **Client**: build via Godot (`Project > Tools > C#: Build`) or `dotnet build GodotSdSample.sln` (Debug or `ExportDebug`). The adapter (`Godot.NET.Sdk`) + core (`ExportDebug`/`ExportRelease` configs) + per-sample `.sln` together make `dotnet build` (incl. Godot's csproj-direct build) and `godot --build-solutions` succeed with 0 errors.
- **Server**: `dotnet build GodotSdSampleServer/SdSampleServer.csproj` or `dotnet run --project GodotSdSampleServer -- 7777`. It is a normal `net8.0` console app; `SdAssets.bytes` is copied next to the output exe by the `.csproj`.
- macOS client export-time signing is disabled (`codesign/codesign=0`) for local runs; for distribution use a Developer ID identity + notarization.

---

## 8. Where to read next

- **SdSample** — [`SdSample.md`](SdSample.md) for the Server-Driven design this port shares (server room model, SD config, gotchas).
- **GodotP2pSample** — [`GodotP2pSample.md`](GodotP2pSample.md) for the P2P Godot sample (shared adapter/view pattern, no dedicated server).
- **P2pSample** — [`P2pSample.md`](P2pSample.md) for the deterministic sumo design both ports share (components/systems/asset, pitfalls G1–G7).
