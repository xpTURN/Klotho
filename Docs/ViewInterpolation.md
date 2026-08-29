# View Interpolation Guide

> **Who this is for.** You are building a game on Klotho, you have entities moving in the simulation, and
> you want to know why they look smooth (or why they do not). No prior knowledge of the rollback engine is
> assumed. Everything here is about the **view layer only** — none of it affects determinism, and nothing
> in this document can cause a desync.
>
> **The short version:** the base view class already does all of this for you. Read §2, and come back for
> the rest when something looks wrong.

---

## 1. Why interpolation exists at all

The simulation runs on a **fixed tick** — `TickIntervalMs`, typically 25–50 ms (40 Hz / 20 Hz). The display
runs at whatever the device gives you: 60, 120, 144 Hz. Those two rates almost never divide evenly.

```
simulation:   |-------tick 100-------|-------tick 101-------|-------tick 102-------|
              (position jumps at each boundary, nowhere in between)

display:      | f | f | f | f | f | f | f | f | f | f | f | f | f | f | f | f | f |
              (needs a position for every one of these)
```

If a view simply copied the simulation position every frame, an entity would sit perfectly still for two or
three frames, then jump. At 40 Hz sim / 60 Hz display that is visible stutter on every moving object.

**Interpolation is the answer to "where do I draw this thing on a frame that falls between two ticks?"** The
view keeps two known poses and blends between them by a fraction called **alpha** (0 = the earlier pose,
1 = the later one). The simulation never sees any of it — interpolated positions exist only on the transform
you look at.

There is a second, networking-specific reason. On a networked peer, ticks arrive in **bursts** — a batch of
confirmed ticks lands, then nothing for a while. Interpolating against the raw arrival of those batches would
turn network jitter directly into visible jitter. So Klotho renders remote entities deliberately **a few ticks
in the past**, on a smoothly advancing clock of its own, and spends that small delay buying jitter absorption.

---

## 2. What you actually have to do

Almost nothing. The transform pipeline lives in the base view class — `EntityView` (Unity) /
`EntityViewNode` (Godot) — and runs on every view, every frame, automatically.

| # | What | Where | Needed? |
|---|------|-------|---------|
| ⑴ | **Subclass the base view** and let it drive the transform. Override `OnUpdateView` / `OnLateUpdateView` for animation and effects only. | your view script | always |
| ⑵ | **Stamp `TransformComponent.TeleportTick = frame.Tick`** whenever your simulation moves an entity discontinuously (respawn, warp, dash-blink). | your simulation systems | whenever you teleport |
| ⑶ | Pick **`InterpolationDelayTicks`** (default `3`). | `SimulationConfig` | tune once |
| ⑷ | *(optional)* Set an **interpolation target** child on the prefab so the root stays tick-exact. | prefab / scene | only if you raycast the root |
| ⑸ | *(optional)* Turn on **error correction** to hide rollback jumps — this takes three touches, not one. | see §8 | P2P / high latency |

**The one thing you must not do** is hand-roll your own `LateUpdate` + `Lerp` on a view. See §10.

---

## 3. The two render paths

Every view renders through exactly one of two paths. The choice is made once, at spawn, by the factory —
you do not pick it per frame.

| | **CSP path** (predicted) | **Snapshot path** (verified) |
|---|---|---|
| Blends between | `PredictedPrevious` → `Predicted` (the two most recent local ticks) | `Verified(n)` → `Verified(n+1)` (two confirmed ticks in the past) |
| Alpha source | `RenderClock.PredictedAlpha` | `RenderClock.VerifiedAlpha` |
| Renders | your local prediction — instant, may be wrong and get rolled back | server/quorum-confirmed state — always correct, always late |
| Delay vs. live sim | none | `InterpolationDelayTicks` ticks (plus the SD lead, on an SD client) |
| Typical owner | the local player; every entity in P2P; everything on a server; replay | remote entities on an SD client; everything a spectator sees |
| Rollback jumps | possible → smoothed by error correction (§8) | impossible — confirmed state never rewinds |
| Enabled by | absence of the flag | `ViewFlags.EnableSnapshotInterpolation` |

**Why both exist.** The local player must feel instant, so it renders prediction and accepts the occasional
correction. A remote character has no responsiveness to preserve, so it renders confirmed state and accepts
a small delay — which is strictly better, because it never jitters and never has to be corrected.

### Who gets which

`EntityViewFactory.GetViewFlags` decides, from three runtime facts the prefab cannot know:

```
Replay, or a peer that is not on the verified path (P2P peer, SD server)   → CSP
SD client / spectator:
        entity is owned by the local player                               → CSP
        anything else (remote players, bots, server-owned props)          → Snapshot
```

> **`EnableSnapshotInterpolation` is factory-owned.** Ticking it in the Inspector does nothing: the factory
> owns that bit (`FactoryOwnedFlags`) and its answer wins. That is deliberate — forcing it onto a
> locally-owned entity would render *your own character* several ticks late. Every other `ViewFlags` bit is
> the prefab's, and the two halves are **merged**, not overwritten (`ComposeViewFlags`).

---

## 4. What happens in one frame

```
engine Update
  ├─ advance the verified render clock by wall-clock time      (§5)
  ├─ run 0..n simulation ticks
  └─ for each tick:  OnTickExecuted
         └─ EVU.Reconcile  → spawn / destroy views
         └─ view.OnUpdateView()        ← per TICK: animator params, VFX toggles, cached game data

LateUpdate  (Unity)  /  _Process, ProcessPriority = 1000  (Godot)
  └─ EVU → for each view: InternalLateUpdateView()           ← per FRAME
         ├─ pick the path (CSP / snapshot)
         ├─ compute the interpolated pose
         ├─ tick the error-visual smoother, then read it     (§8)
         └─ ApplyTransform(...)                              ← the only place the transform is written
```

**Per-tick and per-frame are different hooks.** `OnUpdateView` fires once per simulation tick;
`OnLateUpdateView` and `ApplyTransform` fire once per rendered frame. Below the display rate a view sees far
more frame callbacks than tick callbacks — put anything that must move smoothly on the frame side.

---

## 5. The render clock

`Engine.RenderClock` is the single source of "where between two ticks are we right now". It carries both
paths at once.

| Field | Meaning |
|---|---|
| `PredictedBaseTick` / `PredictedAlpha` | `CurrentTick - 1`, and the tick accumulator as a 0..1 fraction. The CSP path's blend factor. |
| `VerifiedBaseTick` / `VerifiedAlpha` | The confirmed tick the snapshot window sits on, and the fraction into it. |
| `Timescale` | The catch-up multiplier the verified clock is currently running at, clamped to `[0.5, 2.0]`. `1.0` when it is on target. |
| `TickIntervalMs` | The tick interval in force (for replay, the one recorded in the file). |

### The verified clock is a clock, not a counter

`LastVerifiedTick` moves in jumps — a network batch confirms several ticks at once, then nothing arrives for
a while. Driving alpha from it directly would make interpolation stutter in time with the network.

So the engine keeps a **separate render time** that advances by wall-clock every frame and *converges* toward
its target of `LastVerifiedTick - InterpolationDelayTicks`:

- **drift-proportional timescale** — 1 tick behind → run at 1.1×; 1 tick ahead → 0.9×; clamped to `[0.5, 2.0]`.
  Catch-up is therefore a slight speed-up you barely notice, not a jump.
- **10-tick snap** — if drift ever exceeds ten ticks (a long stall, a reconnect), give up on converging and
  snap to the target.
- **verified-boundary clamp** — the render time is never allowed past `LastVerifiedTick - 1`. The interpolator
  needs *two* confirmed frames (`base` and `base + 1`), so this is what guarantees both ends of the blend are
  genuinely confirmed, and not predicted state wearing a confirmed label.

### Choosing `InterpolationDelayTicks`

Default `3`, valid range `[1, 4]`.

```
Remote render delay = InterpolationDelayTicks × TickIntervalMs
Frame-time budget   = (InterpolationDelayTicks − 2) × TickIntervalMs      ← must be ≥ your worst frame time
```

Smaller = fresher remote entities, more exposure to jitter. Larger = smoother, more delay. The budget line is
the part that surprises people: the render clock rides a sawtooth of one tick plus one render frame, so at
delay 1 or 2 the clamp engages on every frame with positive drift, at any tick rate. Delay 3 covers a 60 fps
frame for any tick interval ≥ 17 ms; a 16 ms tick needs 4. Use your **worst** frame time, not the average.

Full per-genre table: [SimulationConfigGuide §1](./SimulationConfigGuide.md).

> On an SD client the value is the **server's**, not your local asset's — the config is propagated with the
> match. Set it where the match is authored.

---

## 6. The snapshot path, in detail

Given a window `[base, base + 1]`, `VerifiedFrameInterpolator.GetSnapshotPose` answers in one query: the pose,
whether the entity is even in this window, the `alpha = 0` endpoint, and whether a teleport falls inside.

