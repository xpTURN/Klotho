# Game Developer API Overview

> Related: [Cookbook.md](Cookbook.md) ("I want to X" → where to look) · [Workflow](GameDevWorkflow.md) (step-by-step) · [ECS.md](ECS.md) (the ECS in depth — components, filters, systems, rollback) · [ECSMemoryOptimization.md](ECSMemoryOptimization.md) (`MaxCount` and pruning) · [DesyncDiagnostics.md](DesyncDiagnostics.md) (when the hashes disagree)
>
> This page is the **API surface**: what to call, what it is called, and what the compiler enforces. For the reasoning behind the ECS design — why one heap, why filters pick the smallest storage, what rollback actually restores — read [ECS.md](ECS.md).

---

## 1. Component Definition API

```csharp
// A component authored by the game developer
[KlothoComponent(100)]                          // Unique ID — 1–99 reserved for the framework, 100+ for games
[StructLayout(LayoutKind.Sequential, Pack = 4)] // REQUIRED — omitting it is a compile error
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
}
```

The source generator emits `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` automatically. Duplicate IDs are caught at compile time.

Three things the compiler enforces, so you find out at build time rather than at the first desync:

- **`[StructLayout(LayoutKind.Sequential, Pack = 4)]` is mandatory** on every `[KlothoComponent]` struct (`KLOTHO_STRUCT_LAYOUT_MISSING`, error). The heap lays components out by their declared field order, and two runtimes must agree on it byte for byte.
- **`partial`** — required so the generator can complete the type.
- **`unmanaged`, float-free fields** — `FP64` / integer / bool / fixed buffers. No `float`, `double`, `string`, array, or reference. For text use `FixedString32` / `FixedString64`.

And one thing it cannot enforce: **never renumber a shipped component id.** The id is what the state hash walks in ascending order and what the layout fingerprint folds, so changing one is a hash-and-wire break for every peer. Append new ids; never reshuffle.

**Slot cap (`MaxCount`)** — by default a component type reserves one heap slot per entity (`maxEntities`). A type only a handful of entities ever carry does not need that, and a *large* component left at full capacity is where frame memory usually goes:

```csharp
[KlothoComponent(104, MaxCount = 96)]           // 96 slots, not maxEntities slots
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct SkillCooldownComponent : IComponent { public int RemainingTicks; }
```

The effective count is `min(MaxCount, maxEntities)`, and adding one carrier past the cap **throws** rather than growing — size it for the worst case. It is a determinism input (every peer must agree), so it belongs in source, or in `ISimulationConfig.ComponentMaxCountOverrides` when you cannot touch the source. It is ignored on a singleton (`KLSG_ECS006`, warning). Measure before capping: [ECSMemoryOptimization.md](ECSMemoryOptimization.md).

**One-tick components** — add `[KlothoCleanup(CleanupMode.RemoveComponent)]` (or `DestroyEntity`) and the engine disposes of the component (or its entity) at the end of every tick, so no cleanup system is needed:

```csharp
[KlothoComponent(101)]
[KlothoCleanup(CleanupMode.RemoveComponent)]    // gone by the next tick
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct HitMarkComponent : IComponent { public int Damage; }
```

