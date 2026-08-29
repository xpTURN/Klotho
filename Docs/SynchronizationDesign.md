# Klotho Synchronization — Design Direction

> A design-rationale companion to [Specification.md](Specification.md).
> Where the specification answers *"what does the engine do and what are the exact APIs/defaults?"*, this document answers *"why is the synchronization built this way, and what tensions did each decision resolve?"*

---

## 1. Scope and Intent

Klotho is a deterministic, tick-based multiplayer simulation engine. "Synchronization" here is not a single algorithm but a **layered system** whose job is to keep N independently-running simulations producing byte-identical state, while hiding network latency from the player.

This document explains the *design direction* of that system:

- the **invariant** everything else is built on (determinism),
- the **central abstraction** that organizes the whole engine (the two-chain model: *Verified* vs *Predicted*),
- the four problem domains the design carves the work into — **predict, time, verify, recover** — and why each is shaped the way it is,
- how the two network modes (**P2P lockstep** and **Server-Driven**) reuse the same core machinery under different authority assumptions, and
- the **trade-offs and non-goals** that bound the design.

Exact API signatures, message layouts, and default constants live in [Specification.md](Specification.md); this document references them rather than restating them.

---

## 2. Design Goals and the Tensions Between Them

The synchronization design is the negotiated settlement of four goals that actively conflict:

| Goal | Pulls the design toward… | …and fights against |
| --- | --- | --- |
| **Responsiveness** (input feels instant) | running the simulation *now*, before remote input arrives | correctness — you're guessing |
| **Correctness** (everyone agrees on the world) | waiting for confirmed input, comparing hashes | responsiveness — waiting is lag |
| **Minimal bandwidth** (cost, scale) | sending *inputs only*, never world state | recoverability — you can't just re-send state when something goes wrong |
| **Determinism** (same input → same result) | fixed-point math, fixed iteration order, no wall-clock in logic | ease of authoring — floats and `DateTime` are forbidden in the sim |

No single point satisfies all four. Klotho's resolution is:

1. Make **determinism** a hard, non-negotiable foundation (Section 3). Once it holds, *state is a pure function of inputs*, which makes everything above it cheap.
2. Buy **responsiveness** with **speculative execution** — predict remote input and run immediately (Section 5).
3. Pay for the occasional wrong guess with **rollback**, which is affordable *only because* determinism lets us recompute state from inputs (Section 6).
4. Keep **bandwidth** flat by transmitting inputs only; use that same property to make **verification** a tiny hash exchange rather than a state diff (Section 8).
5. Accept that determinism can still break (platform bug, packet corruption, late join) and build a **graded recovery ladder** as the safety net (Section 9).

The rest of the document is these five moves, expanded.

---

## 3. The Keystone: Determinism

Everything downstream assumes one property:

> Given the same ordered set of inputs, every peer computes byte-identical state, on every platform, every compiler, every run.

This is why the simulation excludes `float`/`double` and is built on `FP64` (32.32 fixed-point) plus a deterministic RNG (Xorshift128+). See [Specification.md §8](Specification.md) for the math layer.

The design payoff of treating this as an *invariant* rather than a *best-effort* is structural:

- **State is reconstructible from inputs alone.** This is what makes rollback cheap (Section 6) and what lets the network carry inputs only (Section 8).
- **Verification reduces to equality of a hash.** If two peers ran the same inputs and got different hashes, the *only* possible cause is a determinism violation — there is no "acceptable drift." That sharpens desync into a binary, debuggable signal (Section 9).
- **The same binary runs on client and server.** The engine core is pure C# with no `UnityEngine` / Godot dependency, so the authoritative server and the client run *the identical simulation* — there is no second implementation to keep in sync. This is a synchronization decision disguised as an architecture decision: it removes an entire class of client/server divergence by construction.

Because determinism is load-bearing, the engine ships a **determinism validator** ([SyncTestRunner.cs](com.xpturn.klotho/Runtime/Core/Sync/SyncTestRunner.cs)) that, every few ticks, rolls back and re-simulates the same inputs and asserts the hash is unchanged. This catches non-determinism in development *before* it reaches the network as a desync (Section 9.5).

---

## 4. The Central Abstraction: Two Chains

The single most important idea in the engine's organization is that each peer maintains **two timelines over the same tick axis**:

- **Verified chain** — ticks for which *all* participating players' real inputs are known. State here is authoritative and will never change. Tracked by `_lastVerifiedTick`.
- **Predicted chain** — ticks from `_lastVerifiedTick + 1` up to `CurrentTick`, executed using a mix of real local input and *guessed* remote input. State here is provisional and may be rewound.

```text
  tick:  … 97   98   99  │ 100  101  102  103
          ───────────────┼────────────────────►
         ◄── Verified ───┤◄──── Predicted ────►
            (immutable)  │   (speculative, rollback-able)
                         │
              _lastVerifiedTick = 99
                                          CurrentTick = 103
```

This split is the organizing principle for nearly every subsystem:

