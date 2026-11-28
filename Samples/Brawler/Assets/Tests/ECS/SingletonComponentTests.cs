using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.ECS.Tests
{
    // Dummy singleton component for guard / API testing.
    // typeId 9003 sits in the Tests range (9000+) to avoid collision with framework/sample slots.
    [KlothoComponent(9003)]
    [KlothoSingletonComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct DummySingletonComponent : IComponent
    {
        public int Value;
    }

    // Non-singleton counterpart at typeId 9004 — verifies the guard does not fire
    // on components without the [KlothoSingletonComponent] attribute.
    [KlothoComponent(9004)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct DummyNonSingletonComponent : IComponent
    {
        public int Value;
    }

    // Singleton at the highest typeId so its storage lands at the heap tail.
    // This is required to exercise the dense-span bound in Frame.RemoveFromStorage: when any
    // other typeId follows the singleton, the over-long span stays within heap.Length and the
    // out-of-range read never surfaces, so a heap-tail placement is the only reliable trigger.
    [KlothoComponent(9999)]
    [KlothoSingletonComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct TailSingletonComponent : IComponent
    {
        public int Value;
    }

    [TestFixture]
    public class SingletonComponentTests
    {
        private Frame _frame;
        IKLogger _logger = null;

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
            _frame = new Frame(16, _logger);
        }

        [Test]
        public void SingletonGuard_SecondAdd_Throws()
        {
            var e1 = _frame.CreateEntity();
            _frame.Add(e1, new DummySingletonComponent { Value = 1 });

            var e2 = _frame.CreateEntity();
            Assert.Throws<InvalidOperationException>(() =>
                _frame.Add(e2, new DummySingletonComponent { Value = 2 }));
        }

        [Test]
        public void NonSingleton_MultipleAdd_DoesNotThrow()
        {
            var e1 = _frame.CreateEntity();
            var e2 = _frame.CreateEntity();
            _frame.Add(e1, new DummyNonSingletonComponent { Value = 1 });
            Assert.DoesNotThrow(() =>
                _frame.Add(e2, new DummyNonSingletonComponent { Value = 2 }));
        }

        [Test]
        public void GetSingleton_Mutable_RoundtripsWrites()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 42 });

            ref var c = ref _frame.GetSingleton<DummySingletonComponent>();
            Assert.AreEqual(42, c.Value);

            c.Value = 99;
            Assert.AreEqual(99, _frame.GetSingleton<DummySingletonComponent>().Value);
        }

        [Test]
        public void GetReadOnlySingleton_ReturnsExpectedValue()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 7 });

            ref readonly var c = ref _frame.GetReadOnlySingleton<DummySingletonComponent>();
            Assert.AreEqual(7, c.Value);
        }

        [Test]
        public void GetSingleton_NoEntity_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                _frame.GetSingleton<DummySingletonComponent>());
        }

        [Test]
        public void GetReadOnlySingleton_NoEntity_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                ref readonly var _ = ref _frame.GetReadOnlySingleton<DummySingletonComponent>();
            });
        }

        [Test]
        public void TryGetSingleton_NoEntity_ReturnsFalse()
        {
            bool ok = _frame.TryGetSingleton<DummySingletonComponent>(out var entity);
            Assert.IsFalse(ok);
            Assert.IsFalse(entity.IsValid);
        }

        [Test]
        public void TryGetSingleton_OneEntity_ReturnsTrueWithCorrectEntity()
        {
            var created = _frame.CreateEntity();
            _frame.Add(created, new DummySingletonComponent { Value = 5 });

            bool ok = _frame.TryGetSingleton<DummySingletonComponent>(out var entity);
            Assert.IsTrue(ok);
            Assert.AreEqual(created.Index, entity.Index);
        }

        // Dense and components shrink to a single slot for singletons, while sparse
        // (entity-indexed) and the public Capacity stay at maxEntities.
        [Test]
        public void Layout_Singleton_DenseComponentsShrinkToOneSlot()
        {
            int singletonId    = ComponentStorageRegistry.GetTypeId<DummySingletonComponent>();
            int nonSingletonId = ComponentStorageRegistry.GetTypeId<DummyNonSingletonComponent>();

            ref readonly var singletonLayout    = ref _frame.Layout.GetLayout(singletonId);
            ref readonly var nonSingletonLayout = ref _frame.Layout.GetLayout(nonSingletonId);

            Assert.AreEqual(1, singletonLayout.SlotCapacity);
            Assert.AreEqual(_frame.MaxEntities, singletonLayout.Capacity);

            Assert.AreEqual(_frame.MaxEntities, nonSingletonLayout.SlotCapacity);
            Assert.AreEqual(nonSingletonLayout.Capacity, nonSingletonLayout.SlotCapacity);
        }

        // Destroying the carrier of a heap-tail singleton must not over-read the now
        // single-slot dense region; an unbounded read here throws ArgumentOutOfRangeException.
        [Test]
        public void DestroyEntity_TailSingletonCarrier_DoesNotThrow()
        {
            // Bump the carrier off index 0 to also cover a non-trivial entity index.
            _frame.CreateEntity();
            _frame.CreateEntity();
            var carrier = _frame.CreateEntity();
            _frame.Add(carrier, new TailSingletonComponent { Value = 123 });

            Assert.DoesNotThrow(() => _frame.DestroyEntity(carrier));
            Assert.IsFalse(_frame.TryGetSingleton<TailSingletonComponent>(out _));
        }

        // Covers the generic single-component remove path (ComponentStorageFlat.Remove),
        // which is distinct from the byte-level RemoveFromStorage exercised by DestroyEntity.
        [Test]
        public void Remove_SingletonComponent_ClearsAndAllowsReAdd()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 11 });

            _frame.Remove<DummySingletonComponent>(entity);
            Assert.IsFalse(_frame.Has<DummySingletonComponent>(entity));

            Assert.DoesNotThrow(() =>
                _frame.Add(entity, new DummySingletonComponent { Value = 22 }));
            Assert.AreEqual(22, _frame.GetSingleton<DummySingletonComponent>().Value);
        }

        // Serialize, deserialize, then serialize again yields identical bytes for singletons
        // because the count=0|1 byte format is unchanged, and the hash is preserved.
        [Test]
        public void Serialize_SingletonRoundtrip_IsIdempotent()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 314 });
            ulong hashBefore = _frame.CalculateHash();

            byte[] data = _frame.SerializeTo();
            var target = new Frame(16, _logger);
            target.DeserializeFrom(data);

            Assert.AreEqual(hashBefore, target.CalculateHash());
            Assert.AreEqual(314, target.GetSingleton<DummySingletonComponent>().Value);
            Assert.AreEqual(data, target.SerializeTo());
        }

        // The full-heap CopyFrom snapshot preserves both the singleton value and the frame hash.
        [Test]
        public void CopyFrom_SingletonRoundtrip_PreservesValueAndHash()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 271 });
            ulong hashBefore = _frame.CalculateHash();

            var target = new Frame(16, _logger);
            target.CopyFrom(_frame);

            Assert.AreEqual(hashBefore, target.CalculateHash());
            Assert.IsTrue(target.TryGetSingleton<DummySingletonComponent>(out _));
            Assert.AreEqual(271, target.GetSingleton<DummySingletonComponent>().Value);
        }
    }
}
