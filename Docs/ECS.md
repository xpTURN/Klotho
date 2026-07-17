# ECS — Entities, Components, Systems

Klotho's simulation state lives in a compact, deterministic ECS. **Entities** are lightweight handles, **Components** are unmanaged structs holding all simulation state, and **Systems** are the per-tick logic that reads and mutates them. The whole world is one `Frame` — a single `byte[]` heap — so a snapshot is one `Buffer.BlockCopy` and a hash is a deterministic walk over the same bytes. This is what makes rollback cheap and cross-peer determinism exact.

> Audience: game developers building simulation logic on top of Klotho.
> Goal: define components, write systems, query entities, and understand how the Frame snapshots/hashes for rollback.
>
> Related: [FEATURES.md](FEATURES.md) (ECS module index) · [Specification.md](Specification.md) §7 (formal state layout) · [ECSMemoryOptimization.md](ECSMemoryOptimization.md) (frame-heap memory tuning) · [DataAsset.md](DataAsset.md) (read-only shared config) · [HFSM.md](HFSM.md) (AI on top of ECS) · [Samples/Brawler.B.Systems.md](Samples/Brawler.B.Systems.md) (real systems)

---

## 1. The Three Concepts

| Concept | Klotho type | What it is |
| ---- | ---- | ---- |
| **Entity** | `EntityRef` (8 bytes) | A generational handle — `Index` + `Version`. Holds no data; just identifies a slot. |
| **Component** | `unmanaged struct : IComponent` | Per-entity mutable simulation state. All fields are deterministic (`FP64` / integer / bool / fixed buffers). |
| **System** | `class : ISystem` | Per-tick logic. `Update(ref Frame)` queries entities and mutates their components. |

Everything is owned by a **`Frame`** — the ECS world state for one tick:

- `EntityManager Entities` — entity lifecycle (generational index + free-list reuse, fixed capacity)
- a single `byte[]` heap holding **every** component storage back-to-back
- `EntityPrototypeRegistry Prototypes` — data-driven entity spawn templates
- `IDataAssetRegistry AssetRegistry` — read-only shared config (see [DataAsset.md](DataAsset.md))
- `int Tick` · `int DeltaTimeMs`

> Why one heap: `Frame.CopyFrom` is a single `Buffer.BlockCopy` of the whole heap, and `CalculateHash` walks the same bytes in a fixed type order. Snapshot, restore, and hash are all O(state size) with no per-component allocation — the foundation rollback stands on.

---

## 2. File Layout

```text
com.xpturn.klotho/Runtime/ECS/
├── Core/
│   ├── Frame.cs                  # the ECS world: heap + entities + lookups + hash/snapshot/serialize
│   ├── EntityRef.cs              # 8-byte generational handle
│   ├── EntityManager.cs          # entity lifecycle (generation index + free-list)
│   ├── IComponent.cs             # component marker interface (source-gen fills the body)
│   ├── ComponentStorageFlat.cs   # sparse-set view over a heap slice (Add/Remove/Has/Get)
│   ├── ComponentStorageRegistry.cs  # assembly-scan type registration + heap layout
│   ├── StorageLayout.cs          # per-type offsets into the heap
│   ├── IEntityPrototype.cs / EntityPrototypeRegistry.cs   # data-driven spawn templates
│   └── FixedString.cs            # FixedString32 / FixedString64 (unmanaged string fields)
├── Attributes/
│   ├── KlothoComponentAttribute.cs            # [KlothoComponent(typeId)]
│   ├── KlothoSingletonComponentAttribute.cs   # [KlothoSingletonComponent]
│   └── FrameDataAttribute.cs                  # [FrameData]
├── System/
│   ├── ISystem.cs                # ISystem + lifecycle/command/sync interfaces + SystemPhase
│   ├── ISignal.cs                # component add/remove signal interfaces
│   └── SystemRunner.cs           # phase-ordered registration & dispatch
├── Snapshot/
│   └── FrameRingBuffer.cs        # pre-tick Frame snapshots for rollback
├── Components/                   # built-in components (Transform, Owner, RandomSeed, ...)
├── Systems/                      # built-in systems (EventSystem, ...)
├── DataAsset/                    # see DataAsset.md
├── FSM/                          # see HFSM.md
└── EcsSimulation.cs              # ISimulation impl: owns Frame + SystemRunner + FrameRingBuffer
```

