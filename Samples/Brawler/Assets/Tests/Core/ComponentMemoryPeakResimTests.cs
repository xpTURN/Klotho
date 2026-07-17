using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Integration proof of the peak-sampler seam: ComponentMemoryPeakSampler subscribes to
    /// <see cref="IKlothoEngine.OnFrameVerified"/>, so it must sample each confirmed tick exactly once
    /// and never a predicted-only / re-simulated frame. This drives a real host+guest engine through a
    /// frozen-verified prediction window (rollback territory) and asserts the event's verified-once
    /// semantics that the sampler relies on. The raw OnTickExecuted stream (which a naive per-tick
    /// sampler would use) is shown to inflate under prediction — the exact double-count OnFrameVerified
    /// avoids.
    ///
    /// The sampler's own accumulation / read-only behaviour is covered by ComponentMemoryAnalyzerTests;
    /// the fake TestSimulation here has no ECS heap, so this test targets the engine seam only.
    /// </summary>
    [TestFixture]
    public class ComponentMemoryPeakResimTests
    {
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("ComponentMemoryPeakResimTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [Test]
        public void OnFrameVerified_FiresVerifiedOnce_ImmuneToPredictionAndResim()
        {
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = int.MaxValue,   // disable presumed-drop auto-seal so the chain truly stalls
                MaxRollbackTicks = 50,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(2);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                harness.Host.Engine.DisableTimeSync();   // throttle is irrelevant to this scenario

                var verifiedTicks = new List<int>();
                int tickExecutedCount = 0;
                harness.Host.Engine.OnFrameVerified += t => verifiedTicks.Add(t);
                harness.Host.Engine.OnTickExecuted += _ => tickExecutedCount++;

                harness.AdvanceAllToTick(20);            // normal play: verified chain advances
                int verifiedCountAfterBaseline = verifiedTicks.Count;
                int tickExecAfterBaseline = tickExecutedCount;
                Assert.Greater(verifiedCountAfterBaseline, 0, "baseline should have verified some ticks");

                // Freeze the verified chain and predict ahead (stall the guest; no disconnect).
                int currentBeforeStall = harness.Host.CurrentTick;
                harness.AdvanceWithFrozenVerifiedTick(currentBeforeStall + 40, guest.LocalPlayerId);

                // (1) The verified chain froze while prediction raced far ahead: OnFrameVerified fired
                //     for only a small in-flight tail, NOT for the ~40 predicted ticks. A naive per-tick
                //     sampler would have sampled all of those predicted-only (later rolled-back) frames.
                int verifiedDuringFreeze = verifiedTicks.Count - verifiedCountAfterBaseline;
                int predictedAdvance = harness.Host.CurrentTick - currentBeforeStall;
                int predictionGap = harness.Host.CurrentTick - harness.Host.Engine.LastVerifiedTick;
                Assert.Greater(predictedAdvance, 20, "prediction should have advanced CurrentTick");
                Assert.Greater(predictionGap, 10, "verified chain froze while prediction raced (large predicted-but-unverified gap)");
                Assert.Less(verifiedDuringFreeze, predictedAdvance,
                    "OnFrameVerified fires far fewer than predicted ticks — the sampler never sees predicted-only frames");
                Assert.Greater(tickExecutedCount - tickExecAfterBaseline, 20,
                    "OnTickExecuted fires for predicted ticks — the inflation OnFrameVerified avoids");

                // Resume the stalled guest: inputs arrive, predicted ticks reconcile (rollback + re-sim)
                // and get verified for the first time.
                for (int i = 0; i < 400 && harness.Host.Engine.LastVerifiedTick < currentBeforeStall + 5; i++)
                    harness.Tick();

                // (2) Verified-once: no tick ever fired OnFrameVerified twice, even across rollback/re-sim.
                CollectionAssert.AllItemsAreUnique(verifiedTicks,
                    "OnFrameVerified fired twice for a tick — re-sim re-fire regression");
                for (int i = 1; i < verifiedTicks.Count; i++)
                    Assert.Less(verifiedTicks[i - 1], verifiedTicks[i],
                        "OnFrameVerified ticks must be strictly increasing (verified-once, monotonic)");

                // (3) The sampler seam fires strictly fewer times than the raw tick stream:
                //     no predicted/re-sim double count reaches the peak accumulator.
                Assert.Greater(tickExecutedCount, verifiedTicks.Count,
                    "OnFrameVerified (sampler seam) fires strictly fewer times than OnTickExecuted");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
