# Brawler Appendix D — Bot HFSM Builder & Decision Predicates

> Related: [Brawler.md](Brawler.md) §10 (Phase 7 — Bot HFSM) · [HFSM.md](../HFSM.md) (engine HFSM framework)
> Target: `BotHFSMRoot.Build()` assembly + Decision criteria
>
> ⚠️ **Note**: The snippets in §D-3 / §D-5 match the actual source (decisions derive `HFSMDecision`, actions derive `AIAction`, both taking `ref AIContext`; values flow through `AIParam<T>` / `AIFunction<T>`). Per-class skill logic lives in `BotFSMHelper` (§D-6). Threshold *values* (margin, knockback %, cooldowns) come from `BotDifficultyAsset` JSON and may change.

---

## D-1. Structure Overview

```
BotFSMSystem (Update, multi-pass)
   │ — Pass 1 runs HFSMManager.Update(frame, entity, ref AIContext) for every entity
   │   matching <Transform, Character, Bot, PhysicsBody, HFSMComponent>
   ▼
HFSMManager + per-entity HFSMComponent (drives the graph registered under BotHFSMRoot.Id=1)
   │   graph is built once via BotHFSMRoot.Build() and registered into the HFSMRoot registry
   │
   ├── 5 states: Idle(0) / Chase(1) / Attack(2) / Evade(3) / Skill(4)
   │
   ├── Common transitions (evaluated in priority order in each state, **excluding self**):
   │     P=90: ShouldEvade      → Evade
   │     P=80: IsKnockback      → Chase
   │     P=70: InAttackRange    → Attack
   │     P=60: ShouldUseSkill   → Skill
   │     P=50: HasTarget        → Chase
   │     P=40: NoTarget         → Idle
   │
   ├── Evade-only transition:
   │     P=50: EvadeArrived     → Idle
   │
   ├── Skill-only transition:
   │     P=100: SkillActionDone → Chase
   │
   └── OnEnter / OnUpdate Actions per state
```

State-transition diagram:

```
 Idle ─HasTarget(50)→ Chase ─InAttackRange(70)→ Attack
  ↑                      │                         │
  NoTarget(40)      ShouldUseSkill(60)        SkillActionDone(100)
  │                      ▼                         │
  └─ ShouldEvade(90) → Evade                     Skill
                        │
                   EvadeArrived(50) → Idle
```

> **Note**: Transition targets — `T_Knockback` returns to `Chase`, and `T_SkillDone` returns to `Chase` (immediately re-enters the next decision loop after attack/skill completes).

---

## D-2. BotHFSMRoot.Build() — Actual Implementation

