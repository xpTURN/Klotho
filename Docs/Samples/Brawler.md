# Brawler Sample Authoring Guide

> Target framework: **xpTURN.Klotho**
> Purpose: a practical authoring guide for a **2–4 player top-down brawler sample** that exercises every core Klotho feature (ECS · deterministic physics/navigation · Bot HFSM · DataAsset · View layer · Replay · ServerDriven).
> Audience: game developers building a Brawler-style multiplayer match sample from scratch.

> Last updated: 2026-04-24

---

## 1. Game Overview

| Item | Description |
|---|---|
| Genre | Top-down 3D multiplayer fighting (Smash-Bros-style) |
| Players | 2–4 (both P2P and Server-Driven modes supported) |
| View | Overhead camera (XZ movement plane + Y-axis jump/land) |
| Win condition | Push opponents off the stage (XZ `FPBounds2`) until their stocks are exhausted |
| Match settings | 3 stocks, 120-second time limit |
| Showcases | Bot AI (HFSM), replay, spectator, late join — all demonstrable |

### 1-1. Core Play Loop

- Players pick one of four classes (Warrior / Mage / Rogue / Knight)
- Top-down (XZ) movement + jump (Y) + basic attack (click) + 2 skills (Q/E)
- Hits accumulate `KnockbackPower` (%); a strong hit then knocks the target off the stage
- Leaving the boundary costs 1 stock and respawns the character at a spawn point
- The stage contains moving platforms, traps, and item-spawn zones

### 1-2. How to Run

See [Brawler.I.HowToRun.md](Brawler.I.HowToRun.md) for step-by-step instructions on running the sample in P2P, ServerDriven single-room, and ServerDriven multi-room configurations.

---

## 2. Klotho Feature Map

| Klotho Area | Brawler Usage |
|---|---|
| **ECS** (`Frame`, `[KlothoComponent]`, `ComponentStorage<T>`) | All character / platform / item / spawn-marker / bot state |
| **[KlothoSerializable]** source generator | Auto-generation of command/event typeIds + serialization |
| **[KlothoDataAsset]** + DataAssetRegistry | Character stats / skill config / bot difficulty as deterministic assets |
| **FP64 / FPVector2/3 / FPAnimationCurve** | All positions, velocities, knockback, cooldowns |
| **FPPhysicsWorld + FPRigidBody + FPCollider** | Character capsule body, kinematic moving platforms |
| **FPStaticCollider + FPStaticBVH** | Fixed walls / structures · ground raycasts |
| **FPNavMesh + FPNavMeshPathfinder + FPNavMeshFunnel** | Bot pathfinding |
| **NavAgentComponent + FPNavAgentSystem** | Bot movement (ECS-integrated, `[KlothoComponent(11)]`) |
| **HFSM** (BotHFSMRoot, BotActions, BotDecisions) | Bot AI decisioning |
| **ICommand / ICommandSender / IInputPredictor** | Player-input transmission and prediction |
| **SimulationEvent (Regular/Synced)** | VFX/sound (Regular) / stock changes / game over (Synced) |
| **EntityViewFactory + EntityViewUpdater + EntityView** | Automatic entity ↔ Unity GameObject mapping |
| **EntityViewComponent** | Animator/VFX submodules (prefab children) |
| **BindBehaviour / ViewFlags** | Local (Predicted) vs. remote (Verified + SnapshotInterp) render branching |
| **IReplaySystem** | Match record/playback with LZ4 compression |
| **SyncTestRunner** | Determinism regression testing |
| **IKlothoNetworkService / ServerDrivenClientService** | Both P2P and Server-Driven modes |

---

## 3. Assemblies & Project Layout

### 3-1. Five Assemblies

```
Assets/Klotho/Samples/Brawler/Scripts/
├── ECS/              (Brawler.ECS)            — deterministic code only
├── DataAssets/       (Brawler.DataAssets)     — static-config asset definitions
├── Manager/          (Brawler.Manager)        — callback impls, session controller
├── View/             (Brawler.View)           — Unity view layer (MonoBehaviour)
└── View/Editor/      (Brawler.View.Editor)    — editor-only view tooling
```

