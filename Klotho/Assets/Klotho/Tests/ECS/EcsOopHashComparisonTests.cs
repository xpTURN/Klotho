using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// Validates snapshot/rollback determinism of EcsSimulation.
    /// Runs the same scenario on two EcsSimulation instances in parallel and checks that hashes match every tick.
    /// </summary>
    [TestFixture]
    public class EcsOopHashComparisonTests
    {
        private const int MaxEntities = 32;
        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private EcsSimulation CreateSimulation()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            sim.AddSystem(new MovementSystem(), SystemPhase.Update);
            sim.AddSystem(new CommandSystem(), SystemPhase.PreUpdate);
            sim.Initialize();
            return sim;
        }

        private EntityRef AddPlayerEntity(EcsSimulation sim, int ownerId, FPVector3 position)
        {
            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new TransformComponent
            {
                Position = position,
                Scale = FPVector3.One
            });
            sim.Frame.Add(entity, new OwnerComponent { OwnerId = ownerId });
            sim.Frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            sim.Frame.Add(entity, new VelocityComponent());
            sim.Frame.Add(entity, new MovementComponent { MoveSpeed = FP64.FromInt(5) });
            return entity;
        }

        // --- Determinism verification ---

        [Test]
        public void TwoInstances_SameSetup_SameHashAtEveryTick()
        {
            var sim1 = CreateSimulation();
            var sim2 = CreateSimulation();

            AddPlayerEntity(sim1, 1, FPVector3.Zero);
            AddPlayerEntity(sim2, 1, FPVector3.Zero);

            for (int tick = 0; tick < 10; tick++)
            {
                sim1.Tick(NoCommands);
                sim2.Tick(NoCommands);

                Assert.AreEqual(
                    sim1.GetStateHash(),
                    sim2.GetStateHash(),
                    $"Hash mismatch at tick {tick + 1}");
            }
        }

        [Test]
        public void TwoInstances_SameMoveCommand_SameHash()
        {
            var sim1 = CreateSimulation();
            var sim2 = CreateSimulation();

            AddPlayerEntity(sim1, 1, FPVector3.Zero);
            AddPlayerEntity(sim2, 1, FPVector3.Zero);

            var target = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            var cmd = new MoveCommand(playerId: 1, tick: 0, target: target);

            sim1.Tick(new List<ICommand> { cmd });
            sim2.Tick(new List<ICommand> { cmd });

            Assert.AreEqual(sim1.GetStateHash(), sim2.GetStateHash());

            for (int i = 0; i < 5; i++)
            {
                sim1.Tick(NoCommands);
                sim2.Tick(NoCommands);
                Assert.AreEqual(sim1.GetStateHash(), sim2.GetStateHash(),
                    $"Hash mismatch at tick {i + 2}");
            }
        }

        // --- Snapshot/rollback determinism ---

        [Test]
        public void Rollback_AndResimulate_ProducesSameHash()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            // Save tick 0 snapshot
            sim.SaveSnapshot();
            long hashAtTick0 = sim.GetStateHash();

            // Advance 5 ticks
            for (int i = 0; i < 5; i++)
                sim.Tick(NoCommands);

            long hashAtTick5 = sim.GetStateHash();
            Assert.AreNotEqual(hashAtTick0, hashAtTick5, "State should have changed after 5 ticks");

            // Rollback to tick 0
            sim.Rollback(0);
            Assert.AreEqual(hashAtTick0, sim.GetStateHash(), "Hash after rollback should match tick 0");

            // Re-simulate 5 ticks with the same inputs
            for (int i = 0; i < 5; i++)
                sim.Tick(NoCommands);

            Assert.AreEqual(hashAtTick5, sim.GetStateHash(),
                "Hash after re-simulation should match original tick 5");
        }

        [Test]
        public void MultipleRollbacks_ProduceDeterministicResult()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            // Advance 3 ticks saving a snapshot each time
            for (int i = 0; i < 3; i++)
            {
                sim.SaveSnapshot();
                sim.Tick(NoCommands);
            }

            long hashAtTick3 = sim.GetStateHash();

            // Rollback to tick 1, then re-simulate 2 ticks
            sim.Rollback(1);
            sim.Tick(NoCommands);
            sim.Tick(NoCommands);

            Assert.AreEqual(hashAtTick3, sim.GetStateHash(),
                "Re-simulation from tick 1 should produce same hash as tick 3");
        }

        // --- 100-tick + rollback scenario ---

        [Test]
        public void HundredTicks_WithRollback_DeterministicHash()
        {
            // maxRollbackTicks=8: FrameRingBuffer capacity=8, slots stored at tick%8.
            // Rollback must target a snapshot tick within the capacity range (8 ticks).
            const int totalTicks = 50;
            const int snapshotInterval = 8;

            var sim1 = CreateSimulation();
            var sim2 = CreateSimulation();

            AddPlayerEntity(sim1, 1, FPVector3.Zero);
            AddPlayerEntity(sim2, 1, FPVector3.Zero);

            var target = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero);

            // Save snapshot before each tick, then advance
            int lastSnapshotTick = 0;
            for (int tick = 0; tick < totalTicks; tick++)
            {
                var commands = new List<ICommand>(NoCommands);
                if (tick % 10 == 0)
                {
                    var cmd = new MoveCommand(playerId: 1, tick: tick, target: target);
                    commands.Add(cmd);
                }

                if (tick % snapshotInterval == 0)
                {
                    sim1.SaveSnapshot();
                    sim2.SaveSnapshot();
                    lastSnapshotTick = tick;
                }

                sim1.Tick(commands);
                sim2.Tick(commands);

                Assert.AreEqual(sim1.GetStateHash(), sim2.GetStateHash(),
                    $"Determinism broken at tick {tick + 1}");
            }

            long sim2HashAtEnd = sim2.GetStateHash();

            // Rollback to the last snapshot tick within capacity range, then re-simulate
            // lastSnapshotTick < totalTicks, so re-simulate only totalTicks - lastSnapshotTick ticks
            sim1.Rollback(lastSnapshotTick);

            for (int tick = lastSnapshotTick; tick < totalTicks; tick++)
            {
                var commands = new List<ICommand>(NoCommands);
                if (tick % 10 == 0)
                {
                    var cmd = new MoveCommand(playerId: 1, tick: tick, target: target);
                    commands.Add(cmd);
                }
                sim1.Tick(commands);
            }

            Assert.AreEqual(sim2HashAtEnd, sim1.GetStateHash(),
                "Hash after rollback+resim should match unrolled simulation");
        }
    }
}