```csharp
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    public static class BotHFSMRoot
    {
        public const int Id = 1; // HFSMRoot registry key

        public const int Idle   = BotStateId.Idle;   // 0
        public const int Chase  = BotStateId.Chase;  // 1
        public const int Attack = BotStateId.Attack; // 2
        public const int Evade  = BotStateId.Evade;  // 3
        public const int Skill  = BotStateId.Skill;  // 4

        // Singleton Decisions / Actions (graph is shared by every entity, so these are stateless)
        static ShouldEvadeDecision     _shouldEvade;
        static IsKnockbackDecision     _isKnockback   = new IsKnockbackDecision();
        static InAttackRangeDecision   _inAttackRange;
        static ShouldUseSkillDecision  _shouldUseSkill;
        static HasTargetDecision       _hasTarget     = new HasTargetDecision();
        static NoTargetDecision        _noTarget      = new NoTargetDecision();
        static EvadeArrivedDecision    _evadeArrived  = new EvadeArrivedDecision();
        static SkillActionDoneDecision _skillDone     = new SkillActionDoneDecision();

        static ClearDestinationAction _clearDest  = new ClearDestinationAction();
        static EvadeEnterAction       _evadeEnter;
        static SkillUpdateAction      _skillUpdate;

        public static void Build(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets,
                                 BasicAttackConfigAsset attack, SkillConfigAsset[][] skills)
        {
            if (HFSMRoot.Has(Id)) return;   // prevent duplicate build

            // 1) Initialize Decisions / Actions. Difficulty-driven values flow through AIParam:
            //    AIParam.From(AIFunction) reads per-entity difficulty at Decide-time; AIParam.Const
            //    captures a fixed value. The Decision logic itself never touches the asset arrays.
            _shouldEvade    = new ShouldEvadeDecision(
                                  AIParam.From(new EvadeMarginByDifficulty(diffAssets)),
                                  AIParam.From(new EvadeKnockbackPctByDifficulty(diffAssets)),
                                  AIParam.Const(behavior.StageBoundary));
            _inAttackRange  = new InAttackRangeDecision(AIParam.Const(attack.MeleeRangeSqr));
            _shouldUseSkill = new ShouldUseSkillDecision(behavior, diffAssets, skills);
            _evadeEnter     = new EvadeEnterAction(behavior);
            _skillUpdate    = new SkillUpdateAction(behavior, diffAssets, skills);

            // 2) Assemble the graph fluently — each State() omits its own self-transition.
            //    To(...) adds a transition; Build() validates the graph (duplicate / dangling /
            //    non-dense ids, default-not-set, reachability) and stably sorts each state's
            //    transitions by descending priority before registering it under Id.
            //    Priorities are named constants in BotPriority (below).
            //    Named(...) is a display name only (Unity HFSM window / overlays), never read by the runtime.
            new HFSMBuilder(Id)
                .Default(Idle)
                .State(Idle).Named(nameof(Idle))                   // excludes the self transition
                    .OnEnter(_clearDest)
                    .To(Evade,  _shouldEvade,    priority: BotPriority.Evade)
                    .To(Chase,  _isKnockback,    priority: BotPriority.Knockback)
                    .To(Attack, _inAttackRange,  priority: BotPriority.Attack)
                    .To(Skill,  _shouldUseSkill, priority: BotPriority.Skill)
                    .To(Chase,  _hasTarget,      priority: BotPriority.HasTarget)
                .State(Chase).Named(nameof(Chase))                 // excludes the hasTarget transition
                    .To(Evade,  _shouldEvade,    priority: BotPriority.Evade)
                    .To(Chase,  _isKnockback,    priority: BotPriority.Knockback)
                    .To(Attack, _inAttackRange,  priority: BotPriority.Attack)
                    .To(Skill,  _shouldUseSkill, priority: BotPriority.Skill)
                    .To(Idle,   _noTarget,       priority: BotPriority.NoTarget)
                .State(Attack).Named(nameof(Attack))               // excludes the self transition
                    .OnEnter(_clearDest)
                    .To(Evade,  _shouldEvade,    priority: BotPriority.Evade)
                    .To(Chase,  _isKnockback,    priority: BotPriority.Knockback)
                    .To(Skill,  _shouldUseSkill, priority: BotPriority.Skill)
                    .To(Chase,  _hasTarget,      priority: BotPriority.HasTarget)
                    .To(Idle,   _noTarget,       priority: BotPriority.NoTarget)
                .State(Evade).Named(nameof(Evade))                 // committed: single exit transition
                    .OnEnter(_evadeEnter)
                    .To(Idle,   _evadeArrived,   priority: BotPriority.EvadeArrived)
                .State(Skill).Named(nameof(Skill))                 // committed: returns to Chase once the action lock clears
                    .OnEnter(_clearDest)
                    .OnUpdate(_skillUpdate)
                    .To(Chase,  _skillDone,      priority: BotPriority.SkillDone)
                .Build();
        }
    }

    public static class BotStateId
    {
        public const int Idle   = 0;
        public const int Chase  = 1;
        public const int Attack = 2;
        public const int Evade  = 3;
        public const int Skill  = 4;
    }

    // Transition priorities — higher is evaluated first (Build sorts each state's
    // transitions by descending priority).
    public static class BotPriority
    {
        public const int SkillDone    = 100; // Skill → Chase (committed exit)
        public const int Evade        = 90;
        public const int Knockback    = 80;  // → Chase
        public const int Attack       = 70;
        public const int Skill        = 60;
        public const int HasTarget    = 50;  // → Chase
        public const int EvadeArrived = 50;  // Evade → Idle (committed exit)
        public const int NoTarget     = 40;  // → Idle
    }
}
```