A component added in `Update` is visible to `PostUpdate`, `LateUpdate` and any `ISignalOnComponentAdded<T>` listener in the same tick, and gone on the next one. Like `MaxCount`, the mode is folded into the layout fingerprint, so two builds that disagree about it are refused before the first tick instead of desyncing from it. See [ECS.md §3](ECS.md#3-defining-a-component) for the full rules (a cleaned-up singleton must be read with `TryGetSingleton`; `DestroyEntity` on a singleton is a compile error).

> You will also see **`[KlothoCoreComponent]`** on engine components. It marks a type as engine-essential so memory pruning can never drop it — you do not normally apply it to your own types, and it says nothing about lifetime (combining it with `[KlothoCleanup]` is a warning, `KLSG_ECS007`).

### Built-in Components

Two sets, and the difference matters: **engine components** are read or written by the engine itself, while
**gameplay components** are optional reference implementations you can ignore entirely.

**Engine components** (`Runtime/ECS/Components/`). All but `OwnerComponent` carry `[KlothoCoreComponent]`, so
memory pruning can never drop them:

| Component | Id | Fields | Purpose |
|---|---|---|---|
| `TransformComponent` | 1 | `Position`, `Rotation`, `Scale`, `PreviousPosition`, `PreviousRotation`, `PreviousInitialized`, `TeleportTick` | Position / rotation / scale + the view-interpolation prev-snapshot (see §4.1) |
| `OwnerComponent` | 2 | `OwnerId` | Owning player. The usual key for "may this command touch this entity" |
| `ErrorCorrectionTargetComponent` | 3 | *(marker)* | Marks an entity the engine may smooth toward a corrected state |
| `SessionParticipantComponent` | 4 | `PlayerId` | Engine writes one per active player at `Start()`, as an all-participants-spawned gate |
| `RandomSeedComponent` | 5 | `Seed` (ulong) | **Singleton.** Engine-injected at session start and restored via FullState on LateJoin / Reconnect / Spectator / Replay. Read `frame.GetReadOnlySingleton<RandomSeedComponent>().Seed` and combine with `DeterministicRandom.FromSeed(seed, featureKey, frame.Tick)` for rollback-stable RNG streams |
| `MatchEndStateComponent` | 26 | `Ended`, `WinnerPlayerId` (`-1` = draw) | **Singleton.** Match-end bookkeeping the engine reads for the end-of-match ladder |

**Optional gameplay components** (`Runtime/Gameplay/Components/`) — plain components with no engine privileges:

| Component | Id | Fields |
|---|---|---|
| `HealthComponent` | 21 | `MaxHealth`, `CurrentHealth` |
| `VelocityComponent` | 22 | `Velocity` (`FPVector3`) |
| `MovementComponent` | 23 | `MoveSpeed`, `TargetPosition`, `IsMoving` |
| `CombatComponent` | 24 | `AttackDamage`, `AttackRange` |
| `PhysicsBodyComponent` | 25 | `RigidBody`, `Collider`, `ColliderOffset` |
| `NavAgentComponent` | 11 | Navigation agent — tuning (`Speed`, `Radius`, `StoppingDistance`, …), live state (`Position`, `Velocity`, `DesiredVelocity`), and a fixed `Corridor[128]`. Owned by the navigation module; see [Navigation.Rebake.md](Navigation.Rebake.md) |

### Singleton Components

Mark a component type with `[KlothoSingletonComponent]` to enforce one-carrier-per-frame:

```csharp
[KlothoComponent(106)]
[KlothoSingletonComponent]                      // exactly one entity may carry this component
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct GameTimerStateComponent : IComponent
{
    public int  StartTick;
    public int  LastReportedSeconds;
    public bool GameOverFired;
}
```

- `Frame.Add<T>(entity, value)` throws if a second entity tries to carry the same singleton component.
- Read via `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>(out var entity)`.
- The source generator emits an `IsSingleton = true` flag onto `ComponentStorageRegistry.TypeIdCache<T>`; the guard is O(1) on `Frame.Add`.
- Use this for "world state" fields that are read by many systems and should never fork into multiple instances (timer state, RNG seed, score state, etc.). The engine itself uses it for `RandomSeedComponent`.

---

## 2. System Implementation API

### Available Interfaces

| Interface | Invocation | Purpose |
|---|---|---|
| `ISystem` | Any phase — PreUpdate / Update / PostUpdate / LateUpdate | General per-tick logic. Phases run in enum order; within one phase, registration order |
| `ICommandSystem` | Per received command, ahead of every `Update` | Command handling |
| `IInitSystem` | Once at simulation init | Initialization |
| `IDestroySystem` | Once at simulation shutdown | Cleanup |
| `ISyncEventSystem` | When a Verified tick is finalized | Sync-event emission |
| `IEntityCreatedSystem` | Right after entity creation | React to creation |
| `IEntityDestroyedSystem` | Right before entity destruction, **after every component has been removed** | React to destruction. `IsAlive` is still true, but `Has<T>`/`Get<T>` no longer work — use `ISignalOnComponentRemoved<T>` if you need the component values — [ECS.md](ECS.md) §6 |
| `ISignalOnComponentAdded<T>` | On `Frame.Add<T>` — the only path that adds | Component reactions. Fires **after** the insert with a `ref` into the slot (you may adjust what was just added). Fires again on every resim, and must not add/remove components — [ECS.md](ECS.md#6-writing-systems) §6 |
| `ISignalOnComponentRemoved<T>` | On `Frame.Remove<T>`, per component on `DestroyEntity`, and per carrying entity on a `[KlothoCleanup]` clear | Same rules. Fires **before** the removal and receives the value **by value** — read what you need from that value, not back out of the frame — [ECS.md](ECS.md#6-writing-systems) §6 |
| `ISignal` (custom) | When `SystemRunner.Signal<T>()` is called | System-to-system broadcast. Unrelated to the two component signals above (they deliberately do not derive from `ISignal`), and its invoker allocates per call — see §8 |

**Tick order.** Knowing this answers most "will my system see that component this tick or next" questions:

1. `ICommandSystem.OnCommand` — once per received command.
2. The built-in previous-transform pass (feeds view interpolation, §4.1).
3. `ISystem.Update` for every system, in phase then registration order.
4. The built-in `[KlothoCleanup]` passes — `RemoveComponent` storages emptied, then `DestroyEntity` carriers destroyed (§1). Skipped entirely when nothing declares the attribute.
5. `Tick++`.

The snapshot and the state hash are taken **after** all of that, so what rolls back and what peers compare is the post-cleanup state.

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

**System registration** — `AddSystem(system, phase)` runs systems in phase order, and within a phase in
registration order. The optional third argument is a **perf-report label only**:

```csharp
simulation.AddSystem(new KnockbackSystem(events), SystemPhase.Update, group: "combat");
```

It groups the system in the per-system perf report and does nothing else — no effect on execution order,
state, hashing, or the layout fingerprint, so peers that label their systems differently still play together.
It is ignored on registrations that do not implement `ISystem` (only those are measured), `"combat"` and
`"Combat"` are two different groups since nothing beyond trimming is validated — keep labels in a `const` —
and short labels read better because the longest name widens the report's whole first column.

The report itself is opt-in and hard-gated: with it off, no `Stopwatch` or GC call happens at all.

```csharp
sim.EnableSystemPerfMonitor(warmupExecutions: 4);   // exclude first-call JIT from steady-state figures
// ... run the match ...
logger.KInformation(sim.AppendSystemPerfLog());     // per-system time + per-tick allocation, plus group totals
```

Rows print as `combat/KnockbackSystem`, group totals as a trailing `= combat` row, and only additive columns
are filled in the totals (per-system peaks happened on different ticks, so neither their max nor their sum is
"what this group cost in one tick"). Two caveats the report states itself: rows that never executed are
omitted, and `ICommandSystem` work is not measured at all — so the totals are not the whole tick.

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
    // Deterministic code only. ⚠ NOT called on the ServerDriven client (see note below).
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

    // A late-joiner entered the world at its deterministic join tick — the late-join analog of
    // OnInitializeWorld. Seed that player's world state here (e.g. an entitlement-derived loadout
    // via engine.GetPlayerEntitlement). Deterministic code only; leave empty if no per-join state.
    public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
    {
    }
}
```

> **ServerDriven: `OnInitializeWorld` is skipped on the client.** A ServerDriven client boots its initial state from the server's **FullState** snapshot and does **not** call `OnInitializeWorld` (only the server / a P2P host does). The FullState snapshot carries dynamic entity state but **not static colliders**. Consequences:
>
> - **Register deterministic static geometry in `RegisterSystems`, not `OnInitializeWorld`.** `RegisterSystems` runs on *every* peer (server and client); `OnInitializeWorld` does not. If you call `PhysicsSystem.LoadStaticColliders(...)` only from `OnInitializeWorld`, the SD client's physics world has no ground/walls → dynamic bodies fall through / pass static geometry → state diverges from the server (desync). Build the static-collider BVH where all peers run it:
>   ```csharp
>   public void RegisterSystems(EcsSimulation sim)
>   {
>       var physics = new PhysicsSystem(gravity: someGravity);
>       physics.LoadStaticColliders("scene", staticColliders);   // ← here, runs on server AND SD client
>       sim.AddSystem(physics, SystemPhase.Update);
>       // ...
>   }
>   ```
> - **Don't cache `engine`/state in `OnInitializeWorld` for use by client-side callbacks** (`OnPollInput`, etc.) — that path never runs on the SD client, so the cached reference stays null and the callback silently no-ops (e.g. input never sent). Use the arguments passed to each callback instead (`OnPollInput`'s `playerId` is already the local player id).
>
> (Data-driven / runtime-mutated static colliders that can't be reproduced deterministically on every peer must instead be carried in the FullState snapshot — a framework concern beyond this guide.)

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
    SimulationConfig    = uSimulationConfig, // ScriptableObject or any ISimulationConfig
    SessionConfig       = uSessionConfig,    // ScriptableObject or any ISessionConfig (host only — guest ignored, populated by GameStartMessage / LateJoinAcceptMessage / ReconnectAcceptMessage)
    AssetRegistry       = dataAssetRegistry, // optional: externally built registry
    CredentialsStore    = credentialsStore,  // optional: warm-reconnect save/clear (guest)
    AppVersion          = Application.version,        // Godot: ProjectSettings.GetSetting("application/config/version") or a literal
    DeviceIdProvider    = new UnityDeviceIdProvider(), // Godot: new GodotDeviceIdProvider()  (OS.GetUniqueId())
    LifecycleObserver   = this,              // implements IKlothoSessionObserver (see §3.1)
    AllowLayoutMismatch = false,             // dev escape hatch — see "Build-mismatch check" below
};
var session = KlothoSession.Create(setup);
```

> **Build-mismatch check before the first tick.** Peers exchange two setup fingerprints on the ready message.
> A **layout** difference means the registered component-type set (or a `MaxCount` / `[KlothoCleanup]` mode)
> differs — that is state-hash input, so those peers would diverge from tick 0: the side with transport control
> (dedicated server, P2P host) refuses the peer with `JoinFailReason.LayoutMismatch`, and a peer without it
> leaves via `AbortReason.LayoutMismatch`. An **environment** difference (static colliders, navmesh, your own
> fingerprint slot) is outside the state hash, so it is only a warning, and only compared before the match runs.
>
> The error message names the two sanctioned fixes: load the same assembly set on both sides — the usual cause
> is a Unity Editor session registering Editor-only test assembly components against a player or dedicated-server
> build — or prune the difference via `ISimulationConfig.SetRuntimePrunedComponentTypeIds`, which is an
> **authority-side** action because the prune set is host/server authoritative.
> `KlothoSessionSetup.AllowLayoutMismatch = true` (also on `SpectatorSessionSetup`) downgrades the refusal to a
> log for development. It lives on the setup rather than in `ISimulationConfig` on purpose: a guest runs the
> config it received over the wire, so a config-borne flag would read its default on exactly the peer that needs
> it off. Set it per peer, and never in a shipping build.

`SessionConfig` carries the 16 host-decided session fields (`RandomSeed`, `MaxPlayers` / `MinPlayers` / `MaxSpectators`, late-join/reconnect policy & tuning, chain-stall watchdog, countdown, match-end grace). Author it once as a `USessionConfig` ScriptableObject and reuse across scenes — `KlothoSession.Create()` copies the values into an internal `SessionConfig` (so editor assets are never mutated, and `RandomSeed = 0` is auto-replaced by `Environment.TickCount` on host). Passing `null` falls back to the runtime default `new SessionConfig()` — convenient for tests and replay paths.

### 3.1 IKlothoSessionObserver — bulk-subscribed lifecycle

Implement `IKlothoSessionObserver` and pass the instance through `KlothoSessionSetup.LifecycleObserver` to bulk-subscribe all session-level lifecycle callbacks at `KlothoSession.Create`. The framework unsubscribes them at `Stop()` and finally calls `OnSessionStopped()` so the game can finish its own teardown. This replaces the per-event `+=` wiring that was previously spread across `StartHost / JoinGame / Reconnect / StopGame` sites.

The observer is the **single recommended surface** for session observation. Besides the NetworkService/Engine callbacks it also delivers: session **state transitions** (`OnStateChanged` / `OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged`), session **creation with role** (`OnSessionCreated(session, SessionEntryKind kind)`), a **pre-stop** hook (`OnSessionStopping()`, fired inside `Stop()` before `Engine.Stop` while the engine is still alive — for view/EVU cleanup), and an **idle-disconnect** hook (`OnIdleDisconnected(DisconnectReason reason)`, raised by the driver when the bound transport drops while no session is attached — for returning to the initial menu; see *Driving the Session*). (The observer is the only session-observation surface — the legacy `KlothoSession` instance state events and per-role `KlothoSessionFlow.On*SessionCreated` events have been removed.)

```csharp
public class MyGameController : MonoBehaviour, IKlothoSessionObserver
{
    // NetworkService callbacks
    public void OnPlayerDisconnected(IPlayerInfo player) { /* host: gray out portrait */ }
    public void OnPlayerReconnected(IPlayerInfo player)  { /* host: clear gray */ }
    public void OnReconnecting() { /* guest UI */ }
    public void OnReconnectFailed(ReconnectRejectReason reason)
    {
        var name = reason.ToName();
        if (reason.RequiresUserChoice())
            ShowAlreadyConnectedDialog();
        else
            FallbackToInitial();
    }
    public void OnReconnected() { /* guest UI */ }

    // Engine callbacks
    public void OnCatchupComplete()              { /* late-join active */ }
    public void OnResyncCompleted(int tick)      { /* state replaced by verified */ }
    public void OnGameStart()                    { /* match running */ }
    public void OnMatchAborted(AbortReason r)    { /* chain stall, divergence, … */ }
    public void OnMatchEnded(int tick, IMatchEndEvent endEvt) { /* normal end */ }
    public void OnMatchReset(ResetReason r)      { /* corrective reset, match continues */ }

    // Session state callbacks (transitions only — no per-frame polling)
    public void OnStateChanged(KlothoState s)        { /* menu.State = s */ }
    public void OnPhaseChanged(SessionPhase p)       { /* menu.Phase = p */ }
    public void OnPlayerCountChanged(int n)          { /* menu.Players = n */ }
    public void OnAllPlayersReadyChanged(bool ready) { /* menu.IsAllReady = ready */ }

    // Session lifecycle
    public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
    {
        // attach driver, init view; branch on kind (Host/Guest/Replay/Spectator)
    }
    public void OnSessionStopping()              { /* engine-alive cleanup: view teardown (before Engine.Stop) */ }
    public void OnSessionStopped()               { /* null-out session, return to initial UI — transport is driver-owned, not disconnected here */ }
    public void OnIdleDisconnected(DisconnectReason reason) { /* transport dropped with no session attached — return to initial UI */ }
}
```

Default no-op implementations are provided on the interface, so the game only overrides the callbacks it needs. `OnReconnectFailed(ReconnectRejectReason reason)` mirrors `KlothoNetworkService.OnReconnectFailed` (see Specification §9.5) — symbolic names via `reason.ToName()`; `reason.RequiresUserChoice()` returns true for `AlreadyConnected`. `ReconnectRejectReason` / `JoinFailReason` are `enum : byte`, so a `switch` over them is exhaustive and IntelliSense lists the cases.

Cold-start reconnect (via `KlothoConnectionAsync.ReconnectAsync` / `KlothoConnection.Reconnect`) surfaces server reject through `ReconnectFailedException` — catch it and branch on `e.Reason` (`ReconnectRejectReason`, same values as `OnReconnectFailed`).

Normal join (`JoinP2PAsync` / `JoinServerDrivenAsync`) surfaces failure through `JoinFailedException` — catch it and branch on `e.Reason` (`JoinFailReason`). Transport/handshake reasons: `TransportStartFailed` / `TimedOut` / `ConnectionLost` / `Rejected` / `HostClosed` / `Unknown`. Server application-level rejections: `RoomFull` / `RoomNotFound` / `RoomClosing` / `LateJoinDisabled` / `ServerFull`. Cancellation surfaces as `OperationCanceledException`, not `JoinFailedException`. Notes: `ConnectionLost` (transport `NetworkFailure`) is the dominant "can't reach host" case; `Rejected` is transport-level only (wrong connection key / protocol mismatch), distinct from the server's app-level `RoomFull` / `RoomClosing` / etc.

### Driving the Session

Drive the session through a **session driver** — an engine adapter node that owns the `Update`/`Stop` loop and exposes `PreSessionUpdate` / `PostSessionUpdate` hooks (session teardown is observed through `IKlothoSessionObserver`, not a driver hook). The driver also owns the main transport: it pumps it while no session is attached (idle) and routes idle disconnects to `IKlothoSessionObserver.OnIdleDisconnected` — bind it once via `BindTransport`, before any session is created.

- **Unity** — `KlothoSessionDriver` (MonoBehaviour, drives via `Update`). Attach as a `[SerializeField]` on the game controller prefab and wire hooks in `Awake`.
- **Godot** — `GodotSessionDriver` (`Node`, drives via `_Process`). Add it to the scene tree and wire hooks in `_Ready`. Same `BindTransport` / `Attach` / `DetachAndStop` API and idle-pump / `OnIdleDisconnected` semantics.

The Unity example below shows the pattern; the Godot equivalent swaps the host type and `Awake`→`_Ready` (see [GodotP2pSample.md](Samples/GodotP2pSample.md)).

```csharp
[SerializeField] private KlothoSessionDriver _sessionDriver;

void Awake()
{
    _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
}

void Start()
{
    // ... create _transport and _flow ...
    // Hand the main transport to the driver: it pumps it while idle and raises OnIdleDisconnected
    // on an idle drop. Bind before the first session so the driver subscribes ahead of NetworkService.
    _sessionDriver.BindTransport(_transport, this, _flow);
}

void OnPreSessionUpdate(KlothoSession s, float dt)
{
    // Capture input / compute aim before Session.Update runs.
    _input.CaptureInput();
}

// Engine-alive cleanup (view / EVU teardown before Engine.Stop) goes in the observer's
// OnSessionStopping() — see §3.1 — not on the driver's Stopping event.

// Attach after the session is created (see §X Flow).
_sessionDriver.Attach(_session);
```

The driver guarantees: dt is computed from `DateTimeOffset.UtcNow` (time-scale invariant) and the bound transport is pumped only while `Session == null`. **Transport ownership**: once bound, the driver owns the main transport's session-less lifetime — it pumps it while idle, routes an idle drop to `OnIdleDisconnected(reason)` and a pre-`Playing` session-present drop to a deferred session stop, and disconnects the socket only at process-exit (`OnDestroy`). The socket is retained across `Stop()` for reuse, so the game neither pumps the transport nor subscribes to `transport.OnDisconnected`. Hook exception policy: steady-state hooks (`PreSessionUpdate` / `PostSessionUpdate`) propagate naked. The driver also raises a `Stopping(KlothoSession)` event, but it is a **framework-internal diagnostic signal** (its sole consumer is `FaultInjectionRuntime` under `KLOTHO_FAULT_INJECTION`) — **not a game hook**. It fires once per stop, wrapped in `try { Stopping?.Invoke(s); } finally { /* session detached */ }` so a throwing subscriber can never strand the stopped session; on a game-triggered stop (`DetachAndStop`) it fires before `session.Stop()`, on a framework-internal stop (auto-shutdown / spectator-drop) it fires when the driver self-detaches just after `Engine.Stop`. Game code observes the session lifecycle through the observer (`OnSessionStopping()` / `OnSessionStopped()`), never by subscribing to `Stopping`.

Session teardown is idempotent at the framework level (`KlothoSession.Stop` `_stopped` guard, `KlothoSessionDriver.DetachAndStop` `_stopping` guard), so game code needs no re-entry guard of its own. A session-stop converges on a single `OnSessionStopped` callback whether the game triggered it (`DetachAndStop`) or the framework did (auto-shutdown grace / spectator-drop) — the driver self-detaches when it observes the session stopped. Put terminal teardown in `OnSessionStopped`; do not re-drive the driver from it.

**Reconnect-credentials & replay policy on teardown**: `KlothoSessionDriver.DetachAndStop(bool keepReconnectCredentials = false, bool saveReplay = true)` and `KlothoSession.Stop(bool keepReconnectCredentials = false, bool saveReplay = true)` accept optional flags. `keepReconnectCredentials` is forwarded to `IKlothoNetworkService.LeaveRoom` — default `false` discards persisted cold-start credentials (user-intent leave / match-end shutdown / failed bootstrap); pass `true` from process-exit entry points so persisted credentials survive into the next launch (`KlothoSessionDriver.OnDestroy` does this internally; mirror it in game `OnApplicationQuit` / `OnDestroy`). `saveReplay` (default `true`) lets `Stop()` write the replay configured via `KlothoFlowSetupBuilder.WithReplaySave(path, dumpJson)` — KlothoSessionFlow stamps the path onto host / guest sessions, then `Stop()` saves after `Engine.Stop` (skipped in replay-playback mode); pass `false` from process-exit teardown to suppress it. For a dynamic per-match path the post-create escape hatch `KlothoSession.ConfigureReplaySave(path, dumpJson)` overrides the builder default (last-write-wins). Explicit cancel / reject paths clear credentials directly via `IReconnectCredentialsStore.Clear()` — do not rely on the teardown flag for those.

`IKlothoSession` API: `Engine` (returns `IKlothoEngine`), `Simulation`, `LocalPlayerId`, `State`, `PlayerCount`, `Phase`, `AllPlayersReady`, `Update(float dt)`, `InputCommand(ICommand)`, `Stop(bool keepReconnectCredentials = false, bool saveReplay = true)`, `IsStopped`, convenience methods `HostGame(name, maxPlayers)` / `JoinGame(name)` / `LeaveRoom()` / `SendPlayerConfig(PlayerConfigBase)` / `SetReady(bool)`. The four read properties `State` / `PlayerCount` / `Phase` / `AllPlayersReady` are mode-agnostic facade reads — the framework supplies null-safe defaults across the create/teardown window (`Phase → SessionPhase.None`, `AllPlayersReady → false`, `PlayerCount` through the `NetworkService → SpectatorService → 0` fallback chain), so the game never reaches into `NetworkService` for state. Call them from a one-shot poll if you missed the initial event.

State observation is exclusively through `IKlothoSessionObserver` (§3.1: `OnStateChanged` / `OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged`) — backed by `IKlothoNetworkService.OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` and `KlothoEngine.OnStateChanged`, with network-service + spectator-service `OnPlayerCountChanged` forwarded so one observer works across host / guest / spectator. These four callbacks line up 1:1 with the four `IKlothoSession` read properties above — subscribe for transitions, read the property for the current value.

Logger channel: prefer `engine.Logger` / `frame.Logger` for runtime logging. `KlothoLogger.CreateDefault` (Unity) / `GodotKlothoLogger.CreateDefault` (Godot) are escape hatches — use them only when a separate category or rolling-file destination is needed.

### 3.2 KlothoSessionFlow — mode-dispatched entry points

For most games the preferred construction path is `KlothoSessionFlow` (it wraps `KlothoSession.Create` and bundles common defaults). `KlothoFlowSetup` carries the long-lived dependencies; the entry methods take only the per-call parameters:

**Assembling the setup.** The recommended way to build `KlothoFlowSetup` is `KlothoFlowSetupBuilder` — it makes `CallbacksFactory` a constructor argument (compile-time required), groups the optional dependencies into cohesive feature methods, and validates feature coherence at `Build()`:

```csharp
var setup = new KlothoFlowSetupBuilder(callbacksFactory)   // required dependency = ctor arg
    .WithLogger(logger)
    .WithTransport(transport)            // host / replay default transport
    .WithAssetRegistry(assetRegistry)
    .WithLifecycleObserver(this)         // IKlothoSessionObserver
    .WithUnityDefaults()                 // AppVersion + UnityDeviceIdProvider (Runtime.Unity layer). Godot: .WithGodotDefaults() (Godot~/Adapters layer)
    .WithReconnect(credentialsStore)     // optional — requires WithHandshake / WithUnityDefaults
    .WithAutoPlayerConfig(() => new MyPlayerConfig { /* ... */ })  // optional
    .WithSpectator(() => new LiteNetLibTransport(/* ... */))       // optional — no-transport SpectateAsync
    .WithReplaySave(replayPath, dumpJson: true)                   // optional — framework saves on Stop (host/guest)
    .Build();                            // Build(strict: true) promotes advisory warnings to throws
var flow = new KlothoSessionFlow(setup);
```

`Build()` throws `FlowSetupValidationException` when `WithReconnect` is set without handshake identity (`WithHandshake` / `WithUnityDefaults`) — reconnect credentials are minted by a prior normal join, which needs that identity. Constructing `KlothoFlowSetup` directly via object initializer remains supported as a low-level escape hatch (custom validation bypass / tests).

> **Godot**: use `.WithGodotDefaults()` (`Godot~/Adapters/GodotFlowSetupBuilderExtensions`) — it reads AppVersion from `ProjectSettings` and injects `GodotDeviceIdProvider` via the core `.WithHandshake(appVersion, deviceIdProvider)` in one call, mirroring `.WithUnityDefaults()`. Both satisfy the `WithReconnect` handshake-identity requirement. `.WithHandshake` lives in `Runtime/Core` and is engine-agnostic if you prefer to call it directly.

| Mode | Entry | Notes |
|---|---|---|
| P2P host | `flow.StartHostAndListen(simCfg, sessionCfg, roomName, address, port)` | synchronous — folds StartHost + HostGame + Transport.Listen with auto-teardown on failure. Returns the running session, or `null` on listen-bind failure (session already torn down); rethrows on other failures after teardown |
| P2P host (low-level) | `flow.StartHost(simCfg, sessionCfg)` | synchronous — escape hatch for custom ordering / multi-transport / tests. Caller drives `HostGame` + `Transport.Listen` and rollback manually |
| Guest (any mode) | `flow.JoinAsync(strategy, transport, host, port, roomId, sessionCfg, ct, connectTimeoutMs?)` | unified entry — the `strategy` (from `KlothoModeStrategy.Resolve(simCfg)`) supplies the pre-join handshake and roomId normalization, so multi-mode games join without branching on the mode. P2P ignores `roomId`. Recommended for games that support more than one mode |
| P2P guest (convenience) | `flow.JoinP2PAsync(transport, host, port, sessionCfg, ct, connectTimeoutMs?)` | fixed-mode overload of `JoinAsync` — no `roomId`. guest receives `sessionCfg` from `GameStartMessage` (the passed value is a seed). `connectTimeoutMs` optional (default 15s, positive only); failure throws `JoinFailedException` (branch on `e.Reason`) |
| ServerDriven client (convenience) | `flow.JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct, connectTimeoutMs?)` | fixed-mode overload of `JoinAsync` — extra `roomId` parameter (P2P does not use it) |
| Reconnect | `flow.ReconnectAsync(transport, creds, sessionConfigSeed, ct)` | `creds` is `PersistedReconnectCredentials` — carries `RoomId`, host address, magic. Mode is recovered from the credentials |
| Spectator | `flow.SpectateAsync(host, port, roomId, ct)` | no-transport overload — library calls `KlothoFlowSetup.SpectatorTransportFactory` |
| Replay | `flow.StartReplayFromFile(path)` | throws `xpTURN.Klotho.Replay.ReplayLoadException` on load failure |

Branch by mode using `KlothoModeStrategy.Resolve(simCfg)` rather than reading `simCfg.Mode` directly. For the effective local role, call `strategy.ResolveRole(isHostPreference)` once and branch on the returned `KlothoRole` (`P2PHost` / `P2PGuest` / `SdClient`) — it folds the mode-vs-host-preference combination into a single value (ServerDriven ignores the preference), with `role.IsLocalHost()` / `role.IsReconnectEligible()` helpers.

For session-created handling, implement the single role-bearing observer callback `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` (§3.1) and branch on `kind` — one method covers all modes (Host / Guest / Replay / Spectator). (The former per-mode `KlothoSessionFlow.On*SessionCreated` events have been removed.)

`KlothoFlowSetup` also carries two optional factories that absorb common boilerplate (set them via the builder's `WithAutoPlayerConfig` / `WithSpectator`):

- `InitialPlayerConfigFactory : Func<PlayerConfigBase>` (`WithAutoPlayerConfig`) — invoked automatically on guest / reconnect paths after the session is created; the framework calls `session.SendPlayerConfig(factory())`. Spectator / replay paths skip the call. The factory is invoked per-session so it always observes the latest user selection.
- `SpectatorTransportFactory : Func<INetworkTransport>` (`WithSpectator`) — invoked from `SpectateAsync(host, port, roomId, ct)` so the library owns the transport instance. The escape-hatch overload `SpectateAsync(transport, host, port, roomId, ct)` is retained for custom transports.

### 3.3 INetworkServiceReceiver — opt-in NetworkService handle

`ISimulationCallbacks` implementations that need the `IKlothoNetworkService` handle on host/guest entry declare it via the `INetworkServiceReceiver` marker. `KlothoSessionFlow` dispatches `SetNetworkService` automatically just before invoking the observer's `OnSessionCreated` callback — gated to Host/Guest kinds, non-null callbacks, and the `is INetworkServiceReceiver recv` pattern — so a receiver already holds the handle when `OnSessionCreated` runs. Implementations that don't need the handle simply omit the interface and avoid empty-body methods.

```csharp
public class MySimulationCallbacks : ISimulationCallbacks, INetworkServiceReceiver
{
    private IKlothoNetworkService _net;
    public void SetNetworkService(IKlothoNetworkService svc) { _net = svc; }
    // ... regular ISimulationCallbacks members ...
}
```

Spectator / replay kinds skip the dispatch at the Flow boundary, so games no longer need `if (!isSpectator && !isReplay)` guards around the call.

### 3.4 IKlothoEngine.IssueOnce — reliable-once command transactions

For commands that must reach the deterministic timeline exactly once despite Duplicate / PastTick rejects, use `engine.IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null)`. The framework `ReliableCommandTracker` owns retry-interval cooldown, past-tick escalation (`ExtraDelayStep` bump, capped at `ExtraDelayMax`), empty-move collision avoidance, and `OnResyncCompleted` reset.

> **`IReliableCommand` opt-in (ServerDriven authoritative placement).** Mark the command type `IReliableCommand` (a subtype of `ISystemCommand`; add an `[KlothoOrder] int SequenceNumber { get; set; }` serialized member and an `OrderKey`) to route it onto a **dedicated reliable channel**: in **ServerDriven** the client submits it tick-less and the **server assigns the execution tick** (`ReliableCommandSubmit` wire message), so it is confirmed-only — no client-side retry/escalation/`WouldCollideAt`/PastTick-reject loop. A plain `IssueOnce` command (not `IReliableCommand`), and **all** reliable commands in **P2P**, take the legacy path described above (retry/escalation/`WouldCollideAt`). The `IssueOnce` call site and `IReliableCommandHandle` surface are identical for both — the routing is internal and mode-dependent. `SpawnCharacterCommand` in the Brawler sample is an `IReliableCommand`.

```csharp
private Func<ICommand>          _spawnBuilder;
private IReliableCommandHandle  _spawnHandle;

// In ctor — bind once (single-alloc, payload re-evaluated per retry)
_spawnBuilder = () => new SpawnCharacterCommand(_selectedClass);

// Issue
_spawnHandle = engine.IssueOnce(_spawnBuilder);   // ReliabilityPolicy.Default

// OnPollInput integration — handle-aware empty-move skip + state-driven ack
if (_spawnHandle != null && _spawnHandle.WouldCollideAt(tick)) return;
if (HasCharacterFor(playerId)) _spawnHandle?.Confirm();
```

`IReliableCommandHandle` surface: `WouldCollideAt(tick)` (caller-side empty-move skip), `Confirm()` (state-driven ack — caller decides), `Cancel()` (caller-side abort), `OutstandingTargetTick`, `OnRejected` / `OnResolved` events. `ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches the prior Brawler spawn invariant. Construct a custom policy for other reliable-input scenarios (e.g. `TreatDuplicateAsAck=false` when the same logical command can legitimately fire multiple times).

### 3.5 EcsSimulation.GetSystem<T> — registered-system lookup

`EcsSimulation.GetSystem<T>()` / `TryGetSystem<T>(out T)` / `GetSystems<T>(List<T> buffer)` return the first registered system instance matching `T` (`T : class`). Lets a callback boundary expose a registered system's secondary interface without a process-wide static slot. Stash the `EcsSimulation` reference on `RegisterSystems` entry and resolve in the property getter:

```csharp
public class MyCallbacks : ISimulationCallbacks
{
    private EcsSimulation _simulation;

    public void RegisterSystems(EcsSimulation simulation)
    {
        _simulation = simulation;                                  // stash for lookup
        // ... AddSystem(...) ...
    }

    // IFPPhysicsProviderSource consumer (e.g. FPPhysicsWorldVisualizer)
    public IFPPhysicsWorldProvider PhysicsProvider
        => _simulation?.GetSystem<PhysicsSystem>();                // first-match, alloc-free
}
```

`GetSystem<T>()` traversal order matches `AddSystem` registration order. For multi-instance lookups, `GetSystems<T>(buffer)` appends every match into a caller-owned `List<T>` (alloc-free for the lookup itself; the buffer manages its own capacity).

---

### 3.6 Per-match config — `StageId` / `MatchConfigData`

A match's **stage** (which map/level to build) and an opaque **per-match payload** (game mode, rules, difficulty) ride on the `SimulationConfig` and reach every peer through the same channel that already carries the seed and session settings. The authority (dedicated server, lobby, or P2P host) sets them; joiners read them.

**Reading them in the game.** The callbacks factory (`KlothoFlowSetupBuilder(...)` / `CallbacksFactory`) runs after the resolved config has arrived and receives the `ISimulationConfig`, so the game selects its stage assets and decodes its match knobs there:

```csharp
new KlothoFlowSetupBuilder((simCfg, sessionCfg) =>
{
    int stage = simCfg.StageId;                       // 0 = default single stage
    var knobs = MyMatchConfig.Decode(simCfg.MatchConfigData); // byte[] → game struct (empty → defaults)
    var (colliders, navMesh) = MyStages.Resolve(stage);
    return new SessionCallbacks(new MySimulationCallbacks(colliders, navMesh, knobs), viewCallbacks);
})
```

- `ISimulationConfig.StageId` (scalar) selects content; `ISimulationConfig.MatchConfigData` (`byte[]`) is opaque — the game owns its codec (e.g. a `[KlothoSerializableStruct]` payload). Both default to "unset" (`StageId` 0, empty payload), so a single-stage game reads nothing new.
- The value is authoritative and propagated, so an SD guest / P2P guest gets exactly what the server / host chose — no client-side lookup or divergence.

**Setting them (authority).** On a **dedicated server**, supply an `IMatchConfigSource` so each room resolves its own config at creation:

```csharp
new RoomManagerConfigBuilder((matchCtx, roomLogger) =>            // match-aware callbacks ctor
        new MyServerCallbacks(roomLogger, MyStages.Resolve(matchCtx.StageId), matchCtx.MatchConfigData))
    .WithMatchConfigSource(new StaticMatchConfigSource().Add(roomId: 0, stageId: 1).Add(1, 2))
    // ... other Build steps
```

`CreateRoomAt` stamps the resolved `StageId`/`MatchConfigData` onto the room's `SimulationConfig` (so the game factory never has to remember to), and a source that **declines** a room refuses creation (client turned away with room-not-found). A **P2P host** sets `SimulationConfig.StageId` / `MatchConfigData` on the config it hosts with. See [LobbyIntegrationGuide §4-E](./LobbyIntegrationGuide.md#4-e-per-room-match-config--reservation-sd-multi-room) for the lobby-driven path.

---

### 3.7 Match result — `IMatchResultProvider`

The mirror of §3.6: where the per-match *config* flows in, the match's **outcome payload** (winner, per-player stats, acquisitions) flows out. The game authors it at match end; the server authority reads it. The framework carries it as an opaque `byte[]` and never parses it — the game owns the schema on both ends.

**Exposing it (game).** Implement `IMatchResultProvider` alongside `IMatchEndEvent` on the match-end event, and assemble the blob from **verified** simulation state when the event fires:

```csharp
[KlothoSerializable(104)]
public partial class GameOverEvent : SimulationEvent, IMatchEndEvent, IMatchResultProvider
{
    public override EventMode Mode => EventMode.Synced;
    [KlothoOrder] public int WinnerPlayerId;
    [KlothoOrder] public FixedString32 Reason;

    // Server-local result blob — deliberately NOT [KlothoOrder]: it is never sent and never part
    // of the deterministic content hash (a byte[] hashes by reference → would fake a divergence).
    public byte[] MatchResultData;

    int IMatchEndEvent.WinnerPlayerId => WinnerPlayerId;
    FixedString32 IMatchEndEvent.Reason => Reason;
    byte[] IMatchResultProvider.MatchResultData => MatchResultData;
}

// At fire time — ALWAYS assign (events are pooled and the generated Reset clears only
// [KlothoOrder] fields, so a skipped assignment would surface a stale blob):
evt.MatchResultData = AssembleResult(ref frame);   // pure read-out of verified ECS state
```

**Reading it (server authority).** In `OnMatchEnded`, cast the event. A `null` blob — or an event that does not implement the interface — means "no result"; the path is opt-in:

```csharp
engine.OnMatchEnded += (tick, endEvt) =>
{
    byte[] blob = (endEvt as IMatchResultProvider)?.MatchResultData;
    if (blob != null) { /* decode with the game codec → persist / report */ }
};
```

- Define the blob's schema with the same serialization tools as any other payload (e.g. a `[KlothoSerializable]` message over `[KlothoSerializableStruct]` entries, as the Brawler sample's `BrawlerMatchResult` does) — producer and consumer share one generated codec, but the framework boundary stays `byte[]`.
- Keep identity **out** of the blob — key entries by `PlayerId`. On a lobby-wired dedicated server the reference reporter ships the blob to the lobby together with a verified identity roster, and the backend joins the two by `PlayerId` — see [LobbyIntegrationGuide §4-F](./LobbyIntegrationGuide.md#4-f-match-result-reporting-sd-multi-room).

---

## 4. Entity Prototype API

Implement `IEntityPrototype` to encapsulate entity-creation logic. Two creation paths:

1. **`frame.CreateEntity(int prototypeId)`** — registered prototype lookup. Use when the prototype carries no per-spawn data.
2. **`frame.CreateEntity<TPrototype>(in TPrototype prototype)`** — typed overload. Use when the prototype needs per-spawn data (spawn position, faction, etc.). No registry registration required.

```csharp
// Define a prototype — struct is preferred (no boxing under the typed overload).
public struct WarriorPrototype : IEntityPrototype
{
    public const int Id = 100;

    // Per-spawn data (used by the typed-overload path)
    public FPVector3 SpawnPosition;
    public FP64 SpawnRotation;

    public void Apply(Frame frame, EntityRef entity)
    {
        var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100);

        frame.Add(entity, new TransformComponent
        {
            Position = SpawnPosition,
            Rotation = SpawnRotation,
        });
        frame.Add(entity, new HealthComponent { CurrentHealth = 100, MaxHealth = 100 });
        frame.Add(entity, new CombatComponent { AttackDamage = 15, AttackRange = FP64.One });
    }
}

// Registered path — register once during RegisterSystems, then create by id
simulation.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());
var e1 = frame.CreateEntity(WarriorPrototype.Id);   // SpawnPosition = default (origin)

// Typed-overload path — carry spawn data on the prototype instance, no registration
var e2 = frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos });
```

`EntityPrototypeRegistry` API: `Register(int prototypeId, IEntityPrototype)`. The typed overload bypasses the registry (no dictionary lookup) but has identical firing-order semantics — `OnEntityCreated` fires before `Apply` for both paths.

### 4.1 TransformComponent in Apply — Position / Rotation initialization pattern

`TransformComponent.PreviousPosition` / `PreviousRotation` drive the view's interpolation (Sample views: PlatformView / CharacterView) and the engine's rollback error correction.

The engine auto-initializes them at first `Frame.Add<TransformComponent>` via a marker field:

- `TransformComponent.PreviousInitialized` (default `false`) — when `Frame.Add` sees this as `false`, the hook copies `Position` → `PreviousPosition`, `Rotation` → `PreviousRotation`, then sets the marker to `true`. The per-tick SavePrev pass at PreUpdate also sets the marker, so any entity that bypasses the Add hook is still covered from the second tick onward.
- Setting `PreviousInitialized = true` in the struct literal **suppresses** the hook — use this when the caller wants the inline `PreviousPosition` value preserved (e.g. an explicit "slide-in" spawn that interpolates from origin).

**Recommended patterns**

```csharp
// (1) Spawn at non-origin, no slide — most common case.
// Hook fires: PreviousPosition := SpawnPosition, marker := true.
public void Apply(Frame frame, EntityRef entity)
{
    frame.Add(entity, new TransformComponent
    {
        Position = SpawnPosition,
        Rotation = SpawnRotation,
    });
}

