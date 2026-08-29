# Cookbook — "I want to X"

The other entry points are **linear**: [GameDevWorkflow.md](GameDevWorkflow.md) §2 walks Step 1→7
(component → command → system → callbacks → events → subscription → view sync), and the two Quick Starts
walk an engine-specific 5-step setup. This page is the other axis. You know what you want to build; this
tells you which of the 27 documents — or which line of the Brawler sample — to open.

Nothing here is new material. Every row points at something that already exists.

---

## 1. Index — task to entry point

### Simulation

| I want to… | Start here |
| ---- | ---- |
| Define a component | [ECS.md](ECS.md) §3 — `unmanaged partial struct`, `[KlothoComponent]`, the float ban |
| Add / read / remove a component on an entity | [ECS.md](ECS.md) §4 · [GameDevAPI.md](GameDevAPI.md) §1 |
| Query entities | [ECS.md](ECS.md) §5 — `Filter<T1..T5>`, `FilterWithout<…>`, and the loop-mutation trap |
| Write a system | [ECS.md](ECS.md) §6 · [GameDevAPI.md](GameDevAPI.md) §2 |
| Register systems and wire a simulation | [ECS.md](ECS.md) §8 · [GameDevAPI.md](GameDevAPI.md) §3 |
| Spawn an entity from a template | [GameDevAPI.md](GameDevAPI.md) §4 (Entity Prototype API) |
| Turn player input into simulation change | [GameDevAPI.md](GameDevAPI.md) §5 (commands) |
| **React to a component being added or removed** | [ECS.md](ECS.md) §6 — `ISignalOnComponentAdded/Removed` and its five rules |
| Make a component live exactly one tick | `[KlothoCleanup]` — [ECS.md](ECS.md) §5 explains the trap it removes |
| Keep state a system owns outside components | [ECS.md](ECS.md) §7 — `ISnapshotParticipant` |
| **Cache something derived from the frame** | [ECS.md](ECS.md) §7 "Derived state outside the frame" — read this *before* writing the cache |
| Know what I may not do (float, wall clock, ordering) | [ECS.md](ECS.md) §11 — the must-read list |

### Presentation & session

| I want to… | Start here |
| ---- | ---- |
| Show simulation state in the engine's scene | [QuickStart.Unity.md](QuickStart.Unity.md) / [QuickStart.Godot.md](QuickStart.Godot.md) — `EntityViewFactory` / `EntityViewUpdater` |
| Read the frame from view code | [GameDevAPI.md](GameDevAPI.md) §7 |
| Make entities move smoothly between ticks (interpolation) | [ViewInterpolation.md](ViewInterpolation.md) — beginner guide: CSP vs snapshot paths, render clock, `InterpolationDelayTicks` |
| Keep a teleport from smearing across the screen | [ViewInterpolation.md](ViewInterpolation.md) §7 — stamp `TransformComponent.TeleportTick` |
| Hide rollback pops on the local player | [ViewInterpolation.md](ViewInterpolation.md) §8 · [SimulationConfigGuide.md](SimulationConfigGuide.md) §1.1 — error correction takes three touches |
| Fire a one-shot effect (VFX/SFX/UI) exactly once | [GameDevAPI.md](GameDevAPI.md) §6 + recipe §2.3 below |
| Join / host / spectate a match | [QuickStart.Unity.md](QuickStart.Unity.md) · [GameDevAPI.md](GameDevAPI.md) §9 (spectator) |
| Run a dedicated server | [Installation.Unity.md](Installation.Unity.md) / [Installation.Godot.md](Installation.Godot.md) · [GameDevAPI.md](GameDevAPI.md) §12 |
| Integrate a lobby / carry tickets | [LobbyIntegrationGuide.md](LobbyIntegrationGuide.md) |
| Carry trusted per-player data | [EntitlementLifecycle.md](EntitlementLifecycle.md) |
| Record and play back a match | [Replay.md](Replay.md) |

### Subsystems

| I want to… | Start here |
| ---- | ---- |
| Fixed-point math (no `float` anywhere) | [DeterministicMath.md](DeterministicMath.md) |
| Physics bodies, colliders, raycasts | [PhysicsWorld.md](PhysicsWorld.md) · [PhysicsVisualizer.Godot.md](PhysicsVisualizer.Godot.md) |
| Pathfinding / agents | [Navigation.md](Navigation.md) · [NavMeshVisualizer.Godot.md](NavMeshVisualizer.Godot.md) |
| Change the navmesh at runtime (buildings) | [Navigation.Rebake.md](Navigation.Rebake.md) |
| Author AI | [HFSM.md](HFSM.md) + recipe §2.4 below |
| Ship static game data | [DataAsset.md](DataAsset.md) |
| Serialize a component / command / event | [Serialization.md](Serialization.md) |
| Pick config values for my genre | [SimulationConfigGuide.md](SimulationConfigGuide.md) |

### When something is wrong

