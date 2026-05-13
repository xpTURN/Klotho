# Game Developer Workflow

> Audience: game developers building gameplay logic on top of the xpTURN.Klotho framework.
>
> Related: [API Overview](GameDevAPI.md)

---

## 1. Game Developer Scope

In xpTURN.Klotho, the area owned by the game developer is the **gameplay-logic layer** that sits on top of the framework layer.

```
┌────────────────────────────────────────────────────────────────────┐
│                  Game-Developer Authoring Area                     │
│                                                                    │
│  1 Component definition  2 System impl       3 Callbacks (det.)    │
│  [KlothoComponent(N)]    ISystem.Update()    ISimulationCallbacks  │
│  partial struct MyComp   ICommandSystem       · RegisterSystems    │
│                          IInitSystem          · OnInitializeWorld  │
│                                               · OnPollInput        │
│                                                                    │
│  4 Command definition    5 Event definition  6 View callbacks      │
│  CommandBase subclass    SimulationEvent     IViewCallbacks        │
│  [KlothoSerializable]    EventMode.Regular    · OnGameStart        │
│                          EventMode.Synced     · OnTickExecuted     │
│                                               · OnLateJoinActivated│
└────────────────────────────────────────────────────────────────────┘
                                │ KlothoSession.Create(setup)
┌────────────────────────────────────────────────────────────────────┐
│                   xpTURN.Klotho Framework                          │
│   ISimulationCallbacks · IViewCallbacks · KlothoSession · Engine   │
│   EcsSimulation · Frame · SystemRunner · EntityManager             │
└────────────────────────────────────────────────────────────────────┘
```

---

## 2. Recommended Workflow

### Step 1: Define Components (use IDs ≥ 100)

Use IDs of 100 or above to avoid colliding with the built-in component-ID range (1–99). The source generator emits `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` automatically. Duplicate IDs are caught at compile time.

```csharp
[KlothoComponent(100)]
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
    public int ClassId;
}
```

### Step 2: Define Commands

Inherit from `CommandBase` and apply `[KlothoSerializable(N)]`. `CommandType`, `SerializeData`, and `DeserializeData` are emitted by the source generator.

```csharp
[KlothoSerializable(100)]
public partial class CastSkillCommand : CommandBase
{
    [KlothoOrder] public int SkillId;
    [KlothoOrder] public FPVector3 TargetPosition;
}
```

### Step 3: Implement Systems

```csharp
public class HeroSystem : ISystem, IInitSystem
{
    public void OnInit(ref Frame frame)
    {
        // Create the initial hero entity
        var hero = frame.CreateEntity();
        frame.Add(hero, new TransformComponent());
        frame.Add(hero, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
        frame.Add(hero, new HeroComponent { Level = 1, ClassId = 1 });
    }

    public void Update(ref Frame frame)
    {
        var filter = frame.Filter<HeroComponent, HealthComponent>();
        while (filter.Next(out var entity))
        {
            ref var hero = ref frame.Get<HeroComponent>(entity);
            // hero logic
        }
    }
}
```

### Step 4: Implement Callbacks & Create a Session

Callbacks are split into two interfaces.
- **`ISimulationCallbacks`** — common to the deterministic side (server, client, replay all behave the same). `RegisterSystems`, `OnInitializeWorld`, `OnPollInput`.
- **`IViewCallbacks`** — client view only (non-determinism allowed). `OnGameStart`, `OnTickExecuted`, `OnLateJoinActivated`.

`RegisterSystems` is called immediately after `EcsSimulation` construction and before `KlothoEngine.Initialize()`. Construct `EventSystem` without arguments; it references `frame.EventRaiser` directly each tick.

