# Game Developer API Overview

> Related: [Workflow](GameDevWorkflow.md)

---

## 1. Component Definition API

```csharp
// A component authored by the game developer
[KlothoComponent(100)]  // Unique ID — 1–99 reserved for the framework, 100+ for game developers
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
}
```

The source generator emits `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` automatically. Duplicate IDs are caught at compile time.

### Built-in Components

| Component | Fields | Purpose |
|---|---|---|
| `TransformComponent` | `Position`, `Rotation`, `Scale` | Position / rotation / scale |
| `VelocityComponent` | — | Velocity |
| `MovementComponent` | `TargetPosition`, `IsMoving` | Movement control |
| `HealthComponent` | `CurrentHealth`, `MaxHealth` | Health |
| `CombatComponent` | `AttackDamage`, `AttackRange` | Combat |
| `OwnerComponent` | `OwnerId` | Owner (player) |
| `PhysicsBodyComponent` | — | Physics body |
| `NavigationComponent` | — | Navigation agent |

---

## 2. System Implementation API

### Available Interfaces

| Interface | Invocation | Purpose |
|---|---|---|
| `ISystem` | Phase.Update / PostUpdate / LateUpdate | General per-tick logic |
| `ICommandSystem` | Phase.PreUpdate (on command receipt) | Command handling |
| `IInitSystem` | Once at simulation init | Initialization |
| `IDestroySystem` | Once at simulation shutdown | Cleanup |
| `ISyncEventSystem` | When a Verified tick is finalized | Sync-event emission |
| `IEntityCreatedSystem` | Right after entity creation | React to creation |
| `IEntityDestroyedSystem` | Right before entity destruction | React to destruction |
| `ISignalOnComponentAdded<T>` | On component add | Component reactions |
| `ISignalOnComponentRemoved<T>` | On component remove | Component reactions |
| `ISignal` (custom) | When `SystemRunner.Signal<T>()` is called | System-to-system signaling |

### Implementation Examples

```csharp
// Plain update system
public class HealthRegenSystem : ISystem
{
    public void Update(ref Frame frame)
    {
        var filter = frame.Filter<HealthComponent>();
        while (filter.Next(out var entity))
        {
            ref var health = ref frame.Get<HealthComponent>(entity);
            if (health.CurrentHealth < health.MaxHealth)
                health.CurrentHealth++;
        }
    }
}

// Command system
public class SpawnCommandSystem : ICommandSystem
{
    public void OnCommand(ref Frame frame, ICommand command)
    {
        if (command is SpawnCommand spawn)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = spawn.Position });
            frame.Add(entity, new OwnerComponent { OwnerId = spawn.PlayerId });
        }
    }
}
```

---

## 3. System Registration & Engine Integration API

Callbacks are split into the **deterministic side (`ISimulationCallbacks`)** and the **client-view side (`IViewCallbacks`)**. Place deterministic code that must run identically on every peer (server, client, replay) in `ISimulationCallbacks`; place non-deterministic client logic such as UI, animation, and spawn commands in `IViewCallbacks`.

### ISimulationCallbacks — Deterministic Common

```csharp
public class MySimulationCallbacks : ISimulationCallbacks
{
    // Register simulation systems — called immediately after EcsSimulation construction,
    // before Engine.Initialize().
    public void RegisterSystems(EcsSimulation sim)
    {
        var events = new EventSystem();
        sim.AddSystem(new CommandSystem(),     SystemPhase.PreUpdate);
        sim.AddSystem(new MovementSystem(),    SystemPhase.Update);
        sim.AddSystem(new CombatSystem(events),SystemPhase.Update);
        sim.AddSystem(new HealthRegenSystem(), SystemPhase.Update);
        sim.AddSystem(events,                  SystemPhase.LateUpdate);
    }

    // Create initial-world entities — called inside Engine.Start(), before SaveSnapshot(0).
    // Runs identically on all peers — deterministic code only.
    public void OnInitializeWorld(IKlothoEngine engine)
    {
        // Examples: fixed-terrain / item spawn, initial player-entity setup, etc.
    }

    // Per-tick input polling — send commands via sender.Send()
    // (no send → EmptyCommand auto-injected).
    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        var cmd = CommandPool.Get<MoveCommand>();
        cmd.PlayerId = playerId;
        // ... fill input ...
        sender.Send(cmd);
    }
}
```

