using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class RayCastTests
    {
        private const float EPSILON = 0.05f;

        #region RaySphere

        [Test]
        public void RaySphere_DirectHit_ReturnsTrueWithCorrectT()
        {
            var sphere = new FPSphereShape(FP64.FromInt(2), new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RaySphere(ray, ref sphere, out FP64 t, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(3.0f, t.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void RaySphere_Miss_ReturnsFalse()
        {
            var sphere = new FPSphereShape(FP64.One, new FPVector3(FP64.FromInt(5), FP64.FromInt(5), FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RaySphere(ray, ref sphere, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RaySphere_BehindRay_ReturnsFalse()
        {
            var sphere = new FPSphereShape(FP64.One, new FPVector3(-FP64.FromInt(5), FP64.Zero, FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RaySphere(ray, ref sphere, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RaySphere_OriginInsideSphere_ReturnsTrueWithPositiveT()
        {
            var sphere = new FPSphereShape(FP64.FromInt(5), FPVector3.Zero);
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RaySphere(ray, ref sphere, out FP64 t, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(5.0f, t.ToFloat(), EPSILON);
        }

        #endregion

        #region RayBox

        [Test]
        public void RayBox_AxisAligned_Hit()
        {
            var box = new FPBoxShape(new FPVector3(FP64.One, FP64.One, FP64.One), new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RayBox(ray, ref box, out FP64 t, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(4.0f, t.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void RayBox_Miss_ReturnsFalse()
        {
            var box = new FPBoxShape(new FPVector3(FP64.One, FP64.One, FP64.One), new FPVector3(FP64.FromInt(5), FP64.FromInt(5), FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RayBox(ray, ref box, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RayBox_Rotated45_Hit()
        {
            var rot = FPQuaternion.Euler(FPVector3.Up * FP64.FromInt(45));
            var box = new FPBoxShape(new FPVector3(FP64.One, FP64.One, FP64.One), new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero), rot);
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RayBox(ray, ref box, out FP64 t, out _);

            Assert.IsTrue(hit);
            Assert.IsTrue(t.ToFloat() > 3.0f);
            Assert.IsTrue(t.ToFloat() < 5.0f);
        }

        [Test]
        public void RayBox_DownwardOntoFloor_Hit()
        {
            var box = new FPBoxShape(
                new FPVector3(FP64.FromInt(10), FP64.One, FP64.FromInt(10)),
                new FPVector3(FP64.Zero, -FP64.One, FP64.Zero));
            var ray = new FPRay3(new FPVector3(FP64.Zero, FP64.FromInt(5), FP64.Zero), -FPVector3.Up);

            bool hit = CollisionTests.RayBox(ray, ref box, out FP64 t, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(5.0f, t.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.y.ToFloat(), EPSILON);
        }

        #endregion

        #region RayCapsule

        [Test]
        public void RayCapsule_SideHit()
        {
            var capsule = new FPCapsuleShape(
                FP64.FromInt(2), FP64.One,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RayCapsule(ray, ref capsule, out FP64 t, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(4.0f, t.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void RayCapsule_CapHit()
        {
            var capsule = new FPCapsuleShape(
                FP64.FromInt(2), FP64.One,
                new FPVector3(FP64.Zero, FP64.FromInt(5), FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Up);

            bool hit = CollisionTests.RayCapsule(ray, ref capsule, out FP64 t, out _);

            Assert.IsTrue(hit);
            Assert.IsTrue(t.ToFloat() > 1.0f);
        }

        [Test]
        public void RayCapsule_Miss_ReturnsFalse()
        {
            var capsule = new FPCapsuleShape(
                FP64.FromInt(2), FP64.One,
                new FPVector3(FP64.FromInt(5), FP64.FromInt(5), FP64.Zero));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = CollisionTests.RayCapsule(ray, ref capsule, out _, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region RayMesh

        static FPMeshData MakeFloorTriangle()
        {
            var verts = new[]
            {
                new FPVector3(-5, 0, -5),
                new FPVector3(5, 0, -5),
                new FPVector3(0, 0, 5)
            };
            return new FPMeshData(verts, new[] { 0, 1, 2 });
        }

        [Test]
        public void RayMesh_DownwardOntoFloor_Hit()
        {
            var meshData = MakeFloorTriangle();
            var mesh = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var ray = new FPRay3(new FPVector3(FP64.Zero, FP64.FromInt(3), FP64.Zero), -FPVector3.Up);

            bool hit = CollisionTests.RayMesh(ray, ref mesh, meshData, out FP64 t, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(3.0f, t.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.y.ToFloat(), EPSILON);
        }

        [Test]
        public void RayMesh_MissOutsideTriangle_ReturnsFalse()
        {
            var meshData = MakeFloorTriangle();
            var mesh = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var ray = new FPRay3(new FPVector3(FP64.FromInt(20), FP64.FromInt(3), FP64.Zero), -FPVector3.Up);

            bool hit = CollisionTests.RayMesh(ray, ref mesh, meshData, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RayMesh_ParallelToSurface_ReturnsFalse()
        {
            var meshData = MakeFloorTriangle();
            var mesh = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var ray = new FPRay3(new FPVector3(-FP64.FromInt(10), FP64.One, FP64.Zero), FPVector3.Right);

            bool hit = CollisionTests.RayMesh(ray, ref mesh, meshData, out _, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region NarrowphaseDispatch.RayCast

        [Test]
        public void Dispatch_SphereCollider_DelegatesToRaySphere()
        {
            var collider = FPCollider.FromSphere(new FPSphereShape(FP64.FromInt(2), new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero)));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = NarrowphaseDispatch.RayCast(ray, ref collider, null, out FP64 t, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(3.0f, t.ToFloat(), EPSILON);
        }

        [Test]
        public void Dispatch_BoxCollider_DelegatesToRayBox()
        {
            var collider = FPCollider.FromBox(new FPBoxShape(
                new FPVector3(FP64.One, FP64.One, FP64.One),
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero)));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = NarrowphaseDispatch.RayCast(ray, ref collider, null, out FP64 t, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(4.0f, t.ToFloat(), EPSILON);
        }

        [Test]
        public void Dispatch_CapsuleCollider_DelegatesToRayCapsule()
        {
            var collider = FPCollider.FromCapsule(new FPCapsuleShape(
                FP64.FromInt(2), FP64.One,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero)));
            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);

            bool hit = NarrowphaseDispatch.RayCast(ray, ref collider, null, out FP64 t, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(4.0f, t.ToFloat(), EPSILON);
        }

        #endregion
    }
}
