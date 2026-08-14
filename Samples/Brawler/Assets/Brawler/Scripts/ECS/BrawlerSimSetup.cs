using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS.FSM;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.Samples.Identity; // DemoEntitlement, DemoEntitlementData
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
            var frame = engine.InitFrame;

            // Global timer / game-over state singleton
            var timerEntity = frame.CreateEntity();
            frame.Add(timerEntity, new GameTimerStateComponent
            {
                StartTick = -1,
                LastReportedSeconds = -1,
                GameOverFired = false,
            });

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

            // Per-player tick-0 entitlement loadout seed. Runs before SaveSnapshot(0) on every peer (P2P)
            // / on the server (SD, then propagated via the Initial FullState). Deterministic pure decode of
            // each peer's verified entitlement -> fixed-width masks -> LoadoutSeedComponent.
            SeedLoadouts(ref frame, engine);

            // Bot spawn — place bots beyond the player range
            if (botCount > 0)
                SpawnBots(ref frame, maxPlayers, botCount,
                          frame.GetReadOnlySingleton<RandomSeedComponent>().Seed);
        }

        // ── Entitlement loadout seed ───────────────────────────────────────────────────────────
        // The entitlement is decoded via DemoEntitlement.Decode to a DemoEntitlementData; the seed reads
        // OwnedSkillMask (8 bits: classIdx*2+slot) and OwnedConsumableMask directly. A null/empty entitlement
        // decodes to "all owned" (no gating), so with no lobby every player gets the full loadout.
        const int FullConsumableMask = ~0; // bots: every consumable owned (no entitlement)

        static void SeedLoadouts(ref Frame frame, xpTURN.Klotho.Core.IKlothoEngine engine)
        {
            // Seed exactly the authoritative active players — the engine's SessionParticipant slots, written
            // from the synced roster before this callback runs. Keying off those (not a local maxPlayers,
            // which on a non-host peer can be a guess until the authoritative SessionConfig arrives) makes the
            // tick-0 seed-entity set identical on every peer. Collect the ids first, then create, so we never
            // create entities while iterating the participant filter. Bots are not participants → seeded full
            // directly in SpawnBots.
            var participants = new List<int>();
            var filter = frame.Filter<xpTURN.Klotho.ECS.SessionParticipantComponent>();
            while (filter.Next(out var slot))
                participants.Add(frame.GetReadOnly<xpTURN.Klotho.ECS.SessionParticipantComponent>(slot).PlayerId);

            for (int i = 0; i < participants.Count; i++)
                SeedOneLoadout(ref frame, engine, participants[i]);
        }

        /// <summary>
        /// Seeds one player's entitlement loadout — shared by the tick-0 seed (<see cref="SeedLoadouts"/>)
        /// and the late-join seed (<c>BrawlerSimulationCallbacks.OnPlayerJoinedWorld</c>). Deterministic pure
        /// decode of the player's verified entitlement → fixed-width masks → LoadoutSeedComponent. create-iff-
        /// not-exists (defensive: the late-join callback already fires once per join / rollback-safe, and tick-0
        /// ids are distinct — the guard keeps re-entry harmless either way; also preserves the tick-0 output).
        /// </summary>
        public static void SeedOneLoadout(ref Frame frame, xpTURN.Klotho.Core.IKlothoEngine engine, int pid)
        {
            if (HasLoadoutSeed(ref frame, pid))
                return;

            // null/empty entitlement → "all owned" (no gating).
            var ent = DemoEntitlement.Decode(engine.GetPlayerEntitlement(pid));

            var seed = frame.CreateEntity();
            frame.Add(seed, new LoadoutSeedComponent
            {
                PlayerId            = pid,
                OwnedSkillMask      = ent.OwnedSkillMask,
                OwnedConsumableMask = ent.OwnedConsumableMask,
            });
        }

        static bool HasLoadoutSeed(ref Frame frame, int pid)
        {
            var filter = frame.Filter<LoadoutSeedComponent>();
            while (filter.Next(out var e))
                if (frame.GetReadOnly<LoadoutSeedComponent>(e).PlayerId == pid)
                    return true;
            return false;
        }

        // PlayerId range invariant.
        //   Real players (P2P): [0, maxPlayers]      — host=0 + guest LateJoin can reach maxPlayers
        //                                              under sparse Pre-GameStart distributions.
        //   Real players (SD):  [1, maxPlayers]      — server has no slot.
        //   Bots:               [maxPlayers+1, ...]  — strictly above the player range, so bot[i] never
        //                                              collides with a LateJoiner that lands on maxPlayers.
        static void SpawnBots(ref Frame frame, int maxPlayers, int botCount, ulong worldSeed)
        {
            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>();
            var stats  = new CharacterStatsAsset[4];
            for (int i = 0; i < 4; i++)
                stats[i] = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + i);

            var rng = DeterministicRandom.FromSeed(worldSeed, BotSpawnFeatureKey);

            for (int i = 0; i < botCount; i++)
            {
                int botPlayerId = maxPlayers + 1 + i;

                int classIdx = rng.NextIntInclusive(0, stats.Length - 1);
                var spawnPos = rules.SpawnPositions[botPlayerId % rules.SpawnPositions.Length];
                EntityRef entity = classIdx switch
                {
                    0 => frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos }),
                    1 => frame.CreateEntity(new MagePrototype    { SpawnPosition = spawnPos }),
                    2 => frame.CreateEntity(new RoguePrototype   { SpawnPosition = spawnPos }),
                    3 => frame.CreateEntity(new KnightPrototype  { SpawnPosition = spawnPos }),
                    _ => throw new System.ArgumentOutOfRangeException(nameof(classIdx)),
                };

                ref var character = ref frame.Get<CharacterComponent>(entity);
                character.PlayerId       = botPlayerId;
                character.StockCount     = 3;
                // Bots carry no entitlement -> full loadout (never disable bots). 0b11 = both skill slots.
                character.AcquiredSkillMask   = 0b11;
                character.OwnedConsumableMask = FullConsumableMask;

                ref var owner = ref frame.Get<OwnerComponent>(entity);
                owner.OwnerId = botPlayerId;

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
                    SpawnPosition = new FPVector2(spawnPos.x, spawnPos.z),
                });
            }
        }

        public static void RegisterSystems(EcsSimulation simulation, IKLogger logger,
                                           List<IDataAsset> dataAssets = null,
                                           List<FPStaticCollider> staticColliders = null,
                                           BotFSMSystem botFSMSystem = null,
                                           int stageId = 0,
                                           FPNavMeshRebakeContext rebakeContext = null)
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
            // Building command context (null snapshot = placement unavailable on this stage).
            platformerCommandSystem.SetRebakeContext(rebakeContext, botFSMSystem);

            if (botFSMSystem != null)
            {
                botFSMSystem.SetCommandSystem(platformerCommandSystem);
                botFSMSystem.SetShapeExpansion(rebakeContext?.ShapeExpansion);
            }

            // PreUpdate — bots, then command processing.
            // PreviousPosition / PreviousRotation are captured by the engine built-in pass.
            if (botFSMSystem != null)
                simulation.AddSystem(botFSMSystem, SystemPhase.PreUpdate);
            simulation.AddSystem(platformerCommandSystem, SystemPhase.PreUpdate);

            // Update — simulation systems
            simulation.AddSystem(new ObstacleMovementSystem(events), SystemPhase.Update);
            simulation.AddSystem(new TopdownMovementSystem(events), SystemPhase.Update);
            simulation.AddSystem(new ActionLockSystem(), SystemPhase.Update);
            simulation.AddSystem(new KnockbackSystem(events), SystemPhase.Update);
            var physicsSystem = new PhysicsSystem(256, FPVector3.Zero);
            physicsSystem.SetSkipStaticGroundResponse(true);
            if (staticColliders != null)
                physicsSystem.LoadStaticColliders($"Stage{stageId}", staticColliders);
            simulation.AddSystem(physicsSystem, SystemPhase.Update);
            platformerCommandSystem.SetRayCaster(physicsSystem);
            if (botFSMSystem != null)
                botFSMSystem.SetRayCaster(physicsSystem);
            simulation.AddSystem(new TrapTriggerSystem(physicsSystem, events), SystemPhase.Update);
            simulation.AddSystem(new SkillCooldownSystem(events), SystemPhase.Update);
            simulation.AddSystem(new BoundaryCheckSystem(events), SystemPhase.Update);
            simulation.AddSystem(new ItemSpawnSystem(events), SystemPhase.Update);
            simulation.AddSystem(new CombatSystem(events), SystemPhase.Update);
            simulation.AddSystem(new RespawnSystem(events), SystemPhase.Update);
            simulation.AddSystem(new TimerSystem(events), SystemPhase.Update);

            // PostUpdate — landing clamp, then game-over detection
            simulation.AddSystem(new GroundClampSystem(physicsSystem), SystemPhase.PostUpdate);
            simulation.AddSystem(new GameOverSystem(events, stageId), SystemPhase.PostUpdate);

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
                new BrawlerGameRulesAsset(),
                new CombatPhysicsAsset(),
                new BasicAttackConfigAsset(),
                new ItemConfigAsset(),
                new MovementPhysicsAsset(),
                new BotBehaviorAsset(),
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
