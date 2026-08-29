# Game Developer Workflow

> Audience: game developers building gameplay logic on top of the xpTURN.Klotho framework.
>
> Related: [API Overview](GameDevAPI.md) (what to call) · [ECS.md](ECS.md) (the ECS in depth) · [Cookbook.md](Cookbook.md) ("I want to X" → where to look) · [DesyncDiagnostics.md](DesyncDiagnostics.md) (when the hashes disagree)

---

## 1. Game Developer Scope

In xpTURN.Klotho, the area owned by the game developer is the **gameplay-logic layer** that sits on top of the framework layer.

```
┌────────────────────────────────────────────────────────────────────┐
│                  Game-Developer Authoring Area                     │
│                                                                    │
│  1 Component definition  2 System impl       3 Callbacks (det.)    │
│  [KlothoComponent(N)]    ISystem.Update()    ISimulationCallbacks  │
│  [StructLayout] partial  ICommandSystem       · RegisterSystems    │
│  [KlothoCleanup] opt.    IInitSystem          · OnInitializeWorld  │
│  MaxCount = N   opt.     ISignalOnComponent*  · OnPollInput        │
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

> **Determinism guardrail (build-time)** — the `DeterminismAnalyzer` shipped in `KlothoGenerator.dll` flags determinism hazards while you author, before they surface as replay/rollback desync. Inside a deterministic-context type (one implementing a deterministic interface / inheriting a deterministic base, or a ref-`Frame` helper method) it warns on: `KLOTHO_DET002` float/double; `KLOTHO_DET003` non-deterministic API/type (`Mathf`, `Random`, `System.Math`, `DateTime`, float-backed `UnityEngine.Vector2/3/4`/`Quaternion`/`Matrix4x4`); `KLOTHO_DET004` `UnityEngine.Time`. Use `FP64` / `FPVector*` / `DeterministicRandom` (seeded from the `RandomSeedComponent` singleton) instead. The FP64 conversion boundary (`FromFloat` / `ToFloat` / …) is exempt, and test / tool assemblies are skipped.

### Step 1: Define Components (use IDs ≥ 100)

Use IDs of 100 or above to avoid colliding with the built-in component-ID range (1–99). The source generator emits `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` automatically. Duplicate IDs are caught at compile time.

```csharp
[KlothoComponent(100)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]   // REQUIRED — omitting it is a compile error
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
    public int ClassId;
}
```

Three things the compiler enforces: the `[StructLayout]` above (`KLOTHO_STRUCT_LAYOUT_MISSING`, error — two
runtimes must agree on the byte layout), `partial`, and `unmanaged` float-free fields (`FP64` / integer / bool /
fixed buffers; `FixedString32`/`64` for text). One thing it cannot: **never renumber a shipped id** — it is what
the state hash walks and what the layout fingerprint folds, so changing one breaks every peer at once.

Two optional attributes are worth knowing before you write your first cleanup system:

```csharp
// A one-tick marker: the engine empties the storage at the end of every tick, so there is no
// cleanup system to write — and no mutate-during-iteration trap to fall into.
[KlothoComponent(101)]
[KlothoCleanup(CleanupMode.RemoveComponent)]      // or CleanupMode.DestroyEntity for the carrier
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct HitMarkComponent : IComponent { public int Damage; }

