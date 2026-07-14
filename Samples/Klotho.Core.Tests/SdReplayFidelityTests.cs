using System.Collections.Generic;

using Xunit;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD-client replay fidelity (Plan-L1). 4-A discovery (Stop off-by-one) + 4-B (L1 resync truncation).
    /// </summary>
    public sealed class SdReplayFidelityTests
    {
        [Fact] // smoke: the headless SD bootstrap reaches Running + recording, and verified ticks record.
        public void SdHarness_Bootstraps_AndRecordsVerifiedTicks()
        {
            var log = new SdCapturingLogger();
            var h = new SdReplayHarness(log);

            Assert.True(h.Engine.IsRecording, "SD client should be recording after GameStart");

            for (int i = 0; i < 6; i++)
                h.DeliverVerified();

            Assert.True(h.Engine.LastVerifiedTick >= 0,
                $"verified ticks should advance the frontier (LastVerifiedTick={h.Engine.LastVerifiedTick}, CurrentTick={h.Engine.CurrentTick})\n--- LOG ---\n{log.Dump()}");

            IReplayData replay = h.StopAndGetReplay();
            Assert.NotNull(replay);
        }

        [Fact] // 4-A discovery: a clean SD session's replay must pin TotalTicks to the last RECORDED tick
               // (executionTick), NOT _lastVerifiedTick (= entry.Tick = executionTick + 1). The SD verified
               // convention (_lastVerifiedTick = entry.Tick, RecordTick at executionTick) makes
               // ComputeReplayTotalTicks over-count by one, so the replay domain trails one never-recorded
               // tick. The per-tick hash compare is blind to that tail (Sim has no capture there to diff),
               // so the TotalTicks pin is the decisive signal. RED until Plan-L1 §3-B lands.
        public void SdCleanSession_ReplayPinsTotalTicksToLastRecordedTick()
        {
            var h = new SdReplayHarness();
            const int N = 5;
            for (int i = 0; i < N; i++)
                h.DeliverVerified();               // executionTicks 0..4 recorded (entry.Ticks 1..5)

            Assert.Equal(N, h.Engine.LastVerifiedTick);   // all promoted, mirror stayed in lockstep
            int lastRecordedTick = N - 1;                 // last executionTick

            IReplayData replay = h.StopAndGetReplay();

            // Fidelity: every recorded tick reproduces on a fresh replay engine.
            DeterministicReplaySim replaySim = h.Replay(replay);
            int compared = 0;
            var mismatchTicks = new List<int>();
            foreach (var kv in replaySim.TickHashCapture)
            {
                if (h.Sim.TickHashCapture.TryGetValue(kv.Key, out long recHash))
                {
                    compared++;
                    if (kv.Value != recHash) mismatchTicks.Add(kv.Key);
                }
            }
            Assert.True(compared > 0, "no overlapping ticks compared — harness/replay wiring broken?");
            // Every replayed tick reproduces its recorded hash. The over-count tail tick (executionTick+1,
            // never recorded) would surface here as a mismatch because the client's prediction lead leaves a
            // stale capture at that tick; a correct TotalTicks keeps the replay from ever running it.
            Assert.True(mismatchTicks.Count == 0,
                $"mismatched ticks: [{string.Join(",", mismatchTicks)}] (recorded 0..{lastRecordedTick}); replayTotalTicks={replay.Metadata.TotalTicks}");

            // Decisive: TotalTicks is the last recorded tick, not one past it.
            Assert.Equal(lastRecordedTick, replay.Metadata.TotalTicks);
        }

        [Fact] // 4-B (L1): a determinism-failure/reconnect resync FullState replaces state wholesale (a state
               // jump with no replay record), so the SD client must truncate its recording there — otherwise
               // playback past the jump diverges (F47's mechanism, SD analog of HostCorrectiveReset). The
               // resync tick must exceed _lastVerifiedTick to clear ApplyFullState's retreat guard. RED until
               // Plan-L1 §3-C wires TruncateReplayForStateJump into HandleServerDrivenFullStateReceived.
        public void SdResyncFullState_TruncatesRecording()
        {
            var h = new SdReplayHarness();
            const int N = 5;
            for (int i = 0; i < N; i++)
                h.DeliverVerified();                       // executionTicks 0..4, recorder frontier = 4
            Assert.True(h.Engine.IsRecording, "recording should be active before resync");
            int preResetRecordedTick = N - 1;              // 4 — last recorded executionTick

            int resyncTick = h.Engine.LastVerifiedTick + 3; // > _lastVerifiedTick(5) → clears the retreat guard
            long distinct = h.Sim.GetStateHash() ^ 0x5A5A_5A5A_5A5A_5A5AL;
            h.Net.RaiseServerFullState(resyncTick, System.BitConverter.GetBytes(distinct), distinct);

            Assert.False(h.Engine.IsRecording, "resync must truncate (stop) recording");
            IReplayData replay = h.Engine.GetCurrentReplayData();
            Assert.NotNull(replay);
            Assert.Equal(preResetRecordedTick, replay.Metadata.TotalTicks);
            Assert.True(replay.Metadata.TotalTicks < resyncTick,
                $"replay must be truncated before the resync tick (TotalTicks={replay.Metadata.TotalTicks}, resyncTick={resyncTick})");
        }

        [Fact] // 4-C (L1 e2e): an SD determinism failure must (a) trigger a FullState request, and (b) on the
               // resync truncate the replay to the last SUCCESSFULLY-recorded tick — not the failed tick
               // (never recorded) nor the reset tick. This drives the real detect→request→truncate chain and
               // pins the 3-B (recorder.CurrentTick) × "failed tick not recorded" interaction the live SD run
               // showed (totalTicks < resetTick with a >1 gap); 4-B injects the resync directly and misses it.
        public void SdDeterminismFailure_RequestsFullState_AndTruncatesToLastRecordedTick()
        {
            var h = new SdReplayHarness();
            const int N = 5;
            for (int i = 0; i < N; i++)
                h.DeliverVerified();                       // executionTicks 0..4 recorded; recorder frontier = 4
            Assert.Equal(N, h.Engine.LastVerifiedTick);
            int lastGoodRecordedTick = N - 1;              // 4

            // A verified tick whose server hash mismatches the client's resim → determinism failure. The
            // failed tick (executionTick N) is never recorded, and the client requests a FullState.
            int failedEntryTick = h.DeliverBadVerified();  // entry.Tick = N+1 (=6), executionTick = N (=5)
            Assert.Equal(failedEntryTick, h.Net.LastFullStateRequestTick);   // detect → request
            Assert.True(h.Engine.IsRecording, "still recording until the resync lands");

            // The server answers with a resync FullState at the requested tick → truncation.
            long distinct = h.Sim.GetStateHash() ^ 0x1234_5678_9ABC_DEF0L;
            h.Net.RaiseServerFullState(failedEntryTick, System.BitConverter.GetBytes(distinct), distinct);

            Assert.False(h.Engine.IsRecording, "resync must truncate (stop) recording");
            IReplayData replay = h.Engine.GetCurrentReplayData();
            Assert.NotNull(replay);
            // The last GOOD tick, NOT the failed one (which was never recorded): resetTick - totalTicks = 2.
            Assert.Equal(lastGoodRecordedTick, replay.Metadata.TotalTicks);
            Assert.True(replay.Metadata.TotalTicks < failedEntryTick,
                $"truncated before the reset tick (TotalTicks={replay.Metadata.TotalTicks}, resetTick={failedEntryTick})");
        }
    }
}
