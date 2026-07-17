# ECS Memory Optimization Guide

Explains **where** the memory Klotho ECS reserves each frame comes from, **how to see it**, and **how to
reduce it** — written so a first-time reader can follow.

There are two levers:

- **`maxCount` (slot cap)** — shrink the number of slots a component type reserves. **This alone is enough
  in most cases.**
- **Pruning (unused-type removal)** — drop an entirely-unused type outright. Narrow preconditions, so it is a
  **secondary** lever.

> **One picture first**: for performance and determinism, Klotho reserves memory **up front and generously**.
> If you say "entities can go up to 256," then even with only 3 alive it **reserves room for 256 every
> frame**. On top of that, rollback (the netcode) keeps **dozens of copies** of that frame. So optimization
> is ultimately about **cutting the room you'll never use**. Trimming just a few large components can reclaim
> several MB.

---

## 1. How the Memory Forms

A Klotho frame's state is **one big contiguous byte array (`byte[]` heap)**. To avoid per-tick `new`/GC (a
deterministic simulation must have no allocation jitter) and to keep the memory layout fixed for
hashing/snapshot/rollback, it is reserved in bulk up front and carved up internally.

Inside this heap, **each registered component type** reserves the following four regions **at a fixed size**.
The key point — **the reservation size is the same no matter how many instances exist right now (even zero)**.

> **What "registered" means — every component in a referenced assembly is collected automatically**: you do
> not register components by hand. At startup Klotho **scans every loaded (referenced) assembly** and
> **auto-registers** each struct tagged `[KlothoComponent]` via `[ModuleInitializer]`. So **any component
> defined in a referenced assembly (engine / shared packages included) is registered — and reserves memory
> every frame — whether or not your game actually uses it.** Reference the engine Gameplay assembly just for
> physics, and its navigation/FSM components come along and get registered too. This is the answer to "why do
> unused types cost memory," and the rationale for **pruning (§4)**, which drops such types entirely.

```
[ count(4) ][ sparse(maxEntities × 4) ][ dense(slotCapacity × 4) ][ components(slotCapacity × memSize) ]
```

This is the common ECS **"sparse-set"** storage scheme. In plain terms:

| Region | Size | What it does (plain) |
|---|---|---|
| **count** | 4 | How many instances are alive right now |
| **sparse** | `maxEntities × 4` | **"entity id → which slot"** lookup. To find by entity id in O(1), it reserves room for **the max entity count**. That's why it stays large even with few instances |
| **dense** | `slotCapacity × 4` | The reverse: **"which slot → which entity"** back-index |
| **components** | `slotCapacity × memSize` | The **actual component data**, packed tightly. Usually the **biggest share** (larger for higher-memSize types) |

Two length parameters matter, and it's important to tell them apart:

- **`maxEntities`** — the max number of entities that can exist this session. Sets the **sparse length**.
  A session-wide value, so an individual component cannot change it.
- **`slotCapacity`** — how many of **this component can exist at once**. Sets the **dense/components length**.
  **Its default is `maxEntities`** — i.e. it assumes "every entity might carry this component." **Waste
  usually starts here** (e.g. at most 2 bots, but NavAgent reserves 256 slots). A singleton component is by
  definition just one, so `slotCapacity = 1`.

**freeze**: this layout is computed once when the first `Frame` is built and then **fixed** (process-wide). It
never grows or shrinks at runtime — every peer must run on the **same memory layout** for determinism. So
optimization values must be set **before the game starts (config/attribute)**.

### Why "× rollback ring" is scary (this is where memory balloons)

Klotho is **rollback netcode**. When a remote input is mispredicted it must rewind to a past tick and
re-simulate, so it **keeps several copies of recent frames in a history ring**. That copy count is the
report's `ringHeaps` (dozens for prediction/rollback-heavy P2P; a few for server-authoritative SD).

Therefore:

```
total memory = (per-frame reservation sum) × (ring copies N)
```

Cut 1KB from one frame and it is reclaimed **×N**. This multiplication is why "a small saving has a big
effect," and it is the lever of optimization.

**Summary**: the waste is almost always **over-reservation — "only a few instances live, but `slotCapacity`
is pinned at `maxEntities` (e.g. 256)."** Reduce it two ways — **fewer slots (`maxCount`)** or **drop the type
outright (pruning)**.

---

## 2. Measuring Runtime Usage (measure first, don't guess)

