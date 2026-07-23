using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.ECS.Tests
{
    // maxCount test components. typeIds in the 9200 block to avoid the 9000-9101/9999 slots
    // already used by other test fixtures.

    // Non-singleton with MaxCount=4 — dense/components cap at min(4, maxEntities).
    [KlothoComponent(9200, MaxCount = 4)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct MaxCountFourComponent : IComponent
    {
        public int Value;
    }

    // No MaxCount — baseline: SlotCapacity must stay == maxEntities.
    [KlothoComponent(9201)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct MaxCountUnsetComponent : IComponent
    {
        public int Value;
    }

    // Singleton + MaxCount=8 — singleton wins: SlotCapacity must be 1 (MaxCount ignored at runtime).
    // The KLSG_ECS006 warning (MaxCount on a singleton) is deliberate here — this fixture exists to prove
    // the singleton-priority behavior — so it is suppressed for this one declaration.
#pragma warning disable KLSG_ECS006
    [KlothoComponent(9202, MaxCount = 8)]
    [KlothoSingletonComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct MaxCountSingletonComponent : IComponent
    {
        public int Value;
    }
#pragma warning restore KLSG_ECS006

    [TestFixture]
    public class ComponentMaxCountTests
    {
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            ComponentStorageRegistry.ResetForTesting();
        }

        // Layout: SlotCapacity clamps to min(maxCount, maxEntities); sparse Capacity stays maxEntities.
        [Test]
        public void Layout_MaxCount_ShrinksSlotCapacity_SparseUnchanged()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);

            int fourId = ComponentStorageRegistry.GetTypeId<MaxCountFourComponent>();
            ref readonly var four = ref ComponentStorageRegistry.GetLayout(fourId);
            Assert.AreEqual(4, four.SlotCapacity, "dense/components slots cap at maxCount");
            Assert.AreEqual(256, four.Capacity, "sparse (entity-index domain) stays maxEntities");

            // Baseline: unspecified maxCount → SlotCapacity == maxEntities.
            int unsetId = ComponentStorageRegistry.GetTypeId<MaxCountUnsetComponent>();
            ref readonly var unset = ref ComponentStorageRegistry.GetLayout(unsetId);
            Assert.AreEqual(256, unset.SlotCapacity);
            Assert.AreEqual(256, unset.Capacity);
        }

        // maxCount > maxEntities clamps down to maxEntities.
        [Test]
        public void Layout_MaxCountAboveMaxEntities_ClampsToMaxEntities()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(2);

            int fourId = ComponentStorageRegistry.GetTypeId<MaxCountFourComponent>();
            ref readonly var four = ref ComponentStorageRegistry.GetLayout(fourId);
            Assert.AreEqual(2, four.SlotCapacity, "min(4, 2) == 2");
        }

        // Overflow threshold: N adds succeed, (N+1)th throws; Remove frees a slot for re-add.
        [Test]
        public void Overflow_ExceedingMaxCount_Throws_RemoveThenReAddSucceeds()
        {
            var frame = new Frame(256, _logger);

            var entities = new EntityRef[4];
            for (int i = 0; i < 4; i++)
            {
                entities[i] = frame.CreateEntity();
                frame.Add(entities[i], new MaxCountFourComponent { Value = i });
            }

            var overflow = frame.CreateEntity();
            Assert.Throws<InvalidOperationException>(
                () => frame.Add(overflow, new MaxCountFourComponent { Value = 99 }));

            // Free one slot, then the pending add fits.
            frame.Remove<MaxCountFourComponent>(entities[0]);
            Assert.DoesNotThrow(
                () => frame.Add(overflow, new MaxCountFourComponent { Value = 42 }));
        }

        // Singleton priority: singleton forces SlotCapacity 1 even with MaxCount=8.
        [Test]
        public void SingletonWithMaxCount_SlotCapacityIsOne()
        {
            ComponentStorageRegistry.EnsureLayoutComputed(256);

            int id = ComponentStorageRegistry.GetTypeId<MaxCountSingletonComponent>();
            ref readonly var layout = ref ComponentStorageRegistry.GetLayout(id);
            Assert.AreEqual(1, layout.SlotCapacity, "singleton wins over MaxCount");
            Assert.IsTrue(ComponentStorageRegistry.IsSingleton(id));
        }

        // Config override wins over the attribute default; an unlisted type falls back to its attribute;
        // with neither, the slot count stays at maxEntities.
        [Test]
        public void ConfigOverride_BeatsAttribute_UnlistedFallsBackToAttribute()
        {
            int fourId = ComponentStorageRegistry.GetTypeId<MaxCountFourComponent>();   // attribute maxCount = 4
            int unsetId = ComponentStorageRegistry.GetTypeId<MaxCountUnsetComponent>();  // no attribute

            var overrides = new Dictionary<int, int> { { fourId, 8 } };   // override 8 > attribute 4
            ComponentStorageRegistry.EnsureLayoutComputed(256, overrides);

            Assert.AreEqual(8, ComponentStorageRegistry.GetLayout(fourId).SlotCapacity, "override wins over attribute");
            Assert.AreEqual(256, ComponentStorageRegistry.GetLayout(unsetId).SlotCapacity, "unlisted + no attribute → maxEntities");
        }

        // The override map is part of the freeze idempotency key: same map = idempotent, different = recompute.
        [Test]
        public void ConfigOverride_DifferentMap_RecomputesLayout()
        {
            int fourId = ComponentStorageRegistry.GetTypeId<MaxCountFourComponent>();

            ComponentStorageRegistry.EnsureLayoutComputed(256, new Dictionary<int, int> { { fourId, 8 } });
            Assert.AreEqual(8, ComponentStorageRegistry.GetLayout(fourId).SlotCapacity);

            // Same maxEntities, different map → editor/test auto-recompute reflects the new value.
            ComponentStorageRegistry.EnsureLayoutComputed(256, new Dictionary<int, int> { { fourId, 32 } });
            Assert.AreEqual(32, ComponentStorageRegistry.GetLayout(fourId).SlotCapacity);
        }

        // Wire round-trip under a reduced slotCapacity: the count-based codec is unaffected — serialize
        // and hash read only the live [0,count) slots, so slotCapacity never enters the bytes or hash.
        [Test]
        public void Serialize_UnderReducedSlotCapacity_RoundTrips()
        {
            var src = new Frame(256, _logger);
            var e0 = src.CreateEntity(); src.Add(e0, new MaxCountFourComponent { Value = 10 });
            var e1 = src.CreateEntity(); src.Add(e1, new MaxCountFourComponent { Value = 20 });
            var e2 = src.CreateEntity(); src.Add(e2, new MaxCountFourComponent { Value = 30 });

            ulong srcHash = src.CalculateHash();
            byte[] bytes = src.SerializeTo();

            var dst = new Frame(256, _logger);
            dst.DeserializeFrom(bytes);

            Assert.AreEqual(srcHash, dst.CalculateHash(), "hash invariant across serialize round-trip");
            Assert.AreEqual(3, dst.GetStorage<MaxCountFourComponent>().Count, "count-based codec restores all instances under slotCapacity=4");
        }
    }
}