// (2) Slide-in intent — interpolate from origin to spawn during the first render frame.
// Marker is explicit: PreviousPosition stays at default and is preserved.
frame.Add(entity, new TransformComponent
{
    Position = spawnPos,
    PreviousInitialized = true,
});

// (3) Explicit Previous* — full control, e.g. resuming from a known prior tick.
frame.Add(entity, new TransformComponent
{
    Position         = currentPos,
    PreviousPosition = priorPos,
    Rotation         = currentRot,
    PreviousRotation = priorRot,
    PreviousInitialized = true,
});

// (4) Runtime ref-set after CreateEntity — e.g. teleport an existing entity to a new spot.
// The Add hook cannot observe a post-Add ref-set, so call RefreshPreviousTransform to
// re-sync Previous* with the new Position and suppress the 1-frame interpolation over the jump.
ref var t = ref frame.Get<TransformComponent>(entity);
t.Position    = dest;
t.TeleportTick = frame.Tick;
frame.RefreshPreviousTransform(entity);

// (5) Per-spawn data on the prototype — use the typed-overload path so Apply receives the data.
//     Avoids the ref-set-then-refresh pattern entirely.
var entity = frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos });
```

**Discouraged**

```csharp
// Discouraged — Apply adds a default TransformComponent, then the caller ref-sets Position later
// without calling RefreshPreviousTransform. The Add hook fires with Position == default and the
// marker becomes true, so the subsequent ref-set leaves PreviousPosition stale at the origin.
// Result: a one-frame interpolation from origin to the ref-set Position. Either pass spawn data
// through the prototype (recommended) or call frame.RefreshPreviousTransform(entity) after the
// ref-set.
var entity = frame.CreateEntity(prototypeId);
ref var t = ref frame.Get<TransformComponent>(entity);
t.Position = spawnPos;
// (no RefreshPreviousTransform call — silent regression)
```

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

### Reliable commands (`IReliableCommand`)

A command that must land exactly once on a latency-insensitive action (spawn, purchase, surrender) can opt into the reliable channel by implementing `IReliableCommand` (a subtype of `ISystemCommand`) and being issued via `engine.IssueOnce` (§3.4):

```csharp
[KlothoSerializable(103)]
public partial class SpawnCharacterCommand : CommandBase, IReliableCommand
{
    [KlothoOrder] public int CharacterClass;
    [KlothoOrder] public int SequenceNumber { get; set; }   // serialized; framework stamps it on issue
    public int OrderKey => 0;                                // ISystemCommand ordering key
}
```

In ServerDriven the server assigns the execution tick (confirmed-only, no client predict); in P2P it falls back to the legacy `IssueOnce` path. Reliable commands share the non-slot system channel (not counted by `HasAllCommands`) and are **not predicted** — use only for latency-insensitive actions. See §3.4.

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
    │                                      — no re-fire when a Predicted firing preceded
    ├── OnEventCanceled(tick, event)     — event invalidated by rollback
    └── OnSyncedEvent(tick, event)       — fired only on Verified ticks (EventMode.Synced)
```

