using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

namespace xpTURN.Klotho.ECS.Tests
{
    // Unregistered component (no [KlothoComponent] attribute) — for error-path testing
    public struct UnregisteredComponent : IComponent
    {
        public int Dummy;

        public ulong GetHash(ulong hash) => 0UL;
        public int GetSerializedSize() => 4;
        public void Serialize(ref xpTURN.Klotho.Serialization.SpanWriter writer) => writer.WriteInt32(Dummy);
        public void Deserialize(ref xpTURN.Klotho.Serialization.SpanReader reader) => Dummy = reader.ReadInt32();
    }

    [TestFixture]
    public class FrameTests
    {
        private Frame _frame;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory configuration (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(16, _logger);
        }

        // --- Entity Lifecycle ---

        [Test]
        public void CreateEntity_ReturnsValidEntity()
        {
            var entity = _frame.CreateEntity();

            Assert.IsTrue(entity.IsValid);
            Assert.AreEqual(0, entity.Index);
        }

        [Test]
        public void DestroyEntity_MakesEntityDead()
        {
            var entity = _frame.CreateEntity();
            _frame.DestroyEntity(entity);

            Assert.IsFalse(_frame.Entities.IsAlive(entity));
        }

        // --- Component CRUD ---

        [Test]
        public void Add_ThenHas_ReturnsTrue()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            Assert.IsTrue(_frame.Has<HealthComponent>(entity));
        }

        [Test]
        public void Has_WithoutAdd_ReturnsFalse()
        {
            var entity = _frame.CreateEntity();

            Assert.IsFalse(_frame.Has<HealthComponent>(entity));
        }

