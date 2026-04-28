using System;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    [TestFixture]
    public class FPMatrix3x3Tests
    {
        private const float EPSILON = 0.01f;
        private const float ROTATION_EPSILON = 0.05f;

        #region Constants

        [Test]
        public void Identity_IsCorrect()
        {
            var m = FPMatrix3x3.Identity;
            Assert.AreEqual(1.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m02.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m10.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m12.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m20.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m21.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void Zero_IsAllZeros()
        {
            var m = FPMatrix3x3.Zero;
            Assert.AreEqual(0.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m22.ToFloat(), EPSILON);
        }

        #endregion

        #region Properties

        [Test]
        public void Determinant_Identity_ReturnsOne()
        {
            Assert.AreEqual(1.0f, FPMatrix3x3.Identity.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Determinant_IsCorrect()
        {
            // | 1  2  3 |
            // | 4  5  6 |  det = 1(5*9-6*8) - 2(4*9-6*7) + 3(4*8-5*7)
            // | 7  8  9 |      = 1(-3) - 2(-6) + 3(-3) = -3+12-9 = 0
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            Assert.AreEqual(0.0f, m.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Determinant_NonSingular()
        {
            // | 2  1  0 |
            // | 0  3  1 |  det = 2(3*1-1*0) - 1(0*1-1*0) + 0 = 6
            // | 0  0  1 |
            var m = new FPMatrix3x3(
                FP64.FromInt(2), FP64.FromInt(1), FP64.Zero,
                FP64.Zero, FP64.FromInt(3), FP64.One,
                FP64.Zero, FP64.Zero, FP64.One);
            Assert.AreEqual(6.0f, m.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Transpose_IsCorrect()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            var t = m.transpose;
            Assert.AreEqual(1.0f, t.m00.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, t.m01.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, t.m02.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, t.m10.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, t.m11.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, t.m12.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, t.m20.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, t.m21.ToFloat(), EPSILON);
            Assert.AreEqual(9.0f, t.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void Trace_IsCorrect()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(2), FP64.Zero, FP64.Zero,
                FP64.Zero, FP64.FromInt(5), FP64.Zero,
                FP64.Zero, FP64.Zero, FP64.FromInt(8));
            Assert.AreEqual(15.0f, m.trace.ToFloat(), EPSILON);
        }

        #endregion

        #region Arithmetic Operators

        [Test]
        public void Addition_WorksCorrectly()
        {
            var a = FPMatrix3x3.Identity;
            var b = FPMatrix3x3.Identity;
            var r = a + b;
            Assert.AreEqual(2.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m11.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = FPMatrix3x3.Identity * FP64.FromInt(3);
            var b = FPMatrix3x3.Identity;
            var r = a - b;
            Assert.AreEqual(2.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m11.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var m = FPMatrix3x3.Identity;
            var r = -m;
            Assert.AreEqual(-1.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, r.m11.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, r.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_WorksCorrectly()
        {
            var m = FPMatrix3x3.Identity;
            var r = m * FP64.FromInt(5);
            Assert.AreEqual(5.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, r.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_LeftSide_WorksCorrectly()
        {
            var m = FPMatrix3x3.Identity;
            var r = FP64.FromInt(3) * m;
            Assert.AreEqual(3.0f, r.m00.ToFloat(), EPSILON);
        }

        #endregion

        #region Matrix Multiply

        [Test]
        public void MatrixMultiply_Identity_ReturnsSame()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            var r = m * FPMatrix3x3.Identity;
            Assert.AreEqual(1.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, r.m02.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, r.m11.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, r.m12.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, r.m20.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, r.m21.ToFloat(), EPSILON);
            Assert.AreEqual(9.0f, r.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixMultiply_IsCorrect()
        {
            // | 1  0  2 |   | 0  1  0 |   | 0+0+2  1+0+0  0+0+4 |   | 2  1  4 |
            // | 0  1  0 | * | 1  0  0 | = | 0+1+0  0+0+0  0+0+0 | = | 1  0  0 |
            // | 0  0  1 |   | 1  0  2 |   | 0+0+1  0+0+0  0+0+2 |   | 1  0  2 |
            var a = new FPMatrix3x3(
                FP64.One, FP64.Zero, FP64.FromInt(2),
                FP64.Zero, FP64.One, FP64.Zero,
                FP64.Zero, FP64.Zero, FP64.One);
            var b = new FPMatrix3x3(
                FP64.Zero, FP64.One, FP64.Zero,
                FP64.One, FP64.Zero, FP64.Zero,
                FP64.One, FP64.Zero, FP64.FromInt(2));
            var r = a * b;
            Assert.AreEqual(2.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m02.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m12.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, r.m20.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m21.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixVectorMultiply_IsCorrect()
        {
            // | 1  2  3 |   | 1 |   | 1+4+9  |   | 14 |
            // | 4  5  6 | * | 2 | = | 4+10+18| = | 32 |
            // | 7  8  9 |   | 3 |   | 7+16+27|   | 50 |
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            var v = new FPVector3(1, 2, 3);
            var r = m * v;
            Assert.AreEqual(14.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(32.0f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(50.0f, r.z.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixVectorMultiply_Identity_ReturnsSame()
        {
            var v = new FPVector3(FP64.FromFloat(3.5f), FP64.FromFloat(7.2f), FP64.FromFloat(-1.3f));
            var r = FPMatrix3x3.Identity * v;
            Assert.AreEqual(3.5f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(7.2f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(-1.3f, r.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Inverse

        [Test]
        public void Inverse_Identity_ReturnsIdentity()
        {
            var inv = FPMatrix3x3.Inverse(FPMatrix3x3.Identity);
            Assert.AreEqual(FPMatrix3x3.Identity, inv);
        }

        [Test]
        public void Inverse_MultipliedByOriginal_ReturnsIdentity()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(2), FP64.FromInt(1), FP64.Zero,
                FP64.Zero, FP64.FromInt(3), FP64.One,
                FP64.Zero, FP64.Zero, FP64.One);
            var inv = FPMatrix3x3.Inverse(m);
            var result = m * inv;

            Assert.AreEqual(1.0f, result.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m02.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m10.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m12.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m20.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m21.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void Inverse_Singular_ReturnsIdentity()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            var inv = FPMatrix3x3.Inverse(m);
            Assert.AreEqual(FPMatrix3x3.Identity, inv);
        }

        #endregion

        #region Rotation

        [Test]
        public void RotateX_90Degrees_RotatesVector()
        {
            var m = FPMatrix3x3.RotateX(FP64.FromInt(90));
            var v = new FPVector3(0, 1, 0);
            var r = m * v;
            Assert.AreEqual(0.0f, r.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, r.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, r.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void RotateY_90Degrees_RotatesVector()
        {
            var m = FPMatrix3x3.RotateY(FP64.FromInt(90));
            var v = new FPVector3(0, 0, 1);
            var r = m * v;
            Assert.AreEqual(1.0f, r.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, r.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, r.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void RotateZ_90Degrees_RotatesVector()
        {
            var m = FPMatrix3x3.RotateZ(FP64.FromInt(90));
            var v = new FPVector3(1, 0, 0);
            var r = m * v;
            Assert.AreEqual(0.0f, r.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, r.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, r.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void Rotation_IsOrthogonal()
        {
            var m = FPMatrix3x3.RotateY(FP64.FromInt(45));
            var mt = m.transpose;
            var result = m * mt;

            Assert.AreEqual(1.0f, result.m00.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m01.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m02.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m10.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, result.m11.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m12.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m20.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m21.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, result.m22.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void Rotation_Determinant_IsOne()
        {
            var m = FPMatrix3x3.RotateAxis(FP64.FromInt(60), new FPVector3(1, 1, 0));
            Assert.AreEqual(1.0f, m.determinant.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void RotateAxis_MatchesSpecificRotation()
        {
            // Y-axis rotation must match RotateY
            var rAxis = FPMatrix3x3.RotateAxis(FP64.FromInt(45), FPVector3.Up);
            var rY = FPMatrix3x3.RotateY(FP64.FromInt(45));

            Assert.AreEqual(rY.m00.ToFloat(), rAxis.m00.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(rY.m02.ToFloat(), rAxis.m02.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(rY.m20.ToFloat(), rAxis.m20.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(rY.m22.ToFloat(), rAxis.m22.ToFloat(), ROTATION_EPSILON);
        }

        #endregion

        #region Scale

        [Test]
        public void Scale_ScalesVector()
        {
            var m = FPMatrix3x3.Scale(FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var v = new FPVector3(1, 2, 3);
            var r = m * v;
            Assert.AreEqual(2.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, r.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Scale_FromVector_ScalesVector()
        {
            var m = FPMatrix3x3.Scale(new FPVector3(2, 3, 4));
            var v = new FPVector3(1, 2, 3);
            var r = m * v;
            Assert.AreEqual(2.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, r.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Quaternion Conversion

        [Test]
        public void FromQuaternion_Identity_ReturnsIdentity()
        {
            var m = FPMatrix3x3.FromQuaternion(FPQuaternion.Identity);
            Assert.AreEqual(1.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m02.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m10.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m12.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m20.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m21.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m22.ToFloat(), EPSILON);
        }

        [Test]
        public void FromQuaternion_RotatesVectorSameAsQuaternion()
        {
            var q = FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.FromInt(60));
            var m = FPMatrix3x3.FromQuaternion(q);

            var v = new FPVector3(1, 2, 3);
            var qResult = q * v;
            var mResult = m * v;

            Assert.AreEqual(qResult.x.ToFloat(), mResult.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(qResult.y.ToFloat(), mResult.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(qResult.z.ToFloat(), mResult.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void ToQuaternion_RoundTrip()
        {
            var q = FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.FromInt(60));
            var m = FPMatrix3x3.FromQuaternion(q);
            var q2 = m.ToQuaternion();

            // Quaternion sign may differ — verify rotation is identical
            var v = new FPVector3(1, 2, 3);
            var r1 = q * v;
            var r2 = q2 * v;

            Assert.AreEqual(r1.x.ToFloat(), r2.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r1.y.ToFloat(), r2.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r1.z.ToFloat(), r2.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void ToQuaternion_Identity_ReturnsIdentity()
        {
            var q = FPMatrix3x3.Identity.ToQuaternion();
            var v = new FPVector3(1, 2, 3);
            var r = q * v;
            Assert.AreEqual(1.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, r.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Lerp

        [Test]
        public void Lerp_AtZero_ReturnsA()
        {
            var a = FPMatrix3x3.Identity;
            var b = FPMatrix3x3.Zero;
            var r = FPMatrix3x3.Lerp(a, b, FP64.Zero);
            Assert.AreEqual(FPMatrix3x3.Identity, r);
        }

        [Test]
        public void Lerp_AtOne_ReturnsB()
        {
            var a = FPMatrix3x3.Identity;
            var b = FPMatrix3x3.Zero;
            var r = FPMatrix3x3.Lerp(a, b, FP64.One);
            Assert.AreEqual(FPMatrix3x3.Zero, r);
        }

        #endregion

        #region Row/Column Access

        [Test]
        public void GetRow_ReturnsCorrectRow()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            var r1 = m.GetRow(1);
            Assert.AreEqual(4.0f, r1.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, r1.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, r1.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetColumn_ReturnsCorrectColumn()
        {
            var m = new FPMatrix3x3(
                FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3),
                FP64.FromInt(4), FP64.FromInt(5), FP64.FromInt(6),
                FP64.FromInt(7), FP64.FromInt(8), FP64.FromInt(9));
            var c2 = m.GetColumn(2);
            Assert.AreEqual(3.0f, c2.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, c2.y.ToFloat(), EPSILON);
            Assert.AreEqual(9.0f, c2.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetRow_InvalidIndex_Throws()
        {
            Assert.Throws<IndexOutOfRangeException>(() => FPMatrix3x3.Identity.GetRow(3));
        }

        [Test]
        public void GetColumn_InvalidIndex_Throws()
        {
            Assert.Throws<IndexOutOfRangeException>(() => FPMatrix3x3.Identity.GetColumn(-1));
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameMatrices_ReturnsTrue()
        {
            var a = FPMatrix3x3.Identity;
            var b = FPMatrix3x3.Identity;
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_DifferentMatrices_ReturnsFalse()
        {
            Assert.IsFalse(FPMatrix3x3.Identity == FPMatrix3x3.Zero);
            Assert.IsTrue(FPMatrix3x3.Identity != FPMatrix3x3.Zero);
        }

        [Test]
        public void GetHashCode_SameMatrices_SameHash()
        {
            var a = FPMatrix3x3.Identity;
            var b = FPMatrix3x3.Identity;
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_RepeatedOperations_SameResult()
        {
            for (int i = 0; i < 100; i++)
            {
                var m = new FPMatrix3x3(
                    FP64.FromFloat(1.5f), FP64.FromFloat(0.3f), FP64.FromFloat(0.1f),
                    FP64.FromFloat(0.7f), FP64.FromFloat(2.1f), FP64.FromFloat(0.4f),
                    FP64.FromFloat(0.2f), FP64.FromFloat(0.8f), FP64.FromFloat(3.0f));

                var det = m.determinant;
                var inv = FPMatrix3x3.Inverse(m);
                var rot = FPMatrix3x3.RotateY(FP64.FromInt(45));

                var m2 = new FPMatrix3x3(
                    FP64.FromFloat(1.5f), FP64.FromFloat(0.3f), FP64.FromFloat(0.1f),
                    FP64.FromFloat(0.7f), FP64.FromFloat(2.1f), FP64.FromFloat(0.4f),
                    FP64.FromFloat(0.2f), FP64.FromFloat(0.8f), FP64.FromFloat(3.0f));

                Assert.AreEqual(m2.determinant, det);
                Assert.AreEqual(FPMatrix3x3.Inverse(m2), inv);
            }
        }

        #endregion
    }
}
