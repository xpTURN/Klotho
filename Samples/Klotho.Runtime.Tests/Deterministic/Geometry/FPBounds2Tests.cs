using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPBounds2Tests
    {
        private const float EPSILON = 0.01f;

        #region Constructor and Properties

        [Test]
        public void Constructor_SizeToExtents()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 6));

            Assert.AreEqual(2.0f, b.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Size_ReturnsTwiceExtents()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 6));

            Assert.AreEqual(4.0f, b.size.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, b.size.y.ToFloat(), EPSILON);
        }

        [Test]
        public void MinMax_ReturnsCorrectValues()
        {
            var b = new FPBounds2(new FPVector2(1, 2), new FPVector2(4, 6));

            Assert.AreEqual(-1.0f, b.min.x.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, b.min.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.max.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, b.max.y.ToFloat(), EPSILON);
        }

        [Test]
        public void SetMinMax_SetsCorrectly()
        {
            var b = new FPBounds2(FPVector2.Zero, FPVector2.Zero);
            b.SetMinMax(new FPVector2(-2, -3), new FPVector2(2, 3));

            Assert.AreEqual(0.0f, b.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, b.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, b.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.y.ToFloat(), EPSILON);
        }

        [Test]
        public void SetMin_UpdatesCenterAndExtents()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            b.min = new FPVector2(-4, -4);

            Assert.AreEqual(-1.0f, b.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.x.ToFloat(), EPSILON);
        }

        [Test]
        public void SetMax_UpdatesCenterAndExtents()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            b.max = new FPVector2(4, 4);

            Assert.AreEqual(1.0f, b.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Contains

        [Test]
        public void Contains_CenterPoint_ReturnsTrue()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            Assert.IsTrue(b.Contains(new FPVector2(0, 0)));
        }

        [Test]
        public void Contains_PointInside_ReturnsTrue()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            Assert.IsTrue(b.Contains(new FPVector2(1, 1)));
        }

        [Test]
        public void Contains_PointOnEdge_ReturnsTrue()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            Assert.IsTrue(b.Contains(new FPVector2(2, 0)));
        }

        [Test]
        public void Contains_PointOutside_ReturnsFalse()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            Assert.IsFalse(b.Contains(new FPVector2(3, 0)));
        }

        #endregion

        #region Intersects

        [Test]
        public void Intersects_Overlapping_ReturnsTrue()
        {
            var a = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            var b = new FPBounds2(new FPVector2(1, 1), new FPVector2(4, 4));

            Assert.IsTrue(a.Intersects(b));
            Assert.IsTrue(b.Intersects(a));
        }

        [Test]
        public void Intersects_Touching_ReturnsTrue()
        {
            var a = new FPBounds2(new FPVector2(0, 0), new FPVector2(2, 2));
            var b = new FPBounds2(new FPVector2(2, 0), new FPVector2(2, 2));

            Assert.IsTrue(a.Intersects(b));
        }

        [Test]
        public void Intersects_Separated_ReturnsFalse()
        {
            var a = new FPBounds2(new FPVector2(0, 0), new FPVector2(2, 2));
            var b = new FPBounds2(new FPVector2(5, 0), new FPVector2(2, 2));

            Assert.IsFalse(a.Intersects(b));
        }

        #endregion

        #region Encapsulate

        [Test]
        public void EncapsulatePoint_InsidePoint_NoChange()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            var origMin = b.min;
            var origMax = b.max;
            b.Encapsulate(new FPVector2(1, 1));

            Assert.AreEqual(origMin.x.ToFloat(), b.min.x.ToFloat(), EPSILON);
            Assert.AreEqual(origMax.x.ToFloat(), b.max.x.ToFloat(), EPSILON);
        }

        [Test]
        public void EncapsulatePoint_OutsidePoint_Expands()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            b.Encapsulate(new FPVector2(5, 0));

            Assert.AreEqual(5.0f, b.max.x.ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, b.min.x.ToFloat(), EPSILON);
        }

        [Test]
        public void EncapsulateBounds_Expands()
        {
            var a = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            var b = new FPBounds2(new FPVector2(5, 5), new FPVector2(2, 2));
            a.Encapsulate(b);

            Assert.AreEqual(-2.0f, a.min.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, a.max.x.ToFloat(), EPSILON);
            Assert.AreEqual(-2.0f, a.min.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, a.max.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Expand

        [Test]
        public void Expand_Uniform()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            b.Expand(FP64.FromFloat(2.0f));

            Assert.AreEqual(3.0f, b.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.y.ToFloat(), EPSILON);
        }

        [Test]
        public void Expand_PerAxis()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            b.Expand(new FPVector2(FP64.FromFloat(2.0f), FP64.FromFloat(4.0f)));

            Assert.AreEqual(3.0f, b.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, b.extents.y.ToFloat(), EPSILON);
        }

        #endregion

        #region ClosestPoint and SqrDistance

        [Test]
        public void ClosestPoint_InsidePoint_ReturnsSamePoint()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            var result = b.ClosestPoint(new FPVector2(1, 1));

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_OutsidePoint_ClampsToSurface()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            var result = b.ClosestPoint(new FPVector2(5, 0));

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_InsidePoint_ReturnsZero()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            Assert.AreEqual(0.0f, b.SqrDistance(new FPVector2(1, 1)).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_OutsidePoint_ReturnsCorrect()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            // Closest point is (2,0); distance to (5,0) = 3, squared = 9
            Assert.AreEqual(9.0f, b.SqrDistance(new FPVector2(5, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region IntersectRay

        [Test]
        public void IntersectRay_HittingRay_ReturnsTrue()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(2, 2));
            var ray = new FPRay2(new FPVector2(-5, 0), new FPVector2(1, 0));

            Assert.IsTrue(b.IntersectRay(ray, out FP64 dist));
            Assert.AreEqual(4.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void IntersectRay_MissingRay_ReturnsFalse()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(2, 2));
            var ray = new FPRay2(new FPVector2(-5, 5), new FPVector2(1, 0));

            Assert.IsFalse(b.IntersectRay(ray, out _));
        }

        [Test]
        public void IntersectRay_RayBehind_ReturnsFalse()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(2, 2));
            var ray = new FPRay2(new FPVector2(-5, 0), new FPVector2(-1, 0));

            Assert.IsFalse(b.IntersectRay(ray, out _));
        }

        [Test]
        public void IntersectRay_OriginInside_ReturnsTrue()
        {
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));
            var ray = new FPRay2(new FPVector2(0, 0), new FPVector2(1, 0));

            Assert.IsTrue(b.IntersectRay(ray, out FP64 dist));
            Assert.IsTrue(dist.ToFloat() >= 0.0f);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameBounds_ReturnsTrue()
        {
            var a = new FPBounds2(new FPVector2(1, 2), new FPVector2(4, 4));
            var b = new FPBounds2(new FPVector2(1, 2), new FPVector2(4, 4));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_DifferentBounds_ReturnsFalse()
        {
            var a = new FPBounds2(new FPVector2(1, 2), new FPVector2(4, 4));
            var b = new FPBounds2(new FPVector2(0, 0), new FPVector2(4, 4));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_IntersectRay_ConsistentAcrossRuns()
        {
            var b = new FPBounds2(
                new FPVector2(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f)),
                new FPVector2(FP64.FromFloat(3.0f), FP64.FromFloat(5.0f))
            );
            var ray = new FPRay2(
                new FPVector2(FP64.FromFloat(-10.0f), FP64.FromFloat(-4.0f)),
                new FPVector2(FP64.FromFloat(1.0f), FP64.FromFloat(0.1f))
            );

            b.IntersectRay(ray, out FP64 firstDist);
            for (int i = 0; i < 100; i++)
            {
                b.IntersectRay(ray, out FP64 dist);
                Assert.AreEqual(firstDist.RawValue, dist.RawValue);
            }
        }

        #endregion
    }
}
