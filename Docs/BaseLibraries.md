# xpTURN.Klotho Base Libraries

> The list of general-purpose base libraries used by the Klotho framework.
> Only project-infrastructure-level libraries — those not directly tied to Klotho-specific logic — are included.

---

## A. xpTURN First-Party Libraries

Standalone packages shared across the xpTURN ecosystem. Installed via Git URL.

### xpTURN.Polyfill

| Item | Contents |
| ---- | ---- |
| Purpose | Polyfills for C# 9 / 10 / 11 language features (targeting .NET Standard 2.1) |
| Git URL | `https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill` |
| Assembly | `xpTURN.Polyfill.Runtime` |
| Dependencies | None (lowest layer) |

**Provided types**:

- `IsExternalInit` — C# 9 init-only properties
- `InterpolatedStringHandlerAttribute` — C# 10 custom interpolated strings
- `CallerArgumentExpressionAttribute` — C# 10
- `RequiredMemberAttribute`, `CompilerFeatureRequiredAttribute` — C# 11
- `ModuleInitializerAttribute` — C# 9 (used by KlothoGenerator emit)

**Klotho usage**: Enables modern syntax (`init` accessors, interpolated-string handlers, etc.) in math structs such as FP64 and the FPVector family. `ModuleInitializerAttribute` is required by the source generator's auto-registration emit.

---

### xpTURN.Klotho.Logging (IKLogger)

| Item | Contents |
| ---- | ---- |
| Purpose | In-house structured logging (no external logging dependency) |
| Location | [`com.xpturn.klotho/Runtime/Logging/`](../com.xpturn.klotho/Runtime/Logging/) (engine-agnostic core — `KLoggerFactory`, `KLogBuilder`, `RollingFileSink`) + per-engine console sinks: `Unity/Logging/` (Unity) / `Godot~/Adapters/` (Godot) |
| Assembly | `xpTURN.Klotho.Logging` (core) + `xpTURN.Klotho.Logging.Unity` (Unity sink). **Godot has no separate `Logging.Godot`** — `GodotDebugSink` / `GodotLogSink` / `GodotKLoggerFactory` live in the single adapter assembly `xpTURN.Klotho.Runtime.Godot` |
| Dependencies | `xpTURN.Polyfill.Runtime` only (`noEngineReferences: true` for the core) |

**Features**:

- `IKLogger` / `KLoggerFactory` — zero external-deps logging interface
- `KLogHandler{Trace,Debug,Information,Warning,Error,Critical}` ref-struct interpolated handlers + `KInformation`/`KDebug`/`KWarning`/`KError` extension methods — uses Polyfill's `InterpolatedStringHandlerAttribute` for GC-free formatting
- Pluggable sinks — `RollingFileSink` is **engine-agnostic core** (`Runtime/Logging/`, composed via the core `KLogBuilder.AddRollingFile`); engine-specific **console** sinks are `UnityDebugSink` (Unity, `AddUnityDebug`) and `GodotDebugSink` / `GodotLogSink` (Godot, `GD.Print` / `GD.PushError`). Compose either with the core factory — e.g. Godot: `KLoggerFactory.Create(b => b.AddSink(new GodotLogSink()).AddRollingFile(...))` (see the Godot samples)
- `RollingFileSink` flush policy — `KFlushMode.PerLine` (default; one flush per line, immediate visibility, ≤ 1 line crash window) or opt-in `KFlushMode.AsyncEvent` (background-thread flush — no per-line flush syscall on the hot path, natural batching of bursts, drains on `Dispose`/process-exit). Select via `AddRollingFile(o => o.FlushMode = KFlushMode.AsyncEvent)`. The dedicated server uses `AsyncEvent`.

**Klotho usage**: Standard logging interface used framework-wide.

**Optional MEL Interop adapter** ([`Plugins~/Logging.Mel/`](../com.xpturn.klotho/Plugins~/Logging.Mel/)):

Opt-in package sample (UPM "Import Sample" → **MEL Logging Plugin**) that bridges `IKLogger` to `Microsoft.Extensions.Logging.ILogger`. Activating the adapter requires the consumer to provide `Microsoft.Extensions.Logging.Abstractions.dll` — consumer responsibility (the core package stays self-contained).