// A slot cap: reserve 32 slots instead of one per entity. Exceeding it throws rather than growing.
[KlothoComponent(102, MaxCount = 32)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct DeadComponent : IComponent { public int DiedOnTick; }
```

Both are **determinism inputs** — they change the frame-heap layout, so every peer must agree and both are
folded into the layout fingerprint compared before the first tick (Step 4). Full rules:
[ECS.md §3](ECS.md#3-defining-a-component); sizing `MaxCount` against measurement:
[ECSMemoryOptimization.md](ECSMemoryOptimization.md).

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
- **`ISimulationCallbacks`** — common to the deterministic side (server, client, replay all behave the same). `RegisterSystems`, `OnInitializeWorld`, `OnPollInput`, `OnPlayerJoinedWorld`.
- **`IViewCallbacks`** — client view only (non-determinism allowed). `OnGameStart`, `OnTickExecuted`, `OnLateJoinActivated`.

`RegisterSystems` is called immediately after `EcsSimulation` construction and before `KlothoEngine.Initialize()`. Construct `EventSystem` without arguments; it references `frame.EventRaiser` directly each tick.

Ordering is phase first, then registration order within a phase — that is the whole model, and it is a design
decision rather than a detail (a combat system registered after movement resolves hits against the positions
movement already wrote this tick). The optional third `AddSystem` argument is a **perf-report label only**:
`sim.AddSystem(new CombatSystem(events), SystemPhase.Update, group: "combat")` groups the row in the
per-system report and affects nothing else — not execution order, not the frame, not the hash, not the layout
fingerprint. Read the report with `sim.EnableSystemPerfMonitor(warmupExecutions: 4)` and
`sim.AppendSystemPerfLog()`; it is off by default and hard-gated, so an unlabelled, unmonitored game pays
nothing.

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

    public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
    {
        // Late-join analog of OnInitializeWorld — seed this player's deterministic world state
        // (e.g. an entitlement-derived loadout). Leave empty if there is no per-join state.
    }
}

public class MyViewCallbacks : IViewCallbacks
{
    public void OnGameStart(IKlothoEngine engine)           { /* spawn commands · UI init */ }
    public void OnTickExecuted(int tick)                    { /* view update */ }
    public void OnLateJoinActivated(IKlothoEngine engine)   { /* late-join initial logic */ }
}

// Construct a KlothoSessionFlow once during startup and reuse it for every mode.
// KlothoFlowSetupBuilder is the recommended way to assemble the setup: the one dependency every
// game must supply (CallbacksFactory) is a constructor argument, so a missing factory is a compile
// error rather than a null at first join, and Build() validates feature coherence.
var setup = new KlothoFlowSetupBuilder(
        (simCfg, sessionCfg) => new SessionCallbacks(new MySimulationCallbacks(), new MyViewCallbacks()))
    .WithLogger(logger)
    .WithTransport(transport)                  // host / replay default transport
    .WithAssetRegistry(dataAssetRegistry)
    .WithLifecycleObserver(this)               // bulk-subscribed IKlothoSessionObserver
    .WithUnityDefaults()                       // AppVersion + UnityDeviceIdProvider
                                               // Godot: .WithGodotDefaults()
    .WithReconnect(credentialsStore)           // optional — needs WithUnityDefaults / WithHandshake
    .WithAutoPlayerConfig(() => new MyPlayerConfig { /* ... */ })   // optional: auto SendPlayerConfig
                                               // on guest / reconnect; run per session, so it always
                                               // observes the latest user selection
    .WithSpectator(() => new LiteNetLibTransport(logger, connectionKey: ConnectionKey))  // optional
    .WithReplaySave(replayPath, dumpJson: true)                     // optional
    .Build();                                  // Build(strict: true) promotes advisories to throws

_flow = new KlothoSessionFlow(setup);
```

`Build()` throws `FlowSetupValidationException` when `WithReconnect` is set without handshake identity —
reconnect credentials are minted by a prior normal join, which needs that identity. Building
`KlothoFlowSetup` directly with an object initializer still works and is the escape hatch for custom
validation or tests.

```csharp

// Session creation + state changes arrive through IKlothoSessionObserver
// (KlothoFlowSetup.LifecycleObserver = this) — one callback set for all modes, no per-frame polling.
// Branch on `kind`, not simCfg.Mode:
public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
{
    _sessionDriver.Attach(session);
    if (kind is SessionEntryKind.Host or SessionEntryKind.Guest) OnHostOrGuestSessionCreated(session);
    else                                                         OnReplayOrSpectatorSessionCreated(session); // Replay / Spectator
}
public void OnStateChanged(KlothoState s)      => UpdateStateUI(s);
public void OnPhaseChanged(SessionPhase p)     => UpdatePhaseUI(p);
public void OnPlayerCountChanged(int n)        => UpdatePlayerCountUI(n);
public void OnAllPlayersReadyChanged(bool r)   => UpdateReadyUI(r);

// Entry points — pick one per game mode. Branch by KlothoModeStrategy.Resolve(simCfg),
// not by inspecting simCfg.Mode directly.
_session = _flow.StartHostAndListen(uSimulationConfig, uSessionConfig, "MyRoom", "0.0.0.0", 9050); // P2P host (StartHost + HostGame + Listen, auto-teardown on failure)
_session = _flow.StartHost(uSimulationConfig, uSessionConfig);                                 // P2P host (low-level — caller drives HostGame + Listen)
_session = await _flow.JoinP2PAsync(transport, host, port, uSessionConfig, ct);                // P2P guest
_session = await _flow.JoinServerDrivenAsync(transport, host, port, roomId, uSessionConfig, ct); // SD client
_session = await _flow.ReconnectAsync(transport, creds, uSessionConfig, ct);                   // cold-start reconnect (creds: PersistedReconnectCredentials)
_session = await _flow.SpectateAsync(host, port, roomId, ct);                                  // spectator (transport via factory)
_session = _flow.StartReplayFromFile(replayPath);                                              // replay (throws ReplayLoadException)

// FaultInjection: macro-agnostic — call without #if KLOTHO_FAULT_INJECTION. Undefined builds
// return a null stub (release cost stays at zero — library-internal readers retain their macro guards).
FaultInjectionRuntime.AttachToSession(_session, transport, logger, /* roleLabel */ "host",
    reconnectFn: ct => ReconnectAsync(ct), _sessionDriver);
```

> **Escape hatch — `KlothoSession.Create(KlothoSessionSetup)`**: the direct factory is still available for games whose architecture does not fit the Flow pattern (custom retry orchestration, multi-session test harnesses, etc.). The Flow is a recommended thin wrapper, not a wall.

#### The first thing that will refuse your join: a build mismatch

Before the first tick, peers compare two **setup fingerprints** carried on the ready message. The one you will
hit while developing is the **layout** fingerprint — it folds the registered component-type set, each type's
`MaxCount`, and each type's `[KlothoCleanup]` mode (Step 1). All three are state-hash input, so peers that
disagree would diverge from tick 0; the side holding transport control refuses with
`JoinFailReason.LayoutMismatch` and a peer without it leaves via `AbortReason.LayoutMismatch`.

The classic trigger is not a code change at all: **a Unity Editor session registers components from
Editor-only test assemblies, and the player build or dedicated server does not.** The refusal message names the
two sanctioned fixes — load the same assembly set on both sides, or prune the difference via
`ISimulationConfig.SetRuntimePrunedComponentTypeIds` (an *authority-side* action, because the prune set is
host/server authoritative and propagated over the wire). When the type *counts* match, the difference is
component metadata rather than the type set, and the message says so by printing `cleanup=` next to `types=`.

To keep iterating while you sort it out, set the per-peer dev gate — and never in a shipping build:

```csharp
// KlothoSessionSetup / SpectatorSessionSetup, default false. It lives on the setup and not in
// ISimulationConfig on purpose: a guest runs the config it received over the wire, so a config-borne
// flag would read its default on exactly the peer that needs it off.
setup.AllowLayoutMismatch = true;   // downgrades the refusal to a log
```

The second fingerprint is the **environment** one (static colliders / navmesh / your own slot). It is outside
the state hash, so a difference is only a warning, and it is compared only before the match runs — runtime
navmesh rebakes move it legitimately.

#### NetworkService handle (opt-in)

If your `ISimulationCallbacks` implementation needs the `IKlothoNetworkService` handle on host/guest entry (e.g. to issue room-level network operations alongside per-tick callbacks), implement the `INetworkServiceReceiver` marker interface — the Flow auto-dispatches `SetNetworkService` just before invoking the observer's `OnSessionCreated` callback (kind-gated to Host/Guest), so the handle is ready when `OnSessionCreated` runs. Most game callbacks don't need it; omit the interface and Flow skips dispatch entirely.

```csharp
public class MySimulationCallbacks : ISimulationCallbacks, INetworkServiceReceiver
{
    private IKlothoNetworkService _net;
    public void SetNetworkService(IKlothoNetworkService svc) { _net = svc; }
    // ... rest of callbacks ...
}
```

#### Reliable-once command (spawn / room-state / one-shot)

For commands that must reach the deterministic timeline exactly once despite duplicate / past-tick rejects, use `engine.IssueOnce`. The framework `ReliableCommandTracker` owns the retry-interval cooldown, past-tick escalation, empty-move collision avoidance, and resync reset.

> Mark the command `IReliableCommand` (subtype of `ISystemCommand`; add a serialized `[KlothoOrder] int SequenceNumber { get; set; }` + `OrderKey`) to route it onto the reliable channel — in **ServerDriven** the server assigns the execution tick (confirmed-only, no retry/escalation/`WouldCollideAt`); in **P2P** (or for a plain `IssueOnce` command) it uses the legacy path below. The call site is identical for both. Reliable commands are **not predicted** — latency-insensitive actions only (spawn / purchase / surrender).

```csharp
private Func<ICommand>          _spawnBuilder;     // bound delegate, single-alloc
private IReliableCommandHandle  _spawnHandle;

public MySimulationCallbacks(/* ... */)
{
    _spawnBuilder = () => new SpawnCharacterCommand(_selectedClass);   // payload re-evaluated per retry
}

private void SendSpawn(IKlothoEngine engine)
{
    _spawnHandle = engine.IssueOnce(_spawnBuilder);   // ReliabilityPolicy.Default
}

public void OnPollInput(int playerId, int tick, ICommandSender sender)
{
    if (_spawnHandle != null && _spawnHandle.WouldCollideAt(tick)) return;   // empty-move skip
    if (HasCharacterFor(playerId)) _spawnHandle?.Confirm();                  // state-driven ack
    // ... regular per-tick input ...
}
```

`ReliabilityPolicy` exposes `RetryIntervalTicks` / `ExtraDelayStep` / `ExtraDelayMax` / `TreatDuplicateAsAck` / `TreatPastTickAsEscalation`. `ReliabilityPolicy.Default` (20 / 4 / 40 / true / true) matches the prior Brawler spawn invariant; supply a custom policy for other reliable-input scenarios.

#### System lookup from a callback boundary

`EcsSimulation.GetSystem<T>()` / `TryGetSystem<T>` / `GetSystems<T>(buffer)` expose a registered system's secondary interface (e.g. `PhysicsSystem` → `IFPPhysicsWorldProvider`) without a process-wide static slot. Stash `simulation` on `RegisterSystems(EcsSimulation simulation)` entry; resolve in the property getter.

```csharp
private EcsSimulation _simulation;

public void RegisterSystems(EcsSimulation simulation)
{
    _simulation = simulation;                                       // stash for later lookup
    // ... AddSystem(...) ...
}

public IFPPhysicsWorldProvider PhysicsProvider
    => _simulation?.GetSystem<PhysicsSystem>();                     // first-match lookup, alloc-free
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

#### Helper — `EngineEventOneShot.Subscribe` (one-shot Predicted/Confirmed/Canceled triple)

For a per-entity one-shot event (animation trigger, attack VFX) that needs the same handler on Predicted and Confirmed (with optional late-dispatch guard) plus a cancel-side cleanup, use `EngineEventOneShot.Subscribe`. The helper wraps all three channels into a single subscription with the de-dupe / late-guard wiring done for you.

```csharp
private EngineEventSubscription _attackSub;

public override void OnActivate(FrameRef frame)
{
    _attackSub = EngineEventOneShot.Subscribe<AttackActionEvent>(
        Engine,
        filter:    e => e.Attacker.Index == EntityRef.Index,
        onPlay:    _ => PlayAttackAnimation(),
        onCancel:  _ => CancelActionTrigger(),
        lateGuard: HasActiveAction);   // skip stale Predicted/Confirmed when action already ended
}

public override void OnDeactivate()
{
    _attackSub?.Dispose();             // IDisposable cleanup is required
}
```

Scope is limited to the Predicted+Confirmed+Canceled triple. Verified-time fallback events (e.g. `ActionCompletedEvent`) keep using the `OnSyncedEvent` channel — `EngineEventOneShot` doesn't replace them.

### Step 7: View Sync — EntityViewFactory / EntityViewUpdater

The View layer is three pieces on both engines: a **Factory** (decides per-entity `BindBehaviour` (Verified / NonVerified) + `ViewFlags`, and creates the view), a single-scene **Updater** that runs Reconcile on every `OnTickExecuted` to spawn/destroy automatically, and the per-entity **View** itself. The engines differ only in the host types and how the view is instantiated:

| Piece | Unity | Godot |
| ---- | ---- | ---- |
| Factory | `EntityViewFactory` (ScriptableObject; `ResolvePrefab → GameObject`) | `EntityViewFactory` (abstract class; `ResolvePrefab → PackedScene`) |
| View creation | `async UniTask<EntityView> CreateAsync` | **synchronous** `EntityViewNode Create` |
| Updater | `EntityViewUpdater` (MonoBehaviour, `Update`) | `EntityViewUpdaterNode` (`Node`, `_Process`, `ProcessPriority = 1000`) |
| View | `EntityView` (prefab MonoBehaviour) | `EntityViewNode` (`Node3D` in a `.tscn`) |

The decision API (`TryGetBindBehaviour` / `GetViewFlags`) and the view lifecycle callbacks (`OnInitialize` / `OnActivate` / `OnUpdateView` / `OnLateUpdateView` / `OnDeactivate`) are **identical** on both.

**Unity:**

```csharp
// 1. Factory subclass — author as a ScriptableObject and assign to the scene's EntityViewUpdater.
//    TWO abstract members are all a normal game implements. The framework's TryGetBindBehaviour /
//    GetViewFlags / CreateAsync / Destroy defaults already handle the rest.
[CreateAssetMenu(menuName = "MyGame/HeroViewFactory", fileName = "HeroViewFactory")]
public class HeroViewFactory : EntityViewFactory
{
    [SerializeField] private GameObject _heroPrefab;

    // Which entities get a view at all. Called per entity on every Reconcile pass — keep it cheap.
    protected override bool ShouldRender(Frame frame, EntityRef entity)
        => frame.Has<HeroComponent>(entity);

    // Which prefab. Branch on component type when one factory serves several kinds of entity;
    // return null to skip the spawn.
    protected override GameObject ResolvePrefab(Frame frame, EntityRef entity)
        => _heroPrefab;

    // Nothing else is required. The base CreateAsync instantiates ResolvePrefab's result
    // (Pool.Rent when a Pool is wired, Object.Instantiate otherwise), and the base Destroy
    // returns to the Pool or destroys.
}
```

**Override the decision methods only when the default is wrong for your game.** The base
`TryGetBindBehaviour` resolves `BindBehaviour` from a 5-input matrix — mode, `IsServer`, `IsReplayMode`,
`IsSpectatorMode`, and `OwnerId == LocalPlayerId` — and the base `GetViewFlags` turns snapshot interpolation
on for the verified path and off for the predicted one. Hand-rolling `isLocal ? NonVerified : Verified` looks
equivalent but silently drops the SD-client / spectator / replay cases the default gets right:

```csharp
// Inside the factory — only if you genuinely need different behaviour. Engine queries
// (Engine.LocalPlayerId, IsServer, …) are valid inside TryGetBindBehaviour / GetViewFlags /
// CreateAsync, and nowhere earlier — not in the constructor, not in OnEnable.
public override ViewFlags GetViewFlags(Frame frame, EntityRef entity)
    => frame.Has<GhostComponent>(entity)
        ? ViewFlags.None                            // this one entity must never interpolate
        : base.GetViewFlags(frame, entity);         // everything else keeps the default
```

```csharp
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
//    Code can supply either instead of the Inspector — DI, a runtime CreateInstance factory, a test
//    harness, or a custom IEntityViewPool. Null means "keep what is already set", not "use the field".
evu.Initialize(session.Engine, factory, pool);

// 4. On session shutdown — return active views, unsubscribe OnTickExecuted
//    (GameObjects are preserved for reuse).
evu.Cleanup();
//    Initialize re-entry reclaims for you: it begins with Cleanup(), so a second Initialize returns
//    the still-active views through the factory before attaching the new engine. Call Cleanup() at
//    session end anyway — that is where the game controls WHEN its views go away.
```

**`ViewFlags` is merged with the prefab, not assigned over it.** The two enums are handled differently on
purpose: `TryGetBindBehaviour` returns a value that covers the whole enum, so the factory's answer replaces
whatever the prefab serialized — but `ViewFlags` is a bitfield the factory only has an opinion about part of.
Composition is `(prefab & ~owned) | (factory & owned)`, where `owned` is the factory's virtual
`FactoryOwnedFlags` (default: `EnableSnapshotInterpolation`). So:

- Prefab-authored view-local behaviour (`DisableUpdate`, `DisablePositionUpdate`) survives the spawn.
- `EnableSnapshotInterpolation` is the **factory's** call — it follows from mode and ownership, and setting
  it in the Inspector has no effect. Forcing it onto a local player's entity would render that entity from
  the verified window instead of the predicted frame — several ticks in the past (under SD, the client lead
  plus `InterpolationDelayTicks`) — and cost exactly that much input response.
- If your `GetViewFlags` override decides a *further* bit, widen the mask by overriding `FactoryOwnedFlags`
  as well — otherwise the merge keeps the prefab's value for it and your decision is dropped.

**Transform pipeline (base-delegated)** — Do **not** override `ApplyTransform` / `LateUpdate` / hand-roll a lerp in subclasses. The base `EntityView` performs lerp + `ApplyTransform` + `UpdatePositionParameter` populate inside `InternalLateUpdateView` (fused with `_errorVisual.Tick`) so that tick-rate < frame-rate environments reflect every per-frame `PredictedAlpha` change without stale-lerp stutter. `UpdatePositionParameter` zeros `ErrorVisualVector` / `ErrorVisualQuaternion` when `EnableSnapshotInterpolation` is set so the verified-frame interpolation path doesn't double-correct the rollback delta — and the whole error-visual stage is skipped when `EnableErrorCorrection` is off, so a game that never turns it on pays nothing per view per frame. Game subclasses override `OnUpdateView` / `OnLateUpdateView` for game-data cache + visual-feedback toggles only. Error correction is opt-in and takes more than the config flag — the deltas `_errorVisual` smooths exist only for entities your simulation marks, see [SimulationConfigGuide §1.1](./SimulationConfigGuide.md#11-enabling-error-correction--three-touches).

**How it works**
- **Reconcile timing** — runs each tick on the `IKlothoEngine.OnTickExecuted` hook
  1. Scans `VerifiedFrame` / `PredictedFrame` and collects entities whose `TryGetBindBehaviour` matches the corresponding path
  2. New entities → asynchronous spawn via `CreateAsync` (a spawn-sequence counter + `EntityRef.Version` prevent duplicate / stale calls)
  3. Disappeared entities → `OnDeactivate`, then `Factory.Destroy` (auto-return when a Pool is present). A **snapshot-interpolated** view is held back until the render clock reaches the tick its entity left on — its render trails the Verified frame by `InterpolationDelayTicks`, so destroying it on disappearance would drop the last `delay` ticks of motion. During that grace it keeps receiving per-frame interpolation but **not** `OnUpdateView` (the entity can no longer be looked up); see [GameDevAPI §7](./GameDevAPI.md). CSP views are destroyed immediately — their lifetime tick and their render tick are the same
- **Hybrid dedup (`EntityRef.Version` + Owner)** — on every Reconcile, EVU compares the live view against the current frame on two axes:
  - `EntityRef.Version` mismatch → entity slot was reused after destroy / rollback → **Rebind** (destroy old view, spawn new) and emit `[ViewLife][Rebind]` (Debug)
  - For entities with `OwnerComponent`, EVU also calls `EntityView.OwnerMatches(currentOwnerId)`. Mismatch → stale destroy. Owner-bearing views **must** override `OwnerMatches`; the default returns `false` to fail loudly. A view whose entity has no `OwnerComponent` is never asked — the helper short-circuits — so overriding it there is dead code that later gets copied as if it were required.
- **Async safety** — if an entity disappears mid-spawn (or its slot is reused) before `CreateAsync` resolves, the result is discarded automatically via the spawn-counter + version mismatch.
- **Factory init constraint** — do not query `Engine.LocalPlayerId` / `IsServer` from the constructor or `OnEnable`. Those values are only guaranteed inside `TryGetBindBehaviour` / `GetViewFlags` / `CreateAsync`.
- **Pool** — wiring `DefaultEntityViewPool` to the `EntityViewUpdater._pool` field enables prefab reuse via `Rent` / `Return` (optional). That field is a concrete type because Unity cannot serialize an interface, so a custom `IEntityViewPool` (async load, Addressables warmup) is handed in through `Initialize` instead.

**Adopting a scene-placed (pre-placed) object as a View**

Sometimes the visual already exists in the scene — a moving platform, an elevator, a level prop the artist positioned by hand — and the simulation entity for it is spawned by game code. Do **not** drive such an object with your own `LateUpdate` + lerp: nothing outside EVU can run the base transform pipeline (`InternalLateUpdateView` is `internal`, and `EntityView` has no `LateUpdate` of its own), so hand-rolling means re-deriving interpolation, teleport handling and version-safe frame lookups yourself. Instead let the Factory *adopt* the existing instance — `CreateAsync` returns it instead of instantiating, and `Destroy` deactivates it instead of destroying:

```csharp
public class StageViewFactory : EntityViewFactory
{
    // ScriptableObjects cannot hold scene references, so a scene component hands the
    // instances over at session start (and again on every re-init — a reloaded scene
    // invalidates the old ones).
    // TryTakePlaced pops from this; Destroy pushes back into it. A plain array cannot express that,
    // so the live state is a list rebuilt from scratch on every bind.
    private readonly List<PlatformView> _placedPool = new();
    private bool _hasPlaced;        // fixed at bind time — see TryGetBindBehaviour
    public void BindPlacedViews(PlatformView[] placed)
    {
        _placedPool.Clear();
        if (placed != null)
            foreach (var v in placed) if (v != null) _placedPool.Add(v);
        _hasPlaced = _placedPool.Count > 0;
    }

    protected override bool ShouldRender(Frame frame, EntityRef e)
        => frame.Has<PlatformComponent>(e) || /* … */;

    // Adoption can FAIL — fewer placed instances than entities, or none at all. CreateAsync then falls
    // through to the base path, which asks ResolvePrefab; returning null there makes EVU discard the
    // spawn and try again on the very next tick, forever and silently. Decide which you want:
    // refuse the entity in TryGetBindBehaviour (below), or give ResolvePrefab a real fallback prefab.
    protected override GameObject ResolvePrefab(Frame frame, EntityRef e) => /* … */;

    public override UniTask<EntityView> CreateAsync(
        Frame frame, EntityRef e, BindBehaviour b, ViewFlags f)
    {
        if (frame.Has<PlatformComponent>(e) && TryTakePlaced(out var view))
        {
            view.gameObject.SetActive(true);          // ← see pitfall 4
            return UniTask.FromResult<EntityView>(view);
        }
        return base.CreateAsync(frame, e, b, f);
    }

    public override void Destroy(EntityView view)
    {
        if (view is PlatformView placed)                                         // ← pitfall 1
        {
            // Deactivating is only half of it: hand the instance back, or the next CreateAsync finds
            // nothing to adopt and the platform is gone for the rest of the session (← pitfall 5).
            // The null check is Unity's — a reloaded scene leaves destroyed objects in the array.
            if (placed != null)
            {
                placed.gameObject.SetActive(false);
                if (!_placedPool.Contains(placed)) _placedPool.Add(placed);
            }
            return;
        }
        base.Destroy(view);
    }

    // Keep the adopted entity on the same timeline as the local player (see pitfall 2).
    public override bool TryGetBindBehaviour(Frame frame, EntityRef e, out BindBehaviour b)
    {
        if (frame.Has<PlatformComponent>(e))
        {
            // Nothing placed this session ⇒ not a render candidate, which is what stops the retry loop
            // pitfall 5 describes. Gate on a value fixed at bind time, NOT on _placedPool.Count: a
            // decision that flips when the last instance is adopted makes EVU destroy the live view and
            // respawn it on alternating ticks.
            if (!_hasPlaced) { b = BindBehaviour.Verified; return false; }
            b = BindBehaviour.NonVerified; return true;
        }
        return base.TryGetBindBehaviour(frame, e, out b);
    }

    public override ViewFlags GetViewFlags(Frame frame, EntityRef e)
        => frame.Has<PlatformComponent>(e) ? ViewFlags.None : base.GetViewFlags(frame, e);
}
```

The adopted view then gets everything a spawned one gets: `EntityRef` / `Engine` injection, flag composition, `InternalActivate`, interpolation, error-visual, the despawn grace, and lifecycle callbacks (`OnActivate` / `OnUpdateView` / `OnDeactivate`) — including teardown on `EntityViewUpdater.Cleanup()`.

Five pitfalls, in the order they bite:

1. **`Destroy` must be overridden — it is not optional.** The default returns the view to the Pool when one is wired, and `DefaultEntityViewPool.Return` treats a view it never handed out as "created outside the pool" and calls `Object.Destroy` on it. A scene object adopted this way is never in the pool's registry, so with a Pool wired the *first* rebind / stale-despawn / `Cleanup` **destroys the scene object permanently**.
2. **Decide the timeline once, in both places.** `TryGetBindBehaviour` and `GetViewFlags` evaluate the predicate *independently*: overriding only the first yields a view whose lifetime follows the Predicted frame while its rendering follows the Verified window. Pick one answer and return it from both. Which answer is right is a game decision — an object the local player stands on or collides with belongs on the **predicted** timeline (`NonVerified` + `ViewFlags.None`) so it matches the locally-predicted character; a purely decorative one can stay on the default verified/snapshot path.
3. **Scene references reach the Factory at runtime, not through the Inspector.** A `ScriptableObject` cannot serialize scene objects, so a scene component must hand the list over during bootstrap (before or right after `evu.Initialize`) and re-hand it whenever the scene is reloaded — a stale array points at destroyed objects.
4. **Re-activation is yours — and so is the pose.** EVU never touches `SetActive`; activation was always the Pool's or `Instantiate`'s job, so if your `Destroy` override deactivates, your `CreateAsync` override must re-activate on the next session. The same applies to placement: every other spawn path puts the view at its entity before returning it, and an override that returns without calling base skips that. Use `TryGetSpawnPose` (`protected static`, both adapters) — `if (TryGetSpawnPose(frame, entity, out var pos, out var rot)) view.transform.SetPositionAndRotation(pos, rot);`. It returns false for an entity with no transform; leave the pose alone then. A view that runs the position line corrects itself on the first frame anyway, but one with `DisableUpdate` / `DisablePositionUpdate` stays where the scene left it forever.
5. **Adoption can fail, and the retry does not stop on its own.** With no instance left to adopt, `CreateAsync` falls through to `base.CreateAsync` → `ResolvePrefab` → `null`, and EVU discards the spawn *and re-dispatches it on the next tick*, indefinitely. EVU warns once per updater when a factory returns no view (`[ViewLife] Factory returned no view for entity=…`), so the loop is no longer silent — but the warning is a symptom report, not a fix, and it fires once while the retry continues every tick. Decide the answer explicitly: refuse the entity in `TryGetBindBehaviour` (as above) when nothing was placed, or give `ResolvePrefab` a fallback prefab. Note the gate must read a value fixed at bind time — see the comment in `TryGetBindBehaviour`.

**Godot:**

The same three pieces, with `Node`-based hosts. The factory is a plain abstract class injected with a `PackedScene`, and `Create` is **synchronous** (instancing a `PackedScene` does not need `await`):

```csharp
// 1. Factory subclass — instantiated in code with the player scene (no ScriptableObject asset).
public class HeroViewFactory : EntityViewFactory
{
    private readonly PackedScene _heroScene;
    public HeroViewFactory(PackedScene heroScene) => _heroScene = heroScene;

    protected override bool ShouldRender(Frame frame, EntityRef entity)
        => frame.Has<HeroComponent>(entity);

    protected override PackedScene ResolvePrefab(Frame frame, EntityRef entity)
        => _heroScene;

    // TryGetBindBehaviour / GetViewFlags: same signatures + semantics as Unity (override as needed).
    // Create() is provided by the base (instantiates ResolvePrefab's PackedScene, root = EntityViewNode);
    // override only for custom instancing.
}

// 2. View — subclass EntityViewNode (root of the .tscn). Same lifecycle callbacks as Unity's EntityView.
public partial class HeroView : EntityViewNode
{
    public override void OnActivate(FrameRef frame) { base.OnActivate(frame); /* cache owner, etc. */ }
    public override void OnUpdateView()            { base.OnUpdateView();     /* per-tick game data */ }
    // OnInitialize / OnLateUpdateView / OnDeactivate / OwnerMatches — same contract as Unity.
}

// 3. Scene wiring — add an EntityViewUpdaterNode to the scene, assign the factory, Initialize on bootstrap.
//    The node self-drives Reconcile via _Process (ProcessPriority = 1000, runs after the session driver).
evu.Factory = new HeroViewFactory(heroScene);
evu.Initialize(session.Engine);
```

The transform pipeline, Reconcile timing, hybrid dedup, and pooling (`DefaultGodotEntityViewPool`) behave the same as Unity — only the host types (`Node3D` / `.tscn` vs MonoBehaviour / prefab) and the synchronous `Create` differ.

---

Last updated: 2026-08-23 — `[StructLayout]` / `MaxCount` / `[KlothoCleanup]` in Step 1, `KlothoFlowSetupBuilder` + the layout-fingerprint refusal in Step 4, and the Unity view-factory example corrected to the two abstract members it actually requires
