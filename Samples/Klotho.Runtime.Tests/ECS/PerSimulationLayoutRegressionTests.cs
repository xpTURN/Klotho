using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace xpTURN.Klotho.ECS.Tests
{
    /// <summary>
    /// Simulations in one process with different layout inputs (maxEntities · maxCount overrides ·
    /// prune-set). Each keeps its own layout: creating a second one neither throws (release semantics)
    /// nor recomputes a layout under the first (editor/debug/test semantics).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class PerSimulationLayoutRegressionTests
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

        [TestCase("maxEntities")]
        [TestCase("maxCountOverride")]
        [TestCase("pruneSet")]
        public void SecondSimulation_WithDifferentLayoutInputs_IsBuiltWithItsOwnLayout(string kind)
        {
            ComponentStorageRegistry.AllowLayoutRecompute = false;
            int a = ComponentStorageRegistry.GetTypeId<PruneGameAComponent>();
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();

            var first = new EcsSimulation(64, 4, 25);
            EcsSimulation second = null;
            Assert.DoesNotThrow(() => second = kind switch
            {
                "maxEntities"      => new EcsSimulation(128, 4, 25),
                "maxCountOverride" => new EcsSimulation(64, 4, 25, maxCountOverrides: new Dictionary<int, int> { { a, 8 } }),
                _                  => new EcsSimulation(64, 4, 25, prunedComponentTypeIds: new[] { b }),
            });

            var firstEntity = first.Frame.CreateEntity();
            first.Frame.Add(firstEntity, new PruneGameAComponent { Value = 1 });
            first.Frame.Add(firstEntity, new PruneGameBComponent { Value = 2 });
            Assert.AreEqual(64, first.Frame.MaxEntities);

            switch (kind)
            {
                case "maxEntities":
                    Assert.AreEqual(128, second.Frame.MaxEntities);
                    EntityRef last = default;
                    for (int i = 0; i < 100; i++)
                    {
                        last = second.Frame.CreateEntity();
                        second.Frame.Add(last, new PruneGameAComponent { Value = i });
                    }
                    Assert.AreEqual(99, second.Frame.GetReadOnly<PruneGameAComponent>(last).Value);
                    break;
                case "maxCountOverride":
                    for (int i = 0; i < 8; i++)
                        second.Frame.Add(second.Frame.CreateEntity(), new PruneGameAComponent { Value = i });
                    var ninth = second.Frame.CreateEntity();
                    Assert.Throws<InvalidOperationException>(
                        () => second.Frame.Add(ninth, new PruneGameAComponent { Value = 8 }),
                        "the second simulation's override caps A at 8 slots");
                    for (int i = 0; i < 9; i++)
                        first.Frame.Add(first.Frame.CreateEntity(), new PruneGameAComponent { Value = i });
                    break;
                default:
                    var secondEntity = second.Frame.CreateEntity();
                    Assert.Throws<InvalidOperationException>(() => second.Frame.Has<PruneGameBComponent>(secondEntity),
                        "B is pruned in the second simulation");
                    Assert.IsTrue(first.Frame.Has<PruneGameBComponent>(firstEntity), "B stays reserved in the first");
                    break;
            }

            Assert.AreEqual(1, first.Frame.GetReadOnly<PruneGameAComponent>(firstEntity).Value);
            Assert.AreEqual(2, first.Frame.GetReadOnly<PruneGameBComponent>(firstEntity).Value);
        }

        [TestCase(64, 128)]
        [TestCase(128, 64)]
        public void SecondSimulation_InRecomputeMode_LeavesALiveSimulationIntact(int firstMax, int secondMax)
        {
            ComponentStorageRegistry.AllowLayoutRecompute = true;
            var live = new EcsSimulation(firstMax, 4, 25);
            var f = live.Frame;
            var e = f.CreateEntity();
            f.Add(e, new PruneGameAComponent { Value = 11 });
            f.Add(e, new PruneGameBComponent { Value = 42 });
            var e2 = f.CreateEntity();
            f.Add(e2, new PruneGameBComponent { Value = 77 });
            ulong hashBefore = f.CalculateHash();

            _ = new EcsSimulation(secondMax, 4, 25);

            Assert.IsTrue(f.Has<PruneGameAComponent>(e));
            Assert.IsTrue(f.Has<PruneGameBComponent>(e));
            Assert.IsTrue(f.Has<PruneGameBComponent>(e2));
            Assert.AreEqual(11, f.GetReadOnly<PruneGameAComponent>(e).Value);
            Assert.AreEqual(42, f.GetReadOnly<PruneGameBComponent>(e).Value);
            Assert.AreEqual(77, f.GetReadOnly<PruneGameBComponent>(e2).Value);
            Assert.AreEqual(hashBefore, f.CalculateHash());

            var e3 = f.CreateEntity();
            f.Add(e3, new PruneGameAComponent { Value = 5 });
            Assert.AreEqual(5, f.GetReadOnly<PruneGameAComponent>(e3).Value);
            Assert.AreEqual(11, f.GetReadOnly<PruneGameAComponent>(e).Value);
        }

        [Test]
        public void ReflectableStorage_IsOfferedOnlyForTypesTheFrameReserves()
        {
            int b = ComponentStorageRegistry.GetTypeId<PruneGameBComponent>();
            var sim = new EcsSimulation(64, 4, 25, prunedComponentTypeIds: new[] { b });
            var e = sim.Frame.CreateEntity();
            sim.Frame.Add(e, new PruneGameAComponent { Value = 5 });

            Assert.IsFalse(sim.Frame.TryGetReflectableStorage(typeof(PruneGameBComponent), out _),
                "a pruned type has no storage in this frame");

            Assert.IsTrue(sim.Frame.TryGetReflectableStorage(typeof(PruneGameAComponent), out var view));
            Assert.IsTrue(view.Has(e.Index));
            Assert.AreEqual(5, ((PruneGameAComponent)view.GetBoxed(e.Index)).Value);
        }
    }
}