| Subsystem | Verified-chain behavior | Predicted-chain behavior |
| --- | --- | --- |
| **Execution** | `ExecuteTick` with confirmed inputs | `ExecuteTickWithPrediction` with guessed inputs |
| **Events** (Section 10) | fire as *Confirmed* / *Synced* (once, durable) | fire as *Predicted* (cosmetic, retractable) |
| **Snapshots** | not needed (won't rewind) | every tick saved for rollback |
| **Network verify** | hash compared against peers | not yet hashed |

The chain boundary advances *continuously and without gaps* via `TryAdvanceVerifiedChain` ([KlothoEngine.FrameVerification.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.FrameVerification.cs)): a tick is promoted to verified only when the input buffer holds every active player's command for it. The moment a gap is found, advancement stops and `OnChainAdvanceBreak` fires. This makes "are we falling behind?" an observable, first-class signal rather than something inferred later.

**Why two chains instead of one "best guess" timeline?** Because the two have fundamentally different *contracts*. Verified state can be acted on irreversibly (award a kill, spend a resource, broadcast to spectators). Predicted state must be retractable. Conflating them would force every consumer to reason about retraction; separating them lets the engine give each consumer exactly the guarantee it needs (Section 10).

---

## 5. Speculative Execution — Why We Predict, and How

### 5.1 The CPU-pipeline analogy

The design borrows directly from CPU branch prediction (see [Specification.md §"Speculative Execution"](Specification.md)):

| CPU pipeline | Klotho |
| --- | --- |
| predict branch outcome | predict remote player's input |
| advance pipeline speculatively | advance simulation on predicted input |
| prediction hit → commit | hit → no rollback |
| prediction miss → flush + re-execute | miss → snapshot restore + re-simulate |

The design bet is the same bet CPUs make: **misprediction is rare enough that paying its full cost is cheaper than always waiting.** In a networked game, "waiting" means input lag on *every* frame; "rollback" means a recompute only on the frames where a guess was wrong.

### 5.2 The prediction model: repeat the last continuous input

Klotho's predictor (`SimpleInputPredictor` in [Input/Impl](com.xpturn.klotho/Runtime/Input/Impl)) deliberately uses the **simplest model that works**: for a missing remote input, replay that player's most recent *continuous* input (movement, aim, fire-held), scanning backward for that player's most recent 5 commands (`PREDICTION_HISTORY_COUNT`; the scan is bounded by the input buffer's retention and the rollback window, not by a fixed tick distance).

Two design choices are doing the work here:

1. **Continuous vs one-shot.** Continuous inputs (holding a direction) have high frame-to-frame autocorrelation — "what they did last tick" is an excellent predictor of "what they do this tick." One-shot inputs (spawn, skill, jump) do *not*; guessing them produces a spawned-then-un-spawned flicker on every miss. So one-shots are **never predicted** — the predictor emits an empty command for them and lets the real input arrive. This concentrates rollbacks on the cheap, common case and avoids them on the expensive, jarring case. There is also an opt-in **reliable command channel** for latency-insensitive one-shots — spawn, purchase, surrender — marked `IReliableCommand`: in ServerDriven the authority assigns the execution tick and the command rides the confirmed-input list rather than the predicted input slot, so it lands exactly once without prediction; P2P retains the legacy `IssueOnce` path. See [Specification.md](Specification.md) API §3.4.

2. **Clone by serialization.** A predicted command is produced by serializing and deserializing the historical one, so the predicted input is *byte-identical* to a real input of the same shape. This matters because mismatch detection (Section 6.1) is itself a byte comparison — the predictor and the verifier speak the same representation, so "did the guess match reality?" is exact, not heuristic.

The predictor also tracks a running hit rate — 1 − (fraction of arrived predictions that forced a rollback) — exposed read-only as `IKlothoEngine.PredictionAccuracy` for diagnostics. It is a **P2P-only, sampled** metric: its sole feed is the P2P command receive path (SD re-prediction is not counted), and predictions cleared by a rollback before their real input arrived are not counted. *Sustained* low accuracy is a signal that the timing buffers are too thin, not that the model is wrong.

**Why not a smarter predictor (velocity extrapolation, ML, etc.)?** Because a smarter predictor that is ever *wrong in a new way* still triggers the same rollback machinery, while adding state that itself must be deterministic and rolled back. "Repeat last input" is wrong predictably and recovers identically every time — it composes cleanly with rollback. Sophistication was spent on *recovery correctness*, not *guess cleverness*.

### 5.3 Prediction and the event ring under a deep stall

P2P prediction is deliberately *unbounded* — a stalled peer does not stop the local head from advancing speculatively (this is what keeps the local player responsive, and what the recovery ladder relies on to keep producing local input). The SD client throttles its lead at `hardLimit` only because the server is its sole authority; the P2P head does not.

One consequence: the snapshot and event rings hold a fixed `MaxRollbackTicks + 2` ticks (Section 6). If the verified chain stalls and the prediction head runs more than that far ahead, tick `T` and tick `T + capacity` alias the same event-ring slot. The slot can only hold one of them, so the earlier tick's events are overwritten. The loss that matters is a *pending* (not-yet-dispatched) **Synced** event — Regular events were already fired speculatively as `OnEventPredicted`, so their loss is cosmetic; the dev-build guard `WarnIfReclaimingPendingSynced` therefore surfaces only the Synced case. This *under-fire* is repaired by rebuilding state on a stall that deep: a wiped match-end is recovered by the fire-forward backstop (Section 10, state-based, not event-based), and any other pending Synced by full-state resync. What must **not** happen is the slot's (later-tick) events being dispatched under the earlier tick's identity: that would fire a Synced event at the wrong tick and again at its real tick, breaking exactly-once. The Synced dispatch path guards against this by matching each event's own stamped `Tick` against the dispatch tick — a wrapped slot's events are skipped at the wrong tick and fire exactly once at their real tick.

---

## 6. Rollback — The Pipeline Flush

When a real remote input arrives and disagrees with what was predicted, the speculative chain from that tick onward is invalid and must be recomputed.

### 6.1 Detecting the miss

