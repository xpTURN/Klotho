using System;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// SD Client-only Verified render clock.
    /// Uses drift-EMA and timescale to smoothly catch up with server batch arrival timing,
    /// and dynamically tunes InterpolationDelayTicks to reflect latency variation.
    /// SD Client calls OnVerifiedBatchArrived when a verified batch is received, and calls Tick every Update.
    /// The CSP path (PredictedBaseTick / PredictedTimeMs) is injected by the engine; this class independently tracks only the Verified side.
    /// </summary>
    public sealed class AdaptiveRenderClock
    {
        // Default constants for catchup/slowdown.
        public const float CATCHUP_SPEED              = 0.02f;
        public const float SLOWDOWN_SPEED             = 0.04f;
        public const float CATCHUP_POSITIVE_THRESHOLD = 1f;   // multiple of sendInterval
        public const float CATCHUP_NEGATIVE_THRESHOLD = -1f;
        public const int   DRIFT_EMA_DURATION         = 1;
        public const int   DELIVERY_TIME_EMA_DURATION = 2;
        public const float DYNAMIC_ADJUSTMENT_TOLERANCE = 1f;

        private readonly ExponentialMovingAverage _driftEma        = new(DRIFT_EMA_DURATION);
        private readonly ExponentialMovingAverage _deliveryTimeEma = new(DELIVERY_TIME_EMA_DURATION);

        private int    _verifiedBaseTick;
        private double _verifiedTimeMs;
        private float  _timescale = 1f;

        private double _lastBatchArrivalMs;
        private double _expectedNextBatchTimeMs;
        private bool   _batchArrivalInitialized;

        /// <summary>Dynamic delay tick count computed from jitter stddev. The static configuration value acts as the upper bound.</summary>
        public int InterpolationDelayTicksDynamic { get; private set; }

        public int    VerifiedBaseTick => _verifiedBaseTick;
        public double VerifiedTimeMs   => _verifiedTimeMs;
        public float  Timescale        => _timescale;

        /// <summary>
        /// Called when a Verified batch arrives. Updates the EMA of drift and delivery interval.
        /// </summary>
        public void OnVerifiedBatchArrived(double nowMs, int tickIntervalMs)
        {
            if (_batchArrivalInitialized)
            {
                double drift = (nowMs - _expectedNextBatchTimeMs) / tickIntervalMs;   // multiple of batch interval
                _driftEma.Add(drift);

                double delivery = nowMs - _lastBatchArrivalMs;
                _deliveryTimeEma.Add(delivery);
            }
            else
            {
                _batchArrivalInitialized = true;
            }

            _lastBatchArrivalMs = nowMs;
            _expectedNextBatchTimeMs = nowMs + tickIntervalMs;
        }

        /// <summary>
        /// Called every frame. Advances the Verified clock according to timescale and tunes InterpolationDelayTicks.
        /// </summary>
        public void Tick(float deltaTime, int tickIntervalMs, int interpolationDelayTicksStatic)
        {
            _timescale = ComputeTimescale(_driftEma.Value);

            // Compute buffer ticks based on jitter stddev. The upper bound is the static configuration value.
            double dynamic = DynamicAdjustment(
                sendInterval:  tickIntervalMs,
                jitterStddev:  _deliveryTimeEma.StandardDeviation,
                tolerance:     DYNAMIC_ADJUSTMENT_TOLERANCE);
            int ceiled = (int)Math.Ceiling(dynamic);
            if (ceiled < 1) ceiled = 1;
            if (ceiled > interpolationDelayTicksStatic) ceiled = interpolationDelayTicksStatic;
            InterpolationDelayTicksDynamic = ceiled;

            _verifiedTimeMs += deltaTime * 1000.0 * _timescale;
            while (_verifiedTimeMs >= tickIntervalMs)
            {
                _verifiedBaseTick++;
                _verifiedTimeMs -= tickIntervalMs;
            }
        }

        /// <summary>Resets the internal EMA and counters to their initial state.</summary>
        public void Reset()
        {
            _driftEma.Reset();
            _deliveryTimeEma.Reset();
            _verifiedBaseTick        = 0;
            _verifiedTimeMs          = 0.0;
            _timescale               = 1f;
            _lastBatchArrivalMs      = 0.0;
            _expectedNextBatchTimeMs = 0.0;
            _batchArrivalInitialized = false;
            InterpolationDelayTicksDynamic = 0;
        }

        /// <summary>Assembles a RenderClockState from the current Verified state. Predicted fields are filled in by the caller.</summary>
        public RenderClockState CreateState(int predictedBaseTick, double predictedTimeMs, int tickIntervalMs)
            => new()
            {
                PredictedBaseTick = predictedBaseTick,
                PredictedTimeMs   = predictedTimeMs,
                VerifiedBaseTick  = _verifiedBaseTick,
                VerifiedTimeMs    = _verifiedTimeMs,
                Timescale         = _timescale,
                TickIntervalMs    = tickIntervalMs,
            };

        private static float ComputeTimescale(double drift)
        {
            if (drift >  CATCHUP_POSITIVE_THRESHOLD) return 1f - SLOWDOWN_SPEED;   // ahead -> slow down
            if (drift <  CATCHUP_NEGATIVE_THRESHOLD) return 1f + CATCHUP_SPEED;    // behind -> speed up
            return 1f;
        }

        // Buffer factor expressing jitter stddev as a multiple of ticks.
        private static double DynamicAdjustment(int sendInterval, double jitterStddev, float tolerance)
        {
            if (sendInterval <= 0) return 0.0;
            double bufferTime = sendInterval + jitterStddev * tolerance;
            return bufferTime / sendInterval;
        }
    }
}