### 3-2. Assembly Reference Graph

```
Brawler.DataAssets   ──► xpTURN.Klotho.Runtime (Core + ECS)
Brawler.ECS          ──► Brawler.DataAssets + Klotho Runtime
Brawler.Manager      ──► Brawler.ECS + Klotho Runtime.Unity + LiteNetLib
Brawler.View         ──► Brawler.Manager + UnityEngine
Brawler.View.Editor  ──► Brawler.View + UnityEditor
```

All Runtime assemblies include `Microsoft.Extensions.Logging` + `ZLogger` as `precompiledReferences`.

### 3-3. Config Assets

| Asset File | Type (ScriptableObject Class) | Path | Create Menu | Purpose |
|---|---|---|---|---|
| **SimulationConfig.asset** | `USimulationConfig` (provided by Klotho) | `Samples/Brawler/Config/` | `Assets > Create > Klotho > Simulation Config` | TickIntervalMs, InputDelayTicks, MaxRollbackTicks, Mode, etc. |
| **BrawlerEntityViewFactory.asset** | `BrawlerEntityViewFactory` (sample-defined, inherits `EntityViewFactory`) | `Samples/Brawler/Config/` | `Assets > Create > Brawler > EntityViewFactory` | Character/Item prefab arrays (`_characterPrefabs[4]`, `_itemPrefabs[3]`) |

> **Terminology**: `USimulationConfig` is the class name (type); `SimulationConfig.asset` is the Unity asset created from that class. Whenever this guide says `SimulationConfig.asset`, it refers to an asset of type `USimulationConfig`.

---

## 4. Phase 1 — Define DataAssets

Klotho's DataAsset subsystem encapsulates static configuration that the deterministic simulation reads. Look up at runtime via `frame.AssetRegistry.Get<T>(assetId)`.

### 4-1. Asset List

| Class | typeId | Role |
|---|---|---|
| `CharacterStatsAsset` | 100 | Per-class Skill0/Skill1 IDs and base stats |
| `SkillConfigAsset` | 101 | Skill duration / cooldown / damage / hitbox |
| `BasicAttackConfigAsset` | 102 | Basic attack range / damage / knockback |
| `BotBehaviorAsset` | 103 | Bot targeting distance / evade radius |
| `BotDifficultyAsset` | 104 | Per-difficulty reaction speed / decision interval |
| `BrawlerGameRulesAsset` | 105 | Max stocks, spawn-position array, boundary size |
| `CombatPhysicsAsset` | 106 | Knockback damping coefficient, friction, hit stun |
| `ItemConfigAsset` | 107 | Item spawn period / position pool / valid ticks |
| `MovementPhysicsAsset` | 108 | Movement speed / acceleration / jump velocity / gravity |

### 4-2. Asset Definition Pattern

```csharp
[KlothoDataAsset(100)]
public partial class CharacterStatsAsset : ScriptableObject, IDataAsset
{
    public int AssetId => 1100;   // instance ID (1100–1103, one per class)
    [KlothoOrder] public FP64 Mass;
    [KlothoOrder] public FP64 Friction;
    [KlothoOrder] public FP64 ColliderHalfHeight;
    [KlothoOrder] public FP64 ColliderRadius;
    [KlothoOrder] public int Skill0Id;
    [KlothoOrder] public int Skill1Id;
    // Serialize/Deserialize/GetHash are emitted by the source generator
}
```

### 4-3. Registry Registration

Either use `BrawlerSimSetup.CreateDefaultDataAssets()` or inject an externally built registry via `KlothoSessionSetup.AssetRegistry`.

```csharp
var builder = new DataAssetRegistryBuilder();
builder.Register(warriorStats);  // AssetId=1100
builder.Register(mageStats);     // AssetId=1101
// ...
var registry = builder.Build();
```

---

## 5. Phase 2 — Component Definitions

Every component is `[KlothoComponent(ID)]` + `partial struct` + `IComponent`. IDs 1–99 are framework-reserved; **100+ is the game space**.