Predicted commands are held in `_pendingCommands`. When the real command for `(tick, playerId)` arrives, it is byte-compared against the prediction. Equal → the guess was right, nothing happens (the common case, and it costs only a comparison). Different → `RequestRollback(tick)` is queued. See [KlothoEngine.Rollback.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.Rollback.cs).

### 6.2 Deferral and merge — rollback once per frame, to the earliest point

Rollbacks are **not** executed the instant a mismatch is found. They are recorded (`_hasPendingRollback`, keeping the *minimum* requested tick) and flushed once at end of frame:

```text
multiple mismatches this frame  →  keep the earliest target  →  one rollback covers them all
```

The rationale is twofold:

- **Correctness:** executing a rollback mid-tick-loop would invalidate state the loop is still iterating over. Deferral gives a clean boundary.
- **Efficiency:** several late packets in one frame often span overlapping ranges. Rolling back once to the earliest tick re-simulates the union in a single pass instead of thrashing.

### 6.3 Re-simulation reuses the *same* prediction path

After restoring the snapshot at the resolved tick, re-simulation from there to `CurrentTick` runs the **identical** "real-where-known, predict-where-missing" logic as the original forward pass. This is deliberate: the corrected run must be reproducible by any peer that later receives the same inputs. There is no special "rollback mode" of the simulation — there is only *the* simulation, run again with better information.

### 6.4 Snapshot ring buffer — sizing as a design statement

Snapshots are owned by the simulation: the ECS frame ring ([FrameRingBuffer.cs](com.xpturn.klotho/Runtime/ECS/Snapshot/FrameRingBuffer.cs)) holds `MaxRollbackTicks + 2` pre-tick frames (`ISimulationConfig.GetSnapshotCapacity()`); the engine stores no simulation snapshots itself and resolves rollback targets by querying `ISimulation.GetNearestRollbackTick`. The `+2` is headroom for the one-tick prediction lead and event-diff timing. The fixed capacity is a *deliberate bound*: it caps memory, and — more importantly — it makes "how far back can we recover by rollback alone?" a known, finite number (`MaxRollbackTicks`, default 50). Anything that would need to reach further is *by design* escalated to a different recovery tier (Section 9), rather than silently growing buffers without limit.

This is the recurring shape of the design: **bound the cheap mechanism, and escalate past its edge to a more expensive one**, rather than making any one mechanism handle everything.

---

## 7. Timing — Making Predictions Land

Prediction and rollback handle *content* (what the input was). A separate problem is *timing*: getting remote inputs to arrive close to when they're needed, and keeping the local render smooth despite jitter. This is split into three cooperating mechanisms.

### 7.1 Static input delay — the baseline buffer

Local input is stamped for a *future* tick: `targetTick = CurrentTick + InputDelayTicks (+ extra)`. With defaults (`TickIntervalMs = 25`, `InputDelayTicks = 4`) this is a 100 ms head start. The design intent: if the buffer covers the round-trip, remote inputs arrive *before* their execution tick and **no prediction or rollback is needed at all**. Input delay is the *first* line of defense; prediction is what catches whatever the delay didn't cover. Bandwidth note: because only inputs travel the wire, this buffer costs latency-hiding, not bytes.

### 7.2 Clock synchronization — a shared time origin

Peers establish a **shared epoch** at handshake and a **per-peer offset** to the host clock (host offset = 0), in [SharedTimeClock.cs](com.xpturn.klotho/Runtime/Network/SharedTimeClock.cs). The offset is calibrated **once at handshake** from the minimum-RTT sample and held fixed for the match (the shared clock carries no continuous mid-match drift correction); ongoing timing adaptation rides on the frame-advantage exchange below, not on re-synchronizing the shared origin.

On top of this, frame-advantage exchange ([KlothoEngine.TimeSync.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.TimeSync.cs)) computes how far ahead/behind the local tick is versus peers. Two robustness choices stand out:

- **Median, not mean**, over remote ticks — one delayed packet shouldn't yank the estimate. The even-count median averages the two middle values (no upper-value bias that under-throttled a 3-player match, whose full roster excludes the local player and so presents an even remote set; runtime filtering of disconnected/stale peers can still leave an odd count, which the median also handles).
- **Staleness gate at `MaxRollbackTicks`** — remote ticks older than the rollback window are discarded, so the system never chases ancient peer state.
- **Exchanged advantage, not a self-mirror** — each peer piggybacks its *own measured* advantage on `CommandMessage` (`SenderAdvantage`); the receiver feeds the negated remote value into the throttle (`AdvanceFrame(-localAdv, -recvRemoteAdv)`), recovering GGPO's `(local−remote)/2` one-way-delay cancellation instead of synthesizing a mirror that double-counts the local estimate. A peer that hasn't reported yet falls back to the mirror (regression-safe). The advantage is computed outside the timesync-enabled guard, so a throttle-disabled (late-join/reconnect) guest still reports truthfully to the host.
- **Sender-machine timing attribution** — a command the host sends *on behalf of* a disconnected/catching-up player carries `IsProxyTiming`; receivers skip the timing vote for it, so a proxy fill never records the host's tick as that player's (the wire-side bias). The proxy fill still supplies the *empty command* the slot needs for chain-advance — only the timing attribution is dropped.
- **Proactive disconnected-peer fill** — the host fills a disconnected player's slots up to the input frontier (`CurrentTick + InputDelay + RecommendedExtraDelay`), the same lead its own input lands at, instead of only the current tick; the verified chain no longer trails by one tick and the per-tick `ChainBreak` spam during a disconnect window disappears.

### 7.3 Adaptive timing — react to conditions, don't over-provision

