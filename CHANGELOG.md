# Changelog

## [0.9.2] - 2026-08-24

### `Frame.TryRead<T>` — the `Has` guard, moved into the signature

- **`if (frame.Has<T>(e)) { var c = frame.GetReadOnly<T>(e); … }` is now `if (frame.TryRead<T>(e, out var c))`.** The point is not the line count and not the saved lookup: `Get<T>`/`GetReadOnly<T>` are unchecked, and `Docs/ECS.md` §4 has always had to teach "guard with `Has<T>`" as something the caller remembers. `TryRead<T>` makes the guard part of the call, so the unchecked read is not reachable through it. An out-of-range index — a decoded, untrusted id, say — returns `false` instead of faulting, which bare `GetReadOnly<T>` does not.
- **`out` is `default` on `false`, and that is a promise.** The other `TryGet*` members on `Frame` deliberately leave it unspecified because reading `default(ReflectableView)` throws. `T` here is `unmanaged`, so there is nothing to dereference and no reason to withhold the guarantee; `TryRead(…) ? x : default` is a legitimate shape.
- **It copies out by value, so it is not the right call for every component.** A large component — a fixed-buffer struct such as an HFSM host, or `TransformComponent` at 96 bytes — is cheaper through `Has<T>` + `GetReadOnly<T>`, which hands back a `ref readonly` and copies nothing. In this repository that is not a corner case: of the 64 adjacent `Has`→`GetReadOnly` sites, 28 are on components of 64 bytes or more, `TransformComponent` in the view-interpolation path among them. There is deliberately no `ref`-returning variant — that case keeps the two-call idiom.
- **It is not a liveness check, and folding the two calls makes that easier to forget.** Like `Has<T>` it forwards `entity.Index` alone and never reads `entity.Version`, so a stale handle answers about whatever now occupies the slot. The `IsAlive` guard used to sit visibly between two lines; there is no longer a gap for it. `StaleEntityRefGuardTests` now pins `TryRead`'s version-blindness alongside `Has`'s, so the day either becomes version-aware the assert fails and the guards get revisited rather than silently kept. The `Docs/ECS.md` row for stale handles no longer says "safe by design" without saying what you have to call.
- **Existing call sites are left alone.** The API lands first; conversions happen when a file is touched for another reason, and only for the small components — the 28 large ones stay on `ref`.
- **Attribution.** The idea is [`xhocquet/Klotho@f2ca837c`](https://github.com/xhocquet/Klotho/commit/f2ca837cea870adef63ad2a8ffd5774f83a4a3f7) (both repositories are Apache-2.0). This implementation is written here rather than cherry-picked — the name, the `out` guarantee, the documentation and the tests are ours — so it is credited as **based on** that commit, not as a contribution carried across with its authorship.

## [0.9.1] - 2026-08-24

### The loop-mutation trap is now caught at runtime, in development builds

- **`Filter` watches the storage it walks.** Removing a component — or destroying an entity — from anything other than the entity `Next()` just handed out now throws `InvalidOperationException` on the following `Next()`, with the mechanism and the fix in the message. 0.9.0 said of this trap that *"`Docs/ECS.md` §5 has always warned about it and left it to the game"*; for the conditional case that is no longer true. The unconditional case is still better served by `[KlothoCleanup]`, which does not iterate at all.
- **Worth being precise about what the bug was, because it is not what it looks like.** The double visit is *not* a desync. Dense order is hash input — the per-type hash walks the dense array in dense order — and it is serialised raw, so every peer and every re-execution after a rollback restore reproduces exactly the same double visit. What you get is an effect applied twice, with no exception, no hash mismatch and no desync log. That is precisely why it needed a runtime check: nothing else in the engine was in a position to ask.
- **Release builds are untouched, structurally rather than by measurement.** `Check`/`Record` carry `[Conditional]` for `DEBUG`/`DEVELOPMENT_BUILD`/`UNITY_EDITOR`, so the C# compiler removes those call sites and their arguments. Construction goes through a generic factory whose body is empty outside those symbols, so the release build never materialises the watch handle either — there is no dead expression left for the JIT or IL2CPP to eliminate. The guard struct has no fields in release, which a test asserts in every configuration.
- **What it deliberately does not catch.** The check is count-based, so removals that a same-pass addition cancels out go unreported, and so does removing the current entity's component and re-adding it (which skips the entity swapped into the vacated slot and visits the current one twice). Counting is still the right signal: it is the only one that is not itself hashed state, where a version counter would have to live in the heap and would move `StorageLayout`, the state hash and the layout fingerprint. And because a multi-type filter walks whichever storage is smallest at construction, a quiet run means *this run* was clean, never that the code is. `Docs/ECS.md` §5 lists all four limits and the per-configuration coverage.
- **Attribution.** The guard was contributed as [`xhocquet/Klotho@7fae2083`](https://github.com/xhocquet/Klotho/commit/7fae20838ccc51456eb1f708837b44cb74d9f197) and is cherry-picked here with the original authorship intact (both repositories are Apache-2.0). Our follow-up commit reshapes the call sites, adds the storage/span pairing assert, and corrects two statements in the imported documentation — the desync claim above, and the description of the single-type `Filter<T1>` symptom, which raises `IndexOutOfRangeException` rather than reading a stale component.

## [0.9.0] - 2026-08-24

### Component signals fire now — the interfaces had everything except a caller

- **`ISignalOnComponentAdded<T>` / `ISignalOnComponentRemoved<T>` are wired to `Frame.Add<T>` / `Frame.Remove<T>`.** The interfaces, the routing (`SystemRunner.OnComponentAdded/OnComponentRemoved`) and even the determinism analyzer's coverage of listener bodies were already in place; nothing called them, so a game could implement one and watch nothing happen. `OnAdded` fires after the insert with a `ref` into the storage slot, so a listener can adjust what was just added; `OnRemoved` fires before the removal, by value, because after it the slot is gone.
- **A project with no listeners pays one field read.** The engine keeps a per-typeId mask built from the interfaces each system declares — read via `GetInterfaces()`, never `MakeGenericType`, so nothing asks the runtime to materialize a generic instantiation (AOT-safe). The mask is OR-ed in at `AddSystem` time rather than lazily: the entry list is append-only, so the bits only ever turn on, and a listener registered late is visible immediately instead of at the next tick. The one axis that can move underneath it — a recomputed component registry — is compared at tick boundaries, not on the `Add` path, which stays a single check. **What the mask does not buy**: once a type has a listener, each `Add` of it still walks the system list to find the implementers.
- **The masks and the sink live only on the executing frame.** `EcsSimulation.Initialize` hands them to the live frame and `Frame.CopyFrom` does not copy them, so ring slots and the sync-test buffer — state containers that never run systems — stay silent by construction.
- **A listener for an unregistered component type is skipped, not an error.** Test doubles routinely declare `ISignalOnComponentAdded<SomeLocalStruct>` where the struct carries no `[KlothoComponent]`; there is no typeId to key a mask bit on, and failing registration over it would break existing fixtures.
- **Destroying an entity now reports its components.** `DestroyEntity` fires `OnRemoved` for every component the entity held, in ascending typeId order, and — this is a deliberate reorder — *before* `IEntityDestroyedSystem`, so a listener reads "these components are going away" then "this entity is going away". `Entities.Destroy` stays last, so `IsAlive` is true in every listener; the removals, however, are interleaved with the signals, each component going immediately after its own `OnRemoved`, so a listener reads only the higher-typeId components and `IEntityDestroyedSystem` reads none. That last part is a behavioural change and is listed under Breaking changes below. Nothing in this repository observed the old order: no production system implements the entity-lifecycle interfaces. The destroy path needs no generic parameter, so the per-typeId dispatch closes over `T` at registration the way the clear dispatch does — and it is not a thin wrapper, because the value has to be read out of the slot before the removal.
- **A throwing listener propagates, and the entity is left half-destroyed.** That is accepted rather than handled: nothing in the engine or the server catches a tick exception (the one `catch` there is protects `OnFrameBoundary` subscribers), so there is no execution left to observe the partial state, and capture-then-complete machinery would be complexity for a state nobody reads. A test pins the policy so the next reader does not have to re-derive it.
- **The `[KlothoCleanup]` clear reports its components too.** A one-tick marker that disappears silently was the one pairing this feature most obviously invited and most obviously failed at: `[KlothoCleanup]` plus a reaction to it. The clear pass now fires `OnRemoved` per carrying entity, in dense order, before the storage is emptied. The cost is visible and bounded: a cleanup type nobody listens to is still emptied by one dispatch call (O(sparse)); one with a listener is walked entity by entity (O(n)). Only the types with listeners pay.
- **Verified in a live 2-player match** with a temporary probe listener (since no sample registers one): all four paths fired on both peers — add at spawn, explicit remove when knockback decayed, remove-on-destroy when an item expired, and the cleanup clear every tick. The run also put a number on rule 1: the dedicated server executed each tick once, the client **~10 times** on average, and the probe's frame-external counters diverged by exactly that factor while the state hash stayed matched throughout. A listener that keeps state outside the frame is not off by 2×; it is off by however many times the client re-runs the tick.
- **`SystemRunner.Signal<TSignal>` is documented as the separate thing it is.** It was easy to mistake for the delivery mechanism of the component signals — it is not: `ISignalOnComponentAdded/Removed` deliberately do not derive from `ISignal`, so that broadcast cannot dispatch them, and its invocation delegate allocates per call, which rules it out for per-tick paths. It stays (public, and `FEATURES.md` advertises it) with those two facts in its XML docs, and the feature list no longer lumps the two mechanisms into one line.
- **Five rules are now documented** (`Docs/ECS.md` §6): signals are not events and fire again on every resim; a listener must not add or remove components (a swap-back would move the slot its `ref` points at); destroy fires per component, before the entity signal, and each removal lands immediately after its own signal; the `[KlothoCleanup]` clear fires them per entity before emptying the storage, which is where the O(n) cost shows up; and a throwing listener propagates.

### `[KlothoCleanup]` — one-tick components are the engine's job now

- **A component that must live exactly one tick is now a declaration, not a system.** `[KlothoCleanup(CleanupMode.RemoveComponent)]` empties the component's storage at the end of every tick; `CleanupMode.DestroyEntity` destroys the carrier instead. The pass runs after all systems and before the tick advances, so the cleaned state is what gets hashed and snapshotted and rollback/resim reproduce it exactly. Nothing is generated — the mode travels as registry metadata and the engine reads it — so there is no new system to register and no ordering question.
- **This removes a trap rather than saving typing.** Removing a component inside a loop over that same component mutates the dense array under the cursor, and removing one from a *different* entity is worse: the tail entity moves into the vacated slot ahead of the cursor and gets processed twice. `Docs/ECS.md` §5 has always warned about it and left it to the game; for the unconditional case the warning is now unnecessary. `RemoveComponent` does not even iterate — it clears the storage through the existing type-erased dispatch, so cost is O(sparse) per type. `DestroyEntity` collects into a pre-allocated per-simulation buffer and checks `IsAlive` before each destroy, so two markers on one entity destroy it once.
- **The mode is a determinism input, so it is part of the layout fingerprint.** Two builds that disagree about a component's cleanup mode produce different state from tick 0 while looking identical to every existing check. Folding the mode in means the ready exchange shipped above catches it — and because the difference is *inside* matching assemblies, the mismatch error now says so: it prints `cleanup=` next to `types=` and explains that when the type counts agree, neither matching assemblies nor pruning is the fix.
- **Three analyzer rules.** `KLSG_ECS007` (warning) flags cleanup on a `[KlothoCoreComponent]` — pruning exemption and lifetime are orthogonal axes, so the combination is suspicious, not illegal. `KLSG_ECS008` (error) rejects `DestroyEntity` on a `[KlothoSingletonComponent]`, whose carrier may hold other components. `KLSG_ECS009` (error) rejects an undefined `CleanupMode` value, which would otherwise emit as a raw cast and be ignored at runtime — attribute present, nothing cleaned up, no warning.
- **Perf reporting gained two builtin slots** (`(builtin) CleanupClear` / `(builtin) CleanupDestroy`), kept separate because clearing is nearly free while destroying is entities × components. The slot indices are fixed constants, so they cannot be bound conditionally; instead the report now omits **any** row that never executed — which keeps these two out of a project that declares no cleanup types, and a registered-but-never-run system out of every report.

### Setup validation — a build mismatch is caught before the first tick, not by a desync

- **The ready exchange now carries two setup fingerprints, so a cold-start P2P match compares builds before it starts.** The engine already had the values — a component-registry layout fingerprint and a static-environment one — but only compared them when a full state was delivered, which a match that starts everyone at tick 0 never does. `PlayerReadyMessage` now carries both, and because the host relays a ready verbatim every peer ends up comparing against every other. A **layout** difference means the registered component-type set differs, which is state-hash input: those peers would diverge from tick 0, so the side holding transport control (dedicated server, P2P host) refuses the peer with a reason, and a peer without it leaves the match. An **environment** difference (colliders, navmesh, the game's own fingerprint slot) is outside the state hash, so it stays a warning — and it is compared only before the match runs, because runtime rebakes move it legitimately.
- **One peer deliberately compares nothing: the server-driven client.** The server relays other clients' ready verbatim, so this client holds *their* fingerprints and never the server's — the server has no local player and sends no ready. Comparing client-to-client would either stay silent when both differ from the server but agree with each other, or make both sides report without either knowing which one matches the authority. The server is the judge; a client learns its own verdict from the reject reason, or later from the initial full-state comparison.
- **The refusal explains itself.** The error names both sanctioned fixes — load the same assembly set on both sides, or prune the difference via `SetRuntimePrunedComponentTypeIds` (a pruned type leaves the layout, so the fingerprints then match) — and says that pruning is an authority-side action, because the prune set is host/server authoritative and a guest cannot fix it alone. `KlothoSessionSetup.AllowLayoutMismatch` (default `false`) downgrades the refusal to a log for development; it lives on the setup rather than in `ISimulationConfig` because a guest runs the config it received over the wire, which would leave the flag stuck at its default on exactly the peer that needs it off.
- **Spectators are covered from both directions now.** A spectator receiving a full state runs the same static-environment check the players have always run (it was simply never called there), and because it also receives every player's ready it detects a layout mismatch of its own for free. The full-state check is placed after the state is applied but before the buffered ticks drain — draining executes commands that can rebake the navmesh, which would compare this peer's fingerprint against a snapshot the sender took earlier and report a mismatch that is not one.
- **Splitting the fold changed no value.** The combined static fingerprint the full-state path has always sent is still `layout ^ environment`, bit for bit, and the `0` → `1` sentinel normalization still happens in exactly one place. The two new halves are raw, so a peer with no environment source at all reports `0` and receivers skip it — the same "not provided" contract the combined value already had.

### Perf report — systems can be grouped, and the totals say what they leave out

- **`AddSystem(sys, phase, group: "combat")` labels a system in the per-system perf report.** Diagnostic only: it is not a sort key (execution order is still phase, then registration order), never reaches the frame, the state hash, or the wire, and — unlike a `[KlothoCleanup]` mode — is deliberately **not** folded into the layout fingerprint, so peers that label their systems differently still play together. Unlabelled registration leaves the report byte-identical to before.
- **The label is stored as its own field and only prepended for display.** The stat keeps the bare type name (so name-based lookups and existing assertions are untouched) while the report prints `combat/KnockbackSystem` — which is what makes two systems sharing a short type name tellable apart. The engine and Brawler both ship a `CombatSystem`, and until now those two rows were indistinguishable.
- **Group totals, with the columns that cannot be summed left blank.** A `= combat` row is appended after the per-system rows — the body keeps execution order, because those rows are a pipeline and not a ranking. Only additive columns are filled: `peak us`/`peak mem` are omitted because each system's peak happened on its own tick, so neither their max nor their sum is "what this group cost in one tick", and `updates` is omitted because summing it would just report systems × ticks. `avg us` is recomputed from the group's steady sums. The builtin passes form an implicit `(builtin)` group so the totals add up to the report's own total, but on their own they never conjure a totals section into a report that had none. A footnote states the limit that remains: command systems are not measured at all, so the totals are not the whole tick.

### Editor — fixed strings read as text again, and the collider exporter says what it leaves out

- **`FixedString32`/`FixedString64` are `[Primitive]`.** Without the marker the entity-component window recursed into their public fields and showed `Bytes (fixed, no reader registered)` plus a `Length` row, so a name or label field was the one thing you could not read in the inspector. The marker makes the window print `ToString()` on one line, which those types already implement.
- The attribute's own summary claimed it was "treated as primitive types by the source generator" — **the generator never reads it**. Its only consumer is the editor's reflection cache, which is why adding it is display-only: serialization, hashing, and the wire are untouched. The doc comment now says that, so the next assessment of "is this safe to add?" does not have to re-derive it.
- **The export window now lists the colliders it will NOT export.** Two ways a collider silently missed the export: it carried neither `FPStatic` nor `FPTrigger`, or it carried a tag but its GameObject was inactive — `FindGameObjectsWithTag` returns only active objects, so the tag alone was never enough. Both are now counted in the window and listed in a warning, and the same lists are logged on export. The collection path that produces the exported bytes is untouched: it decides collider ids from enumeration order, so the export stays byte-identical.
- **The audit is cached, refreshed deliberately, and holds names rather than objects.** A full `Collider` sweep must not run per repaint, so the counts are cached and re-taken on window focus or from a new `Rescan` button, and opening, closing or creating a scene drops them — showing the previous scene's numbers is worse than showing none. Names are stored instead of `GameObject` references because the audit only ever renders text, and a reference held across a scene change made the window throw `MissingReferenceException` out of `OnGUI`.

### Docs — a way in that is not a tutorial, and a rule `[KlothoCleanup]` made load-bearing

- **`Docs/Cookbook.md`**: a task-to-entry-point index, and a row in the README's document table. The linear paths already existed (`GameDevWorkflow` walks Step 1→7, the Quick Starts walk a 5-step setup); what was missing was the other axis — 26 documents and no way to get from *"how do I do knockback"* to the file that answers it. Nothing in it is new material: every row points at an existing document section or a line of the Brawler sample.
- **Four recipes**, chosen because they come up early and are easy to get subtly wrong: spawning a projectile (assembled from prototype + lifetime + destroy, since the sample has no projectile), knockback (the sample has it end to end — the recipe is a pointer), firing a one-shot effect exactly once (`ISyncEventSystem`, *not* a component signal — a client re-runs a tick ~10 times), and deterministic nearest-enemy selection. The last one documents something the sample does that is easy to miss: its explicit index tie-break is not a desync fix — dense order is already deterministic — it stops the pick from depending on where candidates currently sit in the dense array, which is what keeps a bot from changing its mind when an unrelated entity is removed.
- **`DesyncDiagnostics.md` gained a §0 symptom table.** The guide was already thorough (funnel, log reading, verdict, degradation-is-normal, workflow, before-the-first-tick); what it lacked was an entry from *what you actually see*. Two of its rows are for failures that produce **no log line at all** — frame-external state updated per execution, and a derived cache that is in neither the snapshot nor the hash, so a stale one passes every check.
- **`Docs/ECS.md` §7 now lists every path that replaces a frame's contents.** `ISnapshotParticipant` covers state a system *owns*; a cache *derived* from the frame (a value→entity index, a "changed this tick" list) is a different problem, and `Add`/`Remove`/`DestroyEntity` are not the only paths that invalidate one. The list is `CopyFrom` (which runs **every tick** via `SaveFrame`, and which rollback restore is made of, so one O(1) hook there covers rollback too), `Clear`, `DeserializeFrom`, and — new with this release — **`RunCleanupClear`**, whose bulk pass clears storages through the type-erased dispatch and never calls `Frame.Remove<T>`.
- **The general rule is stated once**: maintaining derived state by hooking the per-entity paths breaks silently every time a bulk path is added. `RunCleanupClear` is the example, and it is the awkward kind — it *does* fire `ISignalOnComponentRemoved<T>`, so a listener sees the components go, while a cache hooked on `Frame.Remove<T>` never hears about them. The failure mode is spelled out too: a derived cache is in neither the snapshot nor `CalculateHash`, so a stale one passes every hash check and every peer goes stale identically; nothing reports a desync and the only symptom is a query returning the wrong entity.

### Breaking changes

- **`IEntityDestroyedSystem` no longer sees the dying entity's components.** `DestroyEntity` now removes them before invoking the callback, so `Has<T>` returns `false` and `Get<T>` throws `IndexOutOfRangeException` inside `OnEntityDestroyed` (`IsAlive` is still `true`). The same applies within the `OnRemoved` cascade for any component with a lower typeId than the one being reported. A listener that needs component data must read it from the `OnRemoved` value it is handed, or cache it into a component the destroy path reports later.
- **`PlayerReadyMessage` gained two fields** (`LayoutFingerprint`, `EnvironmentFingerprint`), appended after the existing ones. Peers must run matching builds — a build without the fields sends a shorter payload that a build with them rejects as malformed and disconnects. Games that isolate builds by transport connection key should bump it.
- **The layout fingerprint's value changed for every build** — the per-type fold now includes the cleanup mode (`CleanupMode.None` for every existing component). Nothing in the repository pins the value, and the state hash is unaffected, but peers still have to run matching builds, and the prebuilt engine DLL shipped with the Godot samples must be rebuilt to agree with a source build. `ComponentStorageRegistry.Register<T>` gained a trailing optional `cleanup` parameter for the same feature: generated registration code recompiles against it, a precompiled caller does not.
- **`AbortReason` gained `LayoutMismatch`** (appended) and **`JoinFailReason` gained `LayoutMismatch`** (enum 18, disconnect-payload wire code 12).

### API additions

- **`KlothoCleanupAttribute` and `CleanupMode` are new** — `[KlothoCleanup(CleanupMode.RemoveComponent)]` empties the storage at the end of every tick, `CleanupMode.DestroyEntity` destroys the carrier, and `CleanupMode.None` (0) is where every existing component lands. `ComponentStorageRegistry.GetCleanupMode(typeId)` and `CleanupTypeCount` read the metadata back.
- **`KlothoEngine` gained the ready-path setup check** — `CheckReadyFingerprints(playerId, remoteLayout, remoteEnvironment, compareEnvironment)`, returning the new **`ReadyFingerprintVerdict`** (`Ok`, `LayoutMismatch`), plus **`GetLocalLayoutFingerprint()`** and **`GetLocalEnvironmentFingerprint()`** as the two halves the existing `GetLocalStaticFingerprint()` folds together. `AllowLayoutMismatch` is the per-peer dev gate.
- **`KlothoSessionSetup.AllowLayoutMismatch` and `SpectatorSessionSetup.AllowLayoutMismatch` are new**, both defaulting to `false` (refuse).
- **`AddSystem` gained an optional `group` label** on `EcsSimulation` and `SystemRunner`, and `SystemPerfMonitor.Bind` gained an optional parallel `groups` span read back through `SystemPerfStat.Group`. Diagnostic only, and omitting it changes nothing.
- **`FixedString32` / `FixedString64` are `[Primitive]`** — display-only, so serialization, hashing and the wire are unaffected; `PrimitiveAttribute`'s own summary now says the same.

### Samples

- **Brawler labels 17 of its 18 system registrations** (`engine` / `move` / `combat` / `world` / `nav`) with **no change to registration order**. The eighteenth is its `ICommandSystem`, left unlabelled on purpose — command systems are not instrumented, so a label would only widen the report's first column for a row that never appears. Labels are `const` because the report groups by exact string: `"combat"` and `"Combat"` are two groups, since nothing beyond trimming is validated.
- **`PlayerIdentityInvariantTests` pins an invariant nothing enforced.** A spawn writes the same player id into `OwnerComponent.OwnerId`, `CharacterComponent.PlayerId` and `SpawnMarkerComponent.PlayerId`, and three different lookups key off three different copies (the command system scans `OwnerId`, the session callbacks scan `CharacterComponent.PlayerId`, `RespawnSystem` scans the marker). A divergence would not surface as a desync — the copies are all hashed, so every peer goes wrong the same way — leaving a lookup silently resolving to a different entity than its sibling. The tests drive the real spawn command through the real system and assert that the three copies agree and that both key paths resolve to the same entity. A third case pins a *stronger* neighbouring guarantee that lookups lean on — every entity carrying `OwnerComponent` also carries `Character`, `Transform` and `PhysicsBody`. That one is structural rather than conventional (the four character prototypes each add all four components, nothing anywhere removes them, and Brawler declares no cleanup types), and it is worth a test because the failure mode is a crash: `ComponentStorageFlat.Get` is an unchecked `ComponentsSpan[SparseSpan[i]]`, so reading a component an entity lacks indexes a span with -1.
- **The Godot addon and the three Godot samples are redeployed** (`GodotP2pSample`, `GodotPolySample`, `GodotSdSample`) with a rebuilt runtime and code generator, and the Unity package's prebuilt analyzer alongside them. Not housekeeping this time: the prebuilt runtime computes the layout fingerprint, so a stale copy would be refused outright by the ready check against a source build.

> Guides: [ECS.md §6](Docs/ECS.md#6-writing-systems) for the component-signal rules, [§3](Docs/ECS.md#3-defining-a-component) for `[KlothoCleanup]`, and [§7](Docs/ECS.md#7-snapshots-rollback--hashing) for state derived from the frame; [Cookbook.md](Docs/Cookbook.md) for the task index and [DesyncDiagnostics.md](Docs/DesyncDiagnostics.md#0-symptom-lookup) for the new symptom table.

## [0.8.2] - 2026-08-21

### Navigation — a building may sit flush against a wall, and a wall of buildings may close a corridor

- **Placement rules gained a boundary policy, and the new `Touch` setting lets a building sit flush against the edge of the walkable region.** Until now *any* contact with the walkable boundary refused the placement — a shared edge, a shared corner, even a collinear overlap — because a footprint touching the boundary used to carve nothing at all. The carving now handles a footprint edge that coincides with the boundary exactly, so a flush placement cuts precisely its own footprint (the carved area matches the expanded footprint bit for bit), and only an actual crossing of the wall is still refused. The default is the historical `Reject`, so nothing changes until a game opts in; and it is deliberately a separate switch from building-to-building contact, because "buildings may pack against each other" and "buildings may lean on walls" are choices a game can want independently.
- **Which means a player can wall a corridor shut.** A chain of flush buildings across a passage splits the walkable region in two, and paths from one side to the other simply fail — the tower-defense move that a "never touch the wall" rule made impossible. An agent trapped on the wrong side does not spin in a retry loop: its path request fails cleanly and stays failed until the map opens again.
- **Two refusals guard the corners this opens up.** The "is this spot inside the region" probe moved from a footprint corner to the footprint's centre, because a sealing placement can have every corner sitting exactly on the boundary; when even the centre lands exactly on a boundary edge — for a rectangle, only when a boundary chord runs corner to corner — inside-or-outside is genuinely undefined, and the placement is refused with its own reason rather than guessed. And a flush footprint large enough to cover the *entire* walkable region is refused as leaving no walkable region — a case the default policy could never reach, now guarded so the player sees a refusal instead of every agent losing the ground under it.

### Navigation — or hang past the wall, at any angle

- **The third policy, `ClipOverlap`, accepts a building that overhangs the wall and carves only the part that is actually on the map.** `Touch` can seal a corridor only where the wall offers a grid position to sit flush against — a wall at an odd angle has none, so no legal placement quite reaches it and a sliver of walkable always survives. Under `ClipOverlap` the footprint is first clipped against the walkable boundary and the clipped shape is what gets carved, so one building dropped across a diagonal passage closes it, whatever the angle. Everything `Touch` accepts still carves bit-identically — the clipping only engages when the footprint actually crosses a wall.
- **The new ways a placement can be refused are all deterministic, and all explainable to the player.** A footprint entirely off the map "clips to nothing"; a footprint that grazes a wall so lightly that the walkable sliver at the crossing is thinner than about 4 mm has no room for a clipped corner to land; a clip that would slice the footprint's on-map part into several separate pieces, and a corner landing exactly on the boundary while the footprint also crosses it, are refused rather than carved wrong. Each comes back as its own reason, so a placement UI can distinguish "nudge it a little" from "that shape does not fit here".
- **The single-ghost preview answers under this policy too, so a placement UI can follow the cursor.** `TryValidateOne` runs the clip stage itself and reaches the same verdict a rebake would — no full-list preview, no building list. It costs more than under the other policies, because answering means actually building the clip rings: **0.23 ms with 4 buildings already down and 0.61 ms with 32** on a 22k-triangle stage. That is a per-frame budget either way, but unlike the other policies the time grows with what is already on the map, so a stage that ends a match with hundreds of buildings is worth measuring. **Allocation does not grow — no preview allocates anything in steady state**, whatever the policy, so following the cursor every frame costs the collector nothing. The refusal it reports names the *ghost*, not a neighbour — when a ghost's clipped shape merges with a building already down, the merged group is identified internally by the older building, and reporting that would tell the player the wrong thing. One verdict stays out of reach of any preview: *nothing walkable was left* is only discovered while the ground is being cut, so a footprint large enough to cover the whole region can show green and still be refused — under the historical `Reject` policy that could never happen, and under these two it can.
- **One V1 limit remains, and it is loud rather than silent.** A rebake that actually clips a footprint skips the incremental patch and rebuilds the whole mesh: slower, never wrong. A placement that does *not* overhang a wall is unaffected — it carves its plain footprint, which is byte-identical to what `Touch` carves, and it reuses the previous mesh exactly as `Touch` does, so turning the policy on costs nothing until a building really hangs over an edge.

### Fixes

- **A development-build check no longer trips on placements the validator has always accepted.** The DEBUG-only pipeline verification asserted on *approximate* T-junctions — any vertex within 2 mm of another edge — but the default rules have always allowed two buildings one or two grid steps apart, which lands inside that tolerance: the placement is accepted, the mesh is correct, and the development build dies anyway. The check now asserts only on *exact* T-junctions on the placement grid, which genuinely cannot occur in valid output — so when it fires it means real misuse, never a tight-but-legal placement. Release builds never ran the check and are unchanged.

### API additions

- **`FPBuildingPlacementRules` gained `BoundaryPolicy`, with a constructor that takes it** — `FPBoundaryPlacementPolicy` is `Reject` (0, the historical behaviour), `Touch` (1) and `ClipOverlap` (2). The values are append-only and carry no ordering; existing rules and `default` land on `Reject`, unchanged.
- **`FPBuildingRejection` gained five reasons, appended** — `ProbeOnBoundaryRing` (6), reachable under the contact policies, and the four `ClipOverlap`-only ones: `ExactBoundaryContact` (7), `ClipCandidateMissing` (8), `ClipSplitsWalkableRegion` (9) and `ClipRunsInterleave` (10).
- **`FPInt128.DivRem` is new** — an exact, deterministic truncated 128÷64 division with `long.DivRem` semantics, added for placing the clip walk's intersection points; it is the single division in the whole rebaker.

### Samples

- **Brawler builds under `ClipOverlap`, so its buildings may straddle walls.** The policy lives inside the deterministic placement rules every peer must agree on, so the client and the dedicated server must be built together; and a recording from an earlier build replays identically unless it contains a boundary placement that was refused *then* — those are accepted now, and only those diverge. The switch was confirmed in a live client-server match with the environment fingerprints agreeing throughout.

> Guide: [Runtime NavMesh Rebake](Docs/Navigation.Rebake.md) — the boundary policies and what each refuses, [§4.6](Docs/Navigation.Rebake.md#46-showing-the-player-before-they-click) for the preview and its per-policy cost, and every cost figure in §8 re-measured on the current runtime.

## [0.8.1] - 2026-08-18

### Navigation — the rebake installs itself, and a rollback cannot leave it behind

- **Placing a building no longer means installing the new NavMesh by hand.** Register `FPNavMeshRebakeDriver` as a system and every tick it works out which buildings belong in the mesh — from the frame's own data — and makes sure that mesh is the one the agents are standing on. This is the part a rollback game could not get right on its own: a placement handled on a *predicted* tick used to install the mesh right there, and when the rollback rewound the frame, nothing rewound the mesh with it. Every re-executed tick then ran against a NavMesh its own frame disagreed with, and since the state hash covers components and not the mesh, no peer reported it. Re-deriving the install instead of treating it as an event collapses four situations into one: running forward, re-executing after a rollback, waking up on a full state, and replaying a recording. A game supplies two small pieces — where its placements live in frame state, and how to install a mesh — around seventy lines for a new game.
- **The engine does the rest of the wiring.** It finds the registered driver when it initializes, then paces the work once per frame and re-establishes the mesh at the two moments a per-tick rule cannot cover: right after the world is created, and immediately after a received full state is applied. A game that used to subscribe to the frame hook by hand is simply told someone else is already pacing, so old wiring steps aside on its own. A failure inside a slice is contained and counted instead of taking the tick down with it.
- **Installing the mesh and reseeding the agents are two calls, and only one of them may ever be skipped.** Installing is derived state — skipping it when the right mesh is already in place is fine. Reseeding writes hashed frame state, so *every* execution of a tick must do it, including a re-execution that found the mesh already installed. Fusing the two hides the reseed behind a peer-local comparison that does not roll back, which desyncs quietly. `FPNavAgentInstaller` packages the pair as two calls for exactly that reason, and re-collects the agent set on both.
- **Deciding whether a placement command is legal is now the engine's job too.** `FPNavMeshPlacementValidator` builds the current placement set from frame state, hands out the next placement number, and answers "does this set bake, with the new building added?" — the same verdict on every peer, from the same data. The trial mesh it produces is deliberately thrown away: the tick that accepted the command may be a prediction, so the mesh must be built again on the tick the building actually appears.
- **A predicting client crosses the same placement tick more than once, so the driver keeps two meshes live.** It arrives at the boundary, rolls back, and arrives again — wanting one of two meshes each time. Holding both turned 147 installs on a live match into 8 rebakes instead of 128, which is the fewest the building sets allow. The cost is a second set of work buffers, about 26.6 MB on a 205,000-triangle stage and nothing worth measuring on a small one; `slotCount: 1` turns it off.
- **A game that rebakes but has not wired the two cross-check values is now told once, at startup.** Neither the mesh nor the shape table it was carved from is part of the state hash, and both hooks are optional — so forgetting one looked exactly like a game that has nothing to fold, on both peers. It is a warning, not an error (nothing has diverged; what is missing is the net that would report it if it did), and only games that actually rebake see it.

### Navigation — a placement no longer costs one expensive frame

- **A rebake can be spread over the frames between a placement and the tick it takes effect on, which cuts the worst single frame from 9.33 ms to 2.13 ms on a 204,800-triangle stage.** The result is byte-identical however many pieces it was done in, so no two peers need to agree on the pacing. If the pieces do not finish in time, the effective tick completes the remainder on the spot — the same thing that happened before slicing existed, so forgetting to pace it is slow, never wrong. The budget is a constructor argument and peer-local, defaulting to 20,000 work units per frame, which is what lets a phone and a PC use different values.
- **Two smaller wins came with it.** Ordering the boundary data during extraction is a radix sort now (2.49 ms → 1.04 ms), and the patch path no longer scans its own output for degenerate triangles in release builds — the triangulation cannot produce them, and development builds keep the check as an assertion.

### Dedicated server — one bad minute no longer condemns a room

- **A room's straggler count is now a sliding window over the last 64 dispatched cycles, and force-close happens at 32 of them.** It used to be a lifetime total that only ever went up, so a room that hit a rough patch early carried it for the rest of its life and eventually got closed while running perfectly. Cycles a room completes on time now push the old ones out.
- **A cycle whose own time budget collapsed is not blamed on the rooms.** When the earlier stage of a server tick overruns and leaves the rooms a couple of milliseconds, their failing to finish says nothing about them. Those cycles are excluded from the count and reported once for the whole cycle, so the log names the real cause instead of listing rooms.

### Breaking changes

- **`Room.StragglerCount` is read-only and now means "in the last 64 dispatched cycles", not "ever".** `MarkStraggler()` takes a flag saying whether this cycle should count toward overload, and `NoteCleanCycle()` is new. Only a host that drove those members itself is affected.

### API additions

- **`FPNavMeshRebakeDriver` is new** — register it as a system and it owns the install: `SetSnapshot`, `Update`, `CorrectNow`, `AdvanceSlice`, plus peer-local counters (`CacheHits` / `CacheMisses` / `SlicedFrames` / `BoundaryFinishes` / `TaskInstalls` / `RebuildInstalls` / `Corrections` / `Reseeds` / `SliceFaults`) that are the only way to see work the mesh cannot show. The two seams a game implements are **`IFPNavMeshPlacementSource`** and **`IFPNavMeshInstaller`**, over **`FPNavMeshTimedPlacement`** (a placement plus the ticks it appears and disappears on).
- **`FPNavMeshPlacementValidator` is new** — `Survey`, `NextSequence`, `TryPreview` (a removal) and `TryPreviewWith` (a placement), for validating a command against frame state.
- **`FPNavAgentInstaller` is new** — `Swap`, `Reseed` and `Collect`, the install-and-reseed pair as separate calls.
- **`FPNavMeshRebaker.TryPreviewPlacements` is new** — asks whether a whole set bakes and hands back no mesh on purpose; **`TryBeginRebake` / `TryBeginRebakePlacements`** return a resumable **`FPNavMeshRebakeTask`** (`Step`, `Result`, `Install`, `Discard`), and **`context.DiscardProduced()`** forgets an output instead of installing it.
- **`KlothoEngine.OnFrameBoundary` is new** — fires exactly once per update in every mode, for peer-local work that is paced by frames rather than ticks. It must not touch simulation state; the engine uses it for the driver.

### Samples

- **Brawler's building code is now a thin adapter over the engine's driver and validator**, and its command path accepts or refuses a placement through the shared verdict rather than its own copy. Its buildings carry the tick they take effect on, so a placement announced now appears on the same tick for everyone.
- **The Godot addon and samples are redeployed** with the updated runtime.

> Guides: [Runtime NavMesh Rebake §6](Docs/Navigation.Rebake.md#6-placing-from-a-command-stream) for placing from a command stream, and [SimulationConfigGuide §4.4](Docs/SimulationConfigGuide.md#44-per-device-knobs-that-are-not-in-simulationconfig) for the slice budget as a per-device value.

## [0.8.0] - 2026-08-14

### Navigation — the NavMesh changes during the match

- **A player can put a building down mid-match and the NavMesh changes to match — deterministically, on every peer, with no pre-authored mesh variants.** The rebaker cuts each footprint out of the walkable region and re-triangulates what is left, so the ground around the building stays walkable. Every step is exact integer arithmetic — orientation and in-circle predicates over a 1/1024-metre placement grid, feeding a from-scratch constrained Delaunay triangulation — so Unity, Godot and the .NET server carve the same mesh bit for bit from the same command. Two simpler approaches don't work, and it is worth knowing why: flagging the covered triangles unwalkable closes paths that are still mostly clear (a single NavMesh triangle can span a whole corridor), and re-running the engine's own floating-point baker gives different machines slightly different meshes.
- **The rebake *is* the validation, and a placement UI can ask it before the player commits.** A refusal comes back as a return value, not an exception: it names the reason and the buildings involved — too close once the agent radius is added, reaching the edge of the walkable region, not on the mesh at all, or swallowing a baked hole — and every peer reaches the same verdict on the same input. A cursor cannot afford a rebake per frame, so that verdict is reachable on its own: every reason a player can be refused is decided before a single triangle is cut, and a preview call runs exactly that much and stops. Given the rebake context it needs nothing else — it checks the ghost against what that context last carved, under the rules that rebake ran with. There is no building list and no second ruleset to keep in step, so a preview that says yes over a rebake that says no cannot happen. (Rebaking without a context means supplying both of those yourself, and then it can: a list that was never accepted, or rules stricter than the rebake got, each answer confidently and wrongly.) A malformed call still throws — an unquantized centre, a stale shape id, a zero-area rectangle — so what the player did and what your code got wrong never arrive on the same branch.
- **Footprints are rectangles, oriented boxes, and hexagons standing in for circles.** A rectangle needs no setup. Everything else comes from a per-stage shape catalog built once at load: a placement then names a shape in it, one of its turns, and a centre — an oriented box turns in `M` quantized directions around the full circle. Before carving, the rebaker widens every footprint by the bake agent radius, so the spacing that leaves two buildings flush is *not* the size you authored. Ask for it instead: two shapes meet exactly when their centres differ by a tiling delta, and the expansion table either hands you that delta or snaps a free-hand position onto the shape's own lattice. Boxes and hexagons tile the plane; rounder shapes place and carve perfectly well but cannot be packed wall to wall, and the catalog says which is which.
- **Wall-to-wall packing is a game rule, so it is turned on explicitly.** By default two buildings may not even graze each other; with contact allowed, a shared edge, a shared corner and a collinear overlap are accepted and carved as one merged obstacle. Two things stay rejected whichever way the flag is set: overlapping interiors, including one building nested in another (which would hand the player a building with a courtyard in it), and a footprint touching the walkable boundary (which would be accepted while carving nothing at all).
- **Placing a building on a 22,321-triangle stage costs about 4.6 ms and 2 KB.** Every rebake starts from the stage's original mesh and carves the whole current building list out of it — a carved mesh is never carved again, so small errors cannot pile up over a long match — but it reuses what the previous rebake established, patching the part that did not change instead of rebuilding it. That shortcut holds as long as the building list stays in placement order: 96% of rebakes patch, against 18% for the same set sorted by position, and a per-context tally reports which of the two your game is getting. Most of the remaining work scales with the size of the map rather than with how much changed, so applying eleven placements at once costs about what one costs, and each building already standing adds roughly 0.09 ms.
- **Installing the result is free.** The swap rebinds the query, pathfinder and funnel the agent system already holds, and re-extracts the ORCA obstacles into buffers it keeps across swaps, so it allocates nothing — the whole cost of a placement is the rebake. Reseeding the agents afterwards is yours to call, and it is not optional: every agent still holds a triangle index and a corridor pointing into the mesh that was just replaced, and both are hashed frame state. The per-stage snapshot is immutable and shared read-only across rooms, the per-room context holds the work buffers, and both are built at load — the first rebake in a cold process pays a few hundred milliseconds of JIT, which prewarming absorbs at load instead.
- **The NavMesh stays out of the state hash, and a fingerprint stands in for it.** The mesh is large, and rebuilding it is deterministic anyway, so peers compare one small value instead: implement the nav fingerprint source and the engine folds that value into the environment fingerprint exchanged with a full state. A diverged mesh then surfaces at join or resync, rather than several ticks later as an unexplained agent-position desync. A new callback fires once a received full state has been applied, and that is where a joining peer rebakes — from the building components the state just restored. Which is why the building set belongs in frame state, and not in a system-local list no joiner can reproduce.
- **Known limits.** The base mesh must be single-level (stacked floors and bridges are refused when the context is created, with the reason named), each footprint must be convex, and a base mesh carrying non-uniform area or cost values loses them on re-triangulation. A rebake is a discrete event, not something to call every frame. A preview writes its ghost into storage the context owns — and, since 0.8.2, the clip stage's reused buffers live there too — so it belongs on the thread that rebakes, one previewer per context.

### Navigation — a smaller triangle, a faster baker

- **`FPNavMeshTriangle` is 88 bytes, down from 120.** Its six stored portal vertex indices collapse into one bit per edge: a portal pair is always a permutation of that edge's own two vertices, so all those indices really carried was whether the pair is flipped — and the neighbour index already says whether an edge has a portal at all. A 22k-triangle stage now holds about 1.9 MB of triangle data per mesh instead of 2.7 MB, and that saving counts three times over, since the base, installed and retired meshes are all resident across a swap.
- **The build pipeline got substantially faster on large meshes, with byte-identical output.** A conforming input skips re-triangulation entirely, T-junction candidates are narrowed by a uniform grid hash instead of a full scan, and adjacency is ordered by a two-pass radix sort. One of those was a real defect: the edge key's XOR hash collided systematically on large meshes and degraded adjacency building to near-quadratic. Bake input is also snapped to the placement grid and re-welded as the pipeline's first step, which is what lets the runtime rebaker accept a shipped asset at all.
- **`FPNavMesh` carries logical counts alongside its arrays**, so storage can be pooled and recycled across swaps rather than reallocated per rebake; the contents are read through spans bounded by those counts.

### Desync diagnostics — name the build before the component

- **The component registry now reports a layout fingerprint, because the registered type set is itself state-hash input.** The layered hash folds in every registered type — including the ones with zero live instances — so two peers that register *different* type sets can never agree on a hash, however identical their worlds. The case that prompted this: a Unity Editor session registers components from editor-only test assemblies while a dedicated server has none of them, so every tick diverges from the first, and the per-component breakdown points at symptoms rather than the cause. The fingerprint covers the type set and each type's sizing. It is logged at boot on every peer, printed alongside a hash-mismatch diagnosis with the advice to compare it *first*, and folded into the environment fingerprint compared at join — so this mismatch reads as one line, instead of a cross-reading of two logs, an asmdef and the hash implementation.
- **The environment fingerprint now has a slot the game owns.** The engine folds three sources of its own — the registry layout, the static colliders and the NavMesh — and a game can add a fourth for anything that must be identical across builds but is deliberately outside the state hash. The rebake shape catalog is the case that prompted it: a placement is only a *reference* into that table, so two builds that disagree about the table carve different meshes from identical commands, and nothing looks wrong until the first building goes down. Tuning tables, asset id maps and balance data have the same shape. A mismatch reports each local source separately, so a game-data difference does not read as a NavMesh problem; contributing nothing is a no-op.

### Breaking changes

- **The baked NavMesh binary format advanced to version 4, so `.bytes` assets must be re-exported.** The portal indices are gone from the on-disk triangle, and the bake now snaps and re-welds its input onto the placement grid — an asset exported before this is refused by the runtime rebaker as not grid-snapped. Re-bake and re-export each stage's NavMesh from its scene.
- **`FPNavMesh.Vertices` / `Triangles` / `GridCells` / `GridTriangles` are spans over the live range, not arrays.** The backing arrays may be larger than their contents now that they are pooled, so the matching `VertexCount` / `TriangleCount` / `GridTriangleCount` are the truth about length. Code that held one of those arrays needs to read through the property instead — and must not cache it across a swap.
- **`FPNavMeshTriangle` lost its six `portal*` fields.** Read portals through `GetPortal(edgeIndex, out left, out right)`, which returns `(-1, -1)` for a boundary edge.
- **`EcsDebugBridge.RegisterNavMesh(mesh, query)` became `RegisterNavMeshProvider(provider)`** (with a matching unregister). A cached mesh goes stale at the first runtime swap, so the bridge reads through the provider; consumers should read `NavMesh` / `NavQuery` once into a local per callback rather than twice across a swap.

### API additions

- **`FPNavMeshRebaker` is new** — `CreateContext` / `CreateSnapshot` for the per-room and per-stage objects, `TryRebake` / `TryRebakePlacements` for the game-facing form (a refusal is `false` plus a reason), `Rebake` / `RebakePlacements` for the throwing form used by tests and tooling, `ComputeFingerprint` for the cross-peer check, and on the context `Snapshot`, `CommitSwap`, `ShapeExpansion` and `PatchOutcome`. **`TryValidateOne` / `TryValidateOnePlacement`** are the preview that stops before the first triangle is cut: given the context, the ghost is the only argument and `rules` defaults to the ones that context rebaked under; given a snapshot instead, you pass the accepted list, its live count and an `FPBuildingPreviewScratch`.
- **The placement types are new** — `FPBuildingRect`, `FPBuildingPlacement`, `FPBuildingPlacementRules`, `FPBuildingRejection` / `FPBuildingRejectionInfo`, `FPBuildingPreviewScratch` (the buffers a stateless preview reuses, so a per-frame call allocates nothing), and the shape table: `FPBuildingShapeCatalog` with its `Hash` and `MaxVertexCount` (the widest entry, which is the headroom a ghost of any shape needs), `FPBuildingShapeCatalogBuilder` (`AddObb` / `AddHexagon` / `Add`), and `FPBuildingShapeExpansion` (`TryTilingDelta`, `TrySnapToLattice`).
- **`FPGeoPredicates` is new** — the exact `Orient2D` / `InCircle` predicates the triangulation rests on, plus the placement grid: `Quantize`, `IsOnGrid`, `Snap` / `Unsnap`, `SNAP_FRAC_BITS` and `MAX_SNAPPED_COORD`. `FPConstrainedDelaunay` and `FPConvexOffset` (miter expansion of a convex ring) are new alongside it.
- **`FPNavAgentSystem` gained `SwapNavMesh(mesh)`** — install a rebaked mesh, rebinding the query, pathfinder and funnel it holds — **plus a four-argument form** taking instances you built yourself, **`ReseedAgents(...)`**, and **`CurrentMesh` / `CurrentQuery`** for reading the live instances instead of caching them.
- **`INavFingerprintSource` and `IGameFingerprintSource` are new** — implement on a registered system to have the engine fold your value into the environment fingerprint. **`ComponentStorageRegistry.LayoutFingerprint` / `LayoutTypeCount`** expose the registry's own.
- **`ISimulationCallbacks.OnFullStateApplied(engine, frame)` is new** — a default no-op, so existing implementations are unaffected. It runs after a received full state is live, including one whose hash did not match, and must not throw.
- **`FPNavAvoidance.LoadObstacles` gained a count-taking overload**, so a swap can re-extract obstacles into buffers it already owns.
- **`RejectionReason` gained `InvalidPlacement` (11)**, appended.

### Samples

- **Brawler places and removes buildings during a match.** A player command carries a shape and an orientation into the deterministic command stream; the tick rebakes, swaps, reseeds, and reports a refusal back to the player who caused it. Its catalog holds a deliberately non-square 2×1 oriented box in 16 turns and a hexagon that tiles, hexagon placements snap onto the hexagon's own lattice so consecutive ones land flush, and every peer builds its rebake context through one factory — the client and the dedicated server once disagreed about whether there was a catalog at all, and spent the match trading resync verdicts. A headless bot demo drives placements on an interval for soak runs, and a client that joins mid-match rebakes from the building components its full state restored.
- **The Brawler stages and the Field test asset are re-exported to the version-4 format**, grid-snapped so they can be rebaked at runtime.
- **The Unity NavMesh visualizer follows a runtime swap.** Connected to a running session it re-reads the live mesh when a placement replaces it — rebinding the query and clearing path results, which were indexed into the old mesh — without resetting an in-progress editor experiment, and without overwriting a file the user loaded mid-play. **The Godot addon is redeployed** with the rebaker.

> Guide: [Runtime NavMesh Rebake](Docs/Navigation.Rebake.md) — placing shapes, what gets rejected, lifecycle, determinism and the measured costs.

## [0.7.1] - 2026-08-04

### Tests — the engine-agnostic suite runs headless under `dotnet test`

- **~1,600 engine-agnostic tests moved off the Unity Test Runner into a single dotnet project, `Klotho.Runtime.Tests` (NUnit).** The deterministic core — fixed-point math, ECS, physics, navigation, networking, replay, and the desync/probe diagnostics — now runs headless under `dotnet test` in a couple of seconds instead of a batch-mode editor boot. Brawler's EditMode suite drops from 2,552 tests to 918; what stays behind is what genuinely needs the editor — the Unity `Vector`/`Quaternion`/`AnimationCurve` parity oracles, the GC-allocation constraints, the Gameplay-assembly systems, and full engine integration. NUnit 3's classic assertions match the API the editor tests were already written against, so the bulk of the move is source-identical; the pure test harness (headless simulation, transport, sweep matrix) is shared into the new project rather than converted.
- **The two dotnet test projects collapsed into one.** The former `Klotho.Core.Tests` (xUnit) — home of the headless contract, entitlement, replay-fidelity, and NavMesh-obstacle tests — was converted to NUnit and folded into `Klotho.Runtime.Tests`, ending a split that existed only by test framework and not by scope, since both exercised the same runtime assembly. New engine-agnostic tests now have one home and one convention.

## [0.7.0] - 2026-07-23

### Navigation — agents avoid walls, not just each other

- **ORCA avoidance now steers agents around static geometry — walls, cliffs, and obstacle blocks — on top of the existing agent-agent avoidance.** Each nearby obstacle edge contributes a velocity half-plane (the RVO2 obstacle line, ported to FP64), so an agent's chosen velocity is one that clears both the crowd and the environment in the same solve. Unlike the reciprocal agent lines — which the solver relaxes when a crowd is over-constrained — obstacle lines are **hard constraints** the linear program never relaxes, so a velocity can never leak through a wall no matter how packed the surrounding agents are. Convex and non-convex (reflex) obstacle rings are both handled. It is opt-in and layers onto the same avoidance object, so a game that only wants agent-agent avoidance is unchanged.
- **Obstacles come from two interchangeable sources in one flat format.** The NavMesh's own boundary is the first: a new extractor walks the open boundary edges — orienting each edge by the mesh interior rather than assuming a triangle winding, and chaining them around shared vertices through the walkable fan so pinch and bowtie vertices resolve correctly — and emits obstacle rings with outer boundaries and holes wound consistently, automatically. The second is caller-supplied convex polygon rings, for procedurally placed blocks that never went through a bake. Both feed the same load call, and a single helper on the agent system extracts-and-loads the current NavMesh's boundary in one step.
- **Obstacle selection follows the NavMesh graph, so a stacked floor's walls don't bleed through.** The agent system picks obstacle candidates by walking the NavMesh adjacency outward from the agent's own triangle, considering only walls reachable on its own floor or ramp — an upper storey whose walls overlap in the XZ plane never constrains the agent below it. Expansion is bounded by a per-edge step-delta floor test, a padded range, and a climb cap that auto-derives from the mesh's recorded bake slope where present; an agent that can't be localized falls back to a full scan. Only the *selection* is graph-aware — the half-plane math stays 2D and the hot path stays allocation-free.

### Navigation — clearance that matches the funnel path

- **A baked NavMesh now records the agent settings it was baked with, and the runtime uses the radius to cancel double-counted clearance.** A boundary is already inset from the real wall by the bake agent radius, and ORCA independently holds an agent its own radius off the obstacle line — so a naive setup stops agents roughly two radii short of a wall and chokes tight corridors. The asset carries its bake radius (alongside slope, height, and climb) in a self-describing settings block, and loading the boundary auto-applies it as an inset that cancels the baked offset: under a matching bake-and-simulation radius the boundary itself becomes the constraint and agents hug corners exactly as the point-agent funnel path does. The inset is per-peer local config that rides the asset automatically, and a consumer may still override it after loading.
- **The obstacle time horizon is short, so an agent moving along a wall slides past it rather than braking against it.** The horizon decides how far ahead a wall becomes a hard constraint; keeping it short means an edge an agent is merely travelling parallel to doesn't turn into an active constraint, so agents follow walls cleanly instead of decelerating and hugging them.

### Breaking changes

- **The baked NavMesh binary format advanced to version 3, so `.bytes` assets baked by earlier versions must be re-exported.** The format gained the bake-settings block (agent radius / slope / height / climb) the clearance inset reads at load time; an older file has no such block and will not load. Re-bake and re-export each stage's NavMesh from its scene.

### API additions

- **`FPNavMeshObstacleExtractor` is new** — extracts ORCA obstacle rings (`FPVector2[]` vertices + `int[]` polygon offsets) from a NavMesh's boundary; build/load-time only.
- **`FPObstacleVertex` is new** — the static-obstacle vertex struct (flat-array ring: point, unit direction, convex flag, prev/next, polygon index), the FP64 equivalent of the RVO2 obstacle vertex.
- **`FPNavAvoidance` gained `LoadObstacles(FPVector2[] vertices, int[] polygonOffsets)`, `ObstacleRadiusInset`, and a `TimeHorizonObst` defaulting to a short horizon**, plus obstacle diagnostics (loaded count, selected count, obstacle-line overflow, and which selection path ran).
- **`FPNavAgentSystem` gained `LoadNavMeshObstacles()`** — extracts and loads its own NavMesh's boundary in one call (invoke after configuring avoidance) — **and `MaxClimbWithinHorizon`** for the graph-local selection's climb cap when the mesh records no bake slope.
- **`FPNavMesh`'s constructor gained optional bake-settings parameters** (`bakeAgentRadius` / `bakeMaxSlopeDeg` / `bakeAgentHeight` / `bakeAgentClimb`, all defaulting to zero) and the matching read-only properties; existing callers that omit them are unchanged.

> Obstacle avoidance is per-peer local — obstacle geometry is not on the wire, in a frame, or in the state hash. Every peer loads the same baked geometry and computes identical avoidance velocities, so in a server-driven session **both the client and the server must load the obstacles**; wiring only one side makes the velocity solve diverge. See [Navigation §Static Obstacles](Docs/Navigation.md#static-obstacles-orca).

### Samples

- **Brawler wires NavMesh obstacle avoidance on both its client and its dedicated server**, so bots steer around the stage walls; its stages are baked with a small agent radius so the clearance lines up end to end.
- **The NavMesh visualizer draws the extracted obstacle rings** on both the Unity editor window and the Godot dock, so a stage's obstacle boundary can be inspected before it ever runs; the Godot samples ship the same view.

## [0.6.1] - 2026-07-21

### Profiling the simulation tick

- **A per-system profiler now shows where each tick's CPU and allocation actually go.** Per registered system it accumulates the execution count, the average and worst-case (peak) execution time, and the total and worst-case per-tick heap allocation — so the systems that dominate the frame budget, or leak garbage every tick, are named from a measurement rather than a guess. The peak column surfaces the occasional multi-millisecond hitch that an average hides, and the allocation column is a standing check on the "no per-tick garbage" rule: a system that should be allocation-free but isn't shows up immediately. It counts every execution, including re-simulated rollback ticks, because those are real cost. The profiler is a development aid: off by default, allocation-free on the steady path when off (the timing and allocation probes are not even called), and per-peer local — the flag is not wire-propagated, so measure on the authoritative peer or a replay. Like the component-memory report, it prints once at match end, on the same log channel.
- **Warmup executions are kept out of the steady figures.** A system's first ticks pay one-time costs — JIT, lazy initialization, buffer growth — that would otherwise make a genuinely clean system look like it allocates forever. Each system's early executions fold into a separate warmup bucket, so the reported average, peak, and steady allocation reflect real gameplay while the warmup figure stays visible rather than discarded. A genuine mid- or late-match one-off (a match-end event, say) correctly stays in the steady column, since only the opening ticks are set aside.

### Runtime allocation

- **The physics broadphase no longer allocates on every tick.** It ordered its collision-pair lists with the standard library's `List.Sort(IComparer)` — which boxed the value-type comparator at the call site and, inside the sort, had the runtime build a throwaway `Comparison` delegate from the comparer on every call — so each of the three sort sites (the spatial-grid pairs, the dynamic-vs-static pairs, and the trigger pairs) leaked garbage every tick a match had moving bodies. The sorts now use an allocation-free in-place routine that keeps O(n log n) and produces the identical ordering, so the change touches only the garbage, never the simulation result — it is byte-identical and determinism-neutral. Measured on the Brawler sample, the physics system's per-tick allocation dropped from ~160 bytes to zero.

### Breaking changes

- **`ISimulationConfig` gained `SystemPerfMonitoring` (get-only).** A custom implementation must add it; the built-in `SimulationConfig` and the Unity and Godot config assets already carry it, so only hand-rolled implementers are affected. It is opt-in (default off) and per-peer local — not part of the config wire.

### Samples

- **Brawler's stage floors no longer drop a shielding player through the map.** Two floor tiles were missing the static-collider tag the exporter keys on, so they were silently left out of the exported physics data — a character that walked onto them with its trap knockback suppressed (while shielding) found no ground beneath it and fell to its death. The tiles are tagged and re-exported.

## [0.6.0] - 2026-07-17

### Component storage — sizing the ECS heap

- **A component type can now cap how many instances it reserves, instead of every type sizing for the full entity budget.** Storage is laid out once and held for every frame the rollback ring retains, so a type that only ever holds a handful of instances but reserves for the whole entity count wastes that space on every retained frame. A `maxCount` caps a type's concurrent-instance slots, and the saving multiplies across the ring. It is authored two ways, combinable: a build-time default on the component (`[KlothoComponent(id, MaxCount = N)]`) as a conservative safe bound, and a per-type config override (`ISimulationConfig.ComponentMaxCountOverrides`) for game- or level-specific tuning that wins over the attribute. A type with neither reserves the full budget exactly as before. The cap only shrinks the reserved layout — it never enters the serialized state or the state hash — so a capped match is byte-identical to an uncapped one; and because the cap travels on the config wire, a server-pushed client and every peer size their heap identically. Exceeding a type's cap fails fast at the offending add (uniformly on every peer) rather than corrupting the heap, so a cap set too low surfaces immediately; singletons stay a single slot regardless.
- **A game can now drop component types it never uses out of the reserved layout entirely.** A type no registered system ever touches still reserves its full per-frame footprint; listing it on a reservation denylist removes its storage layout completely — reclaiming the whole reservation, including the entity-indexed floor that a `maxCount` cap alone cannot shrink. The denylist names only the types to drop, so forgetting one merely reserves it (never a crash), and types are declared through the component registry rather than as raw ids. Engine-required types (transform, random seed, match-end state, and the like) are force-excluded, so a denylist can never accidentally strip a type the engine depends on. Reach for this sparingly: a type sitting at zero instances is not automatically removable, because a system that filters or checks for it opens its storage even at zero — and accessing a dropped type raises a clear error at a single chokepoint. Like the cap, the denylist is process-global and carried on the config wire, so server-pushed clients drop the identical set; an empty denylist is byte-identical to before.

### Measuring before tuning

- **A component-memory report shows where the ECS heap actually goes.** Per registered type it breaks down the reserved, live, and wasted bytes — per frame and multiplied across the rollback ring — with the totals that tell you which few types dominate, so caps are chosen from a measurement rather than a guess. An opt-in peak sampler accumulates each type's high-water live count over verified ticks — immune to prediction and rollback double-counting — and recommends a cap from the observed peak. The sampler is a development aid: off by default, allocation-free on the steady path when off, and per-peer local (it is not wire-propagated, so measure on the authoritative peer or a replay).

### Breaking changes

- **`ISimulationConfig` gained `ComponentMaxCountOverrides`, `PrunedComponentTypeIds`, and `ComponentMemoryPeakSampling` (all get-only) plus `SetRuntimePrunedComponentTypeIds(int[])`.** A custom implementation must add them; the built-in `SimulationConfig` and the Unity and Godot config assets already carry them, so only hand-rolled implementers are affected.

### API additions

- **`KlothoComponentAttribute` gained an optional `MaxCount`, and a new `[KlothoCoreComponent]` marks the engine-required types a denylist can never drop.** Existing components are unaffected — no `MaxCount` means the full entity budget as before.
- **`EcsSimulation`'s constructor gained optional `maxCountOverrides` and `prunedComponentTypeIds` parameters** (both default to none, i.e. unchanged), and **`Frame` gained `GetLiveCount(int typeId)`** — a read-only live count by type id used by the memory diagnostics.
- **The config wire (`SimulationConfigMessage`) now carries the per-type caps and the reservation denylist**, so a server pushes them to its clients; the fields are appended, but the reference lobby, dedicated server, and clients should run matching versions so every peer sizes its heap identically.

### Samples

- **Brawler caps its components and prunes one it never uses.** Its gameplay components carry conservative `MaxCount` bounds, its config trims the engine's core components to the game's actual counts, and it drops the single registered component no Brawler system touches — a worked before/after of both levers. The dedicated server gained a `--memreport` mode that dumps the full registered set and its per-frame reservation without running a match, for sizing the caps.

> Guide: [ECS Memory Optimization](Docs/ECSMemoryOptimization.md) — measuring the heap, choosing caps, and when (not) to prune.

## [0.5.7] - 2026-07-14

### Desync diagnostics

- **When peers disagree, the engine now pinpoints where they diverged instead of only reporting that they did.** State hashing is layered: each tick's hash breaks down into the whole-frame hash, a hash and count per component type, and a hash per snapshot-participant system — so a divergence can be attributed to the exact component type or system that differs, not just a mismatched total. The breakdown is a byproduct of the hash the engine already computes, so the steady per-tick path stays allocation-free.
- **A short rolling window of recent per-tick hashes is retained during play**, so a divergence can be read against the ticks around it rather than only at the tick it surfaced. It is on by default and can be tuned down or disabled.
- **On detection, an online probe exchanges hashes between the disagreeing peers to localize the divergence.** A peer asks the other for its per-tick hashes across the disputed window, narrows to the first tick they disagree on, then requests that tick's component/system breakdown — concluding with a verdict that names the tick and the diverging layer. Peer-to-peer, it also classifies whether the peers executed **different inputs** (a propagation problem) or the **same inputs produced a different result** (a determinism problem), by digesting the confirmed command set alongside the state. A server-driven client settles the same question directly against the server's verified state. The probe path is budgeted and rate-limited; a forged or flooding probe can at most produce a misleading log line. See the [Desync Diagnostics](Docs/DesyncDiagnostics.md) guide for reading the resulting logs and verdict.

### Replay fidelity

- **A predicted tick that is later verified is now recorded to the replay.** Recording happens at the moment a tick is confirmed, so a tick that first ran as a prediction — and matched on arrival — no longer leaves a hole in the replay, and a late real command that corrects a predicted tick overwrites the recorded entry instead of leaving a stale one.
- **A corrective reset or resync now ends the replay cleanly at the last verified tick before the jump.** A full-state reset moves the simulation onto a new lineage with no recorded transition, so recording past it made playback diverge; the recording now stops at the pre-reset boundary. This holds on every path a reset originates — the host that initiates it, a peer that receives it, and a server-driven client that resyncs — and a completed replay no longer trails one never-recorded tick past its end.

### Fixes

- **The diagnostic response path no longer trusts wire-supplied lengths or tick ranges.** A crafted response could satisfy a payload's length check through 32-bit overflow and drive a multi-gigabyte allocation, and a crafted request's tick window could spin the responder in an unbounded loop — stalling the responding peer, and on a dedicated server the whole room. Both are now bounded.
- **A peer-to-peer divergence verdict pairs the local and remote hashes from the same tick.** The remote hash had been taken from the detection tick while the local hash came from the (often earlier) first diverging tick, so the logged pair could be internally inconsistent; the verdict now carries the diverging tick's remote total.
- **A departed peer's probe bookkeeping is cleared on disconnect**, so a recycled peer id cannot inherit a stale entry and suppress a later diagnosis.
- **A dedicated server rate-limits full-state requests**, so a single client cannot force repeated full-state rebuilds.

### API changes

- **`SendSyncHash` now also carries the command-set hash** — `SendSyncHash(int tick, long hash, long cmdHash)`. Custom `IKlothoNetworkService` / `IServerDrivenNetworkService` implementers must add the parameter. The extra hash drives the input-vs-state divergence classification and does not affect recovery.

## [0.5.6] - 2026-07-12

### Match results reported to the lobby

- **A dedicated server now reports each match's verified outcome back to the lobby**, so the game backend (persistence, leaderboards, rewards) hears the result from the server authority — never from a client. The game authors the payload: its match-end event implements the new `IMatchResultProvider`, exposing an opaque result blob (winner, per-player stats, acquisitions) assembled from verified simulation state at the moment the match ends. The engine and the wire carry only the bytes — the game owns the schema on both ends. Opt-in: an event that does not implement the interface reports nothing, and with no lobby wired nothing is emitted.
- **Verified identities travel alongside the result, outside the game payload.** The report carries a per-player roster of the lobby-verified account and display name — including players who left before the end — keyed by the same player id the game blob uses, so the backend joins stats to accounts by id. The deterministic game payload itself stays identity-free.
- **Delivery is at-least-once and intake is idempotent.** The server appends each result to a local crash-backstop journal, then sends and re-sends until the lobby acknowledges; a reconnect re-flushes everything un-acked. The lobby hands the result to the game backend through a new `IMatchResultSink` seam and acknowledges only after that handoff returns cleanly — a backend failure withholds the ack so the server retries, and a duplicate is acknowledged without being processed twice. Results are flushed ahead of the room's state report, so the lobby processes the outcome before the room's reclaim signal.
- **Aborted and abandoned matches are reported too.** A server-authoritative mid-match abort (state divergence) and the abandoned case — every player left mid-match with no end ever reached — arrive as an abort notification with a reason code. The two reuse the same "no culprit id" value with different meanings (unknown culprit vs. the whole roster walked away), so penalty and match-void policy should branch on the reason, never on the id alone. A match that never started is not reported; the lobby's reservation timeout reclaims it.

### Per-match instance identity

- **The lobby now mints a unique id for every match instance when it binds a room.** The match id players share to meet in a room is a rendezvous key — it repeats when the same party re-queues — so keying results by it would discard a real second match as a duplicate. The instance id (`{matchId}#{token}`) is pushed to the server with the room reservation and keys the result end to end; a backend recovers the rendezvous key by taking everything before the last `#`. A server re-pushing its reservations after a reconnect keeps the same instance id, so its own live match is never mistaken for a conflicting one.

### Fixes

- **A live room's reservation could be silently dropped by the dedicated server's release gate.** Two defects sat on the same gate: the server released a room's reservation only on the empty state while the lobby also reclaims on disposing — leaving a window where a reservation pushed for the next match was wiped — and a transient failed room read was reported as if the room were empty, releasing a live room's entry (and with it, the key its result would need). The gate now releases exactly once on a live-to-terminal transition, and a failed read holds the last known state instead of masquerading as empty.
- **The demo stage selector no longer trusts its input's trailing character blindly.** The dev lobby derives a stage — which also drives the bot count — from the match id's trailing digit; the parse now accepts ASCII digits only (a fullwidth digit would have produced an unbounded bot count) and clamps to the stages the demo actually ships.

### API additions

- **`IMatchResultProvider` is new** — an optional companion to `IMatchEndEvent` exposing the game-authored result blob (`MatchResultData`, a `byte[]`; null = no result) that the server authority reads at match end. Existing match-end events are unaffected.
- **`RoomManagerConfig.OnRoomCreated` / `OnRoomDraining` are new** — per-room lifecycle hooks a host can subscribe: one fires after a room is fully constructed and before it goes active (the seam for attaching per-room engine and network-service subscriptions), the other fires exactly once on the room's own update thread when it starts draining. Both are optional and default to no-op.

### Samples

- **Brawler carries a worked result payload end to end.** Match end assembles a per-player result — placement (deterministically ranked), remaining stocks, accumulated knockback, and acquired skills/consumables, keyed by player id — onto its game-over event, and both the single-room and multi-room dedicated servers report it to the dev lobby along with abort and abandoned-match notifications.
- **The reference lobby wire grew the result messages and the reservation push now carries the match instance id**, so the sample lobby and dedicated server must run the same version.

> Guides: [Lobby integration §4-F](Docs/LobbyIntegrationGuide.md#4-f-match-result-reporting-sd-multi-room) (the report channel end to end) · [Game-developer API §3.7](Docs/GameDevAPI.md#37-match-result--imatchresultprovider) (authoring and reading the result blob).

## [0.5.5] - 2026-07-11

### Navigation — ORCA avoidance fixes

- **Fixed the ORCA half-plane missing the agent's own velocity offset.** `ComputeAgentOrcaLine` anchored each half-plane at `u * 0.5` instead of `agentVelocity + u * 0.5`, so every constraint was shifted toward the origin and the linear program could accept velocities that don't actually satisfy reciprocal avoidance with the neighbor. Coincident agents (near-zero relative position) now get a small, deterministic separation nudge instead of an undefined direction.
- **`MAX_NEIGHBORS` is now enforced when building ORCA lines.** The constant existed but nothing bounded the neighbor loop, so on a crowded scene the ORCA lines came from whichever in-range neighbors were iterated first rather than the closest ones. A bounded closest-N selection now feeds the ORCA line pass.
- **The velocity solver now recovers from an infeasible constraint set instead of giving up.** When the ORCA half-planes can't all be satisfied at once — a deeply overlapping or heavily crowded cluster — the 2D linear program had no fallback and could leave an agent without a proper separating velocity. A three-dimensional program now takes over that case, relaxing to the velocity that minimizes the largest constraint violation, so a crowded agent keeps a sane avoidance velocity and overlapping agents still push apart.

> The half-plane velocity-offset fix and `MAX_NEIGHBORS` enforcement were contributed by xhocquet.

### Navigation — settling a crowd at its destination

- **Agents that stop on top of each other now spread into a non-overlapping cluster.** Velocity-space avoidance only steers agents that are still moving, so a group converging on the same destination — or otherwise stopped in a pile — could stay overlapped indefinitely. A position-space correction pass now runs after avoidance each step, nudging any remaining overlaps apart over a few deterministic iterations, including agents that have already arrived and stopped. It adjusts position only, never velocity or desired velocity, and runs only when avoidance is configured, so a game that doesn't use avoidance is bit-identical to before.

## [0.5.4] - 2026-07-08

### Multiple stages per dedicated server

- **One dedicated server can now run its rooms on different stages.** Each match's stage is chosen by the authority — the dedicated server, or the lobby when one is wired, or the host in peer-to-peer — and travels to every peer on the same config channel that already carries the random seed and session settings, so a joiner builds the exact stage the authority picked without any client-side lookup. A game selects its stage assets (geometry, navmesh, layout) from the received `StageId`. `StageId` 0 is the default single stage, so a single-stage game is unaffected.
- **Per-room match config is resolved through a new `IMatchConfigSource` seam.** At room-creation time the server's source returns that room's stage plus an opaque per-match payload, or declines the room — in which case creation is refused and the client is turned away with room-not-found rather than a room being created blind. A lobbyless `StaticMatchConfigSource` maps room ids to stages from a fixed table; a lobby-backed source is filled from reservations (below). With no source wired, every room is created open exactly as before.
- **A match can carry opaque, game-defined dynamic config.** Alongside the stage selector, `MatchConfigData` (a raw `byte[]`) lets the authority set per-match knobs — game mode, rules, difficulty, bot count — which the game decodes and applies deterministically while seeding the world. The engine carries only the bytes; their shape and meaning belong to the game. Empty by default, so it stays a no-op until a game uses it.

### Lobby room reservation

- **The reference dedicated-server lobby now reserves a room's match config before the player connects.** When the lobby assigns a room it pushes that room's stage and match config to the server and waits for the server to confirm before handing the player a ticket, so a player only ever receives an endpoint for a room the server has already set up. A room the lobby never reserved is refused, which stops an unauthenticated peer from claiming a room ahead of the players the lobby placed there. Only the assigned server can confirm its own reservation; a reconnecting server has its active reservations re-pushed; and a ticket is minted only once the reservation commits, so a reservation that rolls back never leaves a usable ticket behind.

### Replays

- **A replay now records the match's stage and dynamic config**, so a recorded multi-stage match plays back on the stage it was actually played on. Replay files written by earlier versions may not load and should be re-recorded.

### API additions

- **`IMatchConfigSource`, `MatchConfigContext`, and `StaticMatchConfigSource` are new** for resolving a room's stage and match config at creation time.
- **`RoomManagerConfigBuilder` gained a match-aware callbacks constructor, `WithMatchConfigSource`, and per-match `WithSimulationConfig` / `WithSessionConfig` / `WithCallbacks` overloads**, so a server can build each room from its resolved match config. The existing plain constructor and the value/factory overloads are unchanged.

### Breaking changes

- **`ISimulationConfig` gained `StageId` and `MatchConfigData` (both get-only).** A custom implementation must add them; the built-in `SimulationConfig` and the Unity and Godot config assets already carry them, so only hand-rolled implementers are affected.

### Samples

- **Brawler demonstrates multiple stages across both dedicated-server and peer-to-peer.** Each stage's baked colliders and navmesh drive the deterministic simulation while an additive scene supplies its visuals; the dedicated server assigns a stage per room, the host chooses one in peer-to-peer, and the client builds whichever stage it receives. Static-geometry fingerprints confirm the stages differ and every peer in a match agrees. Brawler also carries a per-match bot count over the dynamic-config channel as a worked example.
- **The Godot dedicated-server and peer-to-peer samples parameterize their stage as well**, with runtime fingerprint and hash checks confirming per-stage divergence and cross-peer agreement.
- **The dev lobby derives a demo stage and bot count per match** from the requested match id, exercising both the stage selector and the dynamic-config channel end to end.

## [0.5.3] - 2026-07-05

### Sample identity — dependency-free Ed25519

- **The reference identity backend now signs and verifies logins with a dependency-free, pure-C# Ed25519 instead of BouncyCastle.** The samples use Atropos — a Zero-GC RFC 8032 implementation — so the shared identity reference no longer bundles a ~4.6 MB crypto library: the .NET and Godot samples pull it from NuGet (`xpTURN.Atropos`) and the Unity samples reference it as a UPM git package. Nothing changes on the wire — Atropos is byte-identical to any conformant implementation, so tickets minted before the switch still verify and players see no difference.
- **Verifying a login ticket now allocates nothing.** The check a dedicated server runs on every join is Zero-GC after a one-time per-thread warm-up, where it previously allocated several kilobytes per verification; signing a ticket allocates only the returned signature. BouncyCastle remains only as an independent cross-check inside the identity tests, off every runtime path.

## [0.5.2] - 2026-07-05

### Logging — configurable timestamp format

- **The log timestamp format is now yours to set instead of being fixed.** A single `SetTimestampFormat` on the builder applies one format to every sink added afterwards (console and rolling file alike), and each sink can still override it — `AddConsole(format)` or the rolling-file options' `TimestampFormat`. The Unity (`KlothoLogger.CreateDefault`) and Godot (`GodotKlothoLogger.CreateDefault`) entry points each gained a matching `timestampFormat` parameter. Optional: the default is unchanged (`yyyy-MM-dd HH:mm:ss.fff`), so nothing changes unless a format is supplied.
- **A malformed format can no longer make logging throw.** A custom format is validated once when the sink is built and quietly falls back to the default if it is invalid, so a bad config degrades the timestamp rather than tearing down every write. The rolling-file sink also spills to a heap string for a format longer than its stack buffer, so an unusually long timestamp is never silently truncated.

### Navigation — deterministic corridor fix

- **Fixed a long path being truncated from the wrong end.** When a computed corridor exceeded the fixed capacity (`MAX_CORRIDOR`), `FPNavMeshPathfinder` kept the segment nearest the destination and dropped the part nearest the agent — so the corridor never contained the agent's current triangle, and corridor-advance tracking in `FPNavAgentSystem` lost the agent on any sufficiently long path. Truncation is now anchored at the start, keeping the triangles the agent actually walks through first. A length bound on the reconstruction guards against a malformed (cyclic) predecessor chain.

### Samples

- **A baked NavMesh sample was added to Brawler** — the `Field` scene plus its exported deterministic NavMesh data, so the navigation path can be exercised end to end.
- **The samples now log with a compact `HH:mm:ss.fff` timestamp** — the Brawler client and dedicated server, the Godot peer-to-peer and dedicated-server samples, and the Godot dedicated-server host all drop the date (it already lives in the log filename) while keeping the wall clock, using the new format hook.

## [0.5.1] - 2026-07-03

### Trusted player data (entitlements)

- **A player can now carry server-authoritative "entitlement" data into a match.** This is an opaque, lobby-signed blob attached when the login is verified — distinct from the client-authored player config, which a client can freely set. The engine carries it through the join and hands it to the game, which reads it to seed the simulation deterministically. Optional: with no entitlement path wired, nothing changes.
- **Start-of-match loadouts are now checked against what a player actually owns.** A game can supply a guard that cross-checks a client's selection against its entitlement and either passes it, clamps it to an owned default, or rejects it. On a dedicated server the decision is authoritative and broadcast once; in peer-to-peer every peer applies the same deterministic clamp — so an unowned pick can neither desync the match nor grant an unearned advantage.
- **In-match actions can be gated the same way.** A game can supply a gate that drops a client-submitted reliable command whose effect the player isn't entitled to — the action simply doesn't happen. This is aimed at ownership that lives off the wire (e.g. a server-only catalog) rather than in the replicated simulation.

> Guides: [Entitlement lifecycle](Docs/EntitlementLifecycle.md) (how the data flows origin → dispose) · [Lobby integration §9](Docs/LobbyIntegrationGuide.md) (which hooks to wire, plus an end-to-end play test).

### Stronger peer-to-peer trust

- **Each peer-to-peer guest can now re-verify a peer's login itself instead of trusting the host's word.** The host propagates the original lobby-signed ticket and every guest independently checks its signature, so no single peer is the sole authority on who everyone is. When not enabled, the previous host-relayed identity is used unchanged.

### Late join carries entitlements

- **A player joining mid-match now gets the same trusted-data treatment as players present from the start.** A new per-join world-seeding hook lets the game initialize that player's simulation state (e.g. an entitlement-derived loadout) deterministically at the exact tick they enter, on every peer, the same way tick-0 players are seeded. Loadout and action enforcement apply to late joiners too.

### Networking

- **Join ordering hardened for late join** — a join is now sequenced ahead of the same-tick gameplay commands, so the joining player's slot and world setup exist before anything else on that tick runs.
- **A reject reason coming back from a game's identity validator is now clamped to the identity range** — an out-of-range code could otherwise be read as a retryable "room full" and send the client into a retry-loop with a credential the authority had already rejected.
- **Fixed a peer-to-peer late-join desync when joining an in-progress match.** The joiner was not sent the confirmed inputs for the exact tick its state snapshot was taken at, so it re-simulated that tick with empty input while everyone else used the real input — its physics (positions/velocities) diverged within a single step, tripping the first post-join sync check and stalling the joiner out of the match. Most visible when joining during active movement/combat. The join now backfills inputs starting at the snapshot tick, matching the reconnect and desync-resync paths.

### API additions

- **Identity validation can return an entitlement blob alongside the verified account** via a new `Accept` overload; the existing one is unchanged.
- **The engine exposes each player's entitlement (`GetPlayerEntitlement`)** for use while seeding the world.
- **Peer-to-peer setup gained an opt-in to wire the loadout guard and enable ticket propagation.**

### Breaking changes

- **`ISimulationCallbacks` gained `OnPlayerJoinedWorld`.** Every implementer must add it; leave it empty when there is no per-join state to seed.
- **`IServerDrivenNetworkService` gained `GetPlayerEntitlement`.** Custom implementers must add it.
- **`IKlothoEngine` gained `GetPlayerEntitlement`.** Consumers only *calling* the engine are unaffected; a custom or mock engine implementation must add it.
- **Custom transports must deliver reliable-ordered messages on a single per-peer stream across both send and broadcast.** Late join relies on a roster update reaching a peer before the join that consumes it. The built-in transport already satisfies this; a custom `INetworkTransport` that routes broadcasts through a separate channel must be corrected.
- **A verified account longer than 62 UTF-8 bytes is now rejected at join instead of being silently truncated.** The account is an identity key, and truncation could collide two distinct accounts into one roster identity. A custom `IPlayerIdentityValidator` must ensure the account it accepts is at most 62 UTF-8 bytes; otherwise those users — previously admitted with a truncated account — are now turned away with the identity-invalid reason. Display names are unaffected (still truncated, as they are cosmetic). The reference validator already bounds account length, so it is unaffected.

### Samples

- **The Brawler sample now wires lobby entitlements** (config-driven on/off, mode-aware), demonstrating loadout seeding and in-match action gating end to end. The dedicated-server demo's guest permissions were aligned with the peer-to-peer rules.
- **Fixed the dedicated-server sample handing remote clients a loopback address** — the lobby now advertises the server's real address instead of the local one.

## [0.5.0] - 2026-06-28

### Verified player identity from the lobby

- **Players can now bring a verified identity into a match.** When a lobby issues a login ticket, the engine carries it through the join handshake and asks the game to verify it — an online check on dedicated servers, or an offline signature check in peer-to-peer — then shares the confirmed account and display name with everyone in the room. This is optional: with no lobby configured, sessions behave exactly as before.
- **A join with a bad login is turned away cleanly.** An expired, invalid, already-used, or banned login is rejected with a specific reason and without taking up a player slot, so the game can ask the user to sign in again instead of silently retrying.
- **Reconnecting players keep their identity.** A returning player's account and name are restored from the session rather than re-checked, because a one-time login ticket cannot be presented twice.
- **For LAN or lobby-less play, a client may suggest its own display name.** This unverified nickname is used only when no verifier is configured; whenever a lobby verifier is present it is ignored, so it cannot be used to impersonate someone.

### Consistent names and live presence

- **Every player now sees the same display name for each participant.** The host (peer-to-peer) or server (dedicated) is the single source of names and shares them through the room roster, so a player is no longer shown a different — or blank — name depending on who is looking.
- **Spectators and clients now see connection changes as they happen** — a player dropping, disconnecting, or reconnecting is reflected live rather than only at the next full sync.
- **A player's own ready state now updates immediately** on their own screen instead of waiting for a round-trip.

### Dedicated-server slot hygiene

- **A peer that connects but never joins no longer holds a slot forever.** On a dedicated server a peer could finish the transport connection (and, in a multi-room server, be routed to a room) yet never send its first message — to join, reconnect, or spectate — and the server would keep its slot reserved indefinitely, slowly starving the room of capacity. Such a peer is now disconnected after a short grace period (10s) if it stays silent, freeing the slot; a peer that joins, reconnects, or spectates within that window is unaffected.

### Serialization

- **`FixedString32`/`FixedString64` are now serialized directly.** These fixed-size text fields can be used in game-defined messages and configs without writing serialization code by hand.
- **`List<T>` and `T[]` of `[KlothoSerializableStruct]` elements are now serialized directly.** Lists and arrays of serializable structs are handled automatically wherever they appear, with no hand-written serialization code.

### Breaking changes

- **The player name field was renamed and an account field was added.** `IPlayerInfo.PlayerName` is now `IPlayerInfo.DisplayName`, and a new `IPlayerInfo.Account` (a stable login id, empty when no lobby is used) was added. Game code that read `PlayerName` must switch to `DisplayName`.
- **The join and roster network format changed, so clients and servers must run the same version.** How the player roster travels over the wire was unified into one compact per-player record; there is no compatibility with older builds on the wire.
- **A dedicated-server identity validator now receives the routed room.** `IdentityValidationRequest` gained a `RoomId` field and a matching constructor argument, so a server-driven validator can bind a ticket to the specific room the player was routed to (the lobby rejects a player who routes to a room their ticket is not bound to). Game code that *implements* a validator only reads the new field — no change required; only code that *constructs* `IdentityValidationRequest` (typically a custom validator's own unit tests) must pass the new argument.

### Dependencies — bundled `Unsafe.dll` and LZ4 removed

- **The package no longer ships `System.Runtime.CompilerServices.Unsafe.dll`** — Unity projects that also obtain this assembly from elsewhere (notably the Unity AI Assistant package, or Burst in IL2CPP player builds) could hit a compile error (`CS0103: 'Unsafe' does not exist…`) or an IL2CPP link failure caused by two copies of the same assembly identity. The handful of low-level helpers the core actually used (`SizeOf`/`As`/`AsRef`) are now implemented inside the package, so the conflicting bundled assembly is gone and the clash can no longer occur. No public API or runtime/determinism behavior change for consumers.
- **Replays are now written uncompressed** — the bundled LZ4 compressor (`K4os.Compression.LZ4`) was removed together with its transitive `Unsafe` dependency. New replay files are somewhat larger but pull in no third-party dependency. **Replay files saved by earlier versions (LZ4-compressed) can no longer be loaded**.

## [0.3.5] - 2026-06-23

### Pre-game lobby consistency

- **Players now see each other the moment they join or leave the lobby** — until a match started, the room only became consistent once the game began: a player who had just joined saw only itself, players already waiting were never told someone new had arrived, and a player leaving the lobby went unnoticed by the rest. Each peer now receives the full roster as it joins and is notified of every later join and leave, so the lobby reflects who is actually present — together with their ready state and names — well before the game starts. Applies to both peer-to-peer and server-driven sessions.
- **A room no longer gets stuck when an unready player leaves** — if everyone else had readied up and the last unready player left, the room could end up all-ready with nothing left to trigger the start, so the only way out was for everyone to leave and try again. A departure from the lobby now re-checks the start condition, so the match begins as soon as the remaining players are all ready. The check is guarded so a player leaving mid-countdown can neither restart nor cancel a start already under way.

## [0.3.4] - 2026-06-22

### Hierarchical FSM — framework upgrade

- **Safer graphs by construction** — the builder now validates the whole state hierarchy up front (with runtime depth guards as a backstop), so a malformed graph fails at build time instead of misbehaving later.
- **Transition inheritance** — a parent state's transitions now apply to all of its children, evaluated leaf→root so a child can preempt its parent; per-state OnUpdate runs across the whole active chain. Shared "escape" transitions can live once on a parent instead of being repeated on every child.
- **Configurable decision/action values (`AIParam`)** — decisions and actions read their thresholds through a small value-source layer, so the same logic can be fed a constant or a per-entity/runtime value (e.g. bot difficulty) without rewriting it. Allocation-free. Build-time checks back it up: every referenced value must be wired to a source (a forgotten wiring fails at init rather than resolving to a silent zero at runtime), and the sources — including custom ones — are scanned for determinism, so one that would desync is caught at build rather than mid-match.
- **Multiple HFSMs per entity (Compound Agent)** — a single entity can now run several independent HFSM axes at once (e.g. movement vs. combat) on the shared manager, with guidance on how the axes arbitrate shared resources. The required host-component layout is verified at build time, so a misplacement that would corrupt FSM state can't ship.
- **Lifecycle & cleanup** — context-injected init + explicit teardown, idempotent registration, named transition priorities for the Brawler bot, and read-only queries that return a safe "no FSM" answer for an entity that carries none instead of throwing. Re-triggering an already-pending event is idempotent.

### Session driver — selectable dt source

- **dt source is now selectable per driver** — the session driver measured its per-frame dt from an engine-independent real-time clock. It can now also drive the session from the engine's own frame delta (Unity `Time.deltaTime` / Godot `_Process` delta), chosen via a single option. The default keeps the existing real-time behavior, so nothing changes unless the engine-frame mode is opted into; the explicit-tick path for headless/deterministic loops is unaffected. Applied identically to the Unity and Godot drivers.

### Serialization — reusable inline struct codec

- **`[KlothoSerializableStruct]`** — bundle a group of fields into a reusable `unmanaged` struct that serializes inline wherever it appears, including inside components. Define a common field group once (e.g. an embedded FSM state) and let the generator emit its codec/hash automatically. Round-trip, hash, and nesting tests added.

### Runtime — per-tick allocation cleanup

- **Trimmed garbage-collector pressure on the hot path** — several spots that quietly allocated a small object on every simulation tick (and again on every rollback re-simulation) now reuse a cached one instead. The biggest were the physics step's trigger callbacks and the server-driven client's per-tick input-resend copies; a per-tick input-cleanup step and a handful of low-frequency network lookups and event messages were tidied up the same way. No behavioral or on-the-wire change — purely fewer short-lived allocations, which means steadier frame times with fewer GC-induced spikes.

### Memory — singleton component storage

- **Singleton components no longer reserve room for the whole entity budget** — a component marked as a singleton only ever has one instance in a match (e.g. the shared random seed or the match-end state), yet its storage was sized for as many copies as the maximum entity count, just like an ordinary component — nearly all of it permanently unused. Singletons now reserve a single value slot while keeping the full-size entity lookup table, so any entity can still carry one. The saving is multiplied across every rollback snapshot the engine keeps. The on-the-wire format and the per-tick state hash are unchanged, so determinism, full-state sync, and replays are unaffected.

### Unity 2022.3 LTS compatibility

- **AI Navigation dependency pinned to 1.1.7** — the 2.0 line requires Unity 2023.1+, breaking the package's declared 2022.3 minimum; 1.1.7 is the latest 1.x that supports 2022.3 LTS.
- **Unity 2022 validation sample** — a `Samples/Unity2022` project on 2022.3.62f3 to verify the package imports and compiles on 2022 LTS.

### Packaging — assembly isolation

- **Bundled Unsafe.dll renamed to avoid a duplicate-assembly clash** — Unity dedups precompiled assemblies by file name, so another package shipping its own `System.Runtime.CompilerServices.Unsafe.dll` (e.g. Unity AI Assistant) could win and hide the Unsafe type, causing CS0103. The bundled DLL is renamed to `xpTURN.Klotho.Unsafe.dll` with Auto Reference off, referenced explicitly by Runtime and LiteNetLib.

### Inspector

- **`[Header]` attribute target fixed** — `[Header]` on `[field: SerializeField]` auto-properties is now `[field: Header]` so it binds to the backing field, restoring inspector header grouping.

## [0.3.3] - 2026-06-18

### Synchronization diagnostics & robustness

- **Mesh geometry folded into the static-geometry fingerprint** — the diagnostic fingerprint of the loaded static colliders hashed only each mesh collider's placement (position/rotation), not its actual shape. Two peers that loaded the same scene but ended up with differing mesh vertices or triangle winding produced an identical fingerprint, so a geometry-only divergence stayed invisible. The mesh's vertex and index content is now folded in (computed once when the mesh is set, allocation-free), so such a divergence surfaces at its source. The fingerprint is a log-only diagnostic outside the per-tick state hash and is not serialized — determinism, consensus, and the wire format are unaffected.
- **Server-driven reconnect no longer cut short by the full-state abort timer** — on a server-driven client, a pending full-state request runs a short abort countdown (~15s) as its own safety net. If the server dropped while that request was outstanding, the client also began its much longer reconnect attempt (~60s) — but the short abort fired first and ended the match, throwing away a reconnect that could still have recovered. The abort countdown is now suspended for the duration of a reconnect: a successful reconnect's incoming full-state resolves the request naturally, and only if the reconnect itself ultimately fails does the match terminate. Ordinary reconnect failures with no full-state request outstanding are still left to the game layer, unchanged.

## [0.3.2] - 2026-06-18

### Reliable command channel

- **One-shot commands get a dedicated reliable channel (server-driven)** — actions that must land exactly once yet aren't latency-sensitive (character spawn, and similar buy/surrender-type commands) used to compete for the same per-tick input slot as movement and leaned on a retry / collision-avoidance workaround to get through. In server-driven mode they now travel a separate channel: the client submits without picking a tick and the server assigns one authoritative execution tick, so the command applies exactly once, in a stable order, on every peer — never predicted or rolled back. A per-player sequence guard makes a resend harmless. Peer-to-peer keeps the existing path; spawn is the first command moved over.
- **Owning player decided by the authority on placement** — a reliable command's player is now taken from the server's validated connection mapping rather than the submitted payload. Previously two players' spawns both collapsed onto the same default id, so only one character appeared and the other was dropped as a duplicate; fixing it also cleared a spurious mismatch in the client's command reconciliation.
- **Reliable-retry collision check false positive** — the guard that stops the legacy retry path from stamping over an in-flight command also fired on the command's own last-attempt tick, suppressing the empty filler that keeps the per-tick quorum advancing and risking a stall rather than preventing a collision. The bad clause was removed, so the guard now blocks only a genuine future-tick collision.

## [0.3.1] - 2026-06-17

### Multi-room dedicated server — straggler lifecycle thread safety

- **Straggler state-machine concurrency fixes** — three races in the multi-room server's straggler handling, surfacing only under `MaxRooms >= 2` load. A per-cycle barrier was stored under every ready room id and then waited/disposed twice (`ObjectDisposedException` → process abort) — it is now a single shared instance disposed exactly once. A timeout marked *all* ready rooms as stragglers, including ones that finished in budget (tick stalls, inflated counts that could force-close healthy rooms) — only rooms that didn't complete this cycle are marked now. And teardown could tear down a room whose worker tick was still running — guarded so a straggler is never collected mid-update.
- **`EndRequested` cross-thread publication fence** — the room's end-request timestamp is written by the worker thread (match-end/abort) and read by the main thread (join validation) without a publication fence, so the reader could see a stale/torn value. Split into a volatile bool gate plus its backing fields, written gate-last / read gate-first, so the companion fields are always visible; the public property and callers are unchanged.

### Input command model

- **Unified per-tick input command (`PlayerInputCommand`)** — the keep-first regression meant a move command claimed each `(tick, playerId)` slot, so a same-tick attack or skill was silently dropped as a duplicate. Move, attack, and skill are now folded into one per-tick command (move axis + a button bitmask + aim/skill-slot), so a single tick can carry movement *and* an action. A prediction hook clears one-shot bits (jump/attack/skill) on repeat so only movement repeats during prediction; humans and bots both emit the unified command, and the three legacy command types were removed.

### Build & packaging hygiene

- **Brawler server fixture-copy glob fix** — a pre-existing path glob for the Brawler server's `Data` test fixtures copied to the wrong location; corrected.

## [0.3.0] - 2026-06-16

### Repository layout

- **Unity adapter layer neutralized to a top-level sibling of Godot~** — the Unity-specific adapter was moved out of the engine-neutral core to a top-level folder, equal in standing to the Godot adapter, so no platform code lives inside the neutral core. 141 files moved with full rename tracking; assembly names and references are unchanged, and the repo-layout docs were realigned.

### Synchronization safety-net fixes — 2nd review

#### Desync detection & recovery ladder

- **Desync anchor veto order-dependence + counter masking (D-F1)** — in 3+ player P2P, a desync could roll back to a tick a peer had already flagged as diverged (depending on message order), and a global desync counter let one peer's match reset it so the resync threshold was never reached. The rollback anchor now keeps a 1-step history so a mismatch can demote it, and the desync counter is tracked per-peer so any single peer can still escalate to resync.
- **Pending-rollback / chain-gap hash mis-send** — two paths broadcast a sync-hash for a tick that was about to be rewound (or while the verified chain was still behind), which a remote peer saw as a false desync and an unnecessary rollback. Both now defer the hash send until the tick is actually verified, so the corrected hash is sent exactly once.
- **Recovery-ladder rung 3↔4 time arithmetic** — at the corrective-reset-budget / abort boundary, a stale in-flight failure report could abort the match too eagerly, while a timeout-type persistent failure decayed the budget faster than it accrued so the abort was never reached. The exhausted-budget path now decides by divergence tick (stale pre-reset reports are absorbed), and the decay window is widened to the worst-case report cadence so a persistent failure still reaches abort.
- **Rollback FullState-cache + post-apply SyncHash invalidation** — after a rollback the host could serve a stale cached full-state, and after a resync the network layer kept pre-reset peer hashes that produced false mismatches. The rollback now invalidates the full-state cache and a resync clears the stale per-tick peer hashes. (A minor early-game tick-0 anchor case is deferred.)
- **FullStateResync verified-chain starvation** — after a desync-triggered resync the requesting guest's verified chain could stall forever, because the resync path never re-supplied the input stream. The host now feeds catchup input batches to the resyncing peer and the guest ingests them, so the gap fills and the chain advances.
- **Residual low-risk fixes bundle (D-F10 · R-11 · Timescale)** — three single-site, behavior-neutral residuals from the post-G8 catalog: a full-state jump now clears orphan deferred check-tick entries that could otherwise cause a duplicate hash send or a small leak (D-F10); a late-join no longer completes on a corrective-reset broadcast, which would anchor catchup on the wrong tick (R-11, latent/defensive — the branch is currently unreachable); and the render clock now exposes its real drift-proportional convergence rate instead of a hardcoded 1.0, with no live consumer so behavior is unchanged (Timescale).

#### Server-Driven (SD) client

- **SD resync-path robustness** — three gaps on the Server-Driven client's full-state resync path, the SD counterpart of the P2P hardening. A lost resync response could leave the request flag stuck forever (a verified freeze) — the SD path now has its own timeout/retry that eventually fails the match locally. The two apply sites now honor the apply result (retry on a guarded skip, terminate on an unrecoverable hash mismatch). A currently-unreachable branch that didn't clear the flag is hardened as a guard against a future in-place-reconnect lifecycle.
- **SD input stamping/acceptance alignment (T-F4 · T-F7)** — on the SD client the local buffer (keep-first) and the server (last-write-wins) could keep different commands for the same tick, eating buttons and desyncing: an auto-injected empty could land on a real input's slot (T-F4), and a delay decrease could collapse several real inputs onto one tick (T-F7). The client now sends to the server only the command the local buffer actually kept, so both sides keep-first regardless of arrival order, with a server-side backstop that refuses to overwrite a stored real input with an empty.
- **SD fault-injection in-session reconnect** — the diagnostic disconnect→reconnect scenario did nothing on the SD client: the reconnect arm was wired only for the P2P service type, and the SD reconnect-completion path never restored the Playing phase. Both are fixed (an SD-specific arm + phase restore), unblocking SD measurement of the resync-robustness scenarios. Diagnostics-only; the normal reconnect path is unchanged.

#### Event system (pool / dispatch / exactly-once)

- **Shared EventPool engine-scoped ClearAll + spectator rollback diff truncation (E-3 · E-4)** — two single-site event defects. E-3: a full-state apply called a process-global pool clear, which in a multi-engine process (editor host+client, or a multi-room server) wiped other engines' pool tracking and stock — the redundant global clear is removed, since this engine's own buffer clear already covers it. E-4: a spectator rollback fired spurious event cancellations for its truncated prediction tail (a cancel→re-predict flicker) — a one-line guard skips that tail. Spectator-view only; P2P/SD are unaffected.
- **Match-end divergence policy: fire-forward backstop + state-based pause-grace gate (E-1-residual · E-7 · E-8)** — adopts a one-way "match-end fires forward, never un-fires" contract behind a new simulation state query. After a full-state restore the engine re-fires the match-end event once if the restored state is ended but it was never dispatched (recovering a match-end lost to a deep stall or a forward jump), and the pause-grace stop-injection gate is now state-based so a rollback that un-ends the match releases the gate without un-firing the latch.
- **Event pool Return leaks — gap-tick / replay-seek / SD-server-drop (E-5a · E-5b)** — three places raised events without returning them to the pool (a leak: pool-tracking growth in dev builds, per-event allocation in release). The SD gap-tick fill and the replay-seek re-simulation now open and drain the event collector and return the events, and the SD server's collector returns the regular events it drops. Events are output-only, so determinism is unaffected; an unused legacy collector was deleted.
- **Replay backward-seek watermark + dev-guard (E-6)** — after seeking a replay backward, synced events went silent (the dispatch watermark wasn't rewound) and a long backward seek spammed a dev-guard error. The seek now lowers the watermark and resets the event buffer (mirroring the spectator reset), so rewound playback re-dispatches correctly and the false errors stop.
- **Ring-wrap Synced dispatch guard (E-1)** — during a very deep P2P prediction stall the event ring could wrap, so a tick's synced events fired at the wrong tick and again at the real one (an exactly-once over-fire). Dispatch now skips any event whose stamped tick doesn't match the dispatch tick, so wrapped events fire exactly once at their real tick; normal play is unchanged. (The deeper under-fire loss inherent to the fixed-size ring is a known limit, covered by other backstops.)

#### Reconnect / late-join / disconnect

- **Reconnect/late-join catchup prediction reconcile** — a reconnecting or late-joining peer kept predicting an absent player's input, but the real input arriving via the catchup path was added without reconciling the misprediction, so the frozen state got promoted to verified and desynced. The catchup path now runs the same prediction-mismatch reconcile (rollback) as the normal receive path, with a resync fallback when the needed rollback is older than the snapshot window. (Smoke: zero desync after reconnect.)
- **Reconnect real-over-unsealed-empty overwrite (host → all P2P peers)** — a reconnecting player's real input was dropped when it landed on an unsealed empty placeholder the host had proactively filled, leaving that peer stuck on empty (a permanent divergence). A real input now overwrites an unsealed empty (with a rollback) on every peer, not just the host; a committed (sealed) slot still blocks it. (Smoke: gated drops fell to 0 on all clients.)
- **Cold-start reconnect late-join degradation** — in the diagnostic harness a returning guest could be misrouted as a late-join (saturating the roster and rejecting the next guest) because a stale prior network service sent a join message first. The harness now arms the in-session reconnect path directly so the returner sends a reconnect request first, matching production. Production is unaffected; the racy "live session on top of cold-start" combination is documented unsupported.
- **Graceful Reconnect on null credentials** — a reconnect attempt with no persisted credentials threw a null-reference instead of failing cleanly; it now reports a normal reconnect failure. Always-compiled hardening.
- **Late-join entity-id divergence** — a late-joining guest spawned its own character with a different entity id than its peers (an off-by-one that desynced every sync window), because the slot-creation gate keyed off the non-deterministic engine roster. It now keys off deterministic simulation state, so every node creates the slot exactly once. A state-determinism fix, unrelated to timing.
- **Late-join/reconnect guest timesync activation** — a late-join/reconnect guest never enabled its own frame-advantage throttle (only the normal start path did), so it never self-throttled — a behavior asymmetry purely by join path. Timesync is now enabled at the late-join/reconnect seed point. Local pacing only; no effect on determinism.
- **Quorum-miss presumed-drop watchdog hardening** — the watchdog could fire on a present, alive player and seal its slot with empty, amplifying a desync into a storm. It no longer self-releases on its own echoed fill (network arrivals only), the release condition is relaxed so a recovering peer isn't latched until the wall-clock timeout, and two false-positive thresholds are tightened. (Re-smoke: storm gone, single-resync convergence.)
- **Disconnected-peer empty-prediction** — during a disconnect window the guest predicted the departed player as repeat-last while the host filled empty, mismatching every tick and forcing a rollback each tick (visible stutter). A confirmed-disconnected peer is now predicted as empty, matching the host's fill, so no rollback fires. Speculative only; the verified chain and state hash are unchanged.
- **Guest-side disconnect awareness propagation** — in 3+ player P2P a guest had no way to learn another guest's drop, so it couldn't exclude that peer from the timing vote. The host now broadcasts roster transitions (disconnect/reconnect/leave) and guests mirror them, replacing the accidental reliance on a proxy-fill side effect. (Phase 1 of the frame-advantage exchange work.)
- **Quieter ChainBreak log during a disconnect window** — the per-tick chain-break warning spammed the log throughout a disconnect window; when every missing player is a confirmed-disconnected peer it now logs at debug instead of warning. Logging-only; no behavior change.

#### Timing / throttle / frame-advantage

- **Extra-delay lifecycle redesign — additive split + reactive server report** — reworks the adaptive input-delay so it stops ratcheting and racing. The delay is split into a server-authoritative baseline plus a client-reactive component; a server push now drains the reactive part instead of stacking on it, the reactive part de-escalates when conditions are stable, and the server/host absorbs a new client report (taking the max across peers in P2P). A clamp invariant is enforced and observability was added.
- **Frame-advantage real exchange + proactive disconnected-peer fill** — closes the timing-data-contamination series and removes the verified-chain trail during a disconnect. Each peer now piggybacks its own measured frame-advantage on the wire so the throttle recovers GGPO's one-way-delay cancellation (instead of a self-mirror), proxy-fills no longer pollute the timing vote, and the host fills a disconnected player's slots up to the input frontier so the chain stops trailing. **Wire format change — all clients must run the same build.**
- **P2P frame-advantage throttle revival (2-player) + companion fixes** — revives the voluntary slow-down throttle (a self-echo had collapsed the advantage estimate so a fast peer never slowed) and ships the two fixes it needs: the throttle no longer permanently latches (a wait is now a one-shot budget that always releases), and the advantage tick stays truthful across a full-state restore so a reconnecting guest isn't throttled to the cap.

### Synchronization safety-net fixes

- **P2P desync detection revived** — the local sync-hash was never stored, so hash comparison and the entire recovery ladder were dormant. The local hash is now stored and compared bidirectionally, with event-based anchor promotion.
- **Event ring trim removed** — a periodic trim wrapped the event ring and destroyed live events a few ticks old (corrupting rollback diffs and losing pending synced events). The ring now self-cleans at execution time, with dev guards added.
- **SD Hard-Tolerance deprecated** — the rejection deadline was structurally unreachable; the dead machinery was removed and the related config/enum kept as no-ops for wire compatibility.
- **Rollback/snapshot alignment** — fixed a retention off-by-one that made deep-rollback clamping dead, unified the snapshot-ring capacity, and clamped the SD prediction lead so it no longer overruns the ring.
- **FullState resync path hardening** — a retreat-guarded skip no longer masquerades as a successful apply (it re-arms the retry instead of wiping input history), post-apply hash mismatches now feed the escalation counter, duplicate desync reports for one tick count once, and the sync-check interval is clamped to stay within the rollback window.
- **Recovery ladder rungs 3–4 wired** — the ladder previously ended at rung 2 in live code. A guest→host failure report now drives a host-side corrective-reset budget, and exhausting it broadcasts a match-abort (opt-out available).
- **Synced-event divergence made observable** — a recovery rollback below the dispatch watermark could silently change already-fired synced history. A new divergence callback reports added/removed synced events (the exactly-once invariant is kept — nothing re-fires), with a match-end exception so the match can still end.
- **Timing/prediction minor fixes** — a batch of smaller fixes: the reject-tracking window now initializes correctly and shares the escalation cooldown; bounded back-scans; duplicate arrivals keep-first without a double pool-return; rollback resim runs in the resimulate stage; quorum checks by membership rather than count; and a failed self-test restores forward state and auto-disables after repeated failures.
- **Dead code & doc realignment** — removed an unused render-clock/EMA pair, fixed and exposed prediction-accuracy accounting, pooled the SD verified-batch list containers, and realigned the design and spec docs to the implementation.
- **BREAKING** — removed the never-fed engine-side snapshot store; simulations now own their rollback history and the engine queries them for the nearest restorable tick.
- **BREAKING** — the per-tick snapshot save joins the simulation interface, completing the snapshot-ownership unification, so non-ECS simulations that keep history can roll back on all paths. Implementations may no-op but must only expose ticks they can actually restore.
- **Command ownership contract documented + DEBUG diagnostic** — the single-owner rule for submitted commands was left unstated at the public surfaces and is now spelled out; a debug-only pool diagnostic rejects cross-thread, double, or foreign returns so misuse can't poison the pool.
- **Command lifetime/ownership unified** — one rule replaces the previous mixed scheme: a deserialized command has exactly one owner (the buffer if accepted, else the caller), and rejected arrivals are no longer pool-returned by the buffer. **BREAKING** — re-initializing the engine without stopping it now throws instead of silently double-subscribing.
- **Replay ghost network service removed** — a replay session wrongly attached a live network service to the main transport, polluting the server's RTT smoothing and leaking session messages into the replay engine. Replay sessions now run with no network service, plus a server-side guard that discards stale ping samples.

#### Docs & build hygiene

- **Documentation consistency + residual line re-alignment** — the final doc-consistency cleanup: realigned five design-doc sections to the implementation (clock offset is fixed at handshake; rung-2/3 state source; the host never self-escalates; the resync delta contract; event-ring self-clean), plus two trivial code touches (a comment-source fix and using the recording's tick interval during replay). Six items found to be live code defects rather than doc-only are tracked separately as future candidates.
- **Fault-injection salt field compiled unconditionally (editor/player layout parity)** — a fault-injection-only field compiled only under a build symbol caused an editor↔player serialization-layout mismatch (a player-build failure). The field is now always compiled (default, inert) while its behavior stays guarded, so the layout matches and runtime behavior is unchanged.

## [0.2.14] - 2026-06-10

### Godot physics tooling

- **FPStaticCollider exporter (IMP56)** — export Godot static colliders to `FPStaticCollider` `.bytes` (+ `.json` sidecar); converter and viewer wired as editor menu / FileSystem dock tools.
- **FPPhysics visualizer (IMP57)** — in-editor physics world overlay: debug panel, immediate-mode drawer, and world visualizer. Static collider viewer hardened (load robustness, HUD corner option).

### Samples & docs

- **GodotPolySample** — added test scenes + exported data.
- Docs — `PhysicsWorld.md` (deterministic physics overview), `PhysicsVisualizer.Godot.md`; adapter comments de-Unity'd; Installation/README touch-ups.

## [0.2.13] - 2026-06-09

### Godot NavMesh

- **Steep-slope traversal fix** — a ramp baked as one large triangle could be impassable (its Y-span exceeds the multi-floor threshold). Resolved data-side with no core change: re-bake with finer tessellation (`edge_max_length <= 3`).
- **Visualizer `Floor Y Thr` knob** — apply `MultiFloorYThreshold` live in the editor sim for diagnosis/tuning (editor-only; default 2.0, behavior unchanged unless raised).

### Packaging

- Moved the generated addon dist (`addons/klotho/`) to `dist/addons/klotho/` — it's a build artifact, not canonical source. Pure relocation; pack/deploy scripts updated.

## [0.2.12] - 2026-06-09

### Godot NavMesh tooling

- **NavMesh exporter** — `NavigationMesh` → `FPNavMesh` exporter; emits a `.bytes` payload plus a `.json` sidecar (shared `ToJson`).
- **Editor NavMesh visualizer** — load a NavMesh, run pathfinding, and simulate an agent in-editor.

### Godot Addon Packaging

- Renamed the addon DLL dir `bin/` → `lib/` so the prebuilt core/server DLLs are no longer dropped by default `bin` ignore filters during export/packaging.

## [0.2.11] - 2026-06-08

### Godot adapter parity

- **`GodotFlowSetupBuilderExtensions`** — adds `WithGodotDefaults()`: reads app version from `ProjectSettings` + injects `GodotDeviceIdProvider` via `WithHandshake` in one call; falls back to `"0.0.0"` when no version is set.
- **`KlothoSessionFlow.Logger` / `DeviceIdProvider` made public** — promoted from `internal` so cross-assembly Godot adapters can access them directly without re-passing as explicit parameters.
- **`GodotSessionFlowAsync` fix** — removed the explicit `logger` parameter from all join/reconnect methods; `flow.Logger` and `flow.DeviceIdProvider` are now referenced directly, matching `KlothoSessionFlowAsync` on the Unity side. Fixes `PlayerJoinMessage.DeviceId` always being empty.
- **Deterministic Geometry adapters** — added Godot conversion helpers for `FPRay3`, `FPPlane`, and `FPBounds3`.
  - `FPRay3`: tuple decomposition (`ToRay` / `ToFPRay3`) + `PhysicsRayQueryParameters3D` helper (`ToRayQuery`).
  - `FPPlane`: `ToPlane` / `ToFPPlane` — applies sign inversion (`Godot.Plane.D = −FPPlane.distance`; different equation convention, not a handedness issue).
  - `FPBounds3`: `ToAabb` / `ToFPBounds3` — maps `Godot.Aabb` Position=min-corner / Size=full-size layout.
- **`GodotKlothoLogger`** — adds `CreateDefault()`: `GodotLogSink` + `RollingFileSink` combined, defaulting to `ProjectSettings.GlobalizePath("user://logs")` (required for exported apps where relative paths are not writable).
- **Samples** — `GodotP2pSample` and `GodotSdSample` updated to use `KlothoFlowSetupBuilder` + `WithGodotDefaults()` and `GodotKlothoLogger.CreateDefault()`.

## [0.2.10] - 2026-06-07

### Fix

- `KlothoJsonContextMenu`: fixed `AddContextMenuItem` callback signature — `Callable.From(OnConvert)` wrapped a zero-argument method while Godot passes the selected paths array, causing an `ArgumentException`; changed to `Callable.From<string[]>` with a matching `Action<string[]>` signature.

## [0.2.9] - 2026-06-07

### Godot (.NET) support

- Godot view-layer adapter (`GodotEntityView`, `GodotEntityViewUpdater`, etc.) — opt-in view pooling, self-driven interpolation via `GodotSessionDriver`, `GodotSessionFlowAsync` join helpers, `GodotPlayerViewRegistry` (EVU auto-population), reconnect support, `EngineEventOneShot` port, `ErrorVisualState` desync blending, and Resource-based editor config assets.
- **GodotP2pSample** — standalone Godot 4 P2P sample (top-down 3D, file + console logging, export-safe asset loading).
- **GodotSdSample** — standalone Godot 4 ServerDriven sample; dedicated server moved to a sibling project `GodotSdSampleServer`.
- Klotho Godot addon (`addons/klotho/`) — stable script UIDs, no-op `EditorPlugin` (enable-able), DataAsset JSON→bytes editor tool, and `KlothoServer.dll` bundled for server builds.

### Fix

- Added fallback for `IDataAsset` `$type` mismatch across engine assembly names.
- `PlayerPrefsReconnectCredentialsStore`: switched JSON serializer from `JsonUtility` to Newtonsoft.Json.

### Docs

- Install guide split per engine (Unity / Godot) with server `.csproj` guidance.
- Official docs fully revised to reflect dual-engine support.

## [0.2.8] - 2026-06-03

### Packaging — flat layout (breaking)

- The framework package is promoted to the repository top level (`com.xpturn.klotho/`); the nested dev-project wrapper is gone. Samples now consume the package through a `file:` manifest reference to that single top-level package (no embedded copy).
- UPM install URL path changes to `?path=com.xpturn.klotho`. Dedicated-server csproj references the per-assembly projects under `com.xpturn.klotho/Server~/` directly.
- LICENSE files added.

### Session lifecycle — IKlothoSessionObserver is the only surface (breaking)

- `IKlothoSessionObserver` is now the single session-observation surface. The deprecated per-event `KlothoSession` state events and the per-mode `KlothoSessionFlow.On*SessionCreated` events are removed — session creation is observed through one `OnSessionCreated(session, SessionEntryKind kind)` callback (branch on `kind`).
- `Phase` / `AllPlayersReady` lifted onto the session facade; state transitions surface as `OnStateChanged` / `OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged`.
- Session stop is idempotent through a single `OnSessionStopped` path; the driver's transient teardown guard is internal.

### Flow / role — unified entry (breaking)

- Guest join unified into a strategy-dispatched `JoinAsync(strategy, ...)`; `JoinP2PAsync` / `JoinServerDrivenAsync` are convenience overloads that delegate to it.
- Mode and local role resolved into a single `KlothoRole` (`P2PHost` / `P2PGuest` / `SdClient`).
- Join/reconnect reject reasons typed as enums (`JoinFailReason` / `JoinFailedException`, `ReconnectRejectReason`); guest join gains a `connectTimeoutMs`.
- The driver owns the main transport: it pumps it while idle and routes idle disconnects to `OnIdleDisconnected` (`BindTransport`); connect-attempt cancellation ownership moved into the flow.

### Builders & config

- `KlothoFlowSetupBuilder` for flow setup, with `WithReplaySave(path, dumpJson)` to declare the replay output path at build time.
- `RoomManagerConfigBuilder` for dedicated-server room config; `SimulationFactory` removed (the simulation is derived from the simulation config, e.g. `WithDerivedSimulation`). Per-room logging replaces the standalone simulation logger.
- `IKlothoEngine.InitFrame` added; samples read frames via `Engine.PredictedFrame.Frame` instead of an `EcsSimulation` downcast.

### Fixes

- ServerDriven reconnect was mis-routed as a late-join — the stale connection is now disposed on a failed handshake.
- ServerDriven client replay: the initial-state snapshot is persisted before recording starts.
- Server join-reject reason surfaced to the client via the disconnect payload.

### Docs

- `README.md` and `Docs/**` refreshed end-to-end for the flat layout and the consolidated session/flow API surface.

## [0.2.7] - 2026-05-31

### ECS — HFSMBuilder (IMP49)

- New fluent `HFSMBuilder` (`Runtime/ECS/FSM/`) replaces the manual array-of-structs HFSM graph with a `State/Default/To/OnEnter/OnUpdate/OnExit` chain. `Build()` validates the graph at registration and fails fast on structural defects (duplicate ids, dangling target/parent/defaultChild, default-not-set, dense `States[i].StateId == i`), runs a reachability BFS, and stably sorts each state's transitions by descending priority. Since the runtime evaluates transitions by array order (not the `Priority` field), the stable descending sort is what gives priority its meaning. Advisory findings (unreachable / duplicate priority / self-transition) warn via `IKLogger` by default and throw under strict.
- `BotHFSMRoot` converted to the builder (graph unchanged — transitions were already descending, so the sorted arrays match 1:1), keeping the `Has()` idempotency guard and decision/action instances.
- `HFSMBuilderTests`: synthetic-graph coverage for each structural throw, advisory warn-vs-strict, stable sort, and same-target transitions.

### Analyzer — DeterminismAnalyzer (DET002~004)

- First true `DiagnosticAnalyzer` in the `KlothoGenerator` DLL (existing ones are generator-embedded helpers). Surfaces determinism hazards at build time instead of waiting until replay/rollback desync. Rules (Category `KlothoGenerator.Determinism`, Warning): `KLOTHO_DET002` (float/double in a deterministic context), `KLOTHO_DET003` (non-deterministic API/type — `Mathf`, `Random`, `System.Math`, `DateTime`, float-backed `UnityEngine.Vector2/3/4`/`Quaternion`/`Matrix4x4`), `KLOTHO_DET004` (`UnityEngine.Time` wall-clock).
- Context gating (`CompilationStart` → `SymbolStart(NamedType)` → `OperationAction`): rules 002/003 fire only on types implementing a deterministic interface or inheriting a deterministic base; rule scanning for ref-`Frame` helper types is limited to their ref-`Frame` methods (covers `FPNavAvoidance`, `CombatHelper`, `BotFSMHelper`). The FP64 conversion boundary (`FromFloat`/`FromDouble`/`ToFloat`/`ToDouble`/`ToFP64`) exempts argument float-ness while DET003/004 still scan the argument subtree. Test/tool assemblies are skipped.
- Tests: `Tools/KlothoGenerator.Tests` (NUnit, manual analyzer driver) — 10/10 pass.

### Flow — StartHostAndListen single-entry host bootstrap

- `KlothoSessionFlow.StartHostAndListen` folds the `StartHost` + `HostGame` + `Transport.Listen` sequence into a single call with framework-side teardown, matching the guest path's single-call symmetry. Reads `MaxPlayers` from `sessionConfig` once and listens via `KlothoFlowSetup.Transport`. Listen-bind failure returns `null` (recoverable); other failures (`HostGame`/`CreateRoom`) `Stop()` then rethrow, so a half-started session is never orphaned.
- P2pSample implements `IKlothoSessionObserver` + wires `LifecycleObserver` so `session.Stop()` cascades to game-side teardown via `OnSessionStopped`; teardown converged into `StopGame()` (IsStopping-guarded). Brawler aligned to `StartHostAndListen`, preserving post-success `SendPlayerConfig` / menu transition. `GameDevAPI.md`: documents `StartHostAndListen`; marks `StartHost` as a low-level escape hatch.

### Logging — re-entrancy / shutdown hardening

- `CharBufferWriter`: per-thread reusable buffer switched from an always-shared `[ThreadStatic]` array to a rent/return pool. A re-entrant log on the same thread (e.g. an argument's `ToString` that logs) now finds the buffer already rented and allocates its own, so the outer message is never corrupted; the pool self-heals if a buffer is not returned (exception mid-format). The non-re-entrant path stays allocation-free.
- `RollingFileSink.Write`: drops the line when termination has begun (`_stopping`) instead of reopening a writer that would never be flushed or closed — prevents a file-handle leak and a stray log file when a write races with `Dispose`/`ProcessExit`.

### Samples

- **LoggingMelConsole** — new console sample (`Samples/LoggingMelConsole`) routing Klotho's `IKLogger` surface through a standard `Microsoft.Extensions.Logging` pipeline via `MelKLogger`, logging to both console and rolling files under `Logs/` with a ZLogger provider.
- **P2p/Sd HUD** — `P2pHud`/`SdHud` frame access lifted from the unsafe `((EcsSimulation)engine.Simulation).Frame` downcast to the typed `Engine.PredictedFrame.Frame` accessor, aligning the intro samples with Brawler's standard View pattern. Per-tick polling gains an explicit `Frame == null` guard; one-shot init swaps the cast only (frame presence is a hard precondition). Sim-side init casts preserved (authoritative write paths).
- **SdSample** — tick rate raised to 60Hz (`TickIntervalMs` 33 → 16, synced across server `simulationconfig.json` + client `SimulationConfig.asset`), halving per-tick input-stamp and remote-render latency from ~66ms to ~32ms at unchanged InputDelay/Interp ticks.

## [0.2.6] - 2026-05-30

### Dedicated server — project references

- New per-assembly server projects under `Server~/` that mirror the client asmdef boundaries (`LiteNetLib`, `xpTURN.Klotho.Logging` / `.Runtime` / `.Gameplay` / `.LiteNetLib`, and a `KlothoServer` aggregate holding the server-only helpers), as an alternative to source-sharing via `KlothoServer.Core.props`. Server helpers (`ConfigPathResolver` / `SessionConfigLoader` / `SimulationConfigLoader`) moved into `KlothoServer/`. Existing `.props`-based builds unchanged.
- With Klotho split across referenced assemblies, registration runs through `[ModuleInitializer]` (fires only once an assembly loads). New `KlothoServerBootstrap` loads every Klotho/game assembly from the deploy directory and runs warmups before any factory is built — a directory scan (not a reference walk, since the compiler drops references to assemblies whose types are never used directly). Assumes a multi-file, untrimmed deploy.
- `BrawlerDedicatedServer` migrated to ProjectReferences + `KlothoGenerator` analyzer; `Program.cs` calls `KlothoServerBootstrap.Initialize("Brawler")` at startup. Verified: build clean, `--test` suite passes (79 tests, 0 failures).
- `DeterminismVerification` migrated from ~50 lines of hand-listed source includes to ProjectReferences (Runtime + Logging only — no networking, so no KlothoServer/Gameplay/transport); `Program.cs` calls `WarmupRegistry.RunAll()`. Verified: determinism run passes (3 seeds × 10k ticks + seed-42 self-verify).
- `SdSample` embedded Klotho synced to the same mirror layout; `SdSampleServer` migrated to ProjectReferences with `KlothoServerBootstrap.Initialize("SdSample", "xpTURN.Samples")`. Verified: build clean, server starts and warmup serializes all registered commands/messages with no errors.

### Docs

- Dedicated-server guidance updated from props source-sharing to project references.

## [0.2.5] - 2026-05-30

### Sample — P2pSample

- New minimum P2P sample under `Samples/P2pSample/` — a 2-player sumo match (cube-on-plane, WASD move, push-off-edge respawn, 60s GameOverEvent). Independent Unity 6.3 project sibling of `Klotho/`, consumes `com.xpturn.klotho 0.2.4` via the same UPM git URL that external users would use (`?path=Klotho/Packages/com.xpturn.klotho#v0.2.4`). Game-side LOC ≈ 795 across 15 files (Sim 7 / View 8) — ~66% the size of Brawler with the same deterministic-core + view-bootstrap pattern. Visuals use Unity built-in primitives only (Cube / Plane) — no external assets.
- Documentation: [`Samples/P2pSample/README.md`](Samples/P2pSample/README.md) (4-step quick start) and [`Docs/Samples/P2pSample.md`](Docs/Samples/P2pSample.md) (architecture walkthrough + common pitfalls). README "Optional samples" anchor updated.
- Common pitfalls surfaced while building this sample (now documented in `Docs/Samples/P2pSample.md` §6): Synced events dispatch via `engine.OnSyncedEvent` (not `OnEventConfirmed`); `session.HostGame(...)` is mandatory after `flow.StartHost(...)` to activate host role; `[KlothoOrder(N)]` is required on every DataAsset field (else generator skips serialization → `.bytes` truncated to header); JoinP2PAsync cancellation does not unwind LiteNetLib peer registration, so a re-entry guard is needed; `AllPlayersReadyChanged` is host-only — guest UI must derive from `SessionPhase` instead.

### Sample — SdSample

- New minimum **ServerDriven** sample under `Samples/SdSample/` — the dedicated-server sibling of P2pSample: same 2-player sumo game, but a Unity client + a minimal .NET 8 dedicated server (`Server/`). `Sim` deterministic code is reused from P2pSample (renamespaced); the server `.csproj` source-shares the same `Sim/*.cs` via the package's `Server~/KlothoServer.Core.props` (KlothoGenerator single-compilation), and uses `RoomManager(MaxRooms=1)` + `RoomRouter` + `ServerLoop`. Client uses `JoinServerDrivenAsync` (no local host), `Mode=ServerDriven`. Game-side LOC ≈ 916 (Sim 292 / Client View 540 / Server 107). Verified: server 1 + 2 clients, server-authoritative + client prediction, per-component hash match (deterministic), WASD move, fall-off respawn, 60s GameOver clean shutdown.
- Documentation: [`Samples/SdSample/README.md`](Samples/SdSample/README.md) (server + 2 clients quick start) and [`Docs/Samples/SdSample.md`](Docs/Samples/SdSample.md) (architecture + SD-specific gotchas). README "Optional samples" anchor updated.
- SD-specific pitfalls surfaced reusing a P2P game on a server topology (documented in `Docs/Samples/SdSample.md` §5): the SD client skips `OnInitializeWorld` (boots from the server FullState), so callbacks must not depend on state cached there — `OnPollInput` dropped all input via a stale `_engine` guard (SG11), and the static ground registered there was missing on the client → landing-tick desync, fixed by registering statics in `RegisterSystems` which runs on every peer (SG14); SD network playerIds are 1-based, so entity spawn / id mapping must match `MoveCommand.PlayerId` (SG12); the match-end Synced event must implement `IMatchEndEvent` or the server never drains (SG13).

### Logging — AsyncEvent flush (opt-in)

- `RollingFileSink` gains an opt-in `KFlushMode` (`PerLine` default — unchanged behavior; `AsyncEvent` — event-driven background-thread flush). `AsyncEvent` removes the per-line flush syscall from the hot path and coalesces bursts into a single flush, while a unified termination path (`Dispose` / `ProcessExit` / `DomainUnload`) drains the tail on graceful exit. Exposed via `KRollingFileOptions.FlushMode` / `AddRollingFile`.
- Dedicated server (`Tools/BrawlerDedicatedServer`) now selects `AsyncEvent` for its rolling file log.

### Diagnostics — static collider fingerprint (IMP48)

- Regression guard for the SD client static-ground-missing desync: static colliders are not part of the state hash, so the omission used to surface only as a downstream dynamic-body desync. A low-cost `FPHash` fingerprint (collider count + per-collider fields) is now logged at boot (`[Physics][StaticGeometry] boot: count=N fp=0x...` — a server-vs-client diff reveals a `count=0` omission) and in the desync dumps. Core state hash / snapshot serialization unchanged.
- `FullStateResponseMessage` gains a `StaticFingerprint` field — the server self-populates it on every FullState send and the client compares on receive, logging `[KlothoEngine] Static geometry mismatch: ...` at join (before the desync, pointing at the cause); `0` is a "not provided" sentinel, logged once per divergence episode. No interface / delegate / `ApplyFullState` signature changes; `IStaticColliderService` gains `GetStaticFingerprint()`.
- Verified (Brawler SD): normal match server = client fingerprint match with zero false positives; with the client static dropped, the mismatch `KError` fires at the join tick ahead of the first `Determinism failure`.

## [0.2.4c] - 2026-05-29

### Fix — Logging file sink

- `RollingFileSink`: per-line flush + `ProcessExit` hook — logs immediately visible, Unity-client quit-time tail loss fixed.

## [0.2.4] - 2026-05-28

### Logging — IKLogger transition (breaking)

- ZLogger + `Microsoft.Extensions.Logging` dependencies fully removed → in-house `xpTURN.Klotho.Logging` (`IKLogger` + `KLogHandler{Trace,Debug,Information,Warning,Error,Critical}` ref-struct interpolated handlers + `KInformation` / `KDebug` / `KWarning` / `KError` extension methods + `UnityDebugSink` / Rolling-File sink). Core stays zero external-logging dependency with `noEngineReferences: true`.
- Optional MEL interop: `Plugins~/Logging.Mel` adapter (`xpTURN.Klotho.Logging.Mel`) — activates when the consumer provides `Microsoft.Extensions.Logging.Abstractions` DLL. Exposed through the UPM "Import Sample" mechanism.
- Game-side call migration: `_logger?.ZLogInformation(...)` → `_logger?.KInformation(...)` (same for other levels).

### Packaging — IMP47 UPM (breaking layout)

- Framework relocated to an embedded UPM package (`com.xpturn.klotho`) at `Klotho/Packages/com.xpturn.klotho/` (`Runtime`, `Editor`, `Plugins/Analyzers`, `Prefabs`, `Plugins~/Logging.Mel`, `Server~`). asmdef GUIDs preserved + `.meta` files moved alongside sources — cross-asmdef references remain intact.
- Dev project flattening: `Assets/Klotho/{Samples/Brawler, Samples/NavMesh, Tests, Benchmarks}` → `Assets/{Brawler, NavMesh, Tests, Benchmarks}` (the `Klotho/` namespace folder is gone).
- External install: open Unity Package Manager → "Add package from git URL..." → add `UniTask → Polyfill → Klotho` (`https://github.com/xpTURN/Klotho.git?path=Klotho/Packages/com.xpturn.klotho`) in order. New "Installation" section in README.
- Third-party source vendoring under `Runtime/ThirdParty/`: `LiteNetLib.v2.1.4` / `K4os.Compression.LZ4.v1.3.8` / `System.Runtime.CompilerServices.Unsafe.v6.1.2`. NuGetForUnity removed (`packages.config` is empty, core `precompiledReferences` are empty).
- Three debug/visualization prefabs (`EcsDebugBridge`, `FPPhysicsWorldVisualizer`, `FPStaticColliderVisualizer`) moved into the package's `Prefabs/`.

### Dedicated server build

- `Tools/Server/` → `Packages/.../Server~/`: `KlothoServer.Core.props` uses `$(MSBuildThisFileDirectory)`-relative paths so it works identically in embedded and PackageCache layouts. `ProjectReference KlothoGenerator.csproj` → `<Analyzer Include="...\Plugins\Analyzers\KlothoGenerator.dll" />` (consumer-compatible). ItemGroup consolidated into a single `Runtime/**/*.cs` glob with `Unity/` and `**/Json/**` excludes.
- Because the source generator requires source-sharing, a consumer dedicated server pulls all of Tier 1-3 (Runtime / Logging / Gameplay / LiteNetLib transport / ThirdParty) with a single `<Import Project="Packages\com.xpturn.klotho\Server~\KlothoServer.Core.props" />`. Two install patterns — (A) git submodule (recommended) or (B) PackageCache + `<KlothoPackageRoot>` — documented in the README "Dedicated server" section.

### Source generator

- `KlothoSerializationGenerator.ResolveProjectRoot` now recognizes the `/Packages/` marker in addition to `/Assets/` → moved core assemblies' `Tools/Generated/` debug-dump output restored. Consumer paths under `Library/PackageCache/com.xpturn.klotho@<hash>/...` match neither marker → `projectRoot=null` → emit skipped automatically (no PackageCache pollution on the consumer side).

### Docs

- `README.md` (Tech Stack / Repository Layout / new Installation / Dedicated server / Sample path), `Docs/BaseLibraries.md` (full rewrite — IKLogger / Vendored restructure), `Docs/Specification.md` (Directory Layout tree rewritten), `Docs/FEATURES.md`, `Docs/GameDevAPI.md`, `Docs/Navigation.md`, `Docs/SimulationConfigGuide.md`, and `Docs/Samples/Brawler.{md, E.Bootstrap, F.SceneNumbers, H.DedicatedServer, I.HowToRun}.md` updated end-to-end for the new package layout / flattening / IKLogger transition. Brawler.H gains a "Reference template" section (Brawler-vs-consumer mapping table). All 77 markdown links validated.

## [0.2.3] - 2026-05-25

- IMP-46 A: `INetworkServiceReceiver` (opt-in marker interface) — `ISimulationCallbacks` implementations declare this when they need the `IKlothoNetworkService` handle on host/guest entry. `KlothoSessionFlow.FireOnSessionCreated` dispatches it right after `OnSessionCreated` and before `InitialPlayerConfigFactory`, gated on session kind (Host/Guest) + non-null callbacks + `session.SimulationCallbacks is INetworkServiceReceiver recv`. Brawler `BrawlerGameController.OnHostOrGuestSessionCreated` no longer hand-calls `SetNetworkService`; `BrawlerSimulationCallbacks` drops its empty-body `SetNetworkService(IKlothoNetworkService _)`.
- IMP-46 B: `IKlothoEngine.IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null)` → `IReliableCommandHandle` — duplicate / past-tick reject escalation + retry-interval cooldown + empty-move collision avoidance absorbed into the framework `ReliableCommandTracker`. `ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches Brawler's prior spawn invariant. `OnResyncCompleted` resets every outstanding handle's `LastAttemptTick=-1` automatically. FaultInjection `DropSpawnCommandPlayerIds` / `ForceSpawnRetryPlayerIds` hooks preserved inside the tracker (no game-side branching). Brawler `BrawlerSimulationCallbacks` spawn-retry boilerplate (~100 LOC) absorbed.
- IMP-46 C: `[KlothoDataAsset(typeId, AssetId = ..., Key = ...)]` named-arg + `IDataAssetRegistry.Get<T>()` / `TryGet<T>(out T)` / `GetByKey<T>(string)` / `TryGetByKey<T>(string, out T)` overloads — attribute `AssetId` auto-resolved + `(Type, string)` tuple key index. The generator (`DataAssetEmitter`) auto-emits `private readonly int _assetId` + `public int AssetId => _assetId` + `ctor(int)` + (single-instance assets only) parameterless `ctor() : this(AssetIdFromAttribute)`. `DataAssetMissingConstructor` rule disabled (the analyzer no longer raises it — the descriptor type itself remains in `DiagnosticDescriptors.cs` for source-level compatibility, but has zero callsites; full removal deferred to a separate cleanup); new `DA006 DataAssetMixedUserGenerated` diagnostic added in its place. `DataAssetContractResolver` uses `OverrideCreator` + `CreatorParameters.Clear/Add` to make Newtonsoft's ctor-branch behaviour explicit. Brawler 9 DataAsset ctor + `AssetId` getter boilerplate removed; 22 callsites across 6 assets migrated from magic-id `Get<T>(N)` to the parameterless `Get<T>()` overload.
- IMP-46 D: `IKlothoSession` exposes 11 new members — 5 convenience methods (`HostGame` / `JoinGame` / `LeaveRoom` / `SendPlayerConfig` / `SetReady`), 4 state events (`StateChanged` / `PhaseChanged` / `PlayerCountChanged` / `AllPlayersReadyChanged`), and 2 properties (`PlayerCount` / `IsStopped`) — game code now depends on the interface, not the concrete class. `IKlothoEngine` exposes `PredictedFrame` / `RenderClock` / `OnEventPredicted` / `OnEventConfirmed` / `OnEventCanceled` / `Logger` (previously concrete-only). `IKlothoSession.Engine` returns `IKlothoEngine` via explicit interface implementation (the concrete `KlothoSession.Engine` getter still returns `KlothoEngine` for source-level backwards compatibility) — game code that depends on `IKlothoSession` now gets `IKlothoEngine` directly without a cast. Brawler `BrawlerSimulationCallbacks` / `BrawlerViewSync` / `GameHUD` / `BotFSMHelper` can drop the `ILogger` ctor param (escape hatch — direct `KlothoLogger.CreateDefault` storage — retained).
- IMP-46 E: `EntityView` transform pipeline integrated at frame-time — the lerp + `ApplyTransform` + `UpdatePositionParameter` populate that lived in `InternalUpdateView` moves into `InternalLateUpdateView`, fused with the `_errorVisual.Tick` call so the read-before-tick order is preserved. In tick-rate < frame-rate environments this reflects every per-frame `PredictedAlpha` change in the transform and avoids stale-lerp stutter. `UpdatePositionParameter` zeros `ErrorVisualVector` / `ErrorVisualQuaternion` when `EnableSnapshotInterpolation` is set, so the verified-frame interpolation path doesn't double-correct the rollback delta. `EngineEventOneShot.Subscribe<TEvent>(engine, filter, onPlay, onCancel, lateGuard) → EngineEventSubscription` (sealed `IDisposable`) — Predicted+Confirmed dispatch `onPlay` when filter+lateGuard pass; Canceled dispatches `onCancel` when filter passes. Brawler `CharacterView` drops the no-op `ApplyTransform` override + `LateUpdate` + `SyncFromEntity` (144 → 102 LOC). `CharacterAnimatorViewComponent` / `CharacterActionVfxViewComponent` collapse their 3-way subscribe + `HandlePlay` 4-method pattern into a single `EngineEventOneShot.Subscribe` call. Four character prefabs (Warrior/Mage/Rogue/Knight) null out `_interpolationTarget` to preserve root-direct smoothing.
- IMP-46 F: `KlothoGenerator` auto-emits a `MessageTypeId` override for `[KlothoSerializable(MessageTypeId = (NetworkMessageType)N)]` when `N >= NetworkMessageType.UserDefined_Start` (200), even for raw-cast values outside the enum (`(NetworkMessageType){rawValue}`). Sub-200 enum-external values keep the prior behaviour (silent drop) so `(NetworkMessageType)0` default-value regressions stay blocked. `SerializableTypeInfo.MessageTypeRawValue (int?)` field added; `FactoryEmitter` includes raw-value messages in `MessageRegistry.Register`. The three id-bearing attributes — `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` — gain XML doc-comments stating their id planes are mutually independent. Brawler `BrawlerReplayConfig` drops its hand-rolled `MessageTypeId` override + 4-line justification comment. `BrawlerPlayerConfig`'s generated `MessageTypeId` switches from `NetworkMessageType.UserDefined_Start` to `(NetworkMessageType)200` (literal-faithful). Wire bytes 200/201 unchanged — existing replay files remain compatible.
- IMP-46 G: `EcsSimulation.GetSystem<T>()` / `TryGetSystem<T>(out T)` / `GetSystems<T>(List<T> buffer)` added — type-match lookup over registered systems (T : class). `SystemRunner.Find<T>` / `FindAll<T>` are the internal helpers. Brawler `BrawlerSimSetup.PhysicsSystem` static property + 8 callsites demoted to a local `var physicsSystem`. `BrawlerSimulationCallbacks` stashes `_simulation` on `RegisterSystems` entry and resolves `PhysicsProvider` via `_simulation?.GetSystem<PhysicsSystem>()`. `TrapTriggerSyncTests` 3 callsites migrate from `BrawlerSimSetup.PhysicsSystem` to `sim.GetSystem<PhysicsSystem>()`.
- Fix (Reconnect): cold-start Reconnect broken on app relaunch — `LeaveRoom` unconditionally cleared the credentials store, and the normal `OnApplicationQuit` → `TeardownAll` → `Session.Stop` → `LeaveRoom` chain wiped persisted credentials on every quit. Added optional `bool keepReconnectCredentials = false` to `KlothoSession.Stop` / `KlothoSessionDriver.DetachAndStop` / `IKlothoNetworkService.LeaveRoom`; process-exit entry points (`Driver.OnDestroy`, `BrawlerGameController.TeardownAll`) pass `true`. Explicit cancel / reject paths clear credentials directly — unchanged.

- Sample (Brawler): `BrawlerSimulationCallbacks.cs` 330 → 250 LOC (spawn-retry boilerplate absorbed — meets the umbrella §9 ≤ 250 LOC target). `CharacterView.cs` 144 → 102 LOC (transform pipeline branching absorbed — 2 lines over the umbrella §9 ≤ 100 LOC target; the remainder is game-data cache + Renderer/Shield/Boost feedback toggling, outside the lift scope).

- Docs: `GameDevAPI.md` / `GameDevWorkflow.md` / `Specification.md` / `FEATURES.md` / `Brawler.md` / `Brawler.C.DataAssets.md` / `Brawler.B.Systems.md` / `Brawler.E.Bootstrap.md` updated for IssueOnce / ReliabilityPolicy / `IDataAssetRegistry.Get<T>()` 1-shot lookup / `IKlothoSession` 11-member surface / `EngineEventOneShot.Subscribe` / `EntityView` standard transform pipeline / `EcsSimulation.GetSystem<T>()` / `INetworkServiceReceiver` opt-in / attribute ID plane invariant.

## [0.2.2] - 2026-05-24

- IMP-45 A: `IKlothoModeStrategy` + `KlothoModeStrategy.Resolve(simCfg)` — per-mode dispatcher (P2P / ServerDriven 2 impls). Absorbs `_simulationConfig.Mode != ...` if-chain previously scattered across game-side `Start` / `OnBtnHost` / `OnBtnGuest` / `StartHost` / `JoinGameAsync` 5+ call sites.
- IMP-45 A: `KlothoSessionFlowAsync.JoinP2PAsync(transport, host, port, sessionCfg, ct)` / `JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)` — mode-split join entry points. Old `JoinAsync(transport, host, port, preJoin, roomId, sessionCfg, ct)` marked `[Obsolete]` (forwarding shim retained; removal target IMP46 / 0.3.0).
- IMP-45 A: `KlothoSessionFlow.OnHostSessionCreated` / `OnGuestSessionCreated` / `OnReplaySessionCreated` / `OnSpectatorSessionCreated` — 4 mode-dispatched callbacks alongside the existing `OnSessionCreated`. Absorbs the `engine.IsReplayMode` / `engine.IsSpectatorMode` 2-flag mode-by-flag branching in game-side `OnFlowSessionCreated`.
- IMP-45 B: `KlothoSessionDriver.IsStopping` — external read of the in-flight teardown guard. Game side replaces its own `_isStopping` / `_teardownInvoked` duplicate flags with `if (_sessionDriver.IsStopping) return;`. `IKlothoSessionObserver.OnSessionStopped` invariant documented for the 2 entry paths (Driver.DetachAndStop / Session.Stop direct).
- IMP-45 C: `KlothoFlowSetup.InitialPlayerConfigFactory : Func<PlayerConfigBase>` — auto `SendPlayerConfig` on guest / reconnect paths (skipped on spectator / replay). Game-side `session.SendPlayerConfig(...)` 3-site duplicate (StartHost / JoinGameAsync / ReconnectAsync) removed.
- IMP-45 D: `KlothoSessionFlow.StartReplayFromFile(string path)` — 1-call file-to-session entry (absorbs `ReplaySystem.LoadFromFile` + `simConfig.Validate()` + `StartReplay`). Throws `xpTURN.Klotho.Replay.ReplayLoadException` on load failure.
- IMP-45 D: `KlothoFlowSetup.SpectatorTransportFactory : Func<INetworkTransport>` + `KlothoSessionFlowAsync.SpectateAsync(host, port, roomId, ct)` no-transport overload — library instantiates the spectator transport via the factory. Game-side `new LiteNetLibTransport(...)` inline removed from `StartSpectatorAsync`.
- IMP-45 D (breaking): `xpTURN.Klotho.Replay.ReplaySystem.LoadFromFile` signature changed — `bool LoadFromFile(string, out IReplayData)` → `void LoadFromFile(string)`; on success the loaded data is exposed via `CurrentReplayData`, on failure the method throws `xpTURN.Klotho.Replay.ReplayLoadException`. External callers should migrate to `flow.StartReplayFromFile(path)`; `flow.StartReplay(IReplayData, ISimulationConfig)` retained as the escape hatch for custom `IReplayData` sources.
- IMP-45 E: `KlothoSession.StateChanged` / `PhaseChanged` / `PlayerCountChanged` / `AllPlayersReadyChanged` — state-change events (replacing per-frame `UpdateStatus` 7-field polling). Backed by `KlothoEngine.OnStateChanged` + `IKlothoNetworkService.OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` on the service side.
- IMP-45 F: `FaultInjectionRuntime.AttachToSession` / `FaultInjectionLoader.TryLoadAndApply` / `FaultInjection` static collections — callable without `#if KLOTHO_FAULT_INJECTION` guard. Undefined builds return null / false / empty stub (library-internal reader bodies and `FaultInjection.Reset()` body retain their macro guards — release cost stays at zero). Brawler game-side 4 `#if` guards (+ 2 BrawlerSimulationCallbacks guards) removed.
- IMP-45 G: `LateJoinNotificationMessage` (NetworkMessageType=75) — host (P2P) / server (SD) broadcast to existing peers on mid-match late-join so they update `OnPlayerJoined` / `PlayerCount` for the joiner. Guest PlayerInfo baseline aligned (P2P `""` / SD `$"Player{id}"`); forged-sender guards (`P2P: !IsHost return`, `SD: peerId != 0 return`) + idempotency.
- IMP-45 H: Spectator player list surface — `ISpectatorService.PlayerCount` + `event OnPlayerCountChanged`; P2P/SD host LateJoin broadcast extended to `_spectators`; spectator-side `LateJoinNotificationMessage` receive handler; `KlothoSession.PlayerCount` unified getter (`NetworkService → SpectatorService → 0` fallback) + `KlothoSession.SubscribeStateForwarders` wires `_spectatorService.OnPlayerCountChanged → RaisePlayerCountChanged`.
- Sample (Brawler): `BrawlerGameController.cs` 886 → 858 LOC. Mode guards / teardown guards / PlayerConfig sends / Replay & Spectator entry points / per-frame Phase polling / FaultInjection `#if` guards all absorbed into the library. (The umbrella plan's `< 600 LOC` target was not met within IMP45 scope — remaining lines are core game logic [BrawlerSettings wiring, GameMenu state machine, data loading]; further reduction deferred to IMP46+.)
- IMP-43: `ISessionConfig` / `ISimulationConfig` realignment — 12 engine-internal fields (Reactive 6, Rollback 2, Resync 3, `CatchupMaxTicksPerFrame`) moved Session → Sim; 3 service-side fields (`LateJoinDelaySafety`, `RttSanityMaxMs`, `MinStallAbortTicks`) moved Sim → Session. Wire schema breakage in 5 messages — server/client must roll out together.
- IMP-43: `USessionConfig` ScriptableObject — Inspector-editable 16-field session config; `KlothoSessionSetup` mirror fields (11 props) removed in favor of a single `SessionConfig` reference. Brawler: `BrawlerSettings._maxPlayers` removed, `_sessionConfig` is the single SoT.
- IMP-44 A: `KlothoSessionDriver` (Runtime.Unity) — MonoBehaviour Update/Stop adapter with `PreSessionUpdate` / `PostSessionUpdate` / `Stopping` / `IdlePoll` hooks.
- IMP-44 B: `FaultInjectionRuntime` + `FaultInjectionHotkeyDriver` (Runtime.Unity, `#if KLOTHO_FAULT_INJECTION`) — RTT-spike / Disconnect schedule + F12 chain-stall hotkey lifted from sample.
- IMP-44 C: `KlothoSessionFlow` (Runtime.Core) + `KlothoSessionFlowAsync` (Runtime.Unity) — 5-entry-point builder (`StartHost` / `JoinAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplay`) with single `OnSessionCreated` event. Absorbs `KlothoSession.Create` / `KlothoSession.CreateSpectator` / `KlothoConnectionAsync` calls. `KlothoSpectatorAsync` deleted (BC break).
- IMP-44 D: Bootstrap helpers (Runtime.Unity) — `KlothoAutoReconnect.TryStart`, `KlothoLogger.CreateDefault`, `ReconnectRejectReason.ToDefaultMessage`.
- IMP-44 D.3: EditorBridge auto-wire — `INavMeshProvider` / `INavAgentProvider` / `IFPPhysicsProviderSource` optional `ISimulationCallbacks` interfaces; sample-side `ConnectPhysicsProvider` + `EcsDebugBridge.Register*` calls removed.
- IMP-44 hotfix bundle: StartReplay `_isStopping` reset; cold-start `ApplyFaultInjection` placement; spectator FI scope guard via `IsSpectatorMode`; `_simulationConfig.Mode` ScriptableObject mutation removed (Mode != P2P rejected with logged error); `OnDestroy` / `OnApplicationQuit` consolidated into single-shot `TeardownAll`; spectator detection via canonical `session.Engine.IsSpectatorMode`; `StopGame` `try/finally` + 5 entry-point reset removed; `OnBtnHost` / `OnBtnGuest` P2P-only guard.
- Framework: `UKlothoBehaviour` removed (unused adapter).
- Sample: `ResultScreen._panel` reset on next session start (prevents stale result screen on subsequent matches).

- Docs: `FEATURES.md` / `GameDevAPI.md` / `GameDevWorkflow.md` / `Specification.md` / `Brawler.md` / `Brawler.E.Bootstrap.md` / `README.md` updated for the new Mode strategy / split-Join entry points / 4-dispatch callbacks / Driver.IsStopping / `KlothoFlowSetup` factories / Session state events / FaultInjection macro-agnostic surface / LateJoin propagation / spectator player list.

## [0.2.0] - 2026-05-21

- IMP-41: Singleton component first-class support — `[KlothoSingletonComponent]` + `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>`; `Frame.Add` enforces one-carrier-per-frame via `ComponentStorageRegistry.TypeIdCache<T>.IsSingleton`.
- IMP-41: Built-in `RandomSeedComponent` (singleton, engine-injected at `Start`; restored via FullState on LateJoin / Reconnect / Spectator / Replay) replaces sample-side `GameSeedComponent`; Brawler `GameTimerStateComponent` migrated to singleton.
- IMP-41: `TransformComponent` marker-based prev-snapshot redesign — `PreviousInitialized` marker drives the `Frame.Add` auto-init hook and PreUpdate `SavePrev` pass; `Frame.RefreshPreviousTransform(entity)` covers post-Add ref-set paths. Sample-side `SavePreviousTransformSystem` removed.
- IMP-41 A-1: `KlothoConnectionAsync` lifted to `Assets/Klotho/Unity/` (Runtime.Unity); A-2: EVU `BindBehaviour` / `ViewFlags` 5-flag decision lifted to `EntityViewFactory` base; A-3: Replay `InitialStateSnapshot` auto-inject lifted to `Engine.StartReplay`; A-4: `IReconnectCredentialsStore` injected via `KlothoSessionSetup.CredentialsStore` (+ `AppVersion` / `DeviceIdProvider`); A-5: client-reactive PastTick / rollback-burst escalation lifted to `DynamicInputDelayPolicy` (thresholds in `SessionConfig`); A-6: `EndGracePolicy.Pause` `StopCommand` auto-injected by engine.
- IMP-41 B-1: `IKlothoSessionObserver` bulk-subscribed lifecycle (replaces per-event `+=` wiring across StartHost / JoinGame / Reconnect / StopGame); adds `OnSessionStopped`. B-2: `OnReconnectFailed(byte)` carries `ReconnectRejectReason` enum directly + `ReconnectFailedException.Reason` for cold-start paths. B-3: `KlothoSession.CreateSpectator(SpectatorSessionSetup)` + `KlothoSpectatorAsync.CreateAsync` — framework owns SpectatorService / two-config await / Engine+Simulation construction; game supplies `CallbacksFactory(simCfg, sessionCfg)`. B-4: `ClientShutdownGraceMs` scheduler routed through `KlothoSession.Update`. B-5: `PlayerViewRegistry<TView>` built into `EntityViewUpdater` (driven by `OwnerComponent` add/remove; events for local-view subscription).
- IMP-41: P2P spectator entry fix — unified spectator broadcast condition + `SpectatorAccept.LastVerifiedTick` derivation + `_lastVerifiedTick` default correction. Spectator bootstrap tick-race fix (framework-side gate) + `ItemSpawnSystem` defensive guard for unwritten singleton seed.
- IMP-42: Spectator EC activation — removed `_isSpectatorMode` guards on `CapturePreRollback` / `ComputeErrorDeltas` / Predict-under-Predicted; `HandleSpectatorUpdate` wires the EC pair only when a verified batch arrives.
- Framework: re-simulation invariant diagnostic guard extended to `DEVELOPMENT_BUILD` (was Editor-only). Pause-grace `StopCommand` auto-inject overwrite blocked + regression test. Brawler sample-system sync tests fixed for the new singleton-entity setup.
- Docs: `FEATURES.md` / `GameDevAPI.md` / `Specification.md` / `Brawler.md` / `Brawler.B.Systems.md` / `Brawler.E.Bootstrap.md` updated for the above lifts (singleton API, `IKlothoSessionObserver`, spectator path, `DynamicInputDelayPolicy`, `OnReconnectFailed(byte)`, `RandomSeedComponent` adoption, sample-side `SavePreviousTransformSystem` removal).

## [0.1.8] - 2026-05-18

- Updated LiteNetLib to [2.1.3](https://github.com/RevenantX/LiteNetLib/releases/tag/2.1.3); renamed `ConnectionRequest` → `LiteConnectionRequest` in `LiteNetLibTransport.OnConnectionRequest` to match the new interface signature.


## [0.1.7] - 2026-05-18

- IMP-40 (`KLOTHO_FAULT_INJECTION`-only): SD RTT-spike desync fix — per-peer FIFO clamp in `LiteNetLibTransport._delayedRecvMessages` resolves the fault-injection reorder bug (production transport unaffected).
- IMP-40: `ProcessVerifiedBatchCore` batch-start gap fill refactored to use `SimulateGapTickWithEmptyFallback` helper, closing a latent bug where missing players were not substituted with `EmptyCommand` (separate from the fault-injection fix above; surfaces in high-RTT predict-snapshot rollback paths).
- IMP-40: SD desync runtime alarm — `[SD][ResimGap]` `ZLogWarning` emits when a verified batch leaves resim behind `entry.Tick`; complemented by dev-only diagnostics (`[InputCollector][Reject]`/`[EmptySubst]`, `[SD][PredSource]`, `[CompHash][History]`). `ServerInit` component hash dump promoted to `ZLogInformation` so release builds still surface it.
- IMP-39: All-participants-spawned gate — introduced `SessionParticipantComponent`; engine writes deterministic participant slots into the frame at `Start()`.
- IMP-39: Normal-end lifecycle — `IMatchEndEvent`/`EndReason` + `OnMatchEnded` channel, grace-driven `Room` drain, wired into `BrawlerGameController`/`GameOverSystem`.
- IMP-39: Client match-end reaction — prediction-freeze path + `ClientShutdownGraceMs` (SessionConfig).
- IMP-39: `StopCommand`-based pause behavior — SD/P2P unified, handled in `PlatformerCommandSystem`.
- IMP-39: `SessionConfig` propagation across 17 fields (6 previously missing + Normal-join bug fix), aligned `GameStartMessage`/`LateJoinAcceptMessage`/`ReconnectAcceptMessage`.

## [0.1.6] - 2026-05-16

- IMP-38: Added `KlothoState.Aborted` — abnormal terminal state distinct from `Finished`; `IsEnded()` extension covers both terminal states.
- IMP-38: `AbortMatch(AbortReason)` API + `OnMatchAborted` event — surfaces ChainStallTimeout / StateDivergence / ReconnectFailed reasons; Brawler subscriber includes double-trigger guard.
- IMP-38: P2P chain-stall watchdog — `CheckChainStallTimeout()`; calls `AbortMatch(ChainStallTimeout)` when lag ≥ `MinStallAbortTicks` ( or reconnect timeout + 100 ticks).
- IMP-38: Corrective Reset — host broadcasts `FullStateKind.CorrectiveReset` via `TryCorrectiveReset()` on hash mismatch; `ApplyReason` enum drives retreat policy; `CorrectiveResetCooldownMs` (default 5 s) prevents broadcast storms; `OnMatchReset(ResetReason)` event for non-terminal recovery.
- IMP-38: Hash gate hardening — `ApplyFullState` blocks post-state application when localHash ≠ remoteHash; `OnHashMismatch(tick, localHash, remoteHash)` event; desync/resync telemetry counters (`ResyncHashMismatchCount`, `ConsecutiveDesyncPeak`, `ResyncRequestTotalCount`, `PostResyncDesyncCount`, `UnexpectedFullStateDropCount`) surfaced in `PresumedDrop` metrics log.
- IMP-38: Reconnect input gap recovery — reconnecting peer input injection changed from `JoinTick` to `LastSentTick + 1`; `IsPlayerInActiveCatchup()` guard suppresses presumed-drop false positives during reconnect catchup.
- IMP-38: `OnPendingWipe(tick, playerId, WipeKind)` event — tracks inputs and SyncedEvents wiped before chain verification; added `RelaySealDropCount` counter.
- DS: `--rtt-metrics` CLI flag for single/multi-room.

## [0.1.5] - 2026-05-13

- IMP-38: RTT distribution metrics from `ServerNetworkService` with `RttMetricsEnabled` runtime toggle; structured metrics for LateJoin/Reconnect extraDelay (game-level playerId); preserve RTT sample across warm Reconnect.
- IMP-38: Phase 2 — clamp policy + interface promotion; replaced `ApplyExtraDelay` bool flag with `ExtraDelaySource` enum.
- IMP-38: Phase 3 — dynamic InputDelay mid-match push + reactive fallback; server-driven `RecommendedExtraDelay` for LateJoin/Reconnect cold-start path; same-tick multi-cmd allowed in monotonic clamp; `avgRtt` clamped to sanity cap; `Sync` extra-delay seed buffered until engine subscribed and branched per `JoinKind` in `SDClientService`; skip `LagReductionLatency` tracker on Reconnect.
- IMP-38: P2P port of `RecommendedExtraDelay` — `IKlothoEngine.IsHost` + `OnChainAdvanceBreak` event; route `RecommendedExtraDelayUpdate` to engine on peers; host `PingPong` hook → dynamic-delay push smoother (full pipeline); guest reactive fallback redesigned from chainbreak-burst to rollback-amplitude (with overflow guard); RTT spike schedule driver + match-scoped metrics collector.
- IMP-38: P2P reconnect hardening — host self-wipe + chain-advance stall P0 fixes (forward gap fill); 3-peer reconnect defects from Phase 2-1 playtest; catchup batch silent drop (defect ④); bundle-based stale peer cleanup on reconnect (dropped `_pendingPeers` keep guard); catchup `InputDelay` window + `Connect` socket cleanup; diagnostic log pruning from reconnect/relay paths.

## [0.1.4] - 2026-05-09

- IMP-36: Unified single-room SD on `RoomManager` bootstrap; exposed drain phase counters and lifetime metric.
- IMP-37: Closed multi-match determinism leak — gated `DeterminismVerification` assembly behind `UNITY_INCLUDE_TESTS` (drops typeId 9000–9002 from runtime layout), skipped `OnInitializeWorld` on SD client to prevent double-init race, added per-component hash dump + pool counters for desync diagnostics.
- IMP-38: Bootstrap & recovery hardening across phases — hardened `InputDelayTicks` validation for SD (Phase 0), state-driven spawn query + resync reconciliation hook (Phase 1), bootstrap handshake removing structural first-tick race (Phase 2), command rejection feedback unicast (Phase 3), fault-injection infrastructure & scenario matrix. Follow-ups: route initial `FullState` through bootstrap path on countdown-skip clients; hybrid (Version + OwnerId) dedup for ghost view; escalate spawn cmd lead on `PastTick` reject.

## [0.1.3] - 2026-04-30

- IMP-35: 3-layer defense against malformed wire packets — L1 `MessageSerializer.Deserialize` try/catch + cache invalidation (overflow-safe boundary check), L3 `Room.DrainInboundQueue` try/finally (guaranteed buffer recovery + loop continuation), L2 server `_pendingPeers` atomicity + immediate disconnect on malformed/unknown payload (pending and regular dispatch). Minimal `ZLogWarning` traceability at 3 client-side wire-input sites.

## [0.1.2] - 2026-04-30

- IMP-32: `LiteNetLibTransport` connection key — constructor injection with `DefaultConnectionKey` constant fallback.
- IMP-33: Propagate disconnect reason via `INetworkTransport.OnDisconnected` — added 6-value `DisconnectReason` enum; `Listen`/`Connect` now return `bool` to surface startup failures immediately. Client handlers gate auto-reconnect to `NetworkFailure`/`ReconnectRequested` only.
- IMP-34: 64-bit `SessionMagic` with CSPRNG generation + device-binding on cold-start reconnect.
- Aligned spectator transport connection key with the main transport.
- Aligned `Connect` API deviceId with the provider pattern; added send-site logging.

## [0.1.1] - 2026-04-29

- IMP-30: Unified Engine `_playerCount` semantics — roster source-of-truth consolidated to `_activePlayerIds.Count`. Replaced `OnPlayerCountChanged` with `OnPlayerJoinedNotification`.
- IMP-31: Split Spectator RoomRouter capacity gate — separated into two layers: an absolute upper bound (DoS protection, `MaxPlayersPerRoom + MaxSpectatorsPerRoom`) and the spectator slot gate (`HandleSpectatorJoin`), resolving the regression where spectators were blocked once 4 players filled the room.
- Added `SessionConfig.MaxSpectators` + wired into `BrawlerDedicatedServer` single-room and multi-room paths (including expanded Listen capacity). Removed the `maxPlayers` CLI argument.

## [0.1.0] - 2026-04-26

- First release