> **Which mechanism for "react when X happens"?** Three options, and picking wrong is the most common
> determinism mistake:
>
> | You want | Use | Why |
> |---|---|---|
> | To change frame state in reaction to a component appearing / disappearing | `ISignalOnComponentAdded/Removed<T>` (§2) | Re-runs with the tick, so it is reproduced exactly. |
> | Something to happen in the **real world** exactly once — a sound, a hit spark, an analytics counter | An event (this section), or `ISyncEventSystem` for the confirmed-only channel | A client re-executes a tick many times; only the verified channel fires once. |
> | To read game state each frame for rendering | `engine.PredictedFrame.Frame` (§7) | No callback needed at all. |
>
> The trap is the middle row implemented with the top row. A component signal that increments a counter
> *outside* the frame does not over-count by 2× — in a measured live server-driven match the dedicated server
> executed each tick once while the client executed it **~10 times** on average, as inputs were confirmed. No
> rollback, no hash mismatch, just normal operation.

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
    var frame = _engine.PredictedFrame.Frame;    // FrameRef.Frame (typed, no cast)
    if (frame == null) return;                   // out of ring-buffer range / uninitialized → skip one frame

    var filter = frame.Filter<TransformComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);
        GetView(entity).transform.position = t.Position.ToVector3();   // FP→engine vector; same call on Godot (returns Godot.Vector3)
    }
}
```

> The conversion extension is named `ToVector3()` on both engines (`FP*.Unity.cs` returns `UnityEngine.Vector3`, `FP*.Godot.cs` returns `Godot.Vector3`) — there is no `ToUnityVector3` / `ToGodotVector3`.

> A game's View should read frames via `engine.PredictedFrame.Frame` (`FrameRef.Frame`) — it accesses the frame type-safely without the `(EcsSimulation)engine.Simulation` downcast, and returns `null` outside the ring-buffer range, so the call site just adds a null guard. On engine-internal paths that hold the `Simulation` instance directly, `_simulation.Frame` is equivalent.

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

Every one of them carries a **development-build check** on the storage it walks: removing a component (or
destroying an entity) mid-iteration throws `InvalidOperationException` on the next `Next()` unless the
target was the entity that `Next()` had just handed out. Release players are unaffected — the check
compiles away and the guard occupies no fields. `Docs/ECS.md` §5 has the mechanism, the four limits, and
the per-configuration table.

### View transform pipeline

The view base class (`EntityView` on Unity, `EntityViewNode` on Godot) handles lerp + `_errorVisual` composition + `VerifiedFrameInterpolator` branching as the
standard path. Every view receives one `ApplyTransform` call per frame — driven by **Unity `LateUpdate`** / **Godot `EntityViewUpdaterNode._Process`** (`ProcessPriority = 1000`, after the session driver). Subclasses only
override `ApplyTransform` when special split is required (e.g. root vs interpolation target); regular views just
inherit the base path.

Per-tick game-data updates (animator parameters / Renderer toggle / VFX SetActive) belong in `OnUpdateView` — it
fires once per tick, before the per-frame transform application.

When `ViewFlags.EnableSnapshotInterpolation` is set (typically SD-Client / Spectator remote views), the base path
skips `_errorVisual` composition — the verified-frame interpolation already renders the authoritative state, so
applying rollback-delta-based offset would double-correct and jitter.

### Engine event subscription

`Engine.OnEventPredicted` / `OnEventConfirmed` / `OnEventCanceled` follow an idempotent dispatch pattern. The
`EngineEventOneShot.Subscribe<TEvent>(engine, filter, onPlay, onCancel?, lateGuard?)` helper absorbs the three-way
subscription:

- Predicted and Confirmed are hash-deduped by the engine → `onPlay` fires once per logical event in normal cases.
- On rollback mismatch: Canceled fires `onCancel` first, then Confirmed re-fires `onPlay` with the corrected event.
- `lateGuard` (optional) returns `false` to skip stale `onPlay` after the action's natural end (late-rollback case).

The returned `EngineEventSubscription` is `IDisposable` — call `Dispose()` in `OnDeactivate` to unsubscribe and
release captured lambdas (required to avoid component leak through the engine event delegate).

```csharp
private EngineEventSubscription _attackSub;

