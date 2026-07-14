# Desync Diagnostics — Root-Cause Localization & Log-Analysis Guide

> **Audience**: developers / operators facing a desync (state divergence).
> **Scope**: the desync-diagnostics system as currently shipped.
> **Design basis**: [SynchronizationDesign.md](SynchronizationDesign.md) recovery ladder — diagnostics **do not change** the ladder; they sit beside it as an observation layer.

---

## 1. Overview — the diagnostic funnel

When a desync is detected the system **automatically** narrows the cause and lands a **one-line verdict**. Narrowing order (the funnel):

| Stage | Question | Output | Owner |
|---|---|---|---|
| **A. Classify** | Did input diverge, or state? | `class=Input\|State` | command digest |
| **B. Tick** | Which tick diverged first? | `tick=T₀` | history ring + online Probe L1 |
| **C. Layer** | Which component type? which system? | `layer=Component(TransformComponent) \| System(PhysicsSystem)` | hash layering + online Probe L2 |

- **Local (always)**: the moment a peer detects, it logs **its own** classification, layered hashes, and recent history.
- **Online (automatic round-trip)**: it fetches the counterpart peer's hashes (P2P host/guest, SD server), **compares them**, and emits a one-line verdict (`[Desync][Verdict]`). SD also records that verdict in the **server log**.

---

## 2. Enabling / Configuration

Diagnostic history is gated by a **single** knob, `ISimulationConfig.DiagnosticHistoryTicks`.

| Value | Meaning |
|---|---|
| `0` | History ring off — the layered breakdown is still logged on detection (locally), but **per-tick localization (B) and the online Probe are disabled**. |
| `> 0` (default **60**) | Continuously accumulates the last N ticks' layered hashes into a zero-GC ring → tick localization + Probe available. |

- **Default 60** (`SimulationConfig`, `USimulationConfig`, `GodotSimulationConfig` alike). Capacity floor ≈ `MaxRollbackTicks` + RTT headroom.
- **Not wire-propagated**: absent from `SimulationConfigMessage` — peers may run different values (each logs its own).
- **Cost**: **SD** already hashes every tick on the server / verified-resim paths, so accumulation is **free** there. **P2P** pays a **new** per-tick hash when `>0` (~0.1% of the tick budget for a typical world; measure for large worlds). If that P2P cost matters in production, set only that config to `0`.

---

## 3. Reading the logs (core)

All diagnostic logs are filterable by the `[Desync]` prefix. Practical flow:

### 3.1 Detection — the classification line
When a desync is caught, the **detecting peer** logs a classification line.

**P2P** ([`CompareAndReportSyncHash`](../com.xpturn.klotho/Runtime/Network/Services/KlothoNetworkService.cs)):
```
[Desync][Diag] tick=1234 class=StateDivergence remotePlayer=2 local.state=0x… remote.state=0x… local.cmd=0x… remote.cmd=0x…
```

**SD client** ([determinism-failure branch](../com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ServerDrivenClient.cs)):
```
[KlothoEngine][SD] Determinism failure: tick=301, local=0x…, server=0x…
[Desync][Diag] tick=301 class=StateDivergence local.cmd=0x8B83… server.cmd=0x8B83…
```

- **`class=InputDivergence`** — the two peers' **command sets (cmdHash) differ** → input propagation / buffering / ordering = an **engine-side** problem.
- **`class=StateDivergence`** — same input, different result → a **determinism violation = game logic / math** side.
- If `local.cmd == remote.cmd` but only `state` differs, it is State (cmd takes precedence in the branch).

