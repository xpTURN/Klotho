using System;
using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Validates the formula of <see cref="ExponentialMovingAverage"/>.
    /// alpha = 2 / (n+1), Value += alpha*delta, Variance = (1-alpha) * (Variance + alpha*delta^2).
    /// </summary>
    [TestFixture]
    public class ExponentialMovingAverageTests
    {
        private const double Epsilon = 1e-9;

        [Test]
        public void Constructor_WithInvalidN_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialMovingAverage(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialMovingAverage(-1));
        }

        [Test]
        public void Constructor_WithValidN_Succeeds()
        {
            Assert.DoesNotThrow(() => new ExponentialMovingAverage(1));
            Assert.DoesNotThrow(() => new ExponentialMovingAverage(10));
        }

        [Test]
        public void InitialState_ValuesAreZero()
        {
            var ema = new ExponentialMovingAverage(5);
            Assert.AreEqual(0.0, ema.Value);
            Assert.AreEqual(0.0, ema.Variance);
            Assert.AreEqual(0.0, ema.StandardDeviation);
        }

        [Test]
        public void FirstAdd_SetsValueExactlyWithoutVariance()
        {
            var ema = new ExponentialMovingAverage(5);
            ema.Add(10.0);
            Assert.AreEqual(10.0, ema.Value, Epsilon);
            Assert.AreEqual(0.0, ema.Variance, Epsilon);
            Assert.AreEqual(0.0, ema.StandardDeviation, Epsilon);
        }

        [Test]
        public void SecondAdd_AppliesAlphaToDelta()
        {
            // n=3 → alpha = 2 / (3+1) = 0.5
            var ema = new ExponentialMovingAverage(3);
            ema.Add(10.0);
            ema.Add(20.0);  // delta = 10, Value += 0.5 * 10 = 5 → 15
            Assert.AreEqual(15.0, ema.Value, Epsilon);
        }

        [Test]
        public void Variance_ComputedFromDeltaSquared()
        {
            // n=3 → alpha=0.5. first Add(10) → Value=10, Variance=0
            // Add(20) → delta=10, Variance = (1-0.5) * (0 + 0.5 * 100) = 0.5 * 50 = 25
            var ema = new ExponentialMovingAverage(3);
            ema.Add(10.0);
            ema.Add(20.0);
            Assert.AreEqual(25.0, ema.Variance, Epsilon);
            Assert.AreEqual(System.Math.Sqrt(25.0), ema.StandardDeviation, Epsilon);
        }

        [Test]
        public void ConvergentSeries_ValueApproachesSteadyState()
        {
            // Repeated Add of the same value → Value converges.
            var ema = new ExponentialMovingAverage(5);
            for (int i = 0; i < 100; i++)
                ema.Add(42.0);
            Assert.AreEqual(42.0, ema.Value, 1e-6);
            // Variance decays toward 0 as delta=0 repeats.
            Assert.Less(ema.Variance, 1e-6);
        }

        [Test]
        public void Reset_ClearsAllState()
        {
            var ema = new ExponentialMovingAverage(3);
            ema.Add(10.0);
            ema.Add(20.0);
            Assert.Greater(ema.Variance, 0);

            ema.Reset();
            Assert.AreEqual(0.0, ema.Value);
            Assert.AreEqual(0.0, ema.Variance);
            Assert.AreEqual(0.0, ema.StandardDeviation);

            // After Reset, the first Add behaves as initial setup again.
            ema.Add(7.0);
            Assert.AreEqual(7.0, ema.Value, Epsilon);
            Assert.AreEqual(0.0, ema.Variance, Epsilon);
        }
    }
}
