using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class GroundClampRayCastTests
    {
        private const float EPSILON = 0.05f;

        static FPStaticCollider MakeBoxFloor(int id, FPVector3 position, FPVector3 halfExtents)
        {
            return new FPStaticCollider
            {
                id = id,
                collider = FPCollider.FromBox(new FPBoxShape(halfExtents, position)),
                meshData = null,
                isTrigger = false,
            };
        }

        static FPStaticCollider MakeRampMesh(int id, FP64 height)
        {
            var verts = new[]
            {
                new FPVector3(-5, 0, -5),
                new FPVector3(5, 0, -5),
                new FPVector3(5, height, 5),
                new FPVector3(-5, height, 5),
            };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };
            var meshData = new FPMeshData(verts, indices);
            var mesh = new FPMeshShape(FPVector3.Zero, FPQuaternion.Identity);
            return new FPStaticCollider
            {
                id = id,
                collider = FPCollider.FromMesh(mesh),
                meshData = meshData,
                isTrigger = false,
            };
        }

        // GroundClamp formula: groundY = hitPoint.y + halfH + r - colliderOffset.y
        static FP64 ComputeGroundY(FP64 hitPointY, FP64 halfH, FP64 r, FP64 colliderOffsetY)
        {
            return hitPointY + halfH + r - colliderOffsetY;
        }

        #region Flat Floor

        [Test]
        public void FlatFloor_GroundYMatchesCapsuleBottom()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[]
            {
                MakeBoxFloor(0, new FPVector3(FP64.Zero, -FP64.One, FP64.Zero),
                    new FPVector3(FP64.FromInt(50), FP64.One, FP64.FromInt(50))),
            };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            FP64 halfH = FP64.FromDouble(0.5);
            FP64 r = FP64.FromDouble(0.3);
            FP64 colliderOffsetY = FP64.FromDouble(0.8);
            FP64 skinOffset = FP64.FromDouble(0.1);
            FP64 maxFallProbe = FP64.FromInt(5);

            FPVector3 playerPos = new FPVector3(FP64.Zero, FP64.FromInt(3), FP64.Zero);
            FPVector3 capsuleCenter = playerPos + new FPVector3(FP64.Zero, colliderOffsetY, FP64.Zero);
            FPVector3 rayOrigin = capsuleCenter - FPVector3.Up * (halfH + r) + FPVector3.Up * skinOffset;
            FPRay3 downRay = new FPRay3(rayOrigin, -FPVector3.Up);

            bool hit = world.RayCastStatic(downRay, new FPPhysicsBody[0], 0, maxFallProbe,
                out FPVector3 groundPt, out _, out _, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, groundPt.y.ToFloat(), EPSILON);

            FP64 groundY = ComputeGroundY(groundPt.y, halfH, r, colliderOffsetY);
            Assert.AreEqual(0.0f, groundY.ToFloat(), EPSILON);
        }

        #endregion

        #region Ramp (slope)

        [Test]
        public void Ramp_GroundYInterpolated()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[] { MakeRampMesh(0, FP64.FromInt(2)) };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            FP64 halfH = FP64.FromDouble(0.5);
            FP64 r = FP64.FromDouble(0.3);
            FP64 colliderOffsetY = FP64.FromDouble(0.8);
            FP64 skinOffset = FP64.FromDouble(0.1);
            FP64 maxFallProbe = FP64.FromInt(10);

            // Position at z=0 on a ramp that goes from y=0 at z=-5 to y=2 at z=5
            // At z=0: interpolated y = 1.0
            FPVector3 playerPos = new FPVector3(FP64.Zero, FP64.FromInt(5), FP64.Zero);
            FPVector3 capsuleCenter = playerPos + new FPVector3(FP64.Zero, colliderOffsetY, FP64.Zero);
            FPVector3 rayOrigin = capsuleCenter - FPVector3.Up * (halfH + r) + FPVector3.Up * skinOffset;
            FPRay3 downRay = new FPRay3(rayOrigin, -FPVector3.Up);

            bool hit = world.RayCastStatic(downRay, new FPPhysicsBody[0], 0, maxFallProbe,
                out FPVector3 groundPt, out _, out _, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(1.0f, groundPt.y.ToFloat(), EPSILON);

            FP64 groundY = ComputeGroundY(groundPt.y, halfH, r, colliderOffsetY);
            Assert.AreEqual(1.0f, groundY.ToFloat(), EPSILON);
        }

        #endregion

        #region Step (elevation change)

        [Test]
        public void Step_TwoLevels_ClampToHigherPlatform()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[]
            {
                MakeBoxFloor(0, new FPVector3(-FP64.FromInt(5), -FP64.One, FP64.Zero),
                    new FPVector3(FP64.FromInt(5), FP64.One, FP64.FromInt(5))),
                MakeBoxFloor(1, new FPVector3(FP64.FromInt(5), FP64.One, FP64.Zero),
                    new FPVector3(FP64.FromInt(5), FP64.One, FP64.FromInt(5))),
            };
            world.LoadStaticColliders("test", colliders, 2);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            FP64 halfH = FP64.FromDouble(0.5);
            FP64 r = FP64.FromDouble(0.3);
            FP64 colliderOffsetY = FP64.FromDouble(0.8);
            FP64 skinOffset = FP64.FromDouble(0.1);
            FP64 maxFallProbe = FP64.FromInt(10);

            // Standing on the higher platform at x=5
            FPVector3 playerPos = new FPVector3(FP64.FromInt(5), FP64.FromInt(5), FP64.Zero);
            FPVector3 capsuleCenter = playerPos + new FPVector3(FP64.Zero, colliderOffsetY, FP64.Zero);
            FPVector3 rayOrigin = capsuleCenter - FPVector3.Up * (halfH + r) + FPVector3.Up * skinOffset;
            FPRay3 downRay = new FPRay3(rayOrigin, -FPVector3.Up);

            bool hit = world.RayCastStatic(downRay, new FPPhysicsBody[0], 0, maxFallProbe,
                out FPVector3 groundPt, out _, out _, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(2.0f, groundPt.y.ToFloat(), EPSILON);
        }

        #endregion

        #region No ground

        [Test]
        public void NoGround_RayMisses_ReturnsFalse()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[]
            {
                MakeBoxFloor(0, new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.One, FP64.One, FP64.One)),
            };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            FPVector3 rayOrigin = new FPVector3(FP64.Zero, FP64.FromInt(5), FP64.Zero);
            FPRay3 downRay = new FPRay3(rayOrigin, -FPVector3.Up);

            bool hit = world.RayCastStatic(downRay, new FPPhysicsBody[0], 0, FP64.FromInt(10),
                out _, out _, out _, out _);

            Assert.IsFalse(hit);
        }

        #endregion
    }
}