| Class | ID | Key Fields |
|---|---|---|
| `CharacterComponent` | 100 | PlayerId, CharacterClass (0–3), StockCount, KnockbackPower (%), IsDead, RespawnTimer |
| `PlatformComponent` | 101 | IsMoving, MoveStart/End (FPVector2), MoveSpeed, MovePhase |
| `ItemComponent` | 102 | ItemType (0=Shield / 1=Boost / 2=Bomb), RemainingTicks |
| `KnockbackComponent` | 103 | Force (FPVector2), DurationTicks |
| `SkillCooldownComponent` | 104 | Skill0/1Cooldown, ShieldTicks |
| `SpawnMarkerComponent` | 105 | SpawnPosition, PlayerId |
| `GameTimerStateComponent` | 106 | StartTick, LastReportedSeconds, GameOverFired |
| `GameSeedComponent` | 107 | WorldSeed (injected at OnGameStart) |
| `BotComponent` | 110 | State, Difficulty, TargetEntity, ActionCooldown |

**Built-in components used**: `TransformComponent`, `PhysicsBodyComponent` (RigidBody + Collider + ColliderOffset), `OwnerComponent` (OwnerId), `NavAgentComponent` (bots only).

---

## 6. Phase 3 — Command Definitions

Inherit from `CommandBase` + apply `[KlothoSerializable(N)]`. `CommandType` / `SerializeData` / `DeserializeData` are auto-generated.

| Class | typeId | Fields |
|---|---|---|
| `MoveInputCommand` | 100 | HorizontalAxis, VerticalAxis (FP64), JumpPressed, JumpHeld |
| `AttackCommand` | 101 | AimDirection (FPVector2) |
| `UseSkillCommand` | 102 | SkillSlot (0 or 1), AimDirection |
| `SpawnCharacterCommand` | 103 | CharacterClass, SpawnPosition |

---

## 7. Phase 4 — Event Definitions

Inherit from `SimulationEvent` + apply `[KlothoSerializable(N)]`. `EventMode.Regular` (immediate VFX on prediction) / `EventMode.Synced` (verified ticks only).

### Regular (7)

| Event | typeId | Purpose |
|---|---|---|
| `AttackHitEvent` | 100 | Hit-VFX / sound |
| `DashEvent` | 101 | Dash effect |
| `JumpEvent` | 106 | Jump effect |
| `GroundSlamEvent` | 107 | Knight ground-slam radial effect |
| `CharacterSpawnedEvent` | 108 | Spawn VFX |
| `AttackActionEvent` | 110 | Attack action start (Animator hook) |
| `SkillActionEvent` | 111 | Skill action start (Animator hook) |

### Synced (6)

| Event | typeId | Purpose |
|---|---|---|
| `ItemPickedUpEvent` | 102 | Item pickup confirmed |
| `CharacterKilledEvent` | 103 | Stock-decrement confirmed |
| `GameOverEvent` | 104 | Win/lose confirmed |
| `RoundTimerEvent` | 105 | Per-second tick (1× per second) |
| `TrapTriggeredEvent` | 109 | Trap activation confirmed |
| `ActionCompletedEvent` | 112 | Action lock release (cooldown begins) |

---

## 8. Phase 5 — Prototypes

Implement `IEntityPrototype`. Author as a struct for zero-GC. Reference DataAssets via `frame.AssetRegistry.Get<T>(assetId)`.

| Class | const Id | Composition |
|---|---|---|
| `WarriorPrototype` | 100 | Capsule + CharacterComponent + SkillCooldown + Owner |
| `MagePrototype` | 101 | Same shape, CharacterClass=1 |
| `RoguePrototype` | 102 | CharacterClass=2 |
| `KnightPrototype` | 103 | CharacterClass=3 |
| `MovingPlatformPrototype` | 200 | Transform + PhysicsBody (Kinematic) + Platform |
| `ItemPickupPrototype` | 300 | Transform + Item |

**Registration & creation**:

```csharp
simulation.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());
// ...
var entity = frame.CreateEntity(WarriorPrototype.Id);   // Apply auto-invoked
```

**`Apply()` pattern** (WarriorPrototype example — pulls capsule / friction / skill IDs from a DataAsset to assemble the entity):

