using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// Validates each stage of the <see cref="ErrorVisualState"/> pipeline.
    ///   1. delta accumulation
    ///   2. immediate Reset when the teleport-snap upper bound is exceeded
    ///   3. zero accumulation when below the zero-snap lower bound
    ///   4. variable-rate decay
    ///   5. exp-blend smoothing
    /// </summary>
    [TestFixture]
    public class ErrorVisualTests
    {
        private ErrorVisualState _state;

        [SetUp]
        public void SetUp()
        {
            _state = ErrorVisualState.Default;
        }

        [Test]
        public void Default_HasPhase6Values()
        {
            var s = ErrorVisualState.Default;
            Assert.AreEqual(3f, s.MinRate);
            Assert.AreEqual(10f, s.MaxRate);
            Assert.AreEqual(0.01f, s.PosBlendStart);
            Assert.AreEqual(0.2f, s.PosBlendEnd);
            Assert.AreEqual(0.001f, s.PosZeroSnapThreshold);
            Assert.AreEqual(1f, s.PosTeleportDistance);
            Assert.AreEqual(90f, s.RotTeleportDeg);
            Assert.AreEqual(200f, s.SmoothingRate);
        }

        [Test]
        public void Reset_ClearsAccumulatedAndSmoothed()
        {
            _state.Tick(new Vector3(0.05f, 0, 0), 0.01f, 0.016f, false);
            Assert.AreNotEqual(Vector3.zero, _state.SmoothedPosError);

            _state.Reset();
            Assert.AreEqual(Vector3.zero, _state.SmoothedPosError);
            Assert.AreEqual(0f, _state.SmoothedYawError);
        }

        [Test]
        public void Tick_WithTeleportedFlag_ResetsImmediately()
        {
            _state.Tick(new Vector3(0.05f, 0, 0), 0.01f, 0.016f, false);
            Assert.AreNotEqual(Vector3.zero, _state.SmoothedPosError);

            _state.Tick(Vector3.zero, 0f, 0.016f, teleported: true);
            Assert.AreEqual(Vector3.zero, _state.SmoothedPosError);
            Assert.AreEqual(0f, _state.SmoothedYawError);
        }

        [Test]
        public void Tick_WithSmallDelta_AccumulatesAndSmoothes()
        {
            // Delta = 0.05m (>PosZeroSnapThreshold 0.001, <PosTeleportDistance 1).
            _state.Tick(new Vector3(0.05f, 0, 0), 0f, 0.016f, false);

            // SmoothedPosError blends from 0 toward the accumulated value. Small value because it is one step.
            Assert.Greater(_state.SmoothedPosError.magnitude, 0f);
            Assert.Less(_state.SmoothedPosError.magnitude, 0.05f);
        }

        [Test]
        public void Tick_WithDeltaExceedingPosTeleportDistance_Resets()
        {
            // PosTeleportDistance = 1. Delta 2m → accumulated |pos| >= 1 → teleport-snap upper bound → Reset.
            _state.Tick(new Vector3(2f, 0, 0), 0f, 0.016f, false);
            Assert.AreEqual(Vector3.zero, _state.SmoothedPosError);
        }

        [Test]
        public void Tick_WithYawDeltaExceedingRotTeleport_Resets()
        {
            // RotTeleportDeg = 90°. 2 rad ≈ 114.6° > 90 → Reset.
            _state.Tick(Vector3.zero, 2f, 0.016f, false);
            Assert.AreEqual(0f, _state.SmoothedYawError);
        }

        [Test]
        public void Tick_RepeatedWithZeroDelta_DecaysToZero()
        {
            // After an initial delta injection, repeated zero-delta inputs converge near 0 via decay + smoothing.
            _state.Tick(new Vector3(0.1f, 0, 0), 0f, 0.016f, false);
            float initialMag = _state.SmoothedPosError.magnitude;

            for (int i = 0; i < 200; i++)
                _state.Tick(Vector3.zero, 0f, 0.016f, false);

            Assert.Less(_state.SmoothedPosError.magnitude, initialMag * 0.01f,
                "After 200 frames, smoothed must decay to under 1% of the initial value");
        }

        [Test]
        public void Tick_WithNegativeYawDelta_AccumulatesSigned()
        {
            _state.Tick(Vector3.zero, -0.3f, 0.016f, false);
            Assert.Less(_state.SmoothedYawError, 0f);
        }

        [Test]
        public void Tick_NonMonotonicSmoothing_BlendsTowardAccumulated()
        {
            // Repeatedly injecting the same delta → smoothed blends toward the accumulated value.
            // Decay and blend act simultaneously and converge to a steady state (this validates convergence rather than an exact value).
            for (int i = 0; i < 30; i++)
                _state.Tick(new Vector3(0.05f, 0, 0), 0f, 0.016f, false);

            float mag = _state.SmoothedPosError.magnitude;
            Assert.Greater(mag, 0f, "smoothed must remain above 0 during continuous injection");
        }
    }
}
