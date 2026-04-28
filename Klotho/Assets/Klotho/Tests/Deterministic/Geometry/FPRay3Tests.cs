using UnityEngine;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPRay3Tests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_SetsOriginAndDirection()
        {
            var origin = new FPVector3(1, 2, 3);
            var direction = new FPVector3(0, 0, 1);
            var ray = new FPRay3(origin, direction);

            Assert.AreEqual(1.0f, ray.origin.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, ray.origin.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, ray.origin.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, ray.direction.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, ray.direction.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, ray.direction.z.ToFloat(), EPSILON);
        }

        #endregion

        #region GetPoint

        [Test]
        public void GetPoint_ZeroDistance_ReturnsOrigin()
        {
            var ray = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 0, 1));
            var point = ray.GetPoint(FP64.Zero);

            Assert.AreEqual(1.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, point.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, point.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_PositiveDistance_ReturnsCorrectPoint()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var point = ray.GetPoint(FP64.FromFloat(5.0f));

            Assert.AreEqual(5.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, point.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, point.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_NegativeDistance_ReturnsPointBehind()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(0, 1, 0));
            var point = ray.GetPoint(FP64.FromFloat(-3.0f));

            Assert.AreEqual(0.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, point.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, point.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_NonUnitDirection_ScalesCorrectly()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(2, 0, 0));
            var point = ray.GetPoint(FP64.FromFloat(3.0f));

            Assert.AreEqual(6.0f, point.x.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_DiagonalDirection()
        {
            var ray = new FPRay3(new FPVector3(1, 1, 1), new FPVector3(1, 1, 0));
            var point = ray.GetPoint(FP64.FromFloat(2.0f));

            Assert.AreEqual(3.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, point.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, point.z.ToFloat(), EPSILON);
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_PointOnRay_ReturnsSamePoint()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var point = new FPVector3(3, 0, 0);
            var closest = ray.ClosestPoint(point);

            Assert.AreEqual(3.0f, closest.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_PointOffRay_ReturnsProjection()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var point = new FPVector3(3, 4, 0);
            var closest = ray.ClosestPoint(point);

            Assert.AreEqual(3.0f, closest.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_PointBehindOrigin_ReturnsOrigin()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var point = new FPVector3(-5, 3, 0);
            var closest = ray.ClosestPoint(point);

            Assert.AreEqual(0.0f, closest.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.z.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistanceToPoint

        [Test]
        public void SqrDistanceToPoint_PointOnRay_ReturnsZero()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var dist = ray.SqrDistanceToPoint(new FPVector3(5, 0, 0));

            Assert.AreEqual(0.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistanceToPoint_PointOffRay_ReturnsCorrectSqrDistance()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var dist = ray.SqrDistanceToPoint(new FPVector3(3, 4, 0));

            Assert.AreEqual(16.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistanceToPoint_PointBehindOrigin()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            // Closest point is the origin (0,0,0); distance to (-3,4,0) = sqrt(9+16) = 5, squared = 25
            var dist = ray.SqrDistanceToPoint(new FPVector3(-3, 4, 0));

            Assert.AreEqual(25.0f, dist.ToFloat(), EPSILON);
        }

        #endregion

        #region DistanceToPoint

        [Test]
        public void DistanceToPoint_PointOffRay()
        {
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(1, 0, 0));
            var dist = ray.DistanceToPoint(new FPVector3(3, 4, 0));

            Assert.AreEqual(4.0f, dist.ToFloat(), EPSILON);
        }

        #endregion

        #region Unity Bridge

        [Test]
        public void FromRay_ConvertsCorrectly()
        {
            var unityRay = new Ray(new Vector3(1, 2, 3), new Vector3(0, 1, 0));
            var fpRay = new FPRay3();
            fpRay.FromRay(unityRay);

            Assert.AreEqual(1.0f, fpRay.origin.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, fpRay.origin.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, fpRay.origin.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, fpRay.direction.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, fpRay.direction.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, fpRay.direction.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ToRay_ConvertsCorrectly()
        {
            var fpRay = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 0, 1));
            var unityRay = fpRay.ToRay();

            Assert.AreEqual(1.0f, unityRay.origin.x, EPSILON);
            Assert.AreEqual(2.0f, unityRay.origin.y, EPSILON);
            Assert.AreEqual(3.0f, unityRay.origin.z, EPSILON);
            Assert.AreEqual(0.0f, unityRay.direction.x, EPSILON);
            Assert.AreEqual(0.0f, unityRay.direction.y, EPSILON);
            Assert.AreEqual(1.0f, unityRay.direction.z, EPSILON);
        }

        [Test]
        public void RoundTrip_UnityToFPAndBack()
        {
            var original = new Ray(new Vector3(1.5f, -2.5f, 3.5f), new Vector3(0, 1, 0));
            var fpRay = new FPRay3();
            fpRay.FromRay(original);
            var roundTrip = fpRay.ToRay();

            Assert.AreEqual(original.origin.x, roundTrip.origin.x, EPSILON);
            Assert.AreEqual(original.origin.y, roundTrip.origin.y, EPSILON);
            Assert.AreEqual(original.origin.z, roundTrip.origin.z, EPSILON);
            Assert.AreEqual(original.direction.x, roundTrip.direction.x, EPSILON);
            Assert.AreEqual(original.direction.y, roundTrip.direction.y, EPSILON);
            Assert.AreEqual(original.direction.z, roundTrip.direction.z, EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameRays_ReturnsTrue()
        {
            var a = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0));
            var b = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.IsTrue(a.Equals(b));
        }

        [Test]
        public void Equality_DifferentOrigin_ReturnsFalse()
        {
            var a = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0));
            var b = new FPRay3(new FPVector3(4, 5, 6), new FPVector3(0, 1, 0));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        [Test]
        public void Equality_DifferentDirection_ReturnsFalse()
        {
            var a = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0));
            var b = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(1, 0, 0));

            Assert.IsFalse(a == b);
        }

        #endregion

        #region ToString

        [Test]
        public void ToString_FormatsCorrectly()
        {
            var ray = new FPRay3(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0));
            var str = ray.ToString();

            Assert.IsTrue(str.Contains("Ray"));
            Assert.IsTrue(str.Contains("origin"));
            Assert.IsTrue(str.Contains("direction"));
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_GetPoint_ConsistentAcrossRuns()
        {
            var ray = new FPRay3(
                new FPVector3(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f), FP64.FromFloat(7.89f)),
                new FPVector3(FP64.FromFloat(0.577f), FP64.FromFloat(0.577f), FP64.FromFloat(0.577f))
            );
            var dist = FP64.FromFloat(10.0f);

            var first = ray.GetPoint(dist);
            for (int i = 0; i < 100; i++)
            {
                var result = ray.GetPoint(dist);
                Assert.AreEqual(first.x.RawValue, result.x.RawValue);
                Assert.AreEqual(first.y.RawValue, result.y.RawValue);
                Assert.AreEqual(first.z.RawValue, result.z.RawValue);
            }
        }

        [Test]
        public void Determinism_ClosestPoint_ConsistentAcrossRuns()
        {
            var ray = new FPRay3(
                new FPVector3(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f), FP64.FromFloat(7.89f)),
                new FPVector3(FP64.FromFloat(0.577f), FP64.FromFloat(0.577f), FP64.FromFloat(0.577f))
            );
            var point = new FPVector3(FP64.FromFloat(3.0f), FP64.FromFloat(5.0f), FP64.FromFloat(-2.0f));

            var first = ray.ClosestPoint(point);
            for (int i = 0; i < 100; i++)
            {
                var result = ray.ClosestPoint(point);
                Assert.AreEqual(first.x.RawValue, result.x.RawValue);
                Assert.AreEqual(first.y.RawValue, result.y.RawValue);
                Assert.AreEqual(first.z.RawValue, result.z.RawValue);
            }
        }

        #endregion
    }
}
