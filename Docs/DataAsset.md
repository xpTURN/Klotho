# DataAsset Guide — Authoring Your Own Data Assets

> Audience: game developers defining their own configuration data on top of Klotho.
> Goal: how to define, author, build, register, and look up a `DataAsset` end-to-end.
>
> Related: [FEATURES.md](FEATURES.md) (DataAsset module overview) · [Samples/Brawler.C.DataAssets.md](Samples/Brawler.C.DataAssets.md) (a full real-world field catalog) · [GameDevAPI.md](GameDevAPI.md)

---

## 1. What a DataAsset Is

A **DataAsset** is Klotho's unit of **immutable, deterministic, globally-shared configuration data** — character stats, physics parameters, item configs, game rules, and so on. It is the data every peer reads during simulation ticks.

Key properties:

- **Immutable at runtime** — assets are loaded once and never mutated during simulation.
- **Deterministic** — every peer must load **byte-identical** asset data, or the simulation diverges. This is the single most important rule (see [§10](#10-determinism-rules)).
- **Plain data class (POCO), not an engine asset object** — a DataAsset only implements `IDataAsset`. It is *not* a Unity `ScriptableObject` (`.asset`) or a Godot `Resource` (`.tres`); there is no editor inspector to edit it in. Values are authored in **JSON**, converted to an engine-neutral `.bytes` blob, and loaded at runtime.
- **Engine-neutral core** — the asset definition, JSON authoring, binary format, and registry API are pure C# and identical on every host engine. Only the editor *conversion command* and the way you *read the `.bytes` file* differ per engine (see [§8](#8-json--bytes-build-pipeline)–[§9](#9-loading--building-the-registry)).
- **Looked up via a registry** — at runtime you read assets through `Frame.AssetRegistry` (an `IDataAssetRegistry`), never via direct references.

> When to use a DataAsset vs. a Component: a **Component** is per-entity mutable simulation state; a **DataAsset** is read-only shared configuration that the same simulation reads but never changes.

---

## 2. Anatomy of a Definition

```csharp
using xpTURN.Klotho.ECS;            // IDataAsset, KlothoDataAsset
using xpTURN.Klotho.Serialization;  // KlothoOrder, KlothoIgnore
using xpTURN.Klotho.Deterministic.Math; // FP64, FPVector3, ...

namespace MyGame.DataAssets
{
    [KlothoDataAsset(200, AssetId = 5001, Key = "MyRules")]
    public partial class MyRulesAsset : IDataAsset
    {
        [KlothoOrder(0)] public int  MatchDurationSec = 180;
        [KlothoOrder(1)] public FP64 GravityAccel     = FP64.FromInt(20);
        [KlothoOrder(2)] public FPVector3[] SpawnPoints = { };

        [KlothoIgnore]   public int  EditorOnlyScratch; // not serialized
    }
}
```

Four required ingredients:

| Ingredient | Why |
|---|---|
| `[KlothoDataAsset(typeId, ...)]` | Marks the class for code generation and assigns the wire-stable **TypeId**. |
| `partial class` | The source generator emits the other half of the class (serialization, ctor, `AssetId`). |
| `: IDataAsset` | The marker interface. Exposes `int AssetId { get; }`. |
| `[KlothoOrder(N)]` on each field | Fixes the **serialization order**. Orders must be unique within the class and stable across builds. |

The generator emits, for the partial class above:

- `public const int TYPE_ID` — the wire-format type id.
- `private readonly int _assetId;` + `public int AssetId => _assetId;`
- `public MyRulesAsset(int assetId)` — always emitted.
- `public MyRulesAsset() : this(5001)` — emitted **only** when `AssetId` is set on the attribute.
- `GetSerializedSize()`, `Serialize(ref SpanWriter)`, `static Deserialize(ref SpanReader)`.
- A `[ModuleInitializer]` registrar that registers the type with `DataAssetTypeRegistry`.

You author **only** the attribute and the fields. Do not hand-write any of the generated members (see diagnostic [`KLSG_DA006`](#12-analyzer-diagnostics)).

---

## 3. Step-by-Step: Your First DataAsset

1. **Define the class** in your game assembly (the assembly must be wired to the Klotho generator, like the sample assemblies). Add the attribute, `partial`, `: IDataAsset`, and `[KlothoOrder]`-tagged fields — see [§2](#2-anatomy-of-a-definition).
2. **Pick your ids** — choose a `typeId` unique across all your DataAssets, and decide single- vs. multi-instance ([§4](#4-single-instance-vs-multi-instance)). Allocate `AssetId`(s) following a range convention ([§5](#5-id-planes-typeid--assetid--key)).
3. **Author the values in JSON** ([§7](#7-authoring-values-in-json)).
4. **Convert JSON → `.bytes`** ([§8](#8-json--bytes-build-pipeline)).
5. **Load and build the registry** at startup ([§9](#9-loading--building-the-registry)).
6. **Look it up** during simulation via `Frame.AssetRegistry` ([§9](#9-loading--building-the-registry)).

---

## 4. Single-Instance vs. Multi-Instance

There are two flavours, chosen by whether you set `AssetId` on the attribute.

### Single-instance — set `AssetId` (and optionally `Key`)

For an asset with exactly one runtime instance (game rules, global physics). The generator emits a parameterless ctor, so lookup needs no magic-number literal:

```csharp
[KlothoDataAsset(200, AssetId = 5001, Key = "MyRules")]
public partial class MyRulesAsset : IDataAsset { /* ... */ }
```

```csharp
var rules  = frame.AssetRegistry.Get<MyRulesAsset>();              // resolves by attribute AssetId
var rules2 = frame.AssetRegistry.GetByKey<MyRulesAsset>("MyRules"); // resolves by Key
```

### Multi-instance — omit `AssetId`

For the same class with many registered instances (one per character class, per difficulty, …). Omit `AssetId`/`Key`; the generator emits **only** `ctor(int)` (no parameterless ctor — this prevents silently registering a sentinel `AssetId = 0`). The id literal stays at the call site, which is the intended domain fan-out:

```csharp
[KlothoDataAsset(201)]   // TypeId only
public partial class CharacterStatsAsset : IDataAsset
{
    [KlothoOrder(0)] public FP64 MoveSpeed;
    // ...
}
```

```csharp
// e.g. Warrior=1100, Mage=1101, ...
var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + classIndex);
```

---

## 5. ID Planes: TypeId / AssetId / Key

Three independent identifiers — do not conflate them:

| Identifier | Where | Meaning | Stability |
|---|---|---|---|
| **TypeId** | positional arg of `[KlothoDataAsset(typeId)]` | Wire-format **type** discriminator. Dispatches `Deserialize` in `DataAssetTypeRegistry`. | Must stay **stable across binary-compatible builds**. Unique per DataAsset type. |
| **AssetId** | `AssetId =` named-arg (single-instance) or `ctor(int)` (multi-instance) | Runtime **instance** id used for registry lookup. | Stable per instance; identical on every peer. |
| **Key** | `Key =` named-arg (optional) | Human-readable string handle for `GetByKey<T>`. Lookup key is `(Type, string)`. | Optional; case-sensitive. |

The TypeId plane is **separate** from the `[KlothoComponent(N)]` and `[KlothoSerializable(N)]` id planes — each attribute has its own namespace, so a TypeId may reuse a number used by a Component without collision. Within DataAssets, however, TypeIds must be unique (duplicate → [`KLSG_DA005`](#12-analyzer-diagnostics)).

A common convention (from the Brawler sample) is to encode category in the leading digits of `AssetId` and instance index in the trailing digits, e.g. `11xx` = character, `12xx` = skill. See [Brawler.C.DataAssets.md §C-1](Samples/Brawler.C.DataAssets.md#c-1-assetid-allocation-rules).

---

## 6. Supported Field Types

Use only these types for `[KlothoOrder]` fields. Each may also be used as an **array** (`T[]`) or a **`List<T>`**, and `Dictionary<K,V>` is supported when both `K` and `V` are mapped types. A null collection is written as empty and reads back as an empty one — initialize collection fields where you declare them (`= System.Array.Empty<int>()`, `= new List<int>()`). An asset registered straight from JSON (`LoadFromJsonAndRegister`) never goes through the binary codec, so an uninitialized field the JSON leaves out stays null there. See [Serialization.md §6](Serialization.md#6-supported-field-types).

| Category | Types |
|---|---|
| Integers | `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` |
| Boolean | `bool` |
| Strings / blobs | `string`, `byte[]` |
| Deterministic math | `FP64`, `FPVector2`, `FPVector3`, `FPVector4`, `FPQuaternion` |
| Klotho refs / physics | `DataAssetRef`, `FPCollider` |

> ⚠️ **Never use `float` or `double`.** They are intentionally **not** supported — floating-point breaks cross-platform determinism. Use `FP64` (fixed-point) instead. An unsupported field type raises a generator diagnostic.

> ⚠️ **Avoid runtime-state types in a DataAsset.** A few types are technically accepted (the generator reuses a type-mapping table shared with Component/Command serialization) but are *not* meaningful as config:
> - **`EntityRef`** — a runtime instance handle (`Index` + `Version` assigned when an entity spawns). There is no entity to point to at config-authoring time, and the value isn't stable. To reference *other config*, use `DataAssetRef` (an int id, authorable in JSON); to reference *entities*, resolve them at runtime and store the `EntityRef` on a Component.
> - **`FPRigidBody`** — primarily a runtime physics-state struct: its `velocity` / `force` / `angularVelocity` / `torque` fields are integrated every tick and are just zeros at config time. Author the *static* parameters you actually want (mass, friction, restitution, damping, …) as individual `FP64` fields instead — as the Brawler `CharacterStatsAsset` does with `Mass` / `Friction`.

Construct fixed-point defaults with the `FP64` helpers, e.g. `FP64.Zero`, `FP64.One`, `FP64.FromInt(20)`, `FP64.FromDouble(0.5)`.

Use `[KlothoIgnore]` on any field that should not be serialized (editor-only scratch values, caches).

---

## 7. Authoring Values in JSON

Asset values are authored as a JSON **array** of instances. Each entry carries a `$type` tag (`Namespace.ClassName, AssemblyName`), an `AssetId`, and the field values:

```json
[
    {
        "$type": "MyGame.DataAssets.MyRulesAsset, MyGame",
        "AssetId": 5001,
        "MatchDurationSec": 180,
        "GravityAccel": 20.0,
        "SpawnPoints": [
            { "x": -4.0, "y": 0.0, "z": -4.0 },
            { "x":  4.0, "y": 0.0, "z": -4.0 }
        ]
    },
    {
        "$type": "MyGame.DataAssets.CharacterStatsAsset, MyGame",
        "AssetId": 1100,
        "MoveSpeed": 5.0
    }
]
```

Notes:

- `$type` is `FullTypeName, AssemblyName` — the assembly-qualified name the `DataAssetSerializationBinder` resolves.
- `AssetId` in JSON is passed to the generated `ctor(int)` on deserialization. For multi-instance assets this is the **only** place the instance id is set.
- `FP64` fields are written as plain JSON numbers (e.g. `20.0`); `FPVector*` as `{ "x":…, "y":…, "z":… }`. Conversion is handled by the converters in the `xpTURN.Klotho.DataAsset.Json` assembly (`FP64JsonConverter`, `FPVector2/3JsonConverter`, `DataAssetRefJsonConverter`, …), built on Newtonsoft.Json.

---

## 8. JSON → .bytes Build Pipeline

The runtime loads a binary `.bytes` blob, not JSON. Every engine converts the same way at the core — `DataAssetJsonConverter.ConvertMixedJsonToBytes(json)` → write the resulting bytes next to the `.json`. Only the editor command that triggers it differs:

| Engine | Convert command |
|---|---|
| **Unity** | Select the `.json` `TextAsset` in the Project window → **`Tools > Klotho > Convert > DataAsset JsonToBytes`**. Writes a `.bytes` `TextAsset` beside it. |
| **Godot** | Right-click the `.json` in the FileSystem dock → **`Convert DataAsset JSON -> bytes`** (Klotho addon context menu). Writes a `.bytes` file beside it. |

Both produce an identical `.bytes` payload. Its binary format has an 8-byte header (`Magic 0x58504441 "XPDA"` + `Version`), then a count, then per-entry `TypeId` + `size` + payload for mixed collections. The reader validates the header and skips unknown TypeIds.

> If you have no editor available, call `DataAssetJsonConverter.ConvertMixedJsonToBytes(json)` / `DataAssetWriter.SaveToFile(path, bytes)` directly from a build script — neither depends on any engine.

> The JSON source is an **editing artifact only**. It is never sent over the network; every peer is expected to already hold the same `.bytes`.

---

## 9. Loading & Building the Registry

At startup, obtain the `.bytes` as a `byte[]`, load the assets, register them, and build the immutable registry. Then inject it into the Klotho flow so it becomes `Frame.AssetRegistry`. Only step 0 (reading the file bytes) is engine-specific; everything after is pure C#:

```csharp
// 0. Read the .bytes blob — engine-specific
//    Unity:  byte[] data = _dataAsset.bytes;                                   // TextAsset assigned in Inspector
//    Godot:  byte[] data = Godot.FileAccess.GetFileAsBytes("res://Data/MyAssets.bytes");
//    Plain:  byte[] data = System.IO.File.ReadAllBytes(path);

// 1. Load the mixed collection (engine-neutral)
List<IDataAsset> assets = DataAssetReader.LoadMixedCollectionFromBytes(data);

// 2. Build the registry
IDataAssetRegistryBuilder builder = new DataAssetRegistry();
builder.RegisterRange(assets);
IDataAssetRegistry registry = builder.Build();

// 3. Inject — forwarded to KlothoSession as Frame.AssetRegistry
_flow = new KlothoSessionFlow(new KlothoFlowSetup {
    AssetRegistry = registry,
    // ...
});
```

> `LoadMixedCollectionFromBytes` also has a `string path` overload (uses `System.IO.File`), handy for plain .NET hosts. On Godot, prefer `FileAccess.GetFileAsBytes` so it also works from inside an exported `.pck`, where `System.IO` cannot read `res://`.

Convenience extensions on `IDataAssetRegistryBuilder` combine load + register:

```csharp
builder.LoadMixedAndRegister(data);                      // mixed collection (byte[] or string path)
builder.LoadCollectionAndRegister<MyRulesAsset>(data);   // homogeneous collection
builder.LoadAndRegister<MyRulesAsset>(data);             // single asset
```

### Runtime lookup

Inside systems and callbacks, read through the registry (`IDataAssetRegistry`):

```csharp
// Single-instance
var rules = frame.AssetRegistry.Get<MyRulesAsset>();                 // by attribute AssetId
var rules = frame.AssetRegistry.GetByKey<MyRulesAsset>("MyRules");   // by Key

// Multi-instance
var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + i);  // by explicit id
if (frame.AssetRegistry.TryGet<CharacterStatsAsset>(id, out var s)) { /* ... */ }
```

`Get<T>()` throws `InvalidOperationException` if the type's attribute omits `AssetId` or no instance is registered for that id. Use the `TryGet…` overloads to avoid exceptions. For component fields that reference assets, store a `DataAssetRef` (`new DataAssetRef(id)`; `IsValid` is `Id != 0`) and resolve via `Get<T>(DataAssetRef)`.

> ⚠️ **`T` is your responsibility, not the id's.** The registry is a single flat `Dictionary<int, IDataAsset>` keyed by `AssetId` only — `DataAssetRef` (and a bare id) carry **no type information**. `T` is supplied at the call site and used solely to cast the result. There is **no compile-time guarantee** that a given id/`DataAssetRef` actually points to a `T`: on a type mismatch `Get<T>` throws `InvalidCastException` and `TryGet<T>` silently returns `false`. So `DataAssetRef` only certifies "this int is an AssetId", not "this id is a `SkillConfigAsset`" — pass the correct `T` yourself.

---

## 10. Determinism Rules

- **Byte-identical across peers.** The `.bytes` payload every peer loads must be identical. Build once and distribute the same blob; never let peers regenerate it independently from JSON that might differ.
- **Immutable after load.** Treat assets as read-only during simulation. Never mutate asset fields in a system.
- **No `float`/`double`.** Use `FP64` and the `FPVector*`/`FPQuaternion` types only ([§6](#6-supported-field-types)).
- **Stable TypeId & order.** Once shipped, do not renumber `TypeId` or change `[KlothoOrder]` values for an existing field — that breaks wire compatibility with already-built `.bytes`.

---

## 11. Evolving an Asset Schema

When adding fields to an existing asset:

- Append new fields with **new, higher `[KlothoOrder]` values**; do not reuse or reorder existing orders.
- Re-author the JSON and **re-convert to `.bytes`**, then redistribute to all peers together — mismatched schema/data across peers diverges.
- Removing a field changes the wire layout; coordinate a rebuild of all `.bytes` and ship them in lockstep.

---

## 12. Analyzer Diagnostics

The generator/analyzer surfaces these at compile time (category `KlothoGenerator.DataAsset`):

| Id | Title | Fix |
|---|---|---|
| `KLSG_DA001` | Missing partial keyword | Declare the class `partial`. |
| `KLSG_DA002` | Missing `IDataAsset` | Add `: IDataAsset`. |
| `KLSG_DA003` | Missing int constructor | Ensure a `ctor(int assetId)` exists (normally generated; the error means generation is blocked). |
| `KLSG_DA004` | Ambiguous constructor | Multiple public ctors confuse Newtonsoft.Json — keep a single int ctor. |
| `KLSG_DA005` | Duplicate DataAsset TypeId | Two classes share a `[KlothoDataAsset(N)]` TypeId — assign distinct ids. |
| `KLSG_DA006` | Mixed user/generated members | You hand-wrote some generated members but not others — author **both or neither**. |

Plus field-level diagnostics: duplicate `[KlothoOrder]`, unsupported field type, unsupported collection element, unsupported dictionary key/value, and **KLSG008** (warning) — a collection field with no initializer.

---

## 13. API Reference (quick)

**Interfaces / types** (`xpTURN.Klotho.ECS`)

```csharp
public interface IDataAsset { int AssetId { get; } }

public readonly struct DataAssetRef {
    public readonly int Id;
    public DataAssetRef(int id);
    public bool IsValid => Id != 0;
    public static readonly DataAssetRef Invalid; // default
}

public interface IDataAssetRegistry {
    T    Get<T>(int id)            where T : IDataAsset;
    bool TryGet<T>(int id, out T)  where T : IDataAsset;
    T    Get<T>(DataAssetRef r)    where T : IDataAsset;
    bool TryGet<T>(DataAssetRef r, out T) where T : IDataAsset;
    T    Get<T>()                  where T : IDataAsset; // by attribute AssetId
    bool TryGet<T>(out T)          where T : IDataAsset;
    T    GetByKey<T>(string key)   where T : IDataAsset;
    bool TryGetByKey<T>(string key, out T) where T : IDataAsset;
}

public interface IDataAssetRegistryBuilder : IDataAssetRegistry {
    void Register(IDataAsset asset);
    void RegisterRange(IReadOnlyList<IDataAsset> assets);
    IDataAssetRegistry Build();
}
```

**Reader / Writer** (`xpTURN.Klotho.ECS`)

```csharp
DataAssetReader.LoadFromBytes<T>(byte[]);
DataAssetReader.LoadCollectionFromBytes<T>(byte[]);
DataAssetReader.LoadMixedCollectionFromBytes(byte[] | string | ReadOnlySpan<byte>);

DataAssetWriter.SerializeToBytes<T>(T);
DataAssetWriter.SerializeCollectionToBytes<T>(IReadOnlyList<T>);
DataAssetWriter.SerializeMixedCollectionToBytes(IReadOnlyList<IDataAssetSerializable>);
DataAssetWriter.SaveMixedCollectionToFile(string path, IReadOnlyList<IDataAssetSerializable>);
```

**Attributes**

```csharp
[KlothoDataAsset(int typeId)]                 // xpTURN.Klotho.ECS — + AssetId, Key named-args
[KlothoOrder(int order)]                      // xpTURN.Klotho.Serialization
[KlothoIgnore]                                // xpTURN.Klotho.Serialization
```

---

For a complete worked example with nine asset classes, JSON authoring, and the full registry build flow, study the Brawler sample: [Samples/Brawler.C.DataAssets.md](Samples/Brawler.C.DataAssets.md).
