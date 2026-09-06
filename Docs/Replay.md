# Replay

A Klotho replay stores **inputs, not state**. Because the simulation is deterministic — the same ordered commands fed to the same engine produce byte-identical state on every tick — a replay only needs to record the per-tick command stream plus a starting point. Playback re-runs the real simulation, feeding those recorded commands back in, and the entire match reconstructs exactly: same positions, same RNG rolls, same outcomes. This is the same machinery that powers rollback, turned to a different purpose, and it makes replays tiny (inputs only) and perfectly faithful.

> Audience: game developers adding match recording, playback UI, or replay-based debugging.
> Goal: record a session, save/load a replay file, drive playback (play / pause / seek / speed), and understand the determinism guarantees that make it work.
>
> Related: [SynchronizationDesign.md](SynchronizationDesign.md) (determinism = inputs only) · [ECS.md](ECS.md) (`SerializeFullState` for the initial snapshot) · [Specification.md](Specification.md) §10 (state machine + file format) · [Samples/Brawler.md](Samples/Brawler.md) (replay in a real sample)

---

## 1. The Core Idea — Record Inputs, Replay the Simulation

A replay file contains:

1. **Metadata** — `SimulationConfig`, the RNG seed, player count, tick interval, total ticks, timestamps, and optional game-defined custom data.
2. **An initial full-state snapshot** — the `EcsSimulation` state at tick 0 (so playback starts from the exact same world).
3. **The per-tick command stream** — every command that was applied, grouped by tick.

That's it — no per-frame state, no positions, no animation data. On playback the engine seeds the RNG from the metadata, restores the initial snapshot, then ticks the simulation forward feeding each tick's recorded commands. Determinism guarantees the reconstructed state matches the original bit-for-bit.

> **Why this matters:** a one-hour match is kilobytes of inputs, not gigabytes of state. And a replay is a perfect bug-repro — if a desync or gameplay bug happened live, it happens again identically under playback, where you can pause, seek, and inspect.

---

## 2. File Layout

```text
com.xpturn.klotho/Runtime/Replay/
├── IReplaySystem.cs          # IReplayRecorder / IReplayPlayer / IReplaySystem + enums + IReplayMetadata / IReplayData
├── ReplayLoadException.cs    # thrown by LoadFromFile on any load failure
└── Impl/
    ├── ReplaySystem.cs       # unified recorder + player + file I/O
    ├── ReplayRecorder.cs     # StartRecording / RecordTick / StopRecording
    ├── ReplayPlayer.cs       # Play / Pause / Resume / Stop / Seek / Step
    └── ReplayData.cs         # metadata + per-tick command serialization
```

The engine owns a `ReplaySystem` instance, reachable as `KlothoEngine.ReplaySystem` (`IReplaySystem`). You normally drive replay through the engine's wrapper methods (below) rather than touching `ReplaySystem` directly.

---

## 3. Interfaces & State

`IReplaySystem` unifies two roles:

- **`IReplayRecorder`** — `StartRecording(playerCount, simConfig, randomSeed)` · `RecordTick(tick, commands)` · `StopRecording(totalTicks, ReplayEndReason reason = Normal) → IReplayData`, plus `OnRecordingStarted` / `OnRecordingStopped`.
- **`IReplayPlayer`** — `Load` · `Play` / `Pause` / `Resume` / `Stop` · `SeekToTick` / `SeekToProgress` · `GetCurrentTickCommands` · `Update(deltaTime)` · `Speed` · `Progress` / `Accumulator`, plus `OnTickPlayed` / `OnPlaybackFinished` / `OnSeekCompleted`.

`IReplaySystem` adds file I/O (`SaveToFile` / `LoadFromFile`), the `IsRecording` / `IsPlaying` flags,
`CurrentReplayData`, and the setters the engine fills before a recording is closed:
`SetGameCustomData`, `SetInitialStateSnapshot(snapshot, hash, tick)`, `SetInitialRoster`,
`SetInitialEntitlements` and `SetReproductionAnchors`. (The concrete `ReplaySystem` also exposes
`StepForward()` / `StepBackward()` / `CancelRecording()`, which are not on the interface.)

> **`CurrentReplayData` is null for the whole match.** It is assigned when a recording **stops** and
> when a file loads — never while ticks are being recorded. So it is not a "is this session
> recording?" signal; `IsRecording` is. This is also why saving has an ordering rule ([§4](#4-recording-engine-driven)).

**`ReplayState`** — `Idle → Recording → Idle`; `Idle → Playing ⇄ Paused → Finished → Idle`.

**`ReplaySpeed`** (enum value = multiplier × 100):