| Case | What is rendered |
|---|---|
| (a) both frames hold the entity | `Lerp(pose(base), pose(base+1), VerifiedAlpha)` — the normal case |
| (b) only `base` holds it | hold `pose(base)` |
| (c) only `base + 1` holds it | hold `pose(base+1)` |
| (d) neither | look **ahead** of the window for the oldest confirmed frame that does hold it, and hold that pose. Failing that, see the fallback ladder below |
| (e) both, but `TeleportTick` differs between them | hold `pose(base)` — **never blend across a teleport** (§7) |

Two safeguards are worth knowing about, because they explain behaviour you might otherwise call a bug:

- **Endpoints are version-checked.** An `EntityRef` is an index plus a version, and index slots get recycled.
  Without the version check, a newly spawned view would smoothly render *the previous occupant of its slot*
  for the whole delay window — smoothly, so nothing on screen would look wrong.
- **Endpoints past the confirmed boundary are rejected.** The frame ring has no notion of "confirmed" and will
  happily hand back a predicted frame. Two moments can leave the clock stale-high for a single frame (session
  start, and a backward move of `LastVerifiedTick` from a resync); this check covers them.

### The fallback ladder — a freshly spawned remote entity

A view is created as soon as the newest **confirmed** frame carries the entity — but the render clock is still
`InterpolationDelayTicks` behind, so for the first few ticks the window has not reached the entity's birth yet.
The path degrades in this order:

1. window holds it → **interpolate** (a/b/c/e)
2. window is empty, but a later confirmed frame holds it → hold **that** pose *(case d — this is the spawn warmup)*
3. no confirmed frame at all — session start, late join, `FullState` restore → hold the **live predicted** pose
4. nothing anywhere → **write nothing** and leave the transform alone

Step 2 matters: rendering the live *predicted* pose during warmup would place the view `lead + delay` ticks ahead
of its own timeline, and the moment the window caught up it would snap **backwards**. Holding a confirmed pose
keeps that jump at zero for delay ≤ 2, one tick at delay 3.

### A snapshot view outlives its entity

Its render sits `delay` ticks in the past, so when the entity leaves the confirmed frame there are still `delay`
ticks of its motion left to draw. The updater keeps the view alive until the render clock arrives — a **despawn
grace**. During the grace:

- `OnLateUpdateView` / `ApplyTransform` keep running (that is the entire point),
- `OnUpdateView` does **not** — the entity is gone from the frame your game code would query,
- `PlayerViews.Get(playerId)` can still hand you that view. It answers *"what is on screen for this player"*,
  not *"is this player alive"*. **Read liveness from the frame, never from the registry.**

CSP views have no grace — their render tick and their lifetime tick are the same tick.

---

## 7. Teleports — the one line of simulation code you owe the view

Interpolation assumes motion is continuous. A teleport is not: blending across it walks the entity through
positions it never occupied, in a straight line, at a speed that scales with your frame rate. The higher the
refresh rate, the more of the fake motion you see.

The view cannot detect this on its own — a 30-metre jump and 30 metres of legitimate motion look identical from
two poses. So the simulation says so explicitly:

```csharp
// in your system, wherever you move an entity discontinuously
ref var transform = ref frame.Get<TransformComponent>(entity);
transform.Position     = spawnPoint;
transform.TeleportTick = frame.Tick;      // ← this line
```

`TeleportTick` lives on `TransformComponent`, so it is already inside every snapshot and needs no engine
support and no error correction. Each path uses it differently, and both are automatic:

- **CSP** — `TeleportTick == CurrentTick - 1` → skip the blend for that tick and snap to the post-teleport pose.
- **Snapshot** — the two window endpoints carry different `TeleportTick` values → hold the earlier pose, so the
  jump lands exactly on the tick boundary rather than a frame early or late.

> `0` means "never teleported" and never counts as a teleport, so leaving the field alone is safe.
>
> Do **not** rely on `Engine.HasEntityTeleported` for this. It means *"re-simulation introduced a teleport"*,
> exists only when error correction is on **and** a rollback happened this frame, and an ordinary respawn is
> neither. The stamp above is the general answer; the engine flag is an extra source OR-ed on top for CSP views.

The Brawler sample stamps it in exactly two places — `RespawnSystem` and `PlatformerCommandSystem` — which is
what a typical game looks like.

---

## 8. Error correction — hiding rollback jumps

