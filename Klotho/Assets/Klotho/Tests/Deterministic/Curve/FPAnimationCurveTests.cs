using System;
using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Deterministic.Curve.Tests
{
    [TestFixture]
    public class FPAnimationCurveTests
    {
        private const float EPSILON = 0.001f;

        #region Construction

        [Test]
        public void EmptyKeyframes_Evaluate_ReturnsZero()
        {
            var curve = new FPAnimationCurve(new FPKeyframe[0]);
            Assert.AreEqual(0f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void SingleKeyframe_Evaluate_ReturnsConstantValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.FromFloat(3.0f))
            });

            Assert.AreEqual(3.0f, curve.Evaluate(FP64.Zero).ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, curve.Evaluate(FP64.One).ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, curve.Evaluate(FP64.FromFloat(10.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void UnsortedInput_SortsCorrectly()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.FromFloat(1.0f), FP64.FromFloat(10.0f)),
                new FPKeyframe(FP64.FromFloat(0.0f), FP64.FromFloat(0.0f)),
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.FromFloat(5.0f)),
            });

            Assert.AreEqual(0.0f, curve[0].time.ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, curve[1].time.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve[2].time.ToFloat(), EPSILON);
        }

        #endregion

        #region Linear Interpolation

        [Test]
        public void TwoLinearKeyframes_EvaluateAtMidpoint()
        {
            // Linear tangent: slope = (1-0)/(1-0) = 1
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
            });

            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void TwoLinearKeyframes_EvaluateAtQuarterPoints()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
            });

            Assert.AreEqual(0.25f, curve.Evaluate(FP64.FromFloat(0.25f)).ToFloat(), EPSILON);
            Assert.AreEqual(0.75f, curve.Evaluate(FP64.FromFloat(0.75f)).ToFloat(), EPSILON);
        }

        #endregion

        #region Cubic Hermite

        [Test]
        public void CubicHermite_ZeroTangents_EaseInOut()
        {
            // Zero tangents produce an ease-in/ease-out curve
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero),
                new FPKeyframe(FP64.One, FP64.One, FP64.Zero, FP64.Zero),
            });

            float mid = curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat();
            // At t=0.5 with zero tangents, Hermite returns exactly 0.5
            Assert.AreEqual(0.5f, mid, EPSILON);

            // At t=0.25 should be below linear (ease-in); at t=0.75 should be above linear (ease-out)
            float q1 = curve.Evaluate(FP64.FromFloat(0.25f)).ToFloat();
            float q3 = curve.Evaluate(FP64.FromFloat(0.75f)).ToFloat();
            Assert.Less(q1, 0.25f);
            Assert.Greater(q3, 0.75f);
        }

        [Test]
        public void CubicHermite_LargeTangents_NoOverflow()
        {
            // Large tangents must not overflow (SafeMultiply saturates)
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.Zero, FP64.FromFloat(100.0f)),
                new FPKeyframe(FP64.One, FP64.One, FP64.FromFloat(100.0f), FP64.Zero),
            });

            // Must not throw and must return a finite value
            var result = curve.Evaluate(FP64.FromFloat(0.5f));
            Assert.IsTrue(result.ToFloat() > -100000f && result.ToFloat() < 100000f);
        }

        [Test]
        public void CubicHermite_NegativeTangents_WorksCorrectly()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.One, FP64.Zero, FP64.FromFloat(-2.0f)),
                new FPKeyframe(FP64.One, FP64.Zero, FP64.FromFloat(-2.0f), FP64.Zero),
            });

            // Endpoints
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.Zero).ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, curve.Evaluate(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void CubicHermite_EndpointsReturnExactValues()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.FromFloat(0.0f), FP64.FromFloat(5.0f), FP64.Zero, FP64.FromFloat(2.0f)),
                new FPKeyframe(FP64.FromFloat(1.0f), FP64.FromFloat(10.0f), FP64.FromFloat(2.0f), FP64.Zero),
            });

            Assert.AreEqual(5.0f, curve.Evaluate(FP64.FromFloat(0.0f)).ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, curve.Evaluate(FP64.FromFloat(1.0f)).ToFloat(), EPSILON);
        }

        #endregion

        #region Constant (Step) Mode

        [Test]
        public void ConstantTangent_ReturnsLeftKeyframeValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.FromFloat(1.0f), FP64.Zero, FP64.MaxValue),
                new FPKeyframe(FP64.One, FP64.FromFloat(5.0f), FP64.MaxValue, FP64.Zero),
            });

            // Between keys: should return the left keyframe value (step behavior)
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(0.25f)).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(0.99f)).ToFloat(), EPSILON);
        }

        [Test]
        public void ConstantTangent_AtExactKeyTime_ReturnsKeyValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.FromFloat(1.0f), FP64.Zero, FP64.MaxValue),
                new FPKeyframe(FP64.One, FP64.FromFloat(5.0f), FP64.MaxValue, FP64.Zero),
            });

            Assert.AreEqual(1.0f, curve.Evaluate(FP64.Zero).ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, curve.Evaluate(FP64.One).ToFloat(), EPSILON);
        }

        #endregion

        #region Wrap Modes

        [Test]
        public void Clamp_BeforeStart_ReturnsFirstValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.FromFloat(1.0f), FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.FromFloat(2.0f), FP64.One, FP64.One),
            });

            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(-1.0f)).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(-100.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Clamp_AfterEnd_ReturnsLastValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.FromFloat(1.0f), FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.FromFloat(2.0f), FP64.One, FP64.One),
            });

            Assert.AreEqual(2.0f, curve.Evaluate(FP64.FromFloat(2.0f)).ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, curve.Evaluate(FP64.FromFloat(100.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Loop_CyclesCorrectly()
        {
            // Linear curve from (0,0) to (1,1) with Loop wrapping
            var curve = new FPAnimationCurve(
                new[]
                {
                    new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                    new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
                },
                FPWrapMode.Clamp,
                FPWrapMode.Loop
            );

            // t=1.5 loops to t=0.5 -> value ~= 0.5
            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(1.5f)).ToFloat(), EPSILON);
            // t=2.25 loops to t=0.25 -> value ~= 0.25
            Assert.AreEqual(0.25f, curve.Evaluate(FP64.FromFloat(2.25f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Loop_NegativeTime_WrapsCorrectly()
        {
            var curve = new FPAnimationCurve(
                new[]
                {
                    new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                    new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
                },
                FPWrapMode.Loop,
                FPWrapMode.Clamp
            );

            // t=-0.5 wraps to t=0.5
            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(-0.5f)).ToFloat(), EPSILON);
            // t=-0.25 wraps to t=0.75
            Assert.AreEqual(0.75f, curve.Evaluate(FP64.FromFloat(-0.25f)).ToFloat(), EPSILON);
        }

        [Test]
        public void PingPong_CyclesCorrectly()
        {
            var curve = new FPAnimationCurve(
                new[]
                {
                    new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                    new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
                },
                FPWrapMode.Clamp,
                FPWrapMode.PingPong
            );

            // t=1.5 ping-pongs back to t=0.5
            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(1.5f)).ToFloat(), EPSILON);
            // t=1.75 ping-pongs back to t=0.25
            Assert.AreEqual(0.25f, curve.Evaluate(FP64.FromFloat(1.75f)).ToFloat(), EPSILON);
        }

        [Test]
        public void PingPong_ReversePhase_MirrorsValues()
        {
            var curve = new FPAnimationCurve(
                new[]
                {
                    new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                    new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
                },
                FPWrapMode.Clamp,
                FPWrapMode.PingPong
            );

            // Forward: t=0.5 -> 0.5
            float forward = curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat();
            // Reverse: t=1.5 -> mirrored to 0.5
            float reverse = curve.Evaluate(FP64.FromFloat(1.5f)).ToFloat();
            Assert.AreEqual(forward, reverse, EPSILON);
        }

        #endregion

        #region Multi-Segment

        [Test]
        public void FiveKeyframes_EvaluateAtEachKey_ReturnsExactValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.FromFloat(0.0f), FP64.FromFloat(0.0f), FP64.One, FP64.One),
                new FPKeyframe(FP64.FromFloat(0.25f), FP64.FromFloat(1.0f), FP64.One, FP64.One),
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.FromFloat(0.5f), FP64.One, FP64.One),
                new FPKeyframe(FP64.FromFloat(0.75f), FP64.FromFloat(2.0f), FP64.One, FP64.One),
                new FPKeyframe(FP64.FromFloat(1.0f), FP64.FromFloat(1.0f), FP64.One, FP64.One),
            });

            Assert.AreEqual(0.0f, curve.Evaluate(FP64.FromFloat(0.0f)).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(0.25f)).ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, curve.Evaluate(FP64.FromFloat(0.75f)).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.FromFloat(1.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void FiveKeyframes_EvaluateBetweenKeys_InterpolatesCorrectly()
        {
            // Linear tangents for verification
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.FromFloat(0.0f), FP64.FromFloat(0.0f), FP64.FromFloat(4.0f), FP64.FromFloat(4.0f)),
                new FPKeyframe(FP64.FromFloat(0.25f), FP64.FromFloat(1.0f), FP64.FromFloat(4.0f), FP64.FromFloat(-2.0f)),
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.FromFloat(0.5f), FP64.FromFloat(-2.0f), FP64.FromFloat(6.0f)),
                new FPKeyframe(FP64.FromFloat(0.75f), FP64.FromFloat(2.0f), FP64.FromFloat(6.0f), FP64.FromFloat(-4.0f)),
                new FPKeyframe(FP64.FromFloat(1.0f), FP64.FromFloat(1.0f), FP64.FromFloat(-4.0f), FP64.Zero),
            });

            // Verify binary search works — values between keys should be interpolated (not an exact Hermite value check)
            float v1 = curve.Evaluate(FP64.FromFloat(0.125f)).ToFloat();
            Assert.IsTrue(v1 > -1.0f && v1 < 3.0f, $"Expected a reasonable interpolated value but got {v1}");

            float v2 = curve.Evaluate(FP64.FromFloat(0.625f)).ToFloat();
            Assert.IsTrue(v2 > -1.0f && v2 < 4.0f, $"Expected a reasonable interpolated value but got {v2}");
        }

        #endregion

        #region Determinism

        [Test]
        public void SameInput_ProducesSameOutput_BitExact()
        {
            var curve = FPAnimationCurve.EaseInOut();

            var t = FP64.FromFloat(0.37f);
            long r1 = curve.Evaluate(t).RawValue;
            long r2 = curve.Evaluate(t).RawValue;
            Assert.AreEqual(r1, r2, "Deterministic evaluation must produce identical raw values");

            // Multiple sample points
            for (int i = 0; i <= 20; i++)
            {
                var time = FP64.FromFloat(i / 20.0f);
                long a = curve.Evaluate(time).RawValue;
                long b = curve.Evaluate(time).RawValue;
                Assert.AreEqual(a, b, $"Mismatch at t={i}/20");
            }
        }

        #endregion

        #region Serialization

        [Test]
        public void SerializeDeserialize_RoundTrip_BitExact()
        {
            var original = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.FromFloat(1.0f), FP64.FromFloat(-0.5f), FP64.FromFloat(2.0f)),
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.FromFloat(3.0f), FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.FromFloat(0.0f), FP64.FromFloat(-1.0f), FP64.Zero),
            });

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);

            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            var deserialized = FPAnimationCurve.Deserialize(ref reader);

            Assert.AreEqual(original.Length, deserialized.Length);
            for (int i = 0; i < original.Length; i++)
            {
                Assert.AreEqual(original[i].time.RawValue, deserialized[i].time.RawValue);
                Assert.AreEqual(original[i].value.RawValue, deserialized[i].value.RawValue);
                Assert.AreEqual(original[i].inTangent.RawValue, deserialized[i].inTangent.RawValue);
                Assert.AreEqual(original[i].outTangent.RawValue, deserialized[i].outTangent.RawValue);
            }

            // Evaluate both and compare
            for (int i = 0; i <= 10; i++)
            {
                var t = FP64.FromFloat(i / 10.0f);
                Assert.AreEqual(original.Evaluate(t).RawValue, deserialized.Evaluate(t).RawValue,
                    $"Evaluation mismatch at t={i}/10");
            }
        }

        [Test]
        public void SerializeDeserialize_PreservesWrapModes()
        {
            var original = new FPAnimationCurve(
                new[]
                {
                    new FPKeyframe(FP64.Zero, FP64.Zero),
                    new FPKeyframe(FP64.One, FP64.One),
                },
                FPWrapMode.Loop,
                FPWrapMode.PingPong
            );

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);

            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            var deserialized = FPAnimationCurve.Deserialize(ref reader);

            Assert.AreEqual(FPWrapMode.Loop, deserialized.PreWrapMode);
            Assert.AreEqual(FPWrapMode.PingPong, deserialized.PostWrapMode);
        }

        #endregion

        #region Factory Methods

        [Test]
        public void Linear_ReturnsExpectedValues()
        {
            var curve = FPAnimationCurve.Linear();

            Assert.AreEqual(0.0f, curve.Evaluate(FP64.Zero).ToFloat(), EPSILON);
            Assert.AreEqual(0.25f, curve.Evaluate(FP64.FromFloat(0.25f)).ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);
            Assert.AreEqual(0.75f, curve.Evaluate(FP64.FromFloat(0.75f)).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void EaseInOut_ReturnsExpectedValues()
        {
            var curve = FPAnimationCurve.EaseInOut();

            // Endpoints
            Assert.AreEqual(0.0f, curve.Evaluate(FP64.Zero).ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, curve.Evaluate(FP64.One).ToFloat(), EPSILON);

            // Midpoint should be 0.5
            Assert.AreEqual(0.5f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);

            // Ease-in: slow at the start
            float q1 = curve.Evaluate(FP64.FromFloat(0.25f)).ToFloat();
            Assert.Less(q1, 0.25f, "Ease-in should be below linear at t=0.25");

            // Ease-out: fast at the end
            float q3 = curve.Evaluate(FP64.FromFloat(0.75f)).ToFloat();
            Assert.Greater(q3, 0.75f, "Ease-out should be above linear at t=0.75");
        }

        [Test]
        public void Constant_ReturnsFixedValue()
        {
            var curve = FPAnimationCurve.Constant(FP64.FromFloat(7.5f));

            Assert.AreEqual(7.5f, curve.Evaluate(FP64.Zero).ToFloat(), EPSILON);
            Assert.AreEqual(7.5f, curve.Evaluate(FP64.FromFloat(0.5f)).ToFloat(), EPSILON);
            Assert.AreEqual(7.5f, curve.Evaluate(FP64.One).ToFloat(), EPSILON);
        }

        #endregion

        #region Properties

        [Test]
        public void Duration_ReturnsCorrectValue()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.Zero),
                new FPKeyframe(FP64.FromFloat(2.5f), FP64.One),
            });

            Assert.AreEqual(2.0f, curve.Duration.ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, curve.StartTime.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, curve.EndTime.ToFloat(), EPSILON);
        }

        [Test]
        public void Length_ReturnsKeyframeCount()
        {
            var curve = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero),
                new FPKeyframe(FP64.FromFloat(0.5f), FP64.One),
                new FPKeyframe(FP64.One, FP64.Zero),
            });

            Assert.AreEqual(3, curve.Length);
        }

        #endregion

        #region Unity Bridge

        [Test]
        public void FromAnimationCurve_ConvertsKeyframesCorrectly()
        {
            var unity = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            var fp = new FPAnimationCurve(new FPKeyframe[0]);
            fp.FromAnimationCurve(unity);

            Assert.AreEqual(unity.keys.Length, fp.Length);
            Assert.AreEqual(unity.keys[0].time, fp[0].time.ToFloat(), EPSILON);
            Assert.AreEqual(unity.keys[0].value, fp[0].value.ToFloat(), EPSILON);
            Assert.AreEqual(unity.keys[1].time, fp[1].time.ToFloat(), EPSILON);
            Assert.AreEqual(unity.keys[1].value, fp[1].value.ToFloat(), EPSILON);
        }

        [Test]
        public void FromAnimationCurve_ConvertsWrapModesCorrectly()
        {
            var unity = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            unity.preWrapMode = WrapMode.Loop;
            unity.postWrapMode = WrapMode.PingPong;

            var fp = new FPAnimationCurve(new FPKeyframe[0]);
            fp.FromAnimationCurve(unity);

            Assert.AreEqual(FPWrapMode.Loop, fp.PreWrapMode);
            Assert.AreEqual(FPWrapMode.PingPong, fp.PostWrapMode);
        }

        [Test]
        public void FromAnimationCurve_InfinityTangent_ConvertsToMaxValue()
        {
            var keys = new Keyframe[]
            {
                new Keyframe(0f, 0f, float.PositiveInfinity, float.PositiveInfinity),
                new Keyframe(1f, 1f, float.PositiveInfinity, float.PositiveInfinity),
            };
            var unity = new AnimationCurve(keys);
            var fp = new FPAnimationCurve(new FPKeyframe[0]);
            fp.FromAnimationCurve(unity);

            Assert.AreEqual(FP64.MaxValue, fp[0].inTangent);
            Assert.AreEqual(FP64.MaxValue, fp[0].outTangent);
        }

        [Test]
        public void ToAnimationCurve_ConvertsKeyframesCorrectly()
        {
            var fp = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
            });
            var unity = fp.ToAnimationCurve();

            Assert.AreEqual(2, unity.keys.Length);
            Assert.AreEqual(0f, unity.keys[0].time, EPSILON);
            Assert.AreEqual(0f, unity.keys[0].value, EPSILON);
            Assert.AreEqual(1f, unity.keys[1].time, EPSILON);
            Assert.AreEqual(1f, unity.keys[1].value, EPSILON);
        }

        [Test]
        public void ToAnimationCurve_MaxValueTangent_ConvertsToInfinity()
        {
            var fp = new FPAnimationCurve(new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.MaxValue, FP64.MaxValue),
                new FPKeyframe(FP64.One, FP64.One, FP64.MaxValue, FP64.MaxValue),
            });
            var unity = fp.ToAnimationCurve();

            Assert.IsTrue(float.IsPositiveInfinity(unity.keys[0].inTangent));
            Assert.IsTrue(float.IsPositiveInfinity(unity.keys[0].outTangent));
        }

        [Test]
        public void ToAnimationCurve_ConvertsWrapModesCorrectly()
        {
            var fp = new FPAnimationCurve(
                new[]
                {
                    new FPKeyframe(FP64.Zero, FP64.Zero),
                    new FPKeyframe(FP64.One, FP64.One),
                },
                FPWrapMode.Loop,
                FPWrapMode.PingPong
            );
            var unity = fp.ToAnimationCurve();

            Assert.AreEqual(WrapMode.Loop, unity.preWrapMode);
            Assert.AreEqual(WrapMode.PingPong, unity.postWrapMode);
        }

        [Test]
        public void RoundTrip_UnityToFPAndBack_PreservesKeyframes()
        {
            var original = new AnimationCurve(
                new Keyframe(0f, 0f, 0f, 2f),
                new Keyframe(0.5f, 1f, 2f, -1f),
                new Keyframe(1f, 0f, -1f, 0f)
            );
            original.preWrapMode = WrapMode.Clamp;
            original.postWrapMode = WrapMode.Loop;

            var fp = new FPAnimationCurve(new FPKeyframe[0]);
            fp.FromAnimationCurve(original);
            var roundTrip = fp.ToAnimationCurve();

            Assert.AreEqual(original.keys.Length, roundTrip.keys.Length);
            for (int i = 0; i < original.keys.Length; i++)
            {
                Assert.AreEqual(original.keys[i].time, roundTrip.keys[i].time, EPSILON);
                Assert.AreEqual(original.keys[i].value, roundTrip.keys[i].value, EPSILON);
            }
            Assert.AreEqual(original.preWrapMode, roundTrip.preWrapMode);
            Assert.AreEqual(original.postWrapMode, roundTrip.postWrapMode);
        }

        #endregion
    }
}