Don't decide by feel which type wastes — **measure**. Turn on
`ISimulationConfig.ComponentMemoryPeakSampling` (dev/measurement only, off by default and zero cost when off)
and a per-tick peak sampler attaches, dumping a `[Mem]` report to the log at session end.

```
[Mem] maxEntities=256 ringHeaps=53 margin=1.5
[Mem] type                        mem  slot  live  peak    reserved   liveB   waste  util ->maxCount
[Mem] NavAgentComponent(11)       708    12     1     1       9,572     712   7,832    8%          2
[Mem] VelocityComponent(22)        24   256     0     0       8,196       0   7,168    0%          0
[Mem] PhysicsBodyComponent(25)    264    20     4     4       6,388   1,072   4,288   20%          6
[Mem] TransformComponent(1)        96    24     7     7       3,428     700   1,700   29%         11
[Mem] -- per-frame total: reserved 67,274 / live 3,318 / waste 42,368  (fixed sparse 21,504 / variable 45,686)
[Mem] -- x53 heaps: reserved 3,565,522 / live 175,854 / waste 2,245,504
```

### What each column means

| Column | Meaning |
|---|---|
| `mem` | Size of one component (bytes). The data one slot occupies |
| `slot` | `slotCapacity` — the number of concurrent slots **reserved** right now |
| `live` | Instances alive **right now** |
| `peak` | The **maximum** concurrent instances observed this session |
| `reserved` | **Total bytes this type takes per frame** (count+sparse+dense+components) |
| `liveB` | Variable bytes actually used = `live × (mem+4)` |
| `waste` | **Discarded variable bytes** = `(slot − live) × (mem+4)`. I.e. "reserved but unused slots" |
| `util` | Utilization = `live / slot`. **Lower = more over-reservation** (reserves a lot, uses little) |
| `->maxCount` | The tool's **recommended slot cap** = `ceil(peak × margin)`. Singleton=1, unmeasured=`-` |

### Reading one line (e.g. `NavAgentComponent`)

> `mem 708, slot 12, live 1, peak 1, waste 7,832, util 8%, ->maxCount 2`
>
> → A large 708-byte component, yet it **reserves 12 slots while using at most 1** (util 8%). The other 11
> slots × (708+4) ≈ **7.8KB thrown away every frame**. The tool suggests "2 slots is enough" (peak 1 × margin
> 1.5). So `slot 12 → 2` reclaims most of it.

### The bottom two lines (totals) are the key to decisions

```
reserved = fixed sparse (Σ maxEntities×4) + variable (count + dense + components)
```

The report totals split the reservation into two buckets. This split tells you **which lever to use**:

- **`fixed sparse`** — the sum of all types' sparse. Always `maxEntities` regardless of `slotCapacity`.
  → **`maxCount` can never reduce it.** Only **pruning** (removing the type itself) removes this floor.
- **`variable`** — dense + components. Proportional to slot count. → **the target of `maxCount` (slot cap)**.
  (`variable = every type's liveB + waste`.)

**Where to start**: rows with large `waste` and low `util` first. Especially **large-`mem` types** (NavAgent
708B, PhysicsBody 264B above) are top priority — explained with the formula in §3.

> ⚠️ Always measure in a **representative session** (play that actually pushes peak load). The cap value is
> only safe if `peak` captured the true maximum.

---

## 3. Per-Component Slot Cap — `maxCount` (the primary lever)

### What it does

It **caps** the slot count (`slotCapacity`) at `min(maxCount, maxEntities)`. That shrinks **dense +
components**, which scale with slot length. E.g. capping NavAgent slots 256 → 2 drops components from
`256×708` to `2×708`.

- **sparse stays** (always `maxEntities`). `maxCount` cannot touch this part.
- Singletons are 1 anyway, so it's irrelevant to them.
- Where you write the cap and the precedence are covered in "Authoring" below.

### Authoring — two ways (combinable)

There are two channels for the cap value. **Set neither** and the slot defaults to `maxEntities` (i.e. the
un-optimized state). **Set both** and **B overrides A**.

#### Method A — build-fixed default (`[KlothoComponent]` attribute)

Bake the cap into the component declaration. A **safe default** applied game-wide; changing it requires a
rebuild.

```csharp
[KlothoComponent(11, MaxCount = 8)]   // cap this type's slots at min(8, maxEntities)
public partial struct NavAgentComponent : IComponent { ... }
```

#### Method B — config override (`ISimulationConfig.ComponentMaxCountOverrides`)