### IViewCallbacks — Client View Only

```csharp
public class MyViewCallbacks : IViewCallbacks
{
    // Called once at game start — send spawn commands, init UI, etc.
    public void OnGameStart(IKlothoEngine engine) { }

    // Called after each tick is executed — view updates, etc.
    public void OnTickExecuted(int tick) { }

    // Called once after late-join catchup completes — initial logic such as spawn commands
    public void OnLateJoinActivated(IKlothoEngine engine) { }
}
```

### Session Creation (`KlothoSession.Create`)

`KlothoSession` is created via the static factory `Create(KlothoSessionSetup)`. Host/guest, network mode (P2P/ServerDriven), and late-join behavior are determined by `KlothoSessionSetup` fields.

```csharp
var setup = new KlothoSessionSetup
{
    Logger = logger,
    SimulationCallbacks = new MySimulationCallbacks(),
    ViewCallbacks       = new MyViewCallbacks(),
    Transport           = transport,         // host only
    Connection          = connectionResult,  // guest only (when set, host fields are ignored)
    SimulationConfig    = uSimulationConfig, // ScriptableObject or any implementation
    AssetRegistry       = dataAssetRegistry, // optional: externally built registry
    RandomSeed          = 0,                 // host-decided (0 → auto via TickCount)
    MaxPlayers          = 4,
    AllowLateJoin       = true,
    // ReconnectTimeoutMs, LateJoinDelayTicks, and other session parameters
};
var session = KlothoSession.Create(setup);
```

### Driving the Session in Unity

Bind the `IKlothoSession` to a `UKlothoBehaviour` and the session is driven automatically from MonoBehaviour Update.

```csharp
GetComponent<UKlothoBehaviour>().Bind(session);
```

`IKlothoSession` API: `Engine`, `Simulation`, `LocalPlayerId`, `State`, `Update(float dt)`, `InputCommand(ICommand)`, `Stop()`

`KlothoSession` convenience methods: `HostGame(name, maxPlayers)`, `JoinGame(name)`, `LeaveRoom()`, `SendPlayerConfig(PlayerConfigBase)`, `SetReady(bool)`

---

## 4. Entity Prototype API

Implement `IEntityPrototype` to encapsulate entity-creation logic, then register it on `frame.Prototypes`. Creation uses `frame.CreateEntity(prototypeId)` (`EntityPrototypeRegistry.Create` is internal).

```csharp
// Define a prototype — both struct and class are valid
public struct WarriorPrototype : IEntityPrototype
{
    public const int Id = 100;

    public void Apply(Frame frame, EntityRef entity)
    {
        // Look up data from DataAssetRegistry if needed
        var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100);

        frame.Add(entity, new TransformComponent());
        frame.Add(entity, new HealthComponent { CurrentHealth = 100, MaxHealth = 100 });
        frame.Add(entity, new CombatComponent { AttackDamage = 15, AttackRange = FP64.One });
    }
}

// Register inside RegisterSystems
simulation.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());

// Create from a command system — frame.CreateEntity(prototypeId) is the official API
public void OnCommand(ref Frame frame, ICommand command)
{
    if (command is SpawnCommand spawn)
    {
        var entity = frame.CreateEntity(spawn.PrototypeId);
        // additional setup ...
    }
}
```

`EntityPrototypeRegistry` API: `Register(int prototypeId, IEntityPrototype)` (creation goes through `frame.CreateEntity(prototypeId)`).

---

## 5. Command Definition API


### Built-in Commands

| Command | Purpose |
|---|---|
| `MoveCommand` | Move-target specification |
| `ActionCommand` | Generic action |
| `SkillCommand` | Skill use |
| `EmptyCommand` | No input (padding) |

### Command Definition Pattern

```csharp
[KlothoSerializable(10)]
public partial class AttackCommand : CommandBase
{
    [KlothoOrder]
    public EntityRef Target;
    [KlothoOrder]
    public int SkillId;

    // CommandType, SerializeData, DeserializeData are emitted by the source generator
}
```

Adding `[KlothoSerializable(N)]` auto-generates `CommandType`, `SerializeData`, and `DeserializeData`. Duplicate TypeIds are caught at compile time.