---

## 3. Defining a Component

A component is an `unmanaged partial struct` implementing `IComponent`, tagged with `[KlothoComponent(typeId)]`. You write **only the fields** — the source generator emits `GetHash` / `Serialize` / `Deserialize` / `GetSerializedSize` into the `partial`.

```csharp
using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace MyGame
{
    [KlothoComponent(100)]                          // 16-bit type id, UserMinId = 100
    [StructLayout(LayoutKind.Sequential, Pack = 4)] // 4-byte aligned, matches the heap layout
    public partial struct HealthComponent : IComponent
    {
        public int  Current;
        public int  Max;
        public FP64 RegenPerSec;   // deterministic fixed-point — never float/double
    }
}
```

Rules:

- **`unmanaged` only** — value-type fields all the way down. No `class`, `string`, array, or managed reference. For text use `FixedString32` / `FixedString64`; for inline buffers use fixed-size value structs. To group related fields into a reusable bundle, use a **`[KlothoSerializableStruct]`** value struct as a field — it serializes inline (see [Serialization.md §3.1](Serialization.md#31-klothoserializablestruct--reusable-inline-field-bundles)).
- **No floating point** — `float`/`double` break determinism. Use `FP64` and the `FPVector*` / `FPQuaternion` types.
- **`[KlothoComponent(id)]`** — the id is a *component* type discriminator on its own 16-bit id plane. User ids start at `KlothoComponentAttribute.UserMinId` (**100**); ids below that are reserved for built-ins (e.g. `TransformComponent` = 1). This id plane is independent of `[KlothoSerializable]` and `[KlothoDataAsset]` ids — id 100 here does not collide with id 100 there.
- **`partial`** — required so the generator can complete the type.

### Singleton components

Mark a type `[KlothoSingletonComponent]` when exactly one entity in the frame should ever carry it (global rules, match state, the RNG seed). `Frame.Add<T>` throws if a second carrier appears; read it without an `EntityRef`:

```csharp
ref var rules = ref frame.GetSingleton<MyRulesComponent>();          // throws if absent
ref readonly var ro = ref frame.GetReadOnlySingleton<MyRulesComponent>();
if (frame.TryGetSingleton<MyRulesComponent>(out var carrier)) { ... } // safe when it may be absent
```

---

## 4. Entities & Components on the Frame

```csharp
// create / destroy
EntityRef e = frame.CreateEntity();
frame.DestroyEntity(e);                       // removes all its components, recycles the slot

// add / read / mutate / remove components
frame.Add(e, new HealthComponent { Current = 100, Max = 100 });
bool alive = frame.Has<HealthComponent>(e);
ref var hp = ref frame.Get<HealthComponent>(e);          // mutable ref — write directly
hp.Current -= 10;
ref readonly var roHp = ref frame.GetReadOnly<HealthComponent>(e);
frame.Remove<HealthComponent>(e);
```

`Get<T>` returns a `ref` directly into the heap — assign to its fields to mutate in place (no write-back call). `EntityRef` is a generational handle: when a slot is reused the `Version` bumps, so a stale `EntityRef` fails `Entities.IsAlive` / filter checks instead of touching a recycled entity.

**Data-driven spawning** — register an `IEntityPrototype` and create by id, or pass a typed prototype to carry spawn data:

```csharp
EntityRef a = frame.CreateEntity(prototypeId);             // via EntityPrototypeRegistry
EntityRef b = frame.CreateEntity(in mySpawnPrototype);     // typed; prototype.Apply(frame, entity)
```

---

## 5. Querying with Filters

`Filter<T1..T5>` iterates every live entity that has **all** listed components; `FilterWithout<…, TExclude>` adds one exclusion. Filters are **`ref struct`** (stack-only, zero-GC) and iterate the **smallest** of the requested storages first, checking `Has` on the rest — so order your most-selective component first matters less than you'd think, the engine already picks the smallest.

```csharp
public class DamageOverTimeSystem : ISystem
{
    public void Update(ref Frame frame)
    {
        var filter = frame.Filter<HealthComponent, PoisonComponent>();
        while (filter.Next(out var entity))
        {
            ref var hp = ref frame.Get<HealthComponent>(entity);
            ref readonly var poison = ref frame.GetReadOnly<PoisonComponent>(entity);
            hp.Current -= poison.DamagePerTick;
        }
    }
}

// "all movable entities that are NOT stunned"
var f = frame.FilterWithout<TransformComponent, VelocityComponent, StunnedComponent>();
while (f.Next(out var e)) { /* ... */ }
```

The iteration uses the `while (filter.Next(out var entity))` pattern — there is no `foreach`/`IEnumerable` (that would allocate). `filter.Count` gives the iteration bound (the smallest storage's count).

> **Mutating during iteration:** writing component *fields* via `Get<T>` is fine. Adding/removing components or destroying entities **of the type you're iterating** mutates the dense array under the cursor (swap-back) — collect the targets in a list and apply after the loop, or iterate a different component's storage.

---

## 6. Writing Systems

A system is a plain class implementing `ISystem`. Register it on the simulation with a **phase**; within a phase, systems run in **registration order**.

```csharp
public enum SystemPhase { PreUpdate, Update, PostUpdate, LateUpdate }
```

```csharp
sim.AddSystem(new InputCommandSystem(), SystemPhase.PreUpdate);
sim.AddSystem(new MovementSystem(),     SystemPhase.Update);
sim.AddSystem(new CombatSystem(),       SystemPhase.Update);
sim.AddSystem(new CleanupSystem(),      SystemPhase.PostUpdate);
```

Each tick `EcsSimulation.Tick(commands)` runs: command systems (PreUpdate) → all `ISystem.Update` in phase+registration order. Optional interfaces let one class hook more than the per-tick update:

| Interface | Method | Called when |
| ---- | ---- | ---- |
| `ISystem` | `Update(ref Frame)` | Every tick, in phase order. |
| `IInitSystem` | `OnInit(ref Frame)` | Once at `Initialize()`. |
| `IDestroySystem` | `OnDestroy(ref Frame)` | On simulation teardown. |
| `ICommandSystem` | `OnCommand(ref Frame, ICommand)` | Per input command, before Update systems. |
| `IEntityCreatedSystem` / `IEntityDestroyedSystem` | `OnEntityCreated/Destroyed(ref Frame, EntityRef)` | On entity lifecycle events. |
| `ISignalOnComponentAdded<T>` / `ISignalOnComponentRemoved<T>` | `OnAdded` / `OnRemoved` | When component `T` is added/removed. |
| `ISyncEventSystem` | `EmitSyncEvents(ref Frame)` | On synced (verified) ticks only — for events that must fire once on the confirmed timeline, not on every predicted resim. |

A class may implement several of these at once. To reach another registered system's secondary interface from a callback boundary (instead of a process-wide static), look it up:

```csharp
var physics = sim.GetSystem<IFPPhysicsWorldProvider>();   // first match in registration order, or null
if (sim.TryGetSystem<IFooService>(out var foo)) { ... }
sim.GetSystems<IBar>(buffer);                             // append all matches into a caller-owned list
```

> All system work runs against `ref Frame` and must be **deterministic**: no wall-clock time, no `Random` (use `RandomSeedComponent` / `DeterministicRandom`), no float, no iteration over unordered managed collections. Anything non-deterministic belongs in the view layer, not a system.

---

## 7. Snapshots, Rollback & Hashing

The whole point of the single-heap design is cheap state capture. `EcsSimulation` wraps a `FrameRingBuffer` of pre-tick snapshots:

- **`SaveSnapshot()`** — the engine calls this each tick; `FrameRingBuffer.SaveFrame(tick, frame)` does `dest.CopyFrom(frame)` (one `BlockCopy`) into slot `tick % capacity`. Capacity is the `maxRollbackTicks` ctor arg.
- **`Rollback(targetTick)`** — restores the live frame from the ring slot, plus any system-owned state (`ISnapshotParticipant`).
- **`CalculateHash()`** — `FNV-1a` over `Tick`, entity count, and each component storage in **ascending typeId order**. This is the value compared across peers for desync detection.
- **`SerializeTo` / `DeserializeFrom`** — full-state serialization for resync / late-join / spectator / replay.

Determinism hinges on every peer producing **byte-identical** heaps. Two consequences for your code:

1. **Component fields must be fully deterministic** (§3). A stray `float` or wall-clock read makes the hash diverge and triggers a rollback-storm or resync.
2. **Filter/iteration order is deterministic by construction** — dense storage order is a pure function of the add/remove sequence, and every peer applies the same sequence. You never need to sort inside a system; just don't introduce order from a non-deterministic source.

Systems that hold state **outside** components (e.g. a physics broadphase) implement `ISnapshotParticipant` so the ring buffer captures/restores them alongside the frame — register them with `AddSystem` and the wiring is automatic.

---

## 8. Wiring a Simulation

`EcsSimulation` is the `ISimulation` Klotho's engine drives. Typical setup:

```csharp
var sim = new EcsSimulation(
    maxEntities:     512,
    maxRollbackTicks: 16,
    deltaTimeMs:      50,         // 20 Hz
    logger:          logger,
    registryBuilder: dataAssetRegistryBuilder);   // or assetRegistry: prebuilt (not both)

// register components' systems in phase order
sim.AddSystem(new MovementSystem(), SystemPhase.Update);
sim.AddSystem(new CombatSystem(),   SystemPhase.Update);

sim.LockAssetRegistry();   // freeze DataAssets before the first tick (see DataAsset.md §9)
```

You normally hand this `sim` to `KlothoEngine` / the session driver rather than calling `Tick` yourself; see [QuickStart.Unity.md](QuickStart.Unity.md) / [QuickStart.Godot.md](QuickStart.Godot.md) for the full session wiring. `sim.Frame` exposes the live frame for tests and debugging.

> `maxEntities` is a **fixed capacity** — the heap and all storages are sized once at construction (`EntityManager` and every `ComponentStorageFlat<T>` throw on overflow). Size it for your worst case; it cannot grow at runtime.

---

## 9. Memory Footprint & Optimization

`maxEntities` fixes the heap size up front (§8), and the `FrameRingBuffer` keeps N of those heaps — so **per-frame reservation is multiplied by the rollback-ring depth**. Each registered component type reserves a fixed heap slice regardless of how many instances are actually live:

```text
[ count(4) ][ sparse(maxEntities×4) ][ dense(slotCapacity×4) ][ components(slotCapacity×memSize) ]
```

`slotCapacity` defaults to `maxEntities`, so a few **large** components left at full capacity can dominate memory (a 700-byte nav-agent component with 8 live but 256 reserved slots is almost all waste). Two levers trim it:

- **`maxCount` (slot cap)** — cap `slotCapacity` below `maxEntities` for types that never approach full population. Primary lever; high-`memSize` + low-utilization types recover the most.
- **Pruning (unused-type removal)** — a **denylist** of types to skip reserving (everything not listed stays reserved — a fail-safe default), removing even the fixed `sparse` floor. Applies narrowly — a `Filter`/`Has` on the type keeps it reserved, so only list types no registered system touches.

Both are **determinism inputs** (the layout must be identical on every peer), set before the first tick and propagated authority-peer → joining peers. Measure first with the `[Mem]` peak-sampling report, then cap the biggest offenders.

> Full guide — heap formation, reading the `[Mem]` report, authoring `maxCount`, and when pruning pays off: **[ECSMemoryOptimization.md](ECSMemoryOptimization.md)**.

---

## 10. Built-in Components & Systems

Klotho ships gameplay-agnostic building blocks you can use, extend, or ignore:

- **Components** — `TransformComponent` (id 1), `VelocityComponent`, `MovementComponent`, `HealthComponent`, `CombatComponent`, `OwnerComponent`, `PhysicsBodyComponent`, `NavAgentComponent`, `SessionParticipantComponent` (engine writes one per active player at `Start()` as an all-participants-spawned gate), `RandomSeedComponent` (singleton; engine-injected at session start, restored on LateJoin / Reconnect / Spectator / Replay).
- **Systems** — `MovementSystem`, `CombatSystem`, `PhysicsSystem`, `NavigationSystem`, `CommandSystem`, `EventSystem`.

**`TransformComponent` view hook:** it carries `PreviousPosition` / `PreviousRotation` / `PreviousInitialized`. The engine auto-initializes `Previous*` on first `Frame.Add` and snapshots them in a `PreUpdate` pass each tick, so the view layer can interpolate between the previous and current verified transform. After a manual `ref`-set of `Position` right after spawn, call `frame.RefreshPreviousTransform(entity)` to snap `Previous*` to the new value and suppress a one-frame interpolation artifact (see [GameDevAPI.md](GameDevAPI.md) §4.1).

---

## 11. Determinism Rules (must-read)

These are the invariants that keep every peer's `CalculateHash()` identical:

1. **Components are `unmanaged` and float-free** — `FP64` / integer / bool / fixed buffers only.
2. **Systems read no ambient nondeterminism** — no wall clock, no `System.Random`, no unordered managed-collection iteration, no float math. Randomness comes from the seeded `DeterministicRandom` / `RandomSeedComponent`.
3. **No hidden order** — filter/dense order is already deterministic; don't reorder by hash codes, object identity, or dictionary enumeration.
4. **State lives in components (or an `ISnapshotParticipant`)** — anything a system mutates that isn't captured by the snapshot will not roll back and will desync.
5. **Same code, same DataAssets, same component registration on every peer** — the typeId order that `CalculateHash` walks must match (the registry is assembly-scanned, so the same binaries guarantee it).

When determinism breaks, the engine's recovery ladder (hash check → rollback → full-state resync → corrective reset) takes over — see [SynchronizationDesign.md](SynchronizationDesign.md).

---

## 12. Debugging

- **`EcsSimulation.LogComponentHashes(logger, label, level)`** / **`Frame.LogComponentHashes(...)`** — dumps per-typeId `count` + `hash` for the current frame. Diff a client log against the server's at a suspect tick to find which component type diverged.
- **`Frame.SnapshotHashesToQueue()` / `FlushHashHistory(logger, dumpTick)`** (editor / dev builds) — keeps a rolling 60-tick history of per-type hashes and dumps it when a desync is detected, so you see the *first* tick that diverged, not just the symptom.
- **`Frame.GetAllLiveEntities(buffer)`** — fills a caller-owned `EntityRef[]` with every live entity for inspection.
- **`Frame.TryGetReflectableStorage(type, out view)`** — editor-only boxed reflection over a component storage for inspector tooling (do not use on runtime paths).
- **`SyncTestRunner`** (Verification Tools) — runs the simulation forward, rolls back, and re-simulates, asserting the hash matches — the fastest way to catch a non-deterministic system in isolation.

---

## 13. Worked Example — a regen system end to end

```csharp
// 1) component
[KlothoComponent(101)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct RegenComponent : IComponent
{
    public int PerTick;
}

// 2) system — heal every entity that has Health + Regen and isn't Dead
public class RegenSystem : ISystem
{
    public void Update(ref Frame frame)
    {
        var f = frame.FilterWithout<HealthComponent, RegenComponent, DeadComponent>();
        while (f.Next(out var e))
        {
            ref var hp = ref frame.Get<HealthComponent>(e);
            ref readonly var regen = ref frame.GetReadOnly<RegenComponent>(e);
            hp.Current = System.Math.Min(hp.Max, hp.Current + regen.PerTick);
        }
    }
}

// 3) register
sim.AddSystem(new RegenSystem(), SystemPhase.Update);

// 4) spawn an entity that uses it
var e = frame.CreateEntity();
frame.Add(e, new HealthComponent { Current = 50, Max = 100 });
frame.Add(e, new RegenComponent  { PerTick = 2 });
```

That's the whole loop: define unmanaged components, write a system that filters and mutates them by `ref`, register it in a phase. The Frame snapshots and hashes itself — rollback and cross-peer determinism come for free as long as the [determinism rules](#11-determinism-rules-must-read) hold.
