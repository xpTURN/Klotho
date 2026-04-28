using System;
using UnityEngine;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    /// <summary>
    /// FP64 fixed-point arithmetic test
    /// </summary>
    [TestFixture]
    public class FP64Tests
    {
        private const float EPSILON = 0.001f;

        #region Creation and Conversion

        [Test]
        public void FromInt_CreatesCorrectValue()
        {
            var fp = FP64.FromInt(5);
            Assert.AreEqual(5, fp.ToInt());
            Assert.AreEqual(5.0f, fp.ToFloat(), EPSILON);
        }

        [Test]
        public void FromFloat_CreatesCorrectValue()
        {
            var fp = FP64.FromFloat(3.14f);
            Assert.AreEqual(3.14f, fp.ToFloat(), EPSILON);
        }

        [Test]
        public void FromDouble_CreatesCorrectValue()
        {
            var fp = FP64.FromDouble(2.71828);
            Assert.AreEqual(2.71828, fp.ToDouble(), EPSILON);
        }

        [Test]
        public void FromRaw_PreservesValue()
        {
            long rawValue = 12345678L;
            var fp = FP64.FromRaw(rawValue);
            Assert.AreEqual(rawValue, fp.RawValue);
        }

        [Test]
        public void NegativeValues_WorkCorrectly()
        {
            var fp = FP64.FromFloat(-5.5f);
            Assert.AreEqual(-5.5f, fp.ToFloat(), EPSILON);
        }

        [Test]
        public void ImplicitIntConversion_WorksCorrectly()
        {
            FP64 fp = 7;
            Assert.AreEqual(7, fp.ToInt());
            Assert.AreEqual(7.0f, fp.ToFloat(), EPSILON);
        }

        [Test]
        public void FromFloat_MaxIntRange_PreservesValue()
        {
            var large = FP64.FromFloat(100000.0f);
            Assert.AreEqual(100000.0f, large.ToFloat(), 1.0f);

            var maxInt = FP64.FromInt(int.MaxValue);
            Assert.AreEqual(int.MaxValue, maxInt.ToInt());
        }

        [Test]
        public void ToString_ReturnsFormattedString()
        {
            Assert.IsNotNull(FP64.FromFloat(3.14f).ToString());
            Assert.IsTrue(FP64.FromFloat(-0.5f).ToString().Contains("-"));
            Assert.AreEqual("0.0000", FP64.Zero.ToString());
        }

        #endregion

        #region Basic Operations

        [Test]
        public void Addition_WorksCorrectly()
        {
            var a = FP64.FromFloat(3.5f);
            var b = FP64.FromFloat(2.5f);
            var result = a + b;
            Assert.AreEqual(6.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.FromFloat(3.0f);
            var result = a - b;
            Assert.AreEqual(2.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Multiplication_WorksCorrectly()
        {
            var a = FP64.FromFloat(3.0f);
            var b = FP64.FromFloat(4.0f);
            var result = a * b;
            Assert.AreEqual(12.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Multiplication_WithFractions_WorksCorrectly()
        {
            var a = FP64.FromFloat(2.5f);
            var b = FP64.FromFloat(4.0f);
            var result = a * b;
            Assert.AreEqual(10.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Division_WorksCorrectly()
        {
            var a = FP64.FromFloat(10.0f);
            var b = FP64.FromFloat(2.0f);
            var result = a / b;
            Assert.AreEqual(5.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Division_WithFractions_WorksCorrectly()
        {
            var a = FP64.FromFloat(7.0f);
            var b = FP64.FromFloat(2.0f);
            var result = a / b;
            Assert.AreEqual(3.5f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Division_ByZero_ThrowsException()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.Zero;
            Assert.Throws<DivideByZeroException>(() => { var result = a / b; });
        }

        [Test]
        public void Division_LargeValues_NoOverflow()
        {
            // Large value division (overflow prevention test)
            var a = FP64.FromFloat(1000.0f);
            var b = FP64.FromFloat(10.0f);
            var result = a / b;
            Assert.AreEqual(100.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Modulo_WorksCorrectly()
        {
            var a = FP64.FromFloat(7.0f);
            var b = FP64.FromFloat(3.0f);
            var result = a % b;
            Assert.AreEqual(1.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Modulo_ByZero_ReturnsZero()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.Zero;
            var result = a % b;
            Assert.AreEqual(FP64.Zero, result);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var a = FP64.FromFloat(5.0f);
            var result = -a;
            Assert.AreEqual(-5.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Multiplication_FastPathBoundary_WorksCorrectly()
        {
            // |raw| = 0x7FFFFFFF is the largest value that takes the fast path
            var a = FP64.FromRaw(0x7FFFFFFFL);
            var b = FP64.FromRaw(0x7FFFFFFFL);
            var result = a * b;
            // Both values ≈ 0.5, result ≈ 0.25
            Assert.AreEqual(0.5f * 0.5f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Multiplication_SlowPath_LargeValues_WorksCorrectly()
        {
            var a = FP64.FromFloat(50000.0f);
            var b = FP64.FromFloat(2.0f);
            Assert.AreEqual(100000.0f, (a * b).ToFloat(), 1.0f);

            var c = FP64.FromFloat(1000.0f);
            var d = FP64.FromFloat(1000.0f);
            Assert.AreEqual(1000000.0f, (c * d).ToFloat(), 1.0f);
        }

        [Test]
        public void Multiplication_OverflowPositive_SaturatesToMaxValue()
        {
            var result = FP64.MaxValue * FP64.FromInt(2);
            Assert.AreEqual(FP64.MaxValue, result);

            var large = FP64.FromFloat(100000.0f) * FP64.FromFloat(100000.0f);
            Assert.AreEqual(FP64.MaxValue, large);
        }

        [Test]
        public void Multiplication_OverflowNegative_SaturatesToMinValue()
        {
            var result = FP64.MaxValue * FP64.FromInt(-2);
            Assert.AreEqual(FP64.MinValue, result);

            var large = FP64.FromFloat(-100000.0f) * FP64.FromFloat(100000.0f);
            Assert.AreEqual(FP64.MinValue, large);
        }

        [Test]
        public void Division_FastPathBoundary_WorksCorrectly()
        {
            // |a| < 0x80000000L takes the fast path (fractional value < 1.0)
            var a = FP64.FromRaw(0x7FFFFFFFL);
            var b = FP64.FromInt(2);
            var result = a / b;
            Assert.AreEqual(a.ToFloat() / 2.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Division_SlowPath_LargeNumerator_WorksCorrectly()
        {
            var a = FP64.FromFloat(50000.0f);
            var b = FP64.FromFloat(25.0f);
            Assert.AreEqual(2000.0f, (a / b).ToFloat(), 1.0f);

            var c = FP64.FromFloat(1000000.0f);
            var d = FP64.FromFloat(1000.0f);
            Assert.AreEqual(1000.0f, (c / d).ToFloat(), 1.0f);
        }

        [Test]
        public void Division_FractionalResult_HasPrecision()
        {
            var oneThird = FP64.FromInt(1) / FP64.FromInt(3);
            Assert.AreEqual(0.3333f, oneThird.ToFloat(), EPSILON);

            var oneSeventh = FP64.FromInt(1) / FP64.FromInt(7);
            Assert.AreEqual(0.14286f, oneSeventh.ToFloat(), EPSILON);

            var piApprox = FP64.FromInt(22) / FP64.FromInt(7);
            Assert.AreEqual(3.14286f, piApprox.ToFloat(), EPSILON);
        }

        [Test]
        public void Modulo_WithFractions_WorksCorrectly()
        {
            var a = FP64.FromFloat(7.5f) % FP64.FromFloat(2.5f);
            Assert.AreEqual(0.0f, a.ToFloat(), EPSILON);

            var b = FP64.FromFloat(5.3f) % FP64.FromFloat(2.0f);
            Assert.AreEqual(1.3f, b.ToFloat(), 0.01f);
        }

        [Test]
        public void Addition_Overflow_Wraps()
        {
            // + operator performs raw long addition (not saturating)
            var result = FP64.MaxValue + FP64.One;
            Assert.IsTrue(result < FP64.Zero, "MaxValue + One should wrap to negative");
        }

        [Test]
        public void Subtraction_Overflow_Wraps()
        {
            var result = FP64.MinValue - FP64.One;
            Assert.IsTrue(result > FP64.Zero, "MinValue - One should wrap to positive");
        }

        #endregion

        #region Comparison Operations

        [Test]
        public void Equality_WorksCorrectly()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.FromFloat(5.0f);
            Assert.IsTrue(a == b);
        }

        [Test]
        public void Inequality_WorksCorrectly()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.FromFloat(3.0f);
            Assert.IsTrue(a != b);
        }

        [Test]
        public void LessThan_WorksCorrectly()
        {
            var a = FP64.FromFloat(3.0f);
            var b = FP64.FromFloat(5.0f);
            Assert.IsTrue(a < b);
            Assert.IsFalse(b < a);
        }

        [Test]
        public void GreaterThan_WorksCorrectly()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.FromFloat(3.0f);
            Assert.IsTrue(a > b);
            Assert.IsFalse(b > a);
        }

        [Test]
        public void LessThanOrEqual_WorksCorrectly()
        {
            var a = FP64.FromFloat(3.0f);
            var b = FP64.FromFloat(5.0f);
            var c = FP64.FromFloat(3.0f);
            Assert.IsTrue(a <= b);
            Assert.IsTrue(a <= c);
            Assert.IsFalse(b <= a);
        }

        [Test]
        public void GreaterThanOrEqual_WorksCorrectly()
        {
            var a = FP64.FromFloat(5.0f);
            var b = FP64.FromFloat(3.0f);
            var c = FP64.FromFloat(5.0f);
            Assert.IsTrue(a >= b);
            Assert.IsTrue(a >= c);
            Assert.IsFalse(b >= a);
        }

        #endregion

        #region Math Functions

        [Test]
        public void Abs_PositiveValue_ReturnsSame()
        {
            var a = FP64.FromFloat(5.0f);
            Assert.AreEqual(5.0f, FP64.Abs(a).ToFloat(), EPSILON);
        }

        [Test]
        public void Abs_NegativeValue_ReturnsPositive()
        {
            var a = FP64.FromFloat(-5.0f);
            Assert.AreEqual(5.0f, FP64.Abs(a).ToFloat(), EPSILON);
        }

        [Test]
        public void Sqrt_PerfectSquare_WorksCorrectly()
        {
            var a = FP64.FromFloat(16.0f);
            var result = FP64.Sqrt(a);
            Assert.AreEqual(4.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Sqrt_NonPerfectSquare_WorksCorrectly()
        {
            var a = FP64.FromFloat(2.0f);
            var result = FP64.Sqrt(a);
            Assert.AreEqual(1.414f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Sqrt_Zero_ReturnsZero()
        {
            var result = FP64.Sqrt(FP64.Zero);
            Assert.AreEqual(FP64.Zero, result);
        }

        [Test]
        public void Sqrt_SmallValue_WorksCorrectly()
        {
            var a = FP64.FromFloat(0.25f);
            var result = FP64.Sqrt(a);
            Assert.AreEqual(0.5f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Sqrt_LargeValue_NoOverflow()
        {
            var a = FP64.FromFloat(10000.0f);
            var result = FP64.Sqrt(a);
            Assert.AreEqual(100.0f, result.ToFloat(), 0.1f);
        }

        [Test]
        public void Sqrt_NegativeValue_ThrowsException()
        {
            var a = FP64.FromFloat(-1.0f);
            Assert.Throws<ArgumentException>(() => FP64.Sqrt(a));
        }

        [Test]
        public void Min_ReturnsSmaller()
        {
            var a = FP64.FromFloat(3.0f);
            var b = FP64.FromFloat(5.0f);
            Assert.AreEqual(a, FP64.Min(a, b));
        }

        [Test]
        public void Max_ReturnsLarger()
        {
            var a = FP64.FromFloat(3.0f);
            var b = FP64.FromFloat(5.0f);
            Assert.AreEqual(b, FP64.Max(a, b));
        }

        [Test]
        public void Clamp_InRange_ReturnsSame()
        {
            var value = FP64.FromFloat(5.0f);
            var result = FP64.Clamp(value, FP64.FromFloat(0.0f), FP64.FromFloat(10.0f));
            Assert.AreEqual(5.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Clamp_BelowMin_ReturnsMin()
        {
            var value = FP64.FromFloat(-5.0f);
            var result = FP64.Clamp(value, FP64.FromFloat(0.0f), FP64.FromFloat(10.0f));
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Clamp_AboveMax_ReturnsMax()
        {
            var value = FP64.FromFloat(15.0f);
            var result = FP64.Clamp(value, FP64.FromFloat(0.0f), FP64.FromFloat(10.0f));
            Assert.AreEqual(10.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtZero_ReturnsA()
        {
            var a = FP64.FromFloat(0.0f);
            var b = FP64.FromFloat(10.0f);
            var result = FP64.Lerp(a, b, FP64.Zero);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtOne_ReturnsB()
        {
            var a = FP64.FromFloat(0.0f);
            var b = FP64.FromFloat(10.0f);
            var result = FP64.Lerp(a, b, FP64.One);
            Assert.AreEqual(10.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtHalf_ReturnsMidpoint()
        {
            var a = FP64.FromFloat(0.0f);
            var b = FP64.FromFloat(10.0f);
            var result = FP64.Lerp(a, b, FP64.Half);
            Assert.AreEqual(5.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Floor_PositiveFraction_ReturnsLowerInt()
        {
            Assert.AreEqual(3.0f, FP64.Floor(FP64.FromFloat(3.7f)).ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, FP64.Floor(FP64.FromFloat(3.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Floor_NegativeFraction_RoundsTowardNegativeInfinity()
        {
            Assert.AreEqual(-3.0f, FP64.Floor(FP64.FromFloat(-2.3f)).ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, FP64.Floor(FP64.FromFloat(-2.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Floor_SmallFraction_BelowOne()
        {
            Assert.AreEqual(0.0f, FP64.Floor(FP64.FromFloat(0.9f)).ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, FP64.Floor(FP64.FromFloat(-0.1f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Ceiling_PositiveFraction_ReturnsUpperInt()
        {
            Assert.AreEqual(4.0f, FP64.Ceiling(FP64.FromFloat(3.1f)).ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, FP64.Ceiling(FP64.FromFloat(3.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Ceiling_NegativeFraction_RoundsTowardZero()
        {
            Assert.AreEqual(-2.0f, FP64.Ceiling(FP64.FromFloat(-2.7f)).ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, FP64.Ceiling(FP64.FromFloat(-2.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Ceiling_SmallFraction_BelowOne()
        {
            Assert.AreEqual(1.0f, FP64.Ceiling(FP64.FromFloat(0.1f)).ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, FP64.Ceiling(FP64.FromFloat(-0.9f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Round_HalfOrAbove_RoundsToCeiling()
        {
            Assert.AreEqual(3.0f, FP64.Round(FP64.FromFloat(2.5f)).ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, FP64.Round(FP64.FromFloat(2.7f)).ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, FP64.Round(FP64.FromFloat(2.3f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Round_Negative_WorksCorrectly()
        {
            Assert.AreEqual(-2.0f, FP64.Round(FP64.FromFloat(-2.3f)).ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, FP64.Round(FP64.FromFloat(-2.7f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Sign_ReturnsCorrectSign()
        {
            Assert.AreEqual(1, FP64.Sign(FP64.FromFloat(5.0f)));
            Assert.AreEqual(-1, FP64.Sign(FP64.FromFloat(-3.0f)));
            Assert.AreEqual(0, FP64.Sign(FP64.Zero));
        }

        [Test]
        public void Lerp_BeyondRange_ClampsBehavior()
        {
            var a = FP64.Zero;
            var b = FP64.FromFloat(10.0f);
            Assert.AreEqual(10.0f, FP64.Lerp(a, b, FP64.FromFloat(2.0f)).ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, FP64.Lerp(a, b, FP64.FromFloat(-1.0f)).ToFloat(), EPSILON);
        }

        [Test]
        public void LerpUnclamped_BeyondRange_Extrapolates()
        {
            var a = FP64.Zero;
            var b = FP64.FromFloat(10.0f);
            Assert.AreEqual(20.0f, FP64.LerpUnclamped(a, b, FP64.FromFloat(2.0f)).ToFloat(), EPSILON);
            Assert.AreEqual(-5.0f, FP64.LerpUnclamped(a, b, FP64.FromFloat(-0.5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Sqrt_VeryLargeValue_ExercisesBetweenPassOverflow()
        {
            var result = FP64.Sqrt(FP64.FromRaw(long.MaxValue));
            // sqrt(long.MaxValue / 2^32) ≈ sqrt(2147483647.9999...) ≈ 46340.95
            Assert.AreEqual(46340.95, result.ToDouble(), 1.0);
        }

        #endregion

        #region Trigonometric Functions

        [Test]
        public void Sin_Zero_ReturnsZero()
        {
            var result = FP64.Sin(FP64.Zero);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Sin_HalfPi_ReturnsOne()
        {
            var result = FP64.Sin(FP64.HalfPi);
            Assert.AreEqual(1.0f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Cos_Zero_ReturnsOne()
        {
            var result = FP64.Cos(FP64.Zero);
            Assert.AreEqual(1.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Cos_Pi_ReturnsMinusOne()
        {
            var result = FP64.Cos(FP64.Pi);
            Assert.AreEqual(-1.0f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Atan2_AllQuadrants_WorksCorrectly()
        {
            var pts = new (double y, double x)[] { (1,1), (1,-1), (-1,1), (-1,-1), (10,-1), (-10,-1) };
            foreach (var p in pts) {
                var y = FP64.FromDouble(p.y);
                var x = FP64.FromDouble(p.x);
                var my = FP64.Atan2(y, x).ToDouble();
                var sys = Mathf.Atan2((float)p.y, (float)p.x);
                Assert.AreEqual(my, sys, 0.05f);
            }
        }

        [Test]
        public void Atan2_Zero_ReturnsZero()
        {
            var result = FP64.Atan2(FP64.Zero, FP64.One);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Sin_Pi_ReturnsZero()
        {
            var result = FP64.Sin(FP64.Pi);
            Assert.AreEqual(0.0f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Sin_NegativeAngle_ReturnsNegative()
        {
            var result = FP64.Sin(-FP64.HalfPi);
            Assert.AreEqual(-1.0f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Cos_HalfPi_ReturnsZero()
        {
            var result = FP64.Cos(FP64.HalfPi);
            Assert.AreEqual(0.0f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Cos_TwoPi_ReturnsOne()
        {
            var result = FP64.Cos(FP64.TwoPi);
            Assert.AreEqual(1.0f, result.ToFloat(), 0.01f);
        }

        [Test]
        public void Sin_LargeAngle_NormalizesCorrectly()
        {
            var result = FP64.Sin(FP64.FromFloat(100.0f));
            var expected = (float)System.Math.Sin(100.0);
            Assert.AreEqual(expected, result.ToFloat(), 0.05f);
        }

        [Test]
        public void Tan_BasicValues_WorksCorrectly()
        {
            Assert.AreEqual(0.0f, FP64.Tan(FP64.Zero).ToFloat(), EPSILON);
            var quarterPi = FP64.FromDouble(System.Math.PI / 4.0);
            Assert.AreEqual(1.0f, FP64.Tan(quarterPi).ToFloat(), 0.01f);
        }

        [Test]
        public void Acos_BoundaryValues_WorksCorrectly()
        {
            Assert.AreEqual(0.0f, FP64.Acos(FP64.One).ToFloat(), EPSILON);
            Assert.AreEqual((float)System.Math.PI, FP64.Acos(-FP64.One).ToFloat(), 0.01f);
            Assert.AreEqual((float)(System.Math.PI / 2.0), FP64.Acos(FP64.Zero).ToFloat(), 0.01f);
            // Clamping: should return 0 when x > 1
            Assert.AreEqual(0.0f, FP64.Acos(FP64.FromFloat(2.0f)).ToFloat(), EPSILON);
        }

        #endregion

        #region Determinism Tests

        [Test]
        public void SameOperations_ProduceSameResults()
        {
            var pairs = new (float a, float b)[]
            {
                (0.5f, 0.5f),
                (50000f, 3.14f),
                (-1000f, 200f),
                (0.001f, 99999f)
            };

            foreach (var (aVal, bVal) in pairs)
            {
                var a = FP64.FromFloat(aVal);
                var b = FP64.FromFloat(bVal);

                var result1 = (a * b) / (a + b);
                var result2 = (a * b) / (a + b);

                Assert.AreEqual(result1.RawValue, result2.RawValue,
                    $"Determinism failed for ({aVal}, {bVal})");
            }
        }

        [Test]
        public void ChainedOperations_AreDeterministic()
        {
            var a = FP64.FromFloat(10.0f);
            var b = FP64.FromFloat(3.0f);

            // Complex operation chain
            var result1 = FP64.Sqrt(a * a + b * b);
            var result2 = FP64.Sqrt(a * a + b * b);

            Assert.AreEqual(result1.RawValue, result2.RawValue);
        }

        [Test]
        public void TrigDeterminism_SameAngle_ProducesSameResult()
        {
            var angles = new double[] { 0, System.Math.PI / 6, System.Math.PI / 4, System.Math.PI / 3,
                System.Math.PI / 2, System.Math.PI, -System.Math.PI / 4, 3 * System.Math.PI, 100.0 };

            foreach (var angle in angles)
            {
                var fpAngle = FP64.FromDouble(angle);
                Assert.AreEqual(FP64.Sin(fpAngle).RawValue, FP64.Sin(fpAngle).RawValue,
                    $"Sin determinism failed for angle {angle}");
                Assert.AreEqual(FP64.Cos(fpAngle).RawValue, FP64.Cos(fpAngle).RawValue,
                    $"Cos determinism failed for angle {angle}");
            }
        }

        #endregion

        #region Constants and Boundary Values

        [Test]
        public void Constants_HaveCorrectValues()
        {
            Assert.AreEqual(3.14159265, FP64.Pi.ToDouble(), 0.0001);
            Assert.AreEqual(6.28318530, FP64.TwoPi.ToDouble(), 0.0001);
            Assert.AreEqual(1.57079632, FP64.HalfPi.ToDouble(), 0.0001);
            Assert.AreEqual(System.Math.PI, (FP64.Deg2Rad * FP64.FromInt(180)).ToDouble(), 0.001);
            Assert.AreEqual(180.0, (FP64.Rad2Deg * FP64.Pi).ToDouble(), 0.01);
            Assert.AreEqual(1L, FP64.Epsilon.RawValue);
        }

        [Test]
        public void Boundary_MaxValue_MinValue_Behavior()
        {
            Assert.IsTrue(FP64.MaxValue > FP64.Zero);
            Assert.IsTrue(FP64.MinValue < FP64.Zero);
            Assert.IsTrue(FP64.MaxValue.ToDouble() > 0);
            Assert.IsTrue(FP64.MinValue.ToDouble() < 0);
            // Two's complement: -MaxValue != MinValue (off by 1)
            Assert.AreNotEqual((-FP64.MaxValue).RawValue, FP64.MinValue.RawValue);
        }

        [Test]
        public void Epsilon_IsSmallestPositiveValue()
        {
            Assert.IsTrue(FP64.Epsilon > FP64.Zero);
            Assert.IsTrue(FP64.Epsilon.ToFloat() > 0f);
            Assert.IsTrue(FP64.Epsilon + FP64.Epsilon > FP64.Epsilon);
        }

        #endregion

        #region SmoothDamp

        [Test]
        public void SmoothDamp_ApproachesTarget()
        {
            FP64 current = FP64.Zero;
            FP64 target = FP64.FromFloat(10f);
            FP64 velocity = FP64.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 100; i++)
                current = FP64.SmoothDamp(current, target, ref velocity, smoothTime, maxSpeed, dt);

            Assert.AreEqual(10f, current.ToFloat(), 0.1f);
        }

        [Test]
        public void SmoothDamp_AtTarget_StaysAtTarget()
        {
            FP64 current = FP64.FromFloat(5f);
            FP64 target = FP64.FromFloat(5f);
            FP64 velocity = FP64.Zero;

            var result = FP64.SmoothDamp(current, target, ref velocity, FP64.FromFloat(0.3f), FP64.FromFloat(1000f), FP64.FromFloat(0.02f));
            Assert.AreEqual(5f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothDamp_Deterministic()
        {
            FP64 c1 = FP64.Zero;
            FP64 c2 = FP64.Zero;
            FP64 target = FP64.FromFloat(10f);
            FP64 v1 = FP64.Zero;
            FP64 v2 = FP64.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 20; i++)
            {
                c1 = FP64.SmoothDamp(c1, target, ref v1, smoothTime, maxSpeed, dt);
                c2 = FP64.SmoothDamp(c2, target, ref v2, smoothTime, maxSpeed, dt);
            }

            Assert.AreEqual(c1.RawValue, c2.RawValue);
        }

        [Test]
        public void SmoothDamp_DoesNotOvershoot()
        {
            FP64 current = FP64.Zero;
            FP64 target = FP64.FromFloat(10f);
            FP64 velocity = FP64.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 200; i++)
            {
                current = FP64.SmoothDamp(current, target, ref velocity, smoothTime, maxSpeed, dt);
                Assert.LessOrEqual(current.ToFloat(), 10.01f);
            }
        }

        #endregion

        #region Asin

        [Test]
        public void Asin_Zero_IsZero()
        {
            Assert.AreEqual(0f, FP64.Asin(FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void Asin_One_IsHalfPi()
        {
            float expected = (float)(System.Math.PI / 2.0);
            Assert.AreEqual(expected, FP64.Asin(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Asin_NegativeOne_IsNegHalfPi()
        {
            float expected = (float)(-System.Math.PI / 2.0);
            Assert.AreEqual(expected, FP64.Asin(-FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Asin_Half_MatchesMath()
        {
            float expected = (float)System.Math.Asin(0.5);
            Assert.AreEqual(expected, FP64.Asin(FP64.Half).ToFloat(), 0.01f);
        }

        [Test]
        public void Asin_Various_MatchMath()
        {
            float[] testValues = { -0.9f, -0.5f, -0.25f, 0.25f, 0.5f, 0.9f };
            foreach (float v in testValues)
            {
                float expected = (float)System.Math.Asin(v);
                float actual = FP64.Asin(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, 0.02f, $"Asin({v})");
            }
        }

        #endregion

        #region Atan

        [Test]
        public void Atan_Zero_IsZero()
        {
            Assert.AreEqual(0f, FP64.Atan(FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void Atan_One_IsPiOver4()
        {
            float expected = (float)(System.Math.PI / 4.0);
            Assert.AreEqual(expected, FP64.Atan(FP64.One).ToFloat(), 0.01f);
        }

        [Test]
        public void Atan_Various_MatchMath()
        {
            float[] testValues = { -10f, -2f, -1f, -0.5f, 0.5f, 1f, 2f, 10f };
            foreach (float v in testValues)
            {
                float expected = (float)System.Math.Atan(v);
                float actual = FP64.Atan(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, 0.02f, $"Atan({v})");
            }
        }

        #endregion

        #region Log2

        [Test]
        public void Log2_One_IsZero()
        {
            Assert.AreEqual(0f, FP64.Log2(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Log2_Two_IsOne()
        {
            Assert.AreEqual(1f, FP64.Log2(FP64.FromInt(2)).ToFloat(), EPSILON);
        }

        [Test]
        public void Log2_Four_IsTwo()
        {
            Assert.AreEqual(2f, FP64.Log2(FP64.FromInt(4)).ToFloat(), EPSILON);
        }

        [Test]
        public void Log2_Half_IsNegOne()
        {
            Assert.AreEqual(-1f, FP64.Log2(FP64.Half).ToFloat(), EPSILON);
        }

        [Test]
        public void Log2_Various_MatchMath()
        {
            float[] testValues = { 0.1f, 0.5f, 1.5f, 3f, 10f, 100f, 1000f };
            foreach (float v in testValues)
            {
                float expected = (float)(System.Math.Log(v) / System.Math.Log(2));
                float actual = FP64.Log2(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, 0.02f, $"Log2({v})");
            }
        }

        [Test]
        public void Log2_NonPositive_Throws()
        {
            Assert.Throws<ArgumentException>(() => FP64.Log2(FP64.Zero));
            Assert.Throws<ArgumentException>(() => FP64.Log2(-FP64.One));
        }

        #endregion

        #region Ln

        [Test]
        public void Ln_One_IsZero()
        {
            Assert.AreEqual(0f, FP64.Ln(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Ln_E_IsOne()
        {
            FP64 e = FP64.FromDouble(System.Math.E);
            Assert.AreEqual(1f, FP64.Ln(e).ToFloat(), 0.02f);
        }

        [Test]
        public void Ln_Various_MatchMath()
        {
            float[] testValues = { 0.1f, 0.5f, 1f, 2f, 10f, 100f };
            foreach (float v in testValues)
            {
                float expected = (float)System.Math.Log(v);
                float actual = FP64.Ln(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, 0.03f, $"Ln({v})");
            }
        }

        #endregion

        #region Exp

        [Test]
        public void Exp_Zero_IsOne()
        {
            Assert.AreEqual(1f, FP64.Exp(FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void Exp_One_IsE()
        {
            float expected = (float)System.Math.E;
            Assert.AreEqual(expected, FP64.Exp(FP64.One).ToFloat(), 0.05f);
        }

        [Test]
        public void Exp_NegOne_IsInvE()
        {
            float expected = (float)(1.0 / System.Math.E);
            Assert.AreEqual(expected, FP64.Exp(-FP64.One).ToFloat(), 0.05f);
        }

        [Test]
        public void Exp_Various_MatchMath()
        {
            float[] testValues = { -5f, -2f, -1f, -0.5f, 0.5f, 1f, 2f, 5f };
            foreach (float v in testValues)
            {
                float expected = (float)System.Math.Exp(v);
                float actual = FP64.Exp(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, expected * 0.02f + 0.01f, $"Exp({v})");
            }
        }

        [Test]
        public void Exp_LargePositive_Saturates()
        {
            var result = FP64.Exp(FP64.FromInt(100));
            Assert.AreEqual(FP64.MaxValue, result);
        }

        [Test]
        public void Exp_LargeNegative_IsZero()
        {
            var result = FP64.Exp(FP64.FromInt(-100));
            Assert.AreEqual(FP64.Zero, result);
        }

        #endregion

        #region Exp2

        [Test]
        public void Exp2_Zero_IsOne()
        {
            Assert.AreEqual(1f, FP64.Exp2(FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void Exp2_One_IsTwo()
        {
            Assert.AreEqual(2f, FP64.Exp2(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Exp2_NegOne_IsHalf()
        {
            Assert.AreEqual(0.5f, FP64.Exp2(-FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Exp2_Ten_Is1024()
        {
            Assert.AreEqual(1024f, FP64.Exp2(FP64.FromInt(10)).ToFloat(), EPSILON);
        }

        [Test]
        public void Exp2_Half_IsSqrt2()
        {
            float expected = (float)System.Math.Sqrt(2.0);
            Assert.AreEqual(expected, FP64.Exp2(FP64.Half).ToFloat(), 0.02f);
        }

        [Test]
        public void Exp2_Various_MatchMath()
        {
            float[] testValues = { -5f, -2.5f, -1f, -0.5f, 0.5f, 1f, 2.5f, 5f };
            foreach (float v in testValues)
            {
                float expected = (float)System.Math.Pow(2.0, v);
                float actual = FP64.Exp2(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, expected * 0.02f + 0.01f, $"Exp2({v})");
            }
        }

        #endregion

        #region Exp_Ln_Roundtrip

        [Test]
        public void Exp_Ln_Roundtrip()
        {
            float[] testValues = { 0.1f, 0.5f, 1f, 2f, 5f, 10f };
            foreach (float v in testValues)
            {
                FP64 fpV = FP64.FromFloat(v);
                FP64 roundtrip = FP64.Exp(FP64.Ln(fpV));
                Assert.AreEqual(v, roundtrip.ToFloat(), v * 0.03f + 0.01f, $"Exp(Ln({v}))");
            }
        }

        [Test]
        public void Exp2_Log2_Roundtrip()
        {
            float[] testValues = { 0.1f, 0.5f, 1f, 2f, 4f, 10f };
            foreach (float v in testValues)
            {
                FP64 fpV = FP64.FromFloat(v);
                FP64 roundtrip = FP64.Exp2(FP64.Log2(fpV));
                Assert.AreEqual(v, roundtrip.ToFloat(), v * 0.03f + 0.01f, $"Exp2(Log2({v}))");
            }
        }

        #endregion

        #region Pow

        [Test]
        public void Pow_AnyBase_ZeroExponent_IsOne()
        {
            Assert.AreEqual(1f, FP64.Pow(FP64.FromFloat(5f), FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void Pow_OneBase_AnyExponent_IsOne()
        {
            Assert.AreEqual(1f, FP64.Pow(FP64.One, FP64.FromFloat(100f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Pow_TwoSquared_IsFour()
        {
            Assert.AreEqual(4f, FP64.Pow(FP64.FromInt(2), FP64.FromInt(2)).ToFloat(), 0.05f);
        }

        [Test]
        public void Pow_TwoCubed_IsEight()
        {
            Assert.AreEqual(8f, FP64.Pow(FP64.FromInt(2), FP64.FromInt(3)).ToFloat(), 0.1f);
        }

        [Test]
        public void Pow_FourHalf_IsTwo()
        {
            // sqrt(4) = 2
            Assert.AreEqual(2f, FP64.Pow(FP64.FromInt(4), FP64.Half).ToFloat(), 0.05f);
        }

        [Test]
        public void Pow_Various_MatchMath()
        {
            float[][] cases = {
                new[] { 2f, 0.5f },
                new[] { 3f, 2f },
                new[] { 10f, 1.5f },
                new[] { 0.5f, 3f },
                new[] { 2f, -1f },
            };
            foreach (float[] c in cases)
            {
                float expected = (float)System.Math.Pow(c[0], c[1]);
                float actual = FP64.Pow(FP64.FromFloat(c[0]), FP64.FromFloat(c[1])).ToFloat();
                Assert.AreEqual(expected, actual, expected * 0.05f + 0.02f, $"Pow({c[0]}, {c[1]})");
            }
        }

        [Test]
        public void Pow_ZeroBase_PositiveExp_IsZero()
        {
            Assert.AreEqual(FP64.Zero, FP64.Pow(FP64.Zero, FP64.One));
        }

        #endregion

        #region Rcp

        [Test]
        public void Rcp_One_IsOne()
        {
            Assert.AreEqual(1f, FP64.Rcp(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Rcp_Two_IsHalf()
        {
            Assert.AreEqual(0.5f, FP64.Rcp(FP64.FromInt(2)).ToFloat(), EPSILON);
        }

        [Test]
        public void Rcp_Half_IsTwo()
        {
            Assert.AreEqual(2f, FP64.Rcp(FP64.Half).ToFloat(), EPSILON);
        }

        [Test]
        public void Rcp_Negative_IsCorrect()
        {
            Assert.AreEqual(-0.25f, FP64.Rcp(FP64.FromInt(-4)).ToFloat(), EPSILON);
        }

        [Test]
        public void Rcp_Zero_Throws()
        {
            Assert.Throws<System.DivideByZeroException>(() => FP64.Rcp(FP64.Zero));
        }

        #endregion

        #region RSqrt

        [Test]
        public void RSqrt_One_IsOne()
        {
            Assert.AreEqual(1f, FP64.RSqrt(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void RSqrt_Four_IsHalf()
        {
            Assert.AreEqual(0.5f, FP64.RSqrt(FP64.FromInt(4)).ToFloat(), EPSILON);
        }

        [Test]
        public void RSqrt_Various_MatchMath()
        {
            float[] testValues = { 0.25f, 0.5f, 1f, 2f, 4f, 9f, 16f, 100f };
            foreach (float v in testValues)
            {
                float expected = (float)(1.0 / System.Math.Sqrt(v));
                float actual = FP64.RSqrt(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, 0.01f, $"RSqrt({v})");
            }
        }

        [Test]
        public void RSqrt_Negative_Throws()
        {
            Assert.Throws<System.ArgumentException>(() => FP64.RSqrt(-FP64.One));
        }

        #endregion

        #region Fmod

        [Test]
        public void Fmod_BasicPositive_MatchesMath()
        {
            float a = 7f, b = 3f;
            float expected = a % b; // 1
            Assert.AreEqual(expected, FP64.Fmod(FP64.FromFloat(a), FP64.FromFloat(b)).ToFloat(), EPSILON);
        }

        [Test]
        public void Fmod_NegativeDividend_PreservesSign()
        {
            // fmod(-7, 3) = -1 (same sign as dividend)
            float expected = -7f % 3f; // -1 in C#
            Assert.AreEqual(expected, FP64.Fmod(FP64.FromFloat(-7f), FP64.FromFloat(3f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Fmod_Fractional_MatchesMath()
        {
            double a = 5.3, b = 2.0;
            double expected = a - System.Math.Floor(a / b) * b; // 1.3
            Assert.AreEqual((float)expected, FP64.Fmod(FP64.FromDouble(a), FP64.FromDouble(b)).ToFloat(), 0.01f);
        }

        [Test]
        public void Fmod_DivisorZero_ReturnsZero()
        {
            Assert.AreEqual(FP64.Zero, FP64.Fmod(FP64.FromFloat(5f), FP64.Zero));
        }

        [Test]
        public void Fmod_ExactDivision_ReturnsZero()
        {
            Assert.AreEqual(0f, FP64.Fmod(FP64.FromFloat(6f), FP64.FromFloat(3f)).ToFloat(), EPSILON);
        }

        #endregion

        #region Remainder

        [Test]
        public void Remainder_BasicPositive()
        {
            // IEEE remainder(7, 3) = 7 - round(7/3)*3 = 7 - 2*3 = 1
            double expected = System.Math.IEEERemainder(7, 3);
            Assert.AreEqual((float)expected, FP64.Remainder(FP64.FromFloat(7f), FP64.FromFloat(3f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Remainder_DiffersFromFmod()
        {
            // remainder(7, 4) = 7 - round(7/4)*4 = 7 - 2*4 = -1
            // fmod(7, 4) = 7 - floor(7/4)*4 = 7 - 1*4 = 3
            double expectedRemainder = System.Math.IEEERemainder(7, 4);
            Assert.AreEqual((float)expectedRemainder, FP64.Remainder(FP64.FromFloat(7f), FP64.FromFloat(4f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Remainder_Negative()
        {
            double expected = System.Math.IEEERemainder(-7, 3);
            Assert.AreEqual((float)expected, FP64.Remainder(FP64.FromFloat(-7f), FP64.FromFloat(3f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Remainder_DivisorZero_ReturnsZero()
        {
            Assert.AreEqual(FP64.Zero, FP64.Remainder(FP64.FromFloat(5f), FP64.Zero));
        }

        #endregion

        #region Cbrt

        [Test]
        public void Cbrt_Zero_IsZero()
        {
            Assert.AreEqual(0f, FP64.Cbrt(FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void Cbrt_One_IsOne()
        {
            Assert.AreEqual(1f, FP64.Cbrt(FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void Cbrt_Eight_IsTwo()
        {
            Assert.AreEqual(2f, FP64.Cbrt(FP64.FromInt(8)).ToFloat(), 0.01f);
        }

        [Test]
        public void Cbrt_TwentySeven_IsThree()
        {
            Assert.AreEqual(3f, FP64.Cbrt(FP64.FromInt(27)).ToFloat(), 0.01f);
        }

        [Test]
        public void Cbrt_NegativeEight_IsNegTwo()
        {
            Assert.AreEqual(-2f, FP64.Cbrt(FP64.FromInt(-8)).ToFloat(), 0.01f);
        }

        [Test]
        public void Cbrt_Fractional_MatchesMath()
        {
            float[] testValues = { 0.125f, 0.5f, 2f, 10f, 100f, 1000f };
            foreach (float v in testValues)
            {
                float expected = (float)System.Math.Pow(v, 1.0 / 3.0);
                float actual = FP64.Cbrt(FP64.FromFloat(v)).ToFloat();
                Assert.AreEqual(expected, actual, 0.02f, $"Cbrt({v})");
            }
        }

        [Test]
        public void Cbrt_Deterministic()
        {
            FP64 v = FP64.FromFloat(27f);
            Assert.AreEqual(FP64.Cbrt(v).RawValue, FP64.Cbrt(v).RawValue);
        }

        #endregion

        #region InverseLerp

        [Test]
        public void InverseLerp_AtA_ReturnsZero()
        {
            Assert.AreEqual(0f, FP64.InverseLerp(FP64.FromFloat(2f), FP64.FromFloat(8f), FP64.FromFloat(2f)).ToFloat(), EPSILON);
        }

        [Test]
        public void InverseLerp_AtB_ReturnsOne()
        {
            Assert.AreEqual(1f, FP64.InverseLerp(FP64.FromFloat(2f), FP64.FromFloat(8f), FP64.FromFloat(8f)).ToFloat(), EPSILON);
        }

        [Test]
        public void InverseLerp_Midpoint_ReturnsHalf()
        {
            Assert.AreEqual(0.5f, FP64.InverseLerp(FP64.FromFloat(0f), FP64.FromFloat(10f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void InverseLerp_BeyondRange_Clamps()
        {
            Assert.AreEqual(0f, FP64.InverseLerp(FP64.FromFloat(0f), FP64.FromFloat(10f), FP64.FromFloat(-5f)).ToFloat(), EPSILON);
            Assert.AreEqual(1f, FP64.InverseLerp(FP64.FromFloat(0f), FP64.FromFloat(10f), FP64.FromFloat(15f)).ToFloat(), EPSILON);
        }

        [Test]
        public void InverseLerp_EqualAB_ReturnsZero()
        {
            Assert.AreEqual(0f, FP64.InverseLerp(FP64.FromFloat(5f), FP64.FromFloat(5f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        #endregion

        #region Repeat

        [Test]
        public void Repeat_PositiveInRange_ReturnsSame()
        {
            Assert.AreEqual(2f, FP64.Repeat(FP64.FromFloat(2f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Repeat_BeyondLength_Wraps()
        {
            Assert.AreEqual(2f, FP64.Repeat(FP64.FromFloat(7f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Repeat_Negative_WrapsPositive()
        {
            // Repeat(-1, 5) = -1 - floor(-1/5)*5 = -1 - (-1)*5 = 4
            Assert.AreEqual(4f, FP64.Repeat(FP64.FromFloat(-1f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void Repeat_ExactMultiple_ReturnsZero()
        {
            Assert.AreEqual(0f, FP64.Repeat(FP64.FromFloat(10f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        #endregion

        #region PingPong

        [Test]
        public void PingPong_InFirstHalf_Ascending()
        {
            // PingPong(2, 5) = 2
            Assert.AreEqual(2f, FP64.PingPong(FP64.FromFloat(2f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void PingPong_InSecondHalf_Descending()
        {
            // PingPong(7, 5): Repeat(7, 10) = 7, 5 - |7 - 5| = 3
            Assert.AreEqual(3f, FP64.PingPong(FP64.FromFloat(7f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void PingPong_AtLength_ReturnsLength()
        {
            Assert.AreEqual(5f, FP64.PingPong(FP64.FromFloat(5f), FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        [Test]
        public void PingPong_AtZero_ReturnsZero()
        {
            Assert.AreEqual(0f, FP64.PingPong(FP64.Zero, FP64.FromFloat(5f)).ToFloat(), EPSILON);
        }

        #endregion

        #region SmoothStep

        [Test]
        public void SmoothStep_AtFrom_ReturnsZero()
        {
            Assert.AreEqual(0f, FP64.SmoothStep(FP64.Zero, FP64.One, FP64.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothStep_AtTo_ReturnsOne()
        {
            Assert.AreEqual(1f, FP64.SmoothStep(FP64.Zero, FP64.One, FP64.One).ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothStep_AtMid_ReturnsHalf()
        {
            // SmoothStep(0, 1, 0.5) = 3*(0.5)^2 - 2*(0.5)^3 = 0.5
            Assert.AreEqual(0.5f, FP64.SmoothStep(FP64.Zero, FP64.One, FP64.Half).ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothStep_BeyondRange_Clamps()
        {
            Assert.AreEqual(0f, FP64.SmoothStep(FP64.Zero, FP64.One, FP64.FromFloat(-1f)).ToFloat(), EPSILON);
            Assert.AreEqual(1f, FP64.SmoothStep(FP64.Zero, FP64.One, FP64.FromFloat(2f)).ToFloat(), EPSILON);
        }

        #endregion

        #region MoveTowards

        [Test]
        public void MoveTowards_SmallDelta_Moves()
        {
            var result = FP64.MoveTowards(FP64.Zero, FP64.FromFloat(10f), FP64.FromFloat(3f));
            Assert.AreEqual(3f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_LargeDelta_SnapsToTarget()
        {
            var result = FP64.MoveTowards(FP64.Zero, FP64.FromFloat(5f), FP64.FromFloat(10f));
            Assert.AreEqual(5f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_Negative_MovesCorrectly()
        {
            var result = FP64.MoveTowards(FP64.FromFloat(10f), FP64.Zero, FP64.FromFloat(3f));
            Assert.AreEqual(7f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_AtTarget_StaysAtTarget()
        {
            var result = FP64.MoveTowards(FP64.FromFloat(5f), FP64.FromFloat(5f), FP64.FromFloat(1f));
            Assert.AreEqual(5f, result.ToFloat(), EPSILON);
        }

        #endregion

        #region DeltaAngle

        [Test]
        public void DeltaAngle_SmallDifference()
        {
            Assert.AreEqual(10f, FP64.DeltaAngle(FP64.FromFloat(10f), FP64.FromFloat(20f)).ToFloat(), EPSILON);
        }

        [Test]
        public void DeltaAngle_CrossZero_ShortPath()
        {
            // 350 -> 10: shortest path is +20
            Assert.AreEqual(20f, FP64.DeltaAngle(FP64.FromFloat(350f), FP64.FromFloat(10f)).ToFloat(), EPSILON);
        }

        [Test]
        public void DeltaAngle_CrossZero_NegativeShortPath()
        {
            // 10 -> 350: shortest path is -20
            Assert.AreEqual(-20f, FP64.DeltaAngle(FP64.FromFloat(10f), FP64.FromFloat(350f)).ToFloat(), EPSILON);
        }

        [Test]
        public void DeltaAngle_Opposite_Returns180()
        {
            Assert.AreEqual(180f, FP64.DeltaAngle(FP64.Zero, FP64.FromFloat(180f)).ToFloat(), EPSILON);
        }

        #endregion

        #region MoveTowardsAngle

        [Test]
        public void MoveTowardsAngle_SmallDelta_Moves()
        {
            var result = FP64.MoveTowardsAngle(FP64.FromFloat(10f), FP64.FromFloat(50f), FP64.FromFloat(10f));
            Assert.AreEqual(20f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowardsAngle_LargeDelta_SnapsToTarget()
        {
            var result = FP64.MoveTowardsAngle(FP64.FromFloat(10f), FP64.FromFloat(20f), FP64.FromFloat(100f));
            Assert.AreEqual(20f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowardsAngle_CrossZero_TakesShortPath()
        {
            // 350 -> 10: delta = +20, move by 5 -> 355
            var result = FP64.MoveTowardsAngle(FP64.FromFloat(350f), FP64.FromFloat(10f), FP64.FromFloat(5f));
            Assert.AreEqual(355f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowardsAngle_NegativeDelta_MovesCorrectly()
        {
            // 10 -> 350: delta = -20, move by 5 -> 5
            var result = FP64.MoveTowardsAngle(FP64.FromFloat(10f), FP64.FromFloat(350f), FP64.FromFloat(5f));
            Assert.AreEqual(5f, result.ToFloat(), EPSILON);
        }

        #endregion
    }
}
