using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPPhysicsWorldTests
    {
        const float EPSILON = 0.05f;

        static FPPhysicsBody MakeDynamicSphere(int id, FPVector3 position, FP64 mass, FP64 radius)
        {
            var body = new FPPhysicsBody();
            body.id = id;
            body.rigidBody = FPRigidBody.CreateDynamic(mass);
            body.collider = FPCollider.FromSphere(new FPSphereShape(radius, position));
            body.position = position;
            body.rotation = FPQuaternion.Identity;
            return body;
        }

        static FPPhysicsBody MakeStaticSphere(int id, FPVector3 position, FP64 radius)
        {
            var body = new FPPhysicsBody();
            body.id = id;
            body.rigidBody = FPRigidBody.CreateStatic();
            body.collider = FPCollider.FromSphere(new FPSphereShape(radius, position));
            body.position = position;
            body.rotation = FPQuaternion.Identity;
            return body;
        }

        #region EmptyWorld

        [Test]
        public void EmptyWorld_NoException()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[0];
            FP64 dt = FP64.FromFloat(0.02f);

            Assert.DoesNotThrow(() =>
                world.Step(bodies, 0, dt, FPVector3.Zero, null, null, null));
        }

        #endregion

        #region Gravity

        [Test]
        public void SingleDynamicBody_GravityApplied()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.Zero, FP64.FromInt(10), FP64.Zero),
                FP64.One, FP64.One);
            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);

            world.Step(bodies, 1, dt, gravity, null, null, null);

            Assert.IsTrue(bodies[0].rigidBody.velocity.y.ToFloat() < 0f);
            Assert.IsTrue(bodies[0].position.y.ToFloat() < 10f);
        }

        [Test]
        public void StaticBody_NoMovement()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];
            bodies[0] = MakeStaticSphere(1, FPVector3.Zero, FP64.One);
            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);

            world.Step(bodies, 1, dt, gravity, null, null, null);

            Assert.AreEqual(0f, bodies[0].position.y.ToFloat(), EPSILON);
            Assert.AreEqual(FPVector3.Zero, bodies[0].rigidBody.velocity);
        }

        #endregion

        #region Collision

        [Test]
        public void TwoBodies_CollisionResolved()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            // Two spheres radius=1, distance=1 -> overlapping
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromFloat(-0.5f), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            bodies[0].rigidBody.restitution = FP64.One;

            bodies[1] = MakeDynamicSphere(2,
                new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[1].rigidBody.velocity = new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero);
            bodies[1].rigidBody.restitution = FP64.One;

            FP64 dt = FP64.FromFloat(0.02f);
            world.Step(bodies, 2, dt, FPVector3.Zero, null, null, null);

            // After collision, velocity direction should be reversed
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 0f);
            Assert.IsTrue(bodies[1].rigidBody.velocity.x.ToFloat() > 0f);
        }

        #endregion

        #region TriggerCallbacks

        [Test]
        public void TriggerCallbacks_EnterStayExit()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            FP64 dt = FP64.FromFloat(0.02f);

            // Two overlapping spheres, one is a trigger
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeStaticSphere(10, FPVector3.Zero, FP64.One);
            bodies[0].isTrigger = true;
            bodies[1] = MakeStaticSphere(20,
                new FPVector3(FP64.Half, FP64.Zero, FP64.Zero), FP64.One);

            // Step 1: Enter
            var entered = new List<(int, int)>();
            world.Step(bodies, 2, dt, FPVector3.Zero,
                (a, b) => entered.Add((a, b)), null, null);
            Assert.AreEqual(1, entered.Count);

            // Step 2: Stay
            var stayed = new List<(int, int)>();
            world.Step(bodies, 2, dt, FPVector3.Zero,
                null, (a, b) => stayed.Add((a, b)), null);
            Assert.AreEqual(1, stayed.Count);

            // Step 3: Exit — separate bodies
            bodies[1].position = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            var exited = new List<(int, int)>();
            world.Step(bodies, 2, dt, FPVector3.Zero,
                null, null, (a, b) => exited.Add((a, b)));
            Assert.AreEqual(1, exited.Count);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_BitExact()
        {
            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);

            var worldA = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesA = new FPPhysicsBody[2];
            bodiesA[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-2), FP64.Zero, FP64.Zero),
                FP64.FromInt(3), FP64.One);
            bodiesA[1] = MakeDynamicSphere(2,
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                FP64.FromInt(5), FP64.One);

            var worldB = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesB = new FPPhysicsBody[2];
            bodiesB[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-2), FP64.Zero, FP64.Zero),
                FP64.FromInt(3), FP64.One);
            bodiesB[1] = MakeDynamicSphere(2,
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                FP64.FromInt(5), FP64.One);

            for (int i = 0; i < 5; i++)
            {
                worldA.Step(bodiesA, 2, dt, gravity, null, null, null);
                worldB.Step(bodiesB, 2, dt, gravity, null, null, null);
            }

            for (int i = 0; i < 2; i++)
            {
                Assert.AreEqual(bodiesA[i].position.x.RawValue, bodiesB[i].position.x.RawValue);
                Assert.AreEqual(bodiesA[i].position.y.RawValue, bodiesB[i].position.y.RawValue);
                Assert.AreEqual(bodiesA[i].position.z.RawValue, bodiesB[i].position.z.RawValue);
                Assert.AreEqual(bodiesA[i].rigidBody.velocity.x.RawValue, bodiesB[i].rigidBody.velocity.x.RawValue);
                Assert.AreEqual(bodiesA[i].rigidBody.velocity.y.RawValue, bodiesB[i].rigidBody.velocity.y.RawValue);
            }
        }

        #endregion

        #region Serialization

        [Test]
        public void SerializeDeserialize_RestoresState()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            FP64 dt = FP64.FromFloat(0.02f);

            // Overlapping trigger pair -> creates trigger state
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeStaticSphere(10, FPVector3.Zero, FP64.One);
            bodies[0].isTrigger = true;
            bodies[1] = MakeStaticSphere(20,
                new FPVector3(FP64.Half, FP64.Zero, FP64.Zero), FP64.One);

            world.Step(bodies, 2, dt, FPVector3.Zero, null, null, null);

            // Serialize
            int size = world.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            world.Serialize(ref writer);

            // Deserialize into new world
            var world2 = new FPPhysicsWorld(FP64.FromInt(4));
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            world2.Deserialize(ref reader);

            // Same pair -> should be Stay (not Enter)
            var entered = new List<(int, int)>();
            var stayed = new List<(int, int)>();
            world2.Step(bodies, 2, dt, FPVector3.Zero,
                (a, b) => entered.Add((a, b)),
                (a, b) => stayed.Add((a, b)),
                null);

            Assert.AreEqual(0, entered.Count);
            Assert.AreEqual(1, stayed.Count);
        }

        #endregion

        #region CCD

        static FPPhysicsBody MakeDynamicBox(int id, FPVector3 position, FP64 mass, FPVector3 halfExtents)
        {
            var body = new FPPhysicsBody();
            body.id = id;
            body.rigidBody = FPRigidBody.CreateDynamic(mass);
            body.collider = FPCollider.FromBox(new FPBoxShape(halfExtents, position));
            body.position = position;
            body.rotation = FPQuaternion.Identity;
            return body;
        }

        [Test]
        public void CCD_Disabled_FastBodiesTunnel()
        {
            // Two spheres are far apart and moving toward each other at high speed
            // Without CCD, the broadphase fails to detect them as a pair
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-50), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);

            FP64 posYBefore = bodies[0].position.y;
            world.Step(bodies, 2, dt, FPVector3.Zero, null, null, null);

            // Without CCD: fast sphere just passes through, no x-axis collision response
            // Sphere passes the static sphere in a single step
            Assert.IsTrue(bodies[0].position.x.ToFloat() > -50f);
        }

        [Test]
        public void CCD_Enabled_BroadphaseDetectsFastBody()
        {
            // Two separated spheres — fast body moves toward static body.
            // Without CCD: broadphase misses the pair (different grid cell), no collision.
            // With CCD: AABB expansion captures the pair -> speculative contact -> velocity clamp.
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            // Dynamic sphere x=-10, radius=1, velocity=1000 in +x direction
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].rigidBody.restitution = FP64.One;
            bodies[0].useCCD = true;

            // Static sphere at x=10, radius=1
            // Separation = 18 (center distance 20 - radii sum 2)
            // displacement = 1000 * 0.02 = 20 -> expanded AABB max.x = -9 + 20 = 11 -> overlaps [9, 11]
            // approach speed = 1000 > 0, dist = 18 < 1000 * 0.02 = 20 -> speculative contact
            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Speculative contact clamps approach velocity -> velocity reduced
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 1000f);
        }

        [Test]
        public void CCD_DisabledGlobally_NoExpansion()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            // CCD globally disabled
            var ccdConfig = new FPCCDConfig { enabled = false, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // No AABB expansion -> no collision detection -> no velocity change
            Assert.AreEqual(1000f, bodies[0].rigidBody.velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void CCD_StaticBody_NoExpansion()
        {
            // Static body with useCCD=true should not get AABB expansion
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeStaticSphere(1, FPVector3.Zero, FP64.One);
            bodies[0].useCCD = true;

            bodies[1] = MakeDynamicSphere(2,
                new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Static body doesn't move, no AABB expansion
            Assert.AreEqual(FPVector3.Zero, bodies[0].rigidBody.velocity);
        }

        [Test]
        public void CCD_PerBodyFalse_NoExpansion()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = false;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Per-body CCD false -> no expansion -> no collision
            Assert.AreEqual(1000f, bodies[0].rigidBody.velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void CCD_Determinism_BitExact()
        {
            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            var worldA = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesA = new FPPhysicsBody[2];
            bodiesA[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodiesA[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodiesA[0].rigidBody.restitution = FP64.One;
            bodiesA[0].useCCD = true;
            bodiesA[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            var worldB = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesB = new FPPhysicsBody[2];
            bodiesB[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodiesB[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodiesB[0].rigidBody.restitution = FP64.One;
            bodiesB[0].useCCD = true;
            bodiesB[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            for (int i = 0; i < 5; i++)
            {
                worldA.Step(bodiesA, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                    ccdConfig, null, null, null);
                worldB.Step(bodiesB, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                    ccdConfig, null, null, null);
            }

            Assert.AreEqual(bodiesA[0].position.x.RawValue, bodiesB[0].position.x.RawValue);
            Assert.AreEqual(bodiesA[0].position.y.RawValue, bodiesB[0].position.y.RawValue);
            Assert.AreEqual(bodiesA[0].rigidBody.velocity.x.RawValue, bodiesB[0].rigidBody.velocity.x.RawValue);
            Assert.AreEqual(bodiesA[0].rigidBody.velocity.y.RawValue, bodiesB[0].rigidBody.velocity.y.RawValue);
        }

        [Test]
        public void CCD_ExistingOverload_StillWorks()
        {
            // Verify the existing Step overload without FPCCDConfig still works
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];
            bodies[0] = MakeDynamicSphere(1, FPVector3.Zero, FP64.One, FP64.One);
            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);

            Assert.DoesNotThrow(() =>
                world.Step(bodies, 1, dt, gravity, null, null, null));
            Assert.IsTrue(bodies[0].rigidBody.velocity.y.ToFloat() < 0f);
        }

        [Test]
        public void CCD_Speculative_VelocityClamped_NotReversed()
        {
            // Speculative contact must clamp approach velocity, not reverse it.
            // After speculative response, the body should still move toward the target
            // but at a speed that won't tunnel past it in one dt.
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Velocity reduced but still positive (clamped, not reflected)
            float vx = bodies[0].rigidBody.velocity.x.ToFloat();
            Assert.IsTrue(vx < 1000f);
            Assert.IsTrue(vx >= 0f);
        }

        [Test]
        public void CCD_Speculative_NoPositionCorrection()
        {
            // Speculative contact must not apply position correction
            // (bodies are separated, not overlapping)
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FPVector3 posBeforeA = bodies[0].position;
            FPVector3 posBeforeB = bodies[1].position;

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Static body position unchanged (no position correction for speculative contact)
            Assert.AreEqual(posBeforeB.x.RawValue, bodies[1].position.x.RawValue);
            // Dynamic body position should change only by integration, not by correction
            // After integration: pos = posBeforeA + velocity * dt (velocity is clamped)
            // Position should have moved forward but not been pushed back by correction
            Assert.IsTrue(bodies[0].position.x.ToFloat() > posBeforeA.x.ToFloat());
        }

        [Test]
        public void CCD_Speculative_SlowBody_NoSpeculativeContact()
        {
            // If approach speed * dt < separation distance,
            // no speculative contact should be created
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            // Slow body: velocity = 10, dt = 0.02 -> displacement = 0.2
            // Separation distance = 18 -> 0.2 < 18 -> no speculative contact
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // No velocity change — no speculative contact created
            Assert.AreEqual(10f, bodies[0].rigidBody.velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void CCD_Speculative_DivergingBodies_NoContact()
        {
            // Diverging bodies must not create a speculative contact
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            // Move away from the static body (negative velocity)
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(-1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Velocity unchanged — diverging bodies
            Assert.AreEqual(-1000f, bodies[0].rigidBody.velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void CCD_Speculative_TriggerPair_FiresEvent()
        {
            // A speculative contact with a trigger must fire trigger events, not a physical response
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);
            bodies[1].isTrigger = true;

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            var entered = new List<(int, int)>();
            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig,
                (a, b) => entered.Add((a, b)), null, null);

            // Trigger event fires via speculative detection
            Assert.AreEqual(1, entered.Count);
            // No velocity change — trigger pair, no physical response
            Assert.AreEqual(1000f, bodies[0].rigidBody.velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void CCD_Speculative_BothDynamic_BothAffected()
        {
            // Two dynamic bodies approaching each other at high speed
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(500), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeDynamicSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[1].rigidBody.velocity = new FPVector3(FP64.FromInt(-500), FP64.Zero, FP64.Zero);
            bodies[1].useCCD = true;

            FP64 dt = FP64.FromFloat(0.02f);
            // Relative approach speed = 1000, dist = 18, 18 < 1000 * 0.02 = 20
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Approach velocities of both bodies must be clamped
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 500f);
            Assert.IsTrue(bodies[1].rigidBody.velocity.x.ToFloat() > -500f);
        }

        [Test]
        public void CCD_Speculative_BoxSphere_Works()
        {
            // Speculative contact also works across different shape types
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicBox(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One,
                new FPVector3(FP64.One, FP64.One, FP64.One));
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            // Box half-extent=1, sphere radius=1 → separation = 20 - 1 - 1 = 18
            // displacement = 1000 * 0.02 = 20 > 18 -> speculative contact
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 1000f);
        }

        [Test]
        public void CCD_GhostCollision_BystanderCausesVelocityChange()
        {
            // Ghost collision: fast body (A) moves toward target (B) along x-axis.
            // Bystander (C) is off-path but within CCD-expanded AABB.
            // A receives a ghost speculative contact with C and its velocity changes
            // Compare against scenario without C.
            //
            // Setup:
            //   A: x=-10, vel=(1000,0,0), radius=1, CCD=true
            //   B: x=10, static, radius=1 (target)
            //   C: (5,3,0), static, radius=1 (bystander)
            //
            // A's expanded AABB: x=[-11, 9], after CCD expansion x=[-11, 9+20]=[-11, 29]
            // C at x=5 is within this range.
            // A-C distance ≈ sqrt(15²+3²)-2 ≈ 13.3, relVel=1000
            // approachSpeed ≈ dot((1000,0,0), normal_A→C) > 0
            // 13.3 < approach speed * 0.02 -> ghost speculative contact created

            // Run 1: without bystander (baseline)
            var world1 = new FPPhysicsWorld(FP64.FromInt(4));
            var baseline = new FPPhysicsBody[2];
            baseline[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            baseline[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            baseline[0].useCCD = true;
            baseline[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.Zero };

            world1.Step(baseline, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            long baseVelX = baseline[0].rigidBody.velocity.x.RawValue;
            long baseVelY = baseline[0].rigidBody.velocity.y.RawValue;

            // Run 2: with bystander C
            var world2 = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[3];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;
            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);
            bodies[2] = MakeStaticSphere(3,
                new FPVector3(FP64.FromInt(5), FP64.FromInt(3), FP64.Zero), FP64.One);

            world2.Step(bodies, 3, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Ghost collision: due to the bystander, A's velocity differs from baseline
            bool ghostOccurred =
                bodies[0].rigidBody.velocity.x.RawValue != baseVelX ||
                bodies[0].rigidBody.velocity.y.RawValue != baseVelY;
            Assert.IsTrue(ghostOccurred,
                "Ghost collision should cause A's velocity to differ when bystander is present");

            // A should still move in +x direction (clamped, not reversed)
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() > 0f,
                "A should still move forward despite ghost collision");
        }

        [Test]
        public void CCD_GhostCollision_BystanderFarAway_NoGhost()
        {
            // If bystander C is far enough from A's path
            // dist > approach speed * dt, no ghost contact is created.
            var world1 = new FPPhysicsWorld(FP64.FromInt(4));
            var baseline = new FPPhysicsBody[2];
            baseline[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            baseline[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            baseline[0].useCCD = true;
            baseline[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.Zero };

            world1.Step(baseline, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            long baseVelX = baseline[0].rigidBody.velocity.x.RawValue;
            long baseVelY = baseline[0].rigidBody.velocity.y.RawValue;

            // Bystander y=30 — very far from path
            var world2 = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[3];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;
            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);
            bodies[2] = MakeStaticSphere(3,
                new FPVector3(FP64.FromInt(5), FP64.FromInt(30), FP64.Zero), FP64.One);

            world2.Step(bodies, 3, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Far bystander: no ghost collision -> same velocity
            Assert.AreEqual(baseVelX, bodies[0].rigidBody.velocity.x.RawValue,
                "Far bystander should not cause ghost collision (x)");
            Assert.AreEqual(baseVelY, bodies[0].rigidBody.velocity.y.RawValue,
                "Far bystander should not cause ghost collision (y)");
        }

        [Test]
        public void CCD_VelocityThreshold_PreventsLowSpeedExpansion()
        {
            // velocityThreshold prevents AABB expansion for bodies below the threshold.
            // Body with velocity=5 < threshold=10 -> no CCD expansion -> no speculative contact.
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            // separation = 8, displacement = 5*0.02 = 0.1 → no overlap without expansion
            // threshold = 10 > velocity 5 -> skip CCD expansion
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10) };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // No expansion, no speculative contact -> no velocity change (excluding integration)
            Assert.AreEqual(5f, bodies[0].rigidBody.velocity.x.ToFloat(), EPSILON,
                "Low-speed body below threshold should not get CCD treatment");
        }

        #endregion

        #region MultiStep

        [Test]
        public void MultiStep_GravityAccumulates()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.Zero, FP64.FromInt(100), FP64.Zero),
                FP64.One, FP64.One);
            FP64 dt = FP64.FromFloat(0.05f);
            FPVector3 gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);

            for (int i = 0; i < 10; i++)
                world.Step(bodies, 1, dt, gravity, null, null, null);

            // After 10 steps: vel.y ~= -10 * 0.05 * 10 = -5
            Assert.AreEqual(-5f, bodies[0].rigidBody.velocity.y.ToFloat(), 0.1f);
            Assert.IsTrue(bodies[0].position.y.ToFloat() < 100f);
        }

        #endregion

        #region Sweep

        [Test]
        public void Sweep_SphereSphere_NoTunneling()
        {
            // Fast sphere (v=1000) aimed at static sphere 20 units away.
            // Without sweep: tunneling. With sweep: stops at contact.
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useSweep = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10), maxSweepIterations = 4 };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Body must not tunnel through x=10
            Assert.IsTrue(bodies[0].position.x.ToFloat() < 10f,
                "Sweep should prevent tunneling");
            // Velocity should change due to collision response
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 1000f,
                "Velocity should be affected by sweep collision");
        }

        [Test]
        public void Sweep_SphereBox_NoTunneling()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useSweep = true;

            bodies[1] = new FPPhysicsBody();
            bodies[1].id = 2;
            bodies[1].rigidBody = FPRigidBody.CreateStatic();
            bodies[1].collider = FPCollider.FromBox(new FPBoxShape(
                new FPVector3(FP64.One, FP64.FromInt(5), FP64.FromInt(5)),
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero)));
            bodies[1].position = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            bodies[1].rotation = FPQuaternion.Identity;

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10), maxSweepIterations = 4 };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            Assert.IsTrue(bodies[0].position.x.ToFloat() < 10f,
                "Sweep should prevent sphere from tunneling through box");
        }

        [Test]
        public void Sweep_NoGhostCollision()
        {
            // Core test: an off-path bystander must not affect the sweep body
            // (unlike speculative CCD which causes ghost collisions)

            // Baseline: no bystander
            var world1 = new FPPhysicsWorld(FP64.FromInt(4));
            var baseline = new FPPhysicsBody[2];
            baseline[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            baseline[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            baseline[0].useSweep = true;
            baseline[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.Zero, maxSweepIterations = 4 };

            world1.Step(baseline, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            long baseVelX = baseline[0].rigidBody.velocity.x.RawValue;
            long baseVelY = baseline[0].rigidBody.velocity.y.RawValue;

            // Bystander (5,3,0) — off-path
            var world2 = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[3];
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useSweep = true;
            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);
            bodies[2] = MakeStaticSphere(3,
                new FPVector3(FP64.FromInt(5), FP64.FromInt(3), FP64.Zero), FP64.One);

            world2.Step(bodies, 3, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Sweep should produce identical results regardless of bystander presence (no ghost collision)
            Assert.AreEqual(baseVelX, bodies[0].rigidBody.velocity.x.RawValue,
                "Sweep: bystander should NOT affect velocity (no ghost collision)");
            Assert.AreEqual(baseVelY, bodies[0].rigidBody.velocity.y.RawValue,
                "Sweep: bystander y velocity should be identical");
        }

        [Test]
        public void Sweep_NonSphere_FallsBackToSpeculative()
        {
            // A box with useSweep=true should not be treated as a sweep body
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicBox(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One,
                new FPVector3(FP64.One, FP64.One, FP64.One));
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useSweep = true;
            bodies[0].useCCD = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10), maxSweepIterations = 4 };

            world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Should receive speculative CCD treatment (velocity clamp)
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 1000f,
                "Non-sphere with useSweep should fall back to speculative CCD");
        }

        [Test]
        public void Sweep_Determinism_BitExact()
        {
            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10), maxSweepIterations = 4 };

            var worldA = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesA = new FPPhysicsBody[2];
            bodiesA[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodiesA[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodiesA[0].useSweep = true;
            bodiesA[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            var worldB = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesB = new FPPhysicsBody[2];
            bodiesB[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodiesB[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodiesB[0].useSweep = true;
            bodiesB[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            for (int i = 0; i < 5; i++)
            {
                worldA.Step(bodiesA, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                    ccdConfig, null, null, null);
                worldB.Step(bodiesB, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                    ccdConfig, null, null, null);
            }

            Assert.AreEqual(bodiesA[0].position.x.RawValue, bodiesB[0].position.x.RawValue);
            Assert.AreEqual(bodiesA[0].position.y.RawValue, bodiesB[0].position.y.RawValue);
            Assert.AreEqual(bodiesA[0].rigidBody.velocity.x.RawValue, bodiesB[0].rigidBody.velocity.x.RawValue);
        }

        [Test]
        public void Sweep_MaxIterations_Terminates()
        {
            // With maxSweepIterations=1, only one sweep collision is resolved per frame
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];

            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useSweep = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10), maxSweepIterations = 1 };

            // Should not hang or crash
            Assert.DoesNotThrow(() =>
                world.Step(bodies, 2, dt, FPVector3.Zero, null, 0, null, 0, 1,
                    ccdConfig, null, null, null));
        }

        [Test]
        public void Sweep_CoexistsWithSpeculative()
        {
            // Two pairs: sweep sphere + speculative box, both at high speed
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[4];

            // Pair 1: sweep sphere
            bodies[0] = MakeDynamicSphere(1,
                new FPVector3(FP64.FromInt(-10), FP64.Zero, FP64.Zero),
                FP64.One, FP64.One);
            bodies[0].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[0].useSweep = true;

            bodies[1] = MakeStaticSphere(2,
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero), FP64.One);

            // Pair 2: speculative CCD box (not sweep)
            bodies[2] = MakeDynamicBox(3,
                new FPVector3(FP64.FromInt(-10), FP64.FromInt(20), FP64.Zero),
                FP64.One,
                new FPVector3(FP64.One, FP64.One, FP64.One));
            bodies[2].rigidBody.velocity = new FPVector3(FP64.FromInt(1000), FP64.Zero, FP64.Zero);
            bodies[2].useCCD = true;

            bodies[3] = MakeStaticSphere(4,
                new FPVector3(FP64.FromInt(10), FP64.FromInt(20), FP64.Zero), FP64.One);

            FP64 dt = FP64.FromFloat(0.02f);
            var ccdConfig = new FPCCDConfig { enabled = true, velocityThreshold = FP64.FromInt(10), maxSweepIterations = 4 };

            world.Step(bodies, 4, dt, FPVector3.Zero, null, 0, null, 0, 1,
                ccdConfig, null, null, null);

            // Both should have their velocity clamped
            Assert.IsTrue(bodies[0].rigidBody.velocity.x.ToFloat() < 1000f,
                "Sweep sphere velocity should be clamped");
            Assert.IsTrue(bodies[2].rigidBody.velocity.x.ToFloat() < 1000f,
                "Speculative box velocity should be clamped");
        }

        #endregion

        #region DynamicStatic

        static FPStaticCollider MakeStaticCollider(int id, FPVector3 position, FP64 radius, bool isTrigger = false)
        {
            return new FPStaticCollider
            {
                id = id,
                collider = FPCollider.FromSphere(new FPSphereShape(radius, position)),
                meshData = default,
                isTrigger = isTrigger
            };
        }

        [Test]
        public void BuildStatic_DynamicHitsStaticBody_DynamicPushedBack()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);
            bodies[1] = MakeStaticSphere(2, FPVector3.Zero, FP64.One);

            world.BuildStatic(bodies, 2);

            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 posBeforeDyn = bodies[0].position;
            world.Step(bodies, 2, dt, FPVector3.Zero, null, null, null);

            // The overlapping dynamic body is pushed away by the static body (moves along x)
            Assert.AreNotEqual(posBeforeDyn.x.RawValue, bodies[0].position.x.RawValue);
        }

        [Test]
        public void BuildStatic_StaticBodyDoesNotMove()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);
            bodies[1] = MakeStaticSphere(2, FPVector3.Zero, FP64.One);

            world.BuildStatic(bodies, 2);

            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 staticPosBefore = bodies[1].position;
            world.Step(bodies, 2, dt, FPVector3.Zero, null, null, null);

            Assert.AreEqual(staticPosBefore.x.RawValue, bodies[1].position.x.RawValue);
            Assert.AreEqual(staticPosBefore.y.RawValue, bodies[1].position.y.RawValue);
            Assert.AreEqual(staticPosBefore.z.RawValue, bodies[1].position.z.RawValue);
        }

        [Test]
        public void BuildStatic_NoOverlap_NoResponse()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero), FP64.One, FP64.One);
            bodies[1] = MakeStaticSphere(2, FPVector3.Zero, FP64.One);

            world.BuildStatic(bodies, 2);

            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 dynPosBefore = bodies[0].position;
            world.Step(bodies, 2, dt, FPVector3.Zero, null, null, null);

            // Step without gravity -> position should not change
            Assert.AreEqual(dynPosBefore.x.RawValue, bodies[0].position.x.RawValue);
        }

        [Test]
        public void BuildStatic_TriggerPair_CallbackFired()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[2];
            bodies[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);
            bodies[0].isTrigger = true;
            bodies[1] = MakeStaticSphere(2, FPVector3.Zero, FP64.One);

            world.BuildStatic(bodies, 2);

            FP64 dt = FP64.FromFloat(0.02f);
            var entered = new List<(int, int)>();
            world.Step(bodies, 2, dt, FPVector3.Zero,
                (a, b) => entered.Add((a, b)), null, null);

            Assert.AreEqual(1, entered.Count);
        }

        [Test]
        public void RebuildStaticBVH_FPStaticCollider_DynamicResolved()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];
            bodies[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);

            var colliders = new[] { MakeStaticCollider(10, FPVector3.Zero, FP64.One) };
            world.LoadStaticColliders(colliders, 1);
            world.RebuildStaticBVH(bodies, 1);

            FP64 dt = FP64.FromFloat(0.02f);
            FPVector3 posBefore = bodies[0].position;
            world.Step(bodies, 1, dt, FPVector3.Zero, null, null, null);

            Assert.AreNotEqual(posBefore.x.RawValue, bodies[0].position.x.RawValue);
        }

        [Test]
        public void RebuildStaticBVH_FPStaticColliderTrigger_CallbackFired()
        {
            var world = new FPPhysicsWorld(FP64.FromInt(4));
            var bodies = new FPPhysicsBody[1];
            bodies[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);

            var colliders = new[] { MakeStaticCollider(10, FPVector3.Zero, FP64.One, isTrigger: true) };
            world.LoadStaticColliders(colliders, 1);
            world.RebuildStaticBVH(bodies, 1);

            FP64 dt = FP64.FromFloat(0.02f);
            var entered = new List<(int, int)>();
            world.Step(bodies, 1, dt, FPVector3.Zero,
                (a, b) => entered.Add((a, b)), null, null);

            Assert.AreEqual(1, entered.Count);
        }

        [Test]
        public void BuildStatic_Determinism_SameResultTwice()
        {
            FP64 dt = FP64.FromFloat(0.02f);

            var worldA = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesA = new FPPhysicsBody[2];
            bodiesA[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);
            bodiesA[1] = MakeStaticSphere(2, FPVector3.Zero, FP64.One);
            worldA.BuildStatic(bodiesA, 2);

            var worldB = new FPPhysicsWorld(FP64.FromInt(4));
            var bodiesB = new FPPhysicsBody[2];
            bodiesB[0] = MakeDynamicSphere(1, new FPVector3(FP64.FromFloat(0.5f), FP64.Zero, FP64.Zero), FP64.One, FP64.One);
            bodiesB[1] = MakeStaticSphere(2, FPVector3.Zero, FP64.One);
            worldB.BuildStatic(bodiesB, 2);

            for (int i = 0; i < 5; i++)
            {
                worldA.Step(bodiesA, 2, dt, FPVector3.Zero, null, null, null);
                worldB.Step(bodiesB, 2, dt, FPVector3.Zero, null, null, null);
            }

            Assert.AreEqual(bodiesA[0].position.x.RawValue, bodiesB[0].position.x.RawValue);
            Assert.AreEqual(bodiesA[0].position.y.RawValue, bodiesB[0].position.y.RawValue);
            Assert.AreEqual(bodiesA[0].rigidBody.velocity.x.RawValue, bodiesB[0].rigidBody.velocity.x.RawValue);
        }

        #endregion
    }
}