        [Test]
        public void Get_ReturnsCorrectValue()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 75 });

            ref var health = ref _frame.Get<HealthComponent>(entity);
            Assert.AreEqual(100, health.MaxHealth);
            Assert.AreEqual(75, health.CurrentHealth);
        }

        [Test]
        public void Get_ReturnsRef_CanModify()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            ref var health = ref _frame.Get<HealthComponent>(entity);
            health.CurrentHealth = 50;

            Assert.AreEqual(50, _frame.Get<HealthComponent>(entity).CurrentHealth);
        }

        [Test]
        public void GetReadOnly_ReturnsCorrectValue()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });

            ref readonly var health = ref _frame.GetReadOnly<HealthComponent>(entity);
            Assert.AreEqual(80, health.CurrentHealth);
        }

        [Test]
        public void Remove_SetsHasFalse()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Remove<HealthComponent>(entity);

            Assert.IsFalse(_frame.Has<HealthComponent>(entity));
        }

        // --- Multiple Component Types ---

        [Test]
        public void MultipleComponentTypes_OnSameEntity()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(entity, new OwnerComponent { OwnerId = 1 });

            Assert.IsTrue(_frame.Has<HealthComponent>(entity));
            Assert.IsTrue(_frame.Has<OwnerComponent>(entity));
            Assert.AreEqual(1, _frame.Get<OwnerComponent>(entity).OwnerId);
        }

        [Test]
        public void MultipleEntities_IndependentComponents()
        {
            var e0 = _frame.CreateEntity();
            var e1 = _frame.CreateEntity();

            _frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 150 });

            Assert.AreEqual(100, _frame.Get<HealthComponent>(e0).MaxHealth);
            Assert.AreEqual(200, _frame.Get<HealthComponent>(e1).MaxHealth);
        }

        // --- Entity Create → Component Add → Access → Destroy flow ---

        [Test]
        public void FullLifecycle_CreateAddAccessDestroy()
        {
            var entity = _frame.CreateEntity();

            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Add(entity, new OwnerComponent { OwnerId = 42 });

            Assert.AreEqual(100, _frame.Get<HealthComponent>(entity).CurrentHealth);
            Assert.AreEqual(42, _frame.Get<OwnerComponent>(entity).OwnerId);

            ref var health = ref _frame.Get<HealthComponent>(entity);
            health.CurrentHealth = 0;
            Assert.AreEqual(0, _frame.Get<HealthComponent>(entity).CurrentHealth);

            _frame.Remove<HealthComponent>(entity);
            Assert.IsFalse(_frame.Has<HealthComponent>(entity));
            Assert.IsTrue(_frame.Has<OwnerComponent>(entity));

            _frame.DestroyEntity(entity);
            Assert.IsFalse(_frame.Entities.IsAlive(entity));
        }

        // --- CopyFrom (snapshot/rollback) ---

        [Test]
        public void CopyFrom_RestoresState()
        {
            var source = new Frame(16, _logger);
            source.Tick = 42;
            source.DeltaTimeMs = 16;

            var e0 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 75 });

            var target = new Frame(16, _logger);
            target.CopyFrom(source);

            Assert.AreEqual(42, target.Tick);
            Assert.AreEqual(16, target.DeltaTimeMs);
            Assert.IsTrue(target.Entities.IsAlive(e0));
            Assert.IsTrue(target.Has<HealthComponent>(e0));
            Assert.AreEqual(75, target.Get<HealthComponent>(e0).CurrentHealth);
        }

        [Test]
        public void CopyFrom_IsDeepCopy()
        {
            var source = new Frame(16, _logger);
            var entity = source.CreateEntity();
            source.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var target = new Frame(16, _logger);
            target.CopyFrom(source);

            // mutate source after copy — target must not be affected
            ref var sourceHealth = ref source.Get<HealthComponent>(entity);
            sourceHealth.CurrentHealth = 0;

            Assert.AreEqual(100, target.Get<HealthComponent>(entity).CurrentHealth);
        }

        // --- Clear ---

        [Test]
        public void Clear_ResetsAllState()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Tick = 10;

            _frame.Clear();

            Assert.AreEqual(0, _frame.Tick);
            Assert.AreEqual(0, _frame.Entities.Count);
            Assert.IsFalse(_frame.Entities.IsAlive(entity));
        }

        [Test]
        public void Clear_ResetsDeltaTimeMs()
        {
            _frame.DeltaTimeMs = 16;
            _frame.Clear();

            Assert.AreEqual(0, _frame.DeltaTimeMs);
        }

        // Verify that accessing an unregistered type throws InvalidOperationException.
        [Test]
        public void GetStorage_UnregisteredType_Throws()
        {
            var entity = _frame.CreateEntity();

            var ex = Assert.Throws<InvalidOperationException>(
                () => _frame.Add(entity, new UnregisteredComponent { Dummy = 1 }));
            StringAssert.Contains("UnregisteredComponent", ex.Message);
            StringAssert.Contains("not registered", ex.Message);
        }

        [Test]
        public void DestroyEntity_RemovesComponents()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            _frame.DestroyEntity(entity);

            Assert.IsFalse(_frame.Entities.IsAlive(entity));
            // RemoveAllComponents is called during DestroyEntity
            Assert.IsFalse(_frame.Has<HealthComponent>(entity));
        }

        // --- Sparse initialization ---

        // (1) Has<T>(0) = false immediately after new Frame
        [Test]
        public void Sparse_NewFrame_HasReturnsFalse()
        {
            var frame = new Frame(16, _logger);

            Assert.IsFalse(frame.Has<HealthComponent>(new EntityRef(0, 0)));
        }

        // (2) Has<T>(0) = false after Clear
        [Test]
        public void Sparse_AfterClear_HasReturnsFalse()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Clear();

            Assert.IsFalse(_frame.Has<HealthComponent>(new EntityRef(0, 0)));
        }

        // (3) Has<T>(0) = false after CopyFrom with empty source
        [Test]
        public void Sparse_AfterCopyFromEmpty_HasReturnsFalse()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var empty = new Frame(16, _logger);
            _frame.CopyFrom(empty);

            Assert.IsFalse(_frame.Has<HealthComponent>(new EntityRef(0, 0)));
        }

        // (4) Has<T>(0) = false after DeserializeFrom with empty data
        [Test]
        public void Sparse_AfterDeserializeFromEmpty_HasReturnsFalse()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            // serialize an empty frame and then deserialize
            var empty = new Frame(16, _logger);
            var data = empty.SerializeTo();
            _frame.DeserializeFrom(data);

            Assert.IsFalse(_frame.Has<HealthComponent>(new EntityRef(0, 0)));
        }

        // (5) Add entity 1 → Remove → Has<T>(1) = false
        [Test]
        public void Sparse_AfterAddRemove_HasReturnsFalse()
        {
            _frame.CreateEntity(); // index 0 (skip)
            var e1 = _frame.CreateEntity(); // index 1
            _frame.Add(e1, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            _frame.Remove<HealthComponent>(e1);

            Assert.IsFalse(_frame.Has<HealthComponent>(e1));
        }

        // (6) Add entity 0 → Has<T>(0) = true (positive control)
        [Test]
        public void Sparse_AfterAdd_HasReturnsTrue()
        {
            var entity = _frame.CreateEntity(); // index 0
            _frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            Assert.IsTrue(_frame.Has<HealthComponent>(entity));
        }
    }
}
