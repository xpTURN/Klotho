using System;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Render clock state for the view layer.
    /// Supports both the CSP path (Predicted*) and the snapshot interpolation path (Verified*) simultaneously.
    /// On SD Client, AdaptiveRenderClock injects Verified values; in other modes the engine fills them with defaults.
    /// </summary>
    public struct RenderClockState
    {
        // CSP path - PredictedPrevious <-> Predicted lerp
        public int PredictedBaseTick;
        public double PredictedTimeMs;

        // Snapshot interpolation path - VerifiedFrame(n) <-> VerifiedFrame(n+1) lerp
        public int VerifiedBaseTick;
        public double VerifiedTimeMs;

        // SD Client adaptive only (1.0 baseline). 1f in other modes.
        public float Timescale;

        // Tick interval of the current mode. For Replay, the value from recording metadata; otherwise SimulationConfig.TickIntervalMs.
        public int TickIntervalMs;

        public float PredictedAlpha
        {
            get
            {
                if (TickIntervalMs <= 0) return 0f;
                double raw = PredictedTimeMs / TickIntervalMs;
                return raw > 1.0 ? 1f : (raw < 0.0 ? 0f : (float)raw);
            }
        }

        public float VerifiedAlpha
        {
            get
            {
                if (TickIntervalMs <= 0) return 0f;
                double raw = VerifiedTimeMs / TickIntervalMs;
                return raw > 1.0 ? 1f : (raw < 0.0 ? 0f : (float)raw);
            }
        }

        [Obsolete("Use PredictedAlpha or VerifiedAlpha explicitly")]
        public float Alpha => PredictedAlpha;
    }
}