Timing buffers face the classic tension: too small → late inputs and rollbacks; too large → constant input lag. Klotho's answer is to **adapt** rather than pick a fixed conservative value.

**Recommended extra delay** ([RecommendedExtraDelayCalculator.cs](com.xpturn.klotho/Runtime/Core/RecommendedExtraDelayCalculator.cs)) derives a buffer from measured RTT:

```text
rttTicks   = ceil(min(avgRtt, RttSanityMaxMs) / TickIntervalMs)
extraDelay = clamp(rttTicks + safety, 0, MaxRollbackTicks / 2)
```

- **`RttSanityMaxMs` (240 ms) cap:** a single retransmit-inflated RTT sample must not balloon everyone's input lag.
- **`safety` margin (`LateJoinDelaySafety`, 2):** absorb variance below the mean; also the fallback when RTT is unmeasurable.
- **Clamp to `MaxRollbackTicks / 2`:** never recommend a delay so large it leaves no rollback headroom — the timing buffer and the rollback budget are explicitly kept from cannibalizing each other.

**Verified render clock** (`AdvanceVerifiedRenderTime` in [KlothoEngine.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.cs)) smooths *presentation* by advancing a dedicated verified-render timeline under drift-proportional continuous control:

```text
timescale = clamp(1 − drift × 0.1, 0.5, 2.0)   // drift in ticks vs. the verified head
|drift| > 10 ticks  →  snap                     // past that, gradual catch-up reads worse than a cut
```

Proportional control (rather than a stepped speed-up/slow-down) converges without oscillating, and keeping the verified render time decomposed separately from `_lastVerifiedTick` absorbs batch-arrival jumps — a verified batch advancing several ticks at once does not yank the view. One path serves P2P, SD, and replay alike. The interpolation buffer is the fixed `InterpolationDelayTicks` — there is no dynamic sizing from measured jitter.

**Reactive escalation** ([DynamicInputDelayPolicy.cs](com.xpturn.klotho/Runtime/Core/Engine/DynamicInputDelayPolicy.cs)) is the client-side fallback when push-based control lags. If past-tick rejections or rollback *bursts* accumulate within a sliding window, the client bumps its own recommended delay — but only after a **grace period** (defer to authoritative server pushes, which are lower-latency) and a **cooldown** (don't escalate every frame). Grace + cooldown exist specifically to prevent the escalation from oscillating against the very condition it's reacting to.

**Additive split + server authority.** The recommended extra delay is two components: a **baseline** (server-push authority, absolute) and a **reactive** correction (client-local, additive), with the public value being the clamped sum `clamp(baseline + reactive, 0, MaxRollbackTicks/2)`. This split is what lets a server push *not* wipe the client's reactive lead (the old single-value model overwrote it, causing permanent lag or oscillation). The reactive component is a **temporary fast-path**: the client reports its effective delay to the server/host (`ReactiveExtraDelayReportMessage`), which folds it into the authoritative baseline (`baseline = max(rttBased, reportedEffective)`). On an **UP push the client migrates** the now-covered reactive into baseline (drains reactive by the increase) so the effective value never ratchets or overshoots and authority transfers to the server; a **DOWN push preserves** reactive (a server RTT-improve must not cancel a locally-needed lead). A stable-interval **de-escalation** (`ReactiveDeEscalateStableTicks`) drains any residual reactive when no further instability is observed. The reactive *trigger* is mode-asymmetric by design: **SD = PastTick reject** (the server's reject feedback), **P2P = rollback-burst** — so the SD client's rollback path intentionally does not feed reactive escalation. P2P broadcasts the **max over peers** (one common delay = worst-case peer, never dragged down by a low-RTT peer).

The unifying philosophy across 7.3: **measure, then provision the minimum that survives observed jitter — and prefer the failure that's invisible (slightly early / slightly over-buffered) over the failure that's felt (late input / sudden hitch).**

---

## 8. Authority Models — One Core, Two Topologies

The two-chain machinery (Sections 4–6) is *mode-agnostic*. What differs between network modes is **who owns the verified chain** and **how inputs reach it**. The strategy seam (`IKlothoModeStrategy`, [IKlothoModeStrategy.cs](com.xpturn.klotho/Runtime/Core/IKlothoModeStrategy.cs)) covers session establishment — connection and handshake flow. Inside the tick loop the engine branches on the mode enum directly (`_simConfig.Mode`) for the few mode-specific paths (server input collection, SD client batch processing, P2P quorum). The speculative core stays honest where it matters: one prediction path and one rollback path are shared regardless of topology.

### 8.1 P2P Lockstep — distributed, equal authority

Every peer holds *equal* simulation authority; no server. Each peer broadcasts its own input and independently simulates the whole world. The verified chain advances locally as soon as all peers' inputs for a tick are in hand. Verification is **peer-to-peer hash comparison** (`SyncCheck`): everyone hashes, everyone compares, mismatch raises an event.

Design consequences that fall out of "no central authority":

- **The host is a sequencer, not an oracle.** Authority for *correctness* is the determinism invariant itself, not a machine. The host's special role is limited to operations that genuinely need a single decider (e.g., corrective reset, Section 9.4).
- **Host loss ends the session.** With equal authority and inputs-only transport, there is no authoritative state holder to fail over to. P2P treats this as a clean session-end rather than pretending to recover (reconnect is guest-only).
- **Watchdogs guard the edges.** Because there's no server to declare a player dropped, P2P adds watchdogs: a **host-side** quorum-miss timer (`QuorumMissDropTicks`, 20) that triggers reactive empty-fill before the transport even reports a disconnect (the disconnected-player pool is host-owned), and a chain-stall abort that ends a match the local peer can no longer make progress in — `MinStallAbortTicks` is the *floor* of that threshold, not the exact trigger point.