**Key types**:
- `HFSMBuilder` — Fluent assembler (`Default`, `State`, `OnEnter / OnUpdate / OnExit`, `Named`, `To`, `Build`). `Build()` validates the graph at registration and fails fast on structural defects (duplicate / dangling / non-dense state ids, default-not-set), runs a reachability BFS, and stably sorts each state's transitions by descending priority — the runtime evaluates transitions in array order, so the sort is what gives `priority` its meaning. `Named` may be called once per state and rejects a blank name; the name is stored on `HFSMStateNode.Name` for display only (the Unity HFSM window shows it in place of the id) and is never part of a snapshot or hash. Advisory findings (unreachable / duplicate priority / self-transition / two states sharing a name) warn via `IKLogger` by default; `Build(strict: true)` promotes them to throws.
- `HFSMRoot` — Root registry (`.Register`, `.Has`, `.Get`) + the instance type itself (`RootId`, `DefaultStateId`, `States`; each `States[id].Name` carries the `Named` display name). `HFSMBuilder.Build()` constructs and registers it for you.
- `HFSMManager` — Static driver over a per-entity HFSM component. `Init` / `Deinit` / `Update(ref Frame, EntityRef, ref AIContext)` / `GetLeafStateId` / `TriggerEvent`. The non-generic overloads operate on `HFSMComponent`; the generic `HFSMManager.Update<TComp>` form supports multiple HFSM axes per entity.
- `HFSMComponent` — Per-entity HFSM runtime state (active state chain, elapsed ticks, pending events) added by `HFSMManager.Init`. The bot filter requires it.
- `AIContext` — `ref struct` passed to every Decide/Execute call. Carries `Frame`, `Entity`, `NavQuery`, `CommandSystem`, `RayCaster`, `Logger`. Built fresh by `BotFSMSystem` each tick.
- `AIParam<T>` / `AIFunction<T>` — A value that resolves to a build-time constant (`AIParam.Const`) or a runtime source (`AIParam.From(AIFunction<T>)`). GC-free, no boxing. `AIFunction<T>` must be deterministic and stateless (singleton shared across entities, not rollback-tracked).
- `HFSMDecision` — Base for transition predicates: `bool Decide(ref AIContext context)`.
- `AIAction` — Base for state enter / update / exit actions: `void Execute(ref AIContext context)`.

Build timing: `BotHFSMRoot.Build()` is called once from `BotFSMSystem.OnInit()` (`IInitSystem`) after the DataAssets are resolved from the registry; `OnInit` then calls `HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id)` for every pre-existing `BotComponent`.

---

## D-3. Decisions

All decisions derive `HFSMDecision` and implement `bool Decide(ref AIContext context)`. They read state through `context.Frame` / `context.Entity` and resolve difficulty-driven values through their `AIParam<T>` fields.

### D-3-1. ShouldEvadeDecision (Priority 90)

