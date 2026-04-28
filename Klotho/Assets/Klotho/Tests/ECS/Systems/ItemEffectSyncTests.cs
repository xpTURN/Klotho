using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using Brawler;

namespace xpTURN.Klotho.Tests
{
    /// <summary>
    /// Uses SyncTestRunner to verify determinism of item effects (Shield/Boost/Bomb) on rollback.
    /// </summary>
    [TestFixture]
    public class ItemEffectSyncTests
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

        private void SpawnGameSeed(EcsSimulation sim, ulong worldSeed = 12345)
        {
            var frame = sim.Frame;
            var entity = frame.CreateEntity();
            frame.Add(entity, new GameSeedComponent { WorldSeed = worldSeed });
        }

        private EntityRef SpawnItem(EcsSimulation sim, int itemType, FPVector2 pos)
        {
            var frame = sim.Frame;
            var entity = frame.CreateEntity(ItemPickupPrototype.Id);

            ref var item = ref frame.Get<ItemComponent>(entity);
            item.ItemType = itemType;
            item.RemainingTicks = 600;
            item.EntityId = entity.ToId();

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(pos.x, FP64.Zero, pos.y);

            return entity;
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
        /// Verifies no knockback occurs when attacked after picking up the Shield item.
        /// </summary>
        [Test]
        public void SyncTest_ShieldPickup_BlocksKnockback()
        {
            var sim = CreateBrawlerSimulation();
            SpawnGameSeed(sim);

            // Player 0: spawn at the Shield item's position
            SpawnWarrior(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));
            // Player 1: spawn nearby (triggers contact knockback)
            SpawnWarrior(sim, 1, new FPVector2(FP64.FromDouble(0.8), FP64.Zero));
            // Place the Shield item at Player 0's position
            SpawnItem(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            // Run 100 ticks: Player 0 picks up Shield -> contact knockback nullified
            RunAndAssertAllPass(runner, 100);

            Assert.AreEqual(0, runner.FailedChecks,
                "Shield pickup + contact knockback should be deterministic");
        }

        /// <summary>
        /// Verifies increased movement distance after picking up the Boost item.
        /// </summary>
        [Test]
        public void SyncTest_BoostPickup_IncreasesSpeed()
        {
            var sim = CreateBrawlerSimulation();
            SpawnGameSeed(sim);

            SpawnWarrior(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));
            SpawnItem(sim, 1, new FPVector2(FP64.Zero, FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 200, tick =>
            {
                return new ICommand[]
                {
                    new MoveInputCommand
                    {
                        PlayerId = 0,
                        Tick = tick,
                        HorizontalAxis = FP64.One,
                        VerticalAxis = FP64.Zero,
                    }
                };
            });

            Assert.AreEqual(0, runner.FailedChecks,
                "Boost pickup + movement should be deterministic");
        }

        /// <summary>
        /// Verifies knockback applied to nearby characters when the Bomb item is picked up.
        /// </summary>
        [Test]
        public void SyncTest_BombPickup_KnockbackNearby()
        {
            var sim = CreateBrawlerSimulation();
            SpawnGameSeed(sim);

            // Player 0: spawn at the Bomb's position
            SpawnWarrior(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));
            // Player 1: within Bomb radius (under 3m)
            SpawnWarrior(sim, 1, new FPVector2(FP64.FromInt(2), FP64.Zero));
            // Bomb item
            SpawnItem(sim, 2, new FPVector2(FP64.Zero, FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 100);

            Assert.AreEqual(0, runner.FailedChecks,
                "Bomb pickup + area knockback should be deterministic");
        }

        /// <summary>
        /// Verifies knockback nullification for a Shield-active character within the Bomb radius.
        /// </summary>
        [Test]
        public void SyncTest_ShieldBlocksBomb()
        {
            var sim = CreateBrawlerSimulation();
            SpawnGameSeed(sim);

            // Player 0: picks up Shield first
            SpawnWarrior(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));
            SpawnItem(sim, 0, new FPVector2(FP64.Zero, FP64.Zero)); // Shield

            // Player 1: Bomb picker (slightly offset position)
            SpawnWarrior(sim, 1, new FPVector2(FP64.FromInt(2), FP64.Zero));
            SpawnItem(sim, 2, new FPVector2(FP64.FromInt(2), FP64.Zero)); // Bomb

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 150);

            Assert.AreEqual(0, runner.FailedChecks,
                "Shield active + Bomb knockback immunity should be deterministic");
        }
    }
}
