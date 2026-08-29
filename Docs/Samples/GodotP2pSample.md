# GodotP2pSample — Standalone P2P Sample on Godot

> Engine: **Godot 4.6.3 mono (.NET)** · top-down 3D
> Purpose: the Godot port of [P2pSample](P2pSample.md) — proves the engine-agnostic `com.xpturn.klotho` core runs unchanged under Godot, and shows the Godot adapter/view pattern for a standalone, playable P2P game.
> Audience: developers building a Godot (.NET) game on the Klotho core, or porting an existing Unity Klotho game to Godot.
> Source: [`<repo>/Samples/GodotP2pSample/`](../../Samples/GodotP2pSample/)

> Verified headless (2-process host+join): both peers Synchronized → Ready → Countdown → Playing, `viewNodes=2`, `viewPos == simPos`.

---

## 1. Game Overview

| Item | Description |
|---|---|
| Genre | Top-down "Sumo" (push opponents off) — identical rules to P2pSample |
| Players | 2 (P2P only, host + 1 guest) |
| Match | 60-second timer, fall = −1 score, respawn at center |
| Win condition | Highest score at timeout. Tie → DRAW. |
| Visuals | Godot built-in primitives (BoxMesh + PlaneMesh); P1 = blue, P2 = red |
| Controls | WASD / arrows (XZ move). 4 buttons: Host / Join / Ready / Stop |
| Distribution | **Standalone**: one instance = one peer (run two to play) |

How to run: see [`<repo>/Samples/GodotP2pSample/README.md`](../../Samples/GodotP2pSample/README.md). This document explains how the port is structured.

---

## 2. Relationship to P2pSample (what's shared, what's new)

The deterministic game logic is **the same** as P2pSample — same components, commands, events, systems, data asset, and rules — apart from one deliberate addition, the error-correction marker noted in the table below. Only the engine-facing layer is rewritten for Godot.

| Layer | P2pSample (Unity) | GodotP2pSample |
|---|---|---|
| Sim (`PlayerComponent`/`MoveCommand`/`MovementSystem`/`ScoreSystem`/`RespawnSystem`/`GameOverEvent`/`PlayerStatsAsset`) | `Sim/` asmdef | **Copied verbatim** into `Sim/` |
| `P2pSimulationCallbacks` | `View/` | Copied into `Game/` (its only non-core dependency is the input capture), with one addition — it adds `ErrorCorrectionTargetComponent` to each spawned player, because this sample runs with error correction on |
| Data asset (`P2pAssets.bytes`) | baked in-project | **Copied** into `Data/`, loaded via Godot `FileAccess` |
| Bootstrap | `P2pGameController : MonoBehaviour` + `KlothoSessionDriver` | `P2pGameNode : Node` + `GodotSessionDriver` (transport+session pump) |
| View pooling | `DefaultEntityViewPool` | `DefaultGodotEntityViewPool` (Prewarm `MaxPlayers`) |
| Join | `KlothoSessionFlow` connect helpers | `GodotSessionFlowAsync.JoinP2PAsync` (Task) |
| Input | `P2pInputCapture` (Unity InputSystem) | `P2pInputCapture` (`Input.IsPhysicalKeyPressed`) |
| View callbacks / HUD / menu | uGUI MonoBehaviours | Godot `Control` nodes |
| Entity view | `EntityView` + prefab | `EntityViewNode`(adapter) + `player.tscn` |

> **Single-source tradeoff**: P2pSample normally references the core as a single source. GodotP2pSample deliberately **copies** the sim + callbacks + data so it is a fully self-contained, independent sample (per request). The *core* (`Runtime/**`) is still shared/unchanged — only the game-specific sim is duplicated. Namespaces are kept as `xpTURN.Samples.P2pSample` so the copied `P2pSimulationCallbacks` and the `.bytes` asset (AssetId-based, generator-driven) load unchanged.

---

## 3. Architecture