| Speed | Multiplier | Value |
| ---- | ---- | ---- |
| `Quarter` | 0.25× | 25 |
| `Half` | 0.5× | 50 |
| `Normal` | 1× (default) | 100 |
| `Double` | 2× | 200 |
| `Quadruple` | 4× | 400 |

**File format** — `SaveToFile` writes the payload uncompressed, self-framed by a leading `RPLY` magic (`0x52504C59`); `LoadFromFile` requires that magic and rejects anything else. Pass `dumpJson: true` to also write a human-readable `.json` debug dump beside the file (reflection-based — debug only, never on a runtime path).

### On-disk layout

```text
┌─ 4 ─────────────────────────────────────────────────────────────────────────┐
│ magic  'RPLY' = 0x52504C59                                                  │
├─ variable ──────────────────────────────────────────────────────────────────┤
│ Metadata                                                                    │
│   i32 Version · str SessionId · i64 RecordedAt · i64 DurationMs             │
│   i32 TotalTicks · i32 PlayerCount · i32 TickIntervalMs · i32 RandomSeed    │
│   ── SimulationConfig — restored on playback ─────────────────────────────  │
│   i32 InputDelayTicks · i32 MaxRollbackTicks · i32 SyncCheckInterval        │
│   b8  UsePrediction · i32 MaxEntities · i32 Mode                            │
│   i32 HardToleranceMs · i32 InputResendIntervalMs · i32 MaxUnackedInputs    │
│   i32 ServerSnapshotRetentionTicks · i32 EventDispatchWarnMs                │
│   i32 TickDriftWarnMultiplier                                               │
│   i32 StageId · blob MatchConfigData                                        │
│   ── the game's own slot ────────────────────────────────────────────────   │
│   blob GameCustomData                                                       │
│   ── the tick-0 world ───────────────────────────────────────────────────   │
│   blob InitialStateSnapshot                                                 │
│   ── reproduction anchors + completion marker (§7-1) ────────────────────   │
│   i64 LayoutFingerprint · i64 StaticColliderFingerprint                     │
│   i64 NavFingerprint · i64 GameFingerprint                                  │
│   i64 InitialStateHash · i32 InitialStateTick · i32 EndReason               │
│   ── tick-0 roster, and per-player verified data parallel to it ─────────   │
│   i32 n · i32 InitialRoster[n]                                              │
│   i32 m · i32 InitialEntitlementLengths[m] · blob InitialEntitlementData    │
│   ── layout inputs: recorded, never restored (§7-1) ─────────────────────   │
│   i32 p · i32 PrunedComponentTypeIds[p]                                     │
│   i32 q · i32 ComponentMaxCountTypeIds[q] · i32 ComponentMaxCountValues[q]  │
├─ 4 ─────────────────────────────────────────────────────────────────────────┤
│ i32 TickCount        how many entries the offset table has                  │
├─ 4 ─────────────────────────────────────────────────────────────────────────┤
│ i32 StreamSize       length in bytes of the command stream                  │
├─ StreamSize ────────────────────────────────────────────────────────────────┤
│ command stream — one chunk per recorded tick (next diagram)                 │
├─ TickCount × 12 ────────────────────────────────────────────────────────────┤
│ offset table:  i32 Tick · i32 Offset · i32 Length     × TickCount           │
└─────────────────────────────────────────────────────────────────────────────┘

blob = i32 length + that many bytes (length 0 ⇒ nothing follows)
str  = i32 UTF-8 byte count + those bytes      b8 = a single byte 0/1
every integer is little-endian; there is no padding and no per-section checksum
```

### The stream data chunk

The command stream is a single blob, and one tick's commands are the slice the offset table points at.
It is **not** self-framed: nothing inside it says where a tick begins or ends.

