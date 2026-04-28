using UnityEngine;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    /// <summary>
    /// FPVector4 vector operations test
    /// </summary>
    [TestFixture]
    public class FPVector4Tests
    {
        private const float EPSILON = 0.01f;

        #region Creation and Conversion

        [Test]
        public void Constructor_FP64_CreatesCorrectVector()
        {
            var v = new FPVector4(FP64.FromFloat(1.0f), FP64.FromFloat(2.0f), FP64.FromFloat(3.0f), FP64.FromFloat(4.0f));
            Assert.AreEqual(1.0f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, v.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_Int_CreatesCorrectVector()
        {
            var v = new FPVector4(1, 2, 3, 4);
            Assert.AreEqual(1.0f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, v.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_Float_CreatesCorrectVector()
        {
            var v = new FPVector4(1.5f, 2.5f, 3.5f, 4.5f);
            Assert.AreEqual(1.5f, v.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, v.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.5f, v.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.5f, v.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_FromVector3_CreatesCorrectVector()
        {
            var v3 = new FPVector3(1, 2, 3);
            var v4 = new FPVector4(v3, FP64.FromInt(4));
            Assert.AreEqual(1.0f, v4.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v4.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v4.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, v4.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_FromVector2_CreatesCorrectVector()
        {
            var v2 = new FPVector2(1, 2);
            var v4 = new FPVector4(v2, FP64.FromInt(3), FP64.FromInt(4));
            Assert.AreEqual(1.0f, v4.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v4.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v4.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, v4.w.ToFloat(), EPSILON);
        }

        [Test]
        public void FromVector4_ConvertsCorrectly()
        {
            var unity = new Vector4(1.5f, 2.5f, 3.5f, 4.5f);
            var fp = new FPVector4();
            fp.FromVector4(unity);
            Assert.AreEqual(1.5f, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, fp.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.5f, fp.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.5f, fp.w.ToFloat(), EPSILON);
        }

        [Test]
        public void ToVector4_ConvertsCorrectly()
        {
            var fp = new FPVector4(1.5f, 2.5f, 3.5f, 4.5f);
            var unity = fp.ToVector4();
            Assert.AreEqual(1.5f, unity.x, EPSILON);
            Assert.AreEqual(2.5f, unity.y, EPSILON);
            Assert.AreEqual(3.5f, unity.z, EPSILON);
            Assert.AreEqual(4.5f, unity.w, EPSILON);
        }

        [Test]
        public void ToFPVector4_ConvertsCorrectly()
        {
            var unity = new Vector4(1.5f, 2.5f, 3.5f, 4.5f);
            var fp = unity.ToFPVector4();
            Assert.AreEqual(1.5f, fp.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.5f, fp.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.5f, fp.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.5f, fp.w.ToFloat(), EPSILON);
        }

        [Test]
        public void RoundTrip_UnityToFPAndBack()
        {
            var original = new Vector4(1.5f, -2.5f, 3.5f, -4.5f);
            var fp = original.ToFPVector4();
            var roundTrip = fp.ToVector4();
            Assert.AreEqual(original.x, roundTrip.x, EPSILON);
            Assert.AreEqual(original.y, roundTrip.y, EPSILON);
            Assert.AreEqual(original.z, roundTrip.z, EPSILON);
            Assert.AreEqual(original.w, roundTrip.w, EPSILON);
        }

        #endregion

        #region Constants

        [Test]
        public void Zero_IsAllZeros()
        {
            Assert.AreEqual(FP64.Zero, FPVector4.Zero.x);
            Assert.AreEqual(FP64.Zero, FPVector4.Zero.y);
            Assert.AreEqual(FP64.Zero, FPVector4.Zero.z);
            Assert.AreEqual(FP64.Zero, FPVector4.Zero.w);
        }

        [Test]
        public void One_IsAllOnes()
        {
            Assert.AreEqual(FP64.One, FPVector4.One.x);
            Assert.AreEqual(FP64.One, FPVector4.One.y);
            Assert.AreEqual(FP64.One, FPVector4.One.z);
            Assert.AreEqual(FP64.One, FPVector4.One.w);
        }

        #endregion

        #region Basic Operations

        [Test]
        public void Addition_WorksCorrectly()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(5, 6, 7, 8);
            var result = a + b;

            Assert.AreEqual(6.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Subtraction_WorksCorrectly()
        {
            var a = new FPVector4(5, 7, 9, 11);
            var b = new FPVector4(1, 2, 3, 4);
            var result = a - b;

            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Negation_WorksCorrectly()
        {
            var v = new FPVector4(1, -2, 3, -4);
            var result = -v;

            Assert.AreEqual(-1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_WorksCorrectly()
        {
            var v = new FPVector4(1, 2, 3, 4);
            var result = v * FP64.FromInt(3);

            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(9.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarMultiply_LeftSide_WorksCorrectly()
        {
            var v = new FPVector4(1, 2, 3, 4);
            var result = FP64.FromInt(2) * v;

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void ScalarDivide_WorksCorrectly()
        {
            var v = new FPVector4(4, 6, 8, 10);
            var result = v / FP64.FromInt(2);

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void DivideByZero_ReturnsZero()
        {
            var v = new FPVector4(1, 2, 3, 4);
            var result = v / FP64.Zero;
            Assert.AreEqual(FPVector4.Zero, result);
        }

        #endregion

        #region Magnitude and Normalization

        [Test]
        public void SqrMagnitude_IsCorrect()
        {
            var v = new FPVector4(1, 2, 3, 4);
            // 1 + 4 + 9 + 16 = 30 (sum of squares of each component)
            Assert.AreEqual(30.0f, v.sqrMagnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Magnitude_IsCorrect()
        {
            var v = new FPVector4(2, 0, 0, 0);
            Assert.AreEqual(2.0f, v.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Magnitude_UnitVector()
        {
            // (1,2,3,4) -> |v| = sqrt(30) ~= 5.477 (vector magnitude)
            var v = new FPVector4(1, 2, 3, 4);
            float expected = Mathf.Sqrt(30.0f);
            Assert.AreEqual(expected, v.magnitude.ToFloat(), 0.02f);
        }

        [Test]
        public void Magnitude_Zero_ReturnsZero()
        {
            Assert.AreEqual(0.0f, FPVector4.Zero.magnitude.ToFloat(), EPSILON);
        }

        [Test]
        public void Normalized_ReturnsUnitVector()
        {
            var v = new FPVector4(3, 0, 0, 4);
            var n = v.normalized;
            Assert.AreEqual(1.0f, n.magnitude.ToFloat(), 0.02f);
            Assert.AreEqual(0.6f, n.x.ToFloat(), 0.02f);
            Assert.AreEqual(0.0f, n.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, n.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.8f, n.w.ToFloat(), 0.02f);
        }

        [Test]
        public void Normalized_ZeroVector_ReturnsZero()
        {
            Assert.AreEqual(FPVector4.Zero, FPVector4.Zero.normalized);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameVectors_ReturnsTrue()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(1, 2, 3, 4);
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_DifferentVectors_ReturnsFalse()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(1, 2, 3, 5);
            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        [Test]
        public void Equals_Object_WorksCorrectly()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(1, 2, 3, 4);
            Assert.IsTrue(a.Equals((object)b));
            Assert.IsFalse(a.Equals("not a vector"));
        }

        [Test]
        public void GetHashCode_SameVectors_SameHash()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(1, 2, 3, 4);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region Static Methods

        [Test]
        public void Dot_OrthogonalVectors_ReturnsZero()
        {
            var a = new FPVector4(1, 0, 0, 0);
            var b = new FPVector4(0, 1, 0, 0);
            Assert.AreEqual(0.0f, FPVector4.Dot(a, b).ToFloat(), EPSILON);
        }

        [Test]
        public void Dot_ParallelVectors_ReturnsProduct()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(2, 3, 4, 5);
            // 2 + 6 + 12 + 20 = 40 (dot product result)
            Assert.AreEqual(40.0f, FPVector4.Dot(a, b).ToFloat(), EPSILON);
        }

        [Test]
        public void Distance_IsCorrect()
        {
            var a = new FPVector4(1, 0, 0, 0);
            var b = new FPVector4(4, 0, 0, 0);
            Assert.AreEqual(3.0f, FPVector4.Distance(a, b).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_IsCorrect()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(4, 6, 3, 4);
            // (3)^2 + (4)^2 + 0 + 0 = 25 (squared distance)
            Assert.AreEqual(25.0f, FPVector4.SqrDistance(a, b).ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtZero_ReturnsA()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(5, 6, 7, 8);
            var result = FPVector4.Lerp(a, b, FP64.Zero);

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtOne_ReturnsB()
        {
            var a = new FPVector4(1, 2, 3, 4);
            var b = new FPVector4(5, 6, 7, 8);
            var result = FPVector4.Lerp(a, b, FP64.One);

            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_AtHalf_ReturnsMidpoint()
        {
            var a = new FPVector4(0, 0, 0, 0);
            var b = new FPVector4(4, 6, 8, 10);
            var result = FPVector4.Lerp(a, b, FP64.Half);

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Lerp_ClampsBeyondOne()
        {
            var a = new FPVector4(0, 0, 0, 0);
            var b = new FPVector4(4, 4, 4, 4);
            var result = FPVector4.Lerp(a, b, FP64.FromFloat(2.0f));
            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void LerpUnclamped_BeyondOne_Extrapolates()
        {
            var a = new FPVector4(0, 0, 0, 0);
            var b = new FPVector4(4, 4, 4, 4);
            var result = FPVector4.LerpUnclamped(a, b, FP64.FromFloat(2.0f));
            Assert.AreEqual(8.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_ReachesTarget()
        {
            var current = new FPVector4(0, 0, 0, 0);
            var target = new FPVector4(3, 0, 0, 0);
            var result = FPVector4.MoveTowards(current, target, FP64.FromInt(5));
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void MoveTowards_PartialStep()
        {
            var current = new FPVector4(0, 0, 0, 0);
            var target = new FPVector4(10, 0, 0, 0);
            var result = FPVector4.MoveTowards(current, target, FP64.FromInt(3));
            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Project_OntoAxis()
        {
            var v = new FPVector4(3, 4, 0, 0);
            var axis = new FPVector4(1, 0, 0, 0);
            var result = FPVector4.Project(v, axis);

            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Project_OntoZero_ReturnsZero()
        {
            var v = new FPVector4(3, 4, 5, 6);
            var result = FPVector4.Project(v, FPVector4.Zero);
            Assert.AreEqual(FPVector4.Zero, result);
        }

        [Test]
        public void ClampMagnitude_WithinLimit_ReturnsOriginal()
        {
            var v = new FPVector4(1, 0, 0, 0);
            var result = FPVector4.ClampMagnitude(v, FP64.FromInt(5));
            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void ClampMagnitude_ExceedsLimit_Clamps()
        {
            var v = new FPVector4(6, 0, 0, 0);
            var result = FPVector4.ClampMagnitude(v, FP64.FromInt(3));
            Assert.AreEqual(3.0f, result.magnitude.ToFloat(), 0.02f);
        }

        [Test]
        public void Min_ReturnsComponentWiseMin()
        {
            var a = new FPVector4(1, 5, 3, 7);
            var b = new FPVector4(4, 2, 6, 1);
            var result = FPVector4.Min(a, b);

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Max_ReturnsComponentWiseMax()
        {
            var a = new FPVector4(1, 5, 3, 7);
            var b = new FPVector4(4, 2, 6, 1);
            var result = FPVector4.Max(a, b);

            Assert.AreEqual(4.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, result.w.ToFloat(), EPSILON);
        }

        [Test]
        public void Scale_ComponentWiseMultiply()
        {
            var a = new FPVector4(2, 3, 4, 5);
            var b = new FPVector4(3, 4, 5, 6);
            var result = FPVector4.Scale(a, b);

            Assert.AreEqual(6.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(12.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(20.0f, result.z.ToFloat(), EPSILON);
            Assert.AreEqual(30.0f, result.w.ToFloat(), EPSILON);
        }

        #endregion

        #region Conversion

        [Test]
        public void ToVector3_ReturnsXYZ()
        {
            var v = new FPVector4(1, 2, 3, 4);
            var v3 = v.ToVector3();
            Assert.AreEqual(1.0f, v3.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v3.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, v3.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ToVector2_ReturnsXY()
        {
            var v = new FPVector4(1, 2, 3, 4);
            var v2 = v.ToVector2();
            Assert.AreEqual(1.0f, v2.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, v2.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ToFloatArray_ReturnsCorrectArray()
        {
            var v = new FPVector4(1, 2, 3, 4);
            var arr = v.ToFloatArray();
            Assert.AreEqual(4, arr.Length);
            Assert.AreEqual(1.0f, arr[0], EPSILON);
            Assert.AreEqual(2.0f, arr[1], EPSILON);
            Assert.AreEqual(3.0f, arr[2], EPSILON);
            Assert.AreEqual(4.0f, arr[3], EPSILON);
        }

        [Test]
        public void ToString_FormatsCorrectly()
        {
            var v = new FPVector4(1, 2, 3, 4);
            string s = v.ToString();
            Assert.IsTrue(s.StartsWith("("));
            Assert.IsTrue(s.EndsWith(")"));
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_RepeatedOperations_SameResult()
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new FPVector4(FP64.FromFloat(1.23f), FP64.FromFloat(4.56f), FP64.FromFloat(7.89f), FP64.FromFloat(0.12f));
                var b = new FPVector4(FP64.FromFloat(9.87f), FP64.FromFloat(6.54f), FP64.FromFloat(3.21f), FP64.FromFloat(5.67f));

                var sum = a + b;
                var dot = FPVector4.Dot(a, b);
                var lerp = FPVector4.Lerp(a, b, FP64.Half);
                var mag = a.magnitude;
                var norm = b.normalized;

                Assert.AreEqual(new FPVector4(FP64.FromFloat(1.23f), FP64.FromFloat(4.56f), FP64.FromFloat(7.89f), FP64.FromFloat(0.12f)) + new FPVector4(FP64.FromFloat(9.87f), FP64.FromFloat(6.54f), FP64.FromFloat(3.21f), FP64.FromFloat(5.67f)), sum);
                Assert.AreEqual(FPVector4.Dot(new FPVector4(FP64.FromFloat(1.23f), FP64.FromFloat(4.56f), FP64.FromFloat(7.89f), FP64.FromFloat(0.12f)), new FPVector4(FP64.FromFloat(9.87f), FP64.FromFloat(6.54f), FP64.FromFloat(3.21f), FP64.FromFloat(5.67f))), dot);
            }
        }

        #endregion
    }
}
