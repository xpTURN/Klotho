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

- **`IReplayRecorder`** — `StartRecording(playerCount, simConfig, randomSeed)` · `RecordTick(tick, commands)` · `StopRecording(totalTicks) → IReplayData`, plus `OnRecordingStarted` / `OnRecordingStopped`.
- **`IReplayPlayer`** — `Load` · `Play` / `Pause` / `Resume` / `Stop` · `SeekToTick` / `SeekToProgress` · `GetCurrentTickCommands` · `Update(deltaTime)` · `Speed` · `Progress` / `Accumulator`, plus `OnTickPlayed` / `OnPlaybackFinished` / `OnSeekCompleted`.

`IReplaySystem` adds file I/O (`SaveToFile` / `LoadFromFile`), `CurrentReplayData`, `SetGameCustomData`, and `SetInitialStateSnapshot`. (The concrete `ReplaySystem` also exposes `StepForward()` / `StepBackward()` / `CancelRecording()`, which are not on the interface.)

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

---

## 4. Recording (engine-driven)

Recording is wired into `KlothoEngine` — you don't call `RecordTick` yourself. When a session starts with recording **enabled**, the engine:

1. calls `StartRecording(activePlayerCount, simConfig, randomSeed)` at boot,
2. calls `SetInitialStateSnapshot(snapshot, hash)` with the tick-0 full state (**required** — playback throws `InvalidDataException` if it's missing),
3. records each tick's commands **at the moment that tick is confirmed (verified)** — P2P, Server-Driven, and late-join paths alike. Because recording is tied to *confirmation* rather than to execution, a tick whose inputs were corrected by a late-arriving command or a rollback is recorded with its **corrected** inputs, and only ticks that actually confirmed end up in the file (see *Recording length* below).

Your game's job is just to attach optional metadata and save the file:

```csharp
var engine = session.Engine;                 // or however you reach KlothoEngine

// (optional) attach game-defined metadata — call after recording has started
engine.ReplaySystem.SetGameCustomData(myHeaderBytes);

// when the match ends, write the replay
engine.SaveReplayToFile(path, dumpJson: false);   // delegates to ReplaySystem.SaveToFile
```

`SaveReplayToFile` serializes `CurrentReplayData` (metadata + initial snapshot + command stream) and writes it. `GetCurrentReplayData()` returns the in-memory `IReplayData` if you want to keep or upload it without a file.

> Recording adds negligible overhead — it copies the command list that the engine already has each tick. The one cost is the initial full-state snapshot, taken once at tick 0.

### Recording length & when a replay ends early

A replay's length (`metadata.TotalTicks`) is the **last tick the match confirmed (verified)** — not necessarily the last tick you saw drawn on screen. A replay that is shorter than the match *felt* is usually fine: it means "everything recorded is faithful," not "something broke." Two cases produce this:

- **The unconfirmed tail is dropped (normal).** A networked client runs a few *predicted* ticks ahead of the inputs it has actually confirmed. If a session ends while some predicted ticks are still unconfirmed, those ticks aren't authoritatively reproducible — their confirming inputs never arrived — so recording stops at the last confirmed tick. The last fraction of a second shown locally may not be in the file, by design: it couldn't be replayed faithfully, so it isn't claimed.

- **A desync recovery truncates the replay.** If the match hit a divergence that rollback couldn't absorb and recovered by dropping in an authoritative state wholesale — a **full-state resync** or **corrective reset** — recording **ends at that point**. Everything up to the reset is a faithful replay; the reset itself is a state *jump* with no input representation, so replaying past it would inevitably diverge. The engine cuts the replay at the last confirmed tick **before** the reset — the result is **shorter but 100% reproducible**, instead of longer and wrong. It is logged as `[KlothoEngine][Replay] truncated at ResyncRequest` (or `CorrectiveReset`) with the cut tick. Such a replay covers the match right up to the moment the desync was corrected — exactly the stretch worth reviewing. (Ordinary **late-join / reconnect / initial-state** deliveries are *not* truncations — only desync-recovery resets are.)

> **Practical read:** if a saved replay is shorter than expected, look for a resync / corrective-reset near the cut tick in the logs. A truncated replay is *honest* ("valid up to here"), not corrupt — it plays cleanly to its end and simply finishes early. The recovery point and everything after it are intentionally absent. If you need to reproduce what happened *after* a reset, that belongs to a fresh recording, not this file.

---

## 5. Playback

The simplest entry point is the session flow, which loads the file, restores `SimulationConfig` from its metadata, validates it, and starts a replay session:

```csharp
KlothoSession session = sessionFlow.StartReplayFromFile(path);   // throws ReplayLoadException on failure
```

`StartReplayFromFile` reconstructs everything from the file's metadata (`ToSimulationConfig()`), so you don't re-specify config. Session creation is observed through `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` — branch on `kind` for a replay-specific view (see [QuickStart](QuickStart.Unity.md)).

Under the hood `KlothoEngine.StartReplay(replayData)` seeds the RNG from `metadata.RandomSeed`, `Initialize()`s the simulation, `RestoreFromFullState(InitialStateSnapshot)`, then plays — each `OnTickPlayed` runs `_simulation.Tick(recordedCommands)` and dispatches that tick's events as **Verified** (a replay is entirely on the verified timeline; there is no prediction).

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
2. **The engine re-derives state from inputs** — never trust positions/HP from "the replay"; they don't exist in the file. They come out of re-simulating. So any non-determinism that would desync a live match also corrupts a replay (see [DeterministicMath.md §11](DeterministicMath.md) and [ECS.md §10](ECS.md)).
3. **The initial snapshot is mandatory** — recording without `SetInitialStateSnapshot` produces a file that can't play back. (The engine injects it automatically; only relevant if you build a custom recorder.)
4. **Gate live-only behavior on `IsReplayMode`** — view/audio/input code that should not run during playback must check `engine.IsReplayMode`, exactly as it would for spectator mode.
5. **Config & seed come from metadata** — playback restores `SimulationConfig` (including the match's `StageId` and opaque `MatchConfigData`) and the RNG seed from the file, so a multi-stage match replays on the stage it was recorded on; don't override them. (Because these fields are now part of the recorded metadata, replay files written by earlier versions may not load — re-record them.)

---

## 8. Error Handling

`LoadFromFile` / `StartReplayFromFile` throw **`ReplayLoadException`** for every load failure — file-not-found, read I/O error, malformed/again-incompatible payload, or invalid metadata (`SimulationConfig.Validate` failures are wrapped into the same type). A null/empty path throws `ArgumentException`. Loading is atomic: on failure the previously loaded `CurrentReplayData` is left untouched.

```csharp
try
{
    var session = sessionFlow.StartReplayFromFile(path);
}
catch (ReplayLoadException e)
{
    logger.KError($"Replay failed to load: {e.Message}");
    // show an error in UI; previous state is unchanged
}
```

---

## 9. Worked Example — record a match, then play it back

```csharp
// ── During a live match (recording is engine-driven; you just save) ──
void OnMatchEnded(KlothoEngine engine, string replayPath)
{
    engine.ReplaySystem.SetGameCustomData(BuildHeader());   // optional: map id, roster, etc.
    engine.SaveReplayToFile(replayPath);         // inputs + initial snapshot
}

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