```
com.xpturn.klotho/Godot~/                         (shared, unchanged)
  ├─ xpTURN.Klotho.Runtime         core (Microsoft.NET.Sdk, source-links Runtime/**)
  └─ xpTURN.Klotho.Runtime.Godot   adapter (Godot.NET.Sdk):
       View:  EntityViewNode / EntityViewUpdaterNode / EntityViewFactory / VerifiedFrameInterpolator
              DefaultGodotEntityViewPool / GodotPlayerViewRegistry / EngineEventOneShot / ErrorVisualState
       Flow:  GodotSessionDriver / GodotSessionFlowAsync / GodotConnectionAsync
       Misc:  GodotDebugSink / GodotLogSink / GodotKlothoLogger / FP*.Godot.cs incl. FPRay3·FPPlane·FPBounds3 (+ reconnect & Resource-config helpers)

Samples/GodotP2pSample/   (Godot.NET.Sdk game; ProjectReference → adapter → core)
  Sim/   (copied)         PlayerComponent / MoveCommand / Movement·Score·RespawnSystem / GameOverEvent / PlayerStatsAsset
  Game/  (copied)         P2pSimulationCallbacks
  Game-new (Godot):
    ├─ P2pGameNode : Node            single session + menu + GodotSessionDriver + pooled views + 3D view + logging
    ├─ P2pInputCapture               WASD/arrows → FP64 H/V
    ├─ GodotP2pViewCallbacks         IViewCallbacks → HUD
    ├─ GodotP2pMenu : Control        Host/Join/Ready/Stop + IP/Port
    ├─ GodotP2pHud : Control         state / score×2 / timer / result
    ├─ P2pEntityViewFactory          player entity → player.tscn
    └─ P2pPlayerView : EntityViewNode  tints mesh by PlayerId
```

The adapter is on `Godot.NET.Sdk` (same SDK as the game) so `GodotSharp` resolves consistently for everyone in the chain (see §6–§7).

---

## 4. Deterministic side (Sim)

Identical to P2pSample — see [P2pSample.md §4](P2pSample.md) for the component/command/event/asset/system details. Summary of what the copied code does:

- `PlayerComponent` (score + input cache) + builtin `TransformComponent` / `PhysicsBodyComponent`.
- `MoveCommand` (`IsContinuousInput`, FP64 H/V), `GameOverEvent` (Synced, `WinnerPlayerId`).
- `PlayerStatsAsset` (`AssetId=100`): MoveSpeed / MatchDuration / FallThresholdY / SpawnPoint / InitialSpawnOffsetX / PlayerMass / PlayerHalfExtent.
- `MovementSystem` (`velocity.x = H`, `velocity.z = V`), `RespawnSystem`, `ScoreSystem` (`GameOverEvent` at `matchEndTick`).
- `P2pSimulationCallbacks.OnInitializeWorld` spawns `MaxPlayers` players (±`InitialSpawnOffsetX`) and registers a 10×0.2×10 ground collider (top face at y=0).
- **Error correction is on here**, which takes two things and not one: `P2pGameNode` builds its `SimulationConfig` with `EnableErrorCorrection = true`, *and* the spawn adds `ErrorCorrectionTargetComponent` — the flag only arms the view-side smoother, while the deltas it smooths are produced only for marked entities. Under P2P every view renders on the predicted (CSP) path, so every player entity is a valid target. This is the one place the sim differs from P2pSample (Unity), which ships with the flag off. See [SimulationConfigGuide §1.1](../SimulationConfigGuide.md#11-enabling-error-correction--three-touches).

Because this is pure core/ECS code, it compiles and runs under Godot's .NET with **no engine reference** — the whole point of the port.

---

## 5. View / engine side (Godot)

### 5-1. Bootstrap (`P2pGameNode : Node`)

A `GodotSessionDriver` child node pumps the transport every frame — even before a session attaches, so the connect handshake (driven by `transport.PollEvents`) can complete — and pumps the session each frame once attached; the `EntityViewUpdaterNode` self-drives view interpolation from its own `_Process`. The game node only wires the menu, resolves the async join, and mirrors the phase to the HUD:

```
_Ready  → WarmupRegistry.RunAll(); CreateLogger (console + rolling file)
        → LoadAssetRegistry (FileAccess on res://Data/P2pAssets.bytes)
        → KlothoFlowSetupBuilder(callbacksFactory).WithLogger(...).WithTransport(...).WithAssetRegistry(...).WithGodotDefaults().Build()
              (sets AppVersion from ProjectSettings + GodotDeviceIdProvider via WithGodotDefaults)
        → P2pEntityViewFactory(player.tscn); DefaultGodotEntityViewPool.Prewarm(player.tscn, MaxPlayers)
        → EntityViewUpdaterNode + GodotSessionDriver (added as children)
        → driver.BindTransport(transport); driver.PreSessionUpdate += capture input when Running
        → wire menu buttons; SetupView3D()

Host  → flow.StartHostAndListen(simCfg, sessCfg, "Game", host, port)   → _session (return value) → OnSessionReady
Join  → flow.JoinP2PAsync(transport, host, port, sessCfg)               (async Task; resolved in _Process) → OnSessionReady
OnSessionReady → view.Initialize(session.Engine, factory, pool); driver.Attach(session); enable Ready/Stop
Ready → _session.SetReady(true)
Stop  → driver.DetachAndStop(); view.Cleanup(); viewCallbacks.Cleanup()

_Process → if joinTask completed → _session = result; OnSessionReady
           if _session != null → HUD ← _session.Phase (polled)
           (transport+session pump and view interpolation run in the driver / updater node, not here)
```

Session is taken from the entry-method **return value** (Host) or the join `Task.Result` (Join) — no `OnSessionCreated`/`LifecycleObserver` needed; `State`/`Phase` are polled (`KlothoState.Running`, `SessionPhase`) instead of relying on transition callbacks. The driver advances the session with a wall-clock `dt` (`DateTimeOffset.UtcNow`) and captures input from its `PreSessionUpdate` hook when `State==Running`.

### 5-2. Per-frame view drive — self-driven

The adapter is a `Godot.NET.Sdk` project, so its `Node`-derived classes get source-generated lifecycle dispatch: `EntityViewUpdaterNode._Process` runs every frame and drives the per-frame interpolation itself, with `ProcessPriority = 1000` so it runs **after** the `GodotSessionDriver` (interpolation reads the frame the driver just advanced). `Engine.OnTickExecuted` (a C# event) drives reconcile/spawn. `ProcessViews()` / `Cleanup()` remain exposed for explicit/headless drive, but the game node no longer pumps them each frame.

### 5-3. Input (`P2pInputCapture`)

WASD + arrows via `Input.IsPhysicalKeyPressed`. The V axis is **flipped** (W/↑ → −V): `MovementSystem` maps `V → world +Z`, but the top-down camera's screen-up is world −Z, so without the flip W/S feel inverted. H (A/D → ∓X) is unchanged.

### 5-4. Rendering & 3D view

- `P2pEntityViewFactory` returns `player.tscn` (root = `P2pPlayerView : EntityViewNode`, child `MeshInstance3D` with a `BoxMesh`) for any entity with `PlayerComponent`.
- `P2pPlayerView.OnActivate` reads `PlayerComponent.PlayerId` and sets a `MaterialOverride` — P1 blue, P2 red.
- `SetupView3D()` (in code, via `LookAt` to avoid hand-written basis matrices): top-down `Camera3D` over the origin, a background+ambient `Environment`, an angled `DirectionalLight3D`, plus a `PlaneMesh` ground (10×10) aligned to the physics collider's top face (y=0).

### 5-5. HUD / menu (`GodotP2pHud` / `GodotP2pMenu`, `Control`)

Same surface as the Unity uGUI versions: menu = 4 `Button`s + 2 `LineEdit` (IP/Port) wired to C# events; HUD = state / P1·P2 score / timer (`Filter<PlayerComponent>`, polled per `OnTickExecuted`) / result panel (WIN/LOSE/DRAW on `GameOverEvent` via `engine.OnSyncedEvent`). Because the game project **is** `Godot.NET.Sdk`, its node callbacks/signals work normally (unlike the adapter, §5-2).

### 5-6. Logging

`P2pGameNode` logs through `IKLogger` to **both** the Godot console (`GodotLogSink`) and a rolling file under `user://logs`, via `GodotKlothoLogger.CreateDefault(filePrefix: "P2p", categoryName: "P2p")`. The default directory resolves to `ProjectSettings.GlobalizePath("user://logs")` — an absolute path that is writable in both the editor and exported apps.

---

## 6. Common pitfalls (Godot-specific)

P2pSample's framework pitfalls (G1–G7 in [P2pSample.md §6](P2pSample.md)) still apply. These are the **Godot-port-specific** ones:

| # | Pitfall | Symptom | Fix |
|---|---|---|---|
| GP1 | Adapter on plain `Microsoft.NET.Sdk` + `GodotSharp` PackageReference | `CS0400 'Godot' not found` (nondeterministic across samples/builds) — a game's transitive/csproj-direct restore drops GodotSharp from the shared obj | Adapter on `Godot.NET.Sdk` (+ `Compile Include="Adapters/**"` to avoid CS0579) |
| GP2 | Godot needs a classic `.sln`; `dotnet 10` makes `.slnx` | Export: "EditorPlugin build callback failed → Aborting" | Commit a hand-written `<assembly_name>.sln` with `Debug;ExportDebug;ExportRelease` |
| GP3 | `System.IO.File` can't read `res://` inside an exported `.pck` | `.app` quits: `P2pAssets.bytes not found` | Read with `Godot.FileAccess.GetFileAsBytes("res://…")`; add `include_filter="*.bytes"` so it's packed |
| GP4 | Assuming the adapter's `Node` subclasses need manual pumping | Redundantly calling `ProcessViews()` from the game node each frame | Because the adapter is `Godot.NET.Sdk` (GP1), `EntityViewUpdaterNode._Process` is dispatched and self-drives interpolation (`ProcessPriority = 1000`, after the driver); `ProcessViews()`/`Cleanup()` remain only for explicit/headless drive |
| GP5 | macOS export-time signing blocks local runs | `.app` won't launch without manual `codesign --force --deep --sign -` | `export_presets.cfg`: `codesign/codesign=0` (no export-time signing) + `disable_library_validation=true` |
| GP6 | Top-down camera screen-up (−Z) vs `MovementSystem` V→+Z | W/S inverted on screen | Flip V in `P2pInputCapture` (W/↑ → −V); don't flip the camera `up` (it would invert A/D) |
| GP7 | Nothing renders despite entities spawning | No `Camera3D`/light/ambient in the scene | Add them (here in `SetupView3D()`); `viewNodes=2` in logs confirms entities exist even when the screen is black |

---

## 7. Build / Export notes

- Build via Godot (`Project > Tools > C#: Build`) or `dotnet build GodotP2pSample.sln` (Debug or `ExportDebug`).
- The adapter (`Godot.NET.Sdk`) + core (`ExportDebug`/`ExportRelease` configs) + per-sample `.sln` together make `dotnet build` (incl. Godot's csproj-direct build) and `godot --build-solutions` succeed with 0 errors.
- macOS export-time signing is disabled (`codesign/codesign=0`) for local runs; for distribution to other machines use a Developer ID identity + notarization.

---

## 8. Where to read next

- **P2pSample** — [`P2pSample.md`](P2pSample.md) for the deterministic design this port shares (components/systems/asset/pitfalls G1–G7).
- **Godot Server-Driven** — [`../../Samples/GodotSdSample/`](../../Samples/GodotSdSample/) for the SD-client port.