### 3.2 Local layered breakdown
At the detection point each peer dumps its own per-layer hashes ([`LogStateBreakdown`](../com.xpturn.klotho/Runtime/ECS/EcsSimulation.cs), label `DesyncLocal`):
```
[Desync][Diag][DesyncLocal] tick=301 total=0x96BE… frame=0x5198… components=22 systems=1
[Desync][Diag][DesyncLocal] layer=Component type=TransformComponent(1) count=5 hash=0x66CF…
[Desync][Diag][DesyncLocal] layer=Component type=HealthComponent(21) count=0 hash=0xCBF2…
   …(one line per registered component type)…
[Desync][Diag][DesyncLocal] layer=System participant=0(PhysicsSystem) hash=0x4D25…
```
- `total` is the same full-state hash as `GetStateHash()`. `frame` folds Tick + entity count + all components.
- The **`layer=System`** line is the key to tracking system-state divergence — components alone can't pin divergence in system state (e.g. RNG) held by snapshot participants.

### 3.3 History (tick trajectory)
```
[Desync][Diag][History] dumpTick=301 depth=60
   …(per-tick layered hashes over the retained window)…
```
Only when `DiagnosticHistoryTicks>0`. Scan the layered hashes around the divergence to see "from when, and which layer, started to wobble."

### 3.4 Online Probe — comparing with the counterpart
When `DiagnosticHistoryTicks>0`, on detection it auto round-trips to the counterpart ([`KlothoEngine.DesyncProbe.cs`](../com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.DesyncProbe.cs)):
```
[Desync][Probe] request: peer=0, level=TickHashes, corrId=1, window=[280..300]   ← L1: find the first diverged tick (P2P)
[Desync][Probe] request: peer=0, level=Breakdown, corrId=2, window=[300..300]     ← L2: layer diff at that tick
[Desync][Diag] layer=Component type=TransformComponent(1) localCount=5 remoteCount=5 local=0xF17F… remote=0x3F95…
```
- **`localCount==remoteCount` but hash differs** = a field-mutation / drift signature (same count, differing values).
- `layer=System participant=s(Name)` means system-state (RNG etc.) divergence.

### 3.5 Verdict — the one-liner
When the round-trip diff completes, the **one-line verdict**:
```
[Desync][Verdict] tick=301 class=State layer=Component target=TransformComponent(1) peer=0 local=0x… remote=0x…
```
The diagnostic's final output — "which tick, which class, which layer/component" on a single line.

### 3.6 The verdict in the SD server log
In SD the side that detects the divergence is the **client**, so left alone the verdict would only live on the user's device. The client therefore sends **just the verdict** (integers) to the server, which records it in the server log too:
```
[Desync][Verdict][src=client-reported unverified] peerId=0 tick=301 class=State layer=Component target=TransformComponent(1) clientLocal=0x… clientRemote=0x…
```
- **`src=client-reported unverified`** — this is the **client's claim**, not a fact the server verified (note this when quoting the log). The server **only records** the value; it does not use it in any decision ([§8](#8-safety-guarantees)).
- The type name is **mapped from the server registry** (only integers cross the wire → zero log-injection surface).

---

## 4. How it works (brief)

| Mechanism | Role |
|---|---|
| **Hash layering** | `StateHashBreakdown` — splits the full hash per component type / per snapshot participant (same compute; the layered hashes are a byproduct). `total` equals the existing `GetStateHash()`. |
| **Command digest** | An FNV digest (`cmdHash`) of the sorted command list. P2P adds +8B to `SyncHashMessage`; SD compares locally. The basis for classification (Input/State). |
| **History ring** | Gated by `DiagnosticHistoryTicks`; a zero-GC numeric ring. Source for tick localization + Probe responses. |
| **Online Probe** | L1 (tick-hash window) → L2 (layered breakdown) round-trip + automatic diff + verdict. |

---

## 5. Mode differences (P2P vs SD)

| | P2P | SD |
|---|---|---|
| Detector | both peers (symmetric) — a guest's counterpart is always the host | the client (own resim ≠ server verified) |
| Classification timing | at L2 (needs the responder's cmdHash) | **locally on detection** (the client also holds the server's confirmed) |
| Probe round-trip | L1 → L2 (two-stage) | **L2 only** (diverged tick = `entry.Tick`, already known) |
| Verdict location | each peer's local log (no verdict sent — symmetric, untrusted) | client-local + **server log** (verdict sent) |
| Responder | host↔guest unicast | the server serves from its own history ring |

