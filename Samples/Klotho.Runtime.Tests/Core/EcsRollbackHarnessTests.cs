using System;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Runtime.Tests.Core
{
    /// <summary>
    /// The rollback harness itself. A harness that silently fails to roll back, or whose
    /// systems never run, would make every test built on it pass for the wrong reason — so it is
    /// pinned before anything depends on it.
    ///
    /// <para>The last test is the shape every consistency test built on this harness takes: a system that
    /// keeps derived state OUTSIDE the frame diverges on replay, and one that recomputes it from
    /// the frame does not — derived state surviving a rewind, reduced to its smallest form.</para>
    /// </summary>
    [TestFixture]
    public class EcsRollbackHarnessTests
    {
        /// <summary>Counts its own Update calls and writes nothing to the frame.</summary>
        private sealed class CountingSystem : ISystem
        {
            public int Updates;
            public void Update(ref Frame frame) { Updates++; }
        }

        /// <summary>
        /// Derived state done RIGHT: recomputed from frame.Tick every tick, so a rollback that
        /// rewinds the frame rewinds this too.
        /// </summary>
        private sealed class TickDerivedSystem : ISystem
        {
            public int Derived;
            public void Update(ref Frame frame) { Derived = frame.Tick * 2; }
        }

        /// <summary>
        /// Derived state done WRONG: accumulated outside the frame, so a re-executed tick sees a
        /// value the first execution never saw.
        /// </summary>
        private sealed class AccumulatingSystem : ISystem
        {
            public int Count;
            public void Update(ref Frame frame) { Count++; }
        }

        [Test]
        public void SystemsActuallyRun()
        {
            var counting = new CountingSystem();
            var h = new EcsRollbackHarness().AddSystem(counting).Initialize();

            h.TickTo(5);

            Assert.AreEqual(5, counting.Updates,
                "the systems did not run — this is exactly what the ISimulation-stub harnesses do");
        }

        [Test]
        public void RollbackRestoresTheFrameTick()
        {
            var h = new EcsRollbackHarness().Initialize();
            h.TickTo(8);
            Assert.AreEqual(8, h.CurrentTick);

            h.RollbackAndReplay(toTick: 4, head: 8);
            Assert.AreEqual(8, h.CurrentTick, "the replay did not return to the head");
        }

        [Test]
        public void ReplayMatchesForward_ForAFrameOnlySystem()
        {
            var h = new EcsRollbackHarness().AddSystem(new TickDerivedSystem()).Initialize();
            h.AssertReplayMatchesForward(forwardTicks: 10, rollbackTo: 6);
        }

        [Test]
        public void ReExecutionRunsTheSystemAgain_WhichIsWhyOutOfFrameStateDiverges()
        {
            // The harness's whole purpose in one assertion. The frame hash agrees across the
            // replay — the simulation is deterministic — while a counter living outside the frame
            // does not, because a rolled-back tick is EXECUTED AGAIN and nothing rewinds it.
            var accumulating = new AccumulatingSystem();
            var h = new EcsRollbackHarness().AddSystem(accumulating).Initialize();

            long[] forward = h.RunAndRecord(10);
            int afterForward = accumulating.Count;
            Assert.AreEqual(10, afterForward);

            long[] replay = h.RollbackAndReplay(toTick: 6, head: 10);

            for (int i = 0; i < replay.Length; i++)
                Assert.AreEqual(forward[6 + i], replay[i], $"frame hash diverged at replayed tick {7 + i}");

            Assert.AreEqual(afterForward + 4, accumulating.Count,
                "the rolled-back ticks were not re-executed — then the harness cannot test re-execution at all");
        }
    }
}
