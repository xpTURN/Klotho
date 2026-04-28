using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPSphereShapeTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_SetsFields()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(1, 2, 3));

            Assert.AreEqual(5.0f, s.radius.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, s.position.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, s.position.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, s.position.z.ToFloat(), EPSILON);
        }

        #endregion

        #region GetWorldBounds

        [Test]
        public void GetWorldBounds_ReturnsCorrectAABB()
        {
            var s = new FPSphereShape(FP64.FromFloat(3.0f), new FPVector3(10, 20, 30));
            var b = s.GetWorldBounds();

            Assert.AreEqual(10.0f, b.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(20.0f, b.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(30.0f, b.center.z.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.extents.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Contains

        [Test]
        public void Contains_Center_ReturnsTrue()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            Assert.IsTrue(s.Contains(FPVector3.Zero));
        }

        [Test]
        public void Contains_Inside_ReturnsTrue()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            Assert.IsTrue(s.Contains(new FPVector3(3, 0, 0)));
        }

        [Test]
        public void Contains_Outside_ReturnsFalse()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            Assert.IsFalse(s.Contains(new FPVector3(6, 0, 0)));
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_InsidePoint_ReturnsSamePoint()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var result = s.ClosestPoint(new FPVector3(2, 0, 0));

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_OutsidePoint_ReturnsOnSurface()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var result = s.ClosestPoint(new FPVector3(10, 0, 0));

            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistance

        [Test]
        public void SqrDistance_InsidePoint_ReturnsZero()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            Assert.AreEqual(0.0f, s.SqrDistance(new FPVector3(2, 0, 0)).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_OutsidePoint_ReturnsCorrect()
        {
            var s = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            // dist from surface = 10-5 = 5, sqr = 25
            Assert.AreEqual(25.0f, s.SqrDistance(new FPVector3(10, 0, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region ToFPSphere

        [Test]
        public void ToFPSphere_MatchesProperties()
        {
            var shape = new FPSphereShape(FP64.FromFloat(3.0f), new FPVector3(1, 2, 3));
            var sphere = shape.ToFPSphere();

            Assert.AreEqual(shape.position, sphere.center);
            Assert.AreEqual(shape.radius.RawValue, sphere.radius.RawValue);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_Same_ReturnsTrue()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(1, 2, 3));
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(1, 2, 3));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_Different_ReturnsFalse()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(1, 2, 3));
            var b = new FPSphereShape(FP64.FromFloat(3.0f), new FPVector3(1, 2, 3));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion
    }
}
