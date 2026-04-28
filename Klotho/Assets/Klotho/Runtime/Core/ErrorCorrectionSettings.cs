namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Error Correction pipeline settings. Holds the thresholds/decay parameters used by the engine to compute deltas.
    /// View-side accumulation/decay/interpolation parameters are managed separately in <c>ErrorVisualState</c>.
    /// The engine computes the position/rotation difference before and after rollback; if it is below the threshold, the delta is ignored,
    /// and otherwise this delta is exposed to the view via GetPositionDelta / GetYawDelta / HasEntityTeleported.
    /// </summary>
    public struct ErrorCorrectionSettings
    {
        /// <summary>Lower bound of the decay rate. Applied when the error is at or below PosBlendStart.</summary>
        public float MinRate;

        /// <summary>Upper bound of the decay rate. Applied when the error is at or above PosBlendEnd.</summary>
        public float MaxRate;

        /// <summary>Decay-rate interpolation start error magnitude (m). Below this, MinRate is held constant.</summary>
        public float PosBlendStart;

        /// <summary>Decay-rate interpolation end error magnitude (m). At or above this, MaxRate is held constant.</summary>
        public float PosBlendEnd;

        /// <summary>
        /// Minimum position correction threshold (m).
        /// Engine: rollback deltas below this value are ignored.
        /// Smoother: when the accumulated error decays at or below this value, snaps to zero.
        /// </summary>
        public float PosMinCorrection;

        /// <summary>
        /// Position teleport distance (m).
        /// Smoother: if the accumulated error is at or above this value, resets immediately instead of decaying (snap).
        /// </summary>
        public float PosTeleportDistance;

        /// <summary>
        /// Minimum rotation correction threshold (degrees).
        /// Engine: rollback yaw deltas below this value are ignored.
        /// Smoother: when the accumulated yaw error decays at or below this value, snaps to zero.
        /// </summary>
        public float RotMinCorrectionDeg;

        /// <summary>
        /// Rotation teleport threshold (degrees).
        /// Smoother: if the accumulated yaw error is at or above this value, resets immediately.
        /// </summary>
        public float RotTeleportDeg;

        /// <summary>
        /// View interpolation rate. blend = 1 - exp(-SmoothingRate * dt).
        /// Higher values cause _smoothed to track _accumulated more aggressively.
        /// </summary>
        public float SmoothingRate;

        public static ErrorCorrectionSettings Default => new()
        {
            MinRate             = 3f,
            MaxRate             = 10f,
            PosBlendStart       = 0.01f,
            PosBlendEnd         = 0.2f,
            PosMinCorrection    = 0.001f,
            PosTeleportDistance  = 1f,
            RotMinCorrectionDeg = 0.05f,
            RotTeleportDeg      = 90f,
            SmoothingRate       = 200f,
        };
    }
}