| I want to… | Start here |
| ---- | ---- |
| Diagnose a desync | [DesyncDiagnostics.md](DesyncDiagnostics.md) — start at the symptom table |
| Understand why a rollback storm is *normal* | [DesyncDiagnostics.md](DesyncDiagnostics.md) §6 · [SynchronizationDesign.md](SynchronizationDesign.md) |
| See where tick time goes | `ISimulationConfig.SystemPerfMonitoring` — per-system report with `AddSystem(…, group:)` labels |
| Reduce per-frame memory | [ECSMemoryOptimization.md](ECSMemoryOptimization.md) — `MaxCount`, pruning |
| Inspect live entities / components | [ECS.md](ECS.md) §12 · the editor's entity-component window |
| Fix stutter, jitter or a view that lags / jumps | [ViewInterpolation.md](ViewInterpolation.md) §11 — symptom table |

---

## 2. Recipes

Four things that come up early and are easy to get subtly wrong. Two of them exist in the Brawler sample,
so the recipe is a pointer rather than a snippet.

### 2.1 Spawn a projectile

Brawler has no projectile (its ranged skill applies impact directly), so this is assembled from three
pieces that all exist:

1. **The entity and its initial transform** — an [entity prototype](GameDevAPI.md#4-entity-prototype-api)
   carries the spawn position. Set the position *in* the prototype, not with a `ref` write after
   `CreateEntity`: [GameDevAPI.md](GameDevAPI.md) §4.1 explains the silent regression that follows if you
   do it after (the previous-transform baseline ends up wrong and interpolation snaps).
2. **Movement** — a `PhysicsBodyComponent` with a velocity, or a plain per-tick position step in a system.
   [PhysicsWorld.md](PhysicsWorld.md) for the former.
3. **A lifetime, and destruction at its end** — a counter on the component, decremented per tick, then
   `frame.DestroyEntity(entity)`. Brawler does exactly this for item pickups:
   `Samples/Brawler/.../Systems/ItemSpawnSystem.cs` `TickItemLifetimes` (`:40-49`) — decrement, and destroy
   at `<= 0`.

If the projectile should vanish on impact instead, destroy it from the collision handler the same way, and
note that destroying an entity now fires `ISignalOnComponentRemoved<T>` for each component it carried
([ECS.md](ECS.md) §6).

### 2.2 Apply knockback

Brawler has this end to end. Three parts:

- **The push, applied once** — `CombatHelper.ApplyKnockback(ref Frame, EntityRef target, FPVector2
  direction, int basePower)` (`Samples/Brawler/.../ECS/CombatHelper.cs:8`). One call site per hit.
- **The state that decays** — `KnockbackComponent` holds the remaining push, so the effect survives across
  ticks and rolls back with the frame like everything else.
- **The removal** — `KnockbackSystem` (`.../Systems/KnockbackSystem.cs:50`) removes the component when it
  has decayed. Note *where* the removal sits: it removes the component of the entity the loop is currently
  visiting, which is the safe case. Removing a *different* entity's component mid-loop is the trap
  [ECS.md](ECS.md) §5 warns about.

### 2.3 Fire a one-shot effect (VFX/SFX) exactly once

The problem is not "how do I raise an event" — it is that a predicted tick can run several times. A naive
"if it happened this tick, play the sound" fires again on every re-execution.

Use **`ISyncEventSystem`**: its `EmitSyncEvents` runs only on verified ticks, so what it emits is emitted
once on the confirmed timeline. [GameDevAPI.md](GameDevAPI.md) §6 covers the event API and the
subscription side; Brawler's `PlatformerCommandSystem` implements the interface
(`.../Systems/PlatformerCommandSystem.cs:23`) and enqueues pooled events (`:860`).

Do **not** put this in a component signal listener. Signals fire per *execution*: in a measured
server-driven match the dedicated server ran each tick once while the client ran it ~10 times
([ECS.md](ECS.md) §6, rule 1).

### 2.4 Find the nearest enemy, deterministically

`BotFSMHelper.SelectTarget` (`Samples/Brawler/.../ECS/FSM/BotFSMHelper.cs`) is the reference
implementation. What makes it safe:

- **Fixed-point distance, squared** — `FP64` throughout and no square root. Comparing squared distances
  avoids both the `float` ban and an unnecessary operation.
- **A dense filter walk** — `Filter<TransformComponent, CharacterComponent>` iterates in dense order,
  which is a pure function of the add/remove sequence, so every peer walks the same order
  ([ECS.md](ECS.md) §11 rule 3).
- **An explicit tie-break** — `score < bestScore || (score == bestScore && candidate.Index < best.Index)`
  (`:74`). Dense order is already deterministic, so this is not a desync fix; it makes the pick independent
  of *where* candidates currently sit in the dense array. Without it, removing an unrelated entity can
  swap-back a tied candidate into a different slot and flip which target gets chosen — consistently on
  every peer, and still visible to players as a bot that changes its mind for no reason.
- **Skipping self and the dead before scoring** — cheap rejects first.