A runnable end-to-end example lives at [`Samples/LoggingMelConsole/`](../Samples/LoggingMelConsole/) — a .NET console app that routes Klotho's `IKLogger` surface through a standard `Microsoft.Extensions.Logging` pipeline via `MelKLogger`, logging to both the console and rolling files under `Logs/` with a ZLogger provider.

---

## B. Cysharp Open-Source Libraries

Cysharp-ecosystem libraries used across the xpTURN project.

### UniTask *(Unity)*

| Item | Contents |
| ---- | ---- |
| Purpose | Unity-specific async (GC-free async/await) |
| Git URL | `https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask` |

**Features**:

- `UniTask`, `UniTask<T>` — ValueTask-based, zero GC
- `UniTaskCompletionSource` — manual completion control
- `PlayerLoopTiming` — selectable Unity loop timing
- `WhenAll`, `WhenAny` — parallel async

**Klotho usage**: Asynchronous view creation in `EntityViewFactory.CreateAsync` (View layer), network connect/disconnect, replay file I/O. **UniTask is referenced only by the Unity-side adapter (`xpTURN.Klotho.Runtime.Unity`)** — the engine-agnostic core does not depend on it.

> **Godot has no UniTask dependency.** The Godot adapter (`xpTURN.Klotho.Runtime.Godot`) uses the standard library `Task` instead — e.g. `GodotConnectionAsync.ConnectAsync` returns `Task<ConnectionResult>`, and `GodotSessionFlowAsync` returns `Task<KlothoSession>`. (Godot view creation is synchronous — `EntityViewFactory.Create`, no async wrapper.)

---

## C. Vendored Third-Party Libraries

Third-party libraries shipped as source under [`com.xpturn.klotho/Runtime/ThirdParty/`](../com.xpturn.klotho/Runtime/ThirdParty/) with their original licenses preserved. **Vendoring policy**: vendored sources include `.cs` + `.meta` only — no `.csproj`/`.sln` (to prevent `bin`/`obj` pollution inside `Packages/`).

### LiteNetLib

| Item | Contents |
| ---- | ---- |
| Purpose | Lightweight UDP networking library |
| GitHub | <https://github.com/RevenantX/LiteNetLib> |
| Version | 2.1.5 (vendored as [`Runtime/ThirdParty/LiteNetLib.v2.1.5/`](../com.xpturn.klotho/Runtime/ThirdParty/LiteNetLib.v2.1.5/)) |
| License | MIT |
| Assembly | `LiteNetLib` |

**Features**:

- Reliable UDP (Reliable Ordered / Unordered / Sequenced)
- Automatic fragmentation, MTU discovery
- NAT punching, connection-request filtering
- Pure C# (Unity-compatible)

**Klotho usage**: `LiteNetLibTransport` (in `Runtime/LiteNetLib/`, `noEngineReferences: true`) — an `INetworkTransport` implementation used for both P2P and server transport.

**Per-message channel mapping**:

| Message | Channel | Reason |
| ---- | ---- | ---- |
| Input (per-tick input) | Sequenced | Only the latest input matters; ordering required; no resend needed |
| InputAck | Unreliable | Loss covered by next ack; minimal latency |
| SyncCheck (hash verification) | ReliableOrdered | Integrity is mandatory; no loss tolerated |
| Handshake (connection setup) | ReliableOrdered | Both reliability and ordering required |

---

## D. Standard Unity Packages

Unity packages declared in [`com.xpturn.klotho/package.json`](../com.xpturn.klotho/package.json) `dependencies` (auto-resolved from the Unity registry).

