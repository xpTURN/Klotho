using System;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    [TestFixture]
    public class FPMatrix2x2Tests
    {
        private const float EPSILON = 0.01f;

        #region Constants

        [Test]
        public void Identity_IsCorrect()
        {
            var m = FPMatrix2x2.Identity;
            Assert.AreEqual(1.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m10.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, m.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void Zero_IsAllZeros()
        {
            var m = FPMatrix2x2.Zero;
            Assert.AreEqual(0.0f, m.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m10.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, m.m11.ToFloat(), EPSILON);
        }

        #endregion

        #region Properties

        [Test]
        public void Determinant_Identity_ReturnsOne()
        {
            Assert.AreEqual(1.0f, FPMatrix2x2.Identity.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Determinant_IsCorrect()
        {
            // | 3  2 |
            // | 1  4 |  det = 3*4 - 2*1 = 10
            var m = new FPMatrix2x2(FP64.FromInt(3), FP64.FromInt(2), FP64.FromInt(1), FP64.FromInt(4));
            Assert.AreEqual(10.0f, m.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Determinant_Singular_ReturnsZero()
        {
            // | 2  4 |
            // | 1  2 |  det = 2*2 - 4*1 = 0
            var m = new FPMatrix2x2(FP64.FromInt(2), FP64.FromInt(4), FP64.FromInt(1), FP64.FromInt(2));
            Assert.AreEqual(0.0f, m.determinant.ToFloat(), EPSILON);
        }

        [Test]
        public void Transpose_IsCorrect()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var t = m.transpose;
            Assert.AreEqual(1.0f, t.m00.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, t.m01.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, t.m10.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, t.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void Trace_IsCorrect()
        {
            var m = new FPMatrix2x2(FP64.FromInt(3), FP64.FromInt(2), FP64.FromInt(1), FP64.FromInt(7));
            Assert.AreEqual(10.0f, m.trace.ToFloat(), EPSILON);
        }

        #endregion

        #region Arithmetic Operators

        [Test]
        public void Addition_WorksCorrectly()
        {
            var a = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var b = new FPMatrix2x2(FP64.FromInt(5), FP64.FromInt(6), FP64.FromInt(7), FP64.FromInt(8));
            var r = a + b;
            Assert.AreEqual(6.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, r.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = new FPMatrix2x2(FP64.FromInt(5), FP64.FromInt(6), FP64.FromInt(7), FP64.FromInt(8));
            var b = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var r = a - b;
            Assert.AreEqual(4.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(-2), FP64.FromInt(3), FP64.FromInt(-4));
            var r = -m;
            Assert.AreEqual(-1.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_WorksCorrectly()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var r = m * FP64.FromInt(3);
            Assert.AreEqual(3.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(9.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, r.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_LeftSide_WorksCorrectly()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var r = FP64.FromInt(2) * m;
            Assert.AreEqual(2.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, r.m11.ToFloat(), EPSILON);
        }

        #endregion

        #region Matrix Multiply

        [Test]
        public void MatrixMultiply_Identity_ReturnsSame()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var r = m * FPMatrix2x2.Identity;
            Assert.AreEqual(m, r);
        }

        [Test]
        public void MatrixMultiply_IsCorrect()
        {
            // | 1  2 |   | 5  6 |   | 1*5+2*7  1*6+2*8 |   | 19  22 |
            // | 3  4 | * | 7  8 | = | 3*5+4*7  3*6+4*8 | = | 43  50 |
            var a = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var b = new FPMatrix2x2(FP64.FromInt(5), FP64.FromInt(6), FP64.FromInt(7), FP64.FromInt(8));
            var r = a * b;
            Assert.AreEqual(19.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(22.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(43.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(50.0f, r.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixVectorMultiply_IsCorrect()
        {
            // | 1  2 |   | 3 |   | 1*3+2*4 |   | 11 |
            // | 5  6 | * | 4 | = | 5*3+6*4 | = | 39 |
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(5), FP64.FromInt(6));
            var v = new FPVector2(3, 4);
            var r = m * v;
            Assert.AreEqual(11.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(39.0f, r.y.ToFloat(), EPSILON);
        }

        [Test]
        public void MatrixVectorMultiply_Identity_ReturnsSame()
        {
            var v = new FPVector2(FP64.FromFloat(3.5f), FP64.FromFloat(7.2f));
            var r = FPMatrix2x2.Identity * v;
            Assert.AreEqual(3.5f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(7.2f, r.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Inverse

        [Test]
        public void Inverse_Identity_ReturnsIdentity()
        {
            var inv = FPMatrix2x2.Inverse(FPMatrix2x2.Identity);
            Assert.AreEqual(FPMatrix2x2.Identity, inv);
        }

        [Test]
        public void Inverse_MultipliedByOriginal_ReturnsIdentity()
        {
            var m = new FPMatrix2x2(FP64.FromInt(3), FP64.FromInt(2), FP64.FromInt(1), FP64.FromInt(4));
            var inv = FPMatrix2x2.Inverse(m);
            var result = m * inv;

            Assert.AreEqual(1.0f, result.m00.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m01.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.m10.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.m11.ToFloat(), EPSILON);
        }

        [Test]
        public void Inverse_Singular_ReturnsIdentity()
        {
            var m = new FPMatrix2x2(FP64.FromInt(2), FP64.FromInt(4), FP64.FromInt(1), FP64.FromInt(2));
            var inv = FPMatrix2x2.Inverse(m);
            Assert.AreEqual(FPMatrix2x2.Identity, inv);
        }

        #endregion

        #region Rotate and Scale

        [Test]
        public void Rotate_90Degrees_RotatesVector()
        {
            var m = FPMatrix2x2.Rotate(FP64.FromInt(90));
            var v = new FPVector2(1, 0);
            var r = m * v;
            Assert.AreEqual(0.0f, r.x.ToFloat(), 0.02f);
            Assert.AreEqual(1.0f, r.y.ToFloat(), 0.02f);
        }

        [Test]
        public void Rotate_180Degrees_NegatesVector()
        {
            var m = FPMatrix2x2.Rotate(FP64.FromInt(180));
            var v = new FPVector2(1, 0);
            var r = m * v;
            Assert.AreEqual(-1.0f, r.x.ToFloat(), 0.02f);
            Assert.AreEqual(0.0f, r.y.ToFloat(), 0.02f);
        }

        [Test]
        public void Rotate_IsOrthogonal()
        {
            var m = FPMatrix2x2.Rotate(FP64.FromInt(45));
            var mt = m.transpose;
            var result = m * mt;

            Assert.AreEqual(1.0f, result.m00.ToFloat(), 0.02f);
            Assert.AreEqual(0.0f, result.m01.ToFloat(), 0.02f);
            Assert.AreEqual(0.0f, result.m10.ToFloat(), 0.02f);
            Assert.AreEqual(1.0f, result.m11.ToFloat(), 0.02f);
        }

        [Test]
        public void Scale_ScalesVector()
        {
            var m = FPMatrix2x2.Scale(FP64.FromInt(2), FP64.FromInt(3));
            var v = new FPVector2(4, 5);
            var r = m * v;
            Assert.AreEqual(8.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(15.0f, r.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Scale_FromVector_ScalesVector()
        {
            var m = FPMatrix2x2.Scale(new FPVector2(2, 3));
            var v = new FPVector2(4, 5);
            var r = m * v;
            Assert.AreEqual(8.0f, r.x.ToFloat(), EPSILON);
            Assert.AreEqual(15.0f, r.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Lerp

        [Test]
        public void Lerp_AtZero_ReturnsA()
        {
            var a = FPMatrix2x2.Identity;
            var b = FPMatrix2x2.Zero;
            var r = FPMatrix2x2.Lerp(a, b, FP64.Zero);
            Assert.AreEqual(FPMatrix2x2.Identity, r);
        }

        [Test]
        public void Lerp_AtOne_ReturnsB()
        {
            var a = FPMatrix2x2.Identity;
            var b = FPMatrix2x2.Zero;
            var r = FPMatrix2x2.Lerp(a, b, FP64.One);
            Assert.AreEqual(FPMatrix2x2.Zero, r);
        }

        [Test]
        public void Lerp_AtHalf_ReturnsMidpoint()
        {
            var a = FPMatrix2x2.Zero;
            var b = new FPMatrix2x2(FP64.FromInt(2), FP64.FromInt(4), FP64.FromInt(6), FP64.FromInt(8));
            var r = FPMatrix2x2.Lerp(a, b, FP64.Half);
            Assert.AreEqual(1.0f, r.m00.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r.m01.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, r.m10.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r.m11.ToFloat(), EPSILON);
        }

        #endregion

        #region Row/Column Access

        [Test]
        public void GetRow_ReturnsCorrectRow()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var r0 = m.GetRow(0);
            var r1 = m.GetRow(1);
            Assert.AreEqual(1.0f, r0.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, r0.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, r1.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, r1.y.ToFloat(), EPSILON);
        }

        [Test]
        public void GetColumn_ReturnsCorrectColumn()
        {
            var m = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var c0 = m.GetColumn(0);
            var c1 = m.GetColumn(1);
            Assert.AreEqual(1.0f, c0.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, c0.y.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, c1.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, c1.y.ToFloat(), EPSILON);
        }

        [Test]
        public void GetRow_InvalidIndex_Throws()
        {
            Assert.Throws<IndexOutOfRangeException>(() => FPMatrix2x2.Identity.GetRow(2));
        }

        [Test]
        public void GetColumn_InvalidIndex_Throws()
        {
            Assert.Throws<IndexOutOfRangeException>(() => FPMatrix2x2.Identity.GetColumn(2));
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameMatrices_ReturnsTrue()
        {
            var a = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var b = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_DifferentMatrices_ReturnsFalse()
        {
            var a = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var b = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(5));
            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        [Test]
        public void GetHashCode_SameMatrices_SameHash()
        {
            var a = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            var b = new FPMatrix2x2(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3), FP64.FromInt(4));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_RepeatedOperations_SameResult()
        {
            for (int i = 0; i < 100; i++)
            {
                var m = new FPMatrix2x2(
                    FP64.FromFloat(1.5f), FP64.FromFloat(2.3f),
                    FP64.FromFloat(0.7f), FP64.FromFloat(4.1f));

                var det = m.determinant;
                var inv = FPMatrix2x2.Inverse(m);
                var rot = FPMatrix2x2.Rotate(FP64.FromInt(45));

                Assert.AreEqual(new FPMatrix2x2(
                    FP64.FromFloat(1.5f), FP64.FromFloat(2.3f),
                    FP64.FromFloat(0.7f), FP64.FromFloat(4.1f)).determinant, det);

                Assert.AreEqual(FPMatrix2x2.Inverse(new FPMatrix2x2(
                    FP64.FromFloat(1.5f), FP64.FromFloat(2.3f),
                    FP64.FromFloat(0.7f), FP64.FromFloat(4.1f))), inv);
            }
        }

        #endregion
    }
}