---

## 6. Event API

### Event Flow

```
Inside the simulation (an ECS System)
    │
    │  EventSystem.Enqueue(new DamageEvent { ... })
    │  or frame.EventRaiser.RaiseEvent(new DamageEvent { ... })
    ▼
ISimulationEventRaiser (EventCollector)
    │
    ▼
IKlothoEngine event callbacks (view layer)
    ├── OnEventPredicted(tick, event)    — fired on a Predicted tick (first firing)
    ├── OnEventConfirmed(tick, event)    — fired directly on a Verified tick without a Predicted firing
    │                                      (verified-direct, replay, new-on-rollback / content change)
    │                                      — no re-fire when a Predicted firing preceded (IMP-21)
    ├── OnEventCanceled(tick, event)     — event invalidated by rollback
    └── OnSyncedEvent(tick, event)       — fired only on Verified ticks (EventMode.Synced)
```

### Game Event Definition Pattern

```csharp
[KlothoSerializable(100)]
public partial class DamageEvent : SimulationEvent
{
    [KlothoOrder]
    public EntityRef Target;
    [KlothoOrder]
    public int Damage;

    // EventTypeId, Serialize/Deserialize/GetContentHash are emitted by the source generator
}
```

Adding `[KlothoSerializable(N)]` auto-generates `EventTypeId => TYPE_ID` and the serialization methods. Duplicate TypeIds are caught at compile time.

### EventSystem Wiring Pattern

Construct `EventSystem` without arguments and register it from the `RegisterSystems` hook. It references `frame.EventRaiser` (the `EventCollector` injected by `KlothoEngine`) directly each tick, so there are no init-order issues.

```csharp
// 1. A System that shares a reference to EventSystem
public class CombatSystem : ISystem
{
    private readonly EventSystem _eventSystem;

    public CombatSystem(EventSystem eventSystem)
    {
        _eventSystem = eventSystem;
    }

    public void Update(ref Frame frame)
    {
        // ... compute damage ...
        _eventSystem.Enqueue(new DamageEvent { Target = target, Damage = 10 });
    }
}

// 2. Register inside RegisterSystems
var events = new EventSystem();
sim.AddSystem(new CombatSystem(events), SystemPhase.Update);
sim.AddSystem(events,                   SystemPhase.LateUpdate);
```

---

## 7. Frame Access API (View Layer)

### Render-Update Pattern

```csharp
private void OnTickExecuted(int tick)
{
    var frame = _simulation.Frame;

    var filter = frame.Filter<TransformComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);
        GetView(entity).transform.position = t.Position.ToUnityVector3();
    }
}
```

### Filter Coverage

| Filter Type | Supported |
|---|---|
| `frame.Filter<T1>()` | ✅ |
| `frame.Filter<T1, T2>()` | ✅ |
| `frame.Filter<T1, T2, T3>()` | ✅ |
| `frame.Filter<T1, T2, T3, T4>()` | ✅ |
| `frame.Filter<T1, T2, T3, T4, T5>()` | ✅ |
| `frame.FilterWithout<T1, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, T3, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, T3, T4, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, T3, T4, T5, TExclude>()` | ✅ |

---

## 8. Entity Lifecycle API

```csharp
// Create an entity
var entity = frame.CreateEntity();
frame.Add(entity, new TransformComponent { ... });
frame.Add(entity, new OwnerComponent { OwnerId = playerId });

// Destroy an entity
frame.DestroyEntity(entity);  // all components removed automatically

// Validity check
bool alive = frame.Entities.IsAlive(entity);
```

### System-to-System Signal Pattern

```csharp
// Define a Signal
public interface ISignalOnDamage : ISignal
{
    void OnDamage(ref Frame frame, EntityRef target, int damage);
}

// Raise (inside CombatSystem)
_systemRunner.Signal<ISignalOnDamage>(ref frame,
    (sys, ref f) => sys.OnDamage(ref f, target, damage));

// Receive (in another System)
public class EffectSystem : ISystem, ISignalOnDamage
{
    public void OnDamage(ref Frame frame, EntityRef target, int damage) { ... }
    public void Update(ref Frame frame) { }
}
```

---

*Last updated: 2026-04-24*
