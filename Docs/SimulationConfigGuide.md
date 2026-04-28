# SimulationConfig Recommended-Value Guide (per Genre / Platform)

> This document is a starting-point guide for how to configure the key parameters of [`ISimulationConfig`](../Assets/Klotho/Runtime/Core/ISimulationConfig.cs) / [`SimulationConfig`](../Assets/Klotho/Runtime/Core/Engine/SimulationConfig.cs) by game genre and platform.
>
> **Recommended values are only a starting point.** Tune them against measured RTT/jitter, content-driven input frequency, and concurrent entity counts. Default definitions live in [`SimulationConfig.cs`](../Assets/Klotho/Runtime/Core/Engine/SimulationConfig.cs); field semantics are documented in [Specification.md §2.2](./Specification.md#22-default-configuration-values).

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

**Key formulas (SD mode):**
```
Effective input latency (perceived) ≈ (InputDelayTicks + SDInputLeadTicks) × TickIntervalMs
Server input arrival deadline       ≈ HardToleranceMs (auto: above + RTT/2 + 20 ms jitter)
```

**Key formulas (P2P mode):**
```
Effective input latency (perceived) ≈ InputDelayTicks × TickIntervalMs
Remote input arrival slack          ≈ InputDelayTicks × TickIntervalMs (RTT/2 must be < this for hiccup-free play)
```

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
| `InputDelayTicks` | **0** (0–2) | **0** (0–2) | Recommended 0 under SD (Lead absorbs it) |
| `MaxRollbackTicks` | **50** (30–60) | **50** (30–60) | Rollback depth ≈ 1.25–2 s |
| `SyncCheckInterval` | **30** | **30** | |
| `UsePrediction` | `false` | `false` | SD is server-authoritative — prediction not needed |
| `SDInputLeadTicks` | **4** (3–6) | **6** (4–10) | Higher on mobile to absorb RTT |
| `HardToleranceMs` | **0** (auto) | **0** (auto) | Auto reflects RTT |
| `InputResendIntervalMs` | **150** (100–200) | **150** (100–250) | Unacked-input resend |
| `MaxUnackedInputs` | 30 | 30 | |
| `EnableErrorCorrection` | `true` | `true` | Remote-entity correction |
| `InterpolationDelayTicks` | 2 | 3 | Higher on mobile to absorb jitter |
| `MaxEntities` | 256 | 128 | Proportional to content scale |

**The Brawler sample (`Tools/BrawlerDedicatedServer/simulationconfig.json`)** is a real-world PC example for this category.

**Tuning notes:**
- For builds with many characters/projectiles, set `MaxEntities` to the measured peak + 25% headroom.
- In mobile handover environments (LTE↔5G transitions), raise `SDInputLeadTicks` to 8–10.
- When running P2P, raise `InputDelayTicks` to 3–4 and ignore `SDInputLeadTicks`.

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

### 4.2 Console (wired / stable)
- PC recommendations work as-is. `TickIntervalMs` can be reduced to 16–17 (60 Hz) for fighting/action games.

### 4.3 Global Matchmaking (cross-region RTT > 150 ms)
- Recommend `SDInputLeadTicks` ≥ 8.
- Keep `HardToleranceMs` at 0 (auto) — it accounts for RTT and is safer.
- Add +1–2 to the recommended `InterpolationDelayTicks`.

---

## 5. Diagnosis / Tuning Workflow

1. **Measure before locking values**: enable basic diagnostics with `TickDriftWarnMultiplier=2` and `EventDispatchWarnMs=5`.
2. **Watch the logs**:
   - `Tick gap` warnings → revisit `TickIntervalMs` suitability or simulation cost.
   - `Accumulator clamped` → frame drops or `TickIntervalMs` set too low.
   - SD `MaxUnackedInputs exceeded` → adjust `InputResendIntervalMs` or raise `SDInputLeadTicks`.
3. **On desync**: temporarily lower `SyncCheckInterval` (e.g., 10 → 5) for faster detection.
4. **Interpolation jitter**: raise `InterpolationDelayTicks` one step at a time and enable `EnableErrorCorrection`.

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
- Real-world SD example: [`Tools/BrawlerDedicatedServer/simulationconfig.json`](../Tools/BrawlerDedicatedServer/simulationconfig.json)
- Per-mode message flow: [`Specification.md §9`](./Specification.md)
- Brawler sample bootstrap: [`Samples/Brawler.E.Bootstrap.md`](./Samples/Brawler.E.Bootstrap.md)
