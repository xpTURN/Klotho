using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// VerifiedBaseTick upper-bound clamp (IMP103 V-2).
    ///
    /// Invariant: <c>VerifiedBaseTick &lt;= max(0, LastVerifiedTick - 1)</c>.
    /// The snapshot interpolator lerps frame <c>base</c> against frame <c>base + 1</c>, so both
    /// endpoints are Verified only when <c>base + 1 &lt;= LastVerifiedTick</c>. Without the clamp a
    /// positive render-time drift pushes base past LastVerifiedTick and the "Verified" path silently
    /// interpolates rollback-mutable predicted snapshots (the ring does not distinguish them).
    ///
    /// Sibling fixture <c>VerifiedRenderTimescaleTests</c> pins the exposed Timescale; this one pins
    /// the boundary invariant. Engine construction is per-case (not [SetUp]) because
    /// InterpolationDelayTicks is the axis under test.
    /// </summary>
    [TestFixture]
    public class VerifiedRenderBaseTickClampTests
    {
        private const int TickIntervalMs = 50;

        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo AdvanceMethod = typeof(KlothoEngine)
            .GetMethod("AdvanceVerifiedRenderTime", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("VerifiedRenderBaseTickClampTests");
        }

        private KlothoEngine CreateEngine(int interpolationDelayTicks)
        {
            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    InterpolationDelayTicks = interpolationDelayTicks,
                    MaxRollbackTicks = 50,
                },
                new SessionConfig());
            engine.Initialize(new TestSimulation(), _logger);
            return engine;
        }

        private static void Advance(KlothoEngine engine, float dt)
            => AdvanceMethod.Invoke(engine, new object[] { dt });

        private static double RenderTimeMs(KlothoEngine engine)
            => (double)RenderTimeMsField.GetValue(engine);

        // Seeds an initialized render time at `driftTicks` ahead of the convergence target.
        // Mirrors VerifiedRenderTimescaleTests.SeedDrift, but takes delay as an argument.
        private static void SeedDrift(KlothoEngine engine, int lastVerifiedTick, int delayTicks, double driftTicks)
        {
            int targetBaseTick = System.Math.Max(0, lastVerifiedTick - delayTicks);
            double targetTimeMs = (double)targetBaseTick * TickIntervalMs;
            LastVerifiedTickField.SetValue(engine, lastVerifiedTick);
            RenderTimeMsField.SetValue(engine, targetTimeMs + driftTicks * TickIntervalMs);
            RenderTimeInitField.SetValue(engine, true);
        }

        private static int UpperBound(KlothoEngine engine)
            => System.Math.Max(0, engine.LastVerifiedTick - 1);

        /// <summary>
        /// T1 — the core invariant. delay=3, lastVerified=20, drift +8:
        /// renderTime = 17*50 + 8*50 = 1250ms, so base = 25 while the bound is 19.
        /// drift 8 is below the 10-tick snap threshold, so the snap does NOT fire — a pass here is
        /// the clamp's doing and nothing else's.
        /// </summary>
        [Test]
        public void DriftPastVerified_ClampsBaseTickToVerifiedBoundary()
        {
            var engine = CreateEngine(interpolationDelayTicks: 3);
            SeedDrift(engine, lastVerifiedTick: 20, delayTicks: 3, driftTicks: 8.0);

            Advance(engine, 0.016f);

            var clock = engine.RenderClock;
            Assert.That(clock.VerifiedBaseTick, Is.LessThanOrEqualTo(UpperBound(engine)),
                "VerifiedBaseTick must not exceed max(0, LastVerifiedTick - 1) — otherwise frame " +
                "base+1 is an unverified predicted snapshot");
            // The snap would have produced targetTimeMs (850); the clamp produces the bound (950).
            Assert.That(RenderTimeMs(engine), Is.EqualTo(19.0 * TickIntervalMs).Within(1e-6),
                "recovery must come from the clamp (bound=950ms), not the 10-tick snap (target=850ms)");
        }

        /// <summary>
        /// T2 — base/alpha coherence. Clamping the derived base in the RenderClock getter (instead of
        /// the render-time scalar) would leave alpha computed from the pre-clamp time, so the pair
        /// would describe two different instants. Stated implementation-independently: base and the
        /// alpha remainder must always reconstruct the stored render time.
        /// </summary>
        [Test]
        public void ClampedState_BaseAndAlphaDescribeTheSameInstant()
        {
            var engine = CreateEngine(interpolationDelayTicks: 3);
            SeedDrift(engine, lastVerifiedTick: 20, delayTicks: 3, driftTicks: 8.0);

            Advance(engine, 0.016f);

            var clock = engine.RenderClock;
            double reconstructed = (double)clock.VerifiedBaseTick * TickIntervalMs + clock.VerifiedTimeMs;
            Assert.That(reconstructed, Is.EqualTo(RenderTimeMs(engine)).Within(1e-6),
                "base * tickMs + VerifiedTimeMs must equal the stored render time");
            Assert.That(clock.VerifiedAlpha, Is.InRange(0f, 1f));
        }

        /// <summary>
        /// T3 — the invariant holds across the whole validated InterpolationDelayTicks range [1,3].
        /// drift is delay+3 so it is always past the violation threshold (drift >= delay) yet below
        /// the 10-tick snap. delay=1 is the zero-slack boundary and must be covered.
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void InvariantHolds_ForEveryValidatedDelay(int delayTicks)
        {
            var engine = CreateEngine(delayTicks);
            SeedDrift(engine, lastVerifiedTick: 20, delayTicks: delayTicks, driftTicks: delayTicks + 3);

            Advance(engine, 0.016f);

            Assert.That(engine.RenderClock.VerifiedBaseTick, Is.LessThanOrEqualTo(UpperBound(engine)),
                $"invariant must hold for InterpolationDelayTicks={delayTicks}");
        }

        /// <summary>
        /// T4 — lastVerified=0 boundary. The bound is max(0, -1) = 0, so base pins to 0 and never
        /// goes negative.
        ///
        /// KNOWN LIMITATION (documented, not a violation): at lastVerified=0 the max(0, ...) floor
        /// breaks the derivation — base+1 = 1 still exceeds LastVerifiedTick = 0, so "both endpoints
        /// Verified" is NOT achieved. Only one Verified frame exists, so interpolation is impossible
        /// by definition. Covering it exactly needs the interpolator-side LastVerifiedTick check,
        /// which this change deliberately leaves to the V-6 patch. This test asserts the limitation
        /// so nobody later reads the invariant as a stronger guarantee than it is.
        /// </summary>
        [Test]
        public void ZeroVerifiedTick_PinsBaseToZero_AndResidualExposureIsDocumented()
        {
            var engine = CreateEngine(interpolationDelayTicks: 3);
            SeedDrift(engine, lastVerifiedTick: 0, delayTicks: 3, driftTicks: 0.0);

            Advance(engine, 0.016f);

            var clock = engine.RenderClock;
            Assert.That(clock.VerifiedBaseTick, Is.EqualTo(0));
            Assert.That(clock.VerifiedBaseTick, Is.LessThanOrEqualTo(UpperBound(engine)));
            Assert.That(clock.VerifiedBaseTick + 1, Is.GreaterThan(engine.LastVerifiedTick),
                "documented residual exposure: with a single Verified frame the b endpoint cannot be Verified");
        }

        /// <summary>
        /// T5 — the bound is not monotonic: _lastVerifiedTick retreats on rollback-to-anchor and
        /// FullState apply. The clamp must pull the render time back the same frame.
        ///
        /// Arithmetic (delay=3, tick=50): seeded renderTime = 850ms at lastVerified=20. Retreat to 15
        /// makes target = 12*50 = 600ms and bound = 14*50 = 700ms. The advance leaves renderTime at
        /// 858ms, which is below target + 10 ticks (1100ms), so the snap does NOT fire. Asserting
        /// renderTime == bound (700) rather than target (600) is what proves the clamp did the work —
        /// without it a deeper retreat would let the snap pass this test for the wrong reason.
        /// </summary>
        [Test]
        public void VerifiedTickRetreat_ClampRecoversInvariantSameFrame()
        {
            var engine = CreateEngine(interpolationDelayTicks: 3);
            SeedDrift(engine, lastVerifiedTick: 20, delayTicks: 3, driftTicks: 0.0);
            LastVerifiedTickField.SetValue(engine, 15);

            Advance(engine, 0.016f);

            Assert.That(engine.RenderClock.VerifiedBaseTick, Is.LessThanOrEqualTo(UpperBound(engine)),
                "invariant must recover on the retreat frame");
            Assert.That(RenderTimeMs(engine), Is.EqualTo(14.0 * TickIntervalMs).Within(1e-6),
                "recovery must come from the clamp (bound=700ms), not the 10-tick snap (target=600ms)");
        }
    }
}