Only relevant on the **CSP path**. When a rollback corrects a predicted entity, its position changes between one
frame and the next: a visible pop. Error correction hides it by rendering
`corrected position + a smoothed offset`, where the offset starts out equal to the correction (so the entity
stays where you last saw it) and then decays to zero over roughly a second and a half.

The snapshot path skips this entirely and by design — it already draws authoritative state, so applying a
rollback offset on top would double-correct and jitter.

**It takes three touches, not one** (details: [SimulationConfigGuide §1.1](./SimulationConfigGuide.md#11-enabling-error-correction--three-touches)):

| # | Where | What |
|---|---|---|
| ⑴ | the **authority's** config | `EnableErrorCorrection = true` |
| ⑵ | your simulation code | `frame.Add(entity, new ErrorCorrectionTargetComponent())` on entities that render predicted |
| ⑶ | *(optional, Unity)* prefab | tune `EntityView._errorVisual` |

⑵ is the one people miss: **the flag alone does nothing.** Deltas are only produced for entities carrying the
marker, and you add it — in practice, to player-owned characters.

The smoother (`ErrorVisualState.Tick`) runs five stages per frame: accumulate the rollback delta → reset outright
if it exceeds the teleport threshold → snap to zero if it is tiny → decay at a rate proportional to its own
magnitude → exponential blend to the output. Defaults are sane; the two you might touch are
`PosTeleportDistance` (above this, don't smooth — just cut) and `MaxRate`/`MinRate` (how fast the offset bleeds
off). `SmoothingRate` is *supposed* to be high (default 200): the smoothed value **is** the offset that hides
the jump, so lowering it exposes more of the pop on arrival, not less.

When the flag is off, none of this runs — no delta lookups, no smoother, no per-view per-frame cost.

---

## 9. The knobs

### `ViewFlags` (prefab, except where noted)

| Flag | Effect |
|---|---|
| `DisableUpdate` | Skips the whole view update — no interpolation, no callbacks. For decorative views the simulation does not move. |
| `DisablePositionUpdate` | Rotation is applied, position is not. For something pinned in place that still turns. |
| `EnableSnapshotInterpolation` | **Factory-owned** — see §3. Setting it on a prefab has no effect. |

### The interpolation target (Unity)

By default the view interpolates its **root** transform. Assign `_interpolationTarget` to a descendant and the
split becomes:

- **root** — the tick-quantized pose (CSP: the live tick; snapshot: the window's `alpha = 0` confirmed endpoint).
  This is your stable reference for collision and raycasts.
- **target (mesh/VFX)** — the interpolated + error-corrected pose. This is what the player sees.

Any descendant works, at any depth and any parent scale — the result is written through world-space setters.
The authored local pose of that child is remembered and restored on pool reuse and on teleport, so pooling never
leaves an old occupant's offset behind.

> **Picking / clicking**: the two transforms are not in the same place, and for a snapshot-interpolated view both
> of them trail the live simulation by the interpolation delay (plus the SD lead). When you need "what the player
> was actually pointing at", raycast against the mesh, not the root.

### `SimulationConfig`

`InterpolationDelayTicks` (§5) and `EnableErrorCorrection` (§8). Both are propagated from the authority in SD.

---

## 10. Rules of thumb

- **Do not hand-roll interpolation** in a `LateUpdate` of your own. The base pipeline is `internal` and drives
  every view; a hand-rolled lerp re-derives teleport handling, version-safe frame lookups and the two-path split
  — and gets them wrong. If a scene object needs to be driven by an entity, have the factory **adopt** it
  (see [GameDevWorkflow](./GameDevWorkflow.md)).
- **Override `ApplyTransform` only for a genuine root/child split.** Regular views inherit it.
- **Per-tick logic → `OnUpdateView`. Per-frame visuals → `OnLateUpdateView`.**
- **Stamp `TeleportTick`.** It is one line and it is the single most common cause of "my character smears across
  the map on respawn".
- **Don't read liveness from `PlayerViews`** — a view in its despawn grace is still registered.
- **Don't tune `InterpolationDelayTicks` on a client** in SD; the server's value is the one that runs.

---

## 11. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Everything stutters at a steady rate | not using the base view pipeline, or `DisableUpdate` is set | subclass `EntityView` / `EntityViewNode`; check the prefab's flags |
| An entity smears across the map on respawn / warp | `TeleportTick` is not stamped | §7 — one line in the system that moves it |
| Remote players look jittery on a bad connection | delay too small for the jitter | raise `InterpolationDelayTicks` (authority's config) |
| Remote players feel laggy | delay too large | lower it — but check the frame-time budget in §5 first |
| Newly spawned remote entity appears, runs ahead, then jumps backwards | pre-warmup rendering of predicted state | this is what the fallback ladder (§6) fixes; if you see it, you are overriding the pose yourself |
| Your own character feels delayed | it is rendering on the snapshot path | it should be CSP — check your `GetViewFlags` override and `FactoryOwnedFlags` |
| Rollback pops are visible on the local player | error correction not fully wired | all three touches in §8 — the marker component is the usual miss |
| Corrections are computed but the pop is still visible | the smoother **discarded** it — the accumulated error crossed `PosTeleportDistance` (1 m) or `RotTeleportDeg` (90°), where cutting is the intended answer | look for `[EC][Visual] … snapped reason=…` (Debug) — it names the bound and how much was thrown away. If the amount looks wrong for what happened on screen, that is the finding, not the snap |
| A view briefly appears at another entity's position | you are looking up frames yourself without a version check | use the base pipeline; `IsAlive` before `Has`, always |
| A prefab flag you ticked has no effect at runtime | the factory owns that bit | only `EnableSnapshotInterpolation` is factory-owned by default; if your override decides more bits, widen `FactoryOwnedFlags` |
| An entity keeps rendering for a moment after it dies | despawn grace on the snapshot path | expected — §6 |

---

## 12. Unity and Godot

The model, the two paths, the render clock, the interpolator and the error-visual smoother are **identical** on
both engines. The differences are hosting details:

| | Unity | Godot (.NET) |
|---|---|---|
| View base | `EntityView` (`MonoBehaviour`, prefab) | `EntityViewNode` (`Node3D`, `.tscn`) |
| Per-frame driver | `EntityViewUpdater.LateUpdate` | `EntityViewUpdaterNode._Process` (`ProcessPriority = 1000`) |
| Transform write | `ApplyTransform(ref UpdatePositionParameter)` — overridable | applied inline; no parameter struct |
| Root / child split | `_interpolationTarget` | not provided — the node is written directly |
| Error-visual tuning | `[SerializeField] _errorVisual`, Inspector-exposed | defaults, in code |

Lifecycle callbacks (`OnInitialize` / `OnActivate` / `OnUpdateView` / `OnLateUpdateView` / `OnDeactivate`) and the
factory decision API (`TryGetBindBehaviour` / `GetViewFlags`) are the same on both.

---

## 13. Where to go next

| Document | For |
|---|---|
| [GameDevWorkflow.md](./GameDevWorkflow.md) | building the view layer step by step — factory, updater, view, adoption |
| [GameDevAPI.md](./GameDevAPI.md) | the view transform pipeline's exact contract, despawn grace, `PlayerViewRegistry` |
| [SimulationConfigGuide.md](./SimulationConfigGuide.md) | per-genre values for `InterpolationDelayTicks`, and error correction's three touches |
| [SynchronizationDesign.md](./SynchronizationDesign.md) | why there are two timelines (Verified / Predicted) in the first place |
| [FEATURES.md](./FEATURES.md) | the full view-layer feature list on both engines |

### Source, if you want to read it

| | Unity | Godot |
|---|---|---|
| View base | [`Unity/View/EntityView.cs`](../com.xpturn.klotho/Unity/View/EntityView.cs) | [`Godot~/Adapters/View/EntityViewNode.cs`](../com.xpturn.klotho/Godot~/Adapters/View/EntityViewNode.cs) |
| Snapshot interpolator | [`Unity/View/VerifiedFrameInterpolator.cs`](../com.xpturn.klotho/Unity/View/VerifiedFrameInterpolator.cs) | [`Godot~/Adapters/View/VerifiedFrameInterpolator.cs`](../com.xpturn.klotho/Godot~/Adapters/View/VerifiedFrameInterpolator.cs) |
| Error-visual smoother | [`Unity/View/ErrorVisualState.cs`](../com.xpturn.klotho/Unity/View/ErrorVisualState.cs) | [`Godot~/Adapters/View/ErrorVisualState.cs`](../com.xpturn.klotho/Godot~/Adapters/View/ErrorVisualState.cs) |
| Render clock | [`Runtime/Core/Clock/RenderClockState.cs`](../com.xpturn.klotho/Runtime/Core/Clock/RenderClockState.cs) · [`KlothoEngine.RenderClock`](../com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.cs) | same (engine-agnostic core) |
