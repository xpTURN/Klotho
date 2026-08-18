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

> **Not tuning knobs:** `SimulationConfig` also carries `StageId` (which stage/map the match runs) and `MatchConfigData` (an opaque per-match payload — game mode, rules, difficulty). These are **content/match selectors set per-match by the authority**, not per-genre performance values, so they are not tuned here — leave them at their defaults (`StageId` 0, empty) for a single-stage game. See [GameDevAPI §3.6](./GameDevAPI.md#36-per-match-config--stageid--matchconfigdata) and [LobbyIntegrationGuide §4-E](./LobbyIntegrationGuide.md#4-e-per-room-match-config--reservation-sd-multi-room).

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
| `InterpolationDelayTicks` | 1 | 2 | Short — responsiveness first |
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
| `InterpolationDelayTicks` | 2 | 3 | Higher on mobile to absorb jitter |
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
| `InterpolationDelayTicks` | 2 | 3 | |
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
- When raising `MaxEntities`, also measure ECS component memory and hash cost.

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
- **`InterpolationDelayTicks`**: add +1 (jitter absorption).
- **`TickIntervalMs`**: bump up one tier vs. PC (e.g., PC 25 ms → Mobile 40 ms).
- **Battery / heat**: lowering tick rate reduces both simulation cost and send/receive frequency, so it has a large impact.
- **Runtime NavMesh rebake**: if your game has one, its slice budget is a per-device knob and is *not* a config field — see [4.4](#44-per-device-knobs-that-are-not-in-simulationconfig).
- **These are room-wide, not per-client.** The values above describe the profile a mobile room runs; a PC and a phone in the SAME room share one config (see the cross-platform table in [4.4](#44-per-device-knobs-that-are-not-in-simulationconfig)). Mixed rosters take the weaker platform's profile.

### 4.2 Console (wired / stable)
- PC recommendations work as-is. `TickIntervalMs` can be reduced to 16–17 (60 Hz) for fighting/action games.

### 4.3 Global Matchmaking (cross-region RTT > 150 ms)
- Recommend `SDInputLeadTicks` ≥ 8 (the server has no fixed tolerance window, so cross-region RTT is absorbed entirely by client lead).
- Add +1–2 to the recommended `InterpolationDelayTicks`.

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
4. **Interpolation jitter**: raise `InterpolationDelayTicks` one step at a time and enable `EnableErrorCorrection`.
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
         InterpolationDelayTicks +1, EnableErrorCorrection=true
```

---

## 7. References

- Field semantics / defaults: [`Specification.md §2.2`](./Specification.md#22-default-configuration-values)
- Desync diagnosis / log analysis (`DiagnosticHistoryTicks`): [`DesyncDiagnostics.md`](./DesyncDiagnostics.md)
- Real-world SD example: [`Samples/Brawler/Server/simulationconfig.json`](../Samples/Brawler/Server/simulationconfig.json)
- Per-mode message flow: [`Specification.md §9`](./Specification.md)
- Brawler sample bootstrap: [`Samples/Brawler.E.Bootstrap.md`](./Samples/Brawler.E.Bootstrap.md)
- Runtime NavMesh rebake, incl. the slice budget: [`Navigation.Rebake.md`](./Navigation.Rebake.md)