```text
one tick = stream[Offset .. Offset + Length)          ← reachable only through the table

┌─ 4 ──────────────────────────────────────────────────┐
│ i32 CommandCount                                     │
├──────────────────────────────────────────────────────┤
│  ┌─ 4 ─────────────────────────────────────────────┐ │
│  │ i32 Size      bytes in the record below         │ │
│  ├─────────────────────────────────────────────────┤ │  repeated
│  │ i32 CommandTypeId                               │ │  CommandCount
│  │ i32 PlayerId                                    │ │  times
│  │ i32 Tick                                        │ │
│  │ payload — Size − 12 bytes, per command type     │ │
│  └─────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

Four properties of the stream that a reader has to respect:

- **Read the table, never scan.** Chunk order is *recording* order. It usually comes out ascending — a
  quiet match writes each tick once, in order — but a rollback re-records ticks, so front-to-back scanning
  is right until the first correction and wrong after it.
- **The stream may contain dead bytes.** Re-recording a tick whose serialization is the *same length*
  overwrites in place — that is what keeps a rollback-heavy match from inflating the buffer. A re-record of
  a *different* length is appended instead, and the superseded bytes stay where they were. Only what the
  table points at is live, and `StreamSize` counts the dead bytes too.
- **`Tick` appears twice** — as the table's key and inside every command record. They agree; the record's
  own field is what the engine reads when it re-executes the command.
- **Trailing bytes are ignored, not rejected.** The loader reads exactly what the header describes and
  stops, so anything appended after the offset table is neither parsed nor refused. File length proves
  nothing about a replay; the anchors and the claim (§10) are what a reader can lean on.

For scale, one measured Brawler recording — 4,982 ticks, two players at tick 0 and a third joining at
tick 273 — is **783,682 bytes**: a ~1.9 KB metadata block (mostly the 1,511-byte tick-0 snapshot), a
721,962-byte stream at **145 bytes per tick**, and a 59,784-byte offset table. Per-tick cost scales with how
many players are issuing commands: the same sample measures **~102 B/tick** for a two-player P2P match and
**~145** for the three-player one above, so budget from your own roster size rather than from one number. Nothing is compressed on the
way out — gzip takes that file to 64,942 bytes (**12×**), so compression is worth doing wherever replays are
stored or uploaded, and the format deliberately leaves it to that layer.

---

## 4. Recording (engine-driven)

Recording is wired into `KlothoEngine` — you don't call `RecordTick` yourself. When a session starts with recording **enabled**, the engine:

1. calls `StartRecording(activePlayerCount, simConfig, randomSeed)` at boot,
2. calls `SetInitialStateSnapshot(snapshot, hash, tick)` with the tick-0 full state (**required** — playback throws `InvalidDataException` if it's missing) and records the reproduction anchors, the tick-0 roster and its per-player entitlements alongside it ([§7-1](#7-determinism-rules-must-read)),
3. records each tick's commands **at the moment that tick is confirmed (verified)** — P2P, Server-Driven, and late-join paths alike. Because recording is tied to *confirmation* rather than to execution, a tick whose inputs were corrected by a late-arriving command or a rollback is recorded with its **corrected** inputs, and only ticks that actually confirmed end up in the file (see *Recording length* below).

Your game's job is just to attach optional metadata and save the file:

```csharp
var engine = session.Engine;                 // or however you reach KlothoEngine

// (optional) attach game-defined metadata — call after recording has started
engine.ReplaySystem.SetGameCustomData(myHeaderBytes);