```csharp
public class MySimulationCallbacks : ISimulationCallbacks
{
    public void RegisterSystems(EcsSimulation sim)
    {
        var events = new EventSystem();
        sim.AddSystem(new CommandSystem(),        SystemPhase.PreUpdate);
        sim.AddSystem(new HeroSystem(),           SystemPhase.Update);
        sim.AddSystem(new CombatSystem(events),   SystemPhase.Update);
        sim.AddSystem(new MovementSystem(),       SystemPhase.Update);
        sim.AddSystem(events,                     SystemPhase.LateUpdate);
    }

    public void OnInitializeWorld(IKlothoEngine engine)
    {
        // Called before SaveSnapshot(0). Runs identically on every peer — deterministic code only.
        // Examples: fixed-terrain / item placement, initial world spawns
    }

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        // Per-tick command send (no send → EmptyCommand auto-injected)
        var cmd = CommandPool.Get<MoveCommand>();
        // ... fill input ...
        sender.Send(cmd);
    }
}

public class MyViewCallbacks : IViewCallbacks
{
    public void OnGameStart(IKlothoEngine engine)           { /* spawn commands · UI init */ }
    public void OnTickExecuted(int tick)                    { /* view update */ }
    public void OnLateJoinActivated(IKlothoEngine engine)   { /* late-join initial logic */ }
}

// Create a session and start the game — KlothoSession.Create(KlothoSessionSetup)
var session = KlothoSession.Create(new KlothoSessionSetup
{
    Logger              = logger,
    SimulationCallbacks = new MySimulationCallbacks(),
    ViewCallbacks       = new MyViewCallbacks(),
    Transport           = transport,             // host
    // Connection       = connectionResult,      // guest (when set, host fields are ignored)
    SimulationConfig    = uSimulationConfig,     // USimulationConfig (SO) or any ISimulationConfig
    AssetRegistry       = dataAssetRegistry,     // optional: externally built registry
    MaxPlayers          = 2,
    AllowLateJoin       = true,
});

session.HostGame("MyRoom", maxPlayers: 2);   // host
// or
session.JoinGame("MyRoom");                  // client
```

### Step 5: Define Events

Inherit from `SimulationEvent` and apply `[KlothoSerializable(N)]`. `EventTypeId`, `Serialize`, `Deserialize`, and `GetContentHash` are emitted by the source generator. Duplicate TypeIds are caught at compile time.

```csharp
[KlothoSerializable(100)]
public partial class CastSkillEvent : SimulationEvent
{
    [KlothoOrder]
    public EntityRef Caster;
    [KlothoOrder]
    public int SkillId;
}
```

### Step 6: Subscribe to Events (View Layer)

```csharp
var engine = session.Engine;
engine.OnEventPredicted  += (tick, evt) => HandleEventPredicted(evt);
engine.OnEventConfirmed  += (tick, evt) => HandleEventConfirmed(evt);
engine.OnEventCanceled   += (tick, evt) => HandleEventCanceled(evt);
engine.OnSyncedEvent     += (tick, evt) => HandleSyncedEvent(evt);

// OnEventPredicted : First firing of a Regular event on a Predicted tick
//                    (play VFX immediately; may be canceled).
// OnEventConfirmed : First firing of a Regular event that lands directly on Verified
//                    without a Predicted firing (verified-direct, replay,
//                    new-on-rollback / content-changed). No re-fire if Predicted preceded —
//                    write the handler the same as Predicted.
// OnEventCanceled  : Fires when a Predicted event is invalidated by rollback.
// OnSyncedEvent    : EventMode.Synced events — fired only on Verified ticks
//                    (game over, level up, round timer, etc. — confirmed-only state changes).
```

### Step 7: View Sync — EntityViewFactory / EntityViewUpdater (Unity)

The View layer combines **`EntityViewFactory` (ScriptableObject)** + **`EntityViewUpdater` (single MonoBehaviour in the scene)** + **`EntityView` (prefab MonoBehaviour)**. The Factory decides per-entity `BindBehaviour` (Verified / NonVerified) and `ViewFlags` (e.g., SnapshotInterpolation), and the Updater runs Reconcile on every `OnTickExecuted` to spawn/destroy automatically.

