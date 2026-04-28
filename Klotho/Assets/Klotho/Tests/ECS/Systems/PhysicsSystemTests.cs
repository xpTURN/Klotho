using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.ECS.Systems.Tests
{
    [TestFixture]
    public class PhysicsSystemTests
    {
        private const int MaxEntities = 32;
        private const int DeltaTimeMs = 50;

        private static readonly FPVector3 NoGravity = FPVector3.Zero;
        private static readonly FPVector3 Gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Configure LoggerFactory (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        private Frame CreateFrame()
        {
            var frame = new Frame(MaxEntities, _logger);
            frame.DeltaTimeMs = DeltaTimeMs;
            return frame;
        }

        private EntityRef CreateDynamicEntity(Frame frame, FPVector3 position, FPVector3 initialVelocity)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = position, Scale = FPVector3.One });
            frame.Add(entity, new VelocityComponent { Velocity = initialVelocity });

            var sphere = new FPSphereShape(FP64.One, position);
            var collider = FPCollider.FromSphere(sphere);
            var rigidBody = FPRigidBody.CreateDynamic(FP64.One);
            rigidBody.velocity = initialVelocity;

            frame.Add(entity, new PhysicsBodyComponent { RigidBody = rigidBody, Collider = collider });
            return entity;
        }

        private EntityRef CreateStaticEntity(Frame frame, FPVector3 position)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = position, Scale = FPVector3.One });

            var sphere = new FPSphereShape(FP64.One, position);
            var collider = FPCollider.FromSphere(sphere);
            var rigidBody = FPRigidBody.CreateStatic();

            frame.Add(entity, new PhysicsBodyComponent { RigidBody = rigidBody, Collider = collider });
            return entity;
        }

        [Test]
        public void KinematicBody_WithInitialVelocity_MovesInDirection()
        {
            var frame = CreateFrame();
            var velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            var entity = CreateDynamicEntity(frame, FPVector3.Zero, velocity);

            var system = new PhysicsSystem(MaxEntities, NoGravity);
            system.Update(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            Assert.IsTrue(transform.Position.x > FP64.Zero, "Body should have moved in +X direction");
        }

        [Test]
        public void Gravity_PullsBodyDown()
        {
            var frame = CreateFrame();
            var entity = CreateDynamicEntity(frame, FPVector3.Zero, FPVector3.Zero);

            var system = new PhysicsSystem(MaxEntities, Gravity);
            system.Update(ref frame);

            ref readonly var velocity = ref frame.GetReadOnly<VelocityComponent>(entity);
            Assert.IsTrue(velocity.Velocity.y < FP64.Zero, "Gravity should pull body downward");
        }

        [Test]
        public void StaticBody_DoesNotMove()
        {
            var frame = CreateFrame();
            var pos = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            var entity = CreateStaticEntity(frame, pos);

            var system = new PhysicsSystem(MaxEntities, Gravity);
            system.Update(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            Assert.AreEqual(pos.x.RawValue, transform.Position.x.RawValue, "Static body should not move");
            Assert.AreEqual(pos.y.RawValue, transform.Position.y.RawValue);
        }

        [Test]
        public void VelocityComponent_SyncedAfterStep()
        {
            var frame = CreateFrame();
            var velocity = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            var entity = CreateDynamicEntity(frame, FPVector3.Zero, velocity);

            var system = new PhysicsSystem(MaxEntities, NoGravity);
            system.Update(ref frame);

            ref readonly var vel = ref frame.GetReadOnly<VelocityComponent>(entity);
            // No gravity, no collision → no velocity change
            Assert.AreEqual(velocity.x.RawValue, vel.Velocity.x.RawValue, "VelocityComponent should reflect physics result");
        }

        [Test]
        public void NoPhysicsBodies_DoesNotThrow()
        {
            var frame = CreateFrame();
            // Entity with no PhysicsBodyComponent
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });

            var system = new PhysicsSystem(MaxEntities, Gravity);
            Assert.DoesNotThrow(() => system.Update(ref frame), "Should handle frame with no physics bodies");
        }

        [Test]
        public void PhysicsBodyComponent_RigidBodyUpdatedAfterStep()
        {
            var frame = CreateFrame();
            var entity = CreateDynamicEntity(frame, FPVector3.Zero, FPVector3.Zero);

            var system = new PhysicsSystem(MaxEntities, Gravity);
            system.Update(ref frame);

            // After a step with gravity applied, rigidBody.velocity.y must be negative
            ref readonly var physBody = ref frame.GetReadOnly<PhysicsBodyComponent>(entity);
            Assert.IsTrue(physBody.RigidBody.velocity.y < FP64.Zero,
                "RigidBody velocity should reflect gravity after step");
        }

        [Test]
        public void Determinism_SameInput_SameHash()
        {
            var frame1 = CreateFrame();
            var frame2 = CreateFrame();

            var velocity = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero);
            CreateDynamicEntity(frame1, FPVector3.Zero, velocity);
            CreateDynamicEntity(frame2, FPVector3.Zero, velocity);

            var system1 = new PhysicsSystem(MaxEntities, Gravity);
            var system2 = new PhysicsSystem(MaxEntities, Gravity);

            for (int i = 0; i < 10; i++)
            {
                system1.Update(ref frame1);
                system2.Update(ref frame2);
            }

            Assert.AreEqual(frame1.CalculateHash(), frame2.CalculateHash(),
                "Identical physics simulations should produce same hash");
        }
    }
}