**The responder never rewinds** — it answers only from ring lookups, or replies "unavailable" if absent (live sim untouched).

---

## 6. Degradation / failure logs (not bugs)

Because diagnostics are an **observation layer beside the ladder**, when they cannot narrow the cause they **degrade honestly** while recovery proceeds normally. These are normal degradation logs:

| Log | Meaning |
|---|---|
| `[Desync][Diag] remote diagnostics unavailable: …` | The counterpart has diagnostics off, or already overwrote that tick → verdict without a layer. |
| `[Desync][Diag] tick localization failed: … no agreed tick …` | No matching anchor inside the shared window (divergence is older than the window) → degrades rather than naming a false tick. |
| `[Desync][Diag] build/registry mismatch: …` | The two peers registered a different number of types (mixed builds) → skips the layer diff (avoids a wrong accusation). |
| `[Desync][Probe] serve: … throttledSinceLastServe=N` | Served after dropping N under the per-peer cooldown (amplification guard). |
| `[Desync][Probe] timeout: …` | No response arrived → pending discarded (no retry). |
| `class=Unknown` / `layer=None` | Couldn't pin the class / layer → still reports the tick and whatever is available. |

---

## 7. Workflow — when a desync happens

1. **Filter logs by `[Desync]`.**
2. **Read the `[Desync][Verdict]` line first** — tick, class, layer, target. Usually this alone locates the culprit code (e.g. `class=State layer=Component target=TransformComponent @301` → the movement/physics logic at that tick).
3. Deeper: narrow "from when" with the **`[Desync][Diag][DesyncLocal]`** layered hashes + the **`[History]`** trajectory.
4. **SD operations**: developers aggregate / trend from the **server log**'s `[Desync][Verdict][src=client-reported unverified]` (e.g. "87% of desyncs are TransformComponent") — keeping in mind it is client-reported.
5. Direction by class: **Input** → input propagation / buffering / ordering (engine); **State** → determinism (game math / RNG / floating point).

---

## 8. Safety Guarantees

- **Diagnostic-only** — the diagnostic payloads (Probe, verdict) **feed no recovery-ladder or anti-cheat decision.** Tampering at worst pollutes a developer log. The server **only records** the verdict, does not re-run the diff, and drops out-of-range values.
- **No rewind** — the responder serves from the history ring or replies "unavailable." It never rolls back the live sim for someone else's diagnostics.
- **Capture before recovery** — detection-time values (layered hashes, command digest) are captured synchronously **before** recovery overwrites the state. This avoids the misdiagnosis of comparing against resim'd values and **silently returning a wrong answer**.
- **zero-GC boundary** — the always-on path before detection (history accumulation) allocates nothing. The post-detection diagnostic round-trip is a cold path, so allocation is allowed there.

---

## 9. Reference

**Config** — `ISimulationConfig.DiagnosticHistoryTicks` (int, default 60, 0=off, not wire-propagated).

**Messages** ([`NetworkMessageType`](../com.xpturn.klotho/Runtime/Network/INetworkMessage.cs), all ReliableOrdered):
| # | Name | Direction |
|---|---|---|
| 93 | `DesyncProbeRequest` | requester → responder (L1/L2) |
| 94 | `DesyncProbeResponse` | responder → requester |
| 95 | `DesyncVerdictReport` | SD client → server (logging only) |

**Enums** ([`DesyncVerdict.cs`](../com.xpturn.klotho/Runtime/Diagnostics/DesyncVerdict.cs)):
- `DesyncClass` : `Input=0` · `State=1` · `Static=2` · `Unknown=3`
- `DesyncLayer` : `None=0` · `Component=1` · `System=2`

**Log prefixes**: `[Desync][Diag]` (classification / layer) · `[Desync][Diag][DesyncLocal]` (local breakdown) · `[Desync][Diag][History]` (trajectory) · `[Desync][Probe]` (round-trip) · `[Desync][Verdict]` (verdict) · `[Desync][Verdict][src=client-reported unverified]` (server-received).
