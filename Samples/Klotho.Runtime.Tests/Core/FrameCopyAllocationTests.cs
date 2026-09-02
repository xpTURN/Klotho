using System;
using NUnit.Framework;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// `Frame.CopyFrom` stays allocation-free.
    ///
    /// It is the hottest copy in the engine — the snapshot ring saves through it and
    /// `CapturePreviousUpdatePredicted` runs it on every normal frame — and it is a single
    /// `Buffer.BlockCopy` into an array the frame already owns, so nothing should reach the allocator.
    ///
    /// Timing is deliberately NOT asserted or reported here: it is measured in the editor at Brawler's
    /// real frame size (`EcsBenchmarks.CopyFrom_Avg_P95_P99` → 5.49 µs/call on Mono), and a wall-clock
    /// assertion on a shared machine is how a suite acquires flaky tests. What that benchmark cannot do
    /// is run without the editor, which is the gap this fills.
    /// </summary>
    [TestFixture]
    public class FrameCopyAllocationTests
    {
        private const int MaxEntities = 256;   // Brawler's SimulationConfig value
        private const int Warmup      = 200;
        private const int Iterations  = 2000;

        [Test]
        public void CopyFrom_AllocatesNothing()
        {
            var source = new Frame(MaxEntities, null);
            var target = new Frame(MaxEntities, null);

            var entity = source.CreateEntity();
            source.Add(entity, new TransformComponent());

            for (int i = 0; i < Warmup; i++) target.CopyFrom(source);

            // AllocProbe, not a bare window: this gate reported 5,344 B in CI on a loop that is a
            // single BlockCopy, which is the instrument's quantum artifact rather than a regression.
            long allocated = AllocProbe.SmallestWindow(
                () => { for (int i = 0; i < Iterations; i++) target.CopyFrom(source); });

            Assert.That(allocated, Is.Zero,
                $"CopyFrom allocated {allocated} bytes over {Iterations} calls — a regression here means the "
                + "heap or the entity manager grew per call");
        }
    }
}
