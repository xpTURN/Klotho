using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPSphereTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor and Properties

        [Test]
        public void Constructor_SetsCenterAndRadius()
        {
            var s = new FPSphere(new FPVector3(1, 2, 3), FP64.FromFloat(5.0f));

            Assert.AreEqual(1.0f, s.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, s.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, s.center.z.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, s.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void Diameter_ReturnsTwiceRadius()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(3.0f));
            Assert.AreEqual(6.0f, s.diameter.ToFloat(), EPSILON);
        }

        #endregion

        #region Contains Point

        [Test]
        public void ContainsPoint_Center_ReturnsTrue()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            Assert.IsTrue(s.Contains(FPVector3.Zero));
        }

        [Test]
        public void ContainsPoint_Inside_ReturnsTrue()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            Assert.IsTrue(s.Contains(new FPVector3(3, 0, 0)));
        }

        [Test]
        public void ContainsPoint_OnSurface_ReturnsTrue()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            Assert.IsTrue(s.Contains(new FPVector3(5, 0, 0)));
        }

        [Test]
        public void ContainsPoint_Outside_ReturnsFalse()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            Assert.IsFalse(s.Contains(new FPVector3(6, 0, 0)));
        }

        #endregion

        #region Contains Sphere

        [Test]
        public void ContainsSphere_FullyInside_ReturnsTrue()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(10.0f));
            var b = new FPSphere(new FPVector3(2, 0, 0), FP64.FromFloat(3.0f));
            Assert.IsTrue(a.Contains(b));
        }

        [Test]
        public void ContainsSphere_PartiallyOutside_ReturnsFalse()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(4, 0, 0), FP64.FromFloat(3.0f));
            Assert.IsFalse(a.Contains(b));
        }

        [Test]
        public void ContainsSphere_Same_ReturnsTrue()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            Assert.IsTrue(a.Contains(b));
        }

        #endregion

        #region Intersects Sphere

        [Test]
        public void IntersectsSphere_Overlapping_ReturnsTrue()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(8, 0, 0), FP64.FromFloat(5.0f));
            Assert.IsTrue(a.Intersects(b));
        }

        [Test]
        public void IntersectsSphere_Touching_ReturnsTrue()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(10, 0, 0), FP64.FromFloat(5.0f));
            Assert.IsTrue(a.Intersects(b));
        }

        [Test]
        public void IntersectsSphere_Separated_ReturnsFalse()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(20, 0, 0), FP64.FromFloat(5.0f));
            Assert.IsFalse(a.Intersects(b));
        }

        #endregion

        #region Intersects Bounds

        [Test]
        public void IntersectsBounds_Overlapping_ReturnsTrue()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPBounds3(new FPVector3(4, 0, 0), new FPVector3(4, 4, 4));
            Assert.IsTrue(s.Intersects(b));
        }

        [Test]
        public void IntersectsBounds_Separated_ReturnsFalse()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(2.0f));
            var b = new FPBounds3(new FPVector3(10, 0, 0), new FPVector3(2, 2, 2));
            Assert.IsFalse(s.Intersects(b));
        }

        [Test]
        public void IntersectsBounds_SphereInsideBounds_ReturnsTrue()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(1.0f));
            var b = new FPBounds3(FPVector3.Zero, new FPVector3(10, 10, 10));
            Assert.IsTrue(s.Intersects(b));
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_InsidePoint_ReturnsSamePoint()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var result = s.ClosestPoint(new FPVector3(2, 0, 0));

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_OutsidePoint_ReturnsOnSurface()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var result = s.ClosestPoint(new FPVector3(10, 0, 0));

            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistance

        [Test]
        public void SqrDistance_InsidePoint_ReturnsZero()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            Assert.AreEqual(0.0f, s.SqrDistance(new FPVector3(2, 0, 0)).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_OutsidePoint_ReturnsCorrect()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            // Distance to surface = 10-5 = 5, squared = 25
            Assert.AreEqual(25.0f, s.SqrDistance(new FPVector3(10, 0, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region Encapsulate Point

        [Test]
        public void EncapsulatePoint_InsidePoint_NoChange()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var origRadius = s.radius;
            s.Encapsulate(new FPVector3(2, 0, 0));

            Assert.AreEqual(origRadius.ToFloat(), s.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void EncapsulatePoint_OutsidePoint_Expands()
        {
            var s = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            s.Encapsulate(new FPVector3(10, 0, 0));

            Assert.IsTrue(s.Contains(new FPVector3(10, 0, 0)));
            Assert.IsTrue(s.radius.ToFloat() > 5.0f);
        }

        #endregion

        #region Encapsulate Sphere

        [Test]
        public void EncapsulateSphere_FullyInside_NoChange()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(10.0f));
            var origRadius = a.radius;
            a.Encapsulate(new FPSphere(new FPVector3(1, 0, 0), FP64.FromFloat(2.0f)));

            Assert.AreEqual(origRadius.ToFloat(), a.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void EncapsulateSphere_PartiallyOutside_Expands()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(8, 0, 0), FP64.FromFloat(5.0f));
            a.Encapsulate(b);

            Assert.IsTrue(a.Contains(new FPVector3(-5, 0, 0)));
            Assert.IsTrue(a.Contains(new FPVector3(13, 0, 0)));
        }

        [Test]
        public void EncapsulateSphere_OtherLarger_BecomeOther()
        {
            var a = new FPSphere(FPVector3.Zero, FP64.FromFloat(1.0f));
            var b = new FPSphere(new FPVector3(0, 0, 0), FP64.FromFloat(10.0f));
            a.Encapsulate(b);

            Assert.AreEqual(10.0f, a.radius.ToFloat(), EPSILON);
        }

        #endregion

        #region GetBounds

        [Test]
        public void GetBounds_ReturnsCorrectAABB()
        {
            var s = new FPSphere(new FPVector3(1, 2, 3), FP64.FromFloat(5.0f));
            var b = s.GetBounds();

            Assert.AreEqual(1.0f, b.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, b.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.center.z.ToFloat(), EPSILON);
            Assert.AreEqual(-4.0f, b.min.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, b.max.x.ToFloat(), EPSILON);
        }

        #endregion

        #region CreateFromPoints

        [Test]
        public void CreateFromPoints_SinglePoint_ZeroRadius()
        {
            var s = FPSphere.CreateFromPoints(new[] { new FPVector3(3, 4, 5) });

            Assert.AreEqual(3.0f, s.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, s.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, s.center.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, s.radius.ToFloat(), EPSILON);
        }

        [Test]
        public void CreateFromPoints_ContainsAllPoints()
        {
            var points = new[]
            {
                new FPVector3(-5, 0, 0),
                new FPVector3(5, 0, 0),
                new FPVector3(0, 3, 0),
                new FPVector3(0, 0, -4)
            };
            var s = FPSphere.CreateFromPoints(points);

            for (int i = 0; i < points.Length; i++)
                Assert.IsTrue(s.Contains(points[i]), $"Point {i} not contained");
        }

        [Test]
        public void CreateFromPoints_Empty_ReturnsZero()
        {
            var s = FPSphere.CreateFromPoints(new FPVector3[0]);

            Assert.AreEqual(0.0f, s.radius.ToFloat(), EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_Same_ReturnsTrue()
        {
            var a = new FPSphere(new FPVector3(1, 2, 3), FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(1, 2, 3), FP64.FromFloat(5.0f));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_Different_ReturnsFalse()
        {
            var a = new FPSphere(new FPVector3(1, 2, 3), FP64.FromFloat(5.0f));
            var b = new FPSphere(new FPVector3(1, 2, 3), FP64.FromFloat(3.0f));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_Encapsulate_ConsistentAcrossRuns()
        {
            var s = new FPSphere(
                new FPVector3(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f), FP64.FromFloat(7.89f)),
                FP64.FromFloat(3.0f)
            );
            var point = new FPVector3(FP64.FromFloat(20.0f), FP64.FromFloat(15.0f), FP64.FromFloat(-10.0f));

            var first = s;
            first.Encapsulate(point);
            for (int i = 0; i < 100; i++)
            {
                var copy = s;
                copy.Encapsulate(point);
                Assert.AreEqual(first.center.x.RawValue, copy.center.x.RawValue);
                Assert.AreEqual(first.center.y.RawValue, copy.center.y.RawValue);
                Assert.AreEqual(first.center.z.RawValue, copy.center.z.RawValue);
                Assert.AreEqual(first.radius.RawValue, copy.radius.RawValue);
            }
        }

        #endregion
    }
}