```csharp
// 1. Factory subclass — author as a ScriptableObject and assign to the scene's EntityViewUpdater
[CreateAssetMenu(menuName = "MyGame/HeroViewFactory", fileName = "HeroViewFactory")]
public class HeroViewFactory : EntityViewFactory
{
    [SerializeField] private GameObject _heroPrefab;

    // Decide whether this entity is rendered as a view and which BindBehaviour to bind under.
    // (false → spawn skipped). Engine queries are allowed only inside this method
    // (and GetViewFlags / CreateAsync).
    public override bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
    {
        if (!frame.Has<HeroComponent>(entity)) { behaviour = BindBehaviour.Verified; return false; }

        bool isLocal = frame.Has<OwnerComponent>(entity)
                    && frame.GetReadOnly<OwnerComponent>(entity).OwnerId == Engine.LocalPlayerId;
        // Local: predicted (NonVerified) / Remote: verified — trades responsiveness vs. accuracy
        behaviour = isLocal ? BindBehaviour.NonVerified : BindBehaviour.Verified;
        return true;
    }

    // View options such as snapshot-interpolation on/off — local is immediate, remote is interpolated
    public override ViewFlags GetViewFlags(Frame frame, EntityRef entity)
    {
        bool isLocal = frame.Has<OwnerComponent>(entity)
                    && frame.GetReadOnly<OwnerComponent>(entity).OwnerId == Engine.LocalPlayerId;
        return isLocal ? ViewFlags.None : ViewFlags.EnableSnapshotInterpolation;
    }

    // Prefab instantiation — Rent if a Pool is present, otherwise Instantiate directly
    public override async UniTask<EntityView> CreateAsync(
        Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
    {
        if (Pool != null) return await Pool.Rent(_heroPrefab);

        var go   = Object.Instantiate(_heroPrefab);
        return go.GetComponent<EntityView>();
    }

    // Override if needed (default: Object.Destroy; auto-Return when a Pool is present)
    // public override void Destroy(EntityView view) { ... }
}

// 2. Attach an EntityView subclass to the prefab — the Updater injects EntityRef/Engine and drives the lifecycle
public class HeroView : EntityView
{
    private int _ownerId;

    public override void OnInitialize()           { base.OnInitialize();       /* once on first prefab creation (skipped on pool reuse) */ }
    public override void OnActivate(FrameRef frame){
        base.OnActivate(frame);
        // Cache the owner from the entity at spawn time; consumed by OwnerMatches below.
        if (frame.Frame.Has<OwnerComponent>(EntityRef))
            _ownerId = frame.Frame.GetReadOnly<OwnerComponent>(EntityRef).OwnerId;
    }
    public override void OnDeactivate()           { base.OnDeactivate();       /* just before destroy / pool return */ }
    public override void OnUpdateView()           { base.OnUpdateView();       /* per tick — inside InternalUpdateView from EVU.OnTickExecuted */ }
    public override void OnLateUpdateView()       { base.OnLateUpdateView();   /* per frame — inside EVU.LateUpdate */ }

    // REQUIRED override for any view bound to an entity with OwnerComponent. EVU uses this on
    // Reconcile to detect entity-slot reuse with owner swap (e.g. player A's character respawn
    // landing on the same ECS entity slot previously held by player B during rollback). The
    // base implementation returns false on purpose — without override, EVU rebinds every
    // Reconcile, which surfaces as continuous churn in [ViewLife][Rebind] logs / profiler.
    // Owner-agnostic views (no OwnerComponent on the bound entity) do NOT need to override.
    public override bool OwnerMatches(int ownerId) => _ownerId == ownerId;
}

// 3. Scene wiring — bind the Factory asset and (optionally) DefaultEntityViewPool to EntityViewUpdater.
//    Call Initialize during session bootstrap.
evu.Initialize(session.Engine);

// 4. On session shutdown — return active views, unsubscribe OnTickExecuted
//    (GameObjects are preserved for reuse).
evu.Cleanup();
```

**How it works**
- **Reconcile timing** — runs each tick on the `IKlothoEngine.OnTickExecuted` hook
  1. Scans `VerifiedFrame` / `PredictedFrame` and collects entities whose `TryGetBindBehaviour` matches the corresponding path
  2. New entities → asynchronous spawn via `CreateAsync` (a spawn-sequence counter + `EntityRef.Version` prevent duplicate / stale calls)
  3. Disappeared entities → `OnDeactivate`, then `Factory.Destroy` (auto-return when a Pool is present)
- **Hybrid dedup (`EntityRef.Version` + Owner)** — on every Reconcile, EVU compares the live view against the current frame on two axes:
  - `EntityRef.Version` mismatch → entity slot was reused after destroy / rollback → **Rebind** (destroy old view, spawn new) and emit `[ViewLife][Rebind]` (Debug)
  - For entities with `OwnerComponent`, EVU also calls `EntityView.OwnerMatches(currentOwnerId)`. Mismatch → stale destroy. Owner-bearing views **must** override `OwnerMatches`; the default returns `false` to fail loudly.
- **Async safety** — if an entity disappears mid-spawn (or its slot is reused) before `CreateAsync` resolves, the result is discarded automatically via the spawn-counter + version mismatch.
- **Factory init constraint** — do not query `Engine.LocalPlayerId` / `IsServer` from the constructor or `OnEnable`. Those values are only guaranteed inside `TryGetBindBehaviour` / `GetViewFlags` / `CreateAsync`.
- **Pool** — wiring `DefaultEntityViewPool` to the `EntityViewUpdater._pool` field enables prefab reuse via `Rent` / `Return` (optional).

---

Last updated: 2026-04-24