public override void OnActivate(FrameRef frame)
{
    _attackSub = EngineEventOneShot.Subscribe<AttackActionEvent>(
        Engine,
        filter:    e => e.Attacker.Index == EntityRef.Index,
        onPlay:    e => PlayAttackAnimation(),
        onCancel:  _ => CancelActionTrigger(),
        lateGuard: HasActiveAction);
}

public override void OnDeactivate()
{
    _attackSub?.Dispose();
    _attackSub = null;
}
```

`OnSyncedEvent` (verified-time channel without Cancel pair) is intentionally outside this helper's scope — subscribe
directly when verified-time fallback Stop is needed.

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

A component declared `[KlothoCleanup(CleanupMode.DestroyEntity)]` (§1) destroys its carrier for you at the end of the tick — adding the marker is the whole "destroy this entity later" idiom, and two markers on one entity destroy it once.

### System-to-System Signal Pattern

A general broadcast to every registered system implementing your own `ISignal`-derived interface. This is a
**different mechanism** from the component signals in §2: `ISignalOnComponentAdded/Removed` deliberately do
not derive from `ISignal`, so `Signal<TSignal>` cannot dispatch them — the engine calls those directly from
`Frame.Add` / `Frame.Remove`.

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

> **Not for per-tick paths.** The invoker lambda above captures `target` and `damage`, so each broadcast
> allocates, and the dispatch walks the whole registered-system list. Fine for occasional, event-shaped calls;
> for something that fires every tick, write a flag on a component and let a system read it. And as with any
> in-simulation callback, it re-runs on every re-execution of the tick — keep it writing to the frame only.

---

## 9. Spectator Session API

Spectator mode is bootstrapped through a dedicated factory. The framework owns `SpectatorService`, the two-config await (`SimulationConfig` + `SessionConfig` arrive in `SpectatorAcceptMessage`), and `Engine` / `Simulation` construction. The game only supplies:

- The connection target (`HostAddress`, `Port`, `RoomId`)
- An `IKlothoSessionObserver` (optional — same lifecycle hooks as the regular client)
- A `CallbacksFactory` that runs **after** server config arrives, so callbacks can size against server-authoritative values

```csharp
// Spectator entry — delegated to KlothoSessionFlow. CallbacksFactory is supplied once at Flow
// construction (game-wide) and fires after SpectatorAcceptMessage delivers server-authoritative
// SimulationConfig + SessionConfig, so callbacks can size against the on-the-wire values.