```csharp
public struct WarriorPrototype : IEntityPrototype
{
    public const int Id = 100;

    public void Apply(Frame frame, EntityRef entity)
    {
        var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100);  // Warrior=1100

        frame.Add(entity, new TransformComponent());

        var rb = FPRigidBody.CreateDynamic(stats.Mass);
        rb.friction = stats.Friction;
        frame.Add(entity, new PhysicsBodyComponent
        {
            RigidBody = rb,
            Collider  = FPCollider.FromCapsule(
                new FPCapsuleShape(stats.ColliderHalfHeight, stats.ColliderRadius, FPVector3.Zero)),
            ColliderOffset = new FPVector3(FP64.Zero, stats.ColliderOffsetY, FP64.Zero),
        });

        frame.Add(entity, new CharacterComponent { CharacterClass = 0 });  // Warrior
        frame.Add(entity, new SkillCooldownComponent());
        frame.Add(entity, new OwnerComponent());  // PlayerId is injected when SpawnCharacterCommand is processed
    }
}
```

The other three character prototypes are identical except for `Get<CharacterStatsAsset>(1101..1103)` and `CharacterClass = 1..3`.

---

## 9. Phase 6 — Implement & Register Systems

### 9-1. System List (16 + EventSystem)

| System | Phase | Role |
|---|---|---|
| `SavePreviousTransformSystem` | PreUpdate | Save PrevPos/PrevRot for interpolation |
| `BotFSMSystem` | PreUpdate | HFSM tick / Action execution / Decision evaluation |
| `PlatformerCommandSystem` | PreUpdate | Process the 4 commands; per-class skill branching (`ICommandSystem` + `ISyncEventSystem`) |
| `ObstacleMovementSystem` | Update | PlatformComponent reciprocating motion |
| `TopdownMovementSystem` | Update | Apply gravity, rotate to XZ direction |
| `ActionLockSystem` | Update | Suppress movement during action lock; emit `ActionCompletedEvent` |
| `KnockbackSystem` | Update | Apply KnockbackComponent.Force; decrement Duration |
| `PhysicsSystem` | Update | ECS↔FPPhysicsBody sync; `FPPhysicsWorld.Step()` |
| `TrapTriggerSystem` | Update | Detect trap entry → add KnockbackComponent + `TrapTriggeredEvent` |
| `SkillCooldownSystem` | Update | Decrement cooldown ticks |
| `BoundaryCheckSystem` | Update | XZ-boundary exit (`FPBounds2`) → `CharacterKilledEvent` |
| `ItemSpawnSystem` | Update | Periodic item spawning via `DeterministicRandom` |
| `CombatSystem` | Update | Detect item pickup (`ItemPickedUpEvent`) |
| `RespawnSystem` | Update | RespawnTimer → return to spawn position |
| `TimerSystem` | Update | Time limit based on frame.Tick × DeltaTimeMs |
| `GroundClampSystem` | PostUpdate | Capsule-geometry-based ground clamp |
| `GameOverSystem` | PostUpdate | Stocks 0 / timeout → `GameOverEvent` (once) |
| `EventSystem` | LateUpdate | Bulk event dispatch |

### 9-2. Registration — `BrawlerSimSetup.RegisterSystems()`

```csharp
public static void RegisterSystems(EcsSimulation sim, ILogger logger)
{
    // Register prototypes
    sim.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());
    // ... other 5 prototypes

    var events = new EventSystem();

    // PreUpdate
    sim.AddSystem(new SavePreviousTransformSystem(), SystemPhase.PreUpdate);
    sim.AddSystem(new BotFSMSystem(/* NavMesh deps */), SystemPhase.PreUpdate);
    sim.AddSystem(new PlatformerCommandSystem(events, logger), SystemPhase.PreUpdate);

    // Update
    sim.AddSystem(new ObstacleMovementSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new TopdownMovementSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new ActionLockSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new KnockbackSystem(events, logger), SystemPhase.Update);
    PhysicsSystem = new PhysicsSystem(256, FPVector3.Zero);
    sim.AddSystem(PhysicsSystem, SystemPhase.Update);
    sim.AddSystem(new TrapTriggerSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new SkillCooldownSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new BoundaryCheckSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new ItemSpawnSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new CombatSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new RespawnSystem(events, logger), SystemPhase.Update);
    sim.AddSystem(new TimerSystem(events, logger), SystemPhase.Update);

    // PostUpdate
    sim.AddSystem(new GroundClampSystem(), SystemPhase.PostUpdate);
    sim.AddSystem(new GameOverSystem(events, logger), SystemPhase.PostUpdate);

    // LateUpdate
    sim.AddSystem(events, SystemPhase.LateUpdate);
}
```