A `typeId → cap` map to **tune per session/deployment**. No rebuild — just change config; applied
process-wide. **Set it only on the authority peer (SD = server / P2P = host)** and it is **auto-propagated to
joining peers (SD = client / P2P = guest) via `SimulationConfigMessage`**, so both sides use the same cap
(hence one side is enough).

Where to author it per config implementation:

- **Server (JSON)** — `simulationconfig.json`: `"ComponentMaxCountOverrides": { "11": 8, "25": 6 }`
- **Unity** — `USimulationConfig`: the inspector's `_componentMaxCountOverrides` list (TypeId/MaxCount pairs)
- **Godot** — `GodotSimulationConfig`: the `.tres` parallel arrays `MaxCountOverrideTypeIds` / `MaxCountOverrideValues`

#### Which to use

| | Method A (attribute) | Method B (config override) |
|---|---|---|
| **Use** | A safe default cap common to all deployments | Per stage/mode/server fine-tuning |
| **Change cost** | Requires rebuild | Swap config only (no rebuild) |
| **Precedence** | Low | **High (overrides A)** |

> **Recommended flow**: the component author bakes a **reasonable default cap with A**, and ops/tuning
> **adjusts specific types with B**. The final value is decided by `ResolveSlotCapacity`: **B > A > maxEntities**
> (all three clamp to `maxEntities` at the end).

### Why "focus on large components" is overwhelmingly effective

The reclaim formula gives it away:

```
reclaimed bytes (per frame) ≈ (slots removed) × (mem + 4)
```

**The amount reclaimed per slot removed is proportional to the component size (`mem`).** So:

- **NavAgent (mem 708)**: removing one slot reclaims `708 + 4 = 712B`. A large component makes each slot heavy.
- **Small type (mem 4, e.g. Owner)**: removing one slot reclaims only `4 + 4 = 8B`. Capping dozens is invisible.

∴ Capping just a **few large (high-`mem`) + low-`util`** types reclaims most of the `variable` waste. Far more
efficient than capping a pile of small components. **Focus on "a few big ones."**

### Representative large-component lineup (top cap candidates)

By `mem` size, from a real Brawler `[Mem]` report. **Engine/shared** types appear in any game using that
feature (physics, navigation, FSM, …), so watch them closely. `mem` is large usually because the struct
holds a **fixed-size array buffer** or **many fields**.

| Component | mem(B) | Owner | Why big | Measured util | Cap fit |
|---|---:|---|---|---:|---|
| `NavAgentComponent` | 708 | engine (nav) | path-waypoint fixed buffer | 8% | ★ top (large, low-util) |
| `PhysicsBodyComponent` | 264 | engine (physics) | many position/velocity/force/collision fields | 20% | ★ high |
| `PlatformComponent` | 120 | game (example) | moving-platform path/state | 1% | ★ top |
| `TransformComponent` | 96 | engine (core) | pos/rot/scale + previous (interpolation) | 29%\* | △ context-dependent |
| `CharacterComponent` | 84 | game (example) | many character stat/state fields | 16% | ○ |
| `HFSMComponent` | 64 | engine (hierarchical FSM) | state stack/timers | 8% | ○ |
| `BotComponent` | 60 | game (example) | bot AI state | 6% | ○ |

- **The four large engine/shared types** (NavAgent·PhysicsBody·Transform·HFSM) are common cap candidates for
  most games. Game components also get big with fixed arrays / many fields (e.g. Platform 120B, Character 84B).
- \* **Watch `TransformComponent`**: nearly every entity carries it, so in a real (crowded) game its `util` is
  high — the 29% above is a sample with only 7 entities. **A type whose `util` approaches `maxEntities` has
  little room to cap.** Cap a large type **only when its util is low.**
- Values differ per game/build, so **always confirm with your own `[Mem]` report**. The table above is only a
  feel for "what kind is big."

### Savings Before → After (grounded in the report)

Starting from **un-optimized** (every type at default `slotCapacity = maxEntities = 256`), applying the
**recommended cap (`->maxCount` = ceil(peak×1.5))** to the large types above reclaims this much. The formula
is `reserved/frame = 1028 + slot×(mem+4)` (where `1028 = count 4 + sparse 256×4`); the total multiplies by
this deployment's ring copies, 53 (an SD deployment has far fewer).

