using NUnit.Framework;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Verifies the LagReductionLatency tracker entry behavior in KlothoEngine.ApplyExtraDelay.
    /// Backs Plan-LagReductionLatencyReconnectFix Option A — Reconnect source skip + stale pending guard.
    /// </summary>
    [TestFixture]
    public class LagReductionLatencyTests
    {
        private KlothoEngine CreateTestEngine()
        {
            return new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 25,
                    MaxRollbackTicks = 50,
                    UsePrediction = false,
                },
                new SessionConfig());
        }

        [Test]
        public void ApplyExtraDelay_ReconnectSource_DoesNotTrackLagReduction()
        {
            var engine = CreateTestEngine();
            engine.ApplyExtraDelay(9, ExtraDelaySource.DynamicPush);   // 0 -> 9
            engine.ApplyExtraDelay(7, ExtraDelaySource.Reconnect);     // 9 -> 7 (decrease)
            Assert.That(engine.LagReductionPendingForTest, Is.False, "Reconnect source must skip tracker entry");
        }

        [Test]
        public void ApplyExtraDelay_DynamicPushDecrease_TracksLagReduction()
        {
            var engine = CreateTestEngine();
            engine.ApplyExtraDelay(9, ExtraDelaySource.DynamicPush);   // 0 -> 9
            engine.ApplyExtraDelay(7, ExtraDelaySource.DynamicPush);   // 9 -> 7 (decrease, normal)
            Assert.That(engine.LagReductionPendingForTest, Is.True, "DynamicPush DOWN must enter tracker");
        }

        [Test]
        public void ApplyExtraDelay_ReconnectAfterPendingDown_ClearsStalePending()
        {
            var engine = CreateTestEngine();
            engine.ApplyExtraDelay(9, ExtraDelaySource.DynamicPush);   // 0 -> 9
            engine.ApplyExtraDelay(7, ExtraDelaySource.DynamicPush);   // 9 -> 7 (DOWN - pending set)
            Assert.That(engine.LagReductionPendingForTest, Is.True);
            engine.ApplyExtraDelay(7, ExtraDelaySource.Reconnect);     // same value, Reconnect source
            Assert.That(engine.LagReductionPendingForTest, Is.False, "Reconnect must force-clear stale pending");
        }
    }
}
