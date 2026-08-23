# ECS — Entities, Components, Systems

Klotho's simulation state lives in a compact, deterministic ECS. **Entities** are lightweight handles, **Components** are unmanaged structs holding all simulation state, and **Systems** are the per-tick logic that reads and mutates them. The whole world is one `Frame` — a single `byte[]` heap — so a snapshot is one `Buffer.BlockCopy` and a hash is a deterministic walk over the same bytes. This is what makes rollback cheap and cross-peer determinism exact.

> Audience: game developers building simulation logic on top of Klotho.
> Goal: define components, write systems, query entities, and understand how the Frame snapshots/hashes for rollback.
>
> Related: [Cookbook.md](Cookbook.md) ("I want to X" → where to look) · [GameDevAPI.md](GameDevAPI.md) (the API surface, condensed) · [FEATURES.md](FEATURES.md) (ECS module index) · [Specification.md](Specification.md) §7 (formal state layout) · [ECSMemoryOptimization.md](ECSMemoryOptimization.md) (frame-heap memory tuning) · [DataAsset.md](DataAsset.md) (read-only shared config) · [HFSM.md](HFSM.md) (AI on top of ECS) · [DesyncDiagnostics.md](DesyncDiagnostics.md) (when the hashes disagree) · [Samples/Brawler.B.Systems.md](Samples/Brawler.B.Systems.md) (real systems)

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
│   ├── ComponentStorageRegistry.cs  # assembly-scan registration + heap layout + layout fingerprint
│   ├── StorageLayout.cs          # per-type offsets into the heap
│   ├── IEntityPrototype.cs / EntityPrototypeRegistry.cs  # data-driven spawn templates
│   ├── StateHashBreakdown.cs / StateHashRing.cs          # per-type hash split + rolling history (§12)
│   ├── IStorageReflector.cs      # editor-only boxed reflection over one storage (inspector tooling)
│   └── FixedString.cs            # FixedString32 / FixedString64 (unmanaged string fields)
├── Attributes/
│   ├── KlothoComponentAttribute.cs              # [KlothoComponent(typeId)], plus MaxCount (§9)
│   ├── KlothoSingletonComponentAttribute.cs     # [KlothoSingletonComponent]
│   ├── KlothoCleanupAttribute.cs, CleanupMode.cs  # [KlothoCleanup(mode)] one-tick components (§3)
│   ├── KlothoCoreComponentAttribute.cs          # [KlothoCoreComponent] — engine-owned, never pruned
│   └── FrameDataAttribute.cs                    # [FrameData] — declared, nothing consumes it yet
├── System/
│   ├── ISystem.cs                # ISystem + lifecycle/command/sync interfaces + SystemPhase
│   ├── ISignal.cs                # component add/remove signal interfaces (§6)
│   ├── ISnapshotParticipant.cs   # for state a system owns outside components (§7)
│   ├── Filter.cs                 # Filter<T1..T5> / FilterWithout<…, TExclude> (§5)
│   └── SystemRunner.cs           # phase-ordered dispatch + the built-in tick-start/tick-end passes
├── Snapshot/
│   └── FrameRingBuffer.cs        # pre-tick Frame snapshots for rollback
├── Diagnostics/
│   ├── ComponentMemoryReport.cs, ComponentMemoryPeakSampler.cs  # the `[Mem]` report (§9)
│   └── SystemPerfMonitor.cs      # per-system time / allocation report (§12)
├── Components/                   # engine components — Transform, Owner, RandomSeed, … (§10)
├── Systems/                      # EventSystem (the only one here)
├── DataAsset/                    # see DataAsset.md
├── FSM/                          # see HFSM.md
└── EcsSimulation.cs              # ISimulation impl: owns Frame + SystemRunner + FrameRingBuffer
```

The optional *gameplay* building blocks — `HealthComponent`, `MovementSystem`, `PhysicsSystem` and friends —
are **not** in this folder. They live under `Runtime/Gameplay/`, because nothing in the engine requires them:
use them, build around them, or define your own equivalents and never reference them. §10 lists both sets.

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
- **Never renumber a shipped id.** The id is what `CalculateHash` walks in ascending order and what the layout fingerprint folds, so changing one is a wire-and-hash break: every peer must run the same id assignment. Treat the number like a serialization tag — append new ones, never reshuffle.

Two optional attributes change how a component is stored or how long it lives:

- **`MaxCount`** *(optional, on `[KlothoComponent]`)* — caps how many slots this type reserves in the heap, independent of `maxEntities`. A type that only ever has a handful of carriers does not need room for every entity:

  ```csharp
  [KlothoComponent(104, MaxCount = 96)]   // 96 slots, not maxEntities slots
  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  public partial struct SkillCooldownComponent : IComponent { public int RemainingTicks; }
  ```

  The effective slot count is `min(MaxCount, maxEntities)`, and adding the 97th carrier **throws** rather than growing — so size it for your worst case, not your typical one. It is a **determinism input**: every peer must agree, which is why it belongs in source (or in `ISimulationConfig.ComponentMaxCountOverrides`, propagated authority → joining peers). It is ignored on a `[KlothoSingletonComponent]`, which can only ever have one carrier anyway (`KLSG_ECS006`, warning). §9 explains when capping pays and how to measure before you guess.
- **`[KlothoCleanup(mode)]`** *(optional)* — declares a component that must live exactly one tick. See below.

You will also see **`[KlothoCoreComponent]`** on the engine's own components (`TransformComponent`, `RandomSeedComponent`, …). It marks a type as engine-essential so memory *pruning* can never drop it (§9), and you do not normally apply it to your own types. It says nothing about lifetime — combining it with `[KlothoCleanup]` is a compiler **warning** (`KLSG_ECS007`), because the engine's own core types are all persistent state.

### One-tick components — `[KlothoCleanup]`

Some components are messages, not state: a hit mark that a VFX system reads once, a "this entity was just
knocked back" flag, a "destroy me at end of tick" tag. Without help you write a cleanup system for each one,
and cleanup systems are where the mutate-during-iteration trap in §5 usually bites.

Declare the lifetime instead and the engine does the pass for you:

```csharp
// A hit mark: written by the combat system, read by anything downstream in the SAME tick, gone after it.
[KlothoComponent(101)]
[KlothoCleanup(CleanupMode.RemoveComponent)]      // storage emptied at the end of every tick
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct HitMarkComponent : IComponent
{
    public int  Damage;
    public FP64 KnockbackX;
}