// Recommended: no-transport overload. The library calls KlothoFlowSetup.SpectatorTransportFactory
// to instantiate the transport (register the factory once during Flow construction).
_session = await _flow.SpectateAsync(host, port, roomId, ct);

// Escape hatch: pass a custom transport instance.
var spectatorTransport = new LiteNetLibTransport(_logger, connectionKey: ConnectionKey);
_session = await _flow.SpectateAsync(spectatorTransport, host, port, roomId, ct);
```

The Flow's `CallbacksFactory` (set on `KlothoFlowSetup`) is invoked once the server config arrives:

```csharp
private SessionCallbacks BuildCallbacks(ISimulationConfig simCfg, ISessionConfig sessionCfg)
{
    // sessionCfg.MaxPlayers is server-authoritative — size callbacks against it,
    // not against any local Inspector value.
    var simCallbacks  = new MySimulationCallbacks(sessionCfg.MaxPlayers);
    var viewCallbacks = new MyViewCallbacks(simCallbacks);
    return new SessionCallbacks(simCallbacks, viewCallbacks);
}
```

The returned object is a regular `KlothoSession`: drive it through `KlothoSessionDriver.Attach(_session)` (same hook pattern as host/guest sessions) and observe lifecycle via the same `IKlothoSessionObserver`. There is no separate spectator-only Engine/Simulation field for the game to track. Spectator is identified at runtime via `session.Engine.IsSpectatorMode` (canonical signal — not `NetworkService == null` heuristic).

`KlothoSession.CreateSpectator(SpectatorSessionSetup)` remains as the synchronous escape hatch (see §X Escape Hatch APIs) for advanced users whose architecture does not fit the Flow pattern.

Notes:
- `SpectatorSessionSetup` has no `CredentialsStore`, no `SessionConfig`, and no `MaxPlayers` field. Those values either do not apply to spectators or arrive over the wire.
- The engine's error-correction path (`CapturePreRollback` / `ComputeErrorDeltas` / Predict-under-Predicted) is active in spectator mode so smoothing applies to spectator views as it does to regular clients.
- **Spectator player list surface**: `ISpectatorService.PlayerCount` and `event OnPlayerCountChanged` mirror the network-service equivalents. The host (P2P) / server (SD) extends its `LateJoinNotificationMessage` (NetworkMessageType=75) broadcast to the `_spectators` set so spectators see existing players appear / late-joiners arrive without polling. Subscribe via `IKlothoSessionObserver.OnPlayerCountChanged` — the session forwards both network-service and spectator-service `OnPlayerCountChanged` so the same subscriber works across all modes.

---

## 10. Dynamic InputDelay (client-reactive policy)

Non-host sessions automatically attach a `DynamicInputDelayPolicy` (in `com.xpturn.klotho/Runtime/Core/Engine/`) that escalates `engine.RecommendedExtraDelay` when the server-driven push control falls behind:

- **Trigger A — PastTick reject sliding window**: non-spawn `CommandRejected(PastTick)` events accumulate within a tick-based window (`SimulationConfig.ReactiveWindowTicks`); when the count crosses `ReactiveEscalateThreshold`, the policy calls `engine.EscalateExtraDelay(ReactiveStep, ReactiveMax)`.
- **Trigger B — rollback burst**: rollback events accumulate within `SimulationConfig.RollbackWindowTicks`; reaching `RollbackBurstCount` triggers the same escalation. Primary fallback for P2P guests (no `CommandRejectedMessage`).
- **Grace gate**: both triggers ignore events within `ServerPushGraceTicks` of the last server `RecommendedExtraDelayUpdate` push (refreshed via `OnExtraDelayChanged`). Prevents double-counting against the authoritative path.
- **Cooldown**: rollback-triggered escalations require `ReactiveEscalateCooldownTicks` between firings.

Thresholds live in `SimulationConfig` and are server-authoritative. Games typically do not subscribe to `OnCommandRejected` or `OnRollbackExecuted` for delay control — only for game-specific responses (e.g. spawn-cmd retry shaping).

---

## 11. Attribute ID planes (mutually independent)

Klotho exposes three positional-int attributes that look syntactically similar but live in independent ID planes:

| Attribute | Plane | Range / convention |
|---|---|---|
| `[KlothoComponent(ComponentTypeId)]` | ECS Frame Heap component discriminator | `0..UserMinId-1` reserved for runtime; user range >= `KlothoComponentAttribute.UserMinId` (100). |
| `[KlothoSerializable(TypeId)]` | Command / Event / Message wire discriminator (per category) | Distinct sub-planes per base class — `CommandBase` / `SimulationEvent` / `NetworkMessageBase` do not share IDs. (Entity prototypes are a separate plane — `IEntityPrototype` registered by id via `EntityPrototypeRegistry`, not `[KlothoSerializable]`.) |
| `[KlothoDataAsset(TypeId, AssetId = ..., Key = ...)]` | DataAsset wire discriminator (`TypeId`) + runtime instance id (`AssetId`) + optional `Key` | `TypeId` is wire-stable. `AssetId` (named) is the runtime instance id used by `IDataAssetRegistry.Get<T>()`. `Key` (named) is an optional string handle for `GetByKey<T>(string)`. Generator auto-emits `AssetId` property + `ctor(int)` + (when `AssetId` is set) parameterless `ctor() : this(AssetIdFromAttribute)`. |

These planes do not collide. `[KlothoComponent(100)]` and `[KlothoDataAsset(100)]` can coexist on different types without conflict.

#### DataAsset lookup overloads

`IDataAssetRegistry` exposes typed lookups that auto-resolve the `AssetId` / `Key` named-args on `[KlothoDataAsset]`:

| Overload | Resolution |
|---|---|
| `Get<T>()` / `TryGet<T>(out T)` | Reads the `AssetId` named-arg on `T`'s attribute. Throws `InvalidOperationException` when the asset omits `AssetId` (single-instance assets only). |
| `GetByKey<T>(string)` / `TryGetByKey<T>(string, out T)` | Reads the `Key` named-arg on `T`'s attribute. Backed by a `(Type, string)` tuple index built at `Register` time. |
| `Get<T>(int id)` / `TryGet<T>(int id, out T)` | Caller-supplied id literal — for multi-instance assets where the same class has multiple registered instances (e.g. `BotDifficultyAsset 1700..1702`). |

The first two are the preferred entry points for single-instance assets — no magic-id literal at the call site. The third remains the escape hatch for multi-instance fan-out where the id is part of the domain (slot index, class index, etc.).

### User-defined NetworkMessageType values

`NetworkMessageType.UserDefined_Start = 200` reserves the byte range >= 200 for game-specific message types. Games may cast freely past this point — both:

```csharp
[KlothoSerializable(MessageTypeId = (NetworkMessageType)200)]   // generator emits raw-cast override
[KlothoSerializable(MessageTypeId = (NetworkMessageType)201)]   // generator emits raw-cast override
```

are auto-handled by the generator. There is no need to manually override `MessageTypeId` — the generator emits the override and the factory registration for both enum-named and raw-cast values. Values below 200 must match a defined `NetworkMessageType` member; unknown sub-200 values are silently skipped (the base class abstract `MessageTypeId` will then fail to compile, surfacing the mistake).

---

## 12. Dedicated Server Setup (RoomManager / RoomManagerConfigBuilder)

§3 covers client / P2P-host construction (`KlothoSession` / `KlothoSessionFlow`). A **dedicated multi-room server** uses a different entry point: `RoomManager` owns one or more rooms, and each room internally wires its own `EcsSimulation` / `ServerNetworkService` / `KlothoEngine` from a shared `RoomManagerConfig`. The game supplies that config; `ServerLoop` drives the tick loop.

```
ServerLoop  ──drives──▶  RoomManager  ──per room──▶  EcsSimulation + ServerNetworkService + KlothoEngine
                              ▲
                              │ RoomManagerConfig (per-room factories + limits)
