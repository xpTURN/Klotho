using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Regression guards for replay-fidelity defects (F46 predicted-tick tail recording, F47
    /// corrective-reset truncation — guest receive path and host self-apply path).
    /// **The assertions describe the expected (post-fix) behavior**, deliberately NOT the defect itself
    /// ("empty" / "stale") — asserting the defect would pass on the broken code and invert the check.
    ///
    /// While a fix was still pending, its test was red and carried a `Skip` to protect the green-required
    /// mainline; each `Skip` was removed as its fix landed. Every test here is now active — the fixes have
    /// landed, so they guard the fixed behavior. (SD-client L1 truncation is covered separately in
    /// <c>SdReplayFidelityTests</c>.)
    /// </summary>
    public sealed class ReplayFidelityDefectTests
    {
        // ── Test A — predicted tick not recorded ─────────────────────
        [Fact]
        public void PredictedTick_IsRecordedToReplay()
        {
            var h = new ReplayFidelityHarness();
            const int predictTick = 10;

            // Withhold remote[predictTick] to force prediction at CurrentTick=predictTick (delay = 6 ticks ≤ MaxRollbackTicks).
            h.WithholdRemote(predictTick);
            h.AdvanceTo(predictTick + 6);          // predictTick runs via ExecuteTickWithPrediction (not recorded)
            h.DeliverRemote(predictTick);          // late remote arrival (empty == prediction → byte-equal, verified without rollback)
            h.Pump(3);                             // pump after delivery — verifies the chain
            h.AdvanceTo(30);                       // advance far enough that predictTick falls inside the clamp domain

            IReplayData replay = h.StopAndGetReplay();

            // Expected (post-fix): a verified predicted tick is also recorded to the replay. Currently fails — the predict/re-verify path has no RecordTick.
            Assert.True(h.Engine.LastVerifiedTick >= predictTick,
                $"predictTick must be verified before Stop (LastVerifiedTick={h.Engine.LastVerifiedTick})");
            Assert.True(replay.GetCommandsForTick(predictTick).Count >= 1,
                $"predicted tick {predictTick} must have commands recorded (got {replay.GetCommandsForTick(predictTick).Count})");
        }

        // ── Test B — stale recording ───────────────────────────────
        [Fact]
        public void LateRealCommand_OverwritesStaleEmptyFill_InReplay()
        {
            var h = new ReplayFidelityHarness();
            const int T = 12;

            // (1) Seat an unsealed empty fill at remote[T] (withhold auto-delivery, then seat manually).
            h.WithholdRemote(T);
            h.DeliverRemote(T);                    // empty fill (unsealed — normal delivery)
            // (2) After T is executed and recorded via ExecuteTick (no prediction — both players' commands exist before execution)
            h.AdvanceTo(T + 3);
            // (3) Deliver the real command (StopCommand) for the same (T, remote) after execution → DroppedDuplicate → overwrite + rollback
            h.DeliverRemote(T, new StopCommand());
            h.Pump(3);                             // flush the rollback and re-simulation
            h.AdvanceTo(30);

            IReplayData replay = h.StopAndGetReplay();
            IReadOnlyList<ICommand> recorded = replay.GetCommandsForTick(T);

            // Expected (post-fix): the re-record holds the corrected real command. Currently fails — the re-simulation loop has no RecordTick, leaving a stale empty.
            Assert.Contains(recorded, c => c is StopCommand);
        }

        // ── Corrective reset jump ────────────────────────────────────
        [Fact]
        public void CorrectiveReset_ReplayDoesNotDiverge()
        {
            var h = new ReplayFidelityHarness();
            h.AdvanceTo(15);
            int resetTick = h.Engine.CurrentTick;

            // Force the corrective reset into a distinct state (Applied and clean): since DeterministicReplaySim has GetStateHash() == the restored value,
            // stateData=GetBytes(X), stateHash=X (X ≠ current) makes hashMatched=true (Applied) while the state diverges.
            long distinctState = h.Sim.GetStateHash() ^ 0x5A5A5A5A5A5A5A5AL;
            byte[] stateData = BitConverter.GetBytes(distinctState);
            h.Net.RaiseFullStateReceived(resetTick, stateData, distinctState, FullStateKind.CorrectiveReset);

            h.Pump(10);   // advance after the reset (prediction) — the record sim captures post-reset ticks on the X lineage
            IReplayData replay = h.StopAndGetReplay();
            DeterministicReplaySim replaySim = h.Replay(replay);

            // Expected (post-fix): truncation ends the replay domain before the reset, so all ticks match. Currently fails — the reset is not recorded, so
            // the replay domain's post-reset ticks diverge from the record (post-reset X lineage).
            int mismatched = 0, compared = 0;
            foreach (var kv in replaySim.TickHashCapture)
            {
                if (h.Sim.TickHashCapture.TryGetValue(kv.Key, out long recHash))
                {
                    compared++;
                    if (kv.Value != recHash) mismatched++;
                }
            }
            Assert.True(compared > 0, "no overlapping ticks compared — harness/replay wiring broken?");
            Assert.Equal(0, mismatched);
        }

        // ── Truncation-mechanism pin (reason gate + pre-reset capture) ──────
        [Fact]
        public void CorrectiveReset_TruncatesRecordingAtPreResetVerifiedTick()
        {
            var h = new ReplayFidelityHarness();
            h.AdvanceTo(15);
            int resetTick = h.Engine.CurrentTick;
            int preResetVerified = h.Engine.LastVerifiedTick;
            Assert.True(h.Engine.IsRecording, "recording should be active before reset");

            long distinct = h.Sim.GetStateHash() ^ 0x1234L;
            h.Net.RaiseFullStateReceived(resetTick, System.BitConverter.GetBytes(distinct), distinct, FullStateKind.CorrectiveReset);

            // A corrective reset must truncate (StopRecording) the recording, and TotalTicks is the last verified tick before the reset (< reset tick).
            Assert.False(h.Engine.IsRecording, "corrective reset must truncate (stop) recording");
            IReplayData replay = h.Engine.GetCurrentReplayData();
            Assert.NotNull(replay);
            Assert.Equal(preResetVerified, replay.Metadata.TotalTicks);
            Assert.True(replay.Metadata.TotalTicks < resetTick,
                $"replay must be truncated before the reset tick (TotalTicks={replay.Metadata.TotalTicks}, resetTick={resetTick})");
        }

        // ── M1 — host-originated corrective reset ────────────────────
        [Fact] // The F47 truncation was wired only into the guest receive path (HandleFullStateReceived).
               // The HOST originates every corrective reset and self-applies via TryCorrectiveReset without
               // routing through that handler, so its own replay was never truncated — the reset's state jump
               // is unrecorded, and playback past it diverges. This drives the host path (rung 3), which the
               // existing guest tests (RaiseFullStateReceived) never exercise.
        public void HostCorrectiveReset_TruncatesRecording()
        {
            var h = new ReplayFidelityHarness();
            h.AdvanceTo(15);
            int resetTick = h.Engine.CurrentTick;
            int preResetVerified = h.Engine.LastVerifiedTick;
            Assert.True(h.Engine.IsRecording, "recording should be active before reset");

            // Guest resync-failure report → host HandleResyncFailureReported → TryCorrectiveReset (self-apply).
            h.Net.RaiseResyncFailureReported(ReplayFidelityHarness.RemotePlayerId, resetTick);

            Assert.False(h.Engine.IsRecording, "host corrective reset must truncate (stop) recording");
            IReplayData replay = h.Engine.GetCurrentReplayData();
            Assert.NotNull(replay);
            Assert.Equal(preResetVerified, replay.Metadata.TotalTicks);
            Assert.True(replay.Metadata.TotalTicks < resetTick,
                $"replay must be truncated before the reset tick (TotalTicks={replay.Metadata.TotalTicks}, resetTick={resetTick})");
        }
    }
}
