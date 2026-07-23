using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Timescale — RenderClock.Timescale now exposes AdvanceVerifiedRenderTime's
    /// drift-proportional convergence rate (was hardcoded 1f). The field is reset to 1f at the
    /// method entry so an early-return frame (no drift computed) reports 1f rather than a stale
    /// previous value. Pure-additive exposure (no live consumer) — these tests pin the exposed value.
    /// </summary>
    [TestFixture]
    public class VerifiedRenderTimescaleTests
    {
        private const int TickIntervalMs = 50;
        private const int InterpolationDelayTicks = 3;

        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo AdvanceMethod = typeof(KlothoEngine)
            .GetMethod("AdvanceVerifiedRenderTime", BindingFlags.NonPublic | BindingFlags.Instance);

        private KlothoEngine _engine;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("VerifiedRenderTimescaleTests");
        }

        [SetUp]
        public void SetUp()
        {
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    InterpolationDelayTicks = InterpolationDelayTicks,
                    MaxRollbackTicks = 50,
                },
                new SessionConfig());
            _engine.Initialize(new TestSimulation(), _logger);
        }

        private void Advance(float dt) => AdvanceMethod.Invoke(_engine, new object[] { dt });

        // Seeds an initialized render time at `driftTicks` ahead of the convergence target.
        private void SeedDrift(int lastVerifiedTick, double driftTicks)
        {
            int targetBaseTick = System.Math.Max(0, lastVerifiedTick - InterpolationDelayTicks);
            double targetTimeMs = (double)targetBaseTick * TickIntervalMs;
            LastVerifiedTickField.SetValue(_engine, lastVerifiedTick);
            RenderTimeMsField.SetValue(_engine, targetTimeMs + driftTicks * TickIntervalMs);
            RenderTimeInitField.SetValue(_engine, true);
        }

        /// <summary>
        /// Positive drift (render ahead of target) → slowdown timescale &lt; 1 (drift +2 → 0.8),
        /// within [0.5, 2.0]. Drift 2 ticks &lt; the 10-tick snap threshold so no instant snap.
        /// </summary>
        [Test]
        public void DriftAhead_ExposesSlowdownTimescale()
        {
            SeedDrift(lastVerifiedTick: 20, driftTicks: 2.0);

            Advance(0.016f);

            float ts = _engine.RenderClock.Timescale;
            Assert.AreEqual(0.8f, ts, 1e-4f, "drift +2 ticks → timescale 1 - 0.2 = 0.8");
            Assert.That(ts, Is.GreaterThanOrEqualTo(0.5f).And.LessThanOrEqualTo(2.0f), "clamped to [0.5,2.0]");
        }

        /// <summary>Zero drift → exactly 1f (neither catchup nor slowdown).</summary>
        [Test]
        public void NoDrift_ExposesUnitTimescale()
        {
            SeedDrift(lastVerifiedTick: 20, driftTicks: 0.0);

            Advance(0.016f);

            Assert.AreEqual(1f, _engine.RenderClock.Timescale, 1e-4f);
        }

        /// <summary>
        /// Early-return staleness pin (entry-prepend): after a non-1f frame, an
        /// early-return frame (`_lastVerifiedTick &lt; 0`, e.g. tick-0 resync) must report 1f — NOT
        /// the previous 0.8. Without the entry `_verifiedRenderTimescale = 1f`, the field stays stale.
        /// </summary>
        [Test]
        public void EarlyReturnAfterDrift_ResetsToUnitTimescale()
        {
            SeedDrift(lastVerifiedTick: 20, driftTicks: 2.0);
            Advance(0.016f);
            Assert.AreEqual(0.8f, _engine.RenderClock.Timescale, 1e-4f, "precondition: non-1f computed");

            // Early-return path: negative verified tick (reachable right after a tick-0 FullState apply).
            LastVerifiedTickField.SetValue(_engine, -1);
            Advance(0.016f);

            Assert.AreEqual(1f, _engine.RenderClock.Timescale, 1e-4f,
                "early-return frame must report 1f (entry-prepend), not the stale 0.8");
        }
    }
}