```csharp
public class ShouldEvadeDecision : HFSMDecision
{
    readonly AIParam<FP64> _evadeMargin;        // From(EvadeMarginByDifficulty)
    readonly AIParam<int>  _evadeKnockbackPct;  // From(EvadeKnockbackPctByDifficulty)
    readonly AIParam<FP64> _stageBoundary;      // Const(behavior.StageBoundary)

    public override bool Decide(ref AIContext context)
    {
        ref readonly var bot       = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
        ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
        ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);

        if (bot.EvadeCooldown > 0) return false;

        FP64 evadeMargin   = _evadeMargin.Resolve(ref context);
        int  knockbackPct  = _evadeKnockbackPct.Resolve(ref context);
        FP64 stageBoundary = _stageBoundary.Resolve(ref context);

        FPVector3 pos = transform.Position;
        bool nearEdge      = FP64.Abs(pos.x) >= stageBoundary - evadeMargin
                          || FP64.Abs(pos.z) >= stageBoundary - evadeMargin;
        bool highKnockback = character.KnockbackPower >= knockbackPct;

        return nearEdge || highKnockback;
    }
}
```

**Thresholds** (from `BotDifficultyAsset` — `Assets/Brawler/Data/BrawlerAssets.json`):
- Boundary: `StageBoundary - EvadeMargin` where `EvadeMargin` = Easy 1.0 / Normal 2.0 / Hard 3.0
- Knockback: `EvadeKnockbackPct` = Easy 50 / Normal 70 / Hard 85

### D-3-2. IsKnockbackDecision (Priority 80)

```csharp
public override bool Decide(ref AIContext context)
    => context.Frame.Has<KnockbackComponent>(context.Entity);
```

If currently in knockback, transition to Chase (interrupting any attack / skill).

### D-3-3. InAttackRangeDecision (Priority 70)

```csharp
readonly AIParam<FP64> _meleeRangeSqr;   // Const(attack.MeleeRangeSqr)

public override bool Decide(ref AIContext context)
{
    ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
    if (bot.AttackCooldown != 0) return false;
    ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
    if (character.ActionLockTicks > 0) return false;

    var targetRef = bot.Target;
    if (!targetRef.IsValid || !context.Frame.Has<TransformComponent>(targetRef)) return false;

    ref readonly var selfT   = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);
    ref readonly var targetT = ref context.Frame.GetReadOnly<TransformComponent>(targetRef);
    FPVector3 d = targetT.Position - selfT.Position;
    return d.x * d.x + d.z * d.z <= _meleeRangeSqr.Resolve(ref context);
}
```

### D-3-4. ShouldUseSkillDecision (Priority 60)

Delegates the per-class cooldown / range / danger check to `BotFSMHelper.ShouldUseSkill` (§D-6), then — only when the class has a **ranged** skill planned (per-class `RangedSlotMask`) and a `RayCaster` is available — runs a static-geometry line-of-sight ray and **fails the transition if the line is blocked**.

```csharp
// Per-class ranged-slot bitmask (bit0=Slot0, bit1=Slot1):
// Warrior(0): none / Mage(1): Slot0 / Rogue(2): Slot1 / Knight(3): none
static readonly int[] RangedSlotMask = { 0b00, 0b01, 0b10, 0b00 };

readonly BotBehaviorAsset     _behavior;
readonly BotDifficultyAsset[] _diffAssets;
readonly SkillConfigAsset[][] _skills;

public override bool Decide(ref AIContext context)
{
    ref var          bot       = ref context.Frame.Get<BotComponent>(context.Entity);
    ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
    if (character.ActionLockTicks > 0) return false;
    ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);

    var targetRef = bot.Target;
    if (!targetRef.IsValid || !context.Frame.Has<TransformComponent>(targetRef)) return false;

    ref readonly var targetT = ref context.Frame.GetReadOnly<TransformComponent>(targetRef);
    FPVector3 d       = targetT.Position - transform.Position;
    FP64      distSqr = d.x * d.x + d.z * d.z;

    if (!BotFSMHelper.ShouldUseSkill(ref context.Frame, context.Entity, ref bot, in character,
                                     transform.Position, targetRef, distSqr,
                                     in _behavior, in _diffAssets[bot.Difficulty], _skills))
        return false;

    // LOS check only when a ranged skill is planned for this class.
    int classIdx = character.CharacterClass;
    int mask     = (uint)classIdx < (uint)RangedSlotMask.Length ? RangedSlotMask[classIdx] : 0;
    if (mask != 0 && context.RayCaster != null)
    {
        FPVector3 eyeOffset = FPVector3.Up * _behavior.EyeHeight;
        FPVector3 from = transform.Position + eyeOffset;
        FPVector3 to   = targetT.Position   + eyeOffset;
        FPVector3 dir  = to - from;
        FP64      dist = dir.magnitude;
        if (dist > FP64.Zero)
        {
            var ray = new FPRay3(from, dir.normalized);
            if (context.RayCaster.RayCastStatic(ray, dist, out _, out _, out _))
                return false;   // static geometry between bot and target → don't fire
        }
    }

    return true;
}
```

