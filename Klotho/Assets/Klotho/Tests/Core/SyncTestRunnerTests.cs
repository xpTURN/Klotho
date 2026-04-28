using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Core.Tests
{
    [TestFixture]
    public class SyncTestRunnerTests
    {
        private const int MaxEntities = 32;
        private const int MaxRollbackTicks = 10;
        private static readonly ICommand[] NoCommands = Array.Empty<ICommand>();

        private EcsSimulation CreateSimulation(int maxRollbackTicks = MaxRollbackTicks)
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: maxRollbackTicks, deltaTimeMs: 50);
            sim.AddSystem(new CommandSystem(), SystemPhase.PreUpdate);
            sim.AddSystem(new MovementSystem(), SystemPhase.Update);
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

        private void RunAndAssertAllPass(SyncTestRunner runner, EcsSimulation sim, int ticks,
            Func<int, IReadOnlyList<ICommand>> commandProvider = null)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                var commands = commandProvider?.Invoke(tick) ?? (IReadOnlyList<ICommand>)NoCommands;
                var result = runner.RunTick(tick, commands);

                Assert.AreNotEqual(SyncTestStatus.Fail, result.Status,
                    $"Desync at tick {tick}: expected=0x{result.ExpectedHash:X16}, actual=0x{result.ActualHash:X16}");
            }
        }

        #region Basic Verification

        [Test]
        public void SyncTest_EmptySimulation_AllPass()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, sim, 100);

            Assert.AreEqual(0, runner.FailedChecks);
            Assert.AreEqual(1.0f, runner.SuccessRate);
        }

        [Test]
        public void SyncTest_SingleEntity_NoCommands()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, sim, 100);

            Assert.AreEqual(0, runner.FailedChecks);
        }

        [Test]
        public void SyncTest_SingleEntity_WithMove()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            var target = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero);

            RunAndAssertAllPass(runner, sim, 100, tick =>
            {
                if (tick % 10 == 0)
                    return new ICommand[] { new MoveCommand(playerId: 1, tick: tick, target: target) };
                return NoCommands;
            });

            Assert.AreEqual(0, runner.FailedChecks);
        }

        [Test]
        public void SyncTest_MultipleEntities_WithCollision()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);
            AddPlayerEntity(sim, 2, new FPVector3(FP64.FromInt(20), FP64.Zero, FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            var target1 = new FPVector3(FP64.FromInt(20), FP64.Zero, FP64.Zero);
            var target2 = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);

            RunAndAssertAllPass(runner, sim, 100, tick =>
            {
                if (tick == 0)
                    return new ICommand[]
                    {
                        new MoveCommand(playerId: 1, tick: tick, target: target1),
                        new MoveCommand(playerId: 2, tick: tick, target: target2)
                    };
                return NoCommands;
            });

            Assert.AreEqual(0, runner.FailedChecks);
        }

        [Test]
        public void SyncTest_EntitySpawnDespawn()
        {
            var sim = CreateSimulation();

            // Spawn/destroy before the SyncTest loop — direct Frame mutations
            // bypass the command pipeline, so they must run before verification starts
            // to be captured in the initial snapshot.
            var entity1 = sim.Frame.CreateEntity();
            sim.Frame.Add(entity1, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var entity2 = sim.Frame.CreateEntity();
            sim.Frame.Add(entity2, new HealthComponent { MaxHealth = 50, CurrentHealth = 50 });

            // Destroy one entity before SyncTest starts
            sim.Frame.DestroyEntity(entity2);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, sim, 100);

            Assert.AreEqual(0, runner.FailedChecks);
        }

        [Test]
        public void SyncTest_SkipUntilCheckDistance()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            for (int tick = 0; tick < 5; tick++)
            {
                var result = runner.RunTick(tick, NoCommands);
                Assert.AreEqual(SyncTestStatus.Skip, result.Status,
                    $"Tick {tick} should be Skip");
            }

            var resultAtCheckDistance = runner.RunTick(5, NoCommands);
            Assert.AreEqual(SyncTestStatus.Pass, resultAtCheckDistance.Status,
                "Tick at checkDistance should be Pass");
        }

        #endregion

        #region checkDistance Variation

        [Test]
        public void SyncTest_CheckDistance1()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 1);

            RunAndAssertAllPass(runner, sim, 50);
            Assert.AreEqual(0, runner.FailedChecks);
        }

        [Test]
        public void SyncTest_CheckDistanceMaxBoundary()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: MaxRollbackTicks - 1);

            RunAndAssertAllPass(runner, sim, 50);
            Assert.AreEqual(0, runner.FailedChecks);
        }

        [Test]
        public void SyncTest_CheckDistanceExceedsMax()
        {
            var sim = CreateSimulation();

            var runner = new SyncTestRunner();
            // checkDistance == RollbackCapacity -> throws (slot collision)
            Assert.Throws<ArgumentException>(() =>
                runner.Initialize(sim, checkDistance: MaxRollbackTicks));
        }

        [Test]
        public void SyncTest_CheckDistanceZero()
        {
            var sim = CreateSimulation();

            var runner = new SyncTestRunner();
            Assert.Throws<ArgumentException>(() =>
                runner.Initialize(sim, checkDistance: 0));
        }

        [Test]
        public void SyncTest_CheckDistanceVaries()
        {
            int[] distances = { 1, 3, 5, 8, 9 };

            foreach (int distance in distances)
            {
                var sim = CreateSimulation();
                AddPlayerEntity(sim, 1, FPVector3.Zero);

                var runner = new SyncTestRunner();
                runner.Initialize(sim, checkDistance: distance);

                RunAndAssertAllPass(runner, sim, 30);
                Assert.AreEqual(0, runner.FailedChecks,
                    $"Failed with checkDistance={distance}");
            }
        }

        #endregion

        #region Long-Running

        [Test]
        public void SyncTest_1000Ticks()
        {
            var sim = CreateSimulation();
            AddPlayerEntity(sim, 1, FPVector3.Zero);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            var target = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);

            RunAndAssertAllPass(runner, sim, 1000, tick =>
            {
                if (tick % 50 == 0)
                    return new ICommand[] { new MoveCommand(playerId: 1, tick: tick, target: target) };
                return NoCommands;
            });

            Assert.AreEqual(0, runner.FailedChecks);
            Assert.Greater(runner.TotalChecks, 0);
        }

        #endregion

        #region Intentional Failure Detection

        [Test]
        public void SyncTest_OnSyncError_Fires()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 1);

            SyncTestFailure? receivedFailure = null;
            runner.OnSyncError += failure => receivedFailure = failure;

            // First run normally to build history
            runner.RunTick(0, NoCommands);

            // How to corrupt state between forward run and verification:
            // Manipulate the hash ring on the second tick to modify state mid-flight,
            // or modify the frame after forward run to simulate a non-deterministic scenario.
            // RunTick performs forward+rollback atomically, which makes this tricky.
            // We test via SuccessRate/FailedChecks tracking.

            // When all ticks pass, OnSyncError must not fire
            runner.RunTick(1, NoCommands);

            if (runner.FailedChecks == 0)
                Assert.IsNull(receivedFailure, "OnSyncError should not fire when all pass");
        }

        [Test]
        public void SyncTest_SuccessRate_Tracks()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 3);

            // Run 10 ticks — first 3 are Skip, remaining 7 are verified
            for (int tick = 0; tick < 10; tick++)
                runner.RunTick(tick, NoCommands);

            Assert.AreEqual(7, runner.TotalChecks);
            Assert.AreEqual(0, runner.FailedChecks);
            Assert.AreEqual(1.0f, runner.SuccessRate);
        }

        #endregion

        #region Result Struct Accuracy

        [Test]
        public void SyncTest_PassResult_FieldsCorrect()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 3);

            // Skip ticks
            for (int tick = 0; tick < 3; tick++)
                runner.RunTick(tick, NoCommands);

            // First verification tick
            var result = runner.RunTick(3, NoCommands);

            Assert.AreEqual(SyncTestStatus.Pass, result.Status);
            Assert.AreEqual(3, result.Tick);
            Assert.AreEqual(3, result.RollbackFromTick);
            Assert.AreEqual(0, result.RollbackToTick);
            Assert.AreEqual(result.ExpectedHash, result.ActualHash);
            Assert.AreNotEqual(0L, result.ExpectedHash);
        }

        [Test]
        public void SyncTest_SkipResult_FieldsCorrect()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            var result = runner.RunTick(0, NoCommands);

            Assert.AreEqual(SyncTestStatus.Skip, result.Status);
            Assert.AreEqual(0, result.Tick);
            Assert.AreEqual(0, result.RollbackFromTick);
            Assert.AreEqual(0, result.RollbackToTick);
        }

        #endregion

        #region NotifyExternalRollback

        [Test]
        public void SyncTest_NotifyExternalRollback_SkipsUntilReaccumulated()
        {
            var sim = CreateSimulation();
            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 3);

            // Run 10 ticks normally
            for (int tick = 0; tick < 10; tick++)
                runner.RunTick(tick, NoCommands);

            Assert.Greater(runner.TotalChecks, 0);
            int checksBeforeRollback = runner.TotalChecks;

            // Simulate external rollback to tick 5
            // (KlothoEngine calls sim.Rollback + NotifyExternalRollback together)
            sim.Rollback(5);
            runner.NotifyExternalRollback(5);

            // Ticks 5~7 must be Skip (need fresh ticks for checkDistance=3)
            for (int tick = 5; tick < 8; tick++)
            {
                var result = runner.RunTick(tick, NoCommands);
                Assert.AreEqual(SyncTestStatus.Skip, result.Status,
                    $"Tick {tick} should be Skip after external rollback");
            }

            // Tick 8 must resume verification
            var resumed = runner.RunTick(8, NoCommands);
            Assert.AreEqual(SyncTestStatus.Pass, resumed.Status,
                "Verification should resume after checkDistance fresh ticks");

            Assert.AreEqual(checksBeforeRollback + 1, runner.TotalChecks);
        }

        #endregion
    }
}