```

`RoomManagerConfig` is a plain data object with four per-room inputs plus the room limits:

| Field | Role | Game-unique? |
|---|---|---|
| `CallbacksFactory : Func<IKLogger, ISimulationCallbacks>` | Builds each room's `ISimulationCallbacks` (RegisterSystems + game logic, see §3) from the room logger | **◯ — only the game can supply it** |
| `SimulationConfigFactory : Func<SimulationConfig>` | Supplies each room's `SimulationConfig` | — |
| `SessionConfigFactory : Func<SessionConfig>` | Supplies each room's `SessionConfig` | — |
| `AssetRegistry` + `SimulationMaxRollbackTicks` | Inputs the room manager derives each room's `EcsSimulation` from (`maxEntities` / `deltaTimeMs` are read from the simulation config) | — |
| `MaxRooms` / `MaxPlayersPerRoom` / `MaxSpectatorsPerRoom` | Room limits (defaults `4` / `4` / `0`) | — |

### Recommended path — `RoomManagerConfigBuilder`

The preferred way to assemble `RoomManagerConfig` is `RoomManagerConfigBuilder`. It mirrors the client-side `KlothoFlowSetupBuilder` (§3.2): the one game-unique dependency (`CallbacksFactory`) is a **constructor argument** (compile-time required), the rest are fluent `.With*()` methods, and `Build()` validates that every required input is present — turning a missing factory into a clear `RoomManagerConfigValidationException` at startup instead of a `NullReferenceException` at first room creation.

```csharp
// SdSample dedicated server — single concurrent match (maxRooms = 1)
var roomManagerConfig = new RoomManagerConfigBuilder((roomLogger) => new SdServerCallbacks(roomLogger, maxPlayers))
    .WithRoomLimits(maxRooms, maxPlayers, maxSpectatorsPerRoom: 0)
    .WithSimulationConfig(simConfig)            // shared across rooms (value overload)
    .WithSessionConfig(sessionConfig)           // shared across rooms (value overload)
    .WithDerivedSimulation(sharedRegistry)      // derive each room's EcsSimulation from the sim config + registry
    .Build();                                   // Build(strict: true) promotes the non-positive-limits warning to a throw

