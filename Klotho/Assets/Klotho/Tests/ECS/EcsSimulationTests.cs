using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class EcsSimulationTests
    {
        private const int MaxEntities = 32;
        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private EcsSimulation CreateSimulation()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            sim.Initialize();
            return sim;
        }

        // --- Basic behavior ---

        [Test]
        public void Initialize_TickIsZero()
        {
            var sim = CreateSimulation();
            Assert.AreEqual(0, sim.CurrentTick);
        }

        [Test]
        public void Tick_IncrementsTick()
        {
            var sim = CreateSimulation();
            sim.Tick(NoCommands);
            Assert.AreEqual(1, sim.CurrentTick);
            sim.Tick(NoCommands);
            Assert.AreEqual(2, sim.CurrentTick);
        }

        [Test]
        public void Reset_ResetsTickToZero()
        {
            var sim = CreateSimulation();
            sim.Tick(NoCommands);
            sim.Tick(NoCommands);
            sim.Reset();
            Assert.AreEqual(0, sim.CurrentTick);
        }

        // --- System execution ---

        [Test]
        public void AddSystem_SystemRunsDuringTick()
        {
            var sim = CreateSimulation();

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new MovementComponent
            {
                MoveSpeed = FP64.FromInt(5),
                TargetPosition = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero),
                IsMoving = true
            });
            sim.Frame.Add(entity, new TransformComponent
            {
                Position = FPVector3.Zero,
                Scale = FPVector3.One
            });
            sim.Frame.Add(entity, new VelocityComponent());

            sim.AddSystem(new MovementSystem(), SystemPhase.Update);

            sim.Tick(NoCommands);

            ref readonly var transform = ref sim.Frame.GetReadOnly<TransformComponent>(entity);
            Assert.Greater(transform.Position.x.RawValue, 0L, "Entity should have moved");
        }

        [Test]
        public void CommandSystem_AppliesCommandToFrame()
        {
            var sim = CreateSimulation();

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new OwnerComponent { OwnerId = 1 });
            sim.Frame.Add(entity, new MovementComponent { MoveSpeed = FP64.FromInt(5) });
            sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });

            sim.AddSystem(new CommandSystem(), SystemPhase.PreUpdate);

            var target = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            var command = new MoveCommand(playerId: 1, tick: 0, target: target);
            sim.Tick(new List<ICommand> { command });

            ref readonly var movement = ref sim.Frame.GetReadOnly<MovementComponent>(entity);
            Assert.IsTrue(movement.IsMoving, "Movement should be active after MoveCommand");
            Assert.AreEqual(target.x.RawValue, movement.TargetPosition.x.RawValue);
        }

        // --- GetStateHash ---

        [Test]
        public void GetStateHash_SameState_SameHash()
        {
            var sim1 = CreateSimulation();
            var sim2 = CreateSimulation();

            sim1.Frame.Add(sim1.Frame.CreateEntity(), new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            sim2.Frame.Add(sim2.Frame.CreateEntity(), new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });

            Assert.AreEqual(sim1.GetStateHash(), sim2.GetStateHash());
        }

        [Test]
        public void GetStateHash_DifferentState_DifferentHash()
        {
            var sim1 = CreateSimulation();
            var sim2 = CreateSimulation();

            sim1.Frame.Add(sim1.Frame.CreateEntity(), new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            sim2.Frame.Add(sim2.Frame.CreateEntity(), new HealthComponent { MaxHealth = 100, CurrentHealth = 50 });

            Assert.AreNotEqual(sim1.GetStateHash(), sim2.GetStateHash());
        }

        // --- Rollback ---

        [Test]
        public void Rollback_RestoresPreviousState()
        {
            var sim = CreateSimulation();

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            // Save Tick 0
            sim.SaveSnapshot();
            sim.Tick(NoCommands);

            // Reduce health
            sim.Frame.Get<HealthComponent>(entity).CurrentHealth = 50;
            sim.Tick(NoCommands);

            Assert.AreEqual(2, sim.CurrentTick);
            Assert.AreEqual(50, sim.Frame.GetReadOnly<HealthComponent>(entity).CurrentHealth);

            // Rollback to Tick 0
            sim.Rollback(0);

            Assert.AreEqual(0, sim.CurrentTick);
            Assert.AreEqual(100, sim.Frame.GetReadOnly<HealthComponent>(entity).CurrentHealth);
        }

    }
}