// when the match ends: STOP first, then save — the file is assembled by StopRecording
engine.Stop();                                    // closes the recording (KlothoSession.Stop does this)
engine.SaveReplayToFile(path, dumpJson: false);   // delegates to ReplaySystem.SaveToFile
```

**Save after the recording is closed, not before.** `StopRecording` is what assembles the
`IReplayData`, and `KlothoEngine.Stop()` is what calls it (clamping the length to the last verified
tick). Call `SaveReplayToFile` while ticks are still being recorded and there is nothing to write:
it logs `[ReplaySystem] No replay data to save` and returns without creating a file. The same rule
explains `GetCurrentReplayData()`, which returns the in-memory `IReplayData` for keeping or uploading
without a file — and returns `null` until the recording stops.

The wiring that gets this right for you is `ReplaySavePath` on the session setup: `KlothoSession.Stop()`
stops the engine and *then* writes the file, in that order, and skips it in replay mode.

`SaveReplayToFile` serializes `CurrentReplayData` (metadata + initial snapshot + command stream) and writes it.

> Recording adds negligible overhead — it copies the command list that the engine already has each tick. The one cost is the initial full-state snapshot, taken once at tick 0.

### Recording length & when a replay ends early

A replay's length (`metadata.TotalTicks`) is the **last tick the match confirmed (verified)** — not necessarily the last tick you saw drawn on screen. A replay that is shorter than the match *felt* is usually fine: it means "everything recorded is faithful," not "something broke." Two cases produce this:

- **The unconfirmed tail is dropped (normal).** A networked client runs a few *predicted* ticks ahead of the inputs it has actually confirmed. If a session ends while some predicted ticks are still unconfirmed, those ticks aren't authoritatively reproducible — their confirming inputs never arrived — so recording stops at the last confirmed tick. The last fraction of a second shown locally may not be in the file, by design: it couldn't be replayed faithfully, so it isn't claimed.

- **A desync recovery truncates the replay.** If the match hit a divergence that rollback couldn't absorb and recovered by dropping in an authoritative state wholesale — a **full-state resync** or **corrective reset** — recording **ends at that point**. Everything up to the reset is a faithful replay; the reset itself is a state *jump* with no input representation, so replaying past it would inevitably diverge. The engine cuts the replay at the last confirmed tick **before** the reset — the result is **shorter but 100% reproducible**, instead of longer and wrong. It is logged as `[KlothoEngine][Replay] truncated at ResyncRequest` (or `CorrectiveReset`) with the cut tick. Such a replay covers the match right up to the moment the desync was corrected — exactly the stretch worth reviewing. (Ordinary **late-join / reconnect / initial-state** deliveries are *not* truncations — only desync-recovery resets are.)

The file says which of the two it was: `metadata.EndReason` is `Normal` for an ordinary stop (including a dropped unconfirmed tail) and `CorrectiveReset` / `ResyncRequest` for a state-jump truncation. A reader must not treat "shorter than expected" as suspicious without checking it. `Unspecified` means nobody stamped a reason — that is a bug in whatever ended the recording, not a property of the match.

> **Practical read:** if a saved replay is shorter than expected, look for a resync / corrective-reset near the cut tick in the logs. A truncated replay is *honest* ("valid up to here"), not corrupt — it plays cleanly to its end and simply finishes early. The recovery point and everything after it are intentionally absent. If you need to reproduce what happened *after* a reset, that belongs to a fresh recording, not this file.

---

## 5. Playback

The simplest entry point is the session flow, which loads the file, restores `SimulationConfig` from its metadata, validates it, and starts a replay session:

```csharp
KlothoSession session = sessionFlow.StartReplayFromFile(path);   // throws ReplayLoadException on failure
```

`StartReplayFromFile` reconstructs everything from the file's metadata (`ToSimulationConfig()`), so you don't re-specify config. Session creation is observed through `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` — branch on `kind` for a replay-specific view (see [QuickStart](QuickStart.Unity.md)).

Under the hood `KlothoEngine.StartReplay(replayData)` seeds the RNG from `metadata.RandomSeed`, `Initialize()`s the simulation, `RestoreFromFullState(InitialStateSnapshot)`, then plays — each `OnTickPlayed` runs `_simulation.Tick(recordedCommands)` and dispatches that tick's events as **Verified** (a replay is entirely on the verified timeline; there is no prediction).

**Where tick 0 comes from is a choice.** Both `StartReplayFromFile` and `StartReplay` take an
optional `KlothoEngine.ReplayInitialState`:

| Value | What happens | Use it for |
| ---- | ---- | ---- |
| `RestoreSnapshot` *(default)* | The recorded snapshot bytes are restored. | Viewers — you want the world the file describes. |
| `Reconstruct` | Tick 0 is **rebuilt** from the recorded roster, seed and config through the live path (`OnInitializeWorld` runs), and the snapshot bytes are never read. Falls back to `RestoreSnapshot` when the file carries no roster. | Verifiers — see [§10](#10-verifying-a-replay-server-side). |

Playback then reports what it did: `ReplayTick0Reconstructed` says which of the two happened, and
`ReplayTick0Hash` is the hash of the tick-0 state it ended up with — compare it against
`metadata.InitialStateHash`. `CurrentStateHash` folds the state the simulation holds *right now* the
same way, which is the cheap end-of-playback check: the same file run twice must print the same
value. Nothing in the file records an end hash, so it is a differential signal, not a claim check.

Drive playback through the engine wrappers (`IsReplayMode` is `true` throughout):

| Engine method | Effect |
| ---- | ---- |
| `PauseReplay()` / `ResumeReplay()` | Pause / resume; sets engine `State` to `Paused` / `Running`. |
| `StopReplay()` | Stop and unsubscribe; `State → Finished`. |
| `SetReplaySpeed(ReplaySpeed)` | Change playback rate. |
| `SeekReplay(int tick)` | Jump to a tick (see [§6](#6-seeking)). |

When playback reaches the end, the engine fires its finish path (`OnPlaybackFinished` → `State = Finished`, `IsReplayMode = false`).

**Driving `IReplayPlayer` directly** (e.g. a standalone replay viewer not using the engine): `Load(replayData, logger)` → `Play()`, then call `Update(deltaTime)` every frame; consume `GetCurrentTickCommands()` / subscribe `OnTickPlayed`, and read `Progress` (0–1) / `Accumulator` (for view interpolation).

---

## 6. Seeking

`SeekReplay(tick)` uses the simulation's own snapshot ring (the same `FrameRingBuffer` rollback uses): it rolls back to the nearest saved snapshot at or before the target, then re-simulates forward to the target tick, re-feeding recorded commands. With no snapshot history available it re-simulates from tick 0. Backward seeks reset the synced-event watermark so events re-dispatch correctly on the replayed ticks.

`SeekToProgress(float 0..1)` is the fractional equivalent; `Progress` reports current position as 0–1. The concrete `ReplaySystem.StepForward()` / `StepBackward()` give single-tick stepping for frame-by-frame inspection.

---

## 7. Determinism Rules (must-read)

A replay only reproduces correctly if playback is deterministically identical to recording:

1. **Same binaries & determinism-affecting content** — a replay stores only inputs and re-derives state by re-simulating (rule 2), so playback must feed the simulation exactly what recording did. That means the same **simulation / game logic code** and component registration, and the same **content the deterministic simulation consumes**: DataAssets ([DataAsset.md](DataAsset.md)), the **static colliders** and **NavMesh** the stage builds (a stage's baked geometry is part of the simulation, not just visuals), and the RNG seed. Change any of these — a rebalanced DataAsset value, a re-baked collider set or NavMesh, or a logic edit — and the simulation diverges, so the recorded inputs no longer reproduce the original match. A replay is therefore bound to the exact code + content build (and stage) it was recorded on, and is not guaranteed compatible across versions or content revisions that alter simulation behavior or component layout.

    So that a mismatch can be *attributed* rather than merely observed, the file records what it ran against: four fingerprints (component-registry layout, static colliders, navmesh, and the game's own slot), the initial snapshot's hash and the tick it was taken at, the two process-global layout inputs (`PrunedComponentTypeIds`, the per-component maxCount overrides), and the ordered roster the tick-0 world was built from (`InitialRoster` — the participants in the order their entities were created, which makes it state-hash input; late joiners never appear in it). The roster is empty when the recording peer did not build tick 0 — a server-driven client receives its initial state from the server — and that emptiness is the signal, so there is no separate flag. The fingerprints are taken at the same instant as the snapshot — the navigation term moves with runtime rebakes, so any other moment would describe a different world. `0` in a fingerprint means "no such source registered", which is normal for a game that has none. None of this is signed: it distinguishes an honest client's version mismatch from a real divergence, it does not stop a forger.

    **The navigation term is also a gate.** `StartReplay` refuses a file whose recorded `NavFingerprint` disagrees with this process's — that covers both *recorded on another stage* and *recorded on a build whose pathfinding plans different corridors* (`FPNavAgentSystem.NAV_BEHAVIOUR_REVISION` is folded into the term alongside the mesh). Three cases are deliberately not refusals: `0` on either side keeps its "not provided" meaning (a local `0` is warned about, because on the playback path it usually means the check could not run); and a recording whose `InitialStateTick != 0` is warned, not refused, because its anchor describes the mesh at a mid-match instant rather than the base asset. The refusal lands before playback starts and before `OnGameStart` fires. It is still attribution, not integrity — a forged file rewrites the anchor along with the payload.

    The two layout inputs are **recorded, not restored**. The component layout is frozen process-wide before any replay loads, so a verifier must be *started* with them; `ToSimulationConfig()` deliberately leaves them out rather than pretending to apply them.
2. **The engine re-derives state from inputs** — never trust positions/HP from "the replay"; they don't exist in the file. They come out of re-simulating. So any non-determinism that would desync a live match also corrupts a replay (see [DeterministicMath.md §11](DeterministicMath.md) and [ECS.md §10](ECS.md)).
3. **The initial snapshot is mandatory** — recording without `SetInitialStateSnapshot` produces a file that can't play back: `StartReplay` throws `InvalidDataException` on the restore path. (The engine injects it automatically; only relevant if you build a custom recorder.) The one path that does not read those bytes is `ReplayInitialState.Reconstruct` with a recorded roster ([§5](#5-playback)), which rebuilds tick 0 and uses the recorded *hash* alone — a verifier's mode, not a viewer's.
4. **Gate live-only behavior on `IsReplayMode`** — view/audio/input code that should not run during playback must check `engine.IsReplayMode`, exactly as it would for spectator mode.
5. **Config & seed come from metadata** — playback restores `SimulationConfig` (including the match's `StageId` and opaque `MatchConfigData`) and the RNG seed from the file, so a multi-stage match replays on the stage it was recorded on; don't override them. (Replay files are **not** backward compatible and the loader does not try: `metadata.Version` must equal the build's `CURRENT_VERSION` exactly, so a file from any other version is rejected outright instead of being misparsed with the current layout. Re-record it.)

---

## 8. Error Handling

`LoadFromFile` / `StartReplayFromFile` throw **`ReplayLoadException`** for every load failure — file-not-found, read I/O error, malformed or version-incompatible payload, or invalid metadata (`SimulationConfig.Validate` failures are wrapped into the same type). The reason travels in `Message`, not only in `InnerException`, so showing `e.Message` (as below) is enough to tell a player "this replay is from another build — re-record it" apart from "the file is gone". A null/empty path throws `ArgumentException`. Loading is atomic: on failure the previously loaded `CurrentReplayData` is left untouched.

**A second type reaches the same call.** `StartReplayFromFile` loads the file and then *starts*
playback, and the two refusals `StartReplay` makes itself are **`InvalidDataException`**, not
`ReplayLoadException`: a missing initial snapshot, and a navigation fingerprint that disagrees with
this process ([§7-1](#7-determinism-rules-must-read)). Both land before any tick runs and before
`OnGameStart` fires — playback never half-starts, and the flow simply never returns a session — but a
`catch (ReplayLoadException)` alone will not see them. Catch both wherever a player picks the file:

```csharp
try
{
    var session = sessionFlow.StartReplayFromFile(path);
}
catch (ReplayLoadException e)          // the file cannot be read as a replay
{
    logger.KError($"Replay failed to load: {e.Message}");
    // show an error in UI; previous state is unchanged
}
catch (InvalidDataException e)         // it loaded, but this build cannot reproduce it
{
    logger.KError($"Replay cannot be played back here: {e.Message}");
}
```

---

## 9. Worked Example — record a match, then play it back

```csharp
// ── During a live match (recording is engine-driven; you just save) ──
void OnMatchEnded(KlothoEngine engine, string replayPath)
{
    engine.ReplaySystem.SetGameCustomData(BuildHeader());   // optional: map id, roster, etc.
    engine.Stop();                               // closes the recording — nothing to save before this
    engine.SaveReplayToFile(replayPath);         // inputs + initial snapshot
}
// Or skip both lines: set ReplaySavePath on the session setup and KlothoSession.Stop() does it in order.

