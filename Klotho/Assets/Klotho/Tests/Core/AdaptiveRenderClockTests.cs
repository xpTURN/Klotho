using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Unit-tests the drift-EMA / timescale / DynamicAdjustment formulas of <see cref="AdaptiveRenderClock"/>.
    /// The current KlothoEngine.RenderClock path bypasses AdaptiveRenderClock and smoothly tracks the verified render time on its own,
    /// so this test only validates the formula behavior of the class itself.
    /// </summary>
    [TestFixture]
    public class AdaptiveRenderClockTests
    {
        private const int TickMs = 50;
        private const int InterpolationDelayTicksStatic = 3;

        [Test]
        public void InitialState_AllZero()
        {
            var clock = new AdaptiveRenderClock();
            Assert.AreEqual(0, clock.VerifiedBaseTick);
            Assert.AreEqual(0.0, clock.VerifiedTimeMs);
            Assert.AreEqual(1f, clock.Timescale);
            Assert.AreEqual(0, clock.InterpolationDelayTicksDynamic);
        }

        [Test]
        public void Tick_AccumulatesVerifiedTimeAndIncrementsBaseTick()
        {
            var clock = new AdaptiveRenderClock();
            // deltaTime = 0.02s → 20ms/tick. With 50ms tick, baseTick++ after 2.5 frames.
            clock.Tick(0.02f, TickMs, InterpolationDelayTicksStatic);  // 20ms
            clock.Tick(0.02f, TickMs, InterpolationDelayTicksStatic);  // 40ms
            Assert.AreEqual(0, clock.VerifiedBaseTick);
            Assert.AreEqual(40.0, clock.VerifiedTimeMs, 0.001);

            clock.Tick(0.02f, TickMs, InterpolationDelayTicksStatic);  // 60ms → wrap to 10ms, baseTick=1
            Assert.AreEqual(1, clock.VerifiedBaseTick);
            Assert.AreEqual(10.0, clock.VerifiedTimeMs, 0.001);
        }

        [Test]
        public void Tick_WithoutBatchArrival_TimescaleStaysOne()
        {
            // Without an OnVerifiedBatchArrived call, drift EMA does not change → timescale stays in deadband (1f).
            var clock = new AdaptiveRenderClock();
            clock.Tick(0.016f, TickMs, InterpolationDelayTicksStatic);
            Assert.AreEqual(1f, clock.Timescale);
        }

        [Test]
        public void OnVerifiedBatchArrived_FirstCall_InitializesOnly()
        {
            var clock = new AdaptiveRenderClock();
            clock.OnVerifiedBatchArrived(1000, TickMs);
            // First call only sets the baseline; no timescale change.
            clock.Tick(0.016f, TickMs, InterpolationDelayTicksStatic);
            Assert.AreEqual(1f, clock.Timescale);
        }

        [Test]
        public void OnVerifiedBatchArrived_OnTimeBatch_DriftNearZero()
        {
            var clock = new AdaptiveRenderClock();
            double now = 1000;
            clock.OnVerifiedBatchArrived(now, TickMs);           // baseline
            clock.OnVerifiedBatchArrived(now + TickMs, TickMs);  // on-time arrival → drift 0
            clock.Tick(0.016f, TickMs, InterpolationDelayTicksStatic);
            // deadband: |drift| < 1 → timescale = 1
            Assert.AreEqual(1f, clock.Timescale);
        }

        [Test]
        public void OnVerifiedBatchArrived_LateBatch_SlowdownTimescale()
        {
            var clock = new AdaptiveRenderClock();
            double now = 1000;
            clock.OnVerifiedBatchArrived(now, TickMs);
            // 2-tick late arrival → drift = (200 - 50) / 50 = 3 → exceeds CATCHUP_POSITIVE_THRESHOLD=1 → SLOWDOWN
            clock.OnVerifiedBatchArrived(now + TickMs * 3, TickMs);
            clock.Tick(0.016f, TickMs, InterpolationDelayTicksStatic);
            Assert.Less(clock.Timescale, 1f);
            Assert.AreEqual(1f - AdaptiveRenderClock.SLOWDOWN_SPEED, clock.Timescale, 1e-6);
        }

        [Test]
        public void InterpolationDelayTicksDynamic_ClampedToStaticUpperBound()
        {
            var clock = new AdaptiveRenderClock();
            clock.Tick(0.016f, TickMs, InterpolationDelayTicksStatic);
            // Without jitter, ceil(1) = 1, which is below the static upper bound of 3 → 1.
            Assert.GreaterOrEqual(clock.InterpolationDelayTicksDynamic, 1);
            Assert.LessOrEqual(clock.InterpolationDelayTicksDynamic, InterpolationDelayTicksStatic);
        }

        [Test]
        public void Reset_ClearsAllState()
        {
            var clock = new AdaptiveRenderClock();
            clock.OnVerifiedBatchArrived(1000, TickMs);
            clock.OnVerifiedBatchArrived(1500, TickMs);
            clock.Tick(0.5f, TickMs, InterpolationDelayTicksStatic);
            Assert.Greater(clock.VerifiedBaseTick, 0);

            clock.Reset();
            Assert.AreEqual(0, clock.VerifiedBaseTick);
            Assert.AreEqual(0.0, clock.VerifiedTimeMs);
            Assert.AreEqual(1f, clock.Timescale);
            Assert.AreEqual(0, clock.InterpolationDelayTicksDynamic);
        }

        [Test]
        public void CreateState_ComposesFullRenderClockState()
        {
            var clock = new AdaptiveRenderClock();
            clock.Tick(0.02f, TickMs, InterpolationDelayTicksStatic);

            var state = clock.CreateState(predictedBaseTick: 7, predictedTimeMs: 15.0, tickIntervalMs: TickMs);

            Assert.AreEqual(7, state.PredictedBaseTick);
            Assert.AreEqual(15.0, state.PredictedTimeMs, 0.001);
            Assert.AreEqual(clock.VerifiedBaseTick, state.VerifiedBaseTick);
            Assert.AreEqual(clock.VerifiedTimeMs, state.VerifiedTimeMs, 0.001);
            Assert.AreEqual(clock.Timescale, state.Timescale);
            Assert.AreEqual(TickMs, state.TickIntervalMs);
        }
    }
}
