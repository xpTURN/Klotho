namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// The engine's rollback-delta filter — the two thresholds below which a correction is not worth
    /// reporting to the view at all.
    ///
    /// After a rollback the engine compares each entity's pre- and post-rollback render-space transform;
    /// a difference below the matching threshold is dropped, and anything above it is exposed through
    /// <c>GetPositionDelta</c> / <c>GetYawDelta</c> / <c>HasEntityTeleported</c>.
    ///
    /// <b>Nothing about how that delta is then damped lives here.</b> Accumulation, variable-rate decay,
    /// zero-snap, teleport-snap and smoothing are the view's, in <c>ErrorVisualState</c>, and they are
    /// tuned per view (Inspector / prefab) rather than per engine.
    ///
    /// This type used to carry seven more fields — MinRate, MaxRate, PosBlendStart, PosBlendEnd,
    /// PosTeleportDistance, RotTeleportDeg, SmoothingRate — that the engine never read. All seven existed
    /// verbatim on <c>ErrorVisualState</c>, so raising one here did nothing while an identically named
    /// knob did the work elsewhere; they were removed for that reason. The two that remain are
    /// deliberately NOT duplicated on the view side, which is why the deletion boundary was simply "the
    /// names that collide" — the boundary was drawn that way on purpose when the view parameters moved out.
    /// </summary>
    public struct ErrorCorrectionSettings
    {
        /// <summary>
        /// Minimum position correction threshold (m). Rollback deltas below this value are ignored.
        /// </summary>
        public float PosMinCorrection;

        /// <summary>
        /// Minimum rotation correction threshold (degrees). Rollback yaw deltas below this value are ignored.
        /// </summary>
        public float RotMinCorrectionDeg;

        public static ErrorCorrectionSettings Default => new()
        {
            PosMinCorrection    = 0.001f,
            RotMinCorrectionDeg = 0.05f,
        };
    }
}
