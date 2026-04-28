using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPCapsuleShapeTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_SetsAllFields()
        {
            var c = new FPCapsuleShape(
                FP64.FromFloat(2.0f),
                FP64.FromFloat(0.5f),
                new FPVector3(1, 2, 3),
                FPQuaternion.Identity
            );

            Assert.AreEqual(2.0f, c.halfHeight.ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, c.radius.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, c.position.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, c.position.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, c.position.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_TwoArgs_DefaultsToIdentityRotation()
        {
            var c = new FPCapsuleShape(FP64.One, FP64.FromFloat(0.5f), FPVector3.Zero);
            Assert.AreEqual(FPQuaternion.Identity, c.rotation);
        }

        #endregion

        #region Properties

        [Test]
        public void Height_ReturnsCorrectValue()
        {
            // height = (halfHeight + radius) * 2 = (2 + 0.5) * 2 = 5
            var c = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), FPVector3.Zero);
            Assert.AreEqual(5.0f, c.height.ToFloat(), EPSILON);
        }

        #endregion

        #region GetWorldPoints

        [Test]
        public void GetWorldPoints_Identity_AlongYAxis()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            c.GetWorldPoints(out var a, out var b);

            Assert.AreEqual(0.0f, a.x.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, a.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, a.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, b.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, b.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, b.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetWorldPoints_WithOffset()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), new FPVector3(10, 20, 30));
            c.GetWorldPoints(out var a, out var b);

            Assert.AreEqual(10.0f, a.x.ToFloat(), EPSILON);
            Assert.AreEqual(18.0f, a.y.ToFloat(), EPSILON);
            Assert.AreEqual(30.0f, a.z.ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, b.x.ToFloat(), EPSILON);
            Assert.AreEqual(22.0f, b.y.ToFloat(), EPSILON);
            Assert.AreEqual(30.0f, b.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetWorldPoints_Rotated90Z_AlongXAxis()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.Zero, FP64.FromInt(90));
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero, rot);
            c.GetWorldPoints(out var a, out var b);

            // Z-axis 90 deg rotation: Y to -X
            Assert.AreEqual(3.0f, a.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, a.y.ToFloat(), EPSILON);
            Assert.AreEqual(-3.0f, b.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, b.y.ToFloat(), EPSILON);
        }

        #endregion

        #region GetWorldBounds

        [Test]
        public void GetWorldBounds_Identity_CorrectBounds()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var bounds = c.GetWorldBounds();

            // Y: +/-(3+1) = +/-4, X/Z: +/-1 (boundary value)
            Assert.AreEqual(-1.0f, bounds.min.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, bounds.max.x.ToFloat(), EPSILON);
            Assert.AreEqual(-4.0f, bounds.min.y.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, bounds.max.y.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, bounds.min.z.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, bounds.max.z.ToFloat(), EPSILON);
        }

        #endregion

        #region ToFPCapsule

        [Test]
        public void ToFPCapsule_MatchesWorldPoints()
        {
            var shape = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), new FPVector3(1, 2, 3));
            shape.GetWorldPoints(out var expectedA, out var expectedB);
            var capsule = shape.ToFPCapsule();

            Assert.AreEqual(expectedA.x.RawValue, capsule.pointA.x.RawValue);
            Assert.AreEqual(expectedA.y.RawValue, capsule.pointA.y.RawValue);
            Assert.AreEqual(expectedA.z.RawValue, capsule.pointA.z.RawValue);
            Assert.AreEqual(expectedB.x.RawValue, capsule.pointB.x.RawValue);
            Assert.AreEqual(expectedB.y.RawValue, capsule.pointB.y.RawValue);
            Assert.AreEqual(expectedB.z.RawValue, capsule.pointB.z.RawValue);
            Assert.AreEqual(shape.radius.RawValue, capsule.radius.RawValue);
        }

        #endregion

        #region Contains

        [Test]
        public void Contains_Center_ReturnsTrue()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            Assert.IsTrue(c.Contains(FPVector3.Zero));
        }

        [Test]
        public void Contains_OnAxis_ReturnsTrue()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            Assert.IsTrue(c.Contains(new FPVector3(0, 2, 0)));
        }

        [Test]
        public void Contains_Outside_ReturnsFalse()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            Assert.IsFalse(c.Contains(new FPVector3(5, 0, 0)));
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_InsidePoint_ReturnsSamePoint()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var result = c.ClosestPoint(new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero));

            Assert.AreEqual(0.5f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_OutsidePoint_ReturnsOnSurface()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var result = c.ClosestPoint(new FPVector3(10, 0, 0));

            Assert.AreEqual(1.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_NearEndpoint_ReturnsOnHemisphere()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var result = c.ClosestPoint(new FPVector3(0, 10, 0));

            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistance

        [Test]
        public void SqrDistance_InsidePoint_ReturnsZero()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            Assert.AreEqual(0.0f, c.SqrDistance(FPVector3.Zero).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_OutsidePoint_ReturnsCorrect()
        {
            var c = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            // Point (5,0,0); closest point on surface is (1,0,0), distance = 4, squared = 16
            Assert.AreEqual(16.0f, c.SqrDistance(new FPVector3(5, 0, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_Same_ReturnsTrue()
        {
            var a = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), new FPVector3(1, 2, 3));
            var b = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), new FPVector3(1, 2, 3));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_Different_ReturnsFalse()
        {
            var a = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), new FPVector3(1, 2, 3));
            var b = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(0.5f), new FPVector3(1, 2, 3));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_GetWorldPoints_ConsistentAcrossRuns()
        {
            var rot = FPQuaternion.Euler(
                FP64.FromFloat(30.0f), FP64.FromFloat(45.0f), FP64.FromFloat(60.0f)
            );
            var c = new FPCapsuleShape(
                FP64.FromFloat(2.5f), FP64.FromFloat(0.75f),
                new FPVector3(FP64.FromFloat(10.0f), FP64.FromFloat(-5.0f), FP64.FromFloat(7.0f)),
                rot
            );

            c.GetWorldPoints(out var firstA, out var firstB);
            for (int i = 0; i < 100; i++)
            {
                c.GetWorldPoints(out var a, out var b);
                Assert.AreEqual(firstA.x.RawValue, a.x.RawValue);
                Assert.AreEqual(firstA.y.RawValue, a.y.RawValue);
                Assert.AreEqual(firstA.z.RawValue, a.z.RawValue);
                Assert.AreEqual(firstB.x.RawValue, b.x.RawValue);
                Assert.AreEqual(firstB.y.RawValue, b.y.RawValue);
                Assert.AreEqual(firstB.z.RawValue, b.z.RawValue);
            }
        }

        #endregion
    }
}
