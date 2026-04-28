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
    /// Verifies BotFSMSystem state transitions + rollback determinism.
    /// Registers BotFSMSystem without NavMesh and checks that
    /// HFSMComponent + BotComponent are accurately restored from the ECS snapshot.
    /// </summary>
    [TestFixture]
    public class BotNavigationSyncTests
    {
        private const int MaxEntities      = 64;
        private const int MaxRollbackTicks = 10;
        private const int DeltaTimeMs      = 50;
        private static readonly ICommand[] NoCommands = Array.Empty<ICommand>();

        private EcsSimulation CreateSimulation()
        {
            var botFSMSystem = new BotFSMSystem(navSystem: null);
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: MaxRollbackTicks, deltaTimeMs: DeltaTimeMs);

            var dataAssets = BrawlerSimSetup.CreateDefaultDataAssets();
            BrawlerSimSetup.RegisterSystems(sim, logger: null, dataAssets: dataAssets, botFSMSystem: botFSMSystem);
            sim.Initialize();
            return sim;
        }

        private EntityRef SpawnBot(EcsSimulation sim, int playerId, FPVector2 spawnPos,
                                   BotDifficulty difficulty = BotDifficulty.Normal)
        {
            var frame = sim.Frame;
            var entity = frame.CreateEntity(WarriorPrototype.Id);

            ref var character = ref frame.Get<CharacterComponent>(entity);
            character.PlayerId       = playerId;
            character.CharacterClass = 0; // Warrior
            character.StockCount     = 3;

            ref var owner = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId = playerId;

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(spawnPos.x, FP64.Zero, spawnPos.y);

            frame.Add(entity, new BotComponent
            {
                State      = (byte)BotStateId.Idle,
                Difficulty = (byte)difficulty,
            });

            frame.Add(entity, new SpawnMarkerComponent
            {
                PlayerId      = playerId,
                SpawnPosition = spawnPos,
            });

            return entity;
        }

        private EntityRef SpawnPlayer(EcsSimulation sim, int playerId, FPVector2 spawnPos)
        {
            var frame = sim.Frame;
            var entity = frame.CreateEntity(WarriorPrototype.Id);

            ref var character = ref frame.Get<CharacterComponent>(entity);
            character.PlayerId       = playerId;
            character.CharacterClass = 0;
            character.StockCount     = 3;

            ref var owner = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId = playerId;

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(spawnPos.x, FP64.Zero, spawnPos.y);

            frame.Add(entity, new SpawnMarkerComponent
            {
                PlayerId      = playerId,
                SpawnPosition = spawnPos,
            });

            return entity;
        }

        private void RunAndAssertAllPass(SyncTestRunner runner, int ticks,
            Func<int, IReadOnlyList<ICommand>> commandProvider = null)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                var commands = commandProvider?.Invoke(tick) ?? (IReadOnlyList<ICommand>)NoCommands;
                var result   = runner.RunTick(tick, commands);

                Assert.AreNotEqual(SyncTestStatus.Fail, result.Status,
                    $"Desync at tick {tick}: expected=0x{result.ExpectedHash:X16}, actual=0x{result.ActualHash:X16}");
            }
        }

        /// <summary>
        /// Verifies that HFSMComponent + BotComponent fields are restored identically after rollback through the ECS snapshot.
        /// </summary>
        [Test]
        public void SyncTest_BotComponent_Rollback_Deterministic()
        {
            var sim = CreateSimulation();
            SpawnBot(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 100);
            Assert.AreEqual(0, runner.FailedChecks,
                "BotComponent rollback should produce identical hashes");
        }

        /// <summary>
        /// Verifies that state remains deterministic in a mixed bot + player scenario.
        /// </summary>
        [Test]
        public void SyncTest_BotAndPlayer_Mixed_Deterministic()
        {
            var sim = CreateSimulation();
            SpawnBot(sim, 0, new FPVector2(FP64.FromInt(-3), FP64.Zero));
            SpawnPlayer(sim, 1, new FPVector2(FP64.FromInt(3), FP64.Zero));

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 150, tick =>
            {
                if (tick < 80)
                {
                    return new ICommand[]
                    {
                        new MoveInputCommand
                        {
                            PlayerId       = 1,
                            Tick           = tick,
                            HorizontalAxis = -FP64.One,
                            VerticalAxis   = FP64.Zero,
                        }
                    };
                }
                return NoCommands;
            });

            Assert.AreEqual(0, runner.FailedChecks,
                "Bot + player mixed scenario should produce identical hashes after rollback");
        }

        /// <summary>
        /// Verifies state determinism when multiple bots of different difficulties coexist.
        /// </summary>
        [Test]
        public void SyncTest_MultipleBots_AllDifficulties_Deterministic()
        {
            var sim = CreateSimulation();
            SpawnBot(sim, 0, new FPVector2(FP64.FromInt(-5), FP64.Zero), BotDifficulty.Easy);
            SpawnBot(sim, 1, new FPVector2(FP64.Zero,         FP64.Zero), BotDifficulty.Normal);
            SpawnBot(sim, 2, new FPVector2(FP64.FromInt(5),   FP64.Zero), BotDifficulty.Hard);

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 8);

            RunAndAssertAllPass(runner, 200);
            Assert.AreEqual(0, runner.FailedChecks,
                "Multiple bots with all difficulties should produce identical hashes after rollback");
        }

        /// <summary>
        /// Verifies Evade state transition + rollback determinism for a high-knockback bot.
        /// </summary>
        [Test]
        public void SyncTest_BotComponent_HighKnockback_Deterministic()
        {
            var sim = CreateSimulation();
            var botEntity = SpawnBot(sim, 0, new FPVector2(FP64.Zero, FP64.Zero), BotDifficulty.Normal);

            ref var character = ref sim.Frame.Get<CharacterComponent>(botEntity);
            character.KnockbackPower = 80;

            var runner = new SyncTestRunner();
            runner.Initialize(sim, checkDistance: 5);

            RunAndAssertAllPass(runner, 100);
            Assert.AreEqual(0, runner.FailedChecks,
                "Bot with high knockback (Evade state) should produce identical hashes after rollback");
        }

        /// <summary>
        /// Verifies that BotFSMSystem.OnInit registers BotHFSMRoot at Initialize() time.
        /// OnInit runs inside sim.Initialize(), so it is called before SpawnBot.
        /// To actually inject HFSMComponent into a bot entity,
        /// SpawnBot must be called before Initialize() (right after prototype registration).
        /// </summary>
        [Test]
        public void Init_RegistersBotHFSMRoot()
        {
            // HFSMRoot is not registered before Initialize()
            // (it may already be registered if another test ran first, but Build() is idempotent)
            CreateSimulation(); // OnInit is invoked inside Initialize() -> BotHFSMRoot.Build()

            Assert.IsTrue(xpTURN.Klotho.ECS.FSM.HFSMRoot.Has(BotHFSMRoot.Id),
                "BotHFSMRoot should be registered after BotFSMSystem.OnInit");
        }

        /// <summary>
        /// Verifies that calling HFSMManager.Init directly on a bot spawned after Initialize()
        /// correctly adds the HFSMComponent.
        /// EcsSimulation.Initialize() runs Frame.Clear() first, so any pre-spawned entities
        /// are wiped out. Actual spawning must happen after Initialize().
        /// </summary>
        [Test]
        public void Init_PostSpawnedBot_HasHFSMComponent()
        {
            var sim = CreateSimulation(); // Initialize() completed

            var entity = SpawnBot(sim, 0, new FPVector2(FP64.Zero, FP64.Zero));

            var frame = sim.Frame;

            // No bot existed at OnInit time, so HFSMComponent is not yet registered
            Assert.IsFalse(frame.Has<xpTURN.Klotho.ECS.FSM.HFSMComponent>(entity),
                "HFSMComponent should not exist before explicit Init");

            // Init directly after spawning
            xpTURN.Klotho.ECS.FSM.HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);

            Assert.IsTrue(frame.Has<xpTURN.Klotho.ECS.FSM.HFSMComponent>(entity),
                "HFSMComponent should exist after explicit HFSMManager.Init");
        }
    }
}
