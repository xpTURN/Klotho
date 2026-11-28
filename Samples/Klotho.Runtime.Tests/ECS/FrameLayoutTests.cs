using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS.Diagnostics;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// <see cref="FrameLayout"/>: each simulation owns an immutable layout computed from its own inputs,
    /// shared by its live frame and rollback ring; the registry's static readers describe only the
    /// default layout fixed by an explicit <c>EnsureLayoutComputed</c>.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class FrameLayoutTests
    {
        private bool _savedAllowRecompute;

        [SetUp]
        public void SetUp()
        {
            _savedAllowRecompute = ComponentStorageRegistry.AllowLayoutRecompute;
            ComponentStorageRegistry.ResetForTesting();
        }

        [TearDown]
        public void TearDown()
        {
            ComponentStorageRegistry.AllowLayoutRecompute = _savedAllowRecompute;
            ComponentStorageRegistry.ResetForTesting();
        }

        private static int A => ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
        private static int B => ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

        [Test]
        public void TwoSimulations_WithDifferentLayouts_RunSnapshotRollbackAndFullStateIndependently()
        {
            ComponentStorageRegistry.AllowLayoutRecompute = false;
            var small = new EcsSimulation(32, 8, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 8 } }, prunedComponentTypeIds: new[] { B });
            var large = new EcsSimulation(256, 8, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 64 } });
            small.Initialize();
            large.Initialize();

            Assert.Less(small.Frame.HeapSize, large.Frame.HeapSize);
            Assert.AreNotEqual(small.Layout.LayoutFingerprint, large.Layout.LayoutFingerprint);
            Assert.AreEqual(8, small.Layout.GetLayout(A).SlotCapacity);
            Assert.AreEqual(64, large.Layout.GetLayout(A).SlotCapacity);
            Assert.IsFalse(small.Layout.HasLayout(B));
            Assert.IsTrue(large.Layout.HasLayout(B));

            var se = small.Frame.CreateEntity();
            small.Frame.Add(se, new PruneGameAComponent { Value = 11 });
            Assert.Throws<InvalidOperationException>(() => small.Frame.Has<PruneGameBComponent>(se));
            var le = large.Frame.CreateEntity();
            large.Frame.Add(le, new PruneGameAComponent { Value = 22 });
            large.Frame.Add(le, new PruneGameBComponent { Value = 99 });

            small.SaveSnapshot();
            large.SaveSnapshot();
            long smallHash = small.GetStateHash();
            long largeHash = large.GetStateHash();
            byte[] smallState = small.SerializeFullState();
            byte[] largeState = large.SerializeFullState();

            small.Frame.Get<PruneGameAComponent>(se).Value = 111;
            large.Frame.Get<PruneGameBComponent>(le).Value = 999;
            small.Tick(new List<ICommand>());
            large.Tick(new List<ICommand>());
            small.Rollback(0);
            large.Rollback(0);

            Assert.AreEqual(11, small.Frame.GetReadOnly<PruneGameAComponent>(se).Value);
            Assert.AreEqual(99, large.Frame.GetReadOnly<PruneGameBComponent>(le).Value);
            Assert.AreEqual(smallHash, small.GetStateHash());
            Assert.AreEqual(largeHash, large.GetStateHash());

            var smallPeer = new EcsSimulation(32, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 8 } }, prunedComponentTypeIds: new[] { B });
            smallPeer.Initialize();
            smallPeer.RestoreFromFullState(smallState);
            Assert.AreEqual(smallHash, smallPeer.GetStateHash());

            var largePeer = new EcsSimulation(256, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 64 } });
            largePeer.Initialize();
            largePeer.RestoreFromFullState(largeState);
            Assert.AreEqual(largeHash, largePeer.GetStateHash());
        }

        [Test]
        public void CreatingASimulation_NeitherCreatesNorChangesTheDefaultLayout()
        {
            _ = new EcsSimulation(64, 4, 25);
            Assert.AreEqual(0, ComponentStorageRegistry.LayoutFingerprint);
            Assert.AreEqual(0, ComponentStorageRegistry.LayoutTypeCount);
            Assert.IsFalse(ComponentStorageRegistry.IsFrozen);
            Assert.AreEqual(0, ComponentStorageRegistry.RegisteredTypeIdsSorted.Length);
            Assert.Throws<InvalidOperationException>(() => ComponentStorageRegistry.GetLayout(A));

            ComponentStorageRegistry.EnsureLayoutComputed(96, null, null);
            long defaultFp = ComponentStorageRegistry.LayoutFingerprint;
            var other = new EcsSimulation(48, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 4 } }, prunedComponentTypeIds: new[] { B });

            Assert.AreEqual(defaultFp, ComponentStorageRegistry.LayoutFingerprint);
            Assert.AreEqual(96, ComponentStorageRegistry.GetLayout(A).Capacity);
            Assert.AreNotEqual(defaultFp, other.Layout.LayoutFingerprint);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void LegacyFrameConstructor_IsAPureFunctionOfMaxEntitiesAndTheDefault(bool allowRecompute)
        {
            ComponentStorageRegistry.AllowLayoutRecompute = allowRecompute;
            ComponentStorageRegistry.EnsureLayoutComputed(256, new Dictionary<int, int> { { A, 12 } }, null);
            long defaultFp = ComponentStorageRegistry.LayoutFingerprint;

            var sameMax = new Frame(256, null);
            Assert.AreEqual(defaultFp, sameMax.Layout.LayoutFingerprint, "matching maxEntities uses the default");
            Assert.AreEqual(12, sameMax.Layout.GetLayout(A).SlotCapacity);

            var otherMax = new Frame(64, null);
            Assert.AreEqual(64, otherMax.Layout.GetLayout(A).SlotCapacity, "maxEntities alone — no overrides");
            Assert.AreEqual(defaultFp, ComponentStorageRegistry.LayoutFingerprint, "the default is unchanged");
            Assert.AreEqual(256, ComponentStorageRegistry.GetLayout(A).Capacity);
            Assert.AreEqual(otherMax.HeapSize, ComponentStorageRegistry.GetHeapSize(64));
        }

        [Test]
        public void LegacyFrameConstructor_DoesNotPickUpAnExistingSimulationsLayout()
        {
            var sim = new EcsSimulation(96, 4, 25, maxCountOverrides: new Dictionary<int, int> { { A, 12 } });
            var legacy = new Frame(96, null);

            Assert.AreEqual(96, legacy.Layout.GetLayout(A).SlotCapacity);
            Assert.AreNotEqual(sim.Layout.LayoutFingerprint, legacy.Layout.LayoutFingerprint);
            Assert.Throws<InvalidOperationException>(() => legacy.CopyFrom(sim.Frame));

            var compatible = sim.CreateCompatibleFrame();
            Assert.AreSame(sim.Layout, compatible.Layout);
            Assert.DoesNotThrow(() => compatible.CopyFrom(sim.Frame));
        }

        [Test]
        public void CopyFrom_RejectsADifferentLayout_AndAcceptsAnEqualOne()
        {
            var small = new Frame(ComponentStorageRegistry.ComputeLayout(32), null);
            var large = new Frame(ComponentStorageRegistry.ComputeLayout(128), null);
            Assert.Throws<InvalidOperationException>(() => small.CopyFrom(large));
            Assert.Throws<InvalidOperationException>(() => large.CopyFrom(small));

            // Same maxEntities (so the entity table copies fine) but different storage offsets.
            var full = new Frame(ComponentStorageRegistry.ComputeLayout(64), null);
            var pruned = new Frame(ComponentStorageRegistry.ComputeLayout(64, null, new[] { B }), null);
            Assert.Throws<InvalidOperationException>(() => full.CopyFrom(pruned));
            Assert.Throws<InvalidOperationException>(() => pruned.CopyFrom(full));

            var l1 = ComponentStorageRegistry.ComputeLayout(64);
            var l2 = ComponentStorageRegistry.ComputeLayout(64);
            Assert.AreNotSame(l1, l2);
            var src = new Frame(l1, null);
            var e = src.CreateEntity();
            src.Add(e, new PruneGameAComponent { Value = 7 });
            var dst = new Frame(l2, null);
            dst.CopyFrom(src);
            Assert.AreEqual(7, dst.GetReadOnly<PruneGameAComponent>(e).Value);
        }

        [Test]
        public void Engine_ReportsItsOwnSimulationsLayoutFingerprint()
        {
            var simSmall = new EcsSimulation(32, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 8 } }, prunedComponentTypeIds: new[] { B });
            var simLarge = new EcsSimulation(192, 4, 25);
            var simLarge2 = new EcsSimulation(192, 4, 25);

            var engineSmall = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            var engineLarge = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            var engineLarge2 = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engineSmall.Initialize(simSmall, null);
            engineLarge.Initialize(simLarge, null);
            engineLarge2.Initialize(simLarge2, null);

            Assert.AreEqual(simSmall.Layout.LayoutFingerprint, engineSmall.GetLocalLayoutFingerprint());
            Assert.AreEqual(simLarge.Layout.LayoutFingerprint, engineLarge.GetLocalLayoutFingerprint());
            Assert.AreNotEqual(engineSmall.GetLocalLayoutFingerprint(), engineLarge.GetLocalLayoutFingerprint());
            Assert.AreEqual(engineLarge.GetLocalLayoutFingerprint(), engineLarge2.GetLocalLayoutFingerprint());
        }

        [Test]
        public void MemoryReportAndPeakSampler_FollowTheFramesLayout()
        {
            var sim = new EcsSimulation(48, 4, 25,
                maxCountOverrides: new Dictionary<int, int> { { A, 12 } }, prunedComponentTypeIds: new[] { B });
            for (int i = 0; i < 5; i++)
                sim.Frame.Add(sim.Frame.CreateEntity(), new PruneGameAComponent { Value = i });

            var sampler = new ComponentMemoryPeakSampler(sim.Layout.MaxTypeId);
            sampler.Sample(sim.Frame);
            var report = ComponentMemoryAnalyzer.Capture(sim.Frame, 4, sampler.PeakCounts);

            bool foundA = false, foundB = false;
            foreach (var stat in report.Stats)
            {
                if (stat.TypeId == A)
                {
                    foundA = true;
                    Assert.AreEqual(12, stat.SlotCapacity);
                    Assert.AreEqual(5, stat.LiveCount);
                    Assert.AreEqual(5, stat.PeakCount);
                }
                if (stat.TypeId == B) foundB = true;
            }
            Assert.IsTrue(foundA);
            Assert.IsFalse(foundB, "a pruned type is not in this frame's report");
        }

        [Test]
        public void CleanupPass_UsesTheFramesLayout()
        {
            int mark = ComponentStorageRegistry.GetTypeId<CleanupMarkComponent>();
            var pruned = new EcsSimulation(32, 4, 25, prunedComponentTypeIds: new[] { mark });
            var kept = new EcsSimulation(32, 4, 25);
            pruned.Initialize();
            kept.Initialize();
            Assert.AreEqual(kept.Layout.CleanupTypeCount - 1, pruned.Layout.CleanupTypeCount);

            var e = kept.Frame.CreateEntity();
            kept.Frame.Add(e, new CleanupMarkComponent { Value = 1 });
            kept.Tick(new List<ICommand>());
            Assert.IsFalse(kept.Frame.Has<CleanupMarkComponent>(e), "RemoveComponent cleanup ran at tick end");

            Assert.DoesNotThrow(() => pruned.Tick(new List<ICommand>()), "the pruned simulation has no pass for the type");
        }

        [Test]
        public void ComponentTypeIdAt_IsRelativeToTheSimulationsOwnLayout()
        {
            var full = new EcsSimulation(32, 4, 25);
            var pruned = new EcsSimulation(32, 4, 25, prunedComponentTypeIds: new[] { B });

            var fullIds = full.Layout.RegisteredTypeIdsSorted;
            var prunedIds = pruned.Layout.RegisteredTypeIdsSorted;
            Assert.AreEqual(fullIds.Length - 1, prunedIds.Length);
            for (int i = 0; i < prunedIds.Length; i++)
                Assert.AreEqual(prunedIds[i], pruned.GetComponentTypeIdAt(i));
            for (int i = 0; i < fullIds.Length; i++)
                Assert.AreEqual(fullIds[i], full.GetComponentTypeIdAt(i));
            Assert.AreEqual(-1, pruned.GetComponentTypeIdAt(prunedIds.Length));

            int bIndex = Array.IndexOf(fullIds.ToArray(), B);
            Assert.AreEqual(B, full.GetComponentTypeIdAt(bIndex));
            Assert.AreNotEqual(B, pruned.GetComponentTypeIdAt(bIndex));
        }

        [Test]
        public void StaticComponentTypeIdAt_ReadsOnlyTheDefaultLayout()
        {
            var sim = new EcsSimulation(32, 4, 25, prunedComponentTypeIds: new[] { B });
            var allIds = ComponentStorageRegistry.ComputeLayout(32).RegisteredTypeIdsSorted.ToArray();
            int bIndex = Array.IndexOf(allIds, B);
            Assert.GreaterOrEqual(bIndex, 0);

            for (int i = 0; i < allIds.Length; i++)
                Assert.AreEqual(-1, EcsSimulation.ComponentTypeIdAt(i), "building a simulation sets no default layout");

            ComponentStorageRegistry.EnsureLayoutComputed(32, null, null);
            for (int i = 0; i < allIds.Length; i++)
                Assert.AreEqual(allIds[i], EcsSimulation.ComponentTypeIdAt(i));
            Assert.AreEqual(-1, EcsSimulation.ComponentTypeIdAt(allIds.Length));

            var simIds = sim.Layout.RegisteredTypeIdsSorted;
            for (int i = 0; i < simIds.Length; i++)
                Assert.AreEqual(simIds[i], sim.GetComponentTypeIdAt(i));
            Assert.AreEqual(B, EcsSimulation.ComponentTypeIdAt(bIndex));
            Assert.AreNotEqual(B, sim.GetComponentTypeIdAt(bIndex), "the instance reads its own layout, not the default");
        }

        [TestCase(64, false, false)]
        [TestCase(64, true, false)]
        [TestCase(200, true, true)]
        public void ComputeLayout_MatchesTheDefaultLayoutForTheSameInputs(int maxEntities, bool withOverride, bool withPrune)
        {
            var overrides = withOverride ? new Dictionary<int, int> { { A, 6 } } : null;
            var pruned = withPrune ? new[] { B } : null;

            ComponentStorageRegistry.EnsureLayoutComputed(maxEntities, overrides, pruned);
            var computed = ComponentStorageRegistry.ComputeLayout(maxEntities, overrides, pruned);

            Assert.AreEqual(ComponentStorageRegistry.LayoutFingerprint, computed.LayoutFingerprint);
            Assert.AreEqual(ComponentStorageRegistry.GetHeapSize(maxEntities), computed.HeapSize);
            Assert.AreEqual(ComponentStorageRegistry.LayoutTypeCount, computed.LayoutTypeCount);
            var ids = computed.RegisteredTypeIdsSorted;
            for (int i = 0; i < ids.Length; i++)
            {
                ref readonly var d = ref ComponentStorageRegistry.GetLayout(ids[i]);
                ref readonly var c = ref computed.GetLayout(ids[i]);
                Assert.AreEqual(d.CountOffset, c.CountOffset);
                Assert.AreEqual(d.SparseOffset, c.SparseOffset);
                Assert.AreEqual(d.DenseOffset, c.DenseOffset);
                Assert.AreEqual(d.ComponentsOffset, c.ComponentsOffset);
                Assert.AreEqual(d.SlotCapacity, c.SlotCapacity);
                Assert.AreEqual(d.TotalSize, c.TotalSize);
            }
        }
    }
}
