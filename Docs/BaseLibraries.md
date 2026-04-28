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

**Klotho usage**: Enables modern syntax (`init` accessors, interpolated-string handlers, etc.) in math structs such as FP64 and the FPVector family.

---

## B. Cysharp Open-Source Libraries

Cysharp-ecosystem libraries used across the entire xpTURN project.

### UniTask

| Item | Contents |
| ---- | ---- |
| Purpose | Unity-specific async (GC-free async/await) |
| Git URL | `https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask` |

**Features**:

- `UniTask`, `UniTask<T>` — ValueTask-based, zero GC
- `UniTaskCompletionSource` — manual completion control
- `PlayerLoopTiming` — selectable Unity loop timing
- `WhenAll`, `WhenAny` — parallel async

**Klotho usage**: Asynchronous view creation in `EntityViewFactory.CreateAsync` (View layer), network connect/disconnect, replay file I/O.

---

### ZString

| Item | Contents |
| ---- | ---- |
| Purpose | Zero-GC string builder |
| Git URL | `https://github.com/Cysharp/ZString.git?path=src/ZString.Unity/Assets/Scripts/ZString` |

**Features**:

- `Utf16ValueStringBuilder` — stackalloc-based string building
- `Utf8ValueStringBuilder` — direct UTF-8 building
- `ZString.Format`, `ZString.Concat` — static APIs

**Klotho usage**: Used internally by ZLogger as the foundation for structured-log formatting.

---

### ZLogger

| Item | Contents |
| ---- | ---- |
| Purpose | High-performance structured logging (built on Microsoft.Extensions.Logging) |
| Git URL | `https://github.com/Cysharp/ZLogger.git?path=src/ZLogger.Unity/Assets/ZLogger.Unity` |
| Unity Package | `ZLogger.Unity` |

**Features**:

- Conforms to the `ILogger` interface (Microsoft.Extensions.Logging)
- Allocation-free log output (uses ZString internally)
- Structured logging (JSON, MessagePack, etc.)

**Klotho usage**: `ILogger<T>` binding inside the framework and the logging backend for the Brawler sample / tests.

---

## C. NuGet Packages (managed via NuGetForUnity)

`com.github-glitchenzo.nugetforunity` is registered in `Packages/manifest.json`. Actual DLLs are placed under `Assets/Packages/<PackageId>.<Version>/`, with versions pinned in `packages.config`.

### Microsoft.Extensions.Logging / .Abstractions

| Item | Contents |
| ---- | ---- |
| Purpose | Logging abstractions (`ILogger`, `ILogger<T>`, `LogLevel`) |
| Version | 8.0.0 |
| Path | `Assets/Packages/Microsoft.Extensions.Logging.8.0.0/`, `...Abstractions.8.0.0/` |
| Assembly | `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Abstractions` |

**Klotho usage**: Standard logging interface used framework-wide (112+ files). The implementation is ZLogger.

---

### K4os.Compression.LZ4

| Item | Contents |
| ---- | ---- |
| Purpose | High-speed LZ4 block / frame compression |
| Version | 1.3.8 |
| Path | `Assets/Packages/K4os.Compression.LZ4.1.3.8/` |
| Assembly | `K4os.Compression.LZ4` |

**Klotho usage**: Replay-file compression / decompression in `ReplaySystem`. Used on the compressed-stream path (distinguished from the uncompressed `RPLY` magic-number path).

---

## D. Network Transport

### LiteNetLib

| Item | Contents |
| ---- | ---- |
| Purpose | Lightweight UDP networking library |
| GitHub | <https://github.com/RevenantX/LiteNetLib> |
| License | MIT |
| Assembly | `LiteNetLib` |

**Features**:

- Reliable UDP (Reliable Ordered / Unordered / Sequenced)
- Automatic fragmentation, MTU discovery
- NAT punching, connection-request filtering
- Pure C# (Unity-compatible)

**Klotho usage**: `LiteNetLibTransport` — an `INetworkTransport` implementation used for both P2P and server transport.

**Per-message channel mapping**:

| Message | Channel | Reason |
| ---- | ---- | ---- |
| Input (per-tick input) | Sequenced | Only the latest input matters; ordering required; no resend needed |
| InputAck | Unreliable | Loss covered by next ack; minimal latency |
| SyncCheck (hash verification) | ReliableOrdered | Integrity is mandatory; no loss tolerated |
| Handshake (connection setup) | ReliableOrdered | Both reliability and ordering required |

---

## E. Standard Unity Packages

Unity packages bundled in the Klotho project.

| Package | Version | Klotho Usage |
| ---- | ---- | ---- |
| `com.unity.inputsystem` | 1.18.0 | Input-Action-based local input capture → forwarded to the `OnPollInput` callback |
| `com.unity.test-framework` | 1.6.0 | NUnit-based unit / integration / determinism tests |
| `com.unity.ai.navigation` | 2.0.12 | Unity NavMesh baking → converted to `.bytes` by `FPNavMeshExporter` |
| `com.unity.cinemachine` | 3.1.6 | The Brawler sample's `BrawlerCameraController` — `CinemachineCamera`, `CinemachineFollow` |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | DataAsset serialization in the `xpTURN.Klotho.DataAsset.Json` assembly — `FPxxJsonConverter`, `DataAssetJsonSerializer` |

---

## F. Dependency Layering

```
xpTURN.Polyfill            ← bottom (no dependencies)

UniTask                    ← standalone
ZString                    ← standalone
ZLogger.Unity              ← + Microsoft.Extensions.Logging(.Abstractions), ZString

LiteNetLib                 ← standalone (pure C#)
Newtonsoft.Json            ← standalone (com.unity.nuget.newtonsoft-json)
K4os.Compression.LZ4       ← standalone (NuGetForUnity)

xpTURN.Klotho (core)           ← only Microsoft.Extensions.Logging
xpTURN.Klotho (Unity)          ← + UniTask (EntityViewFactory.CreateAsync)
xpTURN.Klotho.Runtime (Replay) ← + K4os.Compression.LZ4 (ReplaySystem)
xpTURN.Klotho.LiteNetLib       ← + LiteNetLib, xpTURN.Klotho (core)
xpTURN.Klotho.DataAsset.Json   ← + Newtonsoft.Json, xpTURN.Klotho (core)
```

---

*Last updated: 2026-04-24*
