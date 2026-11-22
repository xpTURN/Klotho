using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS.Diagnostics;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class PerRoomFrameLayoutTests
    {
        private bool _savedAllowRecompute;

        [SetUp]
        public void SetUp()
        {
            _savedAllowRecompute = ComponentStorageRegistry.AllowLayoutRecompute;
            // Run under shipping semantics (no silent static recompute) to prove per-room layouts
            // never rely on test-only static resets.
            ComponentStorageRegistry.AllowLayoutRecompute = false;
        }

        [TearDown]
        public void TearDown()
        {
            ComponentStorageRegistry.AllowLayoutRecompute = _savedAllowRecompute;
        }

        [Test]
        public void ConcurrentSimulations_WithDifferentMaxEntitiesOverridesAndPruneSets_CoexistAndRunDeterministically()
        {
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int typeB = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var smallSim = new EcsSimulation(
                maxEntities: 32,
                maxRollbackTicks: 8,
                deltaTimeMs: 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 8 } },
                prunedComponentTypeIds: new[] { typeB });

            var largeSim = new EcsSimulation(
                maxEntities: 256,
                maxRollbackTicks: 8,
                deltaTimeMs: 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 64 } },
                prunedComponentTypeIds: null);

            smallSim.Initialize();
            largeSim.Initialize();

            Assert.AreEqual(32, smallSim.Frame.MaxEntities);
            Assert.AreEqual(256, largeSim.Frame.MaxEntities);
            Assert.Less(smallSim.Frame.HeapSize, largeSim.Frame.HeapSize,
                "Small room frame heap must be strictly smaller than large room frame heap.");
            Assert.AreNotEqual(smallSim.Layout.LayoutFingerprint, largeSim.Layout.LayoutFingerprint);
            Assert.AreEqual(8, smallSim.Layout.GetLayout(typeA).SlotCapacity);
            Assert.AreEqual(64, largeSim.Layout.GetLayout(typeA).SlotCapacity);
            Assert.IsFalse(smallSim.Layout.HasLayout(typeB));
            Assert.IsTrue(largeSim.Layout.HasLayout(typeB));

            // Small room pruned B -> throws on access; large room still uses B freely at the same time.
            var smallEntity = smallSim.Frame.CreateEntity();
            smallSim.Frame.Add(smallEntity, new PruneGameAComponent { Value = 11 });
            Assert.Throws<InvalidOperationException>(() => smallSim.Frame.Has<PruneGameBComponent>(smallEntity));

            var largeEntity = largeSim.Frame.CreateEntity();
            largeSim.Frame.Add(largeEntity, new PruneGameAComponent { Value = 22 });
            largeSim.Frame.Add(largeEntity, new PruneGameBComponent { Value = 99 });
            Assert.AreEqual(99, largeSim.Frame.GetReadOnly<PruneGameBComponent>(largeEntity).Value);

            // Snapshot, rollback, serialize, and restore work independently on both rooms.
            smallSim.SaveSnapshot();
            largeSim.SaveSnapshot();
            long smallHash0 = smallSim.GetStateHash();
            long largeHash0 = largeSim.GetStateHash();
            byte[] smallFullState = smallSim.SerializeFullState();
            byte[] largeFullState = largeSim.SerializeFullState();

            smallSim.Frame.Get<PruneGameAComponent>(smallEntity).Value = 111;
            largeSim.Frame.Get<PruneGameBComponent>(largeEntity).Value = 999;
            smallSim.Tick(new List<ICommand>());
            largeSim.Tick(new List<ICommand>());

            smallSim.Rollback(0);
            largeSim.Rollback(0);
            Assert.AreEqual(11, smallSim.Frame.GetReadOnly<PruneGameAComponent>(smallEntity).Value);
            Assert.AreEqual(99, largeSim.Frame.GetReadOnly<PruneGameBComponent>(largeEntity).Value);
            Assert.AreEqual(smallHash0, smallSim.GetStateHash());
            Assert.AreEqual(largeHash0, largeSim.GetStateHash());

            // Restore into a fresh peer simulation with matching per-room layout produces identical hashes.
            var smallPeer = new EcsSimulation(
                maxEntities: 32,
                maxRollbackTicks: 4,
                deltaTimeMs: 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 8 } },
                prunedComponentTypeIds: new[] { typeB });
            smallPeer.Initialize();
            smallPeer.RestoreFromFullState(smallFullState);
            Assert.AreEqual(smallHash0, smallPeer.GetStateHash());

            var largePeer = new EcsSimulation(
                maxEntities: 256,
                maxRollbackTicks: 4,
                deltaTimeMs: 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 64 } },
                prunedComponentTypeIds: null);
            largePeer.Initialize();
            largePeer.RestoreFromFullState(largeFullState);
            Assert.AreEqual(largeHash0, largePeer.GetStateHash());
        }

        [Test]
        public void ComputeLayout_CachesAndSharesImmutableInstance_ForEquivalentInputs()
        {
            int core = ComponentStorageRegistry.GetTypeId<PruneCoreComponent>();
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int typeB = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var layout1 = ComponentStorageRegistry.ComputeLayout(
                64,
                new Dictionary<int, int> { { typeA, 16 }, { typeB, 8 } },
                new[] { typeB });
            var layout2 = ComponentStorageRegistry.ComputeLayout(
                64,
                new Dictionary<int, int> { { typeB, 8 }, { typeA, 16 } },
                new[] { typeB, core }); // core is force-excluded from prune-set

            Assert.AreSame(layout1, layout2,
                "Equivalent layout inputs (order-independent overrides, core-only prune noise) must share one cached FrameLayout.");
        }

        [Test]
        public void Frame_CopyFrom_ThrowsWhenLayoutsDiffer()
        {
            var smallLayout = ComponentStorageRegistry.ComputeLayout(32);
            var largeLayout = ComponentStorageRegistry.ComputeLayout(128);
            var smallFrame = new Frame(smallLayout, null);
            var largeFrame = new Frame(largeLayout, null);

            Assert.Throws<InvalidOperationException>(() => smallFrame.CopyFrom(largeFrame));
            Assert.Throws<InvalidOperationException>(() => largeFrame.CopyFrom(smallFrame));
        }

        [Test]
        public void KlothoEngine_ReportsItsOwnSimulationLayoutFingerprint_AcrossConcurrentEngines()
        {
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int typeB = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var simSmall = new EcsSimulation(32, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 8 } },
                prunedComponentTypeIds: new[] { typeB });
            var simLarge = new EcsSimulation(192, 4, 25);

            var cfgSmall = new SimulationConfig { MaxEntities = 32, TickIntervalMs = 25, MaxRollbackTicks = 4, SyncCheckInterval = 2 };
            var cfgLarge = new SimulationConfig { MaxEntities = 192, TickIntervalMs = 25, MaxRollbackTicks = 4, SyncCheckInterval = 2 };
            var engineSmall = new KlothoEngine(cfgSmall, new SessionConfig { MaxPlayers = 2, MinPlayers = 1 });
            var engineLarge = new KlothoEngine(cfgLarge, new SessionConfig { MaxPlayers = 2, MinPlayers = 1 });

            engineSmall.Initialize(simSmall, new KlothoNetworkService(), null, null);
            engineLarge.Initialize(simLarge, new KlothoNetworkService(), null, null);

            Assert.AreEqual(simSmall.Layout.LayoutFingerprint, engineSmall.GetLocalLayoutFingerprint());
            Assert.AreEqual(simLarge.Layout.LayoutFingerprint, engineLarge.GetLocalLayoutFingerprint());
            Assert.AreNotEqual(engineSmall.GetLocalLayoutFingerprint(), engineLarge.GetLocalLayoutFingerprint());
        }

        [Test]
        public void MemoryAnalyzerAndPeakSampler_RespectPerRoomFrameLayout()
        {
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int typeB = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var sim = new EcsSimulation(48, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 12 } },
                prunedComponentTypeIds: new[] { typeB });
            sim.Initialize();

            for (int i = 0; i < 5; i++)
                sim.Frame.Add(sim.Frame.CreateEntity(), new PruneGameAComponent { Value = i });

            var sampler = new ComponentMemoryPeakSampler(sim.Layout.MaxTypeId);
            sampler.Sample(sim.Frame);
            var report = ComponentMemoryAnalyzer.Capture(sim.Frame, 4, sampler.PeakCounts);

            bool foundA = false, foundB = false;
            foreach (var stat in report.Stats)
            {
                if (stat.TypeId == typeA)
                {
                    foundA = true;
                    Assert.AreEqual(12, stat.SlotCapacity);
                    Assert.AreEqual(5, stat.LiveCount);
                    Assert.AreEqual(5, stat.PeakCount);
                }
                if (stat.TypeId == typeB) foundB = true;
            }

            Assert.IsTrue(foundA, "Unpruned component A must appear with its room-specific SlotCapacity.");
            Assert.IsFalse(foundB, "Pruned component B must not appear in this room's memory report.");
        }

        [Test]
        public void SnapshotFrame_And_CopyFrom_PreserveScopedStateHash()
        {
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int typeB = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var sim = new EcsSimulation(64, 4, 25);
            sim.Initialize();
            sim.Frame.SetStateHashComponentTypes(typeA);
            var e = sim.Frame.CreateEntity();
            sim.Frame.Add(e, new PruneGameAComponent { Value = 7 });
            sim.Frame.Add(e, new PruneGameBComponent { Value = 42 });

            sim.SaveSnapshot();
            Assert.IsTrue(sim.TryGetSnapshotFrame(0, out var snap));
            Assert.AreEqual(sim.Frame.CalculateHash(), snap.CalculateHash(),
                "Saved snapshot must preserve scoped state-hash policy and match live Frame.CalculateHash().");
            Assert.AreNotEqual(snap.CalculateFullHash(), snap.CalculateHash());

            var copy = sim.CreateCompatibleFrame();
            copy.CopyFrom(sim.Frame);
            Assert.AreEqual(sim.Frame.CalculateHash(), copy.CalculateHash());
        }

        [Test]
        public void ReadyHandshake_DetectsDifferentStateHashScopes_WithIdenticalFrameLayout()
        {
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int typeB = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var sim1 = new EcsSimulation(64, 4, 25);
            var sim2 = new EcsSimulation(64, 4, 25);
            sim1.Frame.SetStateHashComponentTypes(typeA);
            sim2.Frame.SetStateHashComponentTypes(typeA, typeB);

            var cfg = new SimulationConfig { MaxEntities = 64, TickIntervalMs = 25, MaxRollbackTicks = 4, SyncCheckInterval = 2 };
            var engine1 = new KlothoEngine(cfg, new SessionConfig { MaxPlayers = 2, MinPlayers = 1 });
            var engine2 = new KlothoEngine(cfg, new SessionConfig { MaxPlayers = 2, MinPlayers = 1 });
            engine1.Initialize(sim1, new KlothoNetworkService(), null, null);
            engine2.Initialize(sim2, new KlothoNetworkService(), null, null);

            Assert.AreEqual(sim1.Layout.LayoutFingerprint, sim2.Layout.LayoutFingerprint);
            Assert.AreNotEqual(engine1.GetLocalLayoutFingerprint(), engine2.GetLocalLayoutFingerprint());
            Assert.AreEqual(
                ReadyFingerprintVerdict.LayoutMismatch,
                engine1.CheckReadyFingerprints(2, engine2.GetLocalLayoutFingerprint(), 0, compareEnvironment: false));
        }

        [Test]
        public void LegacyFrameCtor_MatchesActiveOverriddenLayoutForCaptureStateTo()
        {
            int typeA = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            var sim = new EcsSimulation(
                maxEntities: 96,
                maxRollbackTicks: 4,
                deltaTimeMs: 25,
                maxCountOverrides: new Dictionary<int, int> { { typeA, 12 } });
            sim.Initialize();
            var e = sim.Frame.CreateEntity();
            sim.Frame.Add(e, new PruneGameAComponent { Value = 55 });

            var legacyHelper = new Frame(96, null);
            byte[] sysBuf = null;
            Assert.DoesNotThrow(() => sim.CaptureStateTo(legacyHelper, ref sysBuf));
            Assert.AreEqual(55, legacyHelper.GetReadOnly<PruneGameAComponent>(e).Value);
        }
    }
}
