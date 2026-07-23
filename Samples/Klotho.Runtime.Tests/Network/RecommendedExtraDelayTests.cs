using NUnit.Framework;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// Unit tests for RecommendedExtraDelayCalculator.Compute (shared static helper).
    /// Pure-computation path: no instance state, no logging. Verifies the formula, fallback, and clamp boundaries.
    /// </summary>
    [TestFixture]
    public class RecommendedExtraDelayTests
    {
        // RTT=200ms, 25ms tick → rttTicks=8, +safety 2 = 10. Within clamp(0, 25).
        [Test]
        public void Compute_NormalRtt_ReturnsRttTicksPlusSafety()
        {
            var (extraDelay, fallback, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 200, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.IsFalse(fallback);
            Assert.AreEqual(10, extraDelay);
        }

        // Sub-tick RTT — ceil(24/25)=1, +safety=3.
        [Test]
        public void Compute_SubTickRtt_CeilsToOneTick()
        {
            var (extraDelay, fallback, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 24, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.IsFalse(fallback);
            Assert.AreEqual(3, extraDelay);
        }

        // RTT=0 (unavailable) → fallback to safety only.
        [Test]
        public void Compute_RttZero_FallbackToSafety()
        {
            var (extraDelay, fallback, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 0, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.IsTrue(fallback);
            Assert.AreEqual(2, extraDelay);
        }

        // RTT exceeds sanity bound → clamp avgRtt to sanity cap, compute extraDelay from clamped value.
        // Keeps storm-prevention monotonic in the RTT direction while bounding the worst-case extraDelay.
        [Test]
        public void Compute_RttAboveSanityMax_ClampsToSanity()
        {
            var (extraDelay, fallback, rttTicks, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 300, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.IsFalse(fallback, "Valid avgRtt above sanity cap is not invalid measurement");
            Assert.AreEqual(10, rttTicks, "safeRtt = min(300, 240) = 240 → 10 ticks");
            Assert.AreEqual(12, extraDelay, "10 + safety(2)");
        }

        // RTT=600ms → rttTicks=24, +safety=26. Clamped to MaxRollbackTicks/2 = 25.
        [Test]
        public void Compute_HighRtt_ClampedToHalfMaxRollback()
        {
            var (extraDelay, fallback, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 600, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 800, maxRollbackTicks: 50);

            Assert.IsFalse(fallback);
            Assert.AreEqual(25, extraDelay);
        }

        // Negative safety (degenerate config) → clamp lower bound to 0.
        [Test]
        public void Compute_NegativeSafety_ClampedToZero()
        {
            var (extraDelay, _, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 0, tickIntervalMs: 25, safety: -10, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.AreEqual(0, extraDelay);
        }

        // Boundary: avgRtt == rttSanityMaxMs → still in normal path.
        [Test]
        public void Compute_RttAtSanityBoundary_NormalPath()
        {
            var (extraDelay, fallback, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 240, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.IsFalse(fallback);
            // ceil(240/25)=10, +2=12.
            Assert.AreEqual(12, extraDelay);
        }

        // Spurious measurement (RTT far above sanity) → 1st stage sanity clamp + 2nd stage clampMax bound.
        // Verifies the two-stage bound: safeRtt = min(avgRtt, sanity) cap precedes the Math.Clamp(0, clampMax).
        [Test]
        public void Compute_RttExtreme_BoundedBySanityNotClampMax()
        {
            var (extraDelay, fallback, rttTicks, _, _) = RecommendedExtraDelayCalculator.Compute(
                avgRtt: 5000, tickIntervalMs: 25, safety: 2, rttSanityMaxMs: 240, maxRollbackTicks: 50);

            Assert.IsFalse(fallback, "5000ms is a valid measurement, sanity-clamped first");
            Assert.AreEqual(10, rttTicks, "safeRtt = min(5000, 240) = 240 → 10 ticks");
            Assert.AreEqual(12, extraDelay, "Sanity clamp precedes clampMax");
        }
    }
}
