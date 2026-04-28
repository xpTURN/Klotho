using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Deterministic.Random;
using System.Collections.Generic;

namespace Brawler
{
    public static class BrawlerSimSetup
    {

        const ulong BotSpawnFeatureKey = 0x424F5453504157EDUL; // "BOTSPAWN"

        /// <summary>
        /// Creates the initial world entities at game start.
        /// Since _frame.Clear() is invoked after engine.Initialize(),
        /// this must be called from OnGameStart(), not RegisterSystems().
        /// </summary>
        public static void InitializeWorldState(xpTURN.Klotho.Core.IKlothoEngine engine,
                                                int maxPlayers = 4,
                                                int botCount = 0)
        {
            // BrawlerSimSetup is an ECS-only sample — the EcsSimulation cast is an intended assumption
            var frame = ((EcsSimulation)engine.Simulation).Frame;

            // Global timer / game-over state singleton
            var timerEntity = frame.CreateEntity();
            frame.Add(timerEntity, new GameTimerStateComponent
            {
                StartTick = -1,
                LastReportedSeconds = -1,
                GameOverFired = false,
            });

            // Global seed singleton
            var seedEntity = frame.CreateEntity();
            frame.Add(seedEntity, new GameSeedComponent { WorldSeed = (ulong)engine.RandomSeed });

            // Moving platform
            var platformEntity = frame.CreateEntity();
            frame.Add(platformEntity, new TransformComponent { Position = new FPVector3(-16.0f, 0.0f, -16.0f) });
            var platformRb = FPRigidBody.CreateKinematic();
            frame.Add(platformEntity, new PhysicsBodyComponent
            {
                RigidBody = platformRb,
                Collider  = FPCollider.FromBox(new FPBoxShape(
                    new FPVector3(FP64.FromDouble(2.0), FP64.FromDouble(0.125), FP64.FromDouble(2.0)), // Half Extents
                    FPVector3.Zero)),
            });
            frame.Add(platformEntity, new PlatformComponent
            {
                IsMoving       = true,
                Waypoint0      = new FPVector3(-16.0f, 0.0f, -16.0f),
                Waypoint1      = new FPVector3(+16.0f, 0.0f, -16.0f),
                Waypoint2      = new FPVector3(+16.0f, 0.0f, +16.0f),
                Waypoint3      = new FPVector3(-16.0f, 0.0f, +16.0f),
                WaypointIndex  = 0,
                MoveSpeed      = FP64.FromDouble(0.1),
                MoveProgress   = FP64.Zero,
            });

            // Bot spawn — place bots beyond the player range
            if (botCount > 0)
                SpawnBots(ref frame, maxPlayers, botCount, (ulong)engine.RandomSeed);
        }

        // PlayerId range invariant.
        //   Real players (P2P): [0, maxPlayers]      — host=0 + guest LateJoin can reach maxPlayers
        //                                              under sparse Pre-GameStart distributions.
        //   Real players (SD):  [1, maxPlayers]      — server has no slot.
        //   Bots:               [maxPlayers+1, ...]  — strictly above the player range, so bot[i] never
        //                                              collides with a LateJoiner that lands on maxPlayers.
        static void SpawnBots(ref Frame frame, int maxPlayers, int botCount, ulong worldSeed)
        {
            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
            var stats  = new CharacterStatsAsset[4];
            for (int i = 0; i < 4; i++)
                stats[i] = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + i);

            var rng = DeterministicRandom.FromSeed(worldSeed, BotSpawnFeatureKey);

            for (int i = 0; i < botCount; i++)
            {
                int botPlayerId = maxPlayers + 1 + i;

                int classIdx = rng.NextIntInclusive(0, stats.Length - 1);
                var entity   = frame.CreateEntity(stats[classIdx].PrototypeId);

                ref var character = ref frame.Get<CharacterComponent>(entity);
                character.PlayerId       = botPlayerId;
                character.StockCount     = 3;

                ref var owner = ref frame.Get<OwnerComponent>(entity);
                owner.OwnerId = botPlayerId;

                ref var transform = ref frame.Get<TransformComponent>(entity);
                transform.Position = rules.SpawnPositions[botPlayerId % rules.SpawnPositions.Length];
                transform.PreviousPosition = transform.Position;
                transform.PreviousRotation = transform.Rotation;

                frame.Add(entity, new BotComponent
                {
                    State      = (byte)BotStateId.Idle,
                    Difficulty = (byte)BotDifficulty.Easy,
                });

                HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);

