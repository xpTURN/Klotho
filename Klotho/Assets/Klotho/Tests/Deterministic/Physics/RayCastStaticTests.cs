using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class RayCastStaticTests
    {
        private const float EPSILON = 0.05f;

        static FPStaticCollider MakeSphereCollider(int id, FPVector3 position, FP64 radius)
        {
            return new FPStaticCollider
            {
                id = id,
                collider = FPCollider.FromSphere(new FPSphereShape(radius, position)),
                meshData = null,
                isTrigger = false,
            };
        }

        static FPStaticCollider MakeBoxCollider(int id, FPVector3 position, FPVector3 halfExtents)
        {
            return new FPStaticCollider
            {
                id = id,
                collider = FPCollider.FromBox(new FPBoxShape(halfExtents, position)),
                meshData = null,
                isTrigger = false,
            };
        }

        #region BVH + Narrowphase

        [Test]
        public void RayCastStatic_SingleSphere_Hit()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[] { MakeSphereCollider(0, new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero), FP64.FromInt(2)) };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);
            bool hit = world.RayCastStatic(ray, new FPPhysicsBody[0], 0, FP64.FromInt(100),
                out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(3.0f, hitDistance.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, hitPoint.x.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, hitNormal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void RayCastStatic_Miss_ReturnsFalse()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[] { MakeSphereCollider(0, new FPVector3(FP64.FromInt(5), FP64.FromInt(5), FP64.Zero), FP64.One) };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);
            bool hit = world.RayCastStatic(ray, new FPPhysicsBody[0], 0, FP64.FromInt(100),
                out _, out _, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RayCastStatic_MultipleSpheres_ReturnsNearest()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[]
            {
                MakeSphereCollider(0, new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.FromInt(2)),
                MakeSphereCollider(1, new FPVector3(FP64.FromInt(5),  FP64.Zero, FP64.Zero), FP64.FromInt(2)),
            };
            world.LoadStaticColliders("test", colliders, 2);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);
            bool hit = world.RayCastStatic(ray, new FPPhysicsBody[0], 0, FP64.FromInt(100),
                out _, out _, out FP64 hitDistance, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(3.0f, hitDistance.ToFloat(), EPSILON);
        }

        [Test]
        public void RayCastStatic_MaxDistanceClampsResult()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[] { MakeSphereCollider(0, new FPVector3(FP64.FromInt(20), FP64.Zero, FP64.Zero), FP64.FromInt(2)) };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);
            bool hit = world.RayCastStatic(ray, new FPPhysicsBody[0], 0, FP64.FromInt(10),
                out _, out _, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RayCastStatic_EmptyWorld_ReturnsFalse()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));

            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);
            bool hit = world.RayCastStatic(ray, new FPPhysicsBody[0], 0, FP64.FromInt(100),
                out _, out _, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void RayCastStatic_BoxFloor_DownwardRayHit()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));
            var colliders = new[]
            {
                MakeBoxCollider(0, new FPVector3(FP64.Zero, -FP64.One, FP64.Zero),
                    new FPVector3(FP64.FromInt(10), FP64.One, FP64.FromInt(10))),
            };
            world.LoadStaticColliders("test", colliders, 1);
            world.RebuildStaticBVH(new FPPhysicsBody[0], 0);

            var ray = new FPRay3(new FPVector3(FP64.Zero, FP64.FromInt(5), FP64.Zero), -FPVector3.Up);
            bool hit = world.RayCastStatic(ray, new FPPhysicsBody[0], 0, FP64.FromInt(20),
                out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(5.0f, hitDistance.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, hitPoint.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, hitNormal.y.ToFloat(), EPSILON);
        }

        [Test]
        public void RayCastStatic_MixedBodyAndCollider_HitsNearest()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(10));

            var colliders = new[] { MakeSphereCollider(0, new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.FromInt(2)) };
            world.LoadStaticColliders("test", colliders, 1);

            var bodies = new[]
            {
                new FPPhysicsBody
                {
                    id = 0,
                    rigidBody = FPRigidBody.CreateStatic(),
                    collider = FPCollider.FromSphere(new FPSphereShape(FP64.FromInt(2), new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero))),
                    position = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero),
                    rotation = FPQuaternion.Identity,
                }
            };
            world.RebuildStaticBVH(bodies, 1);

            var ray = new FPRay3(FPVector3.Zero, FPVector3.Right);
            bool hit = world.RayCastStatic(ray, bodies, 1, FP64.FromInt(100),
                out _, out _, out FP64 hitDistance, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(3.0f, hitDistance.ToFloat(), EPSILON);
        }

        #endregion
    }
}