---

## 10. Phase 7 — Bot HFSM

Brawler bot AI is built on a hierarchical state machine (HFSM).

### 10-1. File Layout (`ECS/FSM/`)

| File | Role |
|---|---|
| `BotHFSMRoot.cs` | Root FSM definition — 5 states (Idle/Chase/Attack/Evade/Skill) + 6 transitions |
| `BotDecisions.cs` | Decision predicates (`ShouldEvade`, `IsKnockback`, `InAttackRange`, `ShouldUseSkill`, `HasTarget`, `NoTarget`) |
| `BotActions.cs` | Enter / update actions (`ClearDestinationAction`, `EvadeEnterAction`, `SkillUpdateAction`) |
| `BotFSMHelper.cs` | NavMesh path-request / target-lookup helpers |

### 10-2. Transition Overview

```
Idle ─HasTarget(P=50)→ Chase ─InAttackRange(P=70)→ Attack
 ↑                         │                          │
 NoTarget(P=40)          ShouldUseSkill(P=60)     ActionCompleted
 │                         ▼                          ▼
 └── ShouldEvade(P=90) ─► Evade ◄─ IsKnockback(P=80) ─┘
                                                      │
                                                    Skill
```

Higher Priority is evaluated first. Evade / knockback take top priority.

### 10-3. Registration

`BotFSMSystem` holds a root-FSM instance per entity with `BotComponent` and calls `Tick()` every PreUpdate.

---

## 11. Phase 8 — Callbacks & Session Wiring

### 11-1. ISimulationCallbacks (Deterministic Common)

Implemented in `BrawlerSimulationCallbacks`. **Runs identically on every peer (host, guest, server, replay).**

```csharp
public class BrawlerSimulationCallbacks : ISimulationCallbacks
{
    public void RegisterSystems(EcsSimulation sim)
        => BrawlerSimSetup.RegisterSystems(sim, _logger);

    public void OnInitializeWorld(IKlothoEngine engine)
        => BrawlerSimSetup.InitializeWorldState(engine);  // timer · seed · platform singletons

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        // Read input from BrawlerInputCapture and send the 3 command kinds
        sender.Send(/* MoveInputCommand */);
        if (_input.Attack)         sender.Send(/* AttackCommand */);
        if (_input.SkillSlot >= 0) sender.Send(/* UseSkillCommand */);
    }
}
```

> ⚠️ `RegisterSystems` is called before `Engine.Initialize()`, and the Frame is initialized as part of that process — **do not create entities here**. Initial-world spawns belong in `OnInitializeWorld`.

### 11-2. IViewCallbacks (Client View Only)

Implemented in `BrawlerViewCallbacks`. **Non-determinism allowed** (UI / animation / sending spawn commands).

```csharp
public class BrawlerViewCallbacks : IViewCallbacks
{
    private readonly BrawlerSimulationCallbacks _sim;
    // _sim.SendSpawnCommand(engine) reads _playerConfig (a BrawlerPlayerConfig) internally
    // and emits a SpawnCharacterCommand.

    public void OnGameStart(IKlothoEngine engine)
    {
        if (!engine.IsReplayMode) _sim.SendSpawnCommand(engine);
    }
    public void OnTickExecuted(int tick) { /* update HUD if needed */ }
    public void OnLateJoinActivated(IKlothoEngine engine)
    {
        _sim.SendSpawnCommand(engine);  // spawn right after late join
    }
}
```

`BrawlerPlayerConfig` inherits `PlayerConfigBase` and applies `[KlothoSerializable]`; it is the payload that propagates the player's character selection to the host.