### 8.2 Server-Driven — centralized authority, client prediction

A server owns the authoritative simulation; clients predict locally and **validate against the server's broadcast hash**. The same two-chain engine runs on the client, but the verified chain is now *defined by the server*, not by local quorum.

Server side ([KlothoEngine.ServerDriven.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ServerDriven.cs), [ServerInputCollector.cs](com.xpturn.klotho/Runtime/Network/ServerInputCollector.cs)):

- Collects inputs per tick; the effective deadline is the tick's **execution moment** — inputs missing at execution are substituted with `EmptyCommand`, later arrivals are rejected as past-tick, and chronically late clients self-correct through lead escalation (client `DynamicInputDelayPolicy` + server recommended-delay push). This trades a straggler's fidelity for *fairness and fixed tick timing* — the server never waits on one slow client. (`HardToleranceMs` is deprecated and has no effect.)
- Executes, computes the authoritative hash, and **broadcasts `(tick, commands, hash)`** to all clients.

Client side ([KlothoEngine.ServerDrivenClient.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ServerDrivenClient.cs)):

- Predicts ahead of the server (lead ticks), under **soft- and hard-throttle** limits so it never predicts further than it can afford to roll back.
- On each batch of verified messages: rollback to the nearest snapshot, **re-simulate with the server's real inputs while checking each tick's hash against the server's**, then re-predict the tail. A hash mismatch here is, by the determinism invariant, a genuine divergence — and is escalated straight to a full-state request (Section 9).

The contrast captures the whole point of having one core serve both:

| | P2P Lockstep | Server-Driven |
| --- | --- | --- |
| Verified chain owned by | local quorum of all peers | the server |
| Hash role | peer cross-check, advisory | **authoritative gate**, critical path |
| Missing input | wait / predict / watchdog | server fills `EmptyCommand` at deadline |
| Failure of authority | session ends (host loss) | client reconnects; server persists |
| Scales with | small rosters (2–8) | server capacity, not player count |

Same prediction, same rollback, same snapshots — different answer to *"whose verified chain is the truth?"*

---

## 9. Recovery — The Escalation Ladder

Determinism *should* hold, and rollback *should* repair every misprediction. The recovery system exists for when one of those doesn't: a platform-specific math bug, a corrupted packet, a divergence deeper than the rollback window, a joiner who has no state at all. The design principle is a **graded ladder — cheapest mechanism first, escalate only on failure** — so the common case stays cheap and the rare case stays survivable.

```text
   detect        rung 1            rung 2              rung 3                 rung 4
  ┌───────┐   ┌──────────┐   ┌──────────────┐   ┌────────────────┐   ┌──────────────────┐
  │ hash  │ → │ rollback │ → │ full-state   │ → │ corrective     │ → │ abort match      │
  │ gate  │   │ to last  │   │ resync       │   │ reset          │   │ (give up cleanly)│
  │       │   │ matched  │   │ (request     │   │ (host forces   │   │                  │
  │       │   │ tick     │   │  fresh state)│   │  all to a tick)│   │                  │
  └───────┘   └──────────┘   └──────────────┘   └────────────────┘   └──────────────────┘
   cheap ──────────────────────────────────────────────────────────────────► last resort
```

### 9.1 The hash gate (detection)

Every `SyncCheckInterval` (20; runtime-clamped to at most `MaxRollbackTicks / 2`) ticks, peers hash state and exchange it. Anchor promotion is **event-based**: every completed comparison reports `(tick, remotePlayerId, matched)`. A matched comparison promotes the tick monotonically to `_lastMatchedSyncTick` — a known-good rollback anchor (the previous anchor is kept as a 1-step history) — and clears that peer's consecutive-desync count; a mismatch permanently vetoes that tick. The veto is **order-independent**: a host seeing both a match and a mismatch for the same tick never anchors on it regardless of arrival order — a mismatch arriving after a same-tick match *demotes* the anchor to the prior matched tick before rung-1 reads it (and rung-1 is skipped outright when no known-good tick precedes the divergence). The consecutive-desync count is **per-peer**: a match with one peer no longer clears another peer's accumulation, so a 3+-player partial desync (one peer persistently diverging while another keeps matching) still reaches the resync threshold instead of oscillating below it. The *absence* of comparisons simply skips promotion. Hashes are sent unreliable: a dropped hash skips a check, it doesn't stall anything. Check ticks defer their hash send until the tick verifies — whether executed speculatively, or executed directly while the verified chain is still behind or a rollback is pending.

### 9.2 Rung 1 — rollback to last matched (cheapest)

On a detected desync, the first response is a normal rollback to the last *matched* sync tick — reusing the exact machinery of Section 6. If the divergence was a recoverable timing artifact, this fixes it for free. Consecutive desyncs are counted.

### 9.3 Rung 2 — full-state resync (when rollback can't reach)

When desyncs persist past `DesyncThresholdForResync` (3), or a rollback target falls outside the snapshot window, the engine requests a **full state transfer** ([KlothoEngine.FullStateResync.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.FullStateResync.cs)). This is the one place inputs-only is *intentionally* violated — and it's bounded to the rare case, which is why violating it is acceptable. Design details that matter:

- **The provider caches serialized state per tick**, so repeated requests for the same tick don't re-serialize. The state is serialized from the provider's **`CurrentTick`** — the prediction head (verified on a P2P host / SD server, but speculatively predicted on a guest that serves as provider); the receiver re-simulates forward from it with its own inputs and diffs against the provider's hash to surface any residual divergence.
- **Apply is hash-verified on arrival.** The receiver restores, recomputes its own hash, and compares to the sender's. A mismatch *after* a full-state apply is logged with per-component hashes — this is the deepest diagnostic, because it means the divergence is in the state representation itself.
- **A retreat guard** forbids applying a full state *older* than the current verified tick, except for the few reasons that legitimately rewind (corrective reset, late join, initial state). This prevents resync from itself becoming a source of desync by moving a peer backward.
- **Escalation clears *all* per-peer desync counts**, not just the triggering peer's: a full-state resync is whole-engine recovery, so per-peer counts accumulated against the pre-resync local state are stale and a sibling's residual count must not immediately re-trip the threshold (resync-storm prevention). Counts re-accumulate at the sync-check cadence (`SyncCheckInterval`), not per tick; a resync whose apply still hash-mismatches escalates immediately via the post-apply path rather than waiting out that cadence.
- Full-state has a **timeout with auto-retry** up to `ResyncMaxRetries`; exhausting it raises `OnResyncFailed`. The SD client mirrors this independently: its FullState request (`_fullStateRequestPending`) re-arms on `RESYNC_TIMEOUT_MS` and resends up to `ResyncMaxRetries`, then terminates locally (`OnResyncFailed` + `AbortMatch` under `AutoAbortOnRecoveryExhausted`) — no host rung-3 hand-off since the server is authoritative. An SD resync apply returning `HashMismatch` (restored but non-deterministically-deserialized = unrecoverable) takes the same terminal.

### 9.4 Rung 3 — corrective reset (host forces consensus)

If divergence persists even after resync, the **host** (P2P) broadcasts its state and forces every peer — including itself — to a common tick. By design the host never self-escalates (it holds no desync counter against itself); rungs 3–4 are driven exclusively by **guest failure reports** (`ResyncFailureReport`: a post-apply hash mismatch, or local resync retries exhausted), so in a 3+ peer match consensus is host-owned rather than decided by majority vote. Each report spends one corrective-reset attempt from a host-side budget (`CorrectiveResetMaxAttempts`, 2); a **cooldown** (`CorrectiveResetCooldownMs`, 5000) prevents broadcast storms — cooldown-suppressed resets don't consume attempts. When the budget is exhausted, a report whose divergence tick **predates the latest corrective reset** is stale (it was in flight before the guest could apply that reset) and is absorbed rather than aborting; only a report at/after the reset tick — the guest applied the reset and still diverges — fires the abort. The budget decays back to zero after a quiet period of `max(CorrectiveResetCooldownMs × 2, ResyncMaxRetries × RESYNC_TIMEOUT_MS + CorrectiveResetCooldownMs)` — derived to exceed the worst-case `RetryExhausted` report cadence so a timeout-type persistent failure still reaches rung 4, while isolated transient episodes never accumulate toward an abort. When the budget is exhausted, the host broadcasts `MatchAbort` and aborts locally with `StateDivergence` — rung 4 (opt-out via `AutoAbortOnRecoveryExhausted`). This is the only rung pair that uses centralized authority in P2P, reserved for "we cannot otherwise agree."

### 9.5 Out-of-band guards and the dev-time net

Two mechanisms sit beside the ladder rather than on it:

- **Static-geometry fingerprint.** Static colliders are *not* part of the per-tick state hash (they don't change), so a static-only divergence (e.g., a mismatched map asset) would otherwise stay invisible until it perturbed dynamic state several ticks later. A separate fingerprint surfaces it at the source — checked whenever a full state crosses peers (SD initial/verified exchange; P2P resync and late join), not continuously. Known limit: mesh collider fingerprints do not hash vertex data.
- **SyncTestRunner** ([SyncTestRunner.cs](com.xpturn.klotho/Runtime/Core/Sync/SyncTestRunner.cs)). Development-build opt-in (`EnableSyncTest(checkDistance)`): **every tick**, forward-execute, roll back `checkDistance` (5) ticks, re-simulate the recorded inputs, and assert the hash is identical — per-tick cost ≈ 7× simulation at the default distance. Every-tick checking buys one-tick failure localization (the first failure pinpoints the newest tick in the window) and validates every tick from multiple restore anchors. **Not recommended in live network sessions**: the added tick-loop cost delays input send, injecting the very late-inputs-and-rollbacks being debugged — offline/local validation is the primary use. A failed check restores the forward-branch state (the diverged resim must not stay live) and auto-disables after 3 consecutive failures (`OnSyncTestDisabled`). This catches non-determinism *locally, without a network*, turning "a player desynced in production" into "a unit test failed on my machine." It is the design's acknowledgment that the cheapest place to catch a determinism bug is before it ships.

### 9.6 Why a ladder instead of "always resync"?

Always sending full state would be correct and simple — and would throw away the bandwidth and scale properties the whole design is built to protect. The ladder keeps the expensive, inputs-only-violating mechanism (resync) confined to the genuinely rare case, while the common recoverable case is handled by rollback, which costs nothing on the wire.

---

## 10. Event Semantics — Mapping Chains to Side Effects

A simulation tick produces not just state but *events* (a hit landed, a sound should play, a score changed). The two-chain model dictates *when* an event may be acted on, because an event born on the predicted chain might never have really happened.

The design splits events by their **durability requirement**:

- **Regular events** (cosmetic, local: a muzzle flash, a footstep). These may fire **speculatively** on the predicted chain as `OnEventPredicted`. If a rollback later erases them, the engine emits `OnEventCanceled`. Players tolerate a rarely-retracted cosmetic far better than they tolerate input lag, so these ride the predicted chain.
- **Synced events** (game-critical, possibly networked/scored). These are **buffered, not fired**, while predicted. They emit only when their tick crosses into the verified chain, as `OnSyncedEvent` — exactly once.

The "exactly once" is enforced by a **high-water mark** (`_syncedDispatchHighWaterMark`). Rollback rewinds `_lastVerifiedTick` and then re-advances it over ticks that may have already dispatched their synced events; the high-water mark ensures a given tick's synced events never fire twice. (Full-state resync, which wipes the event buffer, resets the mark accordingly.) A resync re-simulation follows the same delta contract as a rollback: Regular events are cleared and regenerated, observable only through the `OnEventCanceled`/`OnEventConfirmed`/`OnEventPredicted` deltas below, while Synced events stay buffered and fire only when their tick is promoted into the verified chain.

After a rollback, the engine doesn't blindly re-fire everything — it **diffs** old vs new event sets for the re-simulated range and emits only the deltas: `OnEventCanceled` for events that no longer occur, `OnEventConfirmed`/`OnEventPredicted` for newly-correct ones, compared by tick + type + content hash. Game code thus receives a precise "what changed because the prediction was wrong" stream rather than a duplicate flood.

One edge is reported rather than repaired: a desync-recovery rollback that lands *below* the dispatched watermark may change Synced history that already fired. Nothing re-fires (the exactly-once invariant holds); instead `OnSyncedEventDivergence` reports each `Added`/`Removed` Synced event in that region — the engine cannot revert external side effects (a reported score, a posted result), so notification is the only honest response. Sole exception: an Added match-end with no prior `OnMatchEnded` fires it normally so the match can still end.

Match-end specifically is a **one-way notification latch: fire-forward, never un-fire**. *Fire-forward* — `OnMatchEnded` fires whenever a corrected/restored timeline is terminal but the latch is unset: not only the Added-divergence case above, but also a resync/`FullState` restore into an ended state (the fire-forward backstop, recovering a match-end Synced event lost to an event-ring wipe under a deep stall or skipped by a forward-jump buffer clear). The engine reads the simulation's *state* (`ISimulation.IsMatchEndedState`, backed by a deterministic match-end component that round-trips through full state and rolls back with snapshots) rather than only the event, so a lost event still ends the match. *Never un-fire* — a Removed match-end is notification-only; the already-run end flow (UI, result) is not reverted and `IsMatchEnded` stays set. Reverting is the game's `AbortMatch` decision, not automatic. The Pause-grace `StopCommand` injection follows the *current* terminal state (`IsMatchEndedState`, gated additionally by the verified-timed latch to avoid injecting during prediction), so a rollback that un-ends the match stops the injection and the engine resumes — without un-firing the latch.

The design direction here: **let each event consumer subscribe to the guarantee it actually needs** — retractable-but-instant for cosmetics, durable-and-once for game state — instead of forcing one timing policy on all of them.

---

## 11. Error Correction — Hiding the Seam of a Rollback

A rollback can teleport a remote entity when the corrected position differs from the predicted one. Snapping is jarring. Error correction ([KlothoEngine.ErrorCorrection.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ErrorCorrection.cs), filter thresholds in [ErrorCorrectionSettings.cs](com.xpturn.klotho/Runtime/Core/ErrorCorrectionSettings.cs)) computes the visual delta between pre- and post-rollback transforms and lets the **view layer** decay it smoothly, while the **simulation** stays exactly on the corrected (authoritative) value.

This is a deliberate **separation of correctness from presentation**: the simulation is never allowed to lie for the sake of smoothness — only the rendered transform is interpolated. Thresholds encode intent:

- below `PosMinCorrection` / `RotMinCorrectionDeg`: ignore (don't jitter the view over sub-perceptible noise),
- above the view's teleport thresholds (`PosTeleportDistance` / `RotTeleportDeg`): snap (a genuine teleport *should* look like one — smoothing it would read as a glide through a wall),
- in between: exponential decay at a rate that scales with error magnitude (`MinRate`→`MaxRate`).

**Only the first bullet's pair is the engine's.** `ErrorCorrectionSettings` is a delta *filter* and carries exactly `PosMinCorrection` and `RotMinCorrectionDeg`; everything the other two bullets name — accumulation, variable-rate decay, zero-snap, teleport-snap, smoothing — belongs to the view and is authored per view on `ErrorVisualState` (Inspector / prefab), not per engine.

It is **off by default** (`EnableErrorCorrection = false`): it costs per-entity work and only earns its keep under latency high enough to make rollback corrections visible. The design exposes it as a knob rather than baking it in — consistent with the "provision the minimum the conditions require" theme of Section 7.

**And the flag alone does nothing.** The engine produces a delta only for entities carrying `ErrorCorrectionTargetComponent`, which the *game's* simulation code adds, so turning the config flag on without marking anything leaves the whole path inert. See [SimulationConfigGuide §1.1](SimulationConfigGuide.md#11-enabling-error-correction--three-touches) for the three touches it takes (the flag is authority-owned, which entities to mark, and what the marker does and does not travel with).

Where it takes effect follows the **render path**, not the topology. Only views rendered from the predicted (CSP) frames apply the delta; snapshot-interpolated views skip it, because interpolating between two verified frames already shows the corrected state and adding the offset on top would double-correct. So the knob covers the local player under SD, everything under P2P, and nothing at all for a spectator — whose every view is snapshot-interpolated. The delta *computation* used to be wired for the SD client only, on the reasoning that P2P stores real remote input in the buffer — which holds while that input arrives inside the input-delay window and stops holding at exactly the latencies that justify this feature. It is now wired for both, by making "a rollback runs this frame" a caller-supplied argument rather than a peek at the SD client's batch queue: each mode expresses that condition differently, and a function that knows one of them silently disables the others.

A **FullState resync** feeds the same path. The restore replaces the predicted pose with the authoritative one, so the jump is the prediction error at that instant — typically centimetres, well inside the band the smoother exists for, and not a function of what triggered the resync. Both the P2P and SD-client resync handlers publish their deltas to the view; the bootstrap and late-join applies do not, because there is no local pose to diff against.

---

## 12. Resource Discipline as a Synchronization Concern

Zero-GC is listed as a general engine goal, but it is *specifically* a synchronization concern: a GC spike stalls the tick loop, which delays input send/receive, which causes late inputs, which causes rollbacks. Allocation hitches and desync pressure are the same problem viewed from two angles.

Hence the hot paths — `ExecuteTick`, prediction, rollback re-simulation, event diff — are built on object pools, reusable caches, `stackalloc` for command (de)serialization, and fixed-capacity ring buffers (snapshots, events, hash history). The buffers are sized to the rollback window. The input and hash-history buffers are **actively trimmed** (`CleanupOldData`) to `CurrentTick − MaxRollbackTicks − margin`, never ahead of `_lastVerifiedTick`; the event ring is *not* trimmed there but **self-cleans at execution time** as slots are reused (`ClearTick` on wrap) — a nominal-range trim once wiped live events still inside the rollback window. The cleanup margin (10) is the same kind of deliberate headroom as the snapshot ring's `+2`: leave enough that an edge-case rollback or a late event doesn't fall off the end, but bound it so memory and time stay flat over an arbitrarily long match.

---

## 13. The Design Knobs and What They Trade

The configuration surface (full defaults in [Specification.md §2.2](Specification.md)) is best read as a set of **trade-off dials**, each moving one of the Section-2 tensions:

| Knob | Default | Larger value buys… | …at the cost of |
| --- | --- | --- | --- |
| `InputDelayTicks` | 4 | fewer predictions/rollbacks (inputs arrive in time) | more felt input latency |
| `MaxRollbackTicks` | 50 | recover deeper divergences by rollback before resync | more snapshot memory |
| `SyncCheckInterval` | 20 | less hashing/bandwidth | slower desync detection (runtime-clamped to ≤ `MaxRollbackTicks / 2` so the first desync rollback stays inside the rollback window) |
| `UsePrediction` | true | responsiveness | rollback cost (false ⇒ wait-for-input, pure lockstep) |
| `RttSanityMaxMs` | 240 | tolerate genuinely high-RTT links | risk of outliers inflating delay |
| `DesyncThresholdForResync` | 3 | tolerate transient desyncs before the expensive resync | longer visible divergence |
| `InterpolationDelayTicks` | 3 | smoother view under jitter | more presentation latency |
| `EnableErrorCorrection` | false | hide rollback snaps under high latency | per-entity view-layer work |
| `HardToleranceMs` (SD) | — | *deprecated, no effect* | the effective server deadline is the tick's execution moment (late ⇒ past-tick reject + `EmptyCommand`; chronic lateness self-corrects via lead escalation) |

The defaults target small-roster real-time PvP at 40 ticks/s; the dials let a turn-based, high-latency, or large-room title re-balance the same machinery.

---

## 14. Trade-offs and Non-Goals

Stating what the design *deliberately does not do* is as important as what it does:

- **Not interest-managed / not state-replicated.** Because only inputs travel, every peer simulates the *entire* world. This is what keeps bandwidth independent of entity count — and it means the design does **not** target massive open worlds where each client should see only a slice. It targets bounded, fully-shared simulations (fighting, RTS, MOBA, tactics, auto-battlers).
- **Not float-friendly.** The determinism invariant forbids `float`/`double` in simulation logic. Authoring cost (fixed-point math, deterministic RNG) is paid up front in exchange for cross-platform reproducibility.
- **Prediction is intentionally dumb.** "Repeat last input" was chosen over cleverer extrapolation because it composes cleanly with rollback (Section 5.2). Smartness was invested in *recovery*, not *guessing*.
- **P2P does not fail over.** Equal authority + inputs-only means there is no authoritative state holder to promote on host loss; P2P ends the session cleanly rather than feigning recovery. Persistence-across-disconnect is a Server-Driven property by design.
- **Bounded recovery, then graceful surrender.** The ladder (Section 9) ends in `AbortMatch`. The design would rather end a match honestly than mask an unrecoverable divergence and let peers drift apart silently.

Each non-goal is the shadow of a goal: the same decisions that make Klotho excellent for deterministic, bandwidth-flat, small-roster real-time play are what make it the wrong tool for replicated, float-heavy, massive-scale worlds. The synchronization design is coherent precisely because it refuses to be all of those at once.

---

## See Also

- [Specification.md](Specification.md) — exact APIs, message types, defaults, state machine
- [FEATURES.md](FEATURES.md) — feature-level overview
- Source of truth: [com.xpturn.klotho/Runtime/Core/Engine/](com.xpturn.klotho/Runtime/Core/Engine/) (the `KlothoEngine.*.cs` partials), [Runtime/Core/Clock/](com.xpturn.klotho/Runtime/Core/Clock/), [Runtime/Network/](com.xpturn.klotho/Runtime/Network/)
</content>
</invoke>
