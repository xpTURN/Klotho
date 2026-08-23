using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using Brawler;

namespace xpTURN.Klotho.Tests
{
    /// <summary>
    /// Brawler writes one logical value — the player id — into three components at spawn:
    /// OwnerComponent.OwnerId, CharacterComponent.PlayerId and SpawnMarkerComponent.PlayerId
    /// (PlatformerCommandSystem.HandleSpawn; BrawlerSimSetup.SpawnBots does the same for bots).
    /// Nothing enforces that the three agree, yet different lookups key off different copies:
    /// PlatformerCommandSystem.TryFindCharacter scans OwnerId, BrawlerSimulationCallbacks scans
    /// CharacterComponent.PlayerId, and RespawnSystem.FindSpawnPosition scans the marker.
    ///
    /// A divergence would not show up as a desync — the copies are all in the state hash, so every
    /// peer goes wrong identically and the hash checks stay green. The only symptom is a lookup
    /// resolving to a different entity than its sibling. These tests pin the invariant on the
    /// production spawn path.
    /// </summary>
    [TestFixture]
    public class PlayerIdentityInvariantTests
    {
        private const int MaxEntities = 64;
        private const int MaxRollbackTicks = 10;
        private const int DeltaTimeMs = 50;

        private EcsSimulation CreateSimulation()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: MaxRollbackTicks, deltaTimeMs: DeltaTimeMs);
            BrawlerSimSetup.RegisterSystems(sim, null, dataAssets: BrawlerSimSetup.CreateDefaultDataAssets());
            sim.Initialize();

            // Singletons the tick path reads.
            var frame = sim.Frame;
            var seedEntity = frame.CreateEntity();
            frame.Add(seedEntity, new RandomSeedComponent { Seed = 12345 });

            var timerEntity = frame.CreateEntity();
            frame.Add(timerEntity, new GameTimerStateComponent
            {
                StartTick = -1,
                LastReportedSeconds = -1,
                GameOverFired = false,
            });

            return sim;
        }

        private static void Spawn(EcsSimulation sim, int playerId, int characterClass, FPVector2 pos)
        {
            sim.Tick(new List<ICommand>
            {
                new SpawnCharacterCommand
                {
                    PlayerId       = playerId,
                    CharacterClass = characterClass,
                    SpawnPosition  = pos,
                    SequenceNumber = 0,
                },
            });
        }

        // Scans by OwnerComponent.OwnerId — the key PlatformerCommandSystem uses.
        private static bool TryFindByOwnerId(Frame frame, int playerId, out EntityRef result)
        {
            var filter = frame.Filter<OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                if (frame.GetReadOnly<OwnerComponent>(entity).OwnerId == playerId)
                {
                    result = entity;
                    return true;
                }
            }
            result = default;
            return false;
        }

        // Scans by CharacterComponent.PlayerId — the key BrawlerSimulationCallbacks uses.
        private static bool TryFindByCharacterPlayerId(Frame frame, int playerId, out EntityRef result)
        {
            var filter = frame.Filter<OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                if (frame.GetReadOnly<CharacterComponent>(entity).PlayerId == playerId)
                {
                    result = entity;
                    return true;
                }
            }
            result = default;
            return false;
        }

        [Test]
        public void Spawn_WritesTheSameIdentityToOwnerCharacterAndMarker()
        {
            var sim = CreateSimulation();
            Spawn(sim, playerId: 1, characterClass: 0, pos: new FPVector2(4.0f, -4.0f));

            var frame = sim.Frame;

            int found = 0;
            var filter = frame.Filter<OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                found++;
                int ownerId  = frame.GetReadOnly<OwnerComponent>(entity).OwnerId;
                int playerId = frame.GetReadOnly<CharacterComponent>(entity).PlayerId;
                Assert.AreEqual(ownerId, playerId,
                    $"entity {entity.Index}: OwnerComponent.OwnerId and CharacterComponent.PlayerId diverged");
            }
            Assert.AreEqual(1, found, "expected exactly one owned character after a single spawn");

            int markers = 0;
            var markerFilter = frame.Filter<SpawnMarkerComponent>();
            while (markerFilter.Next(out var marker))
            {
                markers++;
                Assert.AreEqual(1, frame.GetReadOnly<SpawnMarkerComponent>(marker).PlayerId,
                    "SpawnMarkerComponent.PlayerId must carry the same player id (RespawnSystem keys off it)");
            }
            Assert.AreEqual(1, markers, "expected exactly one spawn marker after a single spawn");
        }

        /// <summary>
        /// The character component set is co-present by construction: the four character prototypes each
        /// add Transform, PhysicsBody, Character and Owner in one CreateEntity, and nothing anywhere removes
        /// any of the four (Brawler declares no [KlothoCleanup] types either, so no bulk clear touches them).
        /// Lookups rely on that — a shared finder can filter on the key component alone and let each caller
        /// read what it needs, instead of every call site re-checking a requirement that cannot fail.
        ///
        /// This test pins the guarantee, because the failure mode is a crash rather than a warning:
        /// ComponentStorageFlat.Get is an unchecked `ComponentsSpan[SparseSpan[i]]`, so reading a component
        /// an entity does not have indexes a Span with -1.
        /// </summary>
        [Test]
        public void EveryOwnedEntity_CarriesTheFullCharacterComponentSet()
        {
            var sim = CreateSimulation();
            Spawn(sim, playerId: 1, characterClass: 0, pos: new FPVector2(4.0f, -4.0f));
            Spawn(sim, playerId: 2, characterClass: 3, pos: new FPVector2(-4.0f, 4.0f));

            var frame = sim.Frame;

            int owned = 0;
            var filter = frame.Filter<OwnerComponent>();
            while (filter.Next(out var entity))
            {
                owned++;
                Assert.IsTrue(frame.Has<CharacterComponent>(entity),
                    $"entity {entity.Index} carries OwnerComponent without CharacterComponent");
                Assert.IsTrue(frame.Has<TransformComponent>(entity),
                    $"entity {entity.Index} carries OwnerComponent without TransformComponent");
                Assert.IsTrue(frame.Has<PhysicsBodyComponent>(entity),
                    $"entity {entity.Index} carries OwnerComponent without PhysicsBodyComponent");
            }
            Assert.AreEqual(2, owned, "expected exactly one owned entity per spawned player");
        }

        [Test]
        public void BothKeyPaths_ResolveToTheSameEntity_ForEveryPlayer()
        {
            var sim = CreateSimulation();
            Spawn(sim, playerId: 1, characterClass: 0, pos: new FPVector2(4.0f, -4.0f));
            Spawn(sim, playerId: 2, characterClass: 1, pos: new FPVector2(-4.0f, 4.0f));

            var frame = sim.Frame;

            for (int playerId = 1; playerId <= 2; playerId++)
            {
                Assert.IsTrue(TryFindByOwnerId(frame, playerId, out var byOwner),
                    $"player {playerId}: no character found by OwnerComponent.OwnerId");
                Assert.IsTrue(TryFindByCharacterPlayerId(frame, playerId, out var byCharacter),
                    $"player {playerId}: no character found by CharacterComponent.PlayerId");
                Assert.AreEqual(byOwner, byCharacter,
                    $"player {playerId}: the two identity keys resolve to different entities — " +
                    "lookups keyed on OwnerId and on PlayerId would disagree");
            }
        }
    }
}
