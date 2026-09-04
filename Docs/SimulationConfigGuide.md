# SimulationConfig Recommended-Value Guide (per Genre / Platform)

> This document is a starting-point guide for how to configure the key parameters of [`ISimulationConfig`](../com.xpturn.klotho/Runtime/Core/ISimulationConfig.cs) / [`SimulationConfig`](../com.xpturn.klotho/Runtime/Core/Engine/SimulationConfig.cs) by game genre and platform.
>
> **Recommended values are only a starting point.** Tune them against measured RTT/jitter, content-driven input frequency, and concurrent entity counts. Default definitions live in [`SimulationConfig.cs`](../com.xpturn.klotho/Runtime/Core/Engine/SimulationConfig.cs); field semantics are documented in [Specification.md §2.2](./Specification.md#22-default-configuration-values).

---

## 1. Key Parameters and Their Impact

| Parameter | Smaller | Larger | Determinism Impact |
|---|---|---|---|
| `TickIntervalMs` | Faster response · network load ↑ · simulation cost ↑ | Slower response · load ↓ | ✅ must be identical across all peers |
| `InputDelayTicks` | More direct feel · less network slack | Better jitter absorption · higher input latency | ✅ must be identical |
| `MaxRollbackTicks` | Less memory · narrower rollback window | More memory · wider recovery window on hiccups | ✅ |
| `SyncCheckInterval` | Faster desync detection · more traffic | Less traffic · slower detection | — |
| `UsePrediction` | Pause on missing input (Paused) | Predict, then rollback | — |
| `SDInputLeadTicks` | Less server-arrival slack · risk of unapplied input | Higher perceived input latency · greater stability | — (SD only) |
| `InterpolationDelayTicks` | Fresher remote entities · more jitter exposure | Smoother interpolation · more delay | — (View) |
| `EnableErrorCorrection` | Rollback jumps are drawn in full | Rollback jumps are visually smoothed | — (View · **inert without the marker in [1.1](#11-enabling-error-correction--three-touches)**) |
| `QuorumMissDropTicks` | Faster presumed-drop recovery · false-positive rollback thrash on jitter | Slower recovery · more jitter-tolerant | — (P2P only) |
| `DiagnosticHistoryTicks` | `0` disables · desync localization falls back to check-tick granularity | Deeper layered hash history · **new** per-tick hash cost under P2P | — (diagnostic-only · not wire-propagated) |

**Key formulas (SD mode):**
```
Effective input latency (perceived) ≈ (InputDelayTicks + SDInputLeadTicks) × TickIntervalMs
Server input deadline                = the tick's execution moment (no fixed tolerance window)
  · inputs missing at execution → EmptyCommand · later arrivals → past-tick reject
  · chronic lateness self-corrects via client lead escalation (no HardToleranceMs knob — see §3.2 note)
```

**Key formulas (P2P mode):**
```
Effective input latency (perceived) ≈ InputDelayTicks × TickIntervalMs
Remote input arrival slack          ≈ InputDelayTicks × TickIntervalMs (RTT/2 must be < this for hiccup-free play)
```

**Key formula (View interpolation, both modes):**
```
Remote render delay          = InterpolationDelayTicks × TickIntervalMs
Frame-time budget            = (InterpolationDelayTicks − 2) × TickIntervalMs
  · the render clock rides a sawtooth of one tick + one render frame; the slack absorbing it is
    (delay − 1) ticks, so a clamp-free render needs  frameMs ≤ budget
  · budget ≤ 0 at delay 1 and 2 — those clamp on every frame with positive drift, at any tick rate
  · delay 3 covers a 60fps frame for any TickIntervalMs ≥ 17; a 16ms tick needs 4
  · frameMs is the WORST frame time you support, not the typical one
```

> **`InterpolationDelayTicks` is validated to [1, 4].** The upper bound used to be 3; the rows below
> and the §4 adjustments were written before the budget formula existed and recommended values whose
> budget is zero. They have been corrected — if you are copying an older config, re-derive from the
> formula rather than from the old numbers.

> **Not tuning knobs:** `SimulationConfig` also carries `StageId` (which stage/map the match runs) and `MatchConfigData` (an opaque per-match payload — game mode, rules, difficulty). These are **content/match selectors set per-match by the authority**, not per-genre performance values, so they are not tuned here — leave them at their defaults (`StageId` 0, empty) for a single-stage game. See [GameDevAPI §3.6](./GameDevAPI.md#36-per-match-config--stageid--matchconfigdata) and [LobbyIntegrationGuide §4-E](./LobbyIntegrationGuide.md#4-e-per-room-match-config--reservation-sd-multi-room).


### 1.1 Enabling Error Correction — three touches

`EnableErrorCorrection = true` **on its own does nothing.** The flag arms the view-side smoother, but the
rollback deltas it smooths are only produced for entities carrying a marker component — and that marker is
added by *your simulation code*. Three places have to agree:

| # | Where | What |
|---|---|---|
| ⑴ | The **authority's** config | `EnableErrorCorrection = true` |
| ⑵ | Simulation code | `frame.Add(entity, new ErrorCorrectionTargetComponent())` on entities that may render client-predicted |
| ⑶ | *(optional · Unity)* Prefab | tune `EntityView._errorVisual` (`ErrorVisualState`, 11 fields) |

**⑴ is the authority's config, not necessarily the one you are editing.** The flag rides the propagated
`SimulationConfig` (`SimulationConfigMessage` / `SpectatorAcceptMessage`), so an SD **guest** runs the
server's value and its own asset is ignored. Set it where the match is authored: the server config for SD,
the host's for P2P.

**⑵ decides which entities are smoothed, and the rule is "can this render client-predicted?"** — in
practice, player-owned entities. Error correction applies on the CSP render path only; the
snapshot-interpolation path already draws authoritative state and skips the rollback delta by design. So a
spectator gets nothing, an SD client's *remote* entities get nothing, and P2P — where every view is CSP —
is fully covered. Bots and server-owned props do not need the marker. The Brawler sample does exactly
this: the marker goes on player characters at spawn and nowhere else
([PlatformerCommandSystem](../Samples/Brawler/Assets/Brawler/Scripts/ECS/Systems/PlatformerCommandSystem.cs)).

> ⚠️ **⑵ is simulation state, and the desync check does not protect it.** The marker is a tag component
> that contributes nothing to the frame hash, so peers that disagree about who carries it **still pass
> SyncCheck**. It does change the FullState bytes (the per-type entity list), so a disagreement surfaces
> only on a FullState path — LateJoin, Reconnect, Spectator, Replay — where the authority's set silently
> overwrites the receiver's. Treat the marker as part of the deterministic build: every peer ships the same
> code that adds it.

If ⑴ is on and ⑵ is missing, the engine says so: after 8 consecutive rollbacks with no marked entity it
emits a latched `[EC]` warning naming the likely cause. There is no such signal for the reverse (marker
present, flag off) — that combination simply costs a little memory and does nothing.

---

## 2. Mode Selection Guide

| Game Type | Recommended Mode | Rationale |
|---|---|---|
| 1v1 fighting, small co-op (≤4) | `P2P` | Direct RTT, no host overhead |
| Competitive PvP / matchmaking / ranked | `ServerDriven` | Cheat prevention, matchmaking-friendly |
| Mobile matched PvP | `ServerDriven` | Mobile NAT traversal / stability |
| Synchronous turn-based (≥4) | `ServerDriven` | Input authority, end-of-match verification |
| Asynchronous (single-player / replay) | `P2P` (solo) | Network not required |

> Klotho's two modes have different message flows and session-entry paths, so the mode is usually fixed once the game genre is decided.

---

## 3. Per-Genre Recommended Values

> Notation:
> - `PC` = desktop / console (assumes stable RTT 30–80 ms)
> - `Mobile` = LTE/5G (assumes RTT 60–150 ms · jitter 20–60 ms)
> - **Bold** values are recommended starting points; parentheses give the acceptable range
> - All ms values are integers
> - `EnableErrorCorrection = true` in the rows below is only half the setting — it does nothing without
>   the marker component of [1.1](#11-enabling-error-correction--three-touches) ⑵

### 3.1 Fighting — 1v1 / 2v2

A genre where a single input frame translates directly to a visible result. **Rollback is the top priority** with minimal input latency.

| Parameter | PC Recommended | Mobile Recommended | Note |
|---|---|---|---|
| `Mode` | `P2P` | `ServerDriven` | PC = direct RTT; mobile prioritizes stability |
| `TickIntervalMs` | **17** (16–25) | **25** (25–33) | PC=60 Hz / Mobile=40 Hz |
| `InputDelayTicks` | **2** (1–3) | **3** (2–4) | PC ~34 ms / Mobile ~75 ms |
| `MaxRollbackTicks` | **8** (6–12) | **10** (8–14) | Standard for fighting rollback |
| `SyncCheckInterval` | 30 | 30 | Once per second (Mobile ~1.2 s) |
| `UsePrediction` | `true` | `true` | Required |
| `SDInputLeadTicks` | — | **2** (2–4) | Keep small under SD |
| `QuorumMissDropTicks` | **20** (10–40) | — | P2P presumed-drop watchdog (PC only) |
| `EnableErrorCorrection` | `false` | `true` | Smoother interpolation on mobile |
| `InterpolationDelayTicks` | **3** | **3** | Budget 51/75 ms. 1 and 2 were recommended here before the formula and have budget ≤ 0 — at a 16ms tick use 4 |
| `MaxEntities` | 32 | 32 | Sized to characters + projectiles |

**Tuning notes:**
- If perceived input latency is too high on mobile, drop `InputDelayTicks` to 2 but raise `MaxRollback` to 12 to compensate.
- For 1v1, `MaxRollback` can be reduced to 8 to save memory.

### 3.2 Action / Brawler / FPS · TPS (PvP)

Many entities · responsiveness matters · deterministic simulation (the Brawler sample falls in this category).

| Parameter | PC Recommended | Mobile Recommended | Note |
|---|---|---|---|
| `Mode` | `ServerDriven` | `ServerDriven` | Cheat prevention |
| `TickIntervalMs` | **25** (25–33) | **40** (33–50) | PC=40 Hz / Mobile=25 Hz |
| `InputDelayTicks` | **0** (0–4) | **0** (0–4) | 0 lets Lead absorb it; the Brawler sample uses 4 for extra send/receive headroom (additive with Lead) |
| `MaxRollbackTicks` | **50** (30–60) | **50** (30–60) | Rollback depth ≈ 1.25–2 s |
| `SyncCheckInterval` | **30** | **30** | |
| `UsePrediction` | `false` | `false` | SD is server-authoritative — prediction not needed |
| `SDInputLeadTicks` | **4** (3–6) | **6** (4–10) | Higher on mobile to absorb RTT |
| `InputResendIntervalMs` | **150** (100–200) | **150** (100–250) | Unacked-input resend |
| `MaxUnackedInputs` | 30 | 30 | |
| `EnableErrorCorrection` | `true` | `true` | Remote-entity correction |
| `InterpolationDelayTicks` | **3** | **3** (3–4) | Budget 25/40 ms. PC was 2 = budget 0; mobile may go to 4 for jitter |
| `MaxEntities` | 256 | 128 | Proportional to content scale |

**The Brawler sample (`Samples/Brawler/Server/simulationconfig.json`)** is a real-world PC example for this category (`InputDelayTicks=4`, `SDInputLeadTicks=4` — i.e. it opts into the non-zero `InputDelayTicks` end of the range for extra headroom).

**Tuning notes:**
- For builds with many characters/projectiles, set `MaxEntities` to the measured peak + 25% headroom.
- In mobile handover environments (LTE↔5G transitions), raise `SDInputLeadTicks` to 8–10.
- When running P2P, raise `InputDelayTicks` to 3–4, ignore `SDInputLeadTicks`, and tune `QuorumMissDropTicks` (default 20, sweet spot 20–40) for presumed-drop recovery.

### 3.3 MOBA / Co-op Action RPG (PvE)

| Parameter | PC Recommended | Mobile Recommended | Note |
|---|---|---|---|
| `Mode` | `ServerDriven` | `ServerDriven` | |
| `TickIntervalMs` | **33** (25–50) | **50** (40–66) | PC=30 Hz / Mobile=20 Hz |
| `InputDelayTicks` | **0** | **0** | SD absorbs via Lead |
| `MaxRollbackTicks` | **30** (20–50) | **30** (20–50) | |
| `SyncCheckInterval` | 30 | 30 | |
| `UsePrediction` | `false` | `false` | |
| `SDInputLeadTicks` | **4** (3–6) | **6** (4–10) | |
| `EnableErrorCorrection` | `true` | `true` | |
| `InterpolationDelayTicks` | **3** | **3** (3–4) | Budget 33/50 ms. PC was 2 = budget 0 |
| `MaxEntities` | 512 | 256 | Includes minions/mobs |

### 3.4 RTS (Real-Time Strategy)

Many units · low input frequency · rollback impractical (simulation cost too high). **Fixed lockstep + prediction disabled** pattern.

| Parameter | PC Recommended | Mobile Recommended | Note |
|---|---|---|---|
| `Mode` | `P2P` or `ServerDriven` | `ServerDriven` | Cheat prevention / matchmaking |
| `TickIntervalMs` | **100** (66–200) | **150** (100–250) | 10 Hz–5 Hz |
| `InputDelayTicks` | **6** (4–10) | **8** (6–12) | Tolerates 600–1200 ms input latency |
| `MaxRollbackTicks` | **2** (1–4) | **2** (1–4) | Effectively no rollback |
| `SyncCheckInterval` | 10 | 10 | Fast desync detection |
| `UsePrediction` | `false` | `false` | Required |
| `SDInputLeadTicks` | **4** | **6** | Only under SD |
| `QuorumMissDropTicks` | **20** (10–40) | — | P2P watchdog (when PC runs P2P) |
| `EnableErrorCorrection` | `false` | `false` | Correction unnecessary |
| `MaxEntities` | 1024+ | 512+ | Sized to unit count |

**Tuning notes:**
- A small `SyncCheckInterval` is fine despite the traffic cost (the absolute frequency is low because ticks themselves are slow).
- When raising `MaxEntities`, also measure ECS component memory and hash cost — the per-tick frame copy is bound by *reservation*, not by live entities ([ECSMemoryOptimization.md](ECSMemoryOptimization.md#reservation-is-per-tick-time-not-only-bytes)).
- **`MaxEntities` is not a navigation budget.** The built-in navigation layer is sized for tens of agents, not thousands, and it does not fail loudly when you pass that:
  - `FPNavAgentSystem.ResolveCollisions` resolves **at most `MAX_AGENTS` (64)** agents per tick — beyond that, the remaining agents are simply not separated (no error, no log).
  - ORCA neighbour selection scans **every agent for every agent** each tick (O(N²), uncapped) — this is the first thing to bind as unit counts grow.
  - Pathfinding has a **per-call** cap (`MAX_ITERATIONS` 4096) and a per-agent repath cooldown, but **no per-tick global budget** — a mass order can put every unit into A* on the same tick.
  - A path corridor is capped at `MAX_CORRIDOR` (128) triangles, and the fixed array behind it is most of `NavAgentComponent`'s 708-byte footprint.
  These are compile-time constants on purpose: they are determinism inputs (they change simulation results, and `MAX_CORRIDOR` changes the hashed component layout), so they cannot be turned into plain per-peer settings. For unit counts in the hundreds or more, plan on a game-side movement layer (flow fields, a spatial hash, a bounded path-request queue) over the deterministic primitives rather than on the stock agent system.

- **Moving hundreds of units.** Measured, on a 192×192-unit field: the tick where 800 units all receive one move order costs **~970 ms**, against ~16 ms for the steady-state following that follows it — and ~42% of those searches return **no path at all**, silently, because they run out of the per-call A* budget. The order tick, not ORCA, is what binds first. Three levers work today with no engine change:
  1. **Admit paths over several ticks** (promote K destinations per tick) instead of letting an idle army fire on one. The repath cooldown does not do this for you — it only gates units that already repathed.
  2. **Call `Update` once per spatial cluster.** The array is yours; `MAX_AGENTS` is a *per-call* cap, so splitting both cuts the O(N²) neighbour scan and stops silently dropping agents from the correction pass. At 800 units, 50 clusters of 16 measured 6.3 ms against 16.3 ms for a single call.
  3. **Flow fields for same-destination groups**, over the already-public triangle graph.
  The partition is a **determinism input**, not a local optimisation — it must be a pure function of frame state, and a path-admission cursor must live in frame state or be derived from the tick, or rollback resimulation diverges. Full pattern, the measured tables, the quality trade the split makes, and the diagnostic counters that report each cap: [Navigation.md § Crowd Scaling](./Navigation.md#crowd-scaling).

### 3.5 Tactics / Strategy — Non-Real-Time

Each turn is discrete; simulation advances after input arrives. **Slow tick + no rollback.**

| Parameter | PC Recommended | Mobile Recommended | Note |
|---|---|---|---|
| `Mode` | `ServerDriven` | `ServerDriven` | Matchmaking / verification |
| `TickIntervalMs` | **100** (50–250) | **200** (100–500) | 5 Hz–2 Hz |
| `InputDelayTicks` | **2** (0–6) | **2** (0–6) | Turn-based games tolerate large latency |
| `MaxRollbackTicks` | **2** (1–4) | **2** (1–4) | No rollback |
| `SyncCheckInterval` | 5 | 5 | |
| `UsePrediction` | `false` | `false` | |
| `SDInputLeadTicks` | **2** | **2** | |
| `EnableErrorCorrection` | `false` | `false` | |
| `MaxEntities` | 256 | 128 | Sized to board |

---

## 4. Platform / Environment Adjustment Guide

### 4.1 Mobile (LTE / 5G / Wi-Fi)
- **RTT variability**: assume +50–100 ms average RTT and +20–40 ms jitter standard deviation versus PC.
- **`SDInputLeadTicks`**: add +2–4 to the PC recommendation.
- **`InterpolationDelayTicks`**: add +1 for jitter absorption, i.e. 3 → 4 (4 is the validated maximum). The genre rows above already start at 3, so this is the one step available.
- **`TickIntervalMs`**: bump up one tier vs. PC (e.g., PC 25 ms → Mobile 40 ms).
- **Battery / heat**: lowering tick rate reduces both simulation cost and send/receive frequency, so it has a large impact.
- **Runtime NavMesh rebake**: if your game has one, its slice budget is a per-device knob and is *not* a config field — see [4.4](#44-per-device-knobs-that-are-not-in-simulationconfig).
- **These are room-wide, not per-client.** The values above describe the profile a mobile room runs; a PC and a phone in the SAME room share one config (see the cross-platform table in [4.4](#44-per-device-knobs-that-are-not-in-simulationconfig)). Mixed rosters take the weaker platform's profile.

### 4.2 Console (wired / stable)
- PC recommendations work as-is. `TickIntervalMs` can be reduced to 16–17 (60 Hz) for fighting/action games — **at 16 ms raise `InterpolationDelayTicks` to 4**, because 3 leaves a 16 ms budget and a 60fps frame is 16.67 ms. 17 ms is the smallest tick that 3 covers, and it covers it by 0.3 ms.

### 4.3 Global Matchmaking (cross-region RTT > 150 ms)
- Recommend `SDInputLeadTicks` ≥ 8 (the server has no fixed tolerance window, so cross-region RTT is absorbed entirely by client lead).
- Add +1 to the recommended `InterpolationDelayTicks` (3 → 4). The earlier "+1–2" produced 5, which validation rejects; the budget at 4 already covers a 2-frame stall at any tick rate ≥ 17 ms.

### 4.4 Per-device knobs that are NOT in `SimulationConfig`

One tunable is deliberately outside the config, and it belongs in this section because it is the knob
that *should* differ between a PC build and a phone: the **rebake slice budget** —
`FPNavMeshRebakeDriver(source, installer, rules, sliceBudgetUnits, slotCount)`, default **20000 work
units per frame**. It only exists for games that rebuild the NavMesh during a match
([Navigation.Rebake.md](./Navigation.Rebake.md)).

**Why it is not a config field.** `SimulationConfig` is shared — under ServerDriven the server sends
it to every client — so a field there cannot hold a per-device value, and anything in it reads as a
determinism input. The slice budget is the opposite: peer-local by construction. A sliced rebake
produces a byte-identical mesh at any budget, and `SteppedRebake_EqualsOneShot_AtRandomBudgets` proves
it with a budget that changes on *every step*. Each peer may pick its own, and no peer needs to know
what the others chose.

**How to set it.** Constructor argument on the driver — a platform `#if`, a device-tier setting, or a
per-stage value (the stage is already known where the rebake context is built). It cannot be changed
after construction.

**The lower bound, which is the one way this goes wrong.** Lowering the budget does not change the
result; it moves the cost. If the slices do not finish before the placement's effective tick, the
boundary tick finishes the remainder **synchronously, whole** — the spike slicing exists to remove.
Keep:

```text
budget × frames between the command and its effective tick  ≥  that rebake's total work
```

Brawler's window is `BuildDelayTicks = 60` at 25 ms = 1.5 s, roughly 90 frames at 60 fps. Note the
tension: mobile wants a **smaller** budget to protect the frame, and — if it also runs at a lower
frame rate or a longer tick — has **fewer** frames of runway. When the two conflict, the knob to move
is not the budget: it is the build delay (more runway) or the stage size. **Both of those are
match-wide, not per-device** — see below before reaching for either.

**Cross-platform rooms: which of these may differ per peer.** In a room holding a PC and a phone the
line is not "performance knobs may differ" but "peer-local values may differ":

| | Per peer? | Why |
| --- | --- | --- |
| `sliceBudgetUnits` | **Yes** | The mesh is byte-identical at any budget, so nothing downstream can tell. Give the phone a smaller one *because* the room is mixed |
| `slotCount` | **Yes** | A mesh cache. Fewer slots means more rebakes on that peer and the same meshes — `slotCount: 1` is a real memory lever on mobile (the second slot costs a second work-buffer pool) |
| Build delay (`BuildDelayTicks` or your equivalent) | **NO** | Each peer computes `EffectiveTick = frame.Tick + delay` itself. Two values means two ticks for the same building — the mesh diverges while every component still matches. Brawler folds this constant into `GetGameFingerprint` for exactly this reason |
| Stage / base mesh | **NO** | The rebake carves from it; the nav fingerprint covers it |

So on a mixed roster the delay is chosen **for the room**, sized to the device with the least runway,
and the budget is what varies underneath it. Reversing that — per-device delay — is a desync that no
state hash reports.

The same rule governs this whole section: everything in §1 marked ✅ (`TickIntervalMs`,
`InputDelayTicks`, `MaxRollbackTicks`) is a property of the MATCH. The per-platform advice in 4.1 is
about which profile a room runs, not about what one client may set for itself — a mixed room takes one
profile, usually the weaker platform's. Under ServerDriven that is enforced structurally (the server
sends the config); under P2P it has to be agreed before the match starts.

**Measure per platform, not per engine.** A work unit is a deterministic amount of work, not a
duration; how many of them fit in a frame is exactly what differs between devices.
`FPNavMeshRebakerPerfTests.P0F_SliceBudgetCalibration` is the fixture, and the table in
[Navigation.Rebake.md §8](./Navigation.Rebake.md#slicing-it-across-frames) is one stage on one machine
— treat it as the shape of the curve, not as your value.

**The signal that it is too small** is `driver.BoundaryFinishes` rising against `driver.TaskInstalls`.
Neither is logged by the engine, so a game that tunes per device should put both in its own telemetry.

---

## 5. Diagnosis / Tuning Workflow

1. **Measure before locking values**: enable basic diagnostics with `TickDriftWarnMultiplier=2` and `EventDispatchWarnMs=5`.
2. **Watch the logs**:
   - `Tick gap` warnings → revisit `TickIntervalMs` suitability or simulation cost.
   - `Accumulator clamped` → frame drops or `TickIntervalMs` set too low.
   - SD `MaxUnackedInputs exceeded` → adjust `InputResendIntervalMs` or raise `SDInputLeadTicks`.
3. **On desync**: temporarily lower `SyncCheckInterval` (e.g., 10 → 5) for faster detection. Keep `DiagnosticHistoryTicks > 0` (default 60) so the engine localizes the divergence to a component type / system participant and auto-probes the peer for a verdict — see [DesyncDiagnostics.md](./DesyncDiagnostics.md). Under P2P this buys a per-tick hash; set `0` only if that steady cost is unaffordable (localization then degrades to check-tick granularity).
4. **Interpolation jitter**: raise `InterpolationDelayTicks` one step at a time. `EnableErrorCorrection` helps on top of that, but **the flag alone changes nothing** — without the marker of [1.1](#11-enabling-error-correction--three-touches) ⑵ no deltas are produced for it to smooth.
5. **Reactive escalation ceiling**: `SimulationConfig.Validate()` enforces `ReactiveMax ≤ MaxRollbackTicks / 2`. If you lower `MaxRollbackTicks` (e.g. fighting at 8), `ReactiveMax` is clamped to that half (≤4) with a warning — raise `MaxRollbackTicks` first if you need a wider reactive window.
6. **Frame spike when a building lands**: read `driver.BoundaryFinishes`. A rising count means the slices did not finish inside the delay window and the boundary tick paid for the whole rebake — raise the budget, the build delay, or lower the stage size ([4.4](#44-per-device-knobs-that-are-not-in-simulationconfig)).
7. **On `OnMatchAborted(ChainStallTimeout)` false positives**: verify `SessionConfig.ReconnectTimeoutMs` against the recovery floor. Effective threshold = `max(ReconnectTimeoutMs / TickIntervalMs + 100, SessionConfig.MinStallAbortTicks)`. If `ReconnectTimeoutMs < 30s`, `MinStallAbortTicks` (default 600 = 30s @ 50ms) acts as the floor — raise both for high-latency/long-recovery scenarios.

> **Field placement** — `MinStallAbortTicks`, `LateJoinDelaySafety`, `RttSanityMaxMs` live in `SessionConfig` alongside the other LateJoin/Reconnect/ChainStall service-side knobs. The engine-internal knobs — `CatchupMaxTicksPerFrame`, `ResyncMaxRetries`, `DesyncThresholdForResync`, `CorrectiveResetCooldownMs`, `CorrectiveResetMaxAttempts`, `AutoAbortOnRecoveryExhausted`, `ServerSnapshotRetentionTicks`, `QuorumMissDropTicks`, Reactive Dynamic InputDelay 7 (`ReactiveWindowTicks`, `ReactiveEscalateThreshold`, `ReactiveStep`, `ReactiveMax`, `ServerPushGraceTicks`, `ReactiveEscalateCooldownTicks`, `ReactiveDeEscalateStableTicks`), and Rollback Burst 2 (`RollbackBurstCount`, `RollbackWindowTicks`) — live in `SimulationConfig`.

---

## 6. Summary — Decision Tree

```
1) Is the game matchmaking/ranked-based?
   YES → ServerDriven
   NO  → P2P (small friends co-op)

2) Is visible 60 Hz input response important? (fighting · precision shooter)
   YES → TickIntervalMs ≤ 25, InputDelayTicks ≤ 3, MaxRollback 8–12, UsePrediction=true
   NO  → next step

3) Character-scale action, or many-unit strategy?
   Character     → TickIntervalMs 25–40, MaxRollback 30–50
   Many units    → TickIntervalMs 100–200, MaxRollback 1–4, UsePrediction=false

4) Mobile build?
   YES → TickIntervalMs +1 tier, SDInputLeadTicks +2–4,
         InterpolationDelayTicks +1, EnableErrorCorrection=true (+ marker — see 1.1)
```

---

## 7. References

- Field semantics / defaults: [`Specification.md §2.2`](./Specification.md#22-default-configuration-values)
- Desync diagnosis / log analysis (`DiagnosticHistoryTicks`): [`DesyncDiagnostics.md`](./DesyncDiagnostics.md)
- Real-world SD example: [`Samples/Brawler/Server/simulationconfig.json`](../Samples/Brawler/Server/simulationconfig.json)
- Per-mode message flow: [`Specification.md §9`](./Specification.md)
- Brawler sample bootstrap: [`Samples/Brawler.E.Bootstrap.md`](./Samples/Brawler.E.Bootstrap.md)
- Runtime NavMesh rebake, incl. the slice budget: [`Navigation.Rebake.md`](./Navigation.Rebake.md)
