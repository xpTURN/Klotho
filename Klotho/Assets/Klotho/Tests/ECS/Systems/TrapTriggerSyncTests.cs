using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using Brawler;

namespace xpTURN.Klotho.Tests
{
    /// <summary>
    /// Uses SyncTestRunner to verify determinism of trap triggers + rollback.
    /// Checks that hashes match after rollback across movement patterns where a character passes through a trap area.
    ///</summary>
    [TestFixture]
    public class TrapTriggerSyncTests
    {
        private const int MaxEntities = 64;
        private const int MaxRollbackTicks = 10;
        private const int DeltaTimeMs = 50;
        private static readonly ICommand[] NoCommands = Array.Empty<ICommand>();

        private EcsSimulation CreateBrawlerSimulation()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: MaxRollbackTicks, deltaTimeMs: DeltaTimeMs);
            BrawlerSimSetup.RegisterSystems(sim, null, dataAssets: BrawlerSimSetup.CreateDefaultDataAssets());
            sim.Initialize();
            return sim;
        }

        private EntityRef SpawnWarrior(EcsSimulation sim, int playerId, FPVector2 spawnPos)
        {
            var frame = sim.Frame;
            var entity = frame.CreateEntity(WarriorPrototype.Id);

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(spawnPos.x, FP64.Zero, spawnPos.y);

            ref var owner = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId = playerId;

            frame.Add(entity, new SpawnMarkerComponent
            {
                SpawnPosition = spawnPos,
                PlayerId = playerId,
            });

            return entity;
        }

        private void LoadTrapColliders(PhysicsSystem physics)
        {
            // TrapZone_East (6, 0, 0) — Box 3×2×3, isTrigger=true
            // TrapZone_West (-6, 0, 0) — Box 3×2×3, isTrigger=true
            var trapEast = new FPStaticCollider
            {
                id = 100,
                isTrigger = true,
                collider = FPCollider.FromBox(new FPBoxShape(
                    new FPVector3(FP64.FromDouble(1.5), FP64.One, FP64.FromDouble(1.5)),
                    new FPVector3(FP64.FromInt(6), FP64.One, FP64.Zero))),
            };
            var trapWest = new FPStaticCollider
            {
                id = 101,
                isTrigger = true,
                collider = FPCollider.FromBox(new FPBoxShape(
                    new FPVector3(FP64.FromDouble(1.5), FP64.One, FP64.FromDouble(1.5)),
                    new FPVector3(FP64.FromInt(-6), FP64.One, FP64.Zero))),
            };

            var colliders = new[] { trapEast, trapWest };
            physics.LoadStaticColliders("TrapTest", colliders, colliders.Length);
        }

        private void RunAndAssertAllPass(SyncTestRunner runner, int ticks,
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

        /// <summary>
        /// Character moves into the trap area and enters → knockback occurs.
        /// Validates that hashes match after rollback and re-simulation.
        ///</summary>
        [Test]
        public void SyncTest_TrapTrigger_MoveThroughTrap_Deterministic()
        {
            var sim = CreateBrawlerSimulation();
            LoadTrapColliders(BrawlerSimSetup.PhysicsSystem);

            // Spawn character near the trap (X=4 → moving toward East trap at X=6)
            SpawnWarrior(sim, 0, new FPVector2(FP64.FromInt(4), FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            // Move right → enter trap
            RunAndAssertAllPass(runner, 200, tick =>
            {
                if (tick < 100)
                {
                    return new ICommand[]
                    {
                        new MoveInputCommand
                        {
                            PlayerId = 0,
                            Tick = tick,
                            HorizontalAxis = FP64.One,  // right
                            VerticalAxis = FP64.Zero,
                        }
                    };
                }
                return NoCommands;
            });

            Assert.AreEqual(0, runner.FailedChecks,
                "Trap trigger + rollback should produce identical hashes");
        }

        /// <summary>
        /// Scenario where two characters each enter a different trap.
        ///</summary>
        [Test]
        public void SyncTest_TrapTrigger_TwoPlayers_BothTraps()
        {
            var sim = CreateBrawlerSimulation();
            LoadTrapColliders(BrawlerSimSetup.PhysicsSystem);

            SpawnWarrior(sim, 0, new FPVector2(FP64.FromInt(4), FP64.Zero));
            SpawnWarrior(sim, 1, new FPVector2(FP64.FromInt(-4), FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 200, tick =>
            {
                if (tick < 100)
                {
                    return new ICommand[]
                    {
                        new MoveInputCommand
                        {
                            PlayerId = 0,
                            Tick = tick,
                            HorizontalAxis = FP64.One,   // → East trap
                            VerticalAxis = FP64.Zero,
                        },
                        new MoveInputCommand
                        {
                            PlayerId = 1,
                            Tick = tick,
                            HorizontalAxis = -FP64.One,  // ← West trap
                            VerticalAxis = FP64.Zero,
                        }
                    };
                }
                return NoCommands;
            });

            Assert.AreEqual(0, runner.FailedChecks);
        }

        /// <summary>
        /// Direction reversal immediately after entering the trap — fast Enter→Exit scenario.
        ///</summary>
        [Test]
        public void SyncTest_TrapTrigger_QuickEnterExit()
        {
            var sim = CreateBrawlerSimulation();
            LoadTrapColliders(BrawlerSimSetup.PhysicsSystem);

            SpawnWarrior(sim, 0, new FPVector2(FP64.FromInt(4), FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 200, tick =>
            {
                // 30 ticks right → enter trap, then immediately left → exit trap
                FP64 horizontal = tick < 30 ? FP64.One : -FP64.One;
                if (tick >= 60) horizontal = FP64.Zero;  // stop

                return new ICommand[]
                {
                    new MoveInputCommand
                    {
                        PlayerId = 0,
                        Tick = tick,
                        HorizontalAxis = horizontal,
                        VerticalAxis = FP64.Zero,
                    }
                };
            });

            Assert.AreEqual(0, runner.FailedChecks);
        }
    }
}