```csharp
[KlothoSerializable(MessageTypeId = (NetworkMessageType)200)]
public partial class BrawlerPlayerConfig : PlayerConfigBase
{
    [KlothoOrder] public int SelectedCharacterClass;   // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
}
```

`BrawlerGameController` calls `session.SendPlayerConfig(new BrawlerPlayerConfig { ... })` just before Ready; the host broadcasts it to all peers immediately on receipt. At spawn time, each peer looks up the `BrawlerPlayerConfig` for its `PlayerId` to decide `SpawnCharacterCommand.CharacterClass`.

### 11-3. KlothoSession.Create

Called from `BrawlerGameController` (a MonoBehaviour).

```csharp
var session = KlothoSession.Create(new KlothoSessionSetup
{
    Logger              = _logger,
    SimulationCallbacks = _simCallbacks,       // BrawlerSimulationCallbacks
    ViewCallbacks       = _viewCallbacks,      // BrawlerViewCallbacks
    Transport           = _transport,          // LiteNetLibTransport (host) / null (guest)
    Connection          = _connection,         // ConnectionResult (guest)
    SimulationConfig    = _uSimulationConfig,  // USimulationConfig asset
    AssetRegistry       = _assetRegistry,      // DataAssetRegistry
    MaxPlayers          = _settings.MaxPlayers,
    AllowLateJoin       = true,
});

GetComponent<UKlothoBehaviour>().Bind(session);
session.HostGame("Room01", maxPlayers: 2);  // or session.JoinGame(...)
```

---

## 12. Phase 9 — Unity View Layer (IMP24)

### 12-1. Three-Tier Structure

```
EntityViewUpdater (scene MonoBehaviour)
   │  Reconcile on every OnTickExecuted
   ▼
BrawlerEntityViewFactory : EntityViewFactory (ScriptableObject)
   │  TryGetBindBehaviour / GetViewFlags / CreateAsync / Destroy
   ▼
CharacterView / ItemView / PlatformView : EntityView (prefab)
   │  OnInitialize / OnActivate / OnUpdateView / OnLateUpdateView
   └── EntityViewComponent children (Animator/VFX modules)
```

### 12-2. BrawlerEntityViewFactory Rules

| Situation | BindBehaviour | ViewFlags |
|---|---|---|
| P2P / SD-Server / Replay mode | `NonVerified` (Predicted) | `None` |
| SD-Client local player | `NonVerified` | `None` |
| SD-Client remote player / NPC | `Verified` | `EnableSnapshotInterpolation` |
| Spectator-mode entire world | `Verified` | `EnableSnapshotInterpolation` |

For entities without `OwnerComponent` (items, etc.), use `UseVerifiedPath()` for a global decision.

### 12-3. EntityView Subclass

```csharp
public class CharacterView : EntityView
{
    [SerializeField] private Renderer[] _renderers;
    [SerializeField] private GameObject _shieldFx;
    [SerializeField] private GameObject _boostFx;

    public override void OnActivate(FrameRef frame) { base.OnActivate(frame); /* init */ }
    public override void OnDeactivate()             { base.OnDeactivate(); /* cleanup */ }
    // The base InternalUpdateView interpolates TransformComponent and applies it,
    // so OnUpdateView only needs additional rendering logic.
}
```

### 12-4. EntityViewComponent — Animator/VFX Modules

Attach to a child GameObject of the prefab. `EntityView.Awake` collects and binds them automatically.

```csharp
public class CharacterAnimatorViewComponent : EntityViewComponent
{
    public override void OnUpdateView()
    {
        var frame = View.Engine.PredictedFrame.Frame;
        ref readonly var body = ref frame.GetReadOnly<PhysicsBodyComponent>(View.EntityRef);
        _animator.SetFloat("Speed", (float)body.RigidBody.velocity.xz.Magnitude.ToFloat());
        _animator.SetBool ("Jump",  Mathf.Abs((float)body.RigidBody.velocity.y.ToFloat()) > 0.1f);
    }
}

public class CharacterActionVfxViewComponent : EntityViewComponent
{
    // Receive AttackActionEvent / SkillActionEvent → SetActive(true) on VFX, false after duration
}
```

