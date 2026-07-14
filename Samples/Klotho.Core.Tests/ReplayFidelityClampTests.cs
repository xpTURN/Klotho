using System.Collections.Generic;

using Xunit;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Regression pins for replay tail clamping:
    ///  - tail clamp: `TotalTicks == LastVerifiedTick` (removes the over-count) + no empty-command ticks in the replay.
    ///  - full-domain hashes: the clamp aligns the replay domain with the record run's executed range, so all ticks `0..TotalTicks` match.
    ///  - in-place buffer: same-length re-record grows by 0; only a different length appends.
    /// </summary>
    public sealed class ReplayFidelityClampTests
    {
        [Fact] // stop while leaving an unverified tail → TotalTicks is LastVerifiedTick, not CurrentTick+delay
        public void TailClamp_TotalTicksEqualsLastVerifiedTick()
        {
            var h = new ReplayFidelityHarness();
            // Withhold remote[20..22] before auto-delivery → the chain stalls at 19, and only CurrentTick advances via prediction.
            h.WithholdRemote(20);
            h.WithholdRemote(21);
            h.WithholdRemote(22);
            h.AdvanceTo(28);   // 20+ is predicted (remote withheld) → forms an unverified tail

            IReplayData replay = h.StopAndGetReplay();

            Assert.True(h.Engine.LastVerifiedTick < h.Engine.CurrentTick,
                $"expected an unverified tail (LastVerifiedTick={h.Engine.LastVerifiedTick}, CurrentTick={h.Engine.CurrentTick})");
            Assert.Equal(h.Engine.LastVerifiedTick, replay.Metadata.TotalTicks);

            // Tail invariant: no tick in the replay domain lacks commands (verified ticks have at least 1 via auto-inject).
            for (int t = 0; t <= replay.Metadata.TotalTicks; t++)
                Assert.True(replay.GetCommandsForTick(t).Count >= 1, $"tick {t} has no recorded commands");
        }

        [Fact] // after the clamp, a clean session matches the record on every tick of the replay domain (full domain, not just the intersection)
        public void CleanSession_FullReplayDomain_HashesMatch()
        {
            var h = new ReplayFidelityHarness();
            h.AdvanceTo(40);

            IReplayData replay = h.StopAndGetReplay();
            DeterministicReplaySim replaySim = h.Replay(replay);

            int total = replay.Metadata.TotalTicks;
            Assert.True(total > 8, $"replay too short (TotalTicks={total})");
            for (int t = 0; t <= total; t++)
            {
                Assert.True(replaySim.TickHashCapture.ContainsKey(t), $"replay run missing tick {t}");
                Assert.True(h.Sim.TickHashCapture.ContainsKey(t), $"record run missing tick {t}");
                Assert.Equal(h.Sim.TickHashCapture[t], replaySim.TickHashCapture[t]);
            }
        }

        [Fact] // same-length re-record is in-place (no growth); only a different length appends
        public void RecordCommands_SameLengthReRecord_DoesNotGrowBuffer()
        {
            var factory = new CommandFactory();
            var rd = new ReplayData(factory);
            var cmds = new List<ICommand>
            {
                new EmptyCommand { PlayerId = 0, Tick = 5 },
                new EmptyCommand { PlayerId = 1, Tick = 5 },
            };

            rd.RecordCommands(5, cmds, factory);
            int sizeAfterFirst = rd.Serialize().Length;

            // Re-record the same (tick, same-length content) N times → in-place overwrite, so the buffer does not grow.
            for (int i = 0; i < 20; i++)
                rd.RecordCommands(5, cmds, factory);
            int sizeAfterReRecords = rd.Serialize().Length;
            Assert.Equal(sizeAfterFirst, sizeAfterReRecords);

            // Re-record with a different length (an added command) → append (growth). Edge case: the empty→real transition takes this path.
            var bigger = new List<ICommand>
            {
                new EmptyCommand { PlayerId = 0, Tick = 5 },
                new EmptyCommand { PlayerId = 1, Tick = 5 },
                new StopCommand { PlayerId = 0, Tick = 5 },
            };
            rd.RecordCommands(5, bigger, factory);
            int sizeAfterGrow = rd.Serialize().Length;
            Assert.True(sizeAfterGrow > sizeAfterReRecords,
                $"different-length re-record should append (was {sizeAfterReRecords}, now {sizeAfterGrow})");
        }
    }
}