// A short-lived entity: whoever spawns it adds the tag, and the engine destroys the whole entity.
[KlothoComponent(102)]
[KlothoCleanup(CleanupMode.DestroyEntity)]        // the CARRIER ENTITY is destroyed at end of tick
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct DespawnNowComponent : IComponent { public int Reason; }
```

```csharp
// Producer — writes the mark and never cleans up.
public class MeleeResolveSystem : ISystem                 // SystemPhase.Update
{
    public void Update(ref Frame frame)
    {
        var f = frame.Filter<AttackIntentComponent>();
        while (f.Next(out var attacker))
        {
            ref readonly var intent = ref frame.GetReadOnly<AttackIntentComponent>(attacker);
            if (!frame.Entities.IsAlive(intent.Target)) continue;
            if (frame.Has<HitMarkComponent>(intent.Target)) continue;   // Add throws on a duplicate

            frame.Add(intent.Target, new HitMarkComponent { Damage = 10, KnockbackX = FP64.One });
        }
    }
}

// Consumer — a LATER phase in the SAME tick sees the mark. Next tick it is simply not there.
public class HitReactionSystem : ISystem                  // SystemPhase.PostUpdate
{
    public void Update(ref Frame frame)
    {
        var f = frame.Filter<HitMarkComponent, VelocityComponent>();
        while (f.Next(out var e))
        {
            ref readonly var hit = ref frame.GetReadOnly<HitMarkComponent>(e);
            ref var vel = ref frame.Get<VelocityComponent>(e);
            vel.Velocity.x += hit.KnockbackX;
        }
    }
}
```

What the engine guarantees, and what you have to keep in mind:

- **Ordering** — the pass runs after *every* system in *every* phase and before the tick counter advances, so
  the state that gets snapshotted and hashed is the state *after* cleanup. Rollback and re-simulation
  reproduce it exactly; there is nothing peer-local about it.
- **Which phase can read it** — anything registered after the producer in the same tick. A component added in
  `Update` is visible to `PostUpdate` and `LateUpdate`, and to any `ISignalOnComponentAdded<T>` listener; it is
  never visible on the next tick.
- **Cost** — `RemoveComponent` does not iterate at all: it resets the storage's count and sparse table in one
  type-erased call, so the cost is O(sparse) whether one entity carried it or two hundred did.
  `DestroyEntity` collects the carriers into a pre-allocated buffer and destroys them afterwards, so it costs
  one `DestroyEntity` per carrier and allocates nothing.
- **Two markers, one entity** — an entity carrying two `DestroyEntity` components is destroyed once, not twice.
- **It is a determinism input.** The mode is folded into the layout fingerprint, so two builds that disagree
  about it are refused at the ready exchange with a `cleanup=` count in the message — rather than desyncing
  from tick 0, which is what the same disagreement used to look like.
- **Signals still fire** — an `ISignalOnComponentRemoved<T>` listener sees the clear, per carrying entity
  (§6, rule 4). A cache you maintain by hooking `Frame.Remove<T>` does **not** (§7).

Two combinations the analyzer rejects outright: `CleanupMode.DestroyEntity` on a
`[KlothoSingletonComponent]` (its carrier may hold unrelated components — `KLSG_ECS008`, error), and an
argument that is not a defined `CleanupMode` member (it would compile to a raw cast and be silently ignored
at runtime — `KLSG_ECS009`, error).

### Singleton components

Mark a type `[KlothoSingletonComponent]` when exactly one entity in the frame should ever carry it (global rules, match state, the RNG seed). `Frame.Add<T>` throws if a second carrier appears; read it without an `EntityRef`:

```csharp
ref var rules = ref frame.GetSingleton<MyRulesComponent>();          // throws if absent
ref readonly var ro = ref frame.GetReadOnlySingleton<MyRulesComponent>();
if (frame.TryGetSingleton<MyRulesComponent>(out var carrier)) { ... } // safe when it may be absent
```

A singleton may carry `[KlothoCleanup(CleanupMode.RemoveComponent)]`, but then it is absent at the start of every tick — read it with `TryGetSingleton`, never `GetSingleton`. (`CleanupMode.DestroyEntity` on a singleton is a compile error: its carrier may hold other components.)

---

## 4. Entities & Components on the Frame

```csharp
// create / destroy
EntityRef e = frame.CreateEntity();
frame.DestroyEntity(e);                       // removes all its components, recycles the slot