### 12-5. UI (MonoBehaviours)

| File | Role |
|---|---|
| `GameMenu` | Host / Guest / Ready / Replay / Spectator buttons |
| `GameHUD` | Stocks / knockback / timer display (subscribes to `_engine.OnTickExecuted` / `OnSyncedEvent`) |
| `ResultScreen` | Receives `GameOverEvent (Synced)` → win/lose panel |
| `BrawlerViewSync` | Holds scene references (HUD, ResultScreen, etc.); links ViewUpdater to the camera |
| `BrawlerCameraController` | Cinemachine zoom / follow |

---

## 13. Phase 10 — Scenes & Prefabs

### 13-1. Folder Layout

```
Assets/Klotho/Samples/Brawler/
├── Config/
│   ├── SimulationConfig.asset           (USimulationConfig)
│   └── BrawlerEntityViewFactory.asset
├── Scenes/
│   └── BrawlerScene.unity
├── Prefabs/
│   ├── BrawlerGameController.prefab     (root controller + BrawlerViewSync)
│   ├── Characters/
│   │   ├── Warrior.prefab               (CharacterView + Animator + VFX children)
│   │   ├── Mage.prefab
│   │   ├── Rogue.prefab
│   │   └── Knight.prefab
│   ├── Objs/
│   │   ├── MovingPlatform.prefab        (PlatformView)
│   │   ├── CenterObstacle.prefab
│   │   ├── TrapZone.prefab              (visual only)
│   │   └── ItemShield/Boost/Bomb.prefab (ItemView)
│   └── UI/
│       ├── GameMenu.prefab
│       ├── GameHUD.prefab
│       └── ResultScreen.prefab
├── SFX/                                 (VFX prefabs)
└── Data/
    ├── NavMeshData.bytes                (FPNavMeshExporter output)
    └── StaticColliders.bytes            (FPStaticColliderExporterWindow output)
```

### 13-2. Scene Layout — BrawlerScene

```
BrawlerScene
├── Main Camera + Cinemachine
├── Directional Light / Global Volume
├── [Stage]
│   ├── Ground (Plane, 20×20)
│   ├── Walls (4-sided) — FPStaticCollider source
│   ├── CenterObstacle
│   ├── TrapZone × 2
│   └── ItemSpawnZone × 3
├── [MovingPlatforms] — PlatformView assigned
├── BrawlerGameController
│   ├── UKlothoBehaviour
│   ├── BrawlerGameController
│   ├── BrawlerViewSync
│   └── EntityViewUpdater
│       ├── _factory = BrawlerEntityViewFactory.asset
│       └── _pool    = DefaultEntityViewPool (optional)
└── [UI]
    ├── GameMenu (Canvas)
    ├── GameHUD  (Canvas)
    └── ResultScreen (Canvas, inactive)
```

### 13-3. NavMesh / StaticCollider Export

1. Bake the NavMesh in Unity `Window > AI > Navigation`
2. `Tools > Klotho > Export NavMesh` → `Data/NavMeshData.bytes`
3. Tag walls / structures with `FPStatic`
4. `Tools > Klotho > Export Static Colliders` → `Data/StaticColliders.bytes`
5. Load at runtime via Addressables (or `TextAsset`) and build `FPStaticBVH`

---

## 14. Phase 11 — Verification

### 14-1. SyncTest (Determinism Regression)

```csharp
var runner = new SyncTestRunner(simConfig);
runner.Run(totalTicks: 600);   // ~30 seconds
// Internally: snapshot → run → rollback → re-run → hash compare. Throws on mismatch.
```

- Covers character movement, knockback, item spawning, platforms, and bot pathing
- Wire into CI (see `Tests/DeterminismVerification/`)

### 14-2. Desync Detection

- `SimulationConfig.SyncCheckInterval = 10–30` ticks
- Subscribe `engine.OnDesyncDetected` → log / alert

### 14-3. Replay Record / Playback

```csharp
session.Engine.StartRecording();
// ... gameplay ...
session.Engine.SaveReplayToFile("match_20260424.replay");  // LZ4 compression auto-applied

// Playback
session.Engine.StartReplayFromFile("match_20260424.replay");
```

