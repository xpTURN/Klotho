# Serialization & Source Generator

Klotho serializes simulation state, commands, and network messages to compact binary with **zero runtime reflection and zero GC**. You almost never write a `Serialize`/`Deserialize` method by hand: you tag a type with one attribute, and a Roslyn **source generator** (`KlothoGenerator`) emits the read/write/size/hash code at compile time. This is what keeps Klotho **AOT-safe** (IL2CPP, Mono, NativeAOT) — there is no runtime codegen or reflection to strip away — and what makes the per-tick hot path allocation-free.

> Audience: game developers defining components, commands, network messages, events, and data assets.
> Goal: understand the low-level `SpanWriter`/`SpanReader`, the attributes that trigger code generation, what gets generated, supported field types, and the diagnostics that guard determinism.
>
> Related: [ECS.md](ECS.md) (`[KlothoComponent]` components) · [DataAsset.md](DataAsset.md) (`[KlothoDataAsset]` assets) · [Specification.md](Specification.md) §11 (GC strategy) · [FEATURES.md](FEATURES.md) (Serialization index)

---

## 1. Two Layers

| Layer | Types | You use it to |
| ---- | ---- | ---- |
| **Low-level codec** | `SpanWriter` / `SpanReader` (`ref struct`), `ISpanSerializable`, `SerializationBuffer` | Read/write primitives into a byte buffer with no allocation. The generated code calls into this. |
| **Source generator** | `[KlothoComponent]` · `[KlothoSerializable]` · `[KlothoDataAsset]` · `[KlothoSerializableStruct]` + `[KlothoOrder]` / `[KlothoIgnore]` / `[KlothoHashIgnore]` | Mark a type; the generator writes its codec (and factory/hash) for you. |

