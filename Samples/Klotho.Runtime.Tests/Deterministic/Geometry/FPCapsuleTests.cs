using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPCapsuleTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor and Properties

        [Test]
        public void Constructor_SetsFields()
        {
            var c = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(2.0f));

            Assert.AreEqual(0.0f, c.pointA.x.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, c.pointA.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, c.pointB.y.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, c.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void Center_ReturnsMidpoint()
        {
            var c = new FPCapsule(new FPVector3(0, -4, 0), new FPVector3(0, 4, 0), FP64.FromFloat(1.0f));

            Assert.AreEqual(0.0f, c.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, c.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, c.center.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Direction_ReturnsAToB()
        {
            var c = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(1.0f));

            Assert.AreEqual(0.0f, c.direction.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, c.direction.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, c.direction.z.ToFloat(), EPSILON);
        }

        [Test]
        public void SegmentLength_ReturnsCorrect()
        {
            var c = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(1.0f));
            Assert.AreEqual(6.0f, c.segmentLength.ToFloat(), EPSILON);
        }

        [Test]
        public void Height_ReturnsSegmentPlusDiameter()
        {
            var c = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(2.0f));
            // segmentLength=6, radius=2, total height = 6 + 2*2 = 10
            Assert.AreEqual(10.0f, c.height.ToFloat(), EPSILON);
        }

        #endregion

        #region Factory Methods

        [Test]
        public void FromCenterDirection_CreatesCorrectCapsule()
        {
            var c = FPCapsule.FromCenterDirection(
                new FPVector3(0, 0, 0),
                new FPVector3(0, 3, 0),
                FP64.FromFloat(1.0f)
            );

            Assert.AreEqual(0.0f, c.pointA.x.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, c.pointA.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, c.pointB.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, c.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void FromCenterAxisHeight_CreatesCorrectCapsule()
        {
            var c = FPCapsule.FromCenterAxisHeight(
                new FPVector3(0, 0, 0),
                new FPVector3(0, 1, 0),
                FP64.FromFloat(10.0f),
                FP64.FromFloat(2.0f)
            );
            // halfSeg = (10 - 2*2) / 2 = 3 (half segment length)
            Assert.AreEqual(-3.0f, c.pointA.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, c.pointB.y.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, c.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void FromCenterAxisHeight_HeightTooSmall_CollapsesToPoint()
        {
            var c = FPCapsule.FromCenterAxisHeight(
                new FPVector3(0, 0, 0),
                new FPVector3(0, 1, 0),
                FP64.FromFloat(1.0f),
                FP64.FromFloat(2.0f)
            );
            // halfSeg becomes negative so it is clamped to 0
            Assert.AreEqual(0.0f, c.pointA.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, c.pointB.y.ToFloat(), EPSILON);
        }

        #endregion

        #region ClosestPointOnSegment

        [Test]
        public void ClosestPointOnSegment_PointNearMiddle()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(1.0f));
            var result = c.ClosestPointOnSegment(new FPVector3(3, 2, 0));

            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnSegment_PointBeyondA_ClampsToA()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(1.0f));
            var result = c.ClosestPointOnSegment(new FPVector3(0, -10, 0));

            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(-5.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnSegment_PointBeyondB_ClampsToB()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(1.0f));
            var result = c.ClosestPointOnSegment(new FPVector3(0, 10, 0));

            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnSegment_DegenerateSegment_ReturnsPointA()
        {
            var c = new FPCapsule(new FPVector3(1, 2, 3), new FPVector3(1, 2, 3), FP64.FromFloat(1.0f));
            var result = c.ClosestPointOnSegment(new FPVector3(5, 5, 5));

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Contains

        [Test]
        public void Contains_PointOnAxis_ReturnsTrue()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            Assert.IsTrue(c.Contains(new FPVector3(0, 3, 0)));
        }

        [Test]
        public void Contains_PointInsideRadius_ReturnsTrue()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            Assert.IsTrue(c.Contains(new FPVector3(1, 0, 0)));
        }

        [Test]
        public void Contains_PointOutside_ReturnsFalse()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            Assert.IsFalse(c.Contains(new FPVector3(3, 0, 0)));
        }

        [Test]
        public void Contains_PointInEndCap_ReturnsTrue()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            // Point inside the top end-cap sphere
            Assert.IsTrue(c.Contains(new FPVector3(0, 6, 0)));
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_InsidePoint_ReturnsSamePoint()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(3.0f));
            var result = c.ClosestPoint(new FPVector3(1, 0, 0));

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_OutsidePoint_ReturnsOnSurface()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(3.0f));
            var result = c.ClosestPoint(new FPVector3(10, 0, 0));

            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistance

        [Test]
        public void SqrDistance_InsidePoint_ReturnsZero()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(3.0f));
            Assert.AreEqual(0.0f, c.SqrDistance(new FPVector3(1, 0, 0)).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_OutsidePoint_ReturnsCorrect()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(3.0f));
            // Closest point on the segment is (0,0,0); distance to (10,0,0) = 10, surface distance = 10-3 = 7, squared = 49
            Assert.AreEqual(49.0f, c.SqrDistance(new FPVector3(10, 0, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region Intersects Sphere

        [Test]
        public void IntersectsSphere_Overlapping_ReturnsTrue()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            var s = new FPSphere(new FPVector3(4, 0, 0), FP64.FromFloat(3.0f));
            Assert.IsTrue(c.Intersects(s));
        }

        [Test]
        public void IntersectsSphere_Separated_ReturnsFalse()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            var s = new FPSphere(new FPVector3(20, 0, 0), FP64.FromFloat(3.0f));
            Assert.IsFalse(c.Intersects(s));
        }

        [Test]
        public void IntersectsSphere_NearEndCap_ReturnsTrue()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            var s = new FPSphere(new FPVector3(0, 8, 0), FP64.FromFloat(2.0f));
            // Distance from segment endpoint (0,5,0) to sphere center (0,8,0) = 3, rSum = 4
            Assert.IsTrue(c.Intersects(s));
        }

        #endregion

        #region Intersects Capsule

        [Test]
        public void IntersectsCapsule_Parallel_Overlapping_ReturnsTrue()
        {
            var a = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            var b = new FPCapsule(new FPVector3(3, -5, 0), new FPVector3(3, 5, 0), FP64.FromFloat(2.0f));
            Assert.IsTrue(a.Intersects(b));
        }

        [Test]
        public void IntersectsCapsule_Separated_ReturnsFalse()
        {
            var a = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(1.0f));
            var b = new FPCapsule(new FPVector3(10, -5, 0), new FPVector3(10, 5, 0), FP64.FromFloat(1.0f));
            Assert.IsFalse(a.Intersects(b));
        }

        [Test]
        public void IntersectsCapsule_Perpendicular_ReturnsTrue()
        {
            var a = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(1.0f));
            var b = new FPCapsule(new FPVector3(-5, 0, 0), new FPVector3(5, 0, 0), FP64.FromFloat(1.0f));
            Assert.IsTrue(a.Intersects(b));
        }

        #endregion

        #region Intersects Bounds

        [Test]
        public void IntersectsBounds_Overlapping_ReturnsTrue()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(2.0f));
            var b = new FPBounds3(new FPVector3(2, 0, 0), new FPVector3(2, 2, 2));
            Assert.IsTrue(c.Intersects(b));
        }

        [Test]
        public void IntersectsBounds_Separated_ReturnsFalse()
        {
            var c = new FPCapsule(new FPVector3(0, -5, 0), new FPVector3(0, 5, 0), FP64.FromFloat(1.0f));
            var b = new FPBounds3(new FPVector3(10, 0, 0), new FPVector3(2, 2, 2));
            Assert.IsFalse(c.Intersects(b));
        }

        #endregion

        #region GetBounds

        [Test]
        public void GetBounds_ReturnsCorrectAABB()
        {
            var c = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(2.0f));
            var b = c.GetBounds();

            Assert.AreEqual(-2.0f, b.min.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, b.max.x.ToFloat(), EPSILON);
            Assert.AreEqual(-5.0f, b.min.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, b.max.y.ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, b.min.z.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, b.max.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_Same_ReturnsTrue()
        {
            var a = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(2.0f));
            var b = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(2.0f));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_Different_ReturnsFalse()
        {
            var a = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(2.0f));
            var b = new FPCapsule(new FPVector3(0, -3, 0), new FPVector3(0, 3, 0), FP64.FromFloat(1.0f));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_IntersectsCapsule_ConsistentAcrossRuns()
        {
            var a = new FPCapsule(
                new FPVector3(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f), FP64.FromFloat(7.89f)),
                new FPVector3(FP64.FromFloat(3.45f), FP64.FromFloat(6.78f), FP64.FromFloat(-1.23f)),
                FP64.FromFloat(2.5f)
            );
            var b = new FPCapsule(
                new FPVector3(FP64.FromFloat(-2.0f), FP64.FromFloat(1.0f), FP64.FromFloat(3.0f)),
                new FPVector3(FP64.FromFloat(5.0f), FP64.FromFloat(-3.0f), FP64.FromFloat(8.0f)),
                FP64.FromFloat(1.5f)
            );

            bool firstResult = a.Intersects(b);
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(firstResult, a.Intersects(b));
            }
        }

        #endregion
    }
}