### 14-4. Spectator / Late Join

- Enter via the `SpectatorJoin` message → `engine.StartSpectator(SpectatorStartInfo)`
- Late join requires `SessionConfig.AllowLateJoin = true` and a `LateJoinAccept` from the host
- See [Docs/Specification.md](../Specification.md) §9.6 for the full flow

---

## 15. Implementation Order Summary

```
Phase 0 — Project setup (5 asmdefs, 2 config assets)
    ↓
Phase 1 — Define and register 9 DataAssets
    ↓
Phase 2 — 9 components
    ↓
Phase 3 — 4 commands
    ↓
Phase 4 — 13 events (7 Regular + 6 Synced)
    ↓
Phase 5 — 6 prototypes
    ↓
Phase 6 — 16 systems + EventSystem (BrawlerSimSetup.RegisterSystems)
    ↓
Phase 7 — Bot HFSM (Root + Decisions + Actions + Helper)
    ↓
Phase 8 — Callbacks (Simulation/View) + BrawlerGameController · Session.Create
    ↓
Phase 9 — View layer (Factory + EntityView + EntityViewComponent + UI)
    ↓
Phase 10 — Scene / prefabs / NavMesh & StaticCollider export
    ↓
Phase 11 — Verify SyncTest · Desync · Replay · Spectator / Late Join
```

---

## 16. Appendices & References

### 16-1. Production Appendices (this guide's deeper details)

| Appendix | Contents |
|---|---|
| [Brawler.A.Skills.md](Brawler.A.Skills.md) | 4 classes × 2 skills spec + `PlatformerCommandSystem` branching code + SkillConfigAsset defaults |
| [Brawler.B.Systems.md](Brawler.B.Systems.md) | Core logic of the 16 systems (TopdownMovement, Knockback, BoundaryCheck, GroundClamp, CombatHelper, etc.) |
| [Brawler.C.DataAssets.md](Brawler.C.DataAssets.md) | All fields of the 9 DataAssets + AssetId allocation rules + defaults |
| [Brawler.D.BotHFSM.md](Brawler.D.BotHFSM.md) | `BotHFSMRoot.Build()` implementation + decision criteria for the 6 Decisions |
| [Brawler.E.Bootstrap.md](Brawler.E.Bootstrap.md) | `BrawlerGameController` — full Awake → Start → HostGame / JoinGame / Replay flow |
| [Brawler.F.SceneNumbers.md](Brawler.F.SceneNumbers.md) | Spawn coordinates · platform paths · physics constants · prefab checklist · Animator parameters |
| [Brawler.G.InputCapture.md](Brawler.G.InputCapture.md) | Full `BrawlerInputCapture` code + key mapping + split-screen extension |
| [Brawler.H.DedicatedServer.md](Brawler.H.DedicatedServer.md) | `Tools/BrawlerDedicatedServer` headless dedicated server — csproj layout · single-room / multi-room modes · config files · E2E tests |

### 16-2. Framework References

- **Engine API** — [Docs/GameDevAPI.md](../GameDevAPI.md), [Docs/GameDevWorkflow.md](../GameDevWorkflow.md)
- **Engine specification** — [Docs/Specification.md](../Specification.md) (state machine · config · message protocol)
- **Navigation** — [Docs/Navigation.md](../Navigation.md)
- **Libraries** — [Docs/BaseLibraries.md](../BaseLibraries.md)

### 16-3. Third-party Assets

The Brawler sample uses the following three external assets.

| Asset | Author | License | Source |
|---|---|---|---|
| Magic Effects FREE | Hovl Studio | Unity Asset Store EULA | https://assetstore.unity.com/packages/vfx/particles/spells/magic-effects-free-247933 |
| KayKit : Prototype Bits (1.1) | Kay Lousberg (www.kaylousberg.com) | CC0 | https://kaylousberg.itch.io/ |
| KayKit : Character Animations (1.1) | Kay Lousberg (www.kaylousberg.com) | CC0 | https://kaylousberg.itch.io/ |
