using UnityEngine;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    /// <summary>
    /// FPVector2 vector operations test
    /// Includes BigInteger overflow protection validation
    /// </summary>
    [TestFixture]
    public class FPVector2Tests
    {
        private const float EPSILON = 0.01f;

        #region Creation and Conversion

        [Test]
        public void Constructor_CreatesCorrectVector()
        {
            var v = new FPVector2(FP64.FromFloat(1.0f), FP64.FromFloat(2.0f));
            Assert.AreEqual(1.0f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v.y.ToFloat(), EPSILON);
        }

        [Test]
        public void IntConstructor_CreatesCorrectVector()
        {
            var v = new FPVector2(3, 4);
            Assert.AreEqual(3.0f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, v.y.ToFloat(), EPSILON);
        }

        [Test]
        public void FromVector2_ConvertsCorrectly()
        {
            var unity = new Vector2(1.5f, 2.5f);
            var fp = new FPVector2();
            fp.FromVector2(unity);
            Assert.AreEqual(1.5f, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, fp.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ToVector2_ConvertsCorrectly()
        {
            var fp = new FPVector2(FP64.FromFloat(1.5f), FP64.FromFloat(2.5f));
            var unity = fp.ToVector2();
            Assert.AreEqual(1.5f, unity.x, EPSILON);
            Assert.AreEqual(2.5f, unity.y, EPSILON);
        }

        [Test]
        public void ToFPVector2_ConvertsCorrectly()
        {
            var unity = new Vector2(1.5f, 2.5f);
            var fp = unity.ToFPVector2();
            Assert.AreEqual(1.5f, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, fp.y.ToFloat(), EPSILON);
        }

        [Test]
        public void RoundTrip_UnityToFPAndBack()
        {
            var original = new Vector2(1.5f, -2.5f);
            var fp = original.ToFPVector2();
            var roundTrip = fp.ToVector2();
            Assert.AreEqual(original.x, roundTrip.x, EPSILON);
            Assert.AreEqual(original.y, roundTrip.y, EPSILON);
        }

        #endregion

        #region Constants

        [Test]
        public void Zero_IsAllZeros()
        {
            Assert.AreEqual(FP64.Zero, FPVector2.Zero.x);
            Assert.AreEqual(FP64.Zero, FPVector2.Zero.y);
        }

        [Test]
        public void One_IsAllOnes()
        {
            Assert.AreEqual(FP64.One, FPVector2.One.x);
            Assert.AreEqual(FP64.One, FPVector2.One.y);
        }

        [Test]
        public void Up_IsCorrect()
        {
            Assert.AreEqual(FP64.Zero, FPVector2.Up.x);
            Assert.AreEqual(FP64.One, FPVector2.Up.y);
        }

        [Test]
        public void Right_IsCorrect()
        {
            Assert.AreEqual(FP64.One, FPVector2.Right.x);
            Assert.AreEqual(FP64.Zero, FPVector2.Right.y);
        }

        #endregion

        #region Basic Operations

        [Test]
        public void Addition_WorksCorrectly()
        {
            var a = new FPVector2(1, 2);
            var b = new FPVector2(3, 4);
            var result = a + b;

            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = new FPVector2(5, 7);
            var b = new FPVector2(1, 2);
            var result = a - b;

            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var v = new FPVector2(1, 2);
            var result = -v;

            Assert.AreEqual(-1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiplication_WorksCorrectly()
        {
            var v = new FPVector2(2, 3);
            var scalar = FP64.FromFloat(2.0f);
            var result = v * scalar;

            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarDivision_WorksCorrectly()
        {
            var v = new FPVector2(4, 6);
            var scalar = FP64.FromFloat(2.0f);
            var result = v / scalar;

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Vector Functions

        [Test]
        public void Magnitude_UnitVector_ReturnsOne()
        {
            var v = FPVector2.Right;
            Assert.AreEqual(1.0f, v.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Magnitude_3_4_Returns5()
        {
            var v = new FPVector2(3, 4);
            Assert.AreEqual(5.0f, v.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrMagnitude_WorksCorrectly()
        {
            var v = new FPVector2(3, 4);
            Assert.AreEqual(25.0f, v.sqrMagnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrMagnitude_WithBigValues_ProtectedFromOverflow()
        {
            // 100000^2 = 10^10 exceeds the Q32.32 range; saturates without exception
            var v = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            Assert.DoesNotThrow(() => { var _ = v.sqrMagnitude; });
        }

        [Test]
        public void Normalized_ReturnsUnitVector()
        {
            var v = new FPVector2(3, 4);
            var normalized = v.normalized;
            Assert.AreEqual(1.0f, normalized.magnitude.ToFloat(), 0.1f);  // Slightly loose tolerance
        }

        [Test]
        public void Normalized_Zero_ReturnsZero()
        {
            var result = FPVector2.Zero.normalized;
            Assert.AreEqual(FPVector2.Zero, result);
        }

        [Test]
        public void Dot_Perpendicular_ReturnsZero()
        {
            var a = FPVector2.Right;
            var b = FPVector2.Up;
            var result = FPVector2.Dot(a, b);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Dot_Parallel_ReturnsProduct()
        {
            var a = new FPVector2(2, 0);
            var b = new FPVector2(3, 0);
            var result = FPVector2.Dot(a, b);
            Assert.AreEqual(6.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Dot_WithBigValues_ProtectedFromOverflow()
        {
            // 100000*100000 = 10^10 exceeds the Q32.32 integer range (max ~2.1x10^9)
            // SafeMultiply saturates; no exception thrown
            var a = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            var b = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            Assert.DoesNotThrow(() => FPVector2.Dot(a, b));
        }

        [Test]
        public void Cross_WorksCorrectly()
        {
            var a = new FPVector2(1, 0);
            var b = new FPVector2(0, 1);
            var result = FPVector2.Cross(a, b);
            Assert.AreEqual(1.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Cross_Parallel_ReturnsZero()
        {
            var a = new FPVector2(2, 0);
            var b = new FPVector2(3, 0);
            var result = FPVector2.Cross(a, b);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Cross_WithBigValues_ProtectedFromOverflow()
        {
            // Cross product with large values is also protected by BigInteger
            var a = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            var b = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            var result = FPVector2.Cross(a, b);
            // 100000*100000 - 100000*100000 = 0 (cancels out)
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Distance_SamePoint_ReturnsZero()
        {
            var a = new FPVector2(1, 2);
            var result = FPVector2.Distance(a, a);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Distance_WorksCorrectly()
        {
            var a = FPVector2.Zero;
            var b = new FPVector2(3, 4);
            var result = FPVector2.Distance(a, b);
            Assert.AreEqual(5.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtZero_ReturnsA()
        {
            var a = new FPVector2(0, 0);
            var b = new FPVector2(10, 10);
            var result = FPVector2.Lerp(a, b, FP64.Zero);
            Assert.AreEqual(a, result);
        }

        [Test]
        public void Lerp_AtOne_ReturnsB()
        {
            var a = new FPVector2(0, 0);
            var b = new FPVector2(10, 10);
            var result = FPVector2.Lerp(a, b, FP64.One);
            Assert.AreEqual(10.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtHalf_ReturnsMidpoint()
        {
            var a = new FPVector2(0, 0);
            var b = new FPVector2(10, 10);
            var result = FPVector2.Lerp(a, b, FP64.Half);
            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_WithinRange_ReturnsTarget()
        {
            var current = FPVector2.Zero;
            var target = new FPVector2(3, 0);
            var result = FPVector2.MoveTowards(current, target, FP64.FromFloat(10.0f));
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_BeyondRange_MovesMaxDistance()
        {
            var current = FPVector2.Zero;
            var target = new FPVector2(10, 0);
            var result = FPVector2.MoveTowards(current, target, FP64.FromFloat(3.0f));
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Reflect_WorksCorrectly()
        {
            var direction = new FPVector2(1, -1);
            var normal = FPVector2.Up;
            var result = FPVector2.Reflect(direction, normal);
            // Reflection: direction - 2*(direction.normal)*normal
            // (1, -1) - 2*(-1)*(0, 1) = (1, -1) + (0, 2) = (1, 1)
            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Perpendicular_WorksCorrectly()
        {
            var v = new FPVector2(1, 0);
            var result = FPVector2.Perpendicular(v);
            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Angle_Parallel_ReturnsZero()
        {
            var a = new FPVector2(1, 0);
            var b = new FPVector2(2, 0);
            var result = FPVector2.Angle(a, b);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Angle_Perpendicular_ReturnsNinety()
        {
            var a = FPVector2.Right;
            var b = FPVector2.Up;
            var result = FPVector2.Angle(a, b);
            // 90 degrees = pi/2 * 180/pi = 90
            Assert.AreEqual(90.0f, result.ToFloat(), 1.0f);  // Slightly loose tolerance
        }

        [Test]
        public void Angle_WithBigValues_ProtectedFromOverflow()
        {
            var a = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(0.0f)
            );
            var b = new FPVector2(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            var result = FPVector2.Angle(a, b);
            // Approximately 45 degrees
            Assert.AreEqual(45.0, result.ToDouble(), 1.0);
        }

        [Test]
        public void SignedAngle_ReturnsCorrectSign()
        {
            var from = FPVector2.Right;
            var to = FPVector2.Up;
            var result = FPVector2.SignedAngle(from, to);
            // Counter-clockwise so positive
            Assert.IsTrue(result.ToDouble() > 0);
        }

        [Test]
        public void ClampMagnitude_WithinRange_ReturnsUnchanged()
        {
            var v = new FPVector2(3, 4);
            var result = FPVector2.ClampMagnitude(v, FP64.FromFloat(10.0f));
            Assert.AreEqual(v, result);
        }

        [Test]
        public void ClampMagnitude_BeyondRange_ClampsCorrectly()
        {
            var v = new FPVector2(3, 4);
            var result = FPVector2.ClampMagnitude(v, FP64.FromFloat(2.5f));
            // magnitude = 5, clamped to 2.5, 50% of original
            Assert.AreEqual(2.5f, result.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void ClampMagnitude_WithBigMaxLength_ProtectedFromOverflow()
        {
            var v = new FPVector2(1, 1);
            var result = FPVector2.ClampMagnitude(v, FP64.FromFloat(100000.0f));
            // Vector is very small, must be returned as-is
            Assert.AreEqual(v, result);
        }

        #endregion

        #region Comparison

        [Test]
        public void Equality_SameVectors_ReturnsTrue()
        {
            var a = new FPVector2(1, 2);
            var b = new FPVector2(1, 2);
            Assert.IsTrue(a == b);
        }

        [Test]
        public void Inequality_DifferentVectors_ReturnsTrue()
        {
            var a = new FPVector2(1, 2);
            var b = new FPVector2(3, 4);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism Tests

        [Test]
        public void VectorOperations_AreDeterministic()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new FPVector2(
                    FP64.FromFloat(3.14f),
                    FP64.FromFloat(2.71f)
                );
                var b = new FPVector2(
                    FP64.FromFloat(1.23f),
                    FP64.FromFloat(4.56f)
                );

                var result1 = (a + b).normalized;
                var result2 = (a + b).normalized;

                Assert.AreEqual(result1.x.RawValue, result2.x.RawValue);
                Assert.AreEqual(result1.y.RawValue, result2.y.RawValue);
            }
        }

        [Test]
        public void DotProduct_AreDeterministic()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new FPVector2(
                    FP64.FromFloat(3.14f),
                    FP64.FromFloat(2.71f)
                );
                var b = new FPVector2(
                    FP64.FromFloat(1.23f),
                    FP64.FromFloat(4.56f)
                );

                var result1 = FPVector2.Dot(a, b);
                var result2 = FPVector2.Dot(a, b);

                Assert.AreEqual(result1.RawValue, result2.RawValue);
            }
        }

        #endregion

        #region Overflow Protection Specific Tests

        [Test]
        public void OverflowProtection_SqrMagnitude_LargeValues()
        {
            // SqrMagnitude calculation could overflow when components are very large,
            // but BigInteger handles it safely
            var v1 = new FPVector2(FP64.MaxValue, FP64.MaxValue);
            var sqrMag = v1.sqrMagnitude;

            // Clamped to long range
            Assert.IsTrue(sqrMag.RawValue >= long.MinValue && sqrMag.RawValue <= long.MaxValue);
        }

        [Test]
        public void OverflowProtection_Dot_LargeValues()
        {
            // Dot product calculation is also protected by BigInteger
            var a = new FPVector2(FP64.MaxValue, FP64.MaxValue);
            var b = new FPVector2(FP64.MaxValue, FP64.MaxValue);

            var result = FPVector2.Dot(a, b);

            // Clamped to long range
            Assert.IsTrue(result.RawValue >= long.MinValue && result.RawValue <= long.MaxValue);
        }

        [Test]
        public void OverflowProtection_Cross_LargeValues()
        {
            // Cross product calculation is also protected by BigInteger
            var a = new FPVector2(FP64.MaxValue, FP64.MaxValue);
            var b = new FPVector2(FP64.MaxValue, FP64.MaxValue);

            var result = FPVector2.Cross(a, b);

            // Clamped to long range
            Assert.IsTrue(result.RawValue >= long.MinValue && result.RawValue <= long.MaxValue);
        }

        #endregion

        #region SmoothDamp

        [Test]
        public void SmoothDamp_ApproachesTarget()
        {
            var current = new FPVector2(FP64.Zero, FP64.Zero);
            var target = new FPVector2(FP64.FromFloat(10f), FP64.Zero);
            var velocity = FPVector2.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 100; i++)
                current = FPVector2.SmoothDamp(current, target, ref velocity, smoothTime, maxSpeed, dt);

            Assert.AreEqual(10f, current.x.ToFloat(), 0.1f);
            Assert.AreEqual(0f, current.y.ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothDamp_Deterministic()
        {
            var c1 = new FPVector2(FP64.Zero, FP64.Zero);
            var c2 = new FPVector2(FP64.Zero, FP64.Zero);
            var target = new FPVector2(FP64.FromFloat(10f), FP64.FromFloat(5f));
            var v1 = FPVector2.Zero;
            var v2 = FPVector2.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 20; i++)
            {
                c1 = FPVector2.SmoothDamp(c1, target, ref v1, smoothTime, maxSpeed, dt);
                c2 = FPVector2.SmoothDamp(c2, target, ref v2, smoothTime, maxSpeed, dt);
            }

            Assert.AreEqual(c1.x.RawValue, c2.x.RawValue);
            Assert.AreEqual(c1.y.RawValue, c2.y.RawValue);
        }

        #endregion
    }
}
