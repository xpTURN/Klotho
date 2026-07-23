using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class MeshMultiContactTests
    {
        const float EPSILON = 0.05f;

        // Stair mesh: tread (horizontal, y=0) + riser (vertical, x=1)
        // tread tri0: (0,0,0)-(2,0,0)-(2,0,2)  normal = Up
        // tread tri1: (0,0,0)-(2,0,2)-(0,0,2)  normal = Up
        // riser tri2: (1,0,0)-(1,1,0)-(1,1,2)  normal = +X
        // riser tri3: (1,0,0)-(1,1,2)-(1,0,2)  normal = +X
        static FPMeshData MakeStairMesh()
        {
            var verts = new FPVector3[]
            {
                new FPVector3(FP64.Zero,      FP64.Zero, FP64.Zero),        // 0
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),       // 1
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(2)), // 2
                new FPVector3(FP64.Zero,      FP64.Zero, FP64.FromInt(2)),  // 3
                new FPVector3(FP64.One,       FP64.Zero, FP64.Zero),        // 4
                new FPVector3(FP64.One,       FP64.One,  FP64.Zero),        // 5
                new FPVector3(FP64.One,       FP64.One,  FP64.FromInt(2)),  // 6
                new FPVector3(FP64.One,       FP64.Zero, FP64.FromInt(2)),  // 7
            };
            var indices = new int[]
            {
                0, 1, 2,
                0, 2, 3,
                4, 5, 6,
                4, 6, 7,
            };
            return new FPMeshData(verts, indices);
        }

        static FPStaticCollider MakeMeshStaticCollider(int id, FPMeshData meshData)
        {
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            return new FPStaticCollider
            {
                id       = id,
                collider = FPCollider.FromMesh(meshShape),
                meshData = meshData,
            };
        }

        // FPCapsuleShape(halfHeight, radius, position)
        static FPCapsuleShape MakeCapsule(double halfHeight, double radius, double x, double y, double z)
        {
            return new FPCapsuleShape(
                FP64.FromDouble(halfHeight),
                FP64.FromDouble(radius),
                new FPVector3(FP64.FromDouble(x), FP64.FromDouble(y), FP64.FromDouble(z)));
        }

        // ── Test 1: CapsuleMeshMulti — return multiple contacts from stair mesh ──────────────

        [Test]
        public void CapsuleMeshMulti_StairMesh_ReturnsMultipleContacts()
        {
            var meshData = MakeStairMesh();
            // activeEdgeFlags removed — null check in Multi methods handles this

            // Position straddling both tread (y=0) and riser (x=1): x=0.9, y=0.4, z=1
            var capsule = MakeCapsule(0.3, 0.5, 0.9, 0.4, 1.0);
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);

            var buffer = new FPContact[32];
            int count = CollisionTests.CapsuleMeshMulti(ref capsule, ref meshShape, meshData, buffer, 32);

            Assert.Greater(count, 1, "must return multiple contacts when touching both tread + riser");
        }

        [Test]
        public void CapsuleMeshMulti_NoOverlap_ReturnsZero()
        {
            var meshData = MakeStairMesh();
            // activeEdgeFlags removed — null check in Multi methods handles this

            var capsule = MakeCapsule(0.3, 0.5, 0.0, 10.0, 0.0);
            var meshShape = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);

            var buffer = new FPContact[32];
            int count = CollisionTests.CapsuleMeshMulti(ref capsule, ref meshShape, meshData, buffer, 32);

            Assert.AreEqual(0, count);
        }

        // ── Test 2: MergeStaticContacts — merge similar normals / preserve orthogonal normals ──────────

        [Test]
        public void MergeStaticContacts_SimilarNormals_MergedToOne()
        {
            // Mesh with only tread (2 triangles, both with Up normal)
            var verts = new FPVector3[]
            {
                new FPVector3(FP64.Zero,      FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(2)),
                new FPVector3(FP64.Zero,      FP64.Zero, FP64.FromInt(2)),
            };
            var indices = new int[] { 0, 1, 2, 0, 2, 3 };
            var meshData = new FPMeshData(verts, indices);
            // activeEdgeFlags removed — null check in Multi methods handles this

            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];

            var capsule = MakeCapsule(0.3, 0.5, 1.0, 0.45, 1.0);
            bodies[0] = new FPPhysicsBody
            {
                id        = 1,
                collider  = FPCollider.FromCapsule(capsule),
                position  = capsule.position,
                rotation  = FPQuaternion.Identity,
                rigidBody = FPRigidBody.CreateDynamic(FP64.One),
            };

            var collider = MakeMeshStaticCollider(10, meshData);
            world.LoadStaticColliders(new[] { collider }, 1);
            world.RebuildStaticBVH(bodies, 1);

            FPVector3 posBefore = bodies[0].position;
            world.Step(bodies, 1, FP64.FromDouble(1.0 / 60.0), FPVector3.Zero, null, null, null);

            float dy = (bodies[0].position.y - posBefore.y).ToFloat();
            // normal = +Y → positionA -= correction → capsule moves -Y
            // When merge works: 1 response → |dy| ≈ 0.27
            // Without merge: 2 responses → |dy| ≈ 0.54
            Assert.Less(dy, 0f, "must move down due to tread collision response");
            Assert.Greater(dy, -0.45f, "merge must work and prevent over-correction (2 responses)");
        }

        [Test]
        public void MergeStaticContacts_OrthogonalNormals_BothPreserved()
        {
            var meshData = MakeStairMesh();
            // activeEdgeFlags removed — null check in Multi methods handles this

            var world = new FPPhysicsWorld(FP64.FromInt(4));
            world.SetSkipStaticGroundResponse(false);

            var bodies = new FPPhysicsBody[1];
            // dot(Up, +X) = 0 < 0.8 → not merge candidates → respond on both contacts
            var capsule = MakeCapsule(0.3, 0.5, 0.9, 0.4, 1.0);
            bodies[0] = new FPPhysicsBody
            {
                id        = 1,
                collider  = FPCollider.FromCapsule(capsule),
                position  = capsule.position,
                rotation  = FPQuaternion.Identity,
                rigidBody = FPRigidBody.CreateDynamic(FP64.One),
            };

            var collider = MakeMeshStaticCollider(10, meshData);
            world.LoadStaticColliders(new[] { collider }, 1);
            world.RebuildStaticBVH(bodies, 1);

            FPVector3 posBefore = bodies[0].position;
            world.Step(bodies, 1, FP64.FromDouble(1.0 / 60.0), FPVector3.Zero, null, null, null);

            float dy = (bodies[0].position.y - posBefore.y).ToFloat();
            float dx = (bodies[0].position.x - posBefore.x).ToFloat();

            // normal = closestOnSeg - closestOnTri (capsule side - mesh side)
            // tread: closestOnSeg.y > closestOnTri.y → normal = +Y → positionA -= +Y*mag → capsule -Y
            // riser: closestOnSeg.x < closestOnTri.x (x=1) → normal = -X → positionA -= (-X)*mag → capsule +X
            Assert.Less(dy, 0f, "capsule must move down due to tread normal=+Y response");
            Assert.Greater(dx, 0f, "capsule must move +X due to riser normal=-X response");
        }

        // ── Test 3: SetSkipStaticGroundResponse + Multi-Contact integration ─────────────────

        [Test]
        public void SkipMeshGroundResponse_StationaryBodySkipped()
        {
            // When stationary (velocity=0), skip all ground/ceiling responses
            // riser (wall) keeps full response
            var meshData = MakeStairMesh();
            // activeEdgeFlags removed — null check in Multi methods handles this

            var world = new FPPhysicsWorld(FP64.FromInt(4));
            world.SetSkipStaticGroundResponse(true);

            var bodies = new FPPhysicsBody[1];
            var capsule = MakeCapsule(0.3, 0.5, 0.9, 0.4, 1.0);
            bodies[0] = new FPPhysicsBody
            {
                id        = 1,
                collider  = FPCollider.FromCapsule(capsule),
                position  = capsule.position,
                rotation  = FPQuaternion.Identity,
                rigidBody = FPRigidBody.CreateDynamic(FP64.One),
            };

            var collider = MakeMeshStaticCollider(10, meshData);
            world.LoadStaticColliders(new[] { collider }, 1);
            world.RebuildStaticBVH(bodies, 1);

            FPVector3 posBefore = bodies[0].position;
            world.Step(bodies, 1, FP64.FromDouble(1.0 / 60.0), FPVector3.Zero, null, null, null);

            float dy = (bodies[0].position.y - posBefore.y).ToFloat();
            float dx = (bodies[0].position.x - posBefore.x).ToFloat();

            // Stationary: ground (tread) response skipped → dy ≈ 0
            Assert.AreEqual(0f, dy, EPSILON, "ground response must be skipped when stationary");
            // riser (wall, |normal.y| < 0.5) keeps full response → dx > 0
            Assert.Greater(dx, 0f, "riser contact response must be preserved");
        }

        [Test]
        public void SkipMeshGroundResponse_False_GroundContactApplied()
        {
            var meshData = MakeStairMesh();
            // activeEdgeFlags removed — null check in Multi methods handles this

            var world = new FPPhysicsWorld(FP64.FromInt(4));
            world.SetSkipStaticGroundResponse(false);

            var bodies = new FPPhysicsBody[1];
            // Touch only tread (sufficiently far from riser)
            var capsule = MakeCapsule(0.3, 0.5, 0.3, 0.45, 1.0);
            bodies[0] = new FPPhysicsBody
            {
                id        = 1,
                collider  = FPCollider.FromCapsule(capsule),
                position  = capsule.position,
                rotation  = FPQuaternion.Identity,
                rigidBody = FPRigidBody.CreateDynamic(FP64.One),
            };

            var collider = MakeMeshStaticCollider(10, meshData);
            world.LoadStaticColliders(new[] { collider }, 1);
            world.RebuildStaticBVH(bodies, 1);

            FPVector3 posBefore = bodies[0].position;
            world.Step(bodies, 1, FP64.FromDouble(1.0 / 60.0), FPVector3.Zero, null, null, null);

            float dy = (bodies[0].position.y - posBefore.y).ToFloat();
            // normal = +Y → positionA -= correction → capsule moves -Y
            Assert.Less(dy, 0f, "ground response must occur when SetSkipStaticGroundResponse=false");
        }
    }
}
