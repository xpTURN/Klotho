using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPColliderTests
    {
        private const float EPSILON = 0.01f;

        #region Factory

        [Test]
        public void FromBox_SetsTypeAndShape()
        {
            var box = new FPBoxShape(new FPVector3(1, 2, 3), new FPVector3(4, 5, 6));
            var collider = FPCollider.FromBox(box);

            Assert.AreEqual(ShapeType.Box, collider.type);
            Assert.AreEqual(1.0f, collider.box.halfExtents.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, collider.box.halfExtents.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, collider.box.halfExtents.z.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, collider.box.position.x.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, collider.box.position.y.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, collider.box.position.z.ToFloat(), EPSILON);
        }

        [Test]
        public void FromSphere_SetsTypeAndShape()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(1, 2, 3));
            var collider = FPCollider.FromSphere(sphere);

            Assert.AreEqual(ShapeType.Sphere, collider.type);
            Assert.AreEqual(5.0f, collider.sphere.radius.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, collider.sphere.position.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, collider.sphere.position.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, collider.sphere.position.z.ToFloat(), EPSILON);
        }

        [Test]
        public void FromCapsule_SetsTypeAndShape()
        {
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(1, 2, 3));
            var collider = FPCollider.FromCapsule(capsule);

            Assert.AreEqual(ShapeType.Capsule, collider.type);
            Assert.AreEqual(3.0f, collider.capsule.halfHeight.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, collider.capsule.radius.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, collider.capsule.position.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, collider.capsule.position.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, collider.capsule.position.z.ToFloat(), EPSILON);
        }

        #endregion

        #region GetWorldBounds

        [Test]
        public void GetWorldBounds_Box_MatchesBoxBounds()
        {
            var box = new FPBoxShape(new FPVector3(2, 3, 4), new FPVector3(10, 20, 30));
            var collider = FPCollider.FromBox(box);

            var expected = box.GetWorldBounds();
            var actual = collider.GetWorldBounds();

            Assert.AreEqual(expected.center.x.RawValue, actual.center.x.RawValue);
            Assert.AreEqual(expected.center.y.RawValue, actual.center.y.RawValue);
            Assert.AreEqual(expected.center.z.RawValue, actual.center.z.RawValue);
            Assert.AreEqual(expected.extents.x.RawValue, actual.extents.x.RawValue);
            Assert.AreEqual(expected.extents.y.RawValue, actual.extents.y.RawValue);
            Assert.AreEqual(expected.extents.z.RawValue, actual.extents.z.RawValue);
        }

        [Test]
        public void GetWorldBounds_Sphere_MatchesSphereBounds()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(3.0f), new FPVector3(10, 20, 30));
            var collider = FPCollider.FromSphere(sphere);

            var expected = sphere.GetWorldBounds();
            var actual = collider.GetWorldBounds();

            Assert.AreEqual(expected.center.x.RawValue, actual.center.x.RawValue);
            Assert.AreEqual(expected.center.y.RawValue, actual.center.y.RawValue);
            Assert.AreEqual(expected.center.z.RawValue, actual.center.z.RawValue);
            Assert.AreEqual(expected.extents.x.RawValue, actual.extents.x.RawValue);
            Assert.AreEqual(expected.extents.y.RawValue, actual.extents.y.RawValue);
            Assert.AreEqual(expected.extents.z.RawValue, actual.extents.z.RawValue);
        }

        [Test]
        public void GetWorldBounds_Capsule_MatchesCapsuleBounds()
        {
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(10, 20, 30));
            var collider = FPCollider.FromCapsule(capsule);

            var expected = capsule.GetWorldBounds();
            var actual = collider.GetWorldBounds();

            Assert.AreEqual(expected.center.x.RawValue, actual.center.x.RawValue);
            Assert.AreEqual(expected.center.y.RawValue, actual.center.y.RawValue);
            Assert.AreEqual(expected.center.z.RawValue, actual.center.z.RawValue);
            Assert.AreEqual(expected.extents.x.RawValue, actual.extents.x.RawValue);
            Assert.AreEqual(expected.extents.y.RawValue, actual.extents.y.RawValue);
            Assert.AreEqual(expected.extents.z.RawValue, actual.extents.z.RawValue);
        }

        #endregion
    }
}
