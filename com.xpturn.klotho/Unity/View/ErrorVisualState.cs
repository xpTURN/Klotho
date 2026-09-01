using System;
using xpTURN.Klotho.Logging;
using UnityEngine;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Struct that bundles the state and tuning parameters of the per-view error visual pipeline.
    /// Operates independently from the engine-side delta filters (ErrorCorrectionSettings.PosMinCorrection, RotMinCorrectionDeg).
    ///
    /// Pipeline performed in Tick:
    ///   1. Accumulate rollback delta
    ///   2. Reset immediately if Teleport-snap upper bound (PosTeleportDistance / RotTeleportDeg) is exceeded
    ///   3. Snap to zero if below the Zero-snap lower bound (PosZeroSnapThreshold / RotZeroSnapThresholdDeg)
    ///   4. Apply variable decay rate proportional to accumulated magnitude (pos/rot independent)
    ///   5. Exp-blend smoothing based on SmoothingRate
    ///
    /// Stages 4 and 5 are not equal partners at the default tuning, and the split is deliberate. Stage 4
    /// does the visible work: at MaxRate the accumulated error has a 100ms time constant, but the rate
    /// falls toward MinRate below PosBlendEnd, so a 0.3m correction takes roughly 1.5s to reach the
    /// zero-snap threshold (framerate-independent). Stage 5 is a first-order lag with
    /// a time constant of 1/SmoothingRate — 5ms at the default 200, well under one frame — so it mostly
    /// passes the accumulator straight through. It is framerate-coupled rather than inert: blend is
    /// 1 - exp(-SmoothingRate * dt), which is 0.999 at 30fps, 0.964 at 60fps, and 0.751 at 144fps.
    ///
    /// A high SmoothingRate is what the pipeline wants, not a mistuning. The smoothed value IS the offset
    /// that hides the rollback jump: the view renders (corrected position + smoothed), so it only stays
    /// where the player last saw it while smoothed tracks the accumulated delta closely. Lowering the
    /// rate makes the offset shallower on arrival and exposes more of the jump — at 20 a 0.3m correction
    /// would show 0.23m of it immediately.
    /// </summary>
    [Serializable]
    public struct ErrorVisualState
    {
        // ── Decay rate (1, 4) ──

        [Tooltip("Decay rate lower bound. Applied when the error is at or below PosBlendStart (RotBlendStartDeg for Rot).")]
        public float MinRate;

        [Tooltip("Decay rate upper bound. Applied when the error is at or above PosBlendEnd (RotBlendEndDeg for Rot).")]
        public float MaxRate;

        // ── Position pipeline ──

        [Tooltip("Position decay rate interpolation start (m). MinRate is used at or below this value.")]
        public float PosBlendStart;

        [Tooltip("Position decay rate interpolation end (m). MaxRate is used at or above this value.")]
        public float PosBlendEnd;

        /// <summary>
        /// View-side zero-snap threshold. Snaps to zero when the accumulated error drops at or below this value during decay.
        /// Can be tuned independently from the engine filter value.
        /// </summary>
        [Tooltip("Position zero-snap threshold (m). Snaps to zero when accumulated error drops at or below this value during decay.")]
        public float PosZeroSnapThreshold;

        [Tooltip("Position teleport-snap threshold (m). Resets immediately when the accumulated error reaches or exceeds this value.")]
        public float PosTeleportDistance;

        // ── Rotation pipeline ──

        /// <summary>Rotation decay rate interpolation start (deg). MinRate is applied at or below this value.</summary>
        [Tooltip("Rotation decay rate interpolation start (deg). MinRate is used at or below this value.")]
        public float RotBlendStartDeg;

        /// <summary>Rotation decay rate interpolation end (deg). MaxRate is applied at or above this value.</summary>
        [Tooltip("Rotation decay rate interpolation end (deg). MaxRate is used at or above this value.")]
        public float RotBlendEndDeg;

        /// <summary>View-side rotation zero-snap threshold. Operates independently of the engine filter value.</summary>
        [Tooltip("Rotation zero-snap threshold (deg). Snaps to zero when the accumulated error drops at or below this value during decay.")]
        public float RotZeroSnapThresholdDeg;

        [Tooltip("Rotation teleport-snap threshold (deg).")]
        public float RotTeleportDeg;

        // ── Smoothing (5) ──

        [Tooltip("View interpolation rate. blend = 1 - exp(-SmoothingRate * dt).")]
        public float SmoothingRate;

        // ── Runtime state (hidden from Inspector) ──

        [NonSerialized] private Vector3 _accumulatedPosError;
        [NonSerialized] private float   _accumulatedYawError;
        [NonSerialized] private Vector3 _smoothedPosError;
        [NonSerialized] private float   _smoothedYawError;


        /// <summary>Default values. Uses the same initial thresholds as the engine filter.</summary>
        public static ErrorVisualState Default => new()
        {
            MinRate                 = 3f,
            MaxRate                 = 10f,
            PosBlendStart           = 0.01f,
            PosBlendEnd             = 0.2f,
            PosZeroSnapThreshold    = 0.001f,
            PosTeleportDistance     = 1f,
            RotBlendStartDeg        = 0.573f,   // ≈ 0.01 rad
            RotBlendEndDeg          = 11.46f,   // ≈ 0.2 rad
            RotZeroSnapThresholdDeg = 0.05f,
            RotTeleportDeg          = 90f,
            SmoothingRate           = 200f,
        };

        /// <summary>Final view output — used directly as the ErrorVisualVector of ApplyTransform.</summary>
        public Vector3 SmoothedPosError => _smoothedPosError;

        /// <summary>Final view output — Y-axis radians. Used after conversion via Quaternion.Euler.</summary>
        public float SmoothedYawError => _smoothedYawError;

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        public float AccumulatedPosMagnitude => _accumulatedPosError.magnitude;
#endif

        /// <summary>
        /// Per-frame refresh. Consumes the entity rollback delta and teleport intent.
        /// </summary>
        /// <param name="rollbackDelta">PuP frame - Predicted frame position difference (m).</param>
        /// <param name="rollbackYawDelta">Same as above, Y-axis radians.</param>
        /// <param name="deltaTime">Unity Time.deltaTime.</param>
        /// <param name="teleported">Engine-confirmed teleport. Resets immediately when true.</param>
        /// <param name="logger">For debug logging (nullable).</param>
        /// <param name="entityIndex">Entity index for debug logging.</param>
        public void Tick(
            Vector3 rollbackDelta, float rollbackYawDelta, float deltaTime, bool teleported,
            IKLogger logger = null, int entityIndex = -1)
        {
            // Engine-confirmed teleport — highest priority
            if (teleported)
            {
                LogDiscarded(logger, entityIndex, "teleport", rollbackDelta, rollbackYawDelta, 0f);
                Reset();
                return;
            }

            // Stage ① delta accumulation
            _accumulatedPosError += rollbackDelta;
            _accumulatedYawError += rollbackYawDelta;

            // Threshold A. Teleport-snap upper bound — excessive accumulation → reset immediately
            float posMag = _accumulatedPosError.magnitude;
            if (posMag >= PosTeleportDistance)
            {
                LogDiscarded(logger, entityIndex, "pos-snap", rollbackDelta, rollbackYawDelta, PosTeleportDistance);
                Reset();
                return;
            }
            float yawAbs = Mathf.Abs(_accumulatedYawError);
            if (yawAbs >= RotTeleportDeg * Mathf.Deg2Rad)
            {
                LogDiscarded(logger, entityIndex, "yaw-snap", rollbackDelta, rollbackYawDelta, RotTeleportDeg);
                Reset();
                return;
            }

            // Zero-snap lower bound — snaps to zero when the accumulated error is tiny.
            if (posMag > 0f && posMag <= PosZeroSnapThreshold)
                _accumulatedPosError = Vector3.zero;

            float yawZeroSnapRad = RotZeroSnapThresholdDeg * Mathf.Deg2Rad;
            if (yawAbs > 0f && yawAbs <= yawZeroSnapRad)
                _accumulatedYawError = 0f;

            // Variable-rate decay. pos/rot are handled independently.
            // A linear approximation like (1 - rate*dt) can flip sign and oscillate when rate*dt > 1, so exp is used.
            float posMagAfter = _accumulatedPosError.magnitude;
            if (posMagAfter > 0f)
            {
                float rate = ComputeDecayRatePos(posMagAfter);
                _accumulatedPosError *= Mathf.Exp(-rate * deltaTime);
            }

            float yawAbsAfter = Mathf.Abs(_accumulatedYawError);
            if (yawAbsAfter > 0f)
            {
                float rate = ComputeDecayRateRot(yawAbsAfter);
                _accumulatedYawError *= Mathf.Exp(-rate * deltaTime);
            }

            // Exp-blend smoothing. Interpolates the accumulated error to produce the smoothed value.
            float blend = 1f - Mathf.Exp(-SmoothingRate * deltaTime);
            _smoothedPosError = Vector3.Lerp(_smoothedPosError, _accumulatedPosError, blend);
            _smoothedYawError = Mathf.Lerp(_smoothedYawError, _accumulatedYawError, blend);

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            // What the engine actually handed this view, per rollback. A "no delta for 300 frames" warning
            // used to sit beside it, asking whether the entity carried ErrorCorrectionTargetComponent — but
            // it was gated on having received a delta at least once, and deltas are only produced for
            // entities that carry it, so the gate was already the answer. The reachable question ("nobody
            // wired it anywhere") is the engine's, and CapturePreRollbackTransforms detects it directly.
            // Steady input predicting perfectly is what the warning actually fired on.
            if (rollbackDelta.sqrMagnitude > 0f || rollbackYawDelta != 0f)
            {
                float deltaMag = rollbackDelta.magnitude;
                logger?.KDebug($"[EC][Visual] entity={entityIndex} " +
                    $"delta={deltaMag:F4}m yaw={rollbackYawDelta * Mathf.Rad2Deg:F3}deg " +
                    $"accum={_accumulatedPosError.magnitude:F4}m/{Mathf.Abs(_accumulatedYawError) * Mathf.Rad2Deg:F3}deg " +
                    $"smoothed={_smoothedPosError.magnitude:F4}m/{Mathf.Abs(_smoothedYawError) * Mathf.Rad2Deg:F3}deg");
            }
#endif
        }

        /// <summary>
        /// The other half of the <c>[EC][Visual]</c> line: a correction the smoother threw away.
        ///
        /// Every reset path returns before that line, so a delta the view snapped on left no trace at all
        /// and the log could not tell "the view never received one" from "the view received one and
        /// discarded it". That is not a rare corner — a mispredicted turn reaches the 90° bound in a
        /// single tick, and a live P2P session showed exactly that: an engine-side delta of 90.000deg
        /// with no view-side line anywhere near it. Reports what arrived and what was standing before
        /// the reset, so the discarded amount is visible rather than inferred.
        ///
        /// Silent when there is nothing to say — a teleport reset on a view that had accumulated nothing
        /// is a no-op, and this runs per view per frame.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD"), System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void LogDiscarded(IKLogger logger, int entityIndex, string reason,
                                  Vector3 rollbackDelta, float rollbackYawDelta, float bound)
        {
            if (logger == null) return;
            bool arrived  = rollbackDelta.sqrMagnitude > 0f || rollbackYawDelta != 0f;
            bool standing = _accumulatedPosError.sqrMagnitude > 0f || _accumulatedYawError != 0f;
            if (!arrived && !standing) return;

            logger.KDebug($"[EC][Visual] entity={entityIndex} snapped reason={reason} bound={bound:F3} " +
                $"delta={rollbackDelta.magnitude:F4}m/{rollbackYawDelta * Mathf.Rad2Deg:F3}deg " +
                $"discarded={_accumulatedPosError.magnitude:F4}m/{Mathf.Abs(_accumulatedYawError) * Mathf.Rad2Deg:F3}deg");
        }

        /// <summary>Immediately initializes accumulation/interpolation state. Configuration fields are preserved.</summary>
        public void Reset()
        {
            _accumulatedPosError = Vector3.zero;
            _accumulatedYawError = 0f;
            _smoothedPosError    = Vector3.zero;
            _smoothedYawError    = 0f;
        }

        // ── Independent position/rotation decay rate calculation ──

        private float ComputeDecayRatePos(float errorMag_m)
        {
            if (errorMag_m <= PosBlendStart) return MinRate;
            if (errorMag_m >= PosBlendEnd)   return MaxRate;
            float t = (errorMag_m - PosBlendStart) / (PosBlendEnd - PosBlendStart);
            return MinRate + t * (MaxRate - MinRate);
        }

        private float ComputeDecayRateRot(float errorMag_rad)
        {
            float startRad = RotBlendStartDeg * Mathf.Deg2Rad;
            float endRad   = RotBlendEndDeg   * Mathf.Deg2Rad;
            if (errorMag_rad <= startRad) return MinRate;
            if (errorMag_rad >= endRad)   return MaxRate;
            float t = (errorMag_rad - startRad) / (endRad - startRad);
            return MinRate + t * (MaxRate - MinRate);
        }
    }
}
