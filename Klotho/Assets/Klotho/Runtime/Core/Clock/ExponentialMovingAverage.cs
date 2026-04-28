using System;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Utility that updates the exponential moving average along with variance/standard deviation based on N samples.
    /// </summary>
    public sealed class ExponentialMovingAverage
    {
        private readonly float _alpha;
        private bool _initialized;

        public double Value { get; private set; }
        public double Variance { get; private set; }
        public double StandardDeviation { get; private set; }

        public ExponentialMovingAverage(int n)
        {
            if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "n must be >= 1");
            _alpha = 2.0f / (n + 1);
        }

        public void Add(double newValue)
        {
            if (_initialized)
            {
                double delta = newValue - Value;
                Value            += _alpha * delta;
                Variance          = (1 - _alpha) * (Variance + _alpha * delta * delta);
                StandardDeviation = Math.Sqrt(Variance);
            }
            else
            {
                Value = newValue;
                _initialized = true;
            }
        }

        public void Reset()
        {
            _initialized      = false;
            Value             = 0.0;
            Variance          = 0.0;
            StandardDeviation = 0.0;
        }
    }
}
