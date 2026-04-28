using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Systems.Tests
{
    [TestFixture]
    public class CommandSystemTests
    {
        private const int MaxEntities = 32;
        private const int TickIntervalMs = 50;

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

        private Frame CreateFrameWithUnit(int ownerId, FPVector3 position, out EntityRef entity)
        {
            var frame = new Frame(MaxEntities, _logger);
            frame.DeltaTimeMs = TickIntervalMs;
            entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent
            {
                Position = position,
                Scale = FPVector3.One
            });
            frame.Add(entity, new OwnerComponent { OwnerId = ownerId });
            frame.Add(entity, new VelocityComponent());
            frame.Add(entity, new MovementComponent
            {
                MoveSpeed = FP64.FromInt(5),
                IsMoving = false
            });
            return frame;
        }

        [Test]
        public void MoveCommand_SetsTargetAndIsMoving()
        {
            var frame = CreateFrameWithUnit(1, FPVector3.Zero, out var entity);
            var system = new CommandSystem();

            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.FromInt(50));
            var cmd = new MoveCommand(1, 0, target);
            system.OnCommand(ref frame, cmd);

            ref readonly var movement = ref frame.GetReadOnly<MovementComponent>(entity);
            Assert.IsTrue(movement.IsMoving);
            Assert.AreEqual(target.x.RawValue, movement.TargetPosition.x.RawValue);
            Assert.AreEqual(target.z.RawValue, movement.TargetPosition.z.RawValue);
        }

        [Test]
        public void MoveCommand_WrongPlayer_NoEffect()
        {
            var frame = CreateFrameWithUnit(1, FPVector3.Zero, out var entity);
            var system = new CommandSystem();

            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            var cmd = new MoveCommand(2, 0, target); // Player 2, but entity owned by Player 1
            system.OnCommand(ref frame, cmd);

            ref readonly var movement = ref frame.GetReadOnly<MovementComponent>(entity);
            Assert.IsFalse(movement.IsMoving, "Should not move for wrong player");
        }

        [Test]
        public void MoveCommand_MultipleEntities_OnlyOwnerMoves()
        {
            var frame = new Frame(MaxEntities, _logger);
            frame.DeltaTimeMs = TickIntervalMs;

            // Entity owned by Player 1
            var e1 = frame.CreateEntity();
            frame.Add(e1, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
            frame.Add(e1, new OwnerComponent { OwnerId = 1 });
            frame.Add(e1, new MovementComponent { MoveSpeed = FP64.FromInt(5), IsMoving = false });

            // Entity owned by Player 2
            var e2 = frame.CreateEntity();
            frame.Add(e2, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
            frame.Add(e2, new OwnerComponent { OwnerId = 2 });
            frame.Add(e2, new MovementComponent { MoveSpeed = FP64.FromInt(5), IsMoving = false });

            var system = new CommandSystem();
            var target = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.FromInt(50));
            system.OnCommand(ref frame, new MoveCommand(1, 0, target));

            ref readonly var m1 = ref frame.GetReadOnly<MovementComponent>(e1);
            ref readonly var m2 = ref frame.GetReadOnly<MovementComponent>(e2);

            Assert.IsTrue(m1.IsMoving, "Player 1's entity should move");
            Assert.IsFalse(m2.IsMoving, "Player 2's entity should not move");
        }

        [Test]
        public void MoveCommand_OverwritesPreviousTarget()
        {
            var frame = CreateFrameWithUnit(1, FPVector3.Zero, out var entity);
            var system = new CommandSystem();

            var target1 = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            system.OnCommand(ref frame, new MoveCommand(1, 0, target1));

            var target2 = new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(200));
            system.OnCommand(ref frame, new MoveCommand(1, 0, target2));

            ref readonly var movement = ref frame.GetReadOnly<MovementComponent>(entity);
            Assert.AreEqual(target2.z.RawValue, movement.TargetPosition.z.RawValue);
        }

        [Test]
        public void MoveCommand_MultipleOwnedEntities_AllMove()
        {
            var frame = new Frame(MaxEntities, _logger);

            var entities = new EntityRef[3];
            for (int i = 0; i < 3; i++)
            {
                entities[i] = frame.CreateEntity();
                frame.Add(entities[i], new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
                frame.Add(entities[i], new OwnerComponent { OwnerId = 1 }); // All owned by Player 1
                frame.Add(entities[i], new MovementComponent { MoveSpeed = FP64.FromInt(5), IsMoving = false });
            }

            var system = new CommandSystem();
            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            system.OnCommand(ref frame, new MoveCommand(1, 0, target));

            for (int i = 0; i < 3; i++)
            {
                ref readonly var m = ref frame.GetReadOnly<MovementComponent>(entities[i]);
                Assert.IsTrue(m.IsMoving, $"Entity {i} should be moving");
                Assert.AreEqual(target.x.RawValue, m.TargetPosition.x.RawValue);
            }
        }

        [Test]
        public void CommandThenMovement_IntegrationTest()
        {
            var frame = CreateFrameWithUnit(1, FPVector3.Zero, out var entity);

            var commandSystem = new CommandSystem();
            var movementSystem = new MovementSystem();

            // Apply move command
            var target = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            commandSystem.OnCommand(ref frame, new MoveCommand(1, 0, target));

            // Run movement
            movementSystem.Update(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            Assert.IsTrue(transform.Position.x > FP64.Zero, "Should have moved toward target");
        }

        [Test]
        public void SystemRunner_CommandThenMovement_Integration()
        {
            var frame = new Frame(MaxEntities, _logger);
            frame.DeltaTimeMs = TickIntervalMs;

            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
            frame.Add(entity, new OwnerComponent { OwnerId = 1 });
            frame.Add(entity, new VelocityComponent());
            frame.Add(entity, new MovementComponent { MoveSpeed = FP64.FromInt(5), IsMoving = false });

            var runner = new SystemRunner();
            runner.AddSystem(new CommandSystem(), SystemPhase.PreUpdate);
            runner.AddSystem(new MovementSystem(), SystemPhase.Update);

            // Apply command via runner
            var target = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.FromInt(30));
            runner.RunCommandSystems(ref frame, new MoveCommand(1, 0, target));

            // Run update systems
            runner.RunUpdateSystems(ref frame);

            ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
            ref readonly var movement = ref frame.GetReadOnly<MovementComponent>(entity);

            Assert.IsTrue(movement.IsMoving);
            Assert.IsTrue(transform.Position.x > FP64.Zero || transform.Position.z > FP64.Zero,
                "Entity should have moved after command + movement system");
        }
    }
}
