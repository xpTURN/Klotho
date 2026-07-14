using System.Collections.Generic;

using Xunit;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Proves that the verification apparatus itself works.
    /// No behavior change (the engine is untouched): record then replay a clean P2P session (no prediction or reset),
    /// and confirm that DeterministicReplaySim's per-tick post-tick hashes match across the two runs. This harness,
    /// capture, and comparison must hold before the failing tests can actually distinguish defects (the old constant-hash idiom is vacuous).
    /// </summary>
    public sealed class ReplayFidelityHarnessTests
    {
        [Fact] // the harness actually advances ticks and records
        public void Harness_AdvancesAndRecords()
        {
            var h = new ReplayFidelityHarness();
            h.AdvanceTo(30);

            Assert.True(h.Engine.IsRecording, "clean session should be recording");
            Assert.True(h.Engine.CurrentTick >= 30, $"engine should advance past target, CurrentTick={h.Engine.CurrentTick}");
            Assert.True(h.Sim.TickCallCount > 0, "sim should have ticked");
            // Chain must actually verify (no permanent prediction stall on a clean session).
            Assert.True(h.Engine.LastVerifiedTick > h.Engine.CurrentTick - 8,
                $"verified chain lagging: LastVerifiedTick={h.Engine.LastVerifiedTick}, CurrentTick={h.Engine.CurrentTick}");
        }

        [Fact] // record/replay round-trip: per-tick hashes match on every executed tick (verification apparatus holds)
        public void CleanSession_RecordReplay_PerTickHashesMatch()
        {
            var h = new ReplayFidelityHarness();
            h.AdvanceTo(40);

            IReplayData replayData = h.StopAndGetReplay();
            Assert.NotNull(replayData);
            Assert.True(replayData.Metadata.TotalTicks > 0, "replay should have ticks");

            var recordCapture = new Dictionary<int, long>(h.Sim.TickHashCapture);
            var replaySim = h.Replay(replayData);
            IReadOnlyDictionary<int, long> replayCapture = replaySim.TickHashCapture;

            Assert.NotEmpty(recordCapture);
            Assert.NotEmpty(replayCapture);

            // Compare tick-by-tick over the intersection of the replay domain and the record capture (= the ticks actually executed).
            // (Pre-fix: TotalTicks = CurrentTick + InputDelayTicks over-counts, so the replay tail covers ticks the record run
            //  never executed — outside the intersection. After the tail clamp, the intersection == the full domain.)
            int matched = 0;
            foreach (var kv in recordCapture)
            {
                if (replayCapture.TryGetValue(kv.Key, out long replayHash))
                {
                    Assert.True(kv.Value == replayHash,
                        $"tick {kv.Key}: record hash {kv.Value} != replay hash {replayHash}");
                    matched++;
                }
            }

            // The intersection must be substantial (beyond the input-delay window); otherwise the capture/replay wiring is broken.
            Assert.True(matched > 8, $"too few matched ticks ({matched}) — capture/replay wiring broken?");
        }
    }
}
