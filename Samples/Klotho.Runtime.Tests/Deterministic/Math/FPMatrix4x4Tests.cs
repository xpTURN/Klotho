using System;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    [TestFixture]
    public class FPMatrix4x4Tests
    {
        private const float EPSILON = 0.01f;
        private const float ROTATION_EPSILON = 0.05f;

        #region Constants

        [Test]
        public void Identity_IsCorrect()
        {
            var m = FPMatrix4x4.Identity;
            Assert.AreEqual(1.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m10.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m11.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m22.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m33.ToFloat(), EPSILON);
        }

        [Test]
        public void Zero_IsAllZeros()
        {
            var m = FPMatrix4x4.Zero;
            Assert.AreEqual(0.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m33.ToFloat(), EPSILON);
        }

        #endregion

        #region Properties

        [Test]
        public void Determinant_Identity_ReturnsOne()
        {
            Assert.AreEqual(1.0f, FPMatrix4x4.Identity.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Determinant_Scale_ReturnsProduct()
        {
            // det(Scale(2,3,4)) = 2*3*4 = 24 (when m33=1)
            var m = FPMatrix4x4.Scale(new FPVector3(2, 3, 4));
            Assert.AreEqual(24.0f, m.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Transpose_IsCorrect()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(1, 2, 3));
            var t = m.transpose;
            // In row-major, translation is in column 3 -> after transpose it is in row 3
            Assert.AreEqual(1.0f, t.m30.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, t.m31.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, t.m32.ToFloat(), EPSILON);
        }

        [Test]
        public void Trace_IsCorrect()
        {
            Assert.AreEqual(4.0f, FPMatrix4x4.Identity.trace.ToFloat(), EPSILON);
        }

        #endregion

        #region Arithmetic Operators

        [Test]
        public void Addition_WorksCorrectly()
        {
            var r = FPMatrix4x4.Identity + FPMatrix4x4.Identity;
            Assert.AreEqual(2.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m11.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m01.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = FPMatrix4x4.Identity * FP64.FromInt(3);
            var b = FPMatrix4x4.Identity;
            var r = a - b;
            Assert.AreEqual(2.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m33.ToFloat(), EPSILON);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var r = -FPMatrix4x4.Identity;
            Assert.AreEqual(-1.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, r.m33.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_WorksCorrectly()
        {
            var r = FPMatrix4x4.Identity * FP64.FromInt(5);
            Assert.AreEqual(5.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.m01.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_LeftSide_WorksCorrectly()
        {
            var r = FP64.FromInt(3) * FPMatrix4x4.Identity;
            Assert.AreEqual(3.0f, r.m22.ToFloat(), EPSILON);
        }

        #endregion

        #region Matrix Multiply

        [Test]
        public void MatrixMultiply_Identity_ReturnsSame()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(1, 2, 3));
            var r = m * FPMatrix4x4.Identity;
            Assert.AreEqual(1.0f, r.m03.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m13.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, r.m23.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixMultiply_TwoTranslations_AddUp()
        {
            var t1 = FPMatrix4x4.Translate(new FPVector3(1, 0, 0));
            var t2 = FPMatrix4x4.Translate(new FPVector3(0, 2, 0));
            var r = t1 * t2;
            Assert.AreEqual(1.0f, r.m03.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m13.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixVector4Multiply_IsCorrect()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(10, 20, 30));
            var v = new FPVector4(1, 2, 3, 1); // point (w=1)
            var r = m * v;
            Assert.AreEqual(11.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(22.0f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(33.0f, r.z.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, r.w.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixVector4Multiply_Direction_NoTranslation()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(10, 20, 30));
            var v = new FPVector4(1, 2, 3, 0); // direction (w=0)
            var r = m * v;
            Assert.AreEqual(1.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, r.z.ToFloat(), EPSILON);
        }

        #endregion

        #region TRS

        [Test]
        public void Translate_TranslatesPoint()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(5, 10, 15));
            var p = m.MultiplyPoint(new FPVector3(1, 2, 3));
            Assert.AreEqual(6.0f, p.x.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, p.y.ToFloat(), EPSILON);
            Assert.AreEqual(18.0f, p.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Rotate_RotatesPoint()
        {
            var q = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(90), FP64.Zero);
            var m = FPMatrix4x4.Rotate(q);
            var p = m.MultiplyPoint(new FPVector3(0, 0, 1));
            Assert.AreEqual(1.0f, p.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, p.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, p.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void Scale_ScalesPoint()
        {
            var m = FPMatrix4x4.Scale(new FPVector3(2, 3, 4));
            var p = m.MultiplyPoint(new FPVector3(1, 2, 3));
            Assert.AreEqual(2.0f, p.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, p.y.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, p.z.ToFloat(), EPSILON);
        }

        [Test]
        public void TRS_CombinesTransforms()
        {
            var pos = new FPVector3(10, 0, 0);
            var rot = FPQuaternion.Identity;
            var scale = new FPVector3(2, 2, 2);

            var m = FPMatrix4x4.TRS(pos, rot, scale);
            var p = m.MultiplyPoint(new FPVector3(1, 0, 0));
            // scale(1,0,0)*2 = (2,0,0), then +10 translation = (12,0,0)
            Assert.AreEqual(12.0f, p.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, p.y.ToFloat(), EPSILON);
        }

        [Test]
        public void TRS_MatchesSeparateMultiply()
        {
            var pos = new FPVector3(1, 2, 3);
            var rot = FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.Zero);
            var scale = new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(2.0f), FP64.FromFloat(0.5f));

            var trs = FPMatrix4x4.TRS(pos, rot, scale);
            var separate = FPMatrix4x4.Translate(pos) * FPMatrix4x4.Rotate(rot) * FPMatrix4x4.Scale(scale);

            var testPoint = new FPVector3(FP64.FromFloat(4.0f), FP64.FromFloat(-1.0f), FP64.FromFloat(2.5f));
            var r1 = trs.MultiplyPoint(testPoint);
            var r2 = separate.MultiplyPoint(testPoint);

            Assert.AreEqual(r2.x.ToFloat(), r1.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r2.y.ToFloat(), r1.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r2.z.ToFloat(), r1.z.ToFloat(), ROTATION_EPSILON);
        }

        #endregion

        #region MultiplyPoint / MultiplyVector

        [Test]
        public void MultiplyPoint_AppliesTranslation()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(5, 0, 0));
            var p = m.MultiplyPoint(FPVector3.Zero);
            Assert.AreEqual(5.0f, p.x.ToFloat(), EPSILON);
        }

        [Test]
        public void MultiplyVector_IgnoresTranslation()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(5, 0, 0));
            var v = m.MultiplyVector(new FPVector3(1, 0, 0));
            Assert.AreEqual(1.0f, v.x.ToFloat(), EPSILON);
        }

        [Test]
        public void MultiplyVector_AppliesScale()
        {
            var m = FPMatrix4x4.TRS(new FPVector3(100, 100, 100), FPQuaternion.Identity, new FPVector3(3, 3, 3));
            var v = m.MultiplyVector(new FPVector3(1, 0, 0));
            Assert.AreEqual(3.0f, v.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Inverse

        [Test]
        public void Inverse_Identity_ReturnsIdentity()
        {
            var inv = FPMatrix4x4.Inverse(FPMatrix4x4.Identity);
            Assert.AreEqual(FPMatrix4x4.Identity, inv);
        }

        [Test]
        public void Inverse_Translation_NegatesTranslation()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(5, 10, 15));
            var inv = FPMatrix4x4.Inverse(m);
            Assert.AreEqual(-5.0f, inv.m03.ToFloat(), EPSILON);
            Assert.AreEqual(-10.0f, inv.m13.ToFloat(), EPSILON);
            Assert.AreEqual(-15.0f, inv.m23.ToFloat(), EPSILON);
        }

        [Test]
        public void Inverse_MultipliedByOriginal_ReturnsIdentity()
        {
            var m = FPMatrix4x4.TRS(
                new FPVector3(1, 2, 3),
                FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.Zero),
                new FPVector3(FP64.FromFloat(2.0f), FP64.FromFloat(1.5f), FP64.FromFloat(3.0f)));
            var inv = FPMatrix4x4.Inverse(m);
            var result = m * inv;

            Assert.AreEqual(1.0f, result.m00.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m01.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m02.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m03.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m10.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, result.m11.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m12.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m13.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m20.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m21.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, result.m22.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m23.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m30.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m31.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, result.m32.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(1.0f, result.m33.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void Inverse_Singular_ReturnsIdentity()
        {
            var inv = FPMatrix4x4.Inverse(FPMatrix4x4.Zero);
            Assert.AreEqual(FPMatrix4x4.Identity, inv);
        }

        [Test]
        public void InverseAffine_MatchesGeneralInverse()
        {
            var m = FPMatrix4x4.TRS(
                new FPVector3(3, -2, 7),
                FPQuaternion.Euler(FP64.FromInt(20), FP64.FromInt(50), FP64.FromInt(10)),
                new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(2.0f), FP64.FromFloat(0.8f)));

            var invGeneral = FPMatrix4x4.Inverse(m);
            var invAffine = FPMatrix4x4.InverseAffine(m);

            var testPoint = new FPVector3(4, -1, 6);
            var r1 = invGeneral.MultiplyPoint(testPoint);
            var r2 = invAffine.MultiplyPoint(testPoint);

            Assert.AreEqual(r1.x.ToFloat(), r2.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r1.y.ToFloat(), r2.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r1.z.ToFloat(), r2.z.ToFloat(), ROTATION_EPSILON);
        }

        #endregion

        #region Projection

        [Test]
        public void Ortho_CenterPoint_MapsToOrigin()
        {
            var m = FPMatrix4x4.Ortho(
                -FP64.FromInt(10), FP64.FromInt(10),
                -FP64.FromInt(10), FP64.FromInt(10),
                FP64.FromInt(1), FP64.FromInt(100));

            var center = new FPVector4(FP64.Zero, FP64.Zero, FP64.FromFloat(-50.5f), FP64.One);
            var r = m * center;
            Assert.AreEqual(0.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, r.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Perspective_HasCorrectStructure()
        {
            var m = FPMatrix4x4.Perspective(FP64.FromInt(60), FP64.FromFloat(1.5f), FP64.FromFloat(0.1f), FP64.FromInt(100));
            // m30 must be 0, m31 must be 0, m32 must be -1, m33 must be 0
            Assert.AreEqual(0.0f, m.m30.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m31.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, m.m32.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m33.ToFloat(), EPSILON);
        }

        [Test]
        public void LookAt_EyeAtOrigin_LookingForward()
        {
            var m = FPMatrix4x4.LookAt(FPVector3.Zero, FPVector3.Forward, FPVector3.Up);
            // Z-axis = eye - target = (0,0,0)-(0,0,1) = (0,0,-1)
            // View matrix must transform Forward to -Z in view space
            var p = m.MultiplyPoint(FPVector3.Forward);
            Assert.AreEqual(0.0f, p.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(0.0f, p.y.ToFloat(), ROTATION_EPSILON);
            Assert.IsTrue(p.z.ToFloat() < 0); // Must be negative Z
        }

        #endregion

        #region Decompose

        [Test]
        public void GetTranslation_ReturnsCorrect()
        {
            var m = FPMatrix4x4.TRS(new FPVector3(5, 10, 15), FPQuaternion.Identity, FPVector3.One);
            var t = m.GetTranslation();
            Assert.AreEqual(5.0f, t.x.ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, t.y.ToFloat(), EPSILON);
            Assert.AreEqual(15.0f, t.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetScale_ReturnsCorrect()
        {
            var m = FPMatrix4x4.TRS(FPVector3.Zero, FPQuaternion.Identity, new FPVector3(2, 3, 4));
            var s = m.GetScale();
            Assert.AreEqual(2.0f, s.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, s.y.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, s.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetScale_WithRotation_ReturnsCorrect()
        {
            var m = FPMatrix4x4.TRS(
                new FPVector3(1, 2, 3),
                FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(60), FP64.FromInt(10)),
                new FPVector3(2, 3, 4));
            var s = m.GetScale();
            Assert.AreEqual(2.0f, s.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(3.0f, s.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(4.0f, s.z.ToFloat(), ROTATION_EPSILON);
        }

        [Test]
        public void GetRotation_ReturnsCorrectRotation()
        {
            var q = FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.Zero);
            var m = FPMatrix4x4.TRS(new FPVector3(5, 10, 15), q, FPVector3.One);
            var extracted = m.GetRotation();

            // Verify by rotating a test vector
            var v = new FPVector3(1, 0, 0);
            var r1 = q * v;
            var r2 = extracted * v;
            Assert.AreEqual(r1.x.ToFloat(), r2.x.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r1.y.ToFloat(), r2.y.ToFloat(), ROTATION_EPSILON);
            Assert.AreEqual(r1.z.ToFloat(), r2.z.ToFloat(), ROTATION_EPSILON);
        }

        #endregion

        #region Row/Column Access

        [Test]
        public void GetRow_ReturnsCorrectRow()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(1, 2, 3));
            var r0 = m.GetRow(0);
            Assert.AreEqual(1.0f, r0.x.ToFloat(), EPSILON); // m00
            Assert.AreEqual(0.0f, r0.y.ToFloat(), EPSILON); // m01
            Assert.AreEqual(0.0f, r0.z.ToFloat(), EPSILON); // m02
            Assert.AreEqual(1.0f, r0.w.ToFloat(), EPSILON); // m03 = tx (translation component)
        }

        [Test]
        public void GetColumn_ReturnsCorrectColumn()
        {
            var m = FPMatrix4x4.Translate(new FPVector3(1, 2, 3));
            var c3 = m.GetColumn(3);
            Assert.AreEqual(1.0f, c3.x.ToFloat(), EPSILON); // m03
            Assert.AreEqual(2.0f, c3.y.ToFloat(), EPSILON); // m13
            Assert.AreEqual(3.0f, c3.z.ToFloat(), EPSILON); // m23
            Assert.AreEqual(1.0f, c3.w.ToFloat(), EPSILON); // m33 (homogeneous component)
        }

        [Test]
        public void GetRow_InvalidIndex_Throws()
        {
            Assert.Throws<IndexOutOfRangeException>(() => FPMatrix4x4.Identity.GetRow(4));
        }

        [Test]
        public void GetColumn_InvalidIndex_Throws()
        {
            Assert.Throws<IndexOutOfRangeException>(() => FPMatrix4x4.Identity.GetColumn(-1));
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameMatrices_ReturnsTrue()
        {
            var a = FPMatrix4x4.Identity;
            var b = FPMatrix4x4.Identity;
            Assert.IsTrue(a == b);
        }

        [Test]
        public void Equality_DifferentMatrices_ReturnsFalse()
        {
            Assert.IsTrue(FPMatrix4x4.Identity != FPMatrix4x4.Zero);
        }

        [Test]
        public void GetHashCode_SameMatrices_SameHash()
        {
            Assert.AreEqual(FPMatrix4x4.Identity.GetHashCode(), FPMatrix4x4.Identity.GetHashCode());
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_RepeatedOperations_SameResult()
        {
            for (int i = 0; i < 100; i++)
            {
                var m = FPMatrix4x4.TRS(
                    new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(-2.3f), FP64.FromFloat(0.7f)),
                    FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.FromInt(60)),
                    new FPVector3(FP64.FromFloat(1.2f), FP64.FromFloat(0.8f), FP64.FromFloat(2.0f)));

                var det = m.determinant;
                var inv = FPMatrix4x4.Inverse(m);

                var m2 = FPMatrix4x4.TRS(
                    new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(-2.3f), FP64.FromFloat(0.7f)),
                    FPQuaternion.Euler(FP64.FromInt(30), FP64.FromInt(45), FP64.FromInt(60)),
                    new FPVector3(FP64.FromFloat(1.2f), FP64.FromFloat(0.8f), FP64.FromFloat(2.0f)));

                Assert.AreEqual(m2.determinant, det);
                Assert.AreEqual(FPMatrix4x4.Inverse(m2), inv);
            }
        }

        #endregion
    }
}
