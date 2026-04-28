using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Systems.Tests
{
    [TestFixture]
    public class MovementSystemTests
    {
        private const int MaxEntities = 32;
        private const int DeltaTimeMs = 50;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory configuration (same as ZLogger)
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

        private EntityRef CreateMovingEntity(Frame frame, FPVector3 position, FPVector3 target, FP64 moveSpeed)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent
            {
                Position = position,
                Scale = FPVector3.One
            });
            frame.Add(entity, new VelocityComponent());
            frame.Add(entity, new MovementComponent
            {
                MoveSpeed = moveSpeed,
                TargetPosition = target,
                IsMoving = true
            });
            return entity;
        }

        [Test]
        public void NotMoving_NoPositionChange()
        {
            var frame = CreateFrame();
            var entity = frame.CreateEntity();
            var pos = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.FromInt(20));
            frame.Add(entity, new TransformComponent { Position = pos, Scale = FPVector3.One });
            frame.Add(entity, new VelocityComponent());
            frame.Add(entity, new MovementComponent
            {
                MoveSpeed = FP64.FromInt(5),
                IsMoving = false
            });

            var system = new MovementSystem();
            system.Update(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            Assert.AreEqual(pos.x.RawValue, transform.Position.x.RawValue);
            Assert.AreEqual(pos.z.RawValue, transform.Position.z.RawValue);
        }

        [Test]
        public void Moving_PositionChangesTowardTarget()
        {
            var frame = CreateFrame();
            var start = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            var entity = CreateMovingEntity(frame, start, target, FP64.FromInt(5));

            var system = new MovementSystem();
            system.Update(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            // Position should have moved toward the target (x > 0)
            Assert.IsTrue(transform.Position.x > FP64.Zero, "Should move toward target");
            Assert.AreEqual(FP64.Zero.RawValue, transform.Position.z.RawValue, "Z should remain 0");
        }

        [Test]
        public void Moving_VelocitySetCorrectly()
        {
            var frame = CreateFrame();
            var start = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            var entity = CreateMovingEntity(frame, start, target, FP64.FromInt(5));

            var system = new MovementSystem();
            system.Update(ref frame);

            ref readonly var velocity = ref frame.GetReadOnly<VelocityComponent>(entity);
            // Velocity should be in +X direction with magnitude = MoveSpeed
            Assert.IsTrue(velocity.Velocity.x > FP64.Zero);
        }

        [Test]
        public void ReachesTarget_StopsMoving()
        {
            var frame = CreateFrame();
            // Place the entity very close to the target (within the 0.1 threshold)
            var pos = new FPVector3(FP64.FromFloat(99.95f), FP64.Zero, FP64.Zero);
            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            var entity = CreateMovingEntity(frame, pos, target, FP64.FromInt(5));

            var system = new MovementSystem();
            system.Update(ref frame);

            ref readonly var movement = ref frame.GetReadOnly<MovementComponent>(entity);
            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            ref readonly var velocity = ref frame.GetReadOnly<VelocityComponent>(entity);

            Assert.IsFalse(movement.IsMoving, "Should stop moving when reaching target");
            Assert.AreEqual(target.x.RawValue, transform.Position.x.RawValue, "Should snap to target");
            Assert.AreEqual(FP64.Zero.RawValue, velocity.Velocity.x.RawValue, "Velocity should be zero");
        }

        [Test]
        public void DoesNotOvershoot()
        {
            var frame = CreateFrame();
            // MoveSpeed is very high and target is close - must not overshoot
            var start = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            var target = new FPVector3(FP64.FromFloat(0.2f), FP64.Zero, FP64.Zero);
            var entity = CreateMovingEntity(frame, start, target, FP64.FromInt(100));

            var system = new MovementSystem();
            system.Update(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            // Position X must not exceed target X
            Assert.IsTrue(transform.Position.x <= target.x + FP64.FromFloat(0.01f),
                $"Should not overshoot: pos={transform.Position.x}, target={target.x}");
        }

        [Test]
        public void MultipleEntities_IndependentMovement()
        {
            var frame = CreateFrame();

            var entity1 = CreateMovingEntity(frame,
                FPVector3.Zero,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                FP64.FromInt(5));

            var entity2 = CreateMovingEntity(frame,
                FPVector3.Zero,
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(100)),
                FP64.FromInt(10));

            var system = new MovementSystem();
            system.Update(ref frame);

            ref readonly var t1 = ref frame.GetReadOnly<TransformComponent>(entity1);
            ref readonly var t2 = ref frame.GetReadOnly<TransformComponent>(entity2);

            // Entity1 moves +X, Entity2 moves +Z
            Assert.IsTrue(t1.Position.x > FP64.Zero);
            Assert.AreEqual(FP64.Zero.RawValue, t1.Position.z.RawValue);

            Assert.AreEqual(FP64.Zero.RawValue, t2.Position.x.RawValue);
            Assert.IsTrue(t2.Position.z > FP64.Zero);
        }

        [Test]
        public void ContinuousMovement_100Ticks_ReachesTarget()
        {
            var frame = CreateFrame();
            var start = FPVector3.Zero;
            var target = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            var entity = CreateMovingEntity(frame, start, target, FP64.FromInt(5));

            var system = new MovementSystem();
            for (int i = 0; i < 100; i++)
            {
                system.Update(ref frame);
            }

            ref readonly var movement = ref frame.GetReadOnly<MovementComponent>(entity);
            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);

            Assert.IsFalse(movement.IsMoving, "Should have reached target within 100 ticks");
            Assert.AreEqual(target.x.RawValue, transform.Position.x.RawValue);
        }

        [Test]
        public void Determinism_SameInput_SameOutput()
        {
            var system = new MovementSystem();

            var frame1 = CreateFrame();
            var frame2 = CreateFrame();

            var start = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(3));
            var target = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.FromInt(30));
            var speed = FP64.FromInt(7);

            CreateMovingEntity(frame1, start, target, speed);
            CreateMovingEntity(frame2, start, target, speed);

            for (int i = 0; i < 50; i++)
            {
                system.Update(ref frame1);
                system.Update(ref frame2);
            }

            Assert.AreEqual(frame1.CalculateHash(), frame2.CalculateHash(),
                "Two identical frames should produce same hash after 50 ticks");
        }
    }
}