                var marker = frame.CreateEntity();
                frame.Add(marker, new SpawnMarkerComponent
                {
                    PlayerId      = botPlayerId,
                    SpawnPosition = new FPVector2(transform.Position.x, transform.Position.z),
                });
            }
        }

        public static PhysicsSystem PhysicsSystem { get; private set; }

        public static void RegisterSystems(EcsSimulation simulation, ILogger logger,
                                           List<IDataAsset> dataAssets = null,
                                           List<FPStaticCollider> staticColliders = null,
                                           BotFSMSystem botFSMSystem = null)
        {
            // Register assets
            if (dataAssets != null)
            {
                var registry = (IDataAssetRegistryBuilder)simulation.Frame.AssetRegistry;
                registry.LoadMixedAndRegister(dataAssets);
            }

            // Register prototypes
            simulation.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());
            simulation.Frame.Prototypes.Register(MagePrototype.Id, new MagePrototype());
            simulation.Frame.Prototypes.Register(RoguePrototype.Id, new RoguePrototype());
            simulation.Frame.Prototypes.Register(KnightPrototype.Id, new KnightPrototype());
            simulation.Frame.Prototypes.Register(MovingPlatformPrototype.Id, new MovingPlatformPrototype());
            simulation.Frame.Prototypes.Register(ItemPickupPrototype.Id, new ItemPickupPrototype());

            var events = new EventSystem();
            var platformerCommandSystem = new PlatformerCommandSystem(events);

            if (botFSMSystem != null)
                botFSMSystem.SetCommandSystem(platformerCommandSystem);

            // PreUpdate — save previous state for interpolation, then bots, then command processing
            simulation.AddSystem(new SavePreviousTransformSystem(), SystemPhase.PreUpdate);
            if (botFSMSystem != null)
                simulation.AddSystem(botFSMSystem, SystemPhase.PreUpdate);
            simulation.AddSystem(platformerCommandSystem, SystemPhase.PreUpdate);

            // Update — simulation systems
            simulation.AddSystem(new ObstacleMovementSystem(events), SystemPhase.Update);
            simulation.AddSystem(new TopdownMovementSystem(events), SystemPhase.Update);
            simulation.AddSystem(new ActionLockSystem(), SystemPhase.Update);
            simulation.AddSystem(new KnockbackSystem(events), SystemPhase.Update);
            PhysicsSystem = new PhysicsSystem(256, FPVector3.Zero);
            PhysicsSystem.SetSkipStaticGroundResponse(true);
            if (staticColliders != null)
                PhysicsSystem.LoadStaticColliders("BrawlerScene", staticColliders);
            simulation.AddSystem(PhysicsSystem, SystemPhase.Update);
            platformerCommandSystem.SetRayCaster(PhysicsSystem);
            if (botFSMSystem != null)
                botFSMSystem.SetRayCaster(PhysicsSystem);
            simulation.AddSystem(new TrapTriggerSystem(PhysicsSystem, events), SystemPhase.Update);
            simulation.AddSystem(new SkillCooldownSystem(events), SystemPhase.Update);
            simulation.AddSystem(new BoundaryCheckSystem(events), SystemPhase.Update);
            simulation.AddSystem(new ItemSpawnSystem(events), SystemPhase.Update);
            simulation.AddSystem(new CombatSystem(events), SystemPhase.Update);
            simulation.AddSystem(new RespawnSystem(events), SystemPhase.Update);
            simulation.AddSystem(new TimerSystem(events), SystemPhase.Update);

            // PostUpdate — landing clamp, then game-over detection
            simulation.AddSystem(new GroundClampSystem(PhysicsSystem), SystemPhase.PostUpdate);
            simulation.AddSystem(new GameOverSystem(events), SystemPhase.PostUpdate);

            // LateUpdate — event dispatch
            simulation.AddSystem(events, SystemPhase.LateUpdate);
        }

        /// <summary>
        /// Creates the default DataAsset list for tests. Includes assets required by every system's OnInit/Update.
        /// </summary>
        public static List<IDataAsset> CreateDefaultDataAssets()
        {
            int[] skillIds = { 1200, 1201, 1210, 1211, 1220, 1221, 1230, 1231 };
            var assets = new List<IDataAsset>
            {
                new BrawlerGameRulesAsset(1001),
                new CombatPhysicsAsset(1300),
                new BasicAttackConfigAsset(1301),
                new ItemConfigAsset(1400),
                new MovementPhysicsAsset(1500),
                new BotBehaviorAsset(1600),
                new BotDifficultyAsset(1700),
                new BotDifficultyAsset(1701),
                new BotDifficultyAsset(1702),
            };
            for (int c = 0; c < 4; c++)
            {
                assets.Add(new CharacterStatsAsset(1100 + c)
                {
                    Skill0Id = skillIds[c * 2],
                    Skill1Id = skillIds[c * 2 + 1],
                });
                assets.Add(new SkillConfigAsset(skillIds[c * 2]));
                assets.Add(new SkillConfigAsset(skillIds[c * 2 + 1]));
            }
            return assets;
        }

    }
}