The generator (`KlothoGenerator`) runs four independent pipelines — one per top-level attribute — and ships as a prebuilt analyzer DLL (see [§9](#9-building--inspecting-the-generator)). The fourth, `[KlothoSerializableStruct]` ([§3.1](#31-klothoserializablestruct--reusable-inline-field-bundles)), is a *nested* codec with no wire id — a host type inlines it. You rarely touch the low-level codec directly; it's documented here because the generated code, manual escape hatches, and custom transports all bottom out in it.

---

## 2. The Low-Level Codec — `SpanWriter` / `SpanReader`

Both are `ref struct`s wrapping a `Span<byte>` and a cursor (`Position`). They never allocate; the caller owns the buffer.

```csharp
Span<byte> buffer = stackalloc byte[64];          // or a pooled SerializationBuffer
var writer = new SpanWriter(buffer);
writer.WriteInt32(42);
writer.WriteFP(myFp64);                            // FP64 / FPVector2/3/4 / FPQuaternion
writer.WriteEntityRef(entity);
int written = writer.Position;

var reader = new SpanReader(buffer.Slice(0, written));
int   a = reader.ReadInt32();
FP64  b = reader.ReadFP64();
EntityRef e = reader.ReadEntityRef();
```

`SpanWriter` instance methods: `WriteByte` · `WriteBool` · `WriteInt16/UInt16` · `WriteInt32/UInt32` · `WriteInt64/UInt64` · `WriteEntityRef` · `WriteDataAssetRef` · `WriteString` (UTF-8, length-prefixed) · `WriteBytes` (length-prefixed) · `WriteRawBytes` (no prefix); plus `Position` / `Capacity` / `Remaining`. `SpanReader` has the symmetric `Read*`.

- **The FP and physics codecs are extension methods**, not instance methods — `WriteFP` (overloaded for `FP64` / `FPVector2/3/4` / `FPQuaternion`), `WriteFPRigidBody`, `WriteFPCollider`, and the matching `ReadFP64` / `ReadFPVector*` / `ReadFPRigidBody` / `ReadFPCollider` all live in `FPSpanExtensions` (call them as `writer.WriteFP(v)` after `using xpTURN.Klotho.Serialization;`). They bottom out in `WriteInt64(value.RawValue)`, so FP state round-trips bit-exactly as raw 32.32 integers — no float ever touches the wire.

- **All integers are little-endian** (`BinaryPrimitives`), so the wire format is identical across platforms and architectures.
- **Bounds checking is `[Conditional("DEBUG")]`** — `SpanWriter` throws "buffer overflow" in debug builds if you under-size the buffer, but the check is compiled out of release. So `GetSerializedSize()` must be correct; the generator computes it for you.
- **`ISpanSerializable`** is the manual contract — `Serialize(ref SpanWriter)`, `Deserialize(ref SpanReader)`, `GetSerializedSize()`. The generator implements exactly this shape; `IComponent` ([ECS.md](ECS.md)) extends it with `GetHash`.
- **`SerializationBuffer`** is a pooled, `IDisposable` managed `byte[]` for when you need a heap buffer instead of `stackalloc`.

---

## 3. Triggering Code Generation

Three attributes, three **independent id planes**. Each plane is validated by its own pipeline against its own set of ids — the generator never compares an id in one plane against another. A `typeId` only has to be unique *within its own plane*, so **the same integer can name three unrelated things at once**:

```csharp
[KlothoComponent(100)]    public partial struct HealthComponent : IComponent { … }   // component plane
[KlothoSerializable(100)] public partial class  SpawnCommand    : CommandBase  { … }   // serializable plane
[KlothoDataAsset(100)]    public partial class  WeaponAsset     : IDataAsset    { … }   // data-asset plane
```

All three `100`s coexist with zero conflict — there is no shared registry to collide in. Each attribute below maps to a different pipeline:

| Attribute | Applies to | Pipeline | id plane (uniqueness scope) |
| ---- | ---- | ---- | ---- |
| `[KlothoComponent(typeId)]` | `unmanaged partial struct : IComponent` | ECS component | component type id — unique across all components (16-bit; `UserMinId = 100`) |
| `[KlothoSerializable(typeId)]` | `partial class` deriving a known base | Entity / Command / Message / Event | wire `TYPE_ID` — unique **per category** (an Entity and a Command may share an id) |
| `[KlothoDataAsset(typeId, …)]` | `partial class : IDataAsset` | Data asset | asset type id — unique across all data assets (+ a separate `AssetId` instance plane) |
| `[KlothoSerializableStruct]` | `unmanaged partial struct` | Nested inline codec | — none — (no wire id; never dispatched, a host type inlines it; see [§3.1](#31-klothoserializablestruct--reusable-inline-field-bundles)) |

The uniqueness scope is *intra-plane*: a duplicate is only an error against ids **in the same plane** (`KLSG_ECS001` for components, `KLSG007` for serializable types within one category, `KLSG_DA005` for data assets). Nothing checks across planes, because nothing dispatches across them.

`[KlothoComponent]` is covered in [ECS.md §3](ECS.md) and `[KlothoDataAsset]` in [DataAsset.md](DataAsset.md). This section focuses on **`[KlothoSerializable]`**, which serves the four networked/runtime categories. The category is inferred from the **base class**:

| Base class | Category | Generated extra |
| ---- | ---- | ---- |
| `xpTURN.Klotho.State.EntityBase` | Entity | `EntityFactory.TYPE_ID` + factory registration |
| `xpTURN.Klotho.Core.CommandBase` | Command | `CommandFactory.TYPE_ID` + factory registration |
| `xpTURN.Klotho.Network.NetworkMessageBase` | Message | dispatch override (needs `MessageTypeId`) + `MessageSerializer` registration |
| `xpTURN.Klotho.Core.SimulationEvent` | Event | codec only |

```csharp
using xpTURN.Klotho.Serialization;

[KlothoSerializable(typeId: 10)]
public partial class SpawnUnitCommand : CommandBase
{
    [KlothoOrder(0)] public int       UnitKind;
    [KlothoOrder(1)] public FPVector3 Position;
    [KlothoOrder(2)] public EntityRef Owner;
}
```

For a network message, set `MessageTypeId` via named-arg (a `NetworkMessageType` enum member, or a user value past `UserDefined_Start` via cast):

```csharp
[KlothoSerializable(typeId: 50, MessageTypeId = (NetworkMessageType)201)]
public partial class MyCustomMessage : NetworkMessageBase { /* fields */ }
```

### 3.1 `[KlothoSerializableStruct]` — reusable inline field bundles

`[KlothoSerializableStruct]` marks an **`unmanaged partial struct`** so its fields can be grouped into a reusable bundle and serialized *inline* wherever it appears as a field. It is **not** a dispatched type — no wire id, no factory — so it has no id plane. A host type in any of the three categories above that declares a **field** of this struct delegates to the struct's generated codec.

```csharp
using System.Runtime.InteropServices;
using xpTURN.Klotho.Serialization;

[KlothoSerializableStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe partial struct FsmState
{
    public int RootId;
    public fixed int ActiveStateIds[8];
    public int Depth;
}

[KlothoComponent(200)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct AgentComponent : IComponent
{
    public FsmState State;   // serialized inline: AgentComponent.Serialize() calls this.State.Serialize(...)
    public int      Extra;
}
```

- The struct gets generated `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` using the **same field codec as a component** (incl. `fixed` buffers), but **no `TYPE_ID`, factory, or `[ModuleInitializer]`**.
- The host's generated codec delegates: `this.State.Serialize(ref writer)`, folds `this.State.GetHash(hash)`, and its `GetSerializedSize()` becomes a runtime expression (`<const> + this.State.GetSerializedSize()`).
- **Must be `unmanaged` and `[StructLayout(Sequential, Pack = 4)]`** — when embedded in a component it becomes part of that component's `MemoryMarshal.Cast` layout, so it follows the same cross-runtime rules as a component ([§7](#7-component-layout--determinism-rules)).
- **Field or `List`/array element.** A `[KlothoSerializableStruct]` is supported as a plain field (any host category, incl. components) and — in the reference categories that support collections ([§6](#6-supported-field-types)) — as a **`List<T>` / `T[]` element**, each element serialized inline via the struct's codec. It is **not** supported as a `Dictionary` key/value, nor as a `struct`-typed property.

---

## 4. What the Generator Emits

For every tagged type the generator produces a `<Type>.g.cs` partial completing:

- **`Serialize(ref SpanWriter)`** / **`Deserialize(ref SpanReader)`** — field-by-field, in `[KlothoOrder]` order.
- **`GetSerializedSize()`** — exact byte count (so release-mode `SpanWriter` needs no bounds check).
- **`GetHash(ulong)`** — for components and other hash-participating types, folding each field via `FPHash` (FNV-1a).
- For `[KlothoComponent]`: the type is registered into the `Frame` heap layout via a `[ModuleInitializer]` (no manual registration).
- For `[KlothoDataAsset]`: an `AssetId` backing field + property, an `int` constructor, and a registrar.
- For `[KlothoSerializableStruct]`: the four codec methods **only** (`Serialize`/`Deserialize`/`GetSerializedSize`/`GetHash`) — **no** `TYPE_ID`, factory, or `[ModuleInitializer]`. A host type with a field of this struct calls these inline (see [§3.1](#31-klothoserializablestruct--reusable-inline-field-bundles)).

It also emits two aggregate files per assembly:

- **`KlothoFactoryRegistration.g.cs`** — registers Entity/Command types into their factories and messages into `MessageSerializer`, so the wire `TYPE_ID` round-trips to the right concrete type. Emitted only when the factory class is defined in the consuming assembly.
- **`KlothoWarmupRegistration.g.cs`** — a warmup hook that touches each generated path so JIT/AOT stubs are realized before the first tick (avoids a first-frame hitch).

> **Field ordering is wire-stable.** `[KlothoOrder(n)]` fixes the serialized order; reorder source freely but never renumber an existing field, or you break compatibility with already-recorded replays and remote peers. Add new fields with higher order numbers.

---

## 5. Field Control Attributes

| Attribute | Effect |
| ---- | ---- |
| `[KlothoOrder(n)]` | Sets the serialization slot. Each value must be unique within the type (else **KLSG002**). |
| `[KlothoIgnore]` | Excludes the field from serialization entirely (transient/cached state). |
| `[KlothoHashIgnore]` | Serializes the field but excludes it from `GetHash` — for values that travel but must not affect desync detection (e.g. a purely cosmetic field). |

---

## 6. Supported Field Types

The generator maps these directly (fixed size in bytes shown where applicable):

- **Integers / bool / byte** — `bool`(1) `byte`(1) `short`/`ushort`(2) `int`/`uint`(4) `long`/`ulong`(8)
- **Fixed-point** — `FP64`(8) `FPVector2`(16) `FPVector3`(24) `FPVector4`(32) `FPQuaternion`(32)
- **ECS handles** — `EntityRef`(8) `DataAssetRef`(4)
- **Composite physics** — `FPRigidBody`(146) `FPCollider`(81)
- **Fixed-width strings** — `FixedString32`(32) `FixedString64`(64) — UTF-8 in a fixed buffer (`Bytes` + `int16` length, always the full constant size). Unlike variable-length `string` they are `unmanaged`, so they are valid **inside components** and as **collection / dictionary** elements.
- **Variable-length** — `string` (UTF-8, length-prefixed), `byte[]`

**Reference categories only** (`[KlothoSerializable]` Entity/Command/Message/Event, *not* components) additionally support **collections** of supported element types: `T[]`, `List<T>`, and `Dictionary<TKey,TValue>`.

> **Components are different.** A `[KlothoComponent]` struct must be **`unmanaged`** — value types only, no `string`/array/`List`/`Dictionary`/managed references. For text in a component use `FixedString32`/`FixedString64` (the fixed-width string entry above — first-class mapped types that serialize as a constant 32/64 bytes); for inline arrays use a fixed-size value struct. This is why the component and `[KlothoSerializable]` planes have different type rules.

Additionally, a **`[KlothoSerializableStruct]` value struct** ([§3.1](#31-klothoserializablestruct--reusable-inline-field-bundles)) is supported as a **direct field** in all three host categories — it serializes inline via its own generated codec. Because it must itself be `unmanaged`, it is also valid inside a `[KlothoComponent]`. In the reference categories it is **also** supported as a **`List<T>` / `T[]` element** (each element serialized inline via the struct's codec; serialized size is `count * <const>`). It is **not** supported as a `Dictionary` key/value, nor as a `struct`-typed property.

`float`/`double` are **deliberately unsupported** — they break cross-platform determinism, so there is no `WriteFloat`/`WriteDouble` and no field mapping for them. An unmapped field type raises **KLSG003** at compile time, and using a `float`/`double` (or a float-backed Unity type) inside deterministic simulation code is additionally flagged by the determinism analyzer (**KLOTHO_DET002/003**, see [§8](#8-diagnostics-reference)). Store fixed-point state (`FP64` and friends) instead; for non-deterministic view/debug payloads, serialize outside the simulation codec.

---

## 7. Component Layout & Determinism Rules

Because components live in the single `Frame` heap and are `MemoryMarshal.Cast` over raw bytes, their layout must be identical on every runtime. The generator's component analyzer enforces this:

- **`[StructLayout(LayoutKind.Sequential, Pack = 4)]` is required** on every `[KlothoComponent]` struct (**KLOTHO_STRUCT_LAYOUT_MISSING**). `Pack=1` risks ARMv7 `SIGBUS`; `Pack=8` is only safe if ARMv7 is dropped.
- **Empty tag struct** (no fields) must add `Size = 1` (**KLOTHO_EMPTY_STRUCT_SIZE**) — Mono returns `SizeOf<T>() = 0` for a field-less sequential struct, which would `DivideByZero` in `MemoryMarshal.Cast`.
- **`IntPtr`/`UIntPtr` are forbidden** (**KLOTHO_INTPTR_FORBIDDEN**) — platform-variable size breaks cross-runtime determinism.
- **Enum fields** should declare an explicit underlying type (`enum X : byte`) (**KLOTHO_ENUM_UNDERLYING**, warning) — otherwise defaults to 4-byte `int`.
- **`bool`(1B)/`char`(2B UTF-16) fields** warn (**KLOTHO_BOOL_FIELD** / **KLOTHO_CHAR_FIELD**) to flag cross-platform size confusion; prefer `byte`/`short` where it matters.
- **128-byte soft cap** — a component over 128 bytes warns (**KLSG_ECS004**); large components bloat every snapshot/`CopyFrom`.
- **Embedded `[KlothoSerializableStruct]` fields** must themselves be `unmanaged` + `[StructLayout(Sequential, Pack = 4)]` (enforced by **KLSG_SS002/003**) — they become part of the host component's `MemoryMarshal.Cast` layout, so the same cross-runtime rules apply.

See [ECS.md §10](ECS.md) for the broader determinism contract.

---

## 8. Diagnostics Reference

The generator reports compile-time errors/warnings so a malformed type fails the build instead of desyncing at runtime.

| Code | Severity | Meaning |
| ---- | ---- | ---- |
| `KLSG001` | Error | `[KlothoSerializable]` class is not `partial`. |
| `KLSG002` | Error | Duplicate `[KlothoOrder]` value within a type. |
| `KLSG003` | Error | Field has an unsupported type. |
| `KLSG004` | Error | `[KlothoSerializable]` base is not `EntityBase`/`CommandBase`/`NetworkMessageBase` (or `SimulationEvent`). |
| `KLSG005` / `KLSG006` | Error | Unsupported collection element / dictionary key-value type. |
| `KLSG007` | Error | Duplicate wire `TYPE_ID` within a category. |
| `KLSG_ECS001` | Error | Duplicate `[KlothoComponent]` type id. |
| `KLSG_ECS002` | Error | `[KlothoComponent]` struct doesn't implement `IComponent`. |
| `KLSG_ECS003` | Error | `[KlothoComponent]` struct is not `unmanaged`. |
| `KLSG_ECS004` | Warning | Component exceeds 128 bytes. |
| `KLSG_ECS005` | Error | `[KlothoComponent]` struct is not `partial`. |
| `KLOTHO_STRUCT_LAYOUT_MISSING` | Error | Missing `[StructLayout(Sequential, Pack=4)]`. |
| `KLOTHO_INTPTR_FORBIDDEN` | Error | `IntPtr`/`UIntPtr` field in a component. |
| `KLOTHO_EMPTY_STRUCT_SIZE` | Error | Field-less tag struct without `Size = 1`. |
| `KLOTHO_ENUM_UNDERLYING` / `KLOTHO_BOOL_FIELD` / `KLOTHO_CHAR_FIELD` | Warning | Size-ambiguous field types. |
| `KLSG_DA001`–`KLSG_DA006` | Error/Warn | DataAsset rules (partial, `IDataAsset`, int ctor, ambiguous ctor, duplicate type id, mixed user/generated members) — see [DataAsset.md §12](DataAsset.md). |
| `KLSG_SS001` | Error | `[KlothoSerializableStruct]` struct is not `partial`. |
| `KLSG_SS002` | Error | `[KlothoSerializableStruct]` struct is not `unmanaged`. |
| `KLSG_SS003` | Error | `[KlothoSerializableStruct]` struct missing `[StructLayout(Sequential, Pack = 4)]`. |
| `KLOTHO_DET002` | Warning | `float`/`double` used in a deterministic context. |
| `KLOTHO_DET003` | Warning | Non-deterministic API/type in a deterministic context (`System.Math` / `System.Random` / `System.DateTime` / `UnityEngine.Mathf` / `UnityEngine.Random`, or a float-backed Unity value type: `Vector2/3/4` · `Quaternion` · `Matrix4x4`). |
| `KLOTHO_DET004` | Warning | `UnityEngine.Time` (wall-clock) read in a deterministic context — use the fixed tick/`dt` from `Frame`. |

The `KLOTHO_DET*` codes come from a separate **`DeterminismAnalyzer`** (a Roslyn `DiagnosticAnalyzer`, not the source generator) that ships in the same prebuilt analyzer DLL. It scans only **deterministic contexts** — types implementing a deterministic interface / base (e.g. `ISystem`, `ISimulation`) or any method taking a `ref Frame` — and stays silent in view/Unity code. Assemblies whose name contains `Test` or `Editor` are skipped wholesale (tolerance comparisons and probes legitimately use `float`). The `FP64` conversion boundary (`FromFloat`/`ToFloat`/…) is exempt so you can cross into fixed-point without a warning. There is no `KLOTHO_DET001`; the series starts at `DET002`.

---

## 9. Building & Inspecting the Generator

The generator ships **prebuilt** as `com.xpturn.klotho/Plugins/Analyzers/KlothoGenerator.dll` (so consumers don't compile it). When you change generator source under `Tools/KlothoGenerator/`, rebuild and redeploy the DLL:

```bash
Tools/gen.sh                 # dotnet build -c Release, then copies the DLL into Plugins/Analyzers/
Tools/pack-godot-addon.sh    # Godot consumers: copies that same DLL into addons/klotho/Analyzers/
```

Godot projects get the generator through the addon rather than the Unity package, and the addon's
`Klotho.props` references it as `<Analyzer Include="…/Analyzers/KlothoGenerator.dll" />`. The two
copies are one build — pack after `gen.sh`, or the addon keeps generating with the older DLL.

For debugging the **output**, the generator can also write each `.g.cs` to `Tools/Generated/<AssemblyName>/` in your project root. This dump is **opt-in and off by default**: it is written only when `Tools/Generated/` already exists, and the generator never creates that directory. So:

```bash
mkdir -p Tools/Generated   # turn the dump on for this project
rm -rf Tools/Generated     # turn it off again
```

**Turning it on under a running IDE gives a partial dump.** The generator rewrites a type only when
that type's inputs changed, so types the IDE has already cached are not re-emitted and the folder
fills in one edit at a time. That looks like the feature is broken. Restart the IDE, or run a clean
`dotnet build`, for the complete dump. (Turning it *off* needs neither — removing the directory
stops the writes immediately.)

That caching is the same property that keeps an IDE responsive: per-type generation is keyed on the
type's own inputs plus two strings (project root and assembly name), **not** on the `Compilation`
object, which is a new instance after every keystroke. Editing one file therefore re-emits the types
you touched rather than every tagged type in the assembly. Unity does not benefit — it recompiles a
whole assembly at a time regardless — and the generated code is identical either way.

When it is on, a file whose content has not changed is left alone (no rewritten timestamp), so the dump does not churn in version control. The dump is a debug aid, never a build input — nothing references those files, and deleting them changes no build. It is skipped entirely for sources under PackageCache, and for any project whose paths carry no `Assets/` or `Packages/` segment (a plain .NET or Godot project, for instance). Without it, `dotnet build -p:EmitCompilerGeneratedFiles=true` gives you the same generated sources under `obj/`.

> The generator is a `netstandard2.0` Roslyn `IIncrementalGenerator`. Generator source uses `Microsoft.CodeAnalysis`; it is build-time only and never shipped to the runtime. The same DLL also hosts the `DeterminismAnalyzer` (`DiagnosticAnalyzer`) that emits the `KLOTHO_DET*` warnings ([§8](#8-diagnostics-reference)).

---

## 10. Manual Serialization Escape Hatch

If a type already declares its own `Serialize`/`Deserialize`/`GetSerializedSize` (the generator detects this as "manual serialization"), the generator won't overwrite them — you keep full control for an exotic encoding. Use this sparingly: hand-written codecs must keep `GetSerializedSize()` exact (release builds skip the bounds check) and must stay byte-identical across peers, which is exactly the discipline the generator exists to enforce for you.

---

## 11. Worked Example — a command end to end

```csharp
using System.Runtime.InteropServices;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

// 1) a networked command — generator emits Serialize/Deserialize/GetSerializedSize + factory registration
[KlothoSerializable(typeId: 11)]
public partial class CastSkillCommand : CommandBase
{
    [KlothoOrder(0)] public int       SkillId;
    [KlothoOrder(1)] public EntityRef Caster;
    [KlothoOrder(2)] public FPVector3 Target;
    [KlothoIgnore]   public float      DebugLatencyMs;   // never serialized — local-only
}

// 2) a component the command's system mutates — unmanaged, Pack=4, generator emits codec + GetHash
[KlothoComponent(120)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct CooldownComponent : IComponent
{
    [KlothoOrder(0)] public int  SkillId;
    [KlothoOrder(1)] public int  RemainingTicks;
    [KlothoHashIgnore, KlothoOrder(2)] public int LastUiFlash;   // travels but excluded from desync hash
}
```

Both types get their full binary codec at compile time. `CastSkillCommand` round-trips over the wire through its generated `CommandFactory` registration; `CooldownComponent` participates in the `Frame` snapshot and hash automatically. You wrote only fields and ordering — no `SpanWriter` calls, no reflection, no GC.