// ── Later, play the replay back ──
KlothoSession ReplayMatch(KlothoSessionFlow flow, string replayPath)
{
    KlothoSession session = flow.StartReplayFromFile(replayPath);   // restores config/seed from file
    var engine = session.Engine;

    engine.SetReplaySpeed(ReplaySpeed.Double);   // 2× playback
    // engine.PauseReplay(); engine.SeekReplay(600); engine.ResumeReplay();
    return session;
}
```

The replay session ticks the *real* simulation from recorded inputs, so the match unfolds identically — and your view layer renders it the same way it rendered the live game, guarded by `engine.IsReplayMode` where live-only behavior must be suppressed.

---

## 10. Verifying a replay server-side

Because a replay stores inputs and re-derives state (§1), a server build can re-run one and obtain the
match result **authoritatively** — the client cannot lie about an outcome it did not simulate. The Brawler
sample ships this as a mode of its dedicated server:

```
dotnet run --project Samples/Brawler/Server -- --verify path/to/replay.rply
dotnet run --project Samples/Brawler/Server -- --verify a.rply b.rply c.rply --log=information
```

**Exit codes are the verdict.** `0` verified, `10` claim mismatch (a cheat suspicion — and a *successful*
run), `20` unverifiable, `30` unreadable file. Values `1`–`9` stay what they are everywhere else in that
binary: the process failed (bad arguments is `2`). That gap is deliberate — a crashed verifier must never
be mistaken for a detected cheat. One machine-readable line accompanies the code and names *why*.

**A run takes a queue.** Several paths in one process amortise the ~0.6 s of start-up, which is a large
share of a short match's verification. Every file prints its own line, and the process exits with the
**worst** verdict seen (`0 < 10 < 20 < 30`), so a batch cannot bury one bad file among good ones — but
the codes are ordered and the reasons are not, so a caller that only reads the exit code loses the *why*.
`--log=<level>` sets engine log verbosity. Every argument that does not start with `--` is a path; an
unrecognised flag is reported as ignored rather than changing the verdict, because a stray flag must
not turn into a different exit code.

### What it can prove, and what it cannot

- **It needs something to disagree with.** A re-simulation always agrees with itself, so the game writes
  what it *claims* happened into `GameCustomData` at match end, and the verifier compares. A replay with
  no claim is reported **unverifiable**, never verified — otherwise stripping the claim would make every
  replay pass.
- **The anchors attribute, they do not authenticate.** The four fingerprints (§7-1) let the verifier say
  "this was recorded against different code or content" instead of calling an honest client a cheat. They
  are non-cryptographic and unsigned: a forger rewrites them along with the payload. A file whose three
  *environment* terms (colliders, nav, game) are all zero is treated as unverifiable for exactly that
  reason. The layout term is deliberately not part of that test — it is an FNV fold the recorder always
  fills, so including it made the check unreachable.
- **A mismatch is not automatically cheating.** Any anchor disagreement means the two runs happened in
  different worlds, so the comparison is void — the verdict is *unverifiable*, and the line names which
  term moved (`anchors=mismatch:nav`).
- **`anchors=` says how much was actually compared, not just whether it agreed.** A term is compared only
  when *both* sides carry a value, and zero is a legitimate "not provided" — a game that registers no
  `IGameFingerprintSource` reports 0 there. So the field distinguishes `ok:3/3` (all three compared and
  agreed) from `partial:1/3(colliders)` (only that term had a value on both sides) from `unchecked`
  (nothing was compared — playback never reached the tick where the environment exists). Reading a
  `partial` line as a full environment match is the mistake this split exists to prevent.
- **Short is not suspicious.** A recording that ended at a desync recovery (`EndReason != Normal`, §4) is
  faithful only up to the cut, so its final result is not the match's — unverifiable, not a cheat.
- **Tick 0 is rebuilt, not trusted.** Re-simulation proves what the recorded *inputs* produce; the
  starting point they act on is bytes the recording peer wrote. So the verifier reconstructs the initial
  world from the recorded roster, seed and config through the same code path a live match runs, and never
  reads the snapshot bytes at all — substituting them achieves nothing. The rebuilt state is compared
  against the recorded hash, and a disagreement is *unverifiable* rather than cheating: its commonest
  cause is a content revision no anchor covers.
- **The verdict says where tick 0 came from.** `tick0=reconstructed|restored` and `tick0hash=ok|mismatch`
  are separate fields, and the difference is load-bearing: a verified *restore* means only "these inputs
  produce this result", while a verified *rebuild* also means the starting point is this build's own. A
  recording with no roster (an SD client) falls back to restoring the snapshot and is reported as
  `restored`.
- **Reconstruction only works if the file carries every input world-building reads.** Rebuilding runs the
  game's `OnInitializeWorld`, so anything that reads live configuration — a session knob, an inspector
  field, an environment variable — is an input the verifier does not have. Record those in
  `MatchConfigData` (the game's own per-match slot: the file carries it and playback restores it) and read
  them from there on every peer, not from the local config. The Brawler sample learned this the expensive
  way, twice. First: bot ids are numbered past the room capacity, that capacity lived only in each peer's
  `SessionConfig`, and the first rebuilt replay produced a world with **identical entity and component
  counts and a different hash** — indistinguishable from a determinism failure until the missing input was
  traced. Then, once a lobby was in play: tick 0 also seeds each player's **entitlement**, which the engine
  reads from the *network service* — and a replay session has none, so it decodes as "everything owned"
  and disagrees with what the server actually held. A verifier that substitutes a guess for an input it
  does not have reports honest recordings as mismatches, so it should refuse (`20`) and name what is
  missing.

  The two are worth telling apart. A **per-match scalar** (the capacity) fits in the match config and is
  cheap to close. A **per-player, server-verified value** (the entitlement) does not — not because of its
  size, but because of *when* it exists: a match config is issued before anyone has joined, and a late
  joiner arrives later still. Recording that is a format decision (it belongs with the roster), not a
  payload one.

  Two things make this worse than it looks, and both are worth checking in your own game:

  - **It is not confined to tick 0.** A recorded join command replays like any other, so whatever your game
    seeds *at join time* is re-derived during playback from data the replay session does not have. That
    reaches the restore path too — not just reconstruction.
  - **A mid-match divergence does not surface as "unverifiable".** Without a per-tick hash track there is
    nothing to catch it early, so it shows up at the end as a different result — which a verifier compares
    against the recorded claim and reports as a *mismatch*. An honest recording can be labelled cheating.
    Until such inputs are recorded, it is safer for a verifier to refuse files that contain join commands
    than to judge them.

  And note that all of this stays invisible while nobody issues such data: both sides then agree on the
  same empty default, and the first lobby-issued match is where it appears.

  **How Klotho closes it.** The file records the per-player values the tick-0 world was built from,
  index-parallel to `InitialRoster` (`InitialEntitlementData` + `InitialEntitlementLengths`), and a join
  command carries the joining player's copy so the join tick reproduces too. On playback
  `IKlothoEngine.GetPlayerEntitlement` answers from that record when no network service exists — so game
  code keeps reading the same accessor and does not change. An empty record is a valid one: a match with
  no issuer really has none, and the format version, not the field, is what distinguishes an older file.
- **A correct file is not enough: playback has to be wired to read it.** The failure above is about
  inputs the *file* lacks. There is a second, quieter one on the same axis — the file carries the input
  and the replay session never subscribes to the event that delivers it. Klotho shipped exactly that: a
  replay engine is built through a different `Initialize` overload than a networked one, and the join
  subscriptions lived only in the networked body, so on playback a late joiner got no participant slot,
  the game's join-time seed never ran, and the bytes the file carried for that joiner were read by nobody.
  Every recording involved was correct. What makes this class hard is that **nothing in the verdict moves**:
  tick 0 was byte-compared and matched, the final result matched the claim, and the diverged middle had no
  check to fail. If your engine has more than one way to construct a session, the per-mode wiring is worth
  a gate of its own — and a state hash taken at the *end* of playback is the cheapest thing that sees it
  (the same file, run twice, must print the same value; a run whose inputs were altered must not).
- **What rebuilding still leaves open.** After it, a forger no longer picks an arbitrary starting state —
  only the four inputs that produce one (roster, seed, stage, match config), all of which are still the
  recording peer's. Closing that needs a server-issued seed and a signature, neither of which the format
  has.

### Which record is authoritative — it depends on the match

A verified re-simulation is not automatically *the* answer; what it is worth depends on what else exists.

| Match | Authority | What verifying the replay adds |
|---|---|---|
| Dedicated server + lobby | the result the server reported to the lobby | **audit** — re-simulating what the authority already simulated. Catches build/content drift and server bugs, not cheating |
| Dedicated server, no lobby | the server's in-memory result; nothing was reported | there is no second record to compare against; only the file's own claim |
| Peer-to-peer | none — two peers, two recordings | each file verifies on its own; pairing them needs a shared match identity, which a lobbyless session does not have |
| Solo | none | the rebuild is the whole verdict |

The asymmetry is worth stating plainly: **cheat detection has the most to offer exactly where there is no
server**, and that is also where nothing issues an identity or a seed. A server-driven match is the easy case
and the least interesting one.

### Attribution: which match is this file?

Verification answers *"is this file honest"*. It does not answer *"which match is it"*, and the second
question has its own axis in the verdict line — `match=<id>` or one of two reasons it cannot be said:

| Value | Meaning | How to treat it |
|---|---|---|
| `match=<id>` | the issuer's key for this match instance | attributed |
| `unattributed` | the payload carries no identity (a solo, P2P or lobbyless-server match authors its own config) | normal — but it cannot back a ranked result |
| `legacy-payload` | the game's config payload is **shorter than the current layout**, so it predates a field | old, not suspicious — re-record it |

**The runner does not report "an identity was expected and is missing".** Answering that requires knowing the
match was lobby-issued, and nothing in the file says so — a lobby's config payload and a lobbyless server's
have the same fields, and both stamp a room capacity. Inferring an issuer from a stamped capacity would mark
every lobbyless-server and P2P-host recording as suspicious, which is the `legacy-payload` mistake pointed the
other way. So `--verify` reports whether an identity is *present*; whether one was *owed* is a judgement for
whoever holds the issue record — a submission service knows which matches it dispatched, and can compare its
own list against the `match=` values coming back.

**A file with no identity can be perfectly verified and still prove nothing about a specific match.** Without
one, nothing stops a player from running a one-shot attempt several times and submitting only the best
outcome — the file is honest each time. Reconstruction and signatures do not close this; only an identity
issued *before* the match does.

Games that use a lobby should carry that identity in `MatchConfigData`: the authority stamps it, the core
propagates it to every peer (and spectator), and it lands in every peer's replay metadata — no format change.
The replay's own `SessionId` is **not** that identity: it is a local `Guid.NewGuid()`, different in every
peer's recording, and the service has never seen it.

### The server's own recording

A dedicated server records like any other peer — `EnableReplayRecording` defaults to on — but nothing saves
it unless the game asks. In the Brawler sample that is `--save-replays <dir>`. Two reasons to turn it on:

- **It is the only server-driven recording that can be *rebuilt* rather than restored.** A client receives
  its initial state from the server, so a client's file carries no tick-0 roster and always replays
  `tick0=restored`. The server built that world, so its file carries the roster.
- **It is the authority's own record.** When a result is disputed, the client's file shows what that client
  received; the server's shows what the authority actually simulated.

Leaving it off is a legitimate choice — but then consider turning recording off too (`WithoutReplayRecording`
on flows that own their session): the buffer grows for the whole match either way (~114 bytes per tick per
room in the sample), and discarding it at the end is the one combination that pays the cost and gets nothing.

### Booting the verifier correctly

The component layout is frozen process-wide before any simulation exists, so the verifier builds it from
the layout inputs the file records (`PrunedComponentTypeIds`, the maxCount overrides) **before**
constructing anything. `ToSimulationConfig()` deliberately does not restore those two: they exist so a
verifier can *start* correctly, and assigning them afterwards would change nothing while looking like a
restore. A file recorded against a different layout is refused up front, since nothing derived from it
would mean anything.

One consequence: a process verifies replays of **one** layout — the freeze happens on the first file and
the rest of the queue inherits it. A later file with different layout inputs comes back
`unverifiable` naming `layoutFrozenDifferently` rather than being silently mis-verified, so batching
must group by build and content revision. That grouping is forced, not advisory.