| Component | mem | cap | Before(`slot 256`) | After(capped) | Δ/frame | **×53 saved** |
|---|---:|---:|---:|---:|---:|---:|
| NavAgentComponent | 708 | 2 | 183,300 | 2,452 | 180,848 | **≈ 9.1 MB** |
| PhysicsBodyComponent | 264 | 6 | 69,636 | 2,636 | 67,000 | **≈ 3.4 MB** |
| PlatformComponent | 120 | 2 | 32,772 | 1,276 | 31,496 | **≈ 1.6 MB** |
| CharacterComponent | 84 | 5 | 23,556 | 1,468 | 22,088 | **≈ 1.1 MB** |
| HFSMComponent | 64 | 2 | 18,436 | 1,164 | 17,272 | **≈ 0.9 MB** |
| BotComponent | 60 | 2 | 17,412 | 1,156 | 16,256 | **≈ 0.8 MB** |
| **Total (6 types)** | | | **345,112** | **10,152** | **334,960** | **≈ 16.9 MB** |

- **Capping just 6 components reclaims ≈ 17MB** (this deployment). In particular **NavAgent alone is 9MB** —
  showing how expensive it is to leave one large component uncapped. The larger the rollback ring, the more
  this saving is amplified.
- This is the effect of **`maxCount` (slot cap) alone**. **Pruning** adds to it by also removing the
  `fixed sparse` (per type `maxEntities×4` = 1,024/frame) of a type *no system touches* — but only when §4's
  precondition holds.
- A game that already has some caps has that much less left to save. **The gap between your report's
  `->maxCount` and the current `slot`** is your actual headroom.

### How to pick the cap value

Start from the report's `->maxCount` (= `ceil(peak × margin)`). `margin` is **safety headroom** (default
1.5) — a buffer against spikes not seen during measurement (instances momentarily bunching up). **Set it too
tight** and exceeding the slot throws (capacity-overflow), so leave headroom over the representative session's
`peak`.

### Safety (guarantees preserved)

- **Determinism**: `slotCapacity` is a layout input, so **every peer must use the same value**. A config
  override is process-wide and propagated via `SimulationConfigMessage`, so **setting it only on the authority
  peer (SD = server / P2P = host)** makes joining peers (client/guest) match automatically — symmetry
  guaranteed.
- **No regression**: no `maxCount` (0) = `maxEntities` (exactly the un-optimized behavior).
- **Overflow guard**: exceeding the slot cap on `Add` does not silently corrupt — it throws a **clear
  exception**.

---

## 4. Removing Unused Components Outright — Pruning (use sparingly)

### What it does

Pruning means specifying a **"list of types to prune (a denylist)."** Types in the list get **no layout at
all** → count/sparse/dense/components **all 0**. Everything not listed is reserved (the default). It is the
only lever that removes the **`fixed sparse` floor** that `maxCount` cannot.

