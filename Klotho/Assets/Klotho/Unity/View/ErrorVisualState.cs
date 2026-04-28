using System;
using UnityEngine;
using ZLogger;
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

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        [NonSerialized] private bool _everReceivedDelta;
        [NonSerialized] private int  _framesSinceNonZeroDelta;
#endif

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

#if DEVELOPMENT_BUILD || UNITY_EDITOR
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
            Microsoft.Extensions.Logging.ILogger logger = null, int entityIndex = -1)
        {
            // Engine-confirmed teleport — highest priority
            if (teleported)
            {
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
                Reset();
                return;
            }
            float yawAbs = Mathf.Abs(_accumulatedYawError);
            if (yawAbs >= RotTeleportDeg * Mathf.Deg2Rad)
            {
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

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // If the rollback delta stays at 0 for 5 seconds, warn about a possible ErrorCorrection misconfiguration.
            bool deltaNonZero = rollbackDelta.sqrMagnitude > 0f || rollbackYawDelta != 0f;
            if (deltaNonZero)
            {
                _everReceivedDelta = true;
                _framesSinceNonZeroDelta = 0;

                float deltaMag = rollbackDelta.magnitude;
                logger?.ZLogDebug($"[EC][Visual] entity={entityIndex} " +
                    $"delta={deltaMag:F4}m yaw={rollbackYawDelta * Mathf.Rad2Deg:F3}deg " +
                    $"accum={_accumulatedPosError.magnitude:F4}m/{Mathf.Abs(_accumulatedYawError) * Mathf.Rad2Deg:F3}deg " +
                    $"smoothed={_smoothedPosError.magnitude:F4}m/{Mathf.Abs(_smoothedYawError) * Mathf.Rad2Deg:F3}deg");
            }
            else
            {
                _framesSinceNonZeroDelta++;
            }
            if (_everReceivedDelta && _framesSinceNonZeroDelta == 300)
                logger?.ZLogWarning($"[EC] entity={entityIndex}: no delta for 5s — check EnableErrorCorrection / ErrorCorrectionTargetComponent");
#endif
        }

        /// <summary>Immediately initializes accumulation/interpolation state. Configuration fields are preserved.</summary>
        public void Reset()
        {
            _accumulatedPosError = Vector3.zero;
            _accumulatedYawError = 0f;
            _smoothedPosError    = Vector3.zero;
            _smoothedYawError    = 0f;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _everReceivedDelta       = false;
            _framesSinceNonZeroDelta = 0;
#endif
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
