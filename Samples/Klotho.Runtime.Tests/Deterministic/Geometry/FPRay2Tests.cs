using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPRay2Tests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_SetsOriginAndDirection()
        {
            var ray = new FPRay2(new FPVector2(1, 2), new FPVector2(0, 1));

            Assert.AreEqual(1.0f, ray.origin.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, ray.origin.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, ray.direction.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, ray.direction.y.ToFloat(), EPSILON);
        }

        #endregion

        #region GetPoint

        [Test]
        public void GetPoint_ZeroDistance_ReturnsOrigin()
        {
            var ray = new FPRay2(new FPVector2(1, 2), new FPVector2(1, 0));
            var point = ray.GetPoint(FP64.Zero);

            Assert.AreEqual(1.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, point.y.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_PositiveDistance_ReturnsCorrectPoint()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            var point = ray.GetPoint(FP64.FromFloat(5.0f));

            Assert.AreEqual(5.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, point.y.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_NegativeDistance_ReturnsPointBehind()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(0, 1));
            var point = ray.GetPoint(FP64.FromFloat(-3.0f));

            Assert.AreEqual(0.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, point.y.ToFloat(), EPSILON);
        }

        [Test]
        public void GetPoint_DiagonalDirection()
        {
            var ray = new FPRay2(new FPVector2(1, 1), new FPVector2(1, 1));
            var point = ray.GetPoint(FP64.FromFloat(2.0f));

            Assert.AreEqual(3.0f, point.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, point.y.ToFloat(), EPSILON);
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_PointOnRay_ReturnsSamePoint()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            var closest = ray.ClosestPoint(new FPVector2(3, 0));

            Assert.AreEqual(3.0f, closest.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_PointOffRay_ReturnsProjection()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            var closest = ray.ClosestPoint(new FPVector2(3, 4));

            Assert.AreEqual(3.0f, closest.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_PointBehindOrigin_ReturnsOrigin()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            var closest = ray.ClosestPoint(new FPVector2(-5, 3));

            Assert.AreEqual(0.0f, closest.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, closest.y.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistanceToPoint

        [Test]
        public void SqrDistanceToPoint_PointOnRay_ReturnsZero()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            var dist = ray.SqrDistanceToPoint(new FPVector2(5, 0));

            Assert.AreEqual(0.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistanceToPoint_PointOffRay()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            // Closest point is (3,0); distance to (3,4) = 4, squared = 16
            var dist = ray.SqrDistanceToPoint(new FPVector2(3, 4));

            Assert.AreEqual(16.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistanceToPoint_PointBehindOrigin()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            // Closest point is the origin (0,0); distance to (-3,4) = 5, squared = 25
            var dist = ray.SqrDistanceToPoint(new FPVector2(-3, 4));

            Assert.AreEqual(25.0f, dist.ToFloat(), EPSILON);
        }

        #endregion

        #region DistanceToPoint

        [Test]
        public void DistanceToPoint_PointOffRay()
        {
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));
            var dist = ray.DistanceToPoint(new FPVector2(3, 4));

            Assert.AreEqual(4.0f, dist.ToFloat(), EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameRays_ReturnsTrue()
        {
            var a = new FPRay2(new FPVector2(1, 2), new FPVector2(0, 1));
            var b = new FPRay2(new FPVector2(1, 2), new FPVector2(0, 1));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.IsTrue(a.Equals(b));
        }

        [Test]
        public void Equality_DifferentRays_ReturnsFalse()
        {
            var a = new FPRay2(new FPVector2(1, 2), new FPVector2(0, 1));
            var b = new FPRay2(new FPVector2(4, 5), new FPVector2(0, 1));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region ToString

        [Test]
        public void ToString_FormatsCorrectly()
        {
            var ray = new FPRay2(new FPVector2(1, 2), new FPVector2(0, 1));
            var str = ray.ToString();

            Assert.IsTrue(str.Contains("Ray2D"));
            Assert.IsTrue(str.Contains("origin"));
            Assert.IsTrue(str.Contains("direction"));
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_ClosestPoint_ConsistentAcrossRuns()
        {
            var ray = new FPRay2(
                new FPVector2(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f)),
                new FPVector2(FP64.FromFloat(0.707f), FP64.FromFloat(0.707f))
            );
            var point = new FPVector2(FP64.FromFloat(3.0f), FP64.FromFloat(5.0f));

            var first = ray.ClosestPoint(point);
            for (int i = 0; i < 100; i++)
            {
                var result = ray.ClosestPoint(point);
                Assert.AreEqual(first.x.RawValue, result.x.RawValue);
                Assert.AreEqual(first.y.RawValue, result.y.RawValue);
            }
        }

        #endregion
    }
}
