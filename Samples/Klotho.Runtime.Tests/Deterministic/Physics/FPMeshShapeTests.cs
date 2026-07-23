using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPMeshShapeTests
    {
        private const float EPSILON = 0.05f;

        static FPMeshData MakeFloorTriangle()
        {
            var verts = new[]
            {
                new FPVector3(-5, 0, -5),
                new FPVector3(5, 0, -5),
                new FPVector3(0, 0, 5)
            };
            var indices = new[] { 0, 1, 2 };
            return new FPMeshData(verts, indices);
        }

        static FPMeshData MakeTwoTriangleFloor()
        {
            var verts = new[]
            {
                new FPVector3(-5, 0, -5),
                new FPVector3(5, 0, -5),
                new FPVector3(5, 0, 5),
                new FPVector3(-5, 0, 5)
            };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };
            return new FPMeshData(verts, indices);
        }

        #region MeshData

        [Test]
        public void MeshData_TriangleCount()
        {
            var data = MakeTwoTriangleFloor();
            Assert.AreEqual(2, data.TriangleCount);
        }

        [Test]
        public void MeshData_GetTriangle()
        {
            var data = MakeTwoTriangleFloor();
            data.GetTriangle(1, out FPVector3 v0, out FPVector3 v1, out FPVector3 v2);

            Assert.AreEqual(-5.0f, v0.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, v0.y.ToFloat(), EPSILON);
            Assert.AreEqual(-5.0f, v0.z.ToFloat(), EPSILON);

            Assert.AreEqual(5.0f, v1.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, v1.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, v1.z.ToFloat(), EPSILON);

            Assert.AreEqual(-5.0f, v2.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, v2.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, v2.z.ToFloat(), EPSILON);
        }

        [Test]
        public void MeshData_LocalBounds()
        {
            var data = MakeTwoTriangleFloor();

            Assert.AreEqual(0.0f, data.localBounds.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, data.localBounds.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, data.localBounds.center.z.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, data.localBounds.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, data.localBounds.extents.y.ToFloat(), EPSILON);
            Assert.AreEqual(5.0f, data.localBounds.extents.z.ToFloat(), EPSILON);
        }

        #endregion

        #region GetWorldBounds

        [Test]
        public void GetWorldBounds_Identity()
        {
            var data = MakeFloorTriangle();
            var shape = new FPMeshShape(new FPVector3(10, 0, 0), FPQuaternion.Identity);

            var bounds = shape.GetWorldBounds(data);

            Assert.AreEqual(10.0f, bounds.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, bounds.center.y.ToFloat(), EPSILON);
        }

        [Test]
        public void GetWorldBounds_Rotated()
        {
            var data = MakeTwoTriangleFloor();
            var rot = FPQuaternion.AngleAxis(FP64.FromInt(90), FPVector3.Right);
            var shape = new FPMeshShape(FPVector3.Zero, rot);

            var bounds = shape.GetWorldBounds(data);

            Assert.IsTrue(bounds.extents.y.ToFloat() > 0.1f);
        }

        #endregion

        #region SphereMesh

        [Test]
        public void SphereMesh_Hit()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(0, 1, 0));

            bool hit = CollisionTests.SphereMesh(ref sphere, ref meshShape, data, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
            Assert.AreEqual(1.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereMesh_Miss()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(0, 5, 0));

            bool hit = CollisionTests.SphereMesh(ref sphere, ref meshShape, data, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region BoxMesh

        [Test]
        public void BoxMesh_Hit()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var box = new FPBoxShape(new FPVector3(1, 1, 1), new FPVector3(0, 0, 0));

            bool hit = CollisionTests.BoxMesh(ref box, ref meshShape, data, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void BoxMesh_Miss()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var box = new FPBoxShape(new FPVector3(1, 1, 1), new FPVector3(0, 5, 0));

            bool hit = CollisionTests.BoxMesh(ref box, ref meshShape, data, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region CapsuleMesh

        [Test]
        public void CapsuleMesh_Hit()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var capsule = new FPCapsuleShape(FP64.FromFloat(1.0f), FP64.FromFloat(1.0f),
                new FPVector3(0, 0, 0));

            bool hit = CollisionTests.CapsuleMesh(ref capsule, ref meshShape, data, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void CapsuleMesh_Miss()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var capsule = new FPCapsuleShape(FP64.FromFloat(1.0f), FP64.FromFloat(0.5f),
                new FPVector3(0, 5, 0));

            bool hit = CollisionTests.CapsuleMesh(ref capsule, ref meshShape, data, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region Dispatch

        [Test]
        public void Dispatch_SphereMesh()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var meshCollider = FPCollider.FromMesh(meshShape);

            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(0, 1, 0));
            var sphereCollider = FPCollider.FromSphere(sphere);

            bool hit = NarrowphaseDispatch.Test(
                ref sphereCollider, null,
                ref meshCollider, data,
                out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_BitExact()
        {
            var data = MakeFloorTriangle();
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(0, 1, 0));

            CollisionTests.SphereMesh(ref sphere, ref meshShape, data, out FPContact c1);

            var sphere2 = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(0, 1, 0));
            CollisionTests.SphereMesh(ref sphere2, ref meshShape, data, out FPContact c2);

            Assert.AreEqual(c1.point.x.RawValue, c2.point.x.RawValue);
            Assert.AreEqual(c1.point.y.RawValue, c2.point.y.RawValue);
            Assert.AreEqual(c1.point.z.RawValue, c2.point.z.RawValue);
            Assert.AreEqual(c1.normal.x.RawValue, c2.normal.x.RawValue);
            Assert.AreEqual(c1.normal.y.RawValue, c2.normal.y.RawValue);
            Assert.AreEqual(c1.normal.z.RawValue, c2.normal.z.RawValue);
            Assert.AreEqual(c1.depth.RawValue, c2.depth.RawValue);
        }

        #endregion
    }
}