### D-3-5. HasTargetDecision / NoTargetDecision (Priority 50 / 40)

```csharp
public class HasTargetDecision : HFSMDecision {
    public override bool Decide(ref AIContext context) {
        ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
        return bot.Target.IsValid;
    }
}

public class NoTargetDecision : HFSMDecision {
    public override bool Decide(ref AIContext context) {
        ref readonly var bot = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
        return !bot.Target.IsValid;
    }
}
```

> **Target lifecycle** (in `BotFSMSystem`, not the decisions): each tick `BotFSMHelper.ValidateTarget` clears `bot.Target` if the target is dead/missing, and when `DecisionCooldown` elapses with no valid target `BotFSMHelper.SelectTarget` picks a new one before `HFSMManager.Update` runs. The decisions above only read the current `bot.Target.IsValid`.

### D-3-6. EvadeArrivedDecision / SkillActionDoneDecision

```csharp
public class EvadeArrivedDecision : HFSMDecision {
    public override bool Decide(ref AIContext context) {
        ref readonly var bot       = ref context.Frame.GetReadOnly<BotComponent>(context.Entity);
        ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);
        if (!bot.HasDestination) return true;
        FPVector3 pos = transform.Position, dest = bot.Destination;
        FP64 dx = pos.x - dest.x, dz = pos.z - dest.z;
        return dx * dx + dz * dz <= FP64.FromInt(1);   // within 1m of the evade point
    }
}

public class SkillActionDoneDecision : HFSMDecision {
    public override bool Decide(ref AIContext context) {
        ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);
        return character.ActionLockTicks <= 0;
    }
}
```

---

## D-4. BotComponent — Actual Fields

```csharp
public enum BotState : byte     { Idle, Chase, Attack, Evade, Skill }
public enum BotDifficulty : byte { Easy = 0, Normal = 1, Hard = 2 }

[KlothoComponent(110)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct BotComponent : IComponent
{
    public byte        State;             // BotState
    public EntityRef   Target;
    public int         StateTimer;        // ticks spent in the current state
    public int         AttackCooldown;    // attack cooldown
    public int         DecisionCooldown;  // decision interval
    public byte        Difficulty;        // BotDifficulty
    public FPVector3   Destination;       // movement target (for skills / evade)
    public bool        HasDestination;
    public int         EvadeCooldown;     // re-evade cooldown
}
```

> **Correction vs. earlier doc versions**: The fields `TargetSearchCooldown` and `PendingSkillSlot` **do not exist**. Target re-acquisition uses `DecisionCooldown`; the skill slot is communicated via internal logic in `SkillUpdateAction` or via `CharacterComponent.ActiveSkillSlot`.

---

## D-5. Action Implementations

All actions derive `AIAction` and implement `void Execute(ref AIContext context)`. Note the actions only mutate `BotComponent` (destination / cooldown) and emit commands — they do **not** touch `NavAgentComponent` directly. `BotFSMSystem` owns the `BotComponent.Destination → NavAgentComponent` sync (§D-7, Pass 2).

### ClearDestinationAction