// add / read / mutate / remove components
frame.Add(e, new HealthComponent { Current = 100, Max = 100 });
bool has = frame.Has<HealthComponent>(e);
ref var hp = ref frame.Get<HealthComponent>(e);          // mutable ref — write directly
hp.Current -= 10;
ref readonly var roHp = ref frame.GetReadOnly<HealthComponent>(e);
frame.Remove<HealthComponent>(e);
```

`Get<T>` returns a `ref` **directly into the heap** — assign to its fields and the frame is already updated;
there is no write-back call and no copy. `GetReadOnly<T>` is the same reference with the compiler stopping you
from writing through it, which is what you want for the components a system only reads.

Three sharp edges worth knowing before you hit them:

| Call | If the precondition does not hold |
| ---- | ---- |
| `Get<T>` / `GetReadOnly<T>` | **Unchecked.** The lookup is `ComponentsSpan[SparseSpan[index]]`, and a missing component leaves `-1` in the sparse slot — so reading a component an entity does not have indexes a span with `-1`. Guard with `Has<T>`, or reach the entity through a `Filter` that already required the component. |
| `Add<T>` | Throws if the entity already carries that component, if the type's slots are exhausted (`MaxCount`, §9), or if the type is a singleton and someone already carries it. Adding is not "set" — use `Get<T>` to overwrite. |
| a stale `EntityRef` | Safe by design. `EntityRef` is a *generational* handle: recycling a slot bumps its `Version`, so a handle you kept from before the recycle fails `frame.Entities.IsAlive(...)` and never resolves to the new occupant. |

```csharp
// The generational handle in practice — this is why you can keep an EntityRef in a component.
EntityRef target = attacker.LastTargetRef;         // stored last tick, may be long dead
if (!frame.Entities.IsAlive(target)) return;       // the check that makes stale refs a non-problem
if (!frame.Has<HealthComponent>(target)) return;   // alive, but may not carry what you want
ref var hp = ref frame.Get<HealthComponent>(target);
```

**Data-driven spawning.** Hard-coding a spawn as a run of `frame.Add(...)` calls works, but it puts the recipe
in a system instead of in data. A prototype moves it out. Register one and create by id, or pass a typed
prototype by `in` when the spawn needs parameters:

```csharp
// A typed prototype carries its own spawn data; Apply runs inside CreateEntity.
public struct ProjectileSpawn : IEntityPrototype
{
    public FPVector3 Origin;
    public FPVector3 Velocity;
    public int       OwnerId;

    public void Apply(Frame frame, EntityRef e)
    {
        frame.Add(e, new TransformComponent { Position = Origin });
        frame.Add(e, new VelocityComponent  { Velocity = Velocity });
        frame.Add(e, new OwnerComponent     { OwnerId  = OwnerId });
    }
}

EntityRef a = frame.CreateEntity(prototypeId);                  // registered in EntityPrototypeRegistry
EntityRef b = frame.CreateEntity(in new ProjectileSpawn {       // typed, no registration needed
    Origin = muzzle, Velocity = muzzleVelocity, OwnerId = playerId });
```

> If you `ref`-set `TransformComponent.Position` *after* the entity is created (rather than inside the
> prototype), call **`frame.RefreshPreviousTransform(entity)`** afterwards. `Frame.Add` initializes the
> `Previous*` fields the view interpolates from, and a later position write leaves them pointing at the old
> spot — which the view renders as a one-frame streak from the origin (§10).

---

## 5. Querying with Filters

`Filter<T1>` … `Filter<T1,T2,T3,T4,T5>` iterate every live entity that has **all** the listed components;
`FilterWithout<…, TExclude>` (one to five required types plus exactly one excluded type) adds an exclusion.
Filters are **`ref struct`** — stack-only, zero-GC, and not storable in a field.

You do **not** need to put the most selective component first: the filter looks at the requested storages,
walks the **smallest** one, and checks `Has` on the rest. Listing `TransformComponent` (which every entity
has) alongside `PoisonComponent` (which three do) costs three iterations, not `maxEntities`.

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

// one component — "every entity that has a cooldown running"
var single = frame.Filter<SkillCooldownComponent>();
while (single.Next(out var e)) { /* ... */ }

// "all movable entities that are NOT stunned" — the last type parameter is the exclusion
var f = frame.FilterWithout<TransformComponent, VelocityComponent, StunnedComponent>();
while (f.Next(out var e)) { /* ... */ }
```

