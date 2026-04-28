using UnityEngine;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    [TestFixture]
    public class FPQuaternionTests
    {
        private const float EPSILON = 0.01f;

        #region Creation and Constants

        [Test]
        public void Identity_IsCorrect()
        {
            Assert.AreEqual(0f, FPQuaternion.Identity.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, FPQuaternion.Identity.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, FPQuaternion.Identity.z.ToFloat(), EPSILON);
            Assert.AreEqual(1f, FPQuaternion.Identity.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_StoresComponents()
        {
            var q = new FPQuaternion(FP64.FromFloat(0.1f), FP64.FromFloat(0.2f), FP64.FromFloat(0.3f), FP64.FromFloat(0.9f));
            Assert.AreEqual(0.1f, q.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.2f, q.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.3f, q.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.9f, q.w.ToFloat(), EPSILON);
        }

        [Test]
        public void FromQuaternion_ToQuaternion_Roundtrip()
        {
            var unity = Quaternion.Euler(30f, 45f, 60f);
            var fp = new FPQuaternion();
            fp.FromQuaternion(unity);
            var back = fp.ToQuaternion();
            Assert.AreEqual(unity.x, back.x, EPSILON);
            Assert.AreEqual(unity.y, back.y, EPSILON);
            Assert.AreEqual(unity.z, back.z, EPSILON);
            Assert.AreEqual(unity.w, back.w, EPSILON);
        }

        [Test]
        public void ToFPQuaternion_ConvertsCorrectly()
        {
            var unity = Quaternion.Euler(30f, 45f, 60f);
            var fp = unity.ToFPQuaternion();
            Assert.AreEqual(unity.x, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(unity.y, fp.y.ToFloat(), EPSILON);
            Assert.AreEqual(unity.z, fp.z.ToFloat(), EPSILON);
            Assert.AreEqual(unity.w, fp.w.ToFloat(), EPSILON);
        }

        #endregion

        #region Properties

        [Test]
        public void SqrMagnitude_Identity_IsOne()
        {
            Assert.AreEqual(1f, FPQuaternion.Identity.sqrMagnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Magnitude_Identity_IsOne()
        {
            Assert.AreEqual(1f, FPQuaternion.Identity.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Normalized_NonUnit_ReturnsUnit()
        {
            var q = new FPQuaternion(FP64.FromFloat(0f), FP64.FromFloat(0f), FP64.FromFloat(0f), FP64.FromFloat(2f));
            var n = q.normalized;
            Assert.AreEqual(1f, n.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(1f, n.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Normalized_Zero_ReturnsIdentity()
        {
            var q = new FPQuaternion(FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero);
            Assert.AreEqual(FPQuaternion.Identity, q.normalized);
        }

        [Test]
        public void Conjugate_Correctness()
        {
            var q = new FPQuaternion(FP64.FromFloat(0.1f), FP64.FromFloat(0.2f), FP64.FromFloat(0.3f), FP64.FromFloat(0.9f));
            var c = q.conjugate;
            Assert.AreEqual(-0.1f, c.x.ToFloat(), EPSILON);
            Assert.AreEqual(-0.2f, c.y.ToFloat(), EPSILON);
            Assert.AreEqual(-0.3f, c.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.9f, c.w.ToFloat(), EPSILON);
        }

        #endregion

        #region Hamilton Product

        [Test]
        public void Multiply_WithIdentity_ReturnsSame()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            var result = q * FPQuaternion.Identity;
            Assert.AreEqual(q.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(q.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(q.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(q.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Multiply_IdentityWithQ_ReturnsSame()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            var result = FPQuaternion.Identity * q;
            Assert.AreEqual(q.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(q.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(q.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(q.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Multiply_Associativity()
        {
            var a = FPQuaternion.AngleAxis(FP64.FromFloat(30f), FPVector3.Up);
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Right);
            var c = FPQuaternion.AngleAxis(FP64.FromFloat(60f), FPVector3.Forward);

            var ab_c = (a * b) * c;
            var a_bc = a * (b * c);

            // FP64 multiplication is not bit-exact associative (±1 raw unit rounding)
            Assert.AreEqual(ab_c.x.ToFloat(), a_bc.x.ToFloat(), EPSILON);
            Assert.AreEqual(ab_c.y.ToFloat(), a_bc.y.ToFloat(), EPSILON);
            Assert.AreEqual(ab_c.z.ToFloat(), a_bc.z.ToFloat(), EPSILON);
            Assert.AreEqual(ab_c.w.ToFloat(), a_bc.w.ToFloat(), EPSILON);
        }

        #endregion

        #region Vector Rotation

        [Test]
        public void RotateVector_Identity_ReturnsSame()
        {
            var v = new FPVector3(FP64.FromFloat(1f), FP64.FromFloat(2f), FP64.FromFloat(3f));
            var result = FPQuaternion.Identity * v;
            Assert.AreEqual(1f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(3f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateVector_90DegY_ForwardToRight()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var result = q * FPVector3.Forward;
            // Rotate Forward(0,0,1) 90 degrees around Y-axis => Right(1,0,0)
            Assert.AreEqual(1f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateVector_180DegY_ForwardToBack()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(180f), FPVector3.Up);
            var result = q * FPVector3.Forward;
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(-1f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateVector_90DegX_ForwardToDown()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Right);
            var result = q * FPVector3.Forward;
            // Rotate Forward(0,0,1) 90 degrees around X-axis => Down(0,-1,0)
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(-1f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateVector_CompareWithUnity()
        {
            var unityQ = Quaternion.Euler(30f, 45f, 60f);
            var fpQ = FPQuaternion.Euler(FP64.FromFloat(30f), FP64.FromFloat(45f), FP64.FromFloat(60f));

            var unityV = new Vector3(1f, 2f, 3f);
            var fpV = new FPVector3(FP64.FromFloat(1f), FP64.FromFloat(2f), FP64.FromFloat(3f));

            var unityResult = unityQ * unityV;
            var fpResult = fpQ * fpV;

            Assert.AreEqual(unityResult.x, fpResult.x.ToFloat(), 0.05f);
            Assert.AreEqual(unityResult.y, fpResult.y.ToFloat(), 0.05f);
            Assert.AreEqual(unityResult.z, fpResult.z.ToFloat(), 0.05f);
        }

        #endregion

        #region Factory Methods

        [Test]
        public void AngleAxis_ZeroDegrees_ReturnsIdentity()
        {
            var q = FPQuaternion.AngleAxis(FP64.Zero, FPVector3.Up);
            Assert.AreEqual(0f, q.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, q.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, q.z.ToFloat(), EPSILON);
            Assert.AreEqual(1f, q.w.ToFloat(), EPSILON);
        }

        [Test]
        public void AngleAxis_ZeroAxis_ReturnsIdentity()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Zero);
            Assert.AreEqual(FPQuaternion.Identity, q);
        }

        [Test]
        public void AngleAxis_90DegY_Correctness()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var unityQ = Quaternion.AngleAxis(90f, Vector3.up);
            Assert.AreEqual(unityQ.x, q.x.ToFloat(), EPSILON);
            Assert.AreEqual(unityQ.y, q.y.ToFloat(), EPSILON);
            Assert.AreEqual(unityQ.z, q.z.ToFloat(), EPSILON);
            Assert.AreEqual(unityQ.w, q.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Euler_Zero_ReturnsIdentity()
        {
            var q = FPQuaternion.Euler(FP64.Zero, FP64.Zero, FP64.Zero);
            Assert.AreEqual(0f, q.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, q.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, q.z.ToFloat(), EPSILON);
            Assert.AreEqual(1f, q.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Euler_90Y_MatchesAngleAxis()
        {
            var euler = FPQuaternion.Euler(FP64.Zero, FP64.FromFloat(90f), FP64.Zero);
            var angleAxis = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            Assert.AreEqual(angleAxis.x.ToFloat(), euler.x.ToFloat(), EPSILON);
            Assert.AreEqual(angleAxis.y.ToFloat(), euler.y.ToFloat(), EPSILON);
            Assert.AreEqual(angleAxis.z.ToFloat(), euler.z.ToFloat(), EPSILON);
            Assert.AreEqual(angleAxis.w.ToFloat(), euler.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Euler_CompareWithUnity()
        {
            var unityQ = Quaternion.Euler(30f, 45f, 60f);
            var fpQ = FPQuaternion.Euler(FP64.FromFloat(30f), FP64.FromFloat(45f), FP64.FromFloat(60f));
            Assert.AreEqual(unityQ.x, fpQ.x.ToFloat(), EPSILON);
            Assert.AreEqual(unityQ.y, fpQ.y.ToFloat(), EPSILON);
            Assert.AreEqual(unityQ.z, fpQ.z.ToFloat(), EPSILON);
            Assert.AreEqual(unityQ.w, fpQ.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Euler_VectorOverload_MatchesComponents()
        {
            var q1 = FPQuaternion.Euler(FP64.FromFloat(10f), FP64.FromFloat(20f), FP64.FromFloat(30f));
            var q2 = FPQuaternion.Euler(new FPVector3(FP64.FromFloat(10f), FP64.FromFloat(20f), FP64.FromFloat(30f)));
            Assert.AreEqual(q1, q2);
        }

        [Test]
        public void LookRotation_Forward_ReturnsIdentity()
        {
            var q = FPQuaternion.LookRotation(FPVector3.Forward);
            var result = q * FPVector3.Forward;
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void LookRotation_Right_Rotates90Y()
        {
            var q = FPQuaternion.LookRotation(FPVector3.Right);
            var result = q * FPVector3.Forward;
            Assert.AreEqual(1f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void LookRotation_Zero_ReturnsIdentity()
        {
            var q = FPQuaternion.LookRotation(FPVector3.Zero);
            Assert.AreEqual(FPQuaternion.Identity, q);
        }

        [Test]
        public void FromToRotation_SameDirection_ReturnsIdentity()
        {
            var q = FPQuaternion.FromToRotation(FPVector3.Forward, FPVector3.Forward);
            Assert.AreEqual(1f, q.w.ToFloat(), EPSILON);
        }

        [Test]
        public void FromToRotation_ForwardToRight()
        {
            var q = FPQuaternion.FromToRotation(FPVector3.Forward, FPVector3.Right);
            var result = q * FPVector3.Forward;
            Assert.AreEqual(1f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void FromToRotation_OppositeDirections()
        {
            var q = FPQuaternion.FromToRotation(FPVector3.Forward, FPVector3.Back);
            var result = q * FPVector3.Forward;
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(-1f, result.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Static Operations

        [Test]
        public void Dot_IdentityWithIdentity_IsOne()
        {
            Assert.AreEqual(1f, FPQuaternion.Dot(FPQuaternion.Identity, FPQuaternion.Identity).ToFloat(), EPSILON);
        }

        [Test]
        public void Angle_SameQuaternion_IsZero()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            // FP64 dot product of unit quaternions may not be exactly 1.0
            // Tolerance is set slightly wider due to fixed-point multiplication rounding
            Assert.AreEqual(0f, FPQuaternion.Angle(q, q).ToFloat(), 0.1f);
        }

        [Test]
        public void Angle_90DegRotation_Returns90()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            Assert.AreEqual(90f, FPQuaternion.Angle(a, b).ToFloat(), 0.5f);
        }

        [Test]
        public void Inverse_QTimesInverse_IsIdentity()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            var inv = FPQuaternion.Inverse(q);
            var result = q * inv;
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Inverse_Identity_IsIdentity()
        {
            var inv = FPQuaternion.Inverse(FPQuaternion.Identity);
            Assert.AreEqual(FPQuaternion.Identity, inv);
        }

        #endregion

        #region Interpolation

        [Test]
        public void Lerp_T0_ReturnsA()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var result = FPQuaternion.Lerp(a, b, FP64.Zero);
            Assert.AreEqual(a.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(a.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(a.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(a.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_T1_ReturnsB()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var result = FPQuaternion.Lerp(a, b, FP64.One);
            Assert.AreEqual(b.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(b.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(b.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(b.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Slerp_T0_ReturnsA()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var result = FPQuaternion.Slerp(a, b, FP64.Zero);
            Assert.AreEqual(a.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(a.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(a.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(a.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Slerp_T1_ReturnsB()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var result = FPQuaternion.Slerp(a, b, FP64.One);
            Assert.AreEqual(b.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(b.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(b.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(b.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Slerp_T05_MidpointRotation()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var mid = FPQuaternion.Slerp(a, b, FP64.Half);
            FP64 angle = FPQuaternion.Angle(a, mid);
            Assert.AreEqual(45f, angle.ToFloat(), 1f);
        }

        [Test]
        public void Slerp_ShortestPath()
        {
            var a = FPQuaternion.Identity;
            // A sign-flipped quaternion represents the same rotation
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var bNeg = new FPQuaternion(-b.x, -b.y, -b.z, -b.w);

            var result1 = FPQuaternion.Slerp(a, b, FP64.Half);
            var result2 = FPQuaternion.Slerp(a, bNeg, FP64.Half);

            // Both must produce the same rotation (sign may differ)
            var v1 = result1 * FPVector3.Forward;
            var v2 = result2 * FPVector3.Forward;
            Assert.AreEqual(v1.x.ToFloat(), v2.x.ToFloat(), EPSILON);
            Assert.AreEqual(v1.y.ToFloat(), v2.y.ToFloat(), EPSILON);
            Assert.AreEqual(v1.z.ToFloat(), v2.z.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateTowards_WithinRange_ReturnsTarget()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(30f), FPVector3.Up);
            var result = FPQuaternion.RotateTowards(a, b, FP64.FromFloat(90f));
            Assert.AreEqual(b.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(b.y.ToFloat(), result.y.ToFloat(), EPSILON);
            Assert.AreEqual(b.z.ToFloat(), result.z.ToFloat(), EPSILON);
            Assert.AreEqual(b.w.ToFloat(), result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void RotateTowards_LimitedDelta()
        {
            var a = FPQuaternion.Identity;
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            var result = FPQuaternion.RotateTowards(a, b, FP64.FromFloat(45f));
            FP64 angle = FPQuaternion.Angle(a, result);
            Assert.AreEqual(45f, angle.ToFloat(), 1f);
        }

        #endregion

        #region Euler Angles Extraction

        [Test]
        public void EulerAngles_Identity_IsZero()
        {
            var euler = FPQuaternion.Identity.eulerAngles;
            Assert.AreEqual(0f, euler.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, euler.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, euler.z.ToFloat(), EPSILON);
        }

        [Test]
        public void EulerAngles_90Y_Roundtrip()
        {
            var q = FPQuaternion.Euler(FP64.Zero, FP64.FromFloat(90f), FP64.Zero);
            var euler = q.eulerAngles;
            Assert.AreEqual(0f, euler.x.ToFloat(), 1f);
            Assert.AreEqual(90f, euler.y.ToFloat(), 1f);
            Assert.AreEqual(0f, euler.z.ToFloat(), 1f);
        }

        [Test]
        public void EulerAngles_PositiveRange()
        {
            var q = FPQuaternion.Euler(FP64.FromFloat(-45f), FP64.Zero, FP64.Zero);
            var euler = q.eulerAngles;
            Assert.AreEqual(315f, euler.x.ToFloat(), 1f);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameComponents_IsTrue()
        {
            var a = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_DifferentComponents_IsFalse()
        {
            var a = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            var b = FPQuaternion.AngleAxis(FP64.FromFloat(90f), FPVector3.Up);
            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void HamiltonProduct_Determinism()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = FPQuaternion.Euler(FP64.FromFloat(i * 3.7f), FP64.FromFloat(i * 2.3f), FP64.FromFloat(i * 1.1f));
                var b = FPQuaternion.Euler(FP64.FromFloat(i * 1.3f), FP64.FromFloat(i * 4.1f), FP64.FromFloat(i * 2.7f));

                var result1 = a * b;
                var result2 = a * b;

                Assert.AreEqual(result1.x.RawValue, result2.x.RawValue);
                Assert.AreEqual(result1.y.RawValue, result2.y.RawValue);
                Assert.AreEqual(result1.z.RawValue, result2.z.RawValue);
                Assert.AreEqual(result1.w.RawValue, result2.w.RawValue);
            }
        }

        [Test]
        public void VectorRotation_Determinism()
        {
            for (int i = 0; i < 100; i++)
            {
                var q = FPQuaternion.Euler(FP64.FromFloat(i * 3.7f), FP64.FromFloat(i * 2.3f), FP64.FromFloat(i * 1.1f));
                var v = new FPVector3(FP64.FromFloat(i * 0.1f), FP64.FromFloat(i * 0.2f), FP64.FromFloat(i * 0.3f));

                var result1 = q * v;
                var result2 = q * v;

                Assert.AreEqual(result1.x.RawValue, result2.x.RawValue);
                Assert.AreEqual(result1.y.RawValue, result2.y.RawValue);
                Assert.AreEqual(result1.z.RawValue, result2.z.RawValue);
            }
        }

        [Test]
        public void Slerp_Determinism()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = FPQuaternion.Euler(FP64.FromFloat(i * 1.7f), FP64.FromFloat(i * 2.3f), FP64.Zero);
                var b = FPQuaternion.Euler(FP64.FromFloat(i * 3.1f), FP64.FromFloat(i * 0.7f), FP64.Zero);
                var t = FP64.FromFloat(0.3f);

                var result1 = FPQuaternion.Slerp(a, b, t);
                var result2 = FPQuaternion.Slerp(a, b, t);

                Assert.AreEqual(result1.x.RawValue, result2.x.RawValue);
                Assert.AreEqual(result1.y.RawValue, result2.y.RawValue);
                Assert.AreEqual(result1.z.RawValue, result2.z.RawValue);
                Assert.AreEqual(result1.w.RawValue, result2.w.RawValue);
            }
        }

        #endregion

        #region Static Utility Methods

        [Test]
        public void Normalize_ReturnsUnitQuaternion()
        {
            var q = new FPQuaternion(FP64.FromFloat(0f), FP64.FromFloat(0f), FP64.FromFloat(0f), FP64.FromFloat(3f));
            var n = FPQuaternion.Normalize(q);
            Assert.AreEqual(1f, n.magnitude.ToFloat(), EPSILON);
            Assert.AreEqual(1f, n.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Normalize_Zero_ReturnsIdentity()
        {
            var q = new FPQuaternion(FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero);
            var n = FPQuaternion.Normalize(q);
            Assert.AreEqual(FPQuaternion.Identity, n);
        }

        [Test]
        public void IsIdentity_Identity_ReturnsTrue()
        {
            Assert.IsTrue(FPQuaternion.IsIdentity(FPQuaternion.Identity));
        }

        [Test]
        public void IsIdentity_NonIdentity_ReturnsFalse()
        {
            var q = FPQuaternion.AngleAxis(FP64.FromFloat(45f), FPVector3.Up);
            Assert.IsFalse(FPQuaternion.IsIdentity(q));
        }

        [Test]
        public void IsZero_Zero_ReturnsTrue()
        {
            var q = new FPQuaternion(FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero);
            Assert.IsTrue(FPQuaternion.IsZero(q));
        }

        [Test]
        public void IsZero_Identity_ReturnsFalse()
        {
            Assert.IsFalse(FPQuaternion.IsZero(FPQuaternion.Identity));
        }

        #endregion

        #region Overflow Protection

        [Test]
        public void Operations_WithLargeValues_DoNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                var q = new FPQuaternion(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);
                var _ = q.sqrMagnitude;
            });

            Assert.DoesNotThrow(() =>
            {
                var q = new FPQuaternion(FP64.MaxValue, FP64.MaxValue, FP64.MaxValue, FP64.MaxValue);
                var _ = q.normalized;
            });
        }

        #endregion
    }
}