```csharp
public class ClearDestinationAction : AIAction {
    public override void Execute(ref AIContext context) {
        ref var bot = ref context.Frame.Get<BotComponent>(context.Entity);
        bot.HasDestination = false;
    }
}
```

Used as `OnEnter` for Idle, Attack, and Skill.

### EvadeEnterAction

```csharp
public class EvadeEnterAction : AIAction
{
    readonly BotBehaviorAsset _behavior;
    public EvadeEnterAction(BotBehaviorAsset behavior) { _behavior = behavior; }

    public override void Execute(ref AIContext context)
    {
        ref var          bot       = ref context.Frame.Get<BotComponent>(context.Entity);
        ref readonly var transform = ref context.Frame.GetReadOnly<TransformComponent>(context.Entity);
        ref readonly var character = ref context.Frame.GetReadOnly<CharacterComponent>(context.Entity);

        // Pick the EvadePoint farthest from the current position, then snap it onto the NavMesh.
        var evadeRaw     = BotFSMHelper.PickEvadePoint(transform.Position, in _behavior);
        var evadeSnapped = BotFSMHelper.SnapDestination(evadeRaw, transform.Position,
                                                        context.NavQuery, _behavior.NavSnapMaxDist,
                                                        out bool ok, character.PlayerId, "Evade");
        bot.Destination    = evadeSnapped;
        bot.HasDestination = ok;
        bot.EvadeCooldown  = _behavior.EvadeCooldownTicks;
    }
}
```

### SkillUpdateAction

`OnUpdate` Action — runs every tick while in the Skill state. It selects the slot via `BotFSMHelper.SelectSkillSlot`, builds an aim direction toward the target (flipped for Mage Slot 1 "danger evasion"), and emits a **`PlayerInputCommand`** with `HAS_SKILL_BIT` (plus `SKILL_SLOT_BIT` for slot 1) through `context.CommandSystem.OnCommand` — i.e. bots feed the same command path as players, bypassing `OnPollInput`. No `HAS_MOVE_BIT` is set, so the move handler is skipped and velocity is preserved. (Emitting in `OnUpdate`, before the transition is evaluated, avoids the dropped-command race.)

---

## D-6. BotFSMHelper — Key Utilities

Pure, stateless logic shared by `BotFSMSystem`, the decisions, and the actions (deterministic; FP64/int only):

```csharp
public static class BotFSMHelper
{
    // Target lifecycle
    public static void      ValidateTarget(ref Frame frame, ref BotComponent bot);
    public static EntityRef SelectTarget(ref Frame frame, EntityRef self,
                                         in CharacterComponent selfChar, FPVector3 selfPos,
                                         BotDifficulty difficulty, in BotBehaviorAsset behavior);

    // Destination
    public static void      UpdateDestination(ref Frame frame, EntityRef entity, ref BotComponent bot,
                                              in CharacterComponent character, FPNavMeshQuery query,
                                              in BotBehaviorAsset behavior, IKLogger logger = null);
    public static FPVector3 SnapDestination(FPVector3 desired, FPVector3 fallbackPos,
                                            FPNavMeshQuery query, FP64 navSnapMaxDist, out bool snapOk,
                                            int playerId = -1, string context = null, IKLogger logger = null);
    public static FPVector3 PickEvadePoint(FPVector3 position, in BotBehaviorAsset behavior);

    // Skill selection (per class / difficulty)
    public static bool ShouldUseSkill(ref Frame frame, EntityRef entity, ref BotComponent bot,
                                      in CharacterComponent character, FPVector3 position,
                                      EntityRef target, FP64 distSqr, in BotBehaviorAsset behavior,
                                      in BotDifficultyAsset diffAsset, SkillConfigAsset[][] skills);
    public static int  SelectSkillSlot(ref Frame frame, EntityRef entity, ref BotComponent bot,
                                       in CharacterComponent character, FPVector3 position, EntityRef target,
                                       in SkillCooldownComponent cooldown, byte diff, int extraDelay, int classIdx,
                                       in BotBehaviorAsset behavior, in BotDifficultyAsset diffAsset,
                                       SkillConfigAsset[][] skills);   // returns -1 if no slot is usable
    public static int  CountNearEnemies(ref Frame frame, EntityRef self, int selfPlayerId,
                                        FPVector3 position, FP64 rangeSqr);
}
```

