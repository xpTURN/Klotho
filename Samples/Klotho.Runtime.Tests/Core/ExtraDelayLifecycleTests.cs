using System;

using NUnit.Framework;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// extra-delay split (baseline + reactive), migration model, and clamp.
    /// Engine-level unit tests for migration and clamp + Validate. Harness-driven ACs
    /// (grace, max-over-peers, handover, trigger asymmetry, report lifecycle)
    /// live with the network suite; de-escalation and grace self-interference are in
    /// DynamicInputDelayPolicyTests.
    /// </summary>
    [TestFixture]
    public class ExtraDelayLifecycleTests
    {
        // MaxRollbackTicks=50 → clampMax = 25.
        private KlothoEngine CreateTestEngine() => new KlothoEngine(
            new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
                UsePrediction = false,
            },
            new SessionConfig());

        // push-DOWN preserves reactive; push-UP drains it (migration), no ratchet/overshoot.
        [Test]
        public void Migration_PushDownPreserves_PushUpDrains()
        {
            var engine = CreateTestEngine();
            engine.ApplyExtraDelay(10, ExtraDelaySource.Sync);   // baseline=10, reactive=0, effective=10
            engine.EscalateExtraDelay(6, 40);                    // reactive=6, effective=16

            // push-DOWN (8 < baseline 10): reactive preserved → effective = 8 + 6 = 14 (not 8).
            engine.ApplyExtraDelay(8, ExtraDelaySource.DynamicPush);
            Assert.AreEqual(14, engine.RecommendedExtraDelay,
                "push-DOWN must preserve reactive (T-F3): effective = baseline(8) + reactive(6)");

            // push-UP (20 >= baseline 8): reactive drained by the increase → effective = max(20, prev 14)
            // = 20 (NOT clamp(20 + 6) = 25 — that would be the ratchet the migration prevents).
            engine.ApplyExtraDelay(20, ExtraDelaySource.DynamicPush);
            Assert.AreEqual(20, engine.RecommendedExtraDelay,
                "push-UP must drain reactive (migration): effective = max(newBaseline, prevEffective), no ratchet");
        }

        // runtime effective never exceeds clampMax (MaxRollbackTicks/2), and escalation is a no-op
        // once at the ceiling.
        [Test]
        public void Clamp_EffectiveBoundedByMaxRollbackHalf()
        {
            var engine = CreateTestEngine();

            // A baseline push above the budget is clamped at read time (25 = 50/2).
            engine.ApplyExtraDelay(40, ExtraDelaySource.DynamicPush);
            Assert.AreEqual(25, engine.RecommendedExtraDelay, "effective clamps to MaxRollbackTicks/2");

            // At the ceiling, reactive escalation cannot push effective past the clamp.
            engine.EscalateExtraDelay(10, 40);
            Assert.AreEqual(25, engine.RecommendedExtraDelay, "escalation at ceiling is a no-op (effective backstop)");
        }

        // Validate — authored config throws on the invariant violation; wire-received config
        // clamps+warns (never throws) so a malformed server config cannot crash the client.
        [Test]
        public void Validate_AuthoredThrows_WireClamps()
        {
            var authored = new SimulationConfig { MaxRollbackTicks = 50, ReactiveMax = 40 }; // 40 > 25
            Assert.Throws<ArgumentOutOfRangeException>(() => authored.Validate(throwOnError: true),
                "authored config must fail fast on ReactiveMax > MaxRollbackTicks/2");

            var wire = new SimulationConfig { MaxRollbackTicks = 50, ReactiveMax = 40 };
            Assert.DoesNotThrow(() => wire.Validate(throwOnError: false),
                "wire-received config must not throw (no client crash on hostile/misconfigured server)");
            Assert.AreEqual(25, wire.ReactiveMax, "wire path clamps ReactiveMax to MaxRollbackTicks/2");
        }
    }
}