var roomManager = new RoomManager(transport, router, loggerFactory, roomManagerConfig);
var loop = new ServerLoop(transport, roomManager, tickIntervalMs, logger);
loop.Run();
```

### Shared vs fresh-per-room config

`WithSimulationConfig` / `WithSessionConfig` each have **two overloads** that make the per-room lifetime explicit:

```csharp
.WithSimulationConfig(simConfig)                              // value → one instance shared by every room
.WithSimulationConfig(() => new SimulationConfig { /* ... */ })  // factory → a fresh instance per room
```

- **Value overload (shared)** — a single instance is reused across all rooms. Correct when the config is read-only per room (the standard dedicated-server case).
- **Factory overload (fresh)** — a new instance is built for each room. Use this when rooms may mutate their config, or in `MaxRooms > 1` tests that must verify per-room isolation. The shared overload would otherwise let all rooms alias one instance and hide cross-room state bugs.

### `WithDerivedSimulation` vs explicit `maxEntities`

`WithDerivedSimulation(registry, maxRollbackTicks = 1)` derives each room's `EcsSimulation` from the simulation config: `maxEntities` ← `SimulationConfig.MaxEntities`, `deltaTimeMs` ← `SimulationConfig.TickIntervalMs`, plus the shared asset registry and the room logger. `maxRollbackTicks` defaults to `1` — the server-driven "no rollback" convention (distinct from `SimulationConfig.MaxRollbackTicks`, the netcode rollback depth). This collapses the repeated `new EcsSimulation(...)` boilerplate to one call. It reads the simulation config at room-create time, so it honors the fresh/shared choice above and **requires `WithSimulationConfig(...)`** (checked at `Build()`).

### `Build()` validation

| Check | Severity |
|---|---|
| `SimulationConfigFactory` not set (`WithSimulationConfig`) | Hard — always throws |
| `SessionConfigFactory` not set (`WithSessionConfig`) | Hard — always throws |
| `AssetRegistry` not set (`WithDerivedSimulation`) | Hard — always throws |
| `MaxRooms <= 0` or `MaxPlayersPerRoom <= 0` | Advisory — throws only under `Build(strict: true)` |

`CallbacksFactory` cannot be null (constructor-enforced). `WithRoomLimits` is optional — omitting it applies the `RoomManagerConfig` defaults (`MaxRooms = 4`, `MaxPlayersPerRoom = 4`, `MaxSpectatorsPerRoom = 0`).

### Escape hatch

Constructing `RoomManagerConfig` directly via object initializer (and passing it to `new RoomManager(...)`) remains supported for low-level setup / tests that bypass builder validation — the same escape-hatch philosophy as `KlothoFlowSetup` (§3.2). The builder path is recommended because it surfaces missing factories at `Build()` rather than as a `NullReferenceException` on first room creation.

---

*Last updated: 2026-06-18 — IReliableCommand reliable command channel (§3.4 / §5)*