- **`SelectTarget`** scores candidates by squared distance, biased per difficulty: Normal subtracts a knockback factor; Hard additionally adds a stock-count factor (favoring high-knockback / low-stock targets). Ties break on lower entity index for determinism.
- **`ShouldUseSkill` / `SelectSkillSlot`** branch on `CharacterClass` (0 Warrior / 1 Mage / 2 Rogue / 3 Knight): range bands, `SkillExtraDelay`-relaxed cooldowns, Mage danger-evasion, Knight near-enemy count (`CountNearEnemies`), with extra slot-1 behavior gated on `Normal`/`Hard`.

---

## D-7. Spawn & Registration

`BrawlerSimSetup.SpawnBots` spawns each bot from a class prototype (chosen by a seeded `DeterministicRandom`), adds a `BotComponent`, and registers it with the HFSM via `HFSMManager.Init`. **No `NavAgentComponent` is added at spawn** — `BotFSMSystem` attaches one lazily the first frame the bot has a destination (§D-7, Pass 2).

```csharp
int botPlayerId = maxPlayers + 1 + i;
int classIdx    = rng.NextIntInclusive(0, stats.Length - 1);
var spawnPos    = rules.SpawnPositions[botPlayerId % rules.SpawnPositions.Length];

EntityRef entity = classIdx switch {
    0 => frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos }),
    1 => frame.CreateEntity(new MagePrototype    { SpawnPosition = spawnPos }),
    2 => frame.CreateEntity(new RoguePrototype   { SpawnPosition = spawnPos }),
    3 => frame.CreateEntity(new KnightPrototype  { SpawnPosition = spawnPos }),
    _ => throw new System.ArgumentOutOfRangeException(nameof(classIdx)),
};
// ... set PlayerId / StockCount / OwnerId ...
frame.Add(entity, new BotComponent {
    State      = (byte)BotStateId.Idle,
    Difficulty = (byte)BotDifficulty.Easy,   // DecisionCooldown/AttackCooldown come from BotDifficultyAsset
});
HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
```

> `DecisionCooldown` and `AttackCooldown` are **not** seeded at spawn; `BotFSMSystem` refills them from `BotDifficultyAsset` (`DecisionCooldown` / `AttackCooldownBase`) per tick.

Each tick, `BotFSMSystem.Update` runs over the filter `<TransformComponent, CharacterComponent, BotComponent, PhysicsBodyComponent, HFSMComponent>` in five passes:

1. **FSM decision** — `ValidateTarget`, decrement `EvadeCooldown`; on `DecisionCooldown` expiry pick a target (`SelectTarget`) and run `HFSMManager.Update(ref frame, entity, ref context)`, then `UpdateDestination`; otherwise just `UpdateDestination`.
2. **NavAgent sync** — when `bot.HasDestination`, lazily add + position-sync a `NavAgentComponent` and push `bot.Destination`; otherwise `NavAgentComponent.Stop`.
3. **Nav simulation** — `_navSystem.Update` over the collected nav entities.
4. **Result feedback** — clear `bot.HasDestination` for agents that reached `Arrived`.
5. **Command injection** — translate nav velocity (with a PathFailed straight-line fallback) plus the Attack-state basic attack into a single `PlayerInputCommand` via `CommandSystem.OnCommand`.

Dead bots are reset via `ResetBotState` (clears target/destination, `HFSMManager.Deinit` → `Init`).
