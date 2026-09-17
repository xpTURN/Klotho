# Hierarchical FSM (HFSM)

A deterministic, ECS-native hierarchical finite state machine for authoring agent/bot AI. State graphs are assembled once via a fluent builder, registered by id, and ticked per-entity inside the deterministic simulation — so every client evaluates the same transitions in the same order on the same frame.

> **Engine scope**: the entire HFSM **runtime** — graph assembly (`HFSMBuilder` / `HFSMRoot`), the per-entity component (`HFSMComponent`), the driver (`HFSMManager`), and your own `HFSMDecision` / `AIAction` subclasses — is engine-agnostic core under `com.xpturn.klotho/Runtime/ECS/FSM/`. The same graph code runs **unchanged** on Unity, Godot, and the headless .NET server; nothing in the runtime references a host engine. Only two things are host-specific, and both are optional:
>
> - **Driving the tick** — *where* you call `HFSMManager.Update(...)` is your simulation system, which is engine-neutral C# either way; only its registration/spawn wiring touches the host.
> - **Visualization** — a real-time state-tree editor window currently ships for **Unity only** (`Tools ▸ Klotho ▸ Visualizer ▸ HFSM`, under `com.xpturn.klotho/Unity/Editor/FSM/`). On Godot and headless builds there is no dedicated window yet; inspect state through the engine-neutral query API instead (see [Debugging](#debugging)).

## Components

| Type | Role |
| ---- | ---- |
| `HFSMBuilder` | Fluent assembler. Collects states/transitions, validates the graph, sorts transitions by priority, and registers an `HFSMRoot`. |
| `HFSMBuilder.StateBuilder` | Per-state builder returned by `State(...)` (`OnEnter` / `OnUpdate` / `OnExit` / `Named` / `To`). |
| `HFSMRoot` | Built graph + static registry (`Register` / `Has` / `Get`). Holds `RootId`, `DefaultStateId`, and the dense `States[]` array. |
| `HFSMStateNode` | One state node: ids (`StateId` / `ParentId` / `DefaultChildId`), an optional display `Name`, the three action arrays, and `Transitions[]`. |
| `HFSMTransitionNode` | One transition: `Priority`, `TargetStateId`, `Decision`, `EventId`. |
| `HFSMState` | The actual per-axis runtime state (`[KlothoSerializableStruct]`): active-state stack, pending events, elapsed ticks. Embedded as a host component's first field. |
| `HFSMComponent` | Default single-axis host (`[KlothoComponent(200)] : IHFSMHost`) — embeds `HFSMState` as its first field. |
| `IHFSMHost` | Empty marker for a component that hosts an `HFSMState` as its first field (lets `HFSMManager<TComp>` reinterpret it). |
| `HFSMManager` | Static runtime driver: `Init` / `Update` / `TriggerEvent` / `GetLeafStateId` / queries — generic `<TComp>` for multi-axis, with non-generic overloads bound to `HFSMComponent`. |
| `HFSMDecision` | Abstract transition predicate — `bool Decide(ref AIContext)`. |
| `AIAction` | Abstract state action — `void Execute(ref AIContext)`. |
| `AIContext` | `ref struct` passed to decisions/actions: `Frame`, `Entity`, plus optional `NavQuery` / `CommandSystem` / `RayCaster` / `Logger`. |
| `AIParam<T>` | `readonly struct` value holder — a build-time constant **or** an `AIFunction<T>` source. `Resolve(ref AIContext)` returns the value, allocation-free. Lets a decision/action field switch its value source without touching the decision logic. |
| `AIFunction<T>` | Abstract computed value source for `AIParam<T>` (`T Resolve(ref AIContext)`). Reads config/components/services; may hold sub-`AIParam<T>`s for chaining. |
| `HFSMValidationException` | Thrown by `Build()` on a structurally broken graph (or, with `strict: true`, on advisory findings). |

## File Layout

```text
com.xpturn.klotho/Runtime/ECS/FSM/
├── HFSMBuilder.cs            # fluent assembler + validation
├── HFSMRoot.cs              # HFSMRoot / HFSMStateNode / HFSMTransitionNode + registry
├── HFSMState.cs             # HFSMState (runtime state) + IHFSMHost marker
├── HFSMComponent.cs         # default single-axis host component (embeds HFSMState)
├── HFSMManager.cs           # Init / Update / transitions / events (generic over host TComp)
├── HFSMDecision.cs          # transition predicate base
├── AIAction.cs              # state action base
├── AIContext.cs             # context ref struct
├── AIParam.cs               # AIParam<T> value holder + AIFunction<T> source base
└── HFSMFixedArrayReaders.cs # serialization helpers for the fixed buffers

com.xpturn.klotho/Unity/Editor/FSM/   # Unity-only editor tooling (not part of the runtime)
├── HFSMVisualizerWindow.cs  # real-time state-tree window
├── HFSMStateTreeRenderer.cs
└── HFSMReflectionCache.cs
```

> Everything under `Runtime/ECS/FSM/` is shared core; the `Unity/Editor/FSM/` tooling is the only engine-specific HFSM code in the package. There is currently no Godot-side equivalent.

## Core Concepts

### States & hierarchy

Each state has an integer `StateId` and may declare a `ParentId` and a `DefaultChildId` (both default to `-1` = none).

- A state with a `DefaultChildId` is a **composite** state: entering it automatically descends into its default child (recursively) until a leaf is reached. The set of active states from root to leaf is the **active chain**.
- A state with no children is a **leaf**.
- `StateId`s must be **dense** starting at `0` (`StateId == array index`); the runtime indexes `States[]` directly. Gaps are a validation error.

The active chain depth is capped at `HFSMComponent.MaxDepth = 8`.

### Transitions

A transition is `To(targetStateId, decision, priority, eventId = 0)`:

- **`priority`** — higher is evaluated first. `Build()` stably sorts each state's transitions by descending priority; the runtime then walks them in array order. Equal priorities keep declaration order (and earn an advisory warning).
- **`decision`** — an `HFSMDecision`; the transition fires only if `Decide(ref context)` returns `true`. May be `null` (always-true gate).
- **`eventId`** — if non-zero, the transition is additionally gated on a matching pending event (see [Events](#events)). `0` means "no event gate".

> **Transitions are inherited down the active chain (leaf → root).** Each tick the runtime evaluates the active states from the **leaf upward to the root**; the first firing transition wins. A parent's transitions therefore apply to all of its descendants — declare a shared transition (e.g. "always evade when near the edge") **once on the common parent** instead of repeating it on every leaf.
>
> **Child level takes precedence over priority value.** Because evaluation is level-ordered (child first), a child's transition preempts a parent's even if the parent's `priority` number is higher. `priority` only orders transitions **within the same state**.

> **Caveats of inheritance** (hierarchical authoring):
> - **No automatic protection for "committed" states.** A parent transition can interrupt a child that is meant to run to completion (e.g. a skill cast). If a state must be atomic, make it a top-level (root) state, or gate the parent transition's decision on "not currently committed".
> - **`StateElapsedTicks` is leaf-scoped.** It resets on every state change, so it measures time since the last transition — *not* time since entering a composite. A parent transition's decision that needs "time in this composite" must track it in its own component field; there is no per-level elapsed counter.
> - **Beware per-tick thrash.** A parent transition whose condition stays true will fire every tick, repeatedly exiting/re-entering the subtree. Design parent conditions to settle.

### Actions

Each state may carry up to one array each of `OnEnter`, `OnUpdate`, and `OnExit` actions (`AIAction[]`):

- **`OnEnter`** — runs when the state is entered, in root→leaf order along the entered portion of the chain.
- **`OnExit`** — runs when the state is exited, in leaf→root order along the exited portion.
- **`OnUpdate`** — runs every tick along the **whole active chain in root→leaf order** (parent before child), before transitions are evaluated.

## Building a Graph

`HFSMBuilder` is a one-time, init-time assembler (its allocations are not on a per-frame path). In the fluent chain, call `Default(...)` **before** the first `State(...)` — the `StateBuilder` that `State(...)` returns has no `Default` method. (`Build()` itself imposes no ordering, so a statement-style builder local may set it later.)

```csharp
using xpTURN.Klotho.ECS.FSM;

public static class BotHFSMRoot
{
    public const int Id = 1; // HFSMRoot registry key

    public const int Idle = 0, Chase = 1, Attack = 2, Evade = 3, Skill = 4;

    public static void Build(/* config assets */)
    {
        if (HFSMRoot.Has(Id)) return; // guard against a duplicate build

        new HFSMBuilder(Id)
            .Default(Idle)
            .State(Idle).Named(nameof(Idle))
                .OnEnter(_clearDest)
                .To(Evade,  _shouldEvade,    priority: 90)
                .To(Chase,  _isKnockback,    priority: 80)
                .To(Attack, _inAttackRange,  priority: 70)
                .To(Skill,  _shouldUseSkill, priority: 60)
                .To(Chase,  _hasTarget,      priority: 50)
            .State(Chase).Named(nameof(Chase))
                .To(Evade,  _shouldEvade,    priority: 90)
                .To(Attack, _inAttackRange,  priority: 70)
                .To(Idle,   _noTarget,       priority: 40)
            .State(Skill).Named(nameof(Skill))       // committed state: single exit
                .OnEnter(_clearDest)
                .OnUpdate(_skillUpdate)
                .To(Chase,  _skillDone,      priority: 100)
            // ... Attack, Evade ...
            .Build();
    }
}
```

A composite state declares its parent/default-child via the `State` overload:

```csharp
.State(Combat, parentId: -1, defaultChildId: Chase)  // entering Combat descends into Chase
.State(Chase,  parentId: Combat)
.State(Attack, parentId: Combat)
```

`Named(...)` gives a state a display name — `nameof` keeps it in step with the constant. Names are **display only**: the Unity HFSM window shows them in place of ids, and your own overlays can read `HFSMRoot.Get(rootId).States[id].Name`, but the runtime never reads them, they are not part of any snapshot or hash, and logic must not branch on them. A state without a name shows its id.

`Build()` returns the registered `HFSMRoot`. The builder constructs and registers it for you via `HFSMRoot.Register` — you normally never construct `HFSMRoot`/`HFSMStateNode` arrays by hand.

### Reusing a transition set across states

There is no first-class "transition set" type — reuse is a build-time concern, and a plain helper method covers it.

**Reach for inheritance first.** A transition shared by states that have a common ancestor belongs **once on that ancestor** (see [Transitions](#transitions)): it is inherited by every descendant, so no per-state repetition is needed. You only need to repeat a transition set when the states that share it have **no common ancestor** (different branches), or when placing it on the ancestor would wrongly inherit it to siblings you want to exclude.

For that case, extract the `.To(...)` calls into a helper that takes — and returns — the `StateBuilder`:

```csharp
// Shared transition bundle — one ordinary method. Returns the StateBuilder so the chain can continue.
static HFSMBuilder.StateBuilder CommonExits(HFSMBuilder.StateBuilder sb) => sb
    .To(StateId.Death, _isDead,    priority: 100)
    .To(StateId.Flee,  _lowHealth, priority: 90);
```

Because the helper takes the `StateBuilder` as an argument, the call reads most cleanly in **statement style** with a builder local rather than as one long fluent chain:

```csharp
var b = new HFSMBuilder(Id).Default(Patrol);
CommonExits(b.State(Patrol)).To(/* patrol-specific */ Investigate, _heardNoise, priority: 50);
CommonExits(b.State(Chase));
b.Build();
```

This is pure authoring sugar: the helper just calls `To(...)` per state, so the built graph, priority sort, and runtime evaluation are **identical** to writing the transitions out by hand. There is no runtime cost, no new type, and determinism/GC are unaffected.

### Validation rules

`Build()` fails fast (throws `HFSMValidationException`) on structural defects:

| Rule | Failure |
| ---- | ------- |
| `Default(...)` must be called | "Default state not set" |
| State ids ≥ 0, no duplicates | "must be >= 0" / "Duplicate state" |
| Default state must be declared | "Default state … not declared" |
| `ParentId` / `DefaultChildId` / transition targets must reference declared states | "… not declared" / "transitions to undeclared state" |
| State ids must be dense `0..maxId` | "States must be dense … (StateId==index)" |
| `OnEnter` / `OnUpdate` / `OnExit` set at most once per state | "… set more than once" |
| `Named` set at most once per state, with a non-blank name (thrown at the call, not at `Build()`) | "… Named set more than once" / "name must not be null or blank" |
| A state's `DefaultChildId` target must have that state as its `ParentId` | "DefaultChild … has ParentId …, expected …" |
| The default (entry) state must be top-level (`ParentId == -1`) | "Default state … must be top-level (ParentId == -1)" |
| No cycles in any `ParentId` chain | "… has a cycle in its ParentId chain" |
| Hierarchy depth must not exceed `MaxDepth` | "… hierarchy depth exceeds MaxDepth (8)" |

`Build()` also emits **advisory** findings — by default logged as warnings via the optional `IKLogger`, but promoted to throws when you call `Build(strict: true)`:

- a state unreachable from the default state (reachability BFS over transitions + default-child chain),
- a duplicate transition priority within a state,
- a self-transition,
- two states in one root sharing a display name.

```csharp
new HFSMBuilder(Id, logger).Default(Idle)./* … */.Build(strict: true);
```

## Runtime Lifecycle

### Init

```csharp
HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
```

Adds an `HFSMComponent`, enters the root's default state (descending the default-child chain, running `OnEnter` actions), and resets `StateElapsedTicks`. Throws if the entity already has an `HFSMComponent`, or if `rootId` was never registered. In Brawler this runs once per bot inside `InitSystem`/spawn.

The parameterless overload runs the default state's `OnEnter` with a **service-less** context (`NavQuery`/`CommandSystem`/`RayCaster`/`Logger` all null). If your default state's `OnEnter` needs those services, pass a filled context:

```csharp
HFSMManager.Init(ref frame, entity, rootId, ref context); // OnEnter sees context's services
```

### Deinit

```csharp
HFSMManager.Deinit(ref frame, entity);           // or Deinit(ref frame, entity, ref context)
```

The mirror of `Init`: runs `OnExit` for the whole active chain (leaf→root) and then removes the `HFSMComponent`. No-op if the entity has no component. Use this for teardown/respawn instead of `frame.Remove<HFSMComponent>` directly, so `OnExit` actions still run.

### Update (tick)

```csharp
var context = new AIContext
{
    Frame = frame, Entity = entity,
    NavQuery = _query, CommandSystem = _commandSystem,
    RayCaster = _rayCaster, Logger = frame.Logger,
};
HFSMManager.Update(ref frame, entity, ref context);
// or, with a default context:  HFSMManager.Update(ref frame, entity);
```

Each `Update`:

1. Runs `OnUpdate` actions along the **whole active chain (root→leaf)** and increments `StateElapsedTicks`.
2. Walks transitions **leaf→root** (child level first); within each state they are priority-sorted. A transition fires when: its `EventId` is `0` **or** matches a pending event, **and** its `Decision` is `null` **or** `Decide` returns `true`. The first match (anywhere up the chain) wins.
3. On a firing transition, performs the state change (exit/enter).

> Pending events are cleared at the end of **every** `Update`, whether or not one fired — so an event must be triggered in the same tick *before* that entity's `Update`.

> **At most one transition fires per `Update`.** `TryFireTransition` performs the first match and returns; the newly entered state's transitions are **not** re-evaluated this tick, so a multi-step cascade settles over multiple ticks. In the firing tick the entered state runs only its `OnEnter` — its first `OnUpdate` and transition evaluation happen on the next `Update`. This interacts with event gating: because pending events are cleared at the end of the firing `Update`, an **event-gated** transition in the just-entered state will *not* see that same event next tick (re-trigger it if you need to chain). Only **decision-gated** transitions naturally re-evaluate the following tick.

### State change (exit / enter)

`ChangeState` computes the **lowest common ancestor (LCA)** of the current leaf and the target via their ancestor chains, then:

- runs `OnExit` from the current leaf up to (but not including) the LCA — leaf→root order,
- runs `OnEnter` from just below the LCA down to the target — root→leaf order,
- descends the target's default-child chain (so transitioning to a composite state lands on its leaf),
- resets `StateElapsedTicks` to `0`.

A **self-transition** (target == current leaf) exits and re-enters only that leaf. When the two states share no common ancestor, the whole chain is exited and the whole target chain entered.

### Reading state

```csharp
int leaf = HFSMManager.GetLeafStateId(ref frame, entity);

Span<int> chain = stackalloc int[HFSMComponent.MaxDepth];
int depth = HFSMManager.GetActiveStateIds(ref frame, entity, chain); // root..leaf

HFSMManager.GetDebugInfo(ref frame, entity,
    out int rootId, out int activeDepth, out int stateElapsedTicks, out int pendingEventCount);
```

## Multiple HFSM axes (Compound)

One entity can run several **independent** HFSMs (e.g. movement + combat) by giving each axis its own host component type. A host is any `unmanaged partial struct : IComponent, IHFSMHost` that embeds `HFSMState` as its **first field**:

```csharp
[KlothoComponent(201)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct CombatHFSMComponent : IComponent, IHFSMHost
{
    public HFSMState State;   // must be the first field (offset 0)
}
```

Drive each axis with the generic `HFSMManager<TComp>` entry points (own `HFSMRoot` id, own `AIContext` if desired):

```csharp
HFSMManager.Init<MovementHFSMComponent>(ref frame, e, MovementRoot.Id, ref ctx);
HFSMManager.Init<CombatHFSMComponent>  (ref frame, e, CombatRoot.Id,   ref ctx);
// per tick
HFSMManager.Update<MovementHFSMComponent>(ref frame, e, ref moveCtx);
HFSMManager.Update<CombatHFSMComponent>  (ref frame, e, ref combatCtx);
```

- The non-generic `HFSMManager.Init/Update/...` calls are just `<HFSMComponent>` bound — single-axis code is unchanged.
- Each axis is a distinct component → independent transitions/events/elapsed, and each rolls back with the frame snapshot independently.
- **GC-free**: the generic entry points reinterpret the host's first field via `Unsafe.As` (no boxing, no copy); `HFSMState` is `[KlothoSerializableStruct]` so the host's generated codec serializes it inline.
- Each axis needs its own `[KlothoComponent]` id; reusing `HFSMState`'s graph rules (depth ≤ 8, etc.) per root.

> **"Independent" means the two state machines don't share state — it does *not* mean they can't step on each other.**
>
> What's isolated is each axis's *own* `HFSMState`: its active chain, its events, its elapsed ticks. The Move axis can never read or corrupt the Combat axis's current state, and vice versa. That part is automatic.
>
> But the *work* each axis does — the code inside `AIAction.Execute` / `HFSMDecision.Decide` — writes to **other** components (e.g. `BotComponent`), and the runtime puts no guard there. If the Move axis writes `bot.Destination` to chase, and the Combat axis also writes `bot.Destination` to back away, they're fighting over one field. Nothing detects or resolves that; you get whichever write happened last. So *picking the same target field* is the conflict, not the state machines themselves.
>
> Why this never comes up in a single graph: there, only **one** state is active at a time, and evaluation is leaf→root so a child preempts its parent (see [Transitions](#transitions)). One actor, one decision — arbitration is free. Splitting into axes is exactly the act of **giving that up** in exchange for true concurrency (move *and* fight in the same tick). Having opted into concurrency, you own the arbitration. Three ways, simplest first:
>
> 1. **Don't share the field (preferred).** Decide up front which component fields belong to which axis — Move owns `Destination`/velocity, Combat owns attack/skill cooldowns — and never let an axis write outside its set. If they never touch the same field, there is nothing to contend over. This is the real reason to split: clean, non-overlapping responsibilities.
> 2. **Let call order decide.** If they genuinely must write the same field, the order you call them in is fixed (`Update<Move>` then `Update<Combat>`), so the **last one called wins, every time** — same on every client, no desync. You just have to put the axis that should win *second*.
> 3. **One axis tells the other to back off.** Combat sets a flag like `bot.MovementLocked = true` while casting a skill; the Move axis's decisions read that flag and stand down. This is the same "set a component field as a signal" trick used for committed states in a single graph ([Caveats of inheritance](#transitions)).
>
> Rule of thumb: if the behaviors are **mutually exclusive** and fight over one resource (you're either moving *or* fighting), keep them in **one graph** and let the hierarchy arbitrate for free. Only split into **axes** when they must run **at the same time** *and* you can cleanly separate which fields each one writes.

## Events

Events are an additional, edge-triggered gate for transitions. Queue one with `TriggerEvent`, then gate a transition on it via the `eventId` argument of `To(...)`.

```csharp
const int EvtHit = 1;

// In the graph:
.State(Attack).To(Stagger, decision: null, priority: 100, eventId: EvtHit)

// Elsewhere, before this entity's Update runs this tick:
HFSMManager.TriggerEvent(ref frame, entity, EvtHit);
```

- A transition with `eventId != 0` fires only when a matching event is pending **and** its decision (if any) also passes.
- The pending queue holds at most `HFSMComponent.MaxPendingEvents = 4`; `TriggerEvent` returns `false` when full — **check the return value**, an ignored `false` is a silently dropped event.
- An event is **latched** from `TriggerEvent` until that entity's next `Update`, which consumes (clears) it whether or not it fired. So:
  - To handle an event on the same tick, trigger it **before** that entity's `Update` runs — i.e. mind the relative order of the triggering system and the FSM system. System order is deterministic (no desync), but a wrong order silently defers/drops the event.
  - An event whose gated transition's decision returned `false` is **gone** the next tick (consumed, not retained).

There is no built-in 1-tick retention; if you need an event to survive until consumed, re-trigger it each tick or model it as a component flag a decision reads.

## Writing Decisions & Actions

Subclass `HFSMDecision` / `AIAction` and read everything through the `AIContext`:

```csharp
public class InAttackRangeDecision : HFSMDecision
{
    readonly AIParam<FP64> _meleeRangeSqr;
    public InAttackRangeDecision(AIParam<FP64> meleeRangeSqr) => _meleeRangeSqr = meleeRangeSqr;

    public override bool Decide(ref AIContext context)
    {
        ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
        if (bot.AttackCooldown != 0) return false;
        // … fixed-point distance check against _meleeRangeSqr.Resolve(ref context) …
    }
}

public class ClearDestinationAction : AIAction
{
    public override void Execute(ref AIContext context)
    {
        ref var bot = ref context.Frame.Get<BotComponent>(context.Entity);
        bot.HasDestination = false;
    }
}
```

**Construct decisions/actions once and reuse them** (the Brawler graph holds them as `static` singletons). They are stateless apart from injected config, so a single instance is shared across all entities — keep per-entity mutable state in components, never in the decision/action object. The same rule applies to any `AIFunction<T>` source (below): one shared instance, no state carried across `Resolve` calls.

`AIContext` carries optional service handles. They are only populated if you set them when building the context (`HFSMManager.Update(ref frame, entity)` leaves them null), so guard before use:

```csharp
if (context.RayCaster != null) { /* line-of-sight check */ }
```

### Value sources — `AIParam<T>` / `AIFunction<T>`

A field typed `AIParam<T>` lets a decision/action read a value **without hard-coding where the value comes from** — the source is decided at graph-build time, in `BotHFSMRoot.Build`, not in the decision body. The decision just calls `Resolve(ref context)`.

A value comes from one of two places:

- **Constant** — `AIParam.Const(value)`: a fixed value baked at build time (e.g. a single config-asset field read once at startup).
- **Source** — `AIParam.From(new SomeFunction(...))`: an `AIFunction<T>` that computes the value at resolve time from the context (Frame/Entity/services) and constructor-captured config — e.g. a per-difficulty config lookup or any derived value.

```csharp
// AIFunction<T>: difficulty-driven config lookup. Stateless; reads Frame + injected assets.
public sealed class EvadeMarginByDifficulty : AIFunction<FP64>
{
    readonly BotDifficultyAsset[] _diff;
    public EvadeMarginByDifficulty(BotDifficultyAsset[] diff) => _diff = diff;
    public override FP64 Resolve(ref AIContext context)
        => _diff[context.Frame.GetReadOnly<BotComponent>(context.Entity).Difficulty].EvadeMargin;
}

// Wiring at build time — the decision body never changes when the source does:
_shouldEvade   = new ShouldEvadeDecision(
                     AIParam.From(new EvadeMarginByDifficulty(diffAssets)),  // difficulty source
                     AIParam.From(new EvadeKnockbackPctByDifficulty(diffAssets)),
                     AIParam.Const(behavior.StageBoundary));                 // constant
_inAttackRange = new InAttackRangeDecision(AIParam.Const(attack.MeleeRangeSqr));
```

- **Type inference**: prefer the non-generic `AIParam.Const(x)` / `AIParam.From(src)` (T is inferred) over `AIParam<FP64>.Const(...)`.
- **GC-free**: the source is a build-time singleton, so `Resolve` is just a null check plus a virtual call — no per-tick allocation. `AIParam<T>` is generic, so value types resolve without boxing.
- **Chaining**: an `AIFunction<T>` may hold sub-`AIParam<T>` fields and resolve them first. In a code DSL this is rarely needed (a function can read what it wants from `context` directly) — reach for it only when the *sub-value's* source must also be switchable.
- **Unassigned guard**: a `default(AIParam<T>)` (a struct field you forgot to wire) is caught at **graph build time** — `HFSMBuilder.Build()` walks every decision/action the graph references (recursing into `AIFunction` sources for chained sub-params) and throws `HFSMValidationException` on any unassigned `AIParam`, on **every** build configuration before the first tick. (`Resolve` additionally carries a `Debug.Assert` as a secondary in-sim guard, but that is `[Conditional("DEBUG")]` — compiled out of release and unreliable under Unity's default trace listeners, so the build-time pass is the real protection.) `From(null)` throws at construction.

> **When `AIParam` does *not* fit.** `AIParam<T>` wraps a single scalar value. A decision/action that passes a **whole config asset** into a helper (e.g. `BotFSMHelper.SelectSkillSlot(in behaviorAsset, in diffAsset, skills)`) still injects the asset directly — wrapping one of its scalar fields in an `AIParam` while the asset is also injected would only duplicate state. Migrate to `AIParam` the decisions whose value use is purely scalar (Brawler's `ShouldEvadeDecision` / `InAttackRangeDecision`); leave asset-bundle consumers on direct injection.

#### Determinism & rollback constraints on sources

An `AIFunction<T>` runs inside the deterministic simulation and is a build-time **singleton shared by every entity**, living **outside the Frame** (not rollback-tracked). So, on top of the [Determinism](#determinism) rules below, a source **must not carry mutable state across `Resolve` calls** — no caching/memoization in instance fields. Persisted state would leak between entities and survive rollback, causing desync. (A mutable scratch field is only safe if it is written and consumed entirely within a single `Resolve` call.)

### Determinism

`HFSMDecision.Decide` and `AIAction.Execute` run **inside the deterministic simulation** and are tracked by the Klotho determinism analyzer (`DeterminismKnownSymbols`). Their bodies must stay deterministic:

- use fixed-point math (`FP64`, `FPVector3`, …) — **no `float`/`double`**,
- **no** `System.Math` / `UnityEngine.Mathf` / `System.Random` / `UnityEngine.Random` / `System.DateTime`,
- for randomness, derive from the synchronized seed (e.g. `DeterministicRandom` over `RandomSeedComponent.Seed`),
- minimize GC: prefer `ref readonly` component access and `stackalloc` over per-tick allocations.

> **No structural changes inside `Decide`/`Execute` (or `OnEnter`/`OnExit`).** Do not add/remove components or create/destroy entities from within a decision or action while the FSM is ticking. `HFSMManager` holds a `ref` to the entity's `HFSMComponent` across these callbacks; removing an `HFSMComponent` (on this or — via storage compaction — another entity) can relocate that slot and leave the `ref` dangling. Set a component **field** to signal intent and let a separate system apply the structural change after the FSM tick (Brawler's bots emit a buffered command from `SkillUpdateAction` rather than mutating structure inline).

## Debugging

The engine-neutral way to inspect a live entity — works identically on Unity, Godot, and the headless server — is the query API on `HFSMManager`:

```csharp
int leaf = HFSMManager.GetLeafStateId(ref frame, entity);
HFSMManager.GetDebugInfo(ref frame, entity, out var rootId, out var depth, out var elapsed, out var pending);
// GetActiveStateIds / GetPendingEventIds for the full chain + queued events
```

Wire these into whatever overlay or log your host uses (a Godot `_Draw` overlay, a server log line, etc.). For states declared with `.Named(...)`, print `HFSMRoot.Get(rootId).States[leaf].Name` instead of the id.

On **Unity** specifically, the same data is also available visually: open **Tools ▸ Klotho ▸ Visualizer ▸ HFSM** to inspect a live entity's state tree, active chain, and pending events in real time. States show their `.Named(...)` names; a root with no named states shows ids and a hint. There is no Godot editor window for this yet — use the query API above.

## Worked Example

The Brawler sample implements a complete five-state bot AI on this system — graph assembly, decisions, actions, the driving system, and the per-difficulty tuning. See **[Samples/Brawler.D.BotHFSM.md](Samples/Brawler.D.BotHFSM.md)**, with sources under `Samples/Brawler/Assets/Brawler/Scripts/ECS/FSM/` (`BotHFSMRoot`, `BotDecisions`, `BotActions`, `BotFSMHelper`) and the driver in `ECS/Systems/BotFSMSystem.cs`.

Although it lives in the Unity sample, the AI itself is engine-neutral: `BotHFSMRoot`, the `HFSMDecision`/`AIAction` subclasses, and the `HFSMManager.Update` call are plain core C# with no Unity dependency. To run the same bot on Godot or the headless server you would reuse those files as-is and only re-author the host-side wiring (asset loading, entity spawn, system registration).
