using UnityEngine;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPPlaneTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_NormalAndDistance()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.FromFloat(5.0f));

            Assert.AreEqual(0.0f, plane.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, plane.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, plane.normal.z.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, plane.distance.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_NormalAndPoint()
        {
            // y=3 plane: normal=(0,1,0) -> distance = -dot((0,1,0),(0,3,0)) = -3
            var plane = new FPPlane(new FPVector3(0, 1, 0), new FPVector3(0, 3, 0));

            Assert.AreEqual(1.0f, plane.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, plane.distance.ToFloat(), EPSILON);
        }

        #endregion

        #region Set3Points

        [Test]
        public void Set3Points_XZPlane()
        {
            // Three points on the xz plane: normal must be (0,1,0) or (0,-1,0)
            var plane = FPPlane.Set3Points(
                new FPVector3(0, 0, 0),
                new FPVector3(1, 0, 0),
                new FPVector3(0, 0, 1)
            );

            Assert.AreEqual(0.0f, plane.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, plane.normal.z.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, FP64.Abs(plane.normal.y).ToFloat(), EPSILON);
        }

        [Test]
        public void Set3Points_PointOnPlane_HasZeroDistance()
        {
            var a = new FPVector3(1, 0, 0);
            var b = new FPVector3(0, 1, 0);
            var c = new FPVector3(0, 0, 1);
            var plane = FPPlane.Set3Points(a, b, c);

            Assert.AreEqual(0.0f, plane.GetDistanceToPoint(a).ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, plane.GetDistanceToPoint(b).ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, plane.GetDistanceToPoint(c).ToFloat(), EPSILON);
        }

        #endregion

        #region GetDistanceToPoint

        [Test]
        public void GetDistanceToPoint_PointOnPlane_ReturnsZero()
        {
            // y=0 plane: normal=(0,1,0), distance=0
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);

            Assert.AreEqual(0.0f, plane.GetDistanceToPoint(new FPVector3(5, 0, 3)).ToFloat(), EPSILON);
        }

        [Test]
        public void GetDistanceToPoint_PointAbove_ReturnsPositive()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);

            Assert.AreEqual(5.0f, plane.GetDistanceToPoint(new FPVector3(0, 5, 0)).ToFloat(), EPSILON);
        }

        [Test]
        public void GetDistanceToPoint_PointBelow_ReturnsNegative()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);

            Assert.AreEqual(-3.0f, plane.GetDistanceToPoint(new FPVector3(0, -3, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region GetSide

        [Test]
        public void GetSide_PointAbove_ReturnsTrue()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            Assert.IsTrue(plane.GetSide(new FPVector3(0, 5, 0)));
        }

        [Test]
        public void GetSide_PointBelow_ReturnsFalse()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            Assert.IsFalse(plane.GetSide(new FPVector3(0, -5, 0)));
        }

        [Test]
        public void GetSide_PointOnPlane_ReturnsFalse()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            Assert.IsFalse(plane.GetSide(new FPVector3(5, 0, 3)));
        }

        #endregion

        #region SameSide

        [Test]
        public void SameSide_BothAbove_ReturnsTrue()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            Assert.IsTrue(plane.SameSide(new FPVector3(0, 1, 0), new FPVector3(0, 5, 0)));
        }

        [Test]
        public void SameSide_BothBelow_ReturnsTrue()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            Assert.IsTrue(plane.SameSide(new FPVector3(0, -1, 0), new FPVector3(0, -5, 0)));
        }

        [Test]
        public void SameSide_OppositeSides_ReturnsFalse()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            Assert.IsFalse(plane.SameSide(new FPVector3(0, 1, 0), new FPVector3(0, -1, 0)));
        }

        #endregion

        #region ClosestPointOnPlane

        [Test]
        public void ClosestPointOnPlane_PointOnPlane_ReturnsSamePoint()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var result = plane.ClosestPointOnPlane(new FPVector3(3, 0, 5));

            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnPlane_PointAbove_ProjectsDown()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var result = plane.ClosestPointOnPlane(new FPVector3(3, 7, 5));

            Assert.AreEqual(3.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnPlane_OffsetPlane()
        {
            // y=2 plane: normal=(0,1,0), point=(0,2,0) -> distance=-2
            var plane = new FPPlane(new FPVector3(0, 1, 0), new FPVector3(0, 2, 0));
            var result = plane.ClosestPointOnPlane(new FPVector3(1, 5, 1));

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, result.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Raycast

        [Test]
        public void Raycast_HittingRay_ReturnsTrue()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var ray = new FPRay3(new FPVector3(0, 5, 0), new FPVector3(0, -1, 0));

            Assert.IsTrue(plane.Raycast(ray, out FP64 enter));
            Assert.AreEqual(5.0f, enter.ToFloat(), EPSILON);
        }

        [Test]
        public void Raycast_ParallelRay_ReturnsFalse()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var ray = new FPRay3(new FPVector3(0, 5, 0), new FPVector3(1, 0, 0));

            Assert.IsFalse(plane.Raycast(ray, out _));
        }

        [Test]
        public void Raycast_RayPointingAway_ReturnsFalse()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var ray = new FPRay3(new FPVector3(0, 5, 0), new FPVector3(0, 1, 0));

            Assert.IsFalse(plane.Raycast(ray, out _));
        }

        [Test]
        public void Raycast_DiagonalRay()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var ray = new FPRay3(new FPVector3(0, 10, 0), new FPVector3(0, -1, 0));

            Assert.IsTrue(plane.Raycast(ray, out FP64 enter));
            Assert.AreEqual(10.0f, enter.ToFloat(), EPSILON);
        }

        [Test]
        public void Raycast_OriginOnPlane_ReturnsZero()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.Zero);
            var ray = new FPRay3(new FPVector3(0, 0, 0), new FPVector3(0, -1, 0));

            Assert.IsTrue(plane.Raycast(ray, out FP64 enter));
            Assert.AreEqual(0.0f, enter.ToFloat(), EPSILON);
        }

        #endregion

        #region Flipped

        [Test]
        public void Flipped_ReversesNormalAndDistance()
        {
            var plane = new FPPlane(new FPVector3(0, 1, 0), FP64.FromFloat(3.0f));
            var flipped = plane.flipped;

            Assert.AreEqual(0.0f, flipped.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, flipped.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, flipped.normal.z.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, flipped.distance.ToFloat(), EPSILON);
        }

        #endregion

        #region Unity Bridge

        [Test]
        public void FromPlane_ConvertsCorrectly()
        {
            var unity = new Plane(new Vector3(0, 1, 0), 5.0f);
            var fp = new FPPlane();
            fp.FromPlane(unity);

            Assert.AreEqual(0.0f, fp.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, fp.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, fp.normal.z.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, fp.distance.ToFloat(), EPSILON);
        }

        [Test]
        public void ToPlane_ConvertsCorrectly()
        {
            var fp = new FPPlane(new FPVector3(0, 1, 0), FP64.FromFloat(5.0f));
            var unity = fp.ToPlane();

            Assert.AreEqual(0.0f, unity.normal.x, EPSILON);
            Assert.AreEqual(1.0f, unity.normal.y, EPSILON);
            Assert.AreEqual(0.0f, unity.normal.z, EPSILON);
            Assert.AreEqual(5.0f, unity.distance, EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SamePlanes_ReturnsTrue()
        {
            var a = new FPPlane(new FPVector3(0, 1, 0), FP64.FromFloat(3.0f));
            var b = new FPPlane(new FPVector3(0, 1, 0), FP64.FromFloat(3.0f));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_DifferentPlanes_ReturnsFalse()
        {
            var a = new FPPlane(new FPVector3(0, 1, 0), FP64.FromFloat(3.0f));
            var b = new FPPlane(new FPVector3(1, 0, 0), FP64.FromFloat(3.0f));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_Raycast_ConsistentAcrossRuns()
        {
            var plane = new FPPlane(
                new FPVector3(FP64.FromFloat(0.577f), FP64.FromFloat(0.577f), FP64.FromFloat(0.577f)),
                FP64.FromFloat(-2.5f)
            );
            var ray = new FPRay3(
                new FPVector3(FP64.FromFloat(10.0f), FP64.FromFloat(10.0f), FP64.FromFloat(10.0f)),
                new FPVector3(FP64.FromFloat(-0.577f), FP64.FromFloat(-0.577f), FP64.FromFloat(-0.577f))
            );

            plane.Raycast(ray, out FP64 firstEnter);
            for (int i = 0; i < 100; i++)
            {
                plane.Raycast(ray, out FP64 enter);
                Assert.AreEqual(firstEnter.RawValue, enter.RawValue);
            }
        }

        [Test]
        public void Determinism_ClosestPoint_ConsistentAcrossRuns()
        {
            var plane = new FPPlane(
                new FPVector3(FP64.FromFloat(0.577f), FP64.FromFloat(0.577f), FP64.FromFloat(0.577f)),
                FP64.FromFloat(-2.5f)
            );
            var point = new FPVector3(FP64.FromFloat(3.0f), FP64.FromFloat(5.0f), FP64.FromFloat(-2.0f));

            var first = plane.ClosestPointOnPlane(point);
            for (int i = 0; i < 100; i++)
            {
                var result = plane.ClosestPointOnPlane(point);
                Assert.AreEqual(first.x.RawValue, result.x.RawValue);
                Assert.AreEqual(first.y.RawValue, result.y.RawValue);
                Assert.AreEqual(first.z.RawValue, result.z.RawValue);
            }
        }

        #endregion
    }
}