Iteration is always `while (filter.Next(out var entity))`. There is deliberately no `foreach` / `IEnumerable`
— an enumerator would box and allocate on a path that runs every tick for every system. `filter.Count` is the
iteration bound (the smallest requested storage's live count), useful for a fast `if (filter.Count == 0) return;`.

### Mutating while you iterate

Writing component *fields* through `Get<T>` is always fine — the storage's shape does not change, only bytes
inside a slot. What is not fine is changing the **shape** of the storage you are walking: `Add`, `Remove`, or
`DestroyEntity` on the type under the cursor swaps the tail entity into the vacated slot.

The sharpest version of this is not "I removed the entity I'm on". It is **removing from a *different*
entity**: that entity's slot gets backfilled by the tail, which sits *ahead* of the cursor, so the tail entity
is visited twice — once in its old position and once in its new one. No exception, no desync (every peer does
the same thing), just an effect applied twice.

The fix is to separate deciding from doing:

```csharp
public class DeathCleanupSystem : ISystem
{
    // Caller-owned and reused: allocating per tick is what you are trying to avoid.
    private readonly List<EntityRef> _dead = new List<EntityRef>(64);

    public void Update(ref Frame frame)
    {
        _dead.Clear();

        var f = frame.Filter<HealthComponent>();
        while (f.Next(out var e))                       // decide: read only, no shape changes
        {
            ref readonly var hp = ref frame.GetReadOnly<HealthComponent>(e);
            if (hp.Current <= 0) _dead.Add(e);
        }

        for (int i = 0; i < _dead.Count; i++)           // do: the filter is finished
            frame.DestroyEntity(_dead[i]);
    }
}
```

One thing you cannot do is reason your way out of it by picking a different type. A multi-type filter walks
whichever of the requested storages is **smallest at the moment it is constructed** — that is the optimisation
described above, and it means `Filter<HealthComponent, PoisonComponent>` may be walking `Poison` on the very
tick you decide to remove `Poison`. "I'm removing a different component than the one I listed first" is not a
safety argument.

Two ways to avoid needing the pattern at all:

- **Walk a single-type filter you are not mutating.** `frame.Filter<HealthComponent>()` provably walks
  `HealthComponent`, so removing `PoisonComponent` inside it is safe. With one type there is no choice for the
  engine to make.
- **If the removal is unconditional, don't write it.** Declare the component
  `[KlothoCleanup(CleanupMode.RemoveComponent)]` (§3) and the engine clears the whole storage at the end of
  the tick, in one pass, outside any iteration. A "destroy me" tag becomes
  `[KlothoCleanup(CleanupMode.DestroyEntity)]` and the loop above disappears entirely.

---

## 6. Writing Systems

A system is a plain class implementing `ISystem` — no base class, no attribute, nothing to generate. You give
it a **phase** at registration, and that plus registration order is the entire ordering model: phases run in
enum order, and within one phase systems run in the order you registered them.

```csharp
public enum SystemPhase { PreUpdate, Update, PostUpdate, LateUpdate }
```

```csharp
sim.AddSystem(new IntentSystem(),   SystemPhase.PreUpdate);              // reads input into components
sim.AddSystem(new MovementSystem(), SystemPhase.Update,     group: "move");
sim.AddSystem(new CombatSystem(),   SystemPhase.Update,     group: "combat");   // after movement
sim.AddSystem(new HitReactionSystem(), SystemPhase.PostUpdate, group: "combat"); // reads this tick's marks
sim.AddSystem(new EventSystem(),    SystemPhase.LateUpdate);             // publishes what happened
```

Ordering is a design decision, not a detail: `CombatSystem` above resolves hits against positions
`MovementSystem` already wrote this tick. If you find yourself wanting "run me after X but only sometimes",
that is usually a component flag rather than a phase. The optional `group` argument is a perf-report label
and nothing else (§8).

Each tick, `EcsSimulation.Tick(commands)` runs a fixed sequence. Knowing it saves a lot of guessing about
"will my system see that component this tick or next":

1. **`ICommandSystem.OnCommand`** — once per input command, before any `Update`.
2. **The built-in previous-transform pass** — snapshots `TransformComponent.Previous*` for view interpolation
   (§10), ahead of the first `PreUpdate` system.
3. **`ISystem.Update`** for every registered system, in `SystemPhase` order and then registration order.
4. **The built-in `[KlothoCleanup]` passes** — first every `CleanupMode.RemoveComponent` storage is emptied,
   then every `CleanupMode.DestroyEntity` carrier is destroyed (§3). Skipped entirely when nothing declares
   the attribute.
5. **`Tick++`.**

The snapshot and the state hash are taken *after* all of that, so what rollback restores and what peers
compare is the post-cleanup state.

Optional interfaces let one class hook more than the per-tick update:

| Interface | Method | Called when |
| ---- | ---- | ---- |
| `ISystem` | `Update(ref Frame)` | Every tick, in phase order. |
| `IInitSystem` | `OnInit(ref Frame)` | Once at `Initialize()`. |
| `IDestroySystem` | `OnDestroy(ref Frame)` | On simulation teardown. |
| `ICommandSystem` | `OnCommand(ref Frame, ICommand)` | Per input command, before Update systems. |
| `IEntityCreatedSystem` | `OnEntityCreated(ref Frame, EntityRef)` | Right after `CreateEntity` — before any component has been added, so the entity is bare. |
| `IEntityDestroyedSystem` | `OnEntityDestroyed(ref Frame, EntityRef)` | During `DestroyEntity`, **after every component has already been removed**. `IsAlive` is still true; `Has<T>` is false and `Get<T>` throws. If you need component data, use `ISignalOnComponentRemoved<T>` (rule 3 below). |
| `ISignalOnComponentAdded<T>` / `ISignalOnComponentRemoved<T>` | `OnAdded` / `OnRemoved` | On `Frame.Add<T>` / `Frame.Remove<T>`. `OnAdded` fires **after** the insert with a `ref` into the storage slot (a listener may adjust what was just added); `OnRemoved` fires **before** the removal, by value. Read the rules below before implementing one. |
| `ISyncEventSystem` | `EmitSyncEvents(ref Frame)` | On synced (verified) ticks only — for events that must fire once on the confirmed timeline, not on every predicted resim. |

#### Component signals: five rules

1. **They are not events — they fire on every *execution* of a tick, not once per tick.** A listener that
   only writes to the frame is reproduced exactly and is safe. One that accumulates *outside* the frame
   over-counts, and by more than you would guess: measured in a live server-driven match, the dedicated
   server executed each tick once while the client executed it **~10 times** on average (it re-runs ticks
   as inputs are confirmed — no rollback log, no hash mismatch, just normal operation). Frame-external
   state is what `ISyncEventSystem` is for.
2. **A listener must not add or remove components.** `OnAdded` receives a `ref` into the storage slot, and
   removing a component of the same type swap-backs another entity into that slot — the ref would then
   point at a different entity's component. Leave a flag on a component and let a system act on it.
3. **Destroying an entity fires `OnRemoved` for every component it held**, in ascending typeId order and
   *before* `IEntityDestroyedSystem` — "these components are going away" then "this entity is going away".
   The entity stays alive throughout, so `IsAlive` is true in every listener — but the removals are
   *interleaved* with the signals: each component is removed immediately after its own `OnRemoved`, so a
   listener can still read only the components with a **higher** typeId, and by the time
   `IEntityDestroyedSystem` runs the entity has none left. Read what you need out of the component value
   you were handed, not back out of the frame.
4. **`[KlothoCleanup]`'s per-tick clear fires them too**, once per carrying entity in dense order, at the
   end of the tick and before the storage is emptied. That is the one place the cost is visible: a cleanup
   type nobody listens to is emptied with a single dispatch call (O(sparse)), while one with a listener is
   walked entity by entity (O(n)). Only the types with listeners pay it.
5. **A throwing listener propagates.** Nothing catches a tick exception, so the exception ends the match —
   which also means a listener that throws mid-destroy leaves the entity half-destroyed with nothing left
   running to observe it. Treat throwing from a listener as the bug it is.

A listener is just a system that also implements the signal interface, so it is registered the same way:

```csharp
// Keep a shield's visual state in sync with the component that grants it, without polling for it.
public class ShieldReactionSystem :
    ISystem,
    ISignalOnComponentAdded<ShieldComponent>,
    ISignalOnComponentRemoved<ShieldComponent>
{
    public void Update(ref Frame frame) { /* the per-tick half, if any */ }

    // Fires AFTER the insert, with a ref into the storage slot — you may adjust what was just added.
    public void OnAdded(ref Frame frame, EntityRef entity, ref ShieldComponent shield)
    {
        if (shield.Hp <= 0) shield.Hp = shield.MaxHp;          // fine: writing a field of this component
        if (frame.Has<VfxStateComponent>(entity))              // fine: writing another component's fields
            frame.Get<VfxStateComponent>(entity).ShieldOn = true;
        // NOT allowed here: frame.Add(...) / frame.Remove(...) / frame.DestroyEntity(...)  — see rule 2.
    }

    // Fires BEFORE the removal, and the value is passed BY VALUE — after this the slot is gone.
    public void OnRemoved(ref Frame frame, EntityRef entity, ShieldComponent shield)
    {
        if (frame.Has<VfxStateComponent>(entity))
        {
            ref var vfx = ref frame.Get<VfxStateComponent>(entity);
            vfx.ShieldOn        = false;
            vfx.LastShieldHpLost = shield.Hp;                  // read the value you were handed, not the frame
        }
    }
}

sim.AddSystem(new ShieldReactionSystem(), SystemPhase.Update);   // same registration as any system
```

Listeners cost nothing when nobody registers one: the engine keeps a per-typeId "does anyone listen" mask, so
`Frame.Add` reads one flag and skips the dispatch entirely — a project that uses no signals pays a single
field read per `Add`. Note what the mask does *not* buy: once a type *has* a listener, each `Add` of it walks
the system list to find the implementers.

> **Not sure whether you want a signal or an event?** If the reaction only writes to the frame, a signal is
> right and rule 1 costs you nothing. If it has to happen exactly once per tick in the real world — a sound,
> an analytics counter, a UI popup — use `ISyncEventSystem`, which runs only on confirmed ticks.

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

### State a system owns outside components

Most systems should be stateless: put everything in components and rollback is free. But some genuinely own
state that does not belong to any single entity — a physics broadphase, an accumulator, a per-match counter.
If that state is not captured, a rollback rewinds the frame and leaves your field at its future value, and the
next re-simulation diverges.

Implement `ISnapshotParticipant` and the ring buffer captures and restores it alongside the frame. Registration
is the ordinary `AddSystem` call — the wiring is automatic:

```csharp
public class ComboCounterSystem : ISystem, ISnapshotParticipant
{
    private int _comboWindowTicks;     // NOT in any component — so it must be snapshotted

    public void Update(ref Frame frame)
    {
        if (_comboWindowTicks > 0) _comboWindowTicks--;
    }

    // Three members, and the size must match exactly what Save writes.
    public int  GetSnapshotSize()                       => sizeof(int);
    public void SaveSnapshot(ref SpanWriter writer)     => writer.WriteInt32(_comboWindowTicks);
    public void RestoreSnapshot(ref SpanReader reader)  => _comboWindowTicks = reader.ReadInt32();
}

sim.AddSystem(new ComboCounterSystem(), SystemPhase.Update);   // ISnapshotParticipant is detected here
```

Whatever you write here must be as deterministic as a component: same rules, same reasons. And if the state
can live in a component instead, prefer that — a component is hashed, so a divergence in it is *reported*,
while participant state is snapshotted but not part of `CalculateHash`.

### Derived state outside the frame

`ISnapshotParticipant` covers state a system **owns**. Caching something **derived** from the frame — a
value→entity index, a "changed this tick" list — is a different problem: the cache has to be invalidated
everywhere the frame's contents are replaced, and `Frame.Add` / `Remove` / `DestroyEntity` are **not** the
only such paths:

| Path | Note |
|---|---|
| `Frame.CopyFrom(source)` | **Runs every tick** — `FrameRingBuffer.SaveFrame` calls it into the ring slot. `RestoreFrame` (rollback) is *only* `target.CopyFrom(...)`, so one hook here covers rollback too — but it must be O(1) (set a dirty flag); rebuilding here costs you a rebuild per tick. |
| `Frame.Clear()` | Zeroes the whole heap, resets the entity manager and sets `Tick = 0` — a session teardown / re-init, not a per-tick path. |
| `Frame.DeserializeFrom(data)` | full-state apply — resync / late-join / spectator / replay |
| `Frame.RunCleanupClear()` | The `[KlothoCleanup(CleanupMode.RemoveComponent)]` bulk pass clears storages through the type-erased clear dispatch and **does not go through `Frame.Remove<T>`** — a hook on `Add`/`Remove` alone goes stale here. (`RunCleanupDestroy` does go through `DestroyEntity`.) |

The general rule: **maintaining derived state by hooking the per-entity paths breaks silently every time a
bulk path is added.** `RunCleanupClear` is the first such path: it *does* fire
`ISignalOnComponentRemoved<T>` (§6, rule 4), but it never routes through `Frame.Remove<T>`, so a cache wired
to the per-entity path alone still goes stale there.

Note the failure mode. A derived cache is not part of the snapshot or of `CalculateHash`, so a stale cache
**passes every hash check** — and because each peer runs the same code, every peer goes stale the same way.
Nothing reports a desync; the only symptom is a query returning the wrong entity. Prefer recomputing from
the frame (a `Filter` walks the *smallest* requested storage, so this is usually cheaper than it looks) and
cache only when a measurement says otherwise.

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
sim.AddSystem(new CombatSystem(),   SystemPhase.Update, group: "combat");   // group: perf report only

sim.LockAssetRegistry();   // manual wiring only — see the note below
```

> **The optional `group` argument labels the system in the per-system perf report — nothing else.** It
> does not affect execution order (that is phase, then registration order), never reaches the frame,
> the state hash, or the wire, and is deliberately *not* part of the layout fingerprint: peers that
> label their systems differently still play together. Practical notes: it is ignored on a registration
> that does not implement `ISystem` (only `ISystem` entries are measured), nothing beyond trimming is
> validated so `"combat"` and `"Combat"` are two groups — keep the labels in a `const` — and short
> labels are better, since the longest name sets the report's whole first column.

> **`LockAssetRegistry()` is called for you on every engine path.** `KlothoSession.Create` and the
> dedicated server's `RoomManager` both call it right after `RegisterSystems`, so a game that goes
> through either never needs to. It stays public for **hand-rolled wiring** — tests and tools that
> construct an `EcsSimulation` directly — where it must run before the first tick to freeze the
> DataAssets (see [DataAsset.md](DataAsset.md) §9). Calling it twice is harmless.

You normally hand this `sim` to `KlothoEngine` / the session driver rather than calling `Tick` yourself; see [QuickStart.Unity.md](QuickStart.Unity.md) / [QuickStart.Godot.md](QuickStart.Godot.md) for the full session wiring. `sim.Frame` exposes the live frame for tests and debugging.

> `maxEntities` is a **fixed capacity** — the heap and all storages are sized once at construction (`EntityManager` and every `ComponentStorageFlat<T>` throw on overflow). Size it for your worst case; it cannot grow at runtime.

---

## 9. Memory Footprint & Optimization

`maxEntities` fixes the heap size up front (§8), and the `FrameRingBuffer` keeps N of those heaps — so **per-frame reservation is multiplied by the rollback-ring depth**. Each registered component type reserves a fixed heap slice regardless of how many instances are actually live:

```text
[ count(4) ][ sparse(maxEntities×4) ][ dense(slotCapacity×4) ][ components(slotCapacity×memSize) ]
```

`slotCapacity` defaults to `maxEntities`, so a few **large** components left at full capacity can dominate memory (a 700-byte nav-agent component with 8 live but 256 reserved slots is almost all waste). Two levers trim it:

- **`MaxCount` (slot cap)** — cap `slotCapacity` below `maxEntities` for types that never approach full population. This is the primary lever, and high-`memSize` + low-utilization types recover the most. Author it on the attribute (`[KlothoComponent(104, MaxCount = 96)]`, §3) or, when you cannot touch the source, through `ISimulationConfig.ComponentMaxCountOverrides`. Exceeding the cap throws rather than growing, so size it for your worst case.
- **Pruning (unused-type removal)** — a **denylist** of types to skip reserving (everything not listed stays reserved, which is the fail-safe default), removing even the fixed `sparse` floor. It applies narrowly: a `Filter`/`Has` on the type keeps it reserved, so only list types that no registered system touches. Types marked `[KlothoCoreComponent]` are force-excluded from the denylist — listing one by mistake cannot prune it.

Both are **determinism inputs** (the layout must be identical on every peer), set before the first tick and propagated authority-peer → joining peers. Measure first with the `[Mem]` peak-sampling report, then cap the biggest offenders.

> Full guide — heap formation, reading the `[Mem]` report, authoring `maxCount`, and when pruning pays off: **[ECSMemoryOptimization.md](ECSMemoryOptimization.md)**.

---

## 10. Built-in Components & Systems

Klotho ships two clearly separated sets. Knowing which is which tells you what you are allowed to ignore.

**Engine components** (`Runtime/ECS/Components/`) — the engine itself reads or writes these. The ones marked
**core** carry `[KlothoCoreComponent]`, so memory pruning can never drop them (§9):

| Component | Id | Core | What it is for |
| ---- | ---- | :--: | ---- |
| `TransformComponent` | 1 | ✔ | Position / rotation / scale, plus the `Previous*` fields the view interpolates from. |
| `OwnerComponent` | 2 |  | Which player owns the entity. The usual key for "is this command allowed to touch it". |
| `ErrorCorrectionTargetComponent` | 3 | ✔ | Marks an entity the engine may smooth toward a corrected state. |
| `SessionParticipantComponent` | 4 | ✔ | The engine writes one per active player at `Start()`, as an all-participants-spawned gate. |
| `RandomSeedComponent` | 5 | ✔ | Singleton. Engine-injected at session start and restored on late-join / reconnect / spectator / replay — the source for `DeterministicRandom`. |
| `MatchEndStateComponent` | 26 | ✔ | Singleton. Match-end bookkeeping the engine reads for the end-of-match ladder. |

**Optional gameplay components** (`Runtime/Gameplay/Components/`) — plain components with no engine
privileges. Use them, extend them, or define your own and never reference these: `HealthComponent` (21),
`VelocityComponent` (22), `MovementComponent` (23), `CombatComponent` (24), `PhysicsBodyComponent` (25).
`NavAgentComponent` (11) lives with the navigation module.

**Systems** — the engine registers nothing on your behalf; every system below is opt-in via `AddSystem`:

| System | Where | Kind |
| ---- | ---- | ---- |
| `EventSystem` | `Runtime/ECS/Systems/` | `ISystem`. Other systems `Enqueue` simulation events and it batch-publishes them through `ISimulationEventRaiser` in `LateUpdate`. |
| `MovementSystem`, `PhysicsSystem`, `CombatSystem` | `Runtime/Gameplay/Systems/` | `ISystem`. Reference implementations over the gameplay components above. |
| `CommandSystem` | `Runtime/Gameplay/Systems/` | `ICommandSystem` — command handling, not a per-tick update. |
| `FPNavMeshRebakeDriver` | `Runtime/Deterministic/Navigation/` | `ISystem`. Owns runtime NavMesh rebake/install; see [Navigation.Rebake.md](Navigation.Rebake.md). |

**`TransformComponent` view hook:** it carries `PreviousPosition` / `PreviousRotation` / `PreviousInitialized`. The engine auto-initializes `Previous*` on first `Frame.Add` and snapshots them in a `PreUpdate` pass each tick, so the view layer can interpolate between the previous and current verified transform. After a manual `ref`-set of `Position` right after spawn, call `frame.RefreshPreviousTransform(entity)` to snap `Previous*` to the new value and suppress a one-frame interpolation artifact (see [GameDevAPI.md](GameDevAPI.md) §4.1).

---

## 11. Determinism Rules (must-read)

These are the invariants that keep every peer's `CalculateHash()` identical:

1. **Components are `unmanaged` and float-free** — `FP64` / integer / bool / fixed buffers only.
2. **Systems read no ambient nondeterminism** — no wall clock, no `System.Random`, no unordered managed-collection iteration, no float math. Randomness comes from the seeded `DeterministicRandom` / `RandomSeedComponent`.
3. **No hidden order** — filter/dense order is already deterministic; don't reorder by hash codes, object identity, or dictionary enumeration.
4. **State lives in components (or an `ISnapshotParticipant`)** — anything a system mutates that isn't captured by the snapshot will not roll back and will desync.
5. **Same code, same DataAssets, same component registration on every peer** — the typeId order that `CalculateHash` walks must match. The registry is assembly-scanned, so the same binaries guarantee it; loading an *extra* assembly's components on one peer (an Editor session with test assemblies, against a player or dedicated-server build) is the classic way to break this without changing a line of gameplay code.

Rule 5 is now checked instead of merely documented. The registry folds `maxEntities`, the sorted typeId set,
type names, slot capacity (`MaxCount`), component size and each type's `[KlothoCleanup]` mode into a **layout
fingerprint**, and peers exchange it before the first tick — a mismatch is refused with a message naming the
two sanctioned fixes (match the assembly sets, or prune the difference) instead of surfacing as an
inexplicable tick-0 divergence. The practical consequence for you: `MaxCount` and `[KlothoCleanup(mode)]` are
*determinism inputs*, not local tuning knobs. Changing either means shipping every peer together.

When determinism breaks anyway, the engine's recovery ladder (hash check → rollback → full-state resync →
corrective reset) takes over — see [SynchronizationDesign.md](SynchronizationDesign.md), and
[DesyncDiagnostics.md](DesyncDiagnostics.md) for reading the logs it produces.

---

## 12. Debugging

**"The hashes diverged — which component?"**

- **`EcsSimulation.LogComponentHashes(logger, label, level)`** / **`Frame.LogComponentHashes(...)`** — dumps per-typeId `count` + `hash` for the current frame. Diff a client log against the server's at a suspect tick and the offending component type falls out of the diff.
- **The rolling hash history** answers the harder question — *which tick* first diverged, rather than the tick you noticed. Turn it on with `ISimulationConfig.DiagnosticHistoryTicks` (default **60**, `0` disables and costs nothing), or directly via `EcsSimulation.SetHashHistoryCapacity(ticks)`. The engine records per-tick, per-type hashes as it goes (`RecordHashHistory` / `ComputeAndRecordHashHistory`) and **`FlushHashHistory(logger, dumpTick)`** dumps the window when a desync is detected. `TryGetHashHistoryBreakdown(tick, …)` reads one tick back out programmatically.
- **`EcsSimulation.LogStateBreakdown(logger, label, level)`** — per-type byte sizes and hashes of the current state, for "what is actually in this frame".
- **`EcsSimulation.LogStaticFingerprint(logger, label, level)`** — the layout / environment fingerprints. Compare these **first**: a component-registry difference (a peer loading an extra assembly's components) diverges from tick 0 and makes every per-component number a symptom rather than a cause.

**"Which system is slow, or allocating?"**

- **`EcsSimulation.EnableSystemPerfMonitor(warmupExecutions)`** then **`AppendSystemPerfLog()`** — per-system elapsed time and per-tick allocation, plus the two built-in passes. Off by default and hard-gated: when it is off, no `Stopwatch` or GC call happens at all. Pass a warmup so first-call JIT does not land in the steady-state figures, and label your registrations (`AddSystem(sys, phase, group: "combat")`, §8) to get per-group totals.

**"What is in the frame right now?"**

- **`Frame.GetAllLiveEntities(buffer)`** — fills a caller-owned `EntityRef[]` with every live entity.
- **`Frame.GetLiveCount(typeId)`** — how many entities carry one component type, without a generic parameter.
- **`Frame.TryGetReflectableStorage(type, out view)`** — editor-only boxed reflection over one component storage, for inspector tooling. Do not call it on a runtime path.

**"Is my system deterministic at all?"**

- **`SyncTestRunner`** (Verification Tools) — runs the simulation forward, rolls back, and re-simulates, asserting the hash matches. This is the cheapest way to catch a non-deterministic system, and it catches it *in isolation* rather than as a mid-match desync between two machines. Run it before you blame the network.

---

## 13. Worked Example — a regen system end to end

Four steps, and nothing about rollback or networking appears in any of them.

```csharp
// 1) components — one persistent, one a marker other systems set
[KlothoComponent(101)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct RegenComponent : IComponent
{
    public int PerTick;
}

[KlothoComponent(102, MaxCount = 32)]        // only a few entities are ever dead at once
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct DeadComponent : IComponent
{
    public int DiedOnTick;
}

// 2) system — heal every entity that has Health + Regen and is NOT dead
public class RegenSystem : ISystem
{
    public void Update(ref Frame frame)
    {
        var f = frame.FilterWithout<HealthComponent, RegenComponent, DeadComponent>();
        while (f.Next(out var e))
        {
            ref var hp = ref frame.Get<HealthComponent>(e);              // ref into the heap
            ref readonly var regen = ref frame.GetReadOnly<RegenComponent>(e);
            hp.Current = System.Math.Min(hp.Max, hp.Current + regen.PerTick);
        }
    }
}

// 3) register — phase decides ordering; the group label is perf-report only
sim.AddSystem(new RegenSystem(), SystemPhase.Update, group: "survival");

// 4) spawn an entity that uses it
var e = frame.CreateEntity();
frame.Add(e, new HealthComponent { Current = 50, Max = 100 });
frame.Add(e, new RegenComponent  { PerTick = 2 });
```

Now add a reaction, and notice that neither piece needs a cleanup system:

```csharp
// A one-tick marker: whoever heals to full sets it, and it is gone by the next tick (§3).
[KlothoComponent(103)]
[KlothoCleanup(CleanupMode.RemoveComponent)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct HealedToFullComponent : IComponent { public int Overheal; }

// A signal listener that fires the moment regeneration is granted (§6).
public class RegenGrantedSystem : ISystem, ISignalOnComponentAdded<RegenComponent>
{
    public void Update(ref Frame frame) { }

    public void OnAdded(ref Frame frame, EntityRef entity, ref RegenComponent regen)
    {
        if (regen.PerTick <= 0) regen.PerTick = 1;    // fix up what was just added — allowed
    }
}
```

That is the whole loop: define unmanaged components, write systems that filter and mutate them by `ref`,
register them in a phase, and declare the lifetimes you do not want to hand-manage. The `Frame` snapshots and
hashes itself, so rollback and cross-peer determinism come for free — as long as the
[determinism rules](#11-determinism-rules-must-read) hold.

> Looking for a specific task rather than the mechanics? [Cookbook.md](Cookbook.md) indexes "I want to X" →
> the document or sample line that answers it, including four worked recipes (projectile, knockback, one-shot
> effect, deterministic target selection).
