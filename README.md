# xpTURN.Klotho
[![Unity 2022.3+](https://img.shields.io/badge/unity-2022.3%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![Godot 4.4+ (.NET)](https://img.shields.io/badge/godot-4.4%2B%20(.NET)-478cbf.svg)](https://godotengine.org/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

**Deterministic Multiplayer Simulation Framework for Unity and Godot**

> ⚠️ **Experimental Release**
> This project is under active development and has not yet reached a stable stage. Public APIs, serialization formats, and network protocols may change without notice. Production use is not recommended.

A deterministic-simulation framework supporting Client-Side Prediction (CSP), Rollback, Frame Synchronization, Server-Driven mode, and Replay. The simulation core is engine-agnostic pure C# with **Unity** and **Godot (.NET)** adapters on top. By excluding floating-point and building the simulation solely on 32.32 fixed-point (`FP64`) and a deterministic RNG (Xorshift128+), it guarantees full reproducibility across platforms and compilers.

> Klotho weaves the simulation, one frame at a time.

**How synchronization works, in one paragraph:** determinism is the keystone — given the same ordered inputs, every peer computes byte-identical state, so the network carries *inputs only* and verification reduces to comparing a hash. On that foundation each peer keeps two timelines over the same tick axis: a **Verified chain** (ticks where every player's real input is known — immutable) and a **Predicted chain** (ticks run ahead using *guessed* remote input — provisional). Like CPU branch prediction, the simulation advances immediately on predicted input instead of waiting; when a real input contradicts a guess, the engine restores a snapshot and re-simulates (**rollback**), which is cheap precisely because state is a pure function of inputs. Input delay and adaptive timing buffers minimize how often that happens, and when determinism genuinely breaks, a graded recovery ladder (hash check → rollback → full-state resync → corrective reset) restores agreement. The same machinery serves both **P2P lockstep** (peers hold equal authority) and **Server-Driven** (the server owns the verified chain). Full rationale: [Docs/SynchronizationDesign.md](Docs/SynchronizationDesign.md).

---

## Key Features

| Area | Contents |
| ---- | ---- |
| **Network Models** | P2P Lockstep · Rollback + Client Prediction · Server-Driven (Authoritative) · Dedicated Server · Spectator · Late Join · Reconnect |
| **Rooms & Match Config** | Multi-room dedicated server · per-room stage (`StageId`) + opaque per-match config (`MatchConfigData`) via `IMatchConfigSource` — one server hosts rooms on different stages · lobby room reservation (reserve-before-connect) · verified match-result reporting to the lobby (`IMatchResultProvider`, at-least-once + idempotent) |
| **Deterministic Math** | `FP64` (32.32 fixed-point) · `FPVector2/3/4` · `FPQuaternion` · `FPMatrix` · LUT/CORDIC-based trigonometry · `DeterministicRandom` (Xorshift128+) |
| **Physics** | `FPPhysicsWorld` · Broadphase (SpatialGrid) · Narrowphase · CCD (Sweep) · Constraint Solver · Joints · Triggers · Static BVH |
| **Navigation** | `FPNavMesh` · A* (triangle graph) · Funnel (SSFA) · ORCA avoidance · ECS-integrated `NavAgentComponent` |
| **ECS** | Sparse-set `ComponentStorage<T>` · `Frame` (single byte[] heap) · `FilterWithout/Filter<T1..T5>` · `FrameRingBuffer` · `SystemRunner` |
| **AI / HFSM** | Deterministic hierarchical FSM · `HFSMBuilder` (fluent) · `HFSMRoot` / `HFSMManager` · `HFSMComponent` (per-entity) · `HFSMDecision` / `AIAction` · Unity state-tree visualizer |
| **Serialization / Source Generator** | `SpanWriter/Reader` (ref struct, GC-free) · automatic code generation via `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` / `[KlothoSerializableStruct]` · build-time `DeterminismAnalyzer` (flags float / non-deterministic APIs in simulation code) |
| **Data Assets** | `IDataAsset` · `DataAssetRegistry` · `DataAssetRef` · JSON serialization (`xpTURN.Klotho.DataAsset.Json`) |
| **Replay** | Record / playback / seek / variable speed |
| **Verification Tools** | `SyncTestRunner` (GGPO-style determinism verification) · `DeterminismVerificationRunner` · benchmark suite |
| **Unity Integration** | `USimulationConfig` · `USessionConfig` · View layer (`EntityViewFactory` / `EntityViewUpdater` / `EntityView`, `BindBehaviour` / `ViewFlags`, `VerifiedFrameInterpolator`) · `KlothoSessionDriver` (MonoBehaviour) · `KlothoConnectionAsync` (UniTask) |
| **Godot Integration** | `GodotSimulationConfig` · `GodotSessionConfig` (Resource) · View layer (`EntityViewFactory` / `EntityViewUpdaterNode` / `EntityViewNode`, `VerifiedFrameInterpolator`) · `GodotSessionDriver` (Node) · `GodotConnectionAsync` (`Task`) · `GodotDebugSink` / `GodotLogSink` |

---

## Suitable Genres

Genres that benefit most from Klotho's deterministic simulation features.

### Best Fit (core targets)

- **Fighting games** — frame-perfect inputs + rollback netcode
- **Platform fighters / arena brawlers**
- **2–4 player PvP action** — optimal for small-roster P2P rollback
- **Tactics / turn-based SRPG** — determinism + replay + sync verification
- **Real-time strategy (RTS)** — lockstep frame sync
- **MOBA / top-down arena** — ECS, physics, and navigation all included
- **Twin-stick shooters / co-op shooters** — deterministic physics + ORCA avoidance
- **Auto-battlers** — deterministic simulation + shareable replays

### Good Fit (structurally well-suited)

- **Roguelike / roguelite (PvE co-op)** — deterministic RNG (Xorshift128+) for shared seeds and replays
- **Card / board / deckbuilder PvP** — low input volume, strong verification and replay
- **Puzzle PvP / falling-block versus**
- **Racing (small scale)** — fixed-point physics guarantees determinism for small grids
- **Top-down survival action**

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Game Application                         │
│   (ISimulation impl: EcsSimulation or custom Simulation)    │
└───────────────────────────┬─────────────────────────────────┘
                            │
         ┌──────────────────┼──────────────────┐
         ▼                  ▼                  ▼
  ┌───────────────┐   ┌──────────────┐   ┌────────────────────┐
  │ KlothoEngine  │   │ ReplaySystem │   │KlothoNetworkService│
  │ (orchestrator)│◄  │ (record/play)│   │                    │
  └──────┬────────┘   └──────────────┘   └─────────┬──────────┘
         │                                         │
    ┌────┴────┐                              ┌─────┴──────┐
    ▼         ▼                              ▼            ▼
 ISimulation  InputBuffer               INetworkTransport  NetworkMessages
    │
    ├── (ECS) Frame — EntityManager + ComponentStorage[]
    ├── SystemRunner — ISystem[]
    ├── FrameRingBuffer (snapshots / rollback)
    └── DataAssetRegistry
```

Three-layer separation:
- **Klotho engine layer** — Pure C#, engine-independent (no `UnityEngine` / Godot references). The same binary runs under the Unity and Godot (.NET) adapters and on the server side (.NET console / ASP.NET).
- **Simulation transport layer** — UDP transport over the `INetworkTransport` abstraction (Input / InputAck / SyncCheck / Handshake). LiteNetLib is provided as the default reference implementation; replaceable with any other library.
- **Game service layer** — Lobby / matchmaking / authentication (external gRPC, etc.; integrated outside this project).

---

## Tech Stack

- **Unity 2022.3+** — scripting backends: Mono / IL2CPP (AOT-safe; serialization via source generation, no runtime codegen/reflection)
- **Godot 4.4+ (mono / .NET, net8.0)** — the Godot adapter ships as `addons/klotho/` (prebuilt core DLL + adapter source).
- **C# language level** — Unity targets **C# 8.0** with the `xpTURN.Polyfill` package supplying the runtime-attribute shims required by C# 11; the Godot adapter is **net8.0 / `LangVersion latest`** and needs no polyfill (native `init` / `required`).
- **UniTask** — async on Unity only (Cysharp). The **Godot adapter uses the standard library `Task`** — no UniTask dependency.
- **xpTURN.Klotho.Logging (IKLogger)** — in-house structured logging (no external logging dependency). Optional MEL interop via the `Plugins~/Logging.Mel` sample adapter (`Microsoft.Extensions.Logging.Abstractions` DLL is consumer-provided).
- **LiteNetLib** — default reference implementation for UDP transport (MIT, pure C#). On Unity it is **vendored as source** under `Runtime/ThirdParty/LiteNetLib.v2.1.4`; on Godot it arrives as the **NuGet package `LiteNetLib 2.1.4`**. The transport layer is abstracted via `INetworkTransport` and is replaceable.
- **Newtonsoft.Json** — DataAsset JSON serialization (Unity: `com.unity.nuget.newtonsoft-json` · Godot: NuGet `Newtonsoft.Json 13.0.3`)

Details: [Docs/BaseLibraries.md](Docs/BaseLibraries.md)

---

## Installation

Klotho installs differently per engine — follow the matching guide. Each covers the **client** first, then the optional **dedicated server** (a plain .NET host on the shared engine-agnostic core):

- **[Installation — Unity](Docs/Installation.Unity.md)** — UPM git URLs + Polyfill activation, then `Server~` project references for a dedicated server.
- **[Installation — Godot (.NET)](Docs/Installation.Godot.md)** — the `addons/klotho/` folder + a one-line `Klotho.props` import, then the (engine-agnostic) dedicated server.

The in-repo projects under [`Samples/`](Samples/) double as install references — see the [Documentation Map](#documentation-map) for per-sample walkthroughs.

---

## Repository Layout

Klotho lives at the repository top level under `com.xpturn.klotho/`. Unity consumers install the package via UPM; Godot consumers use the `Godot~/` adapter packaged as an `addons/klotho/` folder (see [Installation](#installation)). The in-repo Unity samples consume the package through a `file:` manifest reference to the same top-level package.

```
<repo root>/
├── README.md  ·  CHANGELOG.md  ·  LICENSE
├── com.xpturn.klotho/             ← ★ framework package
│   ├── package.json
│   ├── Runtime/                   engine-agnostic core
│   │   ├── Core/                  engine · session · network
│   │   ├── Logging/               IKLogger (in-house)
│   │   ├── Gameplay/              built-in components / systems
│   │   ├── Diagnostics/           fault injection · metrics
│   │   ├── Input/                 input buffer / predictor
│   │   ├── Network/               transport · server · spectator
│   │   ├── State/                 snapshot manager
│   │   ├── Serialization/         SpanWriter/Reader
│   │   ├── Replay/                record / playback
│   │   ├── ECS/                   Frame · components · systems
│   │   ├── Deterministic/         FP64 · physics · navigation
│   │   ├── LiteNetLib/            UDP transport
│   │   └── ThirdParty/            vendored deps
│   ├── Unity/                     Unity adapter
│   │   └── Editor/                Unity-only editor tools
│   ├── Godot~/                    Godot (.NET) adapter
│   │   └── Adapters/Editor/       Godot (.NET) editor tools
│   ├── Plugins/Analyzers/         source generator
│   ├── Plugins~/Logging.Mel/      MEL interop sample
│   └── Server~/                   dedicated-server projects
│
├── dist/addons/klotho/            ← Godot addon
│
├── Samples/                       ← standalone samples
│   ├── Brawler/                   4-player fighter (Unity)
│   ├── P2pSample/                 minimal P2P (Unity)
│   ├── SdSample/                  minimal Server-Driven (Unity)
│   ├── GodotP2pSample/            minimal P2P (Godot)
│   ├── GodotSdSample/             minimal Server-Driven (Godot)
│   └── LoggingMelConsole/         .NET logging sample
│
├── Docs/                          ← documentation
└── Tools/                         ← .NET tooling (internal)
    ├── KlothoGenerator/           source generator
    ├── KlothoGenerator.Tests/     generator tests
    ├── DeterminismVerification/   determinism verifier
    ├── PhysicsDeterminismProbe/   FP determinism probe
    └── gen.sh                     generator build script
```

---

## Quick Start

The four-step path — define a component → implement a system → wire callbacks → create & drive a session — is the same shape on both engines, but the session-driving and view layers are engine-specific. Pick the matching walkthrough (each is self-contained, with full code):

- **[Quick Start — Unity](Docs/QuickStart.Unity.md)** — `MonoBehaviour` controller, `KlothoSessionDriver`, `ScriptableObject` configs, UniTask joins, `EntityViewFactory` / `EntityViewUpdater` / `EntityView`.
- **[Quick Start — Godot (.NET)](Docs/QuickStart.Godot.md)** — `Node` controller, `GodotSessionDriver` (`_Process`), `Resource` configs, standard `Task` joins, `EntityViewNode` / `EntityViewUpdaterNode` (`.tscn`).

Steps 1–3 (component / system / callbacks) are engine-agnostic core; `KlothoSessionFlow` exposes the same entry points (`StartHostAndListen` / `JoinP2PAsync` / `JoinServerDrivenAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplayFromFile`) on both — Unity wraps the async ones in UniTask, Godot in standard `Task`. Session creation is observed through the single `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` — branch on `kind`, not `simCfg.Mode`.

Detailed guides: [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md), [Docs/GameDevAPI.md](Docs/GameDevAPI.md)

---

## Sample

[Samples/Brawler](Samples/Brawler) — a 4-player fighting-game sample

- ECS-based combat / movement / skills / cooldowns / knockback / items / traps
- HFSM-based bot AI (`BotHFSMRoot` / `BotActions` / `BotDecisions`)
- Camera integration with Unity Cinemachine
- Supports both P2P and ServerDriven modes
- Replay record / playback

Docs: [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md)

---

## Documentation Map

| Document | Contents |
| ---- | ---- |
| [Docs/Installation.Unity.md](Docs/Installation.Unity.md) · [Docs/Installation.Godot.md](Docs/Installation.Godot.md) | Engine-specific install (client + dedicated server) |
| [Docs/QuickStart.Unity.md](Docs/QuickStart.Unity.md) · [Docs/QuickStart.Godot.md](Docs/QuickStart.Godot.md) | Engine-specific 5-step quick starts (component → system → callbacks → session → view) |
| [Docs/FEATURES.md](Docs/FEATURES.md) | Full feature list |
| [Docs/Specification.md](Docs/Specification.md) | Engine specification (state machines · configuration · events · message protocol · formats) |
| [Docs/LobbyIntegrationGuide.md](Docs/LobbyIntegrationGuide.md) | Lobby ↔ dedicated server ↔ client integration (mockup) — ticket carriage · validation hooks · identity propagation · match-result reporting |
| [Docs/EntitlementLifecycle.md](Docs/EntitlementLifecycle.md) | Trusted player data (entitlements) lifecycle reference — origin · store · preserve · propagate · read · dispose · invariants |
| [Docs/SynchronizationDesign.md](Docs/SynchronizationDesign.md) | Synchronization design direction (determinism · two-chain model · prediction/rollback · timing · authority models · recovery ladder) |
| [Docs/DesyncDiagnostics.md](Docs/DesyncDiagnostics.md) | Desync root-cause localization & log-analysis guide (diagnostic funnel: class → tick → layer · reading the logs · online Probe verdict · `DiagnosticHistoryTicks` · safety guarantees) |
| [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md) | Game-developer workflow (step-by-step) |
| [Docs/GameDevAPI.md](Docs/GameDevAPI.md) | Game-developer API status |
| [Docs/SimulationConfigGuide.md](Docs/SimulationConfigGuide.md) | SimulationConfig recommended-value guide (per genre / platform) |
| [Docs/BaseLibraries.md](Docs/BaseLibraries.md) | List of base libraries used |
| [Docs/ECS.md](Docs/ECS.md) | ECS guide (entities · components · systems · filters · Frame snapshot/hash · rollback) |
| [Docs/Serialization.md](Docs/Serialization.md) | Serialization & source generator (`SpanWriter/Reader` · `[KlothoComponent]`/`[KlothoSerializable]`/`[KlothoDataAsset]` codegen · supported types · diagnostics) |
| [Docs/DeterministicMath.md](Docs/DeterministicMath.md) | Deterministic math (`FP64` 32.32 fixed-point · `FPVector*`/`FPQuaternion`/`FPMatrix` · trig · geometry · `DeterministicRandom`) |
| [Docs/Replay.md](Docs/Replay.md) | Replay (record inputs · save/load · play/pause/seek/speed · determinism guarantees) |
| [Docs/Navigation.md](Docs/Navigation.md) | Deterministic navigation (FPNavMesh · A* · Funnel · ORCA) |
| [Docs/NavMeshVisualizer.Godot.md](Docs/NavMeshVisualizer.Godot.md) | `Godot (.NET)` editor tool — visualize a serialized `FPNavMesh` (`.bytes`) and validate pathfinding / agent simulation in the 3D viewport |
| [Docs/PhysicsWorld.md](Docs/PhysicsWorld.md) | Deterministic physics (rigid bodies · colliders · contacts · triggers · CCD) |
| [Docs/PhysicsVisualizer.Godot.md](Docs/PhysicsVisualizer.Godot.md) | `Godot (.NET)` runtime/editor tools — draw the live FPPhysics world (bodies · colliders · contacts), HUD inspector, static-collider viewer |
| [Docs/DataAsset.md](Docs/DataAsset.md) | DataAsset authoring guide (define · author JSON · build `.bytes` · register · look up) |
| [Docs/HFSM.md](Docs/HFSM.md) | Hierarchical FSM for agent/bot AI (`HFSMBuilder` · `HFSMRoot` · `HFSMManager` · decisions/actions) |
| [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md) · [Docs/Samples/P2pSample.md](Docs/Samples/P2pSample.md) · [Docs/Samples/SdSample.md](Docs/Samples/SdSample.md) | `Unity` sample walkthroughs — Brawler, P2pSample, SdSample |
| [Docs/Samples/GodotSdSample.md](Docs/Samples/GodotSdSample.md) · [Docs/Samples/GodotP2pSample.md](Docs/Samples/GodotP2pSample.md) | `Godot (.NET)` sample walkthroughs (architecture + Godot-specific gotchas) |
| [Docs/Samples/DevIdentityKeys.md](Docs/Samples/DevIdentityKeys.md) | Dev identity keys — generate & rotate the Ed25519 key pair used by the SD/P2P sample lobby identity |

---

## Design Principles

- **Determinism first** — no float; all simulation state is FP64 / integer / bool
- **Zero-GC oriented** — ref struct, object pools, cached fields, no LINQ
- **Engine independence** — the core is pure C# (no `UnityEngine` / `Godot` references); engine integration (Unity / Godot) lives in adapter layers on top of the shared core
- **Minimal bandwidth** — only inputs (commands) are sent; no state synchronization (only hash verification)
- **Layer separation** — strict separation between simulation callbacks (deterministic) and view callbacks (non-deterministic)