| Package | Version | Klotho Usage |
| ---- | ---- | ---- |
| `com.unity.inputsystem` | 1.18.0 | Input-Action-based local input capture → forwarded to the `OnPollInput` callback (`xpTURN.Klotho.Runtime.Unity`) |
| `com.unity.ai.navigation` | 2.0.12 | Unity NavMesh baking → converted to `.bytes` by `FPNavMeshExporter` (Editor NavMesh tooling only) |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | DataAsset serialization in the `xpTURN.Klotho.DataAsset.Json` assembly — `FPxxJsonConverter`, `DataAssetJsonSerializer` |
| `com.unity.test-framework` | 1.6.0 | NUnit-based unit / integration / determinism tests (dev project's `Assets/Tests`) |
| `com.unity.cinemachine` | 3.1.6 | Brawler sample's `BrawlerCameraController` (`Assets/Brawler` — dev only, not redistributed) |

> Cinemachine / test-framework are dev-project dependencies (`Assets/`); the core package declares only `inputsystem`, `ai.navigation`, `newtonsoft-json` in `dependencies`.

> These registry packages are **Unity-adapter-only** — the engine-agnostic core (`Runtime/**`) references none of them. `inputsystem` and `ai.navigation` have **no Godot counterpart**: Godot games capture input through the built-in `Input` API, and NavMesh baking is a Unity-Editor-only step (Godot just loads the resulting `.bytes`).

### Godot dependencies (different axis)

The Godot distribution wires its runtime dependencies as **NuGet `PackageReference`s** (via `addons/klotho/Klotho.props`), not Unity-registry packages:

| Dependency | Version | Note |
| ---- | ---- | ---- |
| `Newtonsoft.Json` | 13.0.3 | DataAsset JSON (vs Unity's `com.unity.nuget.newtonsoft-json`) |
| `LiteNetLib` | 2.1.5 | UDP transport (vendored as source on Unity) |
| `GodotSharp` | (project's) | Adapter compiles against the consumer's own GodotSharp (source-only) |

> On Unity, `LiteNetLib` ships as **vendored source** under `Runtime/ThirdParty/` (§C); on Godot it arrives as a NuGet package. The Godot core DLL is built with the vendored LiteNetLib excluded so the NuGet `2.1.5` swaps in cleanly.

---

## E. Dependency Layering

```
xpTURN.Polyfill.Runtime              ← bottom (no dependencies)

xpTURN.Klotho.Logging                ← + Polyfill (engine-agnostic, noEngineReferences; KLoggerFactory, KLogBuilder, RollingFileSink)
xpTURN.Klotho.Logging.Unity          ← + Klotho.Logging (UnityDebugSink — Unity console sink)

UniTask                              ← Unity-side only (Godot uses standard Task — no equivalent dependency)

LiteNetLib (vendored)                ← standalone (pure C#)

xpTURN.Klotho.Runtime                ← + Polyfill, Klotho.Logging (noEngineReferences; low-level Unsafe helpers are in-source via KUnsafe — no external dep)
xpTURN.Klotho.Runtime.Unity          ← + Klotho.Runtime, Klotho.Logging.Unity, UniTask, InputSystem
xpTURN.Klotho.Runtime.Godot          ← + Klotho.Runtime, GodotSharp (Godot~/Adapters/**, source-only, net8.0;
                                         folds its console sink GodotDebugSink/GodotLogSink — no separate Logging.Godot)
xpTURN.Klotho.LiteNetLib             ← + LiteNetLib, Klotho.Runtime, Klotho.Logging (noEngineReferences)
xpTURN.Klotho.DataAsset.Json         ← + Newtonsoft.Json, Klotho.Runtime
xpTURN.Klotho.Editor                 ← + Klotho.Runtime, Klotho.Runtime.Unity, Klotho.DataAsset.Json (Editor-only)
xpTURN.Klotho.Gameplay               ← + Klotho.Runtime, Polyfill

xpTURN.Klotho.Logging.Mel (opt-in)   ← + Klotho.Logging + Microsoft.Extensions.Logging.Abstractions (consumer-provided DLL)
```

> **Engine-agnostic core**: `Klotho.Runtime`, `Klotho.Logging`, `Klotho.Gameplay`, `Klotho.LiteNetLib` (transport) have `noEngineReferences: true` — they compile without UnityEngine and run on .NET dedicated server builds (see [Brawler.H.DedicatedServer.md](./Samples/Brawler.H.DedicatedServer.md)). The **Godot adapter** (`Runtime.Godot`) sits beside the Unity adapter on this same core (mirrors session driver / view / config / reconnect; see [GodotSdSample.md](./Samples/GodotSdSample.md) / [GodotP2pSample.md](./Samples/GodotP2pSample.md)).

---

*Last updated: 2026-06-28 (0.5.0: removed the bundled System.Runtime.CompilerServices.Unsafe — now in-source via `KUnsafe` — and K4os.Compression.LZ4, with replays written uncompressed)*
