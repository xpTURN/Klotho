using UnityEngine;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    /// <summary>
    /// FPVector3 vector operations test
    /// </summary>
    [TestFixture]
    public class FPVector3Tests
    {
        private const float EPSILON = 0.01f;

        #region Creation and Conversion

        [Test]
        public void Constructor_CreatesCorrectVector()
        {
            var v = new FPVector3(FP64.FromFloat(1.0f), FP64.FromFloat(2.0f), FP64.FromFloat(3.0f));
            Assert.AreEqual(1.0f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v.z.ToFloat(), EPSILON);
        }

        [Test]
        public void IntConstructor_CreatesCorrectVector()
        {
            var v = new FPVector3(1, 2, 3);
            Assert.AreEqual(1.0f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v.z.ToFloat(), EPSILON);
        }

        [Test]
        public void FromVector3_ConvertsCorrectly()
        {
            var unity = new Vector3(1.5f, 2.5f, 3.5f);
            var fp = new FPVector3();
            fp.FromVector3(unity);
            Assert.AreEqual(1.5f, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, fp.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.5f, fp.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ToVector3_ConvertsCorrectly()
        {
            var fp = new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(2.5f), FP64.FromFloat(3.5f));
            var unity = fp.ToVector3();
            Assert.AreEqual(1.5f, unity.x, EPSILON);
            Assert.AreEqual(2.5f, unity.y, EPSILON);
            Assert.AreEqual(3.5f, unity.z, EPSILON);
        }

        [Test]
        public void ToFPVector3_ConvertsCorrectly()
        {
            var unity = new Vector3(1.5f, 2.5f, 3.5f);
            var fp = unity.ToFPVector3();
            Assert.AreEqual(1.5f, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, fp.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.5f, fp.z.ToFloat(), EPSILON);
        }

        [Test]
        public void RoundTrip_UnityToFPAndBack()
        {
            var original = new Vector3(1.5f, -2.5f, 3.5f);
            var fp = original.ToFPVector3();
            var roundTrip = fp.ToVector3();
            Assert.AreEqual(original.x, roundTrip.x, EPSILON);
            Assert.AreEqual(original.y, roundTrip.y, EPSILON);
            Assert.AreEqual(original.z, roundTrip.z, EPSILON);
        }

        #endregion

        #region Constants

        [Test]
        public void Zero_IsAllZeros()
        {
            Assert.AreEqual(FP64.Zero, FPVector3.Zero.x);
            Assert.AreEqual(FP64.Zero, FPVector3.Zero.y);
            Assert.AreEqual(FP64.Zero, FPVector3.Zero.z);
        }

        [Test]
        public void One_IsAllOnes()
        {
            Assert.AreEqual(FP64.One, FPVector3.One.x);
            Assert.AreEqual(FP64.One, FPVector3.One.y);
            Assert.AreEqual(FP64.One, FPVector3.One.z);
        }

        [Test]
        public void Up_IsCorrect()
        {
            Assert.AreEqual(FP64.Zero, FPVector3.Up.x);
            Assert.AreEqual(FP64.One, FPVector3.Up.y);
            Assert.AreEqual(FP64.Zero, FPVector3.Up.z);
        }

        #endregion

        #region Basic Operations

        [Test]
        public void Addition_WorksCorrectly()
        {
            var a = new FPVector3(1, 2, 3);
            var b = new FPVector3(4, 5, 6);
            var result = a + b;

            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(9.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = new FPVector3(5, 7, 9);
            var b = new FPVector3(1, 2, 3);
            var result = a - b;

            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var v = new FPVector3(1, 2, 3);
            var result = -v;

            Assert.AreEqual(-1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiplication_WorksCorrectly()
        {
            var v = new FPVector3(1, 2, 3);
            var scalar = FP64.FromFloat(2.0f);
            var result = v * scalar;

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarDivision_WorksCorrectly()
        {
            var v = new FPVector3(4, 6, 8);
            var scalar = FP64.FromFloat(2.0f);
            var result = v / scalar;

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Division_ByZero_ReturnsZero()
        {
            var v = new FPVector3(1, 2, 3);
            var result = v / FP64.Zero;
            Assert.AreEqual(FPVector3.Zero, result);
        }

        #endregion

        #region Vector Functions

        [Test]
        public void Magnitude_UnitVector_ReturnsOne()
        {
            var v = FPVector3.Right;
            Assert.AreEqual(1.0f, v.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Magnitude_3_4_0_Returns5()
        {
            var v = new FPVector3(3, 4, 0);
            Assert.AreEqual(5.0f, v.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrMagnitude_WorksCorrectly()
        {
            var v = new FPVector3(3, 4, 0);
            Assert.AreEqual(25.0f, v.sqrMagnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Normalized_ReturnsUnitVector()
        {
            var v = new FPVector3(3, 0, 4);
            var normalized = v.normalized;
            Assert.AreEqual(1.0f, normalized.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Normalized_Zero_ReturnsZero()
        {
            var result = FPVector3.Zero.normalized;
            Assert.AreEqual(FPVector3.Zero, result);
        }

        [Test]
        public void Dot_Perpendicular_ReturnsZero()
        {
            var a = FPVector3.Right;
            var b = FPVector3.Up;
            var result = FPVector3.Dot(a, b);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Dot_Parallel_ReturnsProduct()
        {
            var a = new FPVector3(2, 0, 0);
            var b = new FPVector3(3, 0, 0);
            var result = FPVector3.Dot(a, b);
            Assert.AreEqual(6.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Cross_RightAndUp_ReturnsForward()
        {
            var result = FPVector3.Cross(FPVector3.Right, FPVector3.Up);
            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Distance_SamePoint_ReturnsZero()
        {
            var a = new FPVector3(1, 2, 3);
            var result = FPVector3.Distance(a, a);
            Assert.AreEqual(0.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Distance_WorksCorrectly()
        {
            var a = FPVector3.Zero;
            var b = new FPVector3(3, 4, 0);
            var result = FPVector3.Distance(a, b);
            Assert.AreEqual(5.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtZero_ReturnsA()
        {
            var a = new FPVector3(0, 0, 0);
            var b = new FPVector3(10, 10, 10);
            var result = FPVector3.Lerp(a, b, FP64.Zero);
            Assert.AreEqual(a, result);
        }

        [Test]
        public void Lerp_AtOne_ReturnsB()
        {
            var a = new FPVector3(0, 0, 0);
            var b = new FPVector3(10, 10, 10);
            var result = FPVector3.Lerp(a, b, FP64.One);
            Assert.AreEqual(10.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtHalf_ReturnsMidpoint()
        {
            var a = new FPVector3(0, 0, 0);
            var b = new FPVector3(10, 10, 10);
            var result = FPVector3.Lerp(a, b, FP64.Half);
            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_WithinRange_ReturnsTarget()
        {
            var current = FPVector3.Zero;
            var target = new FPVector3(3, 0, 0);
            var result = FPVector3.MoveTowards(current, target, FP64.FromFloat(10.0f));
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_BeyondRange_MovesMaxDistance()
        {
            var current = FPVector3.Zero;
            var target = new FPVector3(10, 0, 0);
            var result = FPVector3.MoveTowards(current, target, FP64.FromFloat(3.0f));
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Project_OntoAxis_WorksCorrectly()
        {
            var vector = new FPVector3(3, 4, 0);
            var onNormal = FPVector3.Right;
            var result = FPVector3.Project(vector, onNormal);
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Project_OntoZero_ReturnsZero()
        {
            var vector = new FPVector3(3, 4, 0);
            var result = FPVector3.Project(vector, FPVector3.Zero);
            Assert.AreEqual(FPVector3.Zero, result);
        }

        #endregion

        #region Comparison

        [Test]
        public void Equality_SameVectors_ReturnsTrue()
        {
            var a = new FPVector3(1, 2, 3);
            var b = new FPVector3(1, 2, 3);
            Assert.IsTrue(a == b);
        }

        [Test]
        public void Inequality_DifferentVectors_ReturnsTrue()
        {
            var a = new FPVector3(1, 2, 3);
            var b = new FPVector3(4, 5, 6);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism Tests

        [Test]
        public void VectorOperations_AreDeterministic()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new FPVector3(
                    FP64.FromFloat(3.14f),
                    FP64.FromFloat(2.71f),
                    FP64.FromFloat(1.41f)
                );
                var b = new FPVector3(
                    FP64.FromFloat(1.23f),
                    FP64.FromFloat(4.56f),
                    FP64.FromFloat(7.89f)
                );

                var result1 = (a + b).normalized;
                var result2 = (b + a).normalized;

                Assert.AreEqual(result1.x.RawValue, result2.x.RawValue);
                Assert.AreEqual(result1.y.RawValue, result2.y.RawValue);
                Assert.AreEqual(result1.z.RawValue, result2.z.RawValue);
            }
        }

        [Test]
        public void DotProduct_AreDeterministic()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new FPVector3(
                    FP64.FromFloat(3.14f),
                    FP64.FromFloat(2.71f),
                    FP64.FromFloat(1.41f)
                );
                var b = new FPVector3(
                    FP64.FromFloat(1.23f),
                    FP64.FromFloat(4.56f),
                    FP64.FromFloat(7.89f)
                );

                var result1 = FPVector3.Dot(a, b);
                var result2 = FPVector3.Dot(b, a);

                Assert.AreEqual(result1.RawValue, result2.RawValue);
            }
        }

        #endregion

        #region Overflow Protection Specific Tests

        [Test]
        public void OverflowProtection_SqrMagnitude_LargeValues()
        {
            // MaxValue * MaxValue saturates; no exception thrown
            var v1 = new FPVector3(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);
            Assert.DoesNotThrow(() => { var _ = v1.sqrMagnitude; });
        }

        [Test]
        public void OverflowProtection_Dot_LargeValues()
        {
            // MaxValue dot MaxValue saturates; no exception thrown
            var a = new FPVector3(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);
            var b = new FPVector3(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);
            Assert.DoesNotThrow(() => FPVector3.Dot(a, b));
        }

        [Test]
        public void OverflowProtection_Scale_LargeValues()
        {
            var a = new FPVector3(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);
            var b = new FPVector3(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);

            var result = FPVector3.Scale(a, b);

            // Each component is clamped to long range (saturated)
            Assert.IsTrue(result.x.RawValue == long.MaxValue);
            Assert.IsTrue(result.y.RawValue == long.MaxValue);
            Assert.IsTrue(result.z.RawValue == long.MaxValue);
        }

        [Test]
        public void OverflowProtection_Angle_LargeValues()
        {
            // Angle calculation is also protected by BigInteger (sqrMagnitude multiplication)
            var a = new FPVector3(
                FP64.FromFloat(30000.0f),
                FP64.FromFloat(0.0f),
                FP64.FromFloat(0.0f)
            );
            var b = new FPVector3(
                FP64.FromFloat(30000.0f),
                FP64.FromFloat(30000.0f),
                FP64.FromFloat(0.0f)
            );
            
            var result = FPVector3.Angle(a, b);
            
            // Should be approximately 45 degrees
            Assert.AreEqual(45.0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void OverflowProtection_ClampMagnitude_LargeMaxLength()
        {
            var v = new FPVector3(1, 1, 1);
            var result = FPVector3.ClampMagnitude(v, FP64.FromFloat(100000.0f));

            // Vector is very small, must be returned as-is
            Assert.AreEqual(v, result);
        }

        [Test]
        public void SqrMagnitude_LargeValue()
        {
            // Must compute accurately without overflow even for large values
            var v = new FPVector3(
                FP64.FromFloat(10000.0f),
                FP64.FromFloat(10000.0f),
                FP64.FromFloat(10000.0f)
            );
            var sqrMag = v.sqrMagnitude;
            // 10000^2 * 3 = 300000000
            Assert.AreEqual(300000000.0, sqrMag.ToDouble(), EPSILON);
        }

        [Test]
        public void SqrMagnitude_WithBigValues_ProtectedFromOverflow()
        {
            // 100000^2 = 10^10 exceeds the Q32.32 range; saturates without exception
            var v = new FPVector3(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            Assert.DoesNotThrow(() => { var _ = v.sqrMagnitude; });
        }

        [Test]
        public void Dot_LargeValue()
        {
            // Dot product with large values is also protected by BigInteger
            var a = new FPVector3(
                FP64.FromFloat(10000.0f),
                FP64.FromFloat(10000.0f),
                FP64.FromFloat(10000.0f)
            );
            var b = new FPVector3(
                FP64.FromFloat(10000.0f),
                FP64.FromFloat(10000.0f),
                FP64.FromFloat(10000.0f)
            );
            var result = FPVector3.Dot(a, b);
            // 10000*10000 * 3 = 300000000
            Assert.AreEqual(300000000.0, result.ToDouble(), EPSILON);
        }

        [Test]
        public void Dot_WithBigValues_ProtectedFromOverflow()
        {
            // 100000*100000 = 10^10 exceeds the Q32.32 range; saturates without exception
            var a = new FPVector3(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            var b = new FPVector3(
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f),
                FP64.FromFloat(100000.0f)
            );
            Assert.DoesNotThrow(() => FPVector3.Dot(a, b));
        }

        #endregion

        #region SmoothDamp

        [Test]
        public void SmoothDamp_ApproachesTarget()
        {
            var current = new FPVector3(0, 0, 0);
            var target = new FPVector3(10, 0, 0);
            var velocity = FPVector3.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 100; i++)
                current = FPVector3.SmoothDamp(current, target, ref velocity, smoothTime, maxSpeed, dt);

            Assert.AreEqual(10f, current.x.ToFloat(), 0.1f);
            Assert.AreEqual(0f, current.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, current.z.ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothDamp_AtTarget_StaysAtTarget()
        {
            var current = new FPVector3(5, 5, 5);
            var target = new FPVector3(5, 5, 5);
            var velocity = FPVector3.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1000f);
            FP64 dt = FP64.FromFloat(0.02f);

            var result = FPVector3.SmoothDamp(current, target, ref velocity, smoothTime, maxSpeed, dt);
            Assert.AreEqual(5f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(5f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void SmoothDamp_Deterministic()
        {
            for (int trial = 0; trial < 10; trial++)
            {
                var c1 = new FPVector3(FP64.FromFloat(trial), FP64.Zero, FP64.Zero);
                var c2 = c1;
                var target = new FPVector3(10, 0, 0);
                var v1 = FPVector3.Zero;
                var v2 = FPVector3.Zero;
                FP64 smoothTime = FP64.FromFloat(0.3f);
                FP64 maxSpeed = FP64.FromFloat(1000f);
                FP64 dt = FP64.FromFloat(0.02f);

                for (int i = 0; i < 20; i++)
                {
                    c1 = FPVector3.SmoothDamp(c1, target, ref v1, smoothTime, maxSpeed, dt);
                    c2 = FPVector3.SmoothDamp(c2, target, ref v2, smoothTime, maxSpeed, dt);
                }

                Assert.AreEqual(c1.x.RawValue, c2.x.RawValue);
                Assert.AreEqual(c1.y.RawValue, c2.y.RawValue);
                Assert.AreEqual(c1.z.RawValue, c2.z.RawValue);
            }
        }

        [Test]
        public void SmoothDamp_RespectsMaxSpeed()
        {
            var current = new FPVector3(0, 0, 0);
            var target = new FPVector3(1000, 0, 0);
            var velocity = FPVector3.Zero;
            FP64 smoothTime = FP64.FromFloat(0.3f);
            FP64 maxSpeed = FP64.FromFloat(1f);
            FP64 dt = FP64.FromFloat(0.02f);

            var result = FPVector3.SmoothDamp(current, target, ref velocity, smoothTime, maxSpeed, dt);

            // When maxSpeed=1, displacement after one frame (dt=0.02) must be small
            FP64 displacement = (result - current).magnitude;
            Assert.Less(displacement.ToFloat(), 0.5f);
        }

        #endregion

        #region SignedAngle

        [Test]
        public void SignedAngle_Positive_CCW()
        {
            // Rotation from Left to Forward around Up: Cross((-1,0,0),(0,0,1))=(0,1,0), dot(Up,(0,1,0))=1 -> +90
            var result = FPVector3.SignedAngle(FPVector3.Left, FPVector3.Forward, FPVector3.Up);
            Assert.AreEqual(90f, result.ToFloat(), 0.5f);
        }

        [Test]
        public void SignedAngle_Negative_CW()
        {
            // Rotation from Right to Forward around Up: Cross((1,0,0),(0,0,1))=(0,-1,0), dot(Up,(0,-1,0))=-1 -> -90
            var result = FPVector3.SignedAngle(FPVector3.Right, FPVector3.Forward, FPVector3.Up);
            Assert.AreEqual(-90f, result.ToFloat(), 0.5f);
        }

        [Test]
        public void SignedAngle_SameVector_ReturnsZero()
        {
            var result = FPVector3.SignedAngle(FPVector3.Right, FPVector3.Right, FPVector3.Up);
            Assert.AreEqual(0f, result.ToFloat(), EPSILON);
        }

        [Test]
        public void SignedAngle_Opposite_Returns180()
        {
            var result = FPVector3.SignedAngle(FPVector3.Right, FPVector3.Left, FPVector3.Up);
            Assert.AreEqual(180f, System.Math.Abs(result.ToFloat()), 0.5f);
        }

        #endregion

        #region Slerp

        [Test]
        public void Slerp_AtZero_ReturnsA()
        {
            var a = new FPVector3(1, 0, 0);
            var b = new FPVector3(0, 1, 0);
            var result = FPVector3.Slerp(a, b, FP64.Zero);
            Assert.AreEqual(1f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Slerp_AtOne_ReturnsB()
        {
            var a = new FPVector3(1, 0, 0);
            var b = new FPVector3(0, 1, 0);
            var result = FPVector3.Slerp(a, b, FP64.One);
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Slerp_AtHalf_PreservesMagnitude()
        {
            var a = new FPVector3(1, 0, 0);
            var b = new FPVector3(0, 1, 0);
            var result = FPVector3.Slerp(a, b, FP64.Half);
            Assert.AreEqual(1f, result.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Slerp_AtHalf_Correct45()
        {
            // For Slerp between (1,0,0) and (0,1,0), t=0.5 must be 45 degrees
            var a = new FPVector3(1, 0, 0);
            var b = new FPVector3(0, 1, 0);
            var result = FPVector3.Slerp(a, b, FP64.Half);
            float expected = (float)System.Math.Cos(System.Math.PI / 4.0); // ~0.7071
            Assert.AreEqual(expected, result.x.ToFloat(), 0.02f);
            Assert.AreEqual(expected, result.y.ToFloat(), 0.02f);
        }

        [Test]
        public void SlerpUnclamped_Extrapolates()
        {
            var a = new FPVector3(1, 0, 0);
            var b = new FPVector3(0, 1, 0);
            var result = FPVector3.SlerpUnclamped(a, b, FP64.FromFloat(2f));
            // t=2 continues past b by 90 degrees -> must be (-1, 0, 0)
            Assert.AreEqual(-1f, result.x.ToFloat(), 0.05f);
            Assert.AreEqual(0f, result.y.ToFloat(), 0.05f);
        }

        [Test]
        public void Slerp_DifferentMagnitudes_InterpolatesMagnitude()
        {
            var a = new FPVector3(2, 0, 0);
            var b = new FPVector3(0, 4, 0);
            var result = FPVector3.Slerp(a, b, FP64.Half);
            // Magnitude interpolated between 2 and 4 -> 3
            Assert.AreEqual(3f, result.magnitude.ToFloat(), 0.1f);
        }

        #endregion

        #region RotateTowards

        [Test]
        public void RotateTowards_SmallDelta_PartialRotation()
        {
            var current = new FPVector3(1, 0, 0);
            var target = new FPVector3(0, 1, 0);
            FP64 maxRadians = FP64.FromDouble(System.Math.PI / 4); // 45 degrees
            var result = FPVector3.RotateTowards(current, target, maxRadians, FP64.FromFloat(10f));
            // Must rotate 45 degrees from X toward Y direction
            float expected = (float)System.Math.Cos(System.Math.PI / 4);
            Assert.AreEqual(expected, result.x.ToFloat(), 0.05f);
            Assert.AreEqual(expected, result.y.ToFloat(), 0.05f);
        }

        [Test]
        public void RotateTowards_LargeDelta_ReachesTarget()
        {
            var current = new FPVector3(1, 0, 0);
            var target = new FPVector3(0, 1, 0);
            FP64 maxRadians = FP64.FromDouble(System.Math.PI); // 180 degrees
            var result = FPVector3.RotateTowards(current, target, maxRadians, FP64.FromFloat(10f));
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateTowards_MagnitudeClamp()
        {
            var current = new FPVector3(1, 0, 0);
            var target = new FPVector3(0, 5, 0);
            FP64 maxRadians = FP64.FromDouble(System.Math.PI); // full rotation allowed
            FP64 maxMagDelta = FP64.FromFloat(1f); // But magnitude change limited to 1 unit
            var result = FPVector3.RotateTowards(current, target, maxRadians, maxMagDelta);
            // Direction must match the target direction but magnitude is clamped: 1 + 1 = 2
            Assert.AreEqual(2f, result.magnitude.ToFloat(), 0.1f);
        }

        [Test]
        public void RotateTowards_SameDirection_OnlyMagnitudeChanges()
        {
            var current = new FPVector3(1, 0, 0);
            var target = new FPVector3(5, 0, 0);
            FP64 maxRadians = FP64.FromFloat(1f);
            FP64 maxMagDelta = FP64.FromFloat(2f);
            var result = FPVector3.RotateTowards(current, target, maxRadians, maxMagDelta);
            // Same direction, magnitude changes from 1 to 3
            Assert.AreEqual(3f, result.x.ToFloat(), 0.1f);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region OrthoNormalize

        [Test]
        public void OrthoNormalize_2Vectors_Orthogonal()
        {
            var normal = new FPVector3(1, 1, 0);
            var tangent = new FPVector3(1, 0, 0);
            FPVector3.OrthoNormalize(ref normal, ref tangent);

            Assert.AreEqual(1f, normal.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(1f, tangent.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(0f, FPVector3.Dot(normal, tangent).ToFloat(), EPSILON);
        }

        [Test]
        public void OrthoNormalize_3Vectors_AllOrthogonal()
        {
            var normal = new FPVector3(1, 1, 0);
            var tangent = new FPVector3(1, 0, 1);
            var binormal = new FPVector3(0, 1, 1);
            FPVector3.OrthoNormalize(ref normal, ref tangent, ref binormal);

            Assert.AreEqual(1f, normal.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(1f, tangent.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(1f, binormal.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(0f, FPVector3.Dot(normal, tangent).ToFloat(), EPSILON);
            Assert.AreEqual(0f, FPVector3.Dot(normal, binormal).ToFloat(), EPSILON);
            Assert.AreEqual(0f, FPVector3.Dot(tangent, binormal).ToFloat(), EPSILON);
        }

        [Test]
        public void OrthoNormalize_AlreadyOrthonormal_StaysTheSame()
        {
            var normal = FPVector3.Right;
            var tangent = FPVector3.Up;
            FPVector3.OrthoNormalize(ref normal, ref tangent);

            Assert.AreEqual(1f, normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, tangent.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, tangent.y.ToFloat(), EPSILON);
        }

        #endregion
    }
}