- **Being a denylist makes the default safe (fail-safe)**: not listing a type just reserves it — a miss costs
  "lost savings," not a crash. (Conversely, a "list what you use" scheme would prune — and crash on — any new
  component you forget to list. That's why we list only what to prune, a denylist.)

### Authoring

```csharp
// List only the types to prune (= unused). Type-based (no magic typeIds). Core [KlothoCoreComponent] types
// are auto-excluded by the registry (never pruned), so you needn't worry about them.
config.SetRuntimePrunedComponentTypeIds(new[] {
    ComponentStorageRegistry.GetTypeId<MyUnusedComponent>(),
    // ...
});
```

- An **empty (default)** `ISimulationConfig.PrunedComponentTypeIds` = **no pruning** (all types reserved = the
  un-optimized state). null, an empty list, and a core-only list are **all no-pruning**, so they are safe.
- Engine-essential types marked `[KlothoCoreComponent]` (Transform, etc.) are **force-excluded from the
  denylist** by the registry (listing one by mistake won't prune it).
- `SetRuntimePrunedComponentTypeIds` is common to every config implementation and is wire-propagated to
  joining peers (peer symmetry). Unity/Godot can also author it via the inspector / `.tres`
  `PrunedComponentTypeIds` array.

### ⚠ Why "sparingly" — "peak=0" does NOT mean you can drop it

This is the most common misconception. **If a registered system so much as scans that type with
`Filter`/`Has`/`Get`, it must be reserved even at zero instances.** Because `Filter`/`Has` actually opens that
type's storage every tick. Prune it (list it) but have a registered system touch it, and the `GetStorage`
guard **throws with the component name** (preventing silent misbehavior).

→ So **pruning only truly pays off for types "no registered system touches."**

**Measured (Brawler)**: `Health`/`Velocity`/`Combat` sit at `live=0·peak=0`, yet `PhysicsSystem`/`CombatSystem`
scan them every tick, so they **cannot be pruned** (they stay reserved in the report). Waste of such
"came-along-but-scanned" types is handled by **`maxCount` (slot cap)**, not pruning. That is why **for most
games maxCount is primary and pruning is secondary.**

**When pruning truly pays off**: when a specific stage/mode **doesn't register the system group at all**, so
that component is genuinely touched by no one. Only then is the sparse floor reclaimed.

### Example — a game that uses neither navigation nor hierarchical FSM

A classic payoff case: **a game that uses no navigation and no hierarchical FSM (HFSM)** (e.g. a physics-based
party game, a puzzle/card duel). Such a game **never registers `NavigationSystem` / the HFSM system**, so the
large shared components `NavAgentComponent` (708B — the largest) and `HFSMComponent` (64B) are **touched by no
system** → §4's precondition holds, safe to prune. (Referencing the engine package registers these types via
`[ModuleInitializer]` even if unused — "came-along, unused," hence a prune target.)

```csharp
// This game uses no navigation / HFSM → those systems aren't registered → deny the two components
config.SetRuntimePrunedComponentTypeIds(new[] {
    ComponentStorageRegistry.GetTypeId<NavAgentComponent>(),
    ComponentStorageRegistry.GetTypeId<HFSMComponent>(),
});
```

- **Effect**: pruning zeroes `count+sparse+dense+components` **entirely**. `maxCount` can't get here — even at
  peak=0 it can't make the slots 0 (0 = unspecified = maxEntities), and **the sparse floor (`maxEntities×4` =
  1,024/frame) remains**. Only pruning removes that floor. NavAgent being large, its slot reservation reclaim
  is big too.
- **Precondition recheck**: "unused" truly means **not registering the system that touches it**. If the system
  IS registered and only the instances are 0 (peak=0), `Filter`/`Has` opens the storage → **cannot prune**
  (§4 limit). So this example holds only in a "whole-system-group-absent" setup — which is why Brawler (which
  registers bot/physics/combat systems) sees low payoff.

### Safety

- **Process-wide freeze**: the prune-set is a process-level layout input, so **multiple rooms in one process
  cannot have different prune-sets** (watch out on a server mixing several games in one process). For truly
  per-match differences, each match needs a separate process.
- **Peer symmetry**: **set only on the authority peer (SD = server / P2P = host)** → propagated to joining
  peers (client/guest) via `SimulationConfigMessage` so both prune the same set. Verified deterministic under
  SD and P2P (prediction/rollback).
- **Misuse fail-fast**: touch a pruned type and `GetStorage` throws immediately with the component name.

---

## 5. Practical Workflow (in order)

1. **Measure**: turn on `ComponentMemoryPeakSampling` in a representative session and capture the `[Mem]` report.
2. **Cap the big ones first**: give `maxCount` to types with large `mem` and low `util` (= big `waste`). Start
   from `->maxCount`, with margin headroom.
3. **Re-measure**: confirm `util` rose and total `reserved`/`waste` dropped.
4. **(Optional) Prune**: in a specific mode, add to the denylist only the types no registered system touches.
   Leave everything else alone.
5. **Verify determinism**: after optimizing, confirm via e2e that peer state hashes match and desync is 0
   (layout is a determinism input, so this is mandatory).

---

## Appendix: Quick Reference

**Per-type per-frame reservation** = `4 + maxEntities×4 + slotCapacity×4 + slotCapacity×memSize` (4-byte aligned)
**Total memory** = sum of all types' reservations × rollback-ring copies (`ringHeaps`)

| Lever | Reduces | sparse floor | Good target | Risk/constraint |
|---|---|---|---|---|
| **maxCount** | `slotCapacity` (dense+components) | **can't reduce** | large-`mem`, low-`util` types | throws on slot overflow (avoid with cap headroom) |
| **Pruning** | the whole type (sparse included) | **removes** | types no system touches | throws if a system touches it (narrow applicability) |

**Decision in one line**: from the report, **large `mem` + low `util` → `maxCount` first**. A type entirely
unused in a specific mode where its system is absent → **pruning**. Both are determinism inputs, so **uniform
across peers + e2e verification** are mandatory.
