using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class FrameSerializationTests
    {
        private const int MaxEntities = 64;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Configure LoggerFactory (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        // --- Test #1: Frame_SerializeDeserialize_BitExact ---

        [Test]
        public void Frame_SerializeDeserialize_BitExact()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 42;
            source.DeltaTimeMs = 50;

            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 75 });
            source.Add(e0, new OwnerComponent { OwnerId = 1 });
            source.Add(e1, new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(10), FP64.FromInt(20), FP64.FromInt(30)),
                Rotation = FP64.FromInt(45),
                Scale = FPVector3.One
            });

            ulong hashBefore = source.CalculateHash();
            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);

            ulong hashAfter = target.CalculateHash();
            Assert.AreEqual(hashBefore, hashAfter);
        }

        // --- Test #2: Frame_SerializeDeserialize_EmptyFrame ---

        [Test]
        public void Frame_SerializeDeserialize_EmptyFrame()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 0;
            source.DeltaTimeMs = 50;

            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);

            Assert.AreEqual(0, target.Tick);
            Assert.AreEqual(50, target.DeltaTimeMs);
            Assert.AreEqual(0, target.Entities.Count);
            Assert.AreEqual(source.CalculateHash(), target.CalculateHash());
        }

        // --- Test #3: Frame_SerializeDeserialize_MaxEntities ---

        [Test]
        public void Frame_SerializeDeserialize_MaxEntities()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 999;
            source.DeltaTimeMs = 16;

            for (int i = 0; i < MaxEntities; i++)
            {
                var entity = source.CreateEntity();
                source.Add(entity, new HealthComponent { MaxHealth = i * 10, CurrentHealth = i * 5 });
                source.Add(entity, new OwnerComponent { OwnerId = i });
            }

            ulong hashBefore = source.CalculateHash();
            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);

            Assert.AreEqual(999, target.Tick);
            Assert.AreEqual(MaxEntities, target.Entities.Count);
            Assert.AreEqual(hashBefore, target.CalculateHash());

            // Spot-check a few entities
            var e0 = new EntityRef(0, 1);
            var eLast = new EntityRef(MaxEntities - 1, 1);
            Assert.AreEqual(0, target.Get<HealthComponent>(e0).MaxHealth);
            Assert.AreEqual(0, target.Get<OwnerComponent>(e0).OwnerId);
            Assert.AreEqual((MaxEntities - 1) * 10, target.Get<HealthComponent>(eLast).MaxHealth);
            Assert.AreEqual(MaxEntities - 1, target.Get<OwnerComponent>(eLast).OwnerId);
        }

        // --- Test #4: Frame_SerializeDeserialize_MixedComponents ---

        [Test]
        public void Frame_SerializeDeserialize_MixedComponents()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 10;
            source.DeltaTimeMs = 50;

            // Entity 0: Health only
            var e0 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            // Entity 1: Owner + Transform
            var e1 = source.CreateEntity();
            source.Add(e1, new OwnerComponent { OwnerId = 2 });
            source.Add(e1, new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(10)),
                Rotation = FP64.FromInt(90),
                Scale = FPVector3.One
            });

            // Entity 2: all three
            var e2 = source.CreateEntity();
            source.Add(e2, new HealthComponent { MaxHealth = 200, CurrentHealth = 150 });
            source.Add(e2, new OwnerComponent { OwnerId = 3 });
            source.Add(e2, new TransformComponent
            {
                Position = FPVector3.Zero,
                Rotation = FP64.Zero,
                Scale = FPVector3.One
            });

            // Entity 3: no components
            source.CreateEntity();

            ulong hashBefore = source.CalculateHash();
            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);

            Assert.AreEqual(hashBefore, target.CalculateHash());
            Assert.AreEqual(4, target.Entities.Count);

            // e0: Health only
            Assert.IsTrue(target.Has<HealthComponent>(e0));
            Assert.IsFalse(target.Has<OwnerComponent>(e0));
            Assert.AreEqual(100, target.Get<HealthComponent>(e0).CurrentHealth);

            // e1: Owner + Transform
            Assert.IsFalse(target.Has<HealthComponent>(e1));
            Assert.IsTrue(target.Has<OwnerComponent>(e1));
            Assert.IsTrue(target.Has<TransformComponent>(e1));
            Assert.AreEqual(2, target.Get<OwnerComponent>(e1).OwnerId);
            Assert.AreEqual(FP64.FromInt(5).RawValue, target.Get<TransformComponent>(e1).Position.x.RawValue);

            // e2: all three
            Assert.IsTrue(target.Has<HealthComponent>(e2));
            Assert.IsTrue(target.Has<OwnerComponent>(e2));
            Assert.IsTrue(target.Has<TransformComponent>(e2));
            Assert.AreEqual(150, target.Get<HealthComponent>(e2).CurrentHealth);

            // e3: no components
            var e3 = new EntityRef(3, 1);
            Assert.IsTrue(target.Entities.IsAlive(e3));
            Assert.IsFalse(target.Has<HealthComponent>(e3));
        }

        // --- Test #5: EntityManager_SerializeDeserialize ---

        [Test]
        public void EntityManager_SerializeDeserialize()
        {
            var source = new EntityManager(MaxEntities);
            var e0 = source.Create();
            var e1 = source.Create();
            var e2 = source.Create();
            source.Destroy(e1); // creates an empty slot + adds a freeList entry

            // Serialize
            var buffer = new byte[source.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            source.Serialize(ref writer);

            // Deserialize
            var target = new EntityManager(MaxEntities);
            var reader = new SpanReader(buffer);
            target.Deserialize(ref reader);

            Assert.AreEqual(source.Count, target.Count);
            Assert.IsTrue(target.IsAlive(e0));
            Assert.IsFalse(target.IsAlive(e1));
            Assert.IsTrue(target.IsAlive(e2));

            // FreeList restored: next Create must reuse e1's slot
            var reused = target.Create();
            Assert.AreEqual(e1.Index, reused.Index);
            Assert.AreEqual(e1.Version + 1, reused.Version);
        }

        // --- Additional: roundtrip serialization with destroyed entities + freeList ---

        [Test]
        public void Frame_SerializeDeserialize_WithDestroyedEntities()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 77;
            source.DeltaTimeMs = 50;

            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            var e2 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            source.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            source.Add(e2, new OwnerComponent { OwnerId = 42 });
            source.DestroyEntity(e1);

            ulong hashBefore = source.CalculateHash();
            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);

            Assert.AreEqual(77, target.Tick);
            Assert.AreEqual(hashBefore, target.CalculateHash());
            Assert.AreEqual(2, target.Entities.Count);
            Assert.IsTrue(target.Entities.IsAlive(e0));
            Assert.IsFalse(target.Entities.IsAlive(e1));
            Assert.IsTrue(target.Entities.IsAlive(e2));
            Assert.AreEqual(100, target.Get<HealthComponent>(e0).CurrentHealth);
            Assert.AreEqual(42, target.Get<OwnerComponent>(e2).OwnerId);
        }

        // --- Additional: double roundtrip serialization (serialize → deserialize → serialize → compare) ---

        [Test]
        public void Frame_SerializeDeserialize_DoubleRoundtrip()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 123;
            source.DeltaTimeMs = 33;

            var e0 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 50 });
            source.Add(e0, new OwnerComponent { OwnerId = 7 });

            byte[] data1 = source.SerializeTo();

            var mid = new Frame(MaxEntities, _logger);
            mid.DeserializeFrom(data1);

            byte[] data2 = mid.SerializeTo();

            Assert.AreEqual(data1.Length, data2.Length);
            for (int i = 0; i < data1.Length; i++)
                Assert.AreEqual(data1[i], data2[i], $"Byte mismatch at position {i}");
        }

        // --- OPT-1: StreamPool buffer reuse ---

        [Test]
        public void OPT1_SerializeTo_ReturnsExactSizeArray()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 10;
            source.DeltaTimeMs = 50;
            var e0 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 75 });

            byte[] data = source.SerializeTo();

            // exact-size: SerializeTo result is the actual data size, not the StreamPool bucket size
            int estimated = source.EstimateSerializedSize();
            Assert.LessOrEqual(data.Length, estimated);
            Assert.Greater(data.Length, 0);

            // Verify that deserialization works
            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);
            Assert.AreEqual(source.CalculateHash(), target.CalculateHash());
        }

        [Test]
        public void OPT1_SerializeTo_LargeFrame_Over64KB()
        {
            // Over 64KB: StreamPool cannot pool → verify direct-allocation fallback
            const int largeMax = 1024;
            var source = new Frame(largeMax, _logger);
            source.Tick = 1;
            source.DeltaTimeMs = 50;

            for (int i = 0; i < largeMax; i++)
            {
                var entity = source.CreateEntity();
                source.Add(entity, new TransformComponent
                {
                    Position = new FPVector3(FP64.FromInt(i), FP64.FromInt(i), FP64.FromInt(i)),
                    Rotation = FP64.FromInt(i),
                    Scale = FPVector3.One
                });
            }

            int estimated = source.EstimateSerializedSize();
            Assert.Greater(estimated, 65536,
                "Test precondition: estimated size must exceed 64KB");

            byte[] data = source.SerializeTo();
            var target = new Frame(largeMax, _logger);
            target.DeserializeFrom(data);
            Assert.AreEqual(source.CalculateHash(), target.CalculateHash());
        }

        // --- OPT-2: Bulk serialization format ---

        [Test]
        public void OPT2_BulkSerialization_AfterRemove()
        {
            // Serialize/deserialize after Remove with a dense swap
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 1;
            source.DeltaTimeMs = 50;

            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            var e2 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            source.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            source.Add(e2, new HealthComponent { MaxHealth = 300, CurrentHealth = 300 });
            source.Remove<HealthComponent>(e1); // dense swap: e2's data moves into e1's slot

            ulong hashBefore = source.CalculateHash();
            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);

            Assert.AreEqual(hashBefore, target.CalculateHash());
            Assert.IsTrue(target.Has<HealthComponent>(e0));
            Assert.IsFalse(target.Has<HealthComponent>(e1));
            Assert.IsTrue(target.Has<HealthComponent>(e2));
            Assert.AreEqual(300, target.Get<HealthComponent>(e2).CurrentHealth);
        }

        // --- OPT-3: SerializeToWithHash single pass ---

        [Test]
        public void OPT3_SerializeToWithHash_MatchesSeparateCalls()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 42;
            source.DeltaTimeMs = 50;

            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 75 });
            source.Add(e0, new OwnerComponent { OwnerId = 1 });
            source.Add(e1, new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(10), FP64.FromInt(20), FP64.FromInt(30)),
                Rotation = FP64.FromInt(45),
                Scale = FPVector3.One
            });

            // Separate calls
            byte[] dataSeparate = source.SerializeTo();
            ulong hashSeparate = source.CalculateHash();

            // Single pass
            var (dataCombined, hashCombined) = source.SerializeToWithHash();

            // Hashes match
            Assert.AreEqual(hashSeparate, hashCombined);

            // Serialized bytes match
            Assert.AreEqual(dataSeparate.Length, dataCombined.Length);
            for (int i = 0; i < dataSeparate.Length; i++)
                Assert.AreEqual(dataSeparate[i], dataCombined[i], $"Byte mismatch at position {i}");
        }

        [Test]
        public void OPT3_SerializeToWithHash_EmptyFrame()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 0;
            source.DeltaTimeMs = 50;

            ulong hashSeparate = source.CalculateHash();
            var (data, hashCombined) = source.SerializeToWithHash();

            Assert.AreEqual(hashSeparate, hashCombined);

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);
            Assert.AreEqual(source.CalculateHash(), target.CalculateHash());
        }

        [Test]
        public void OPT3_SerializeToWithHash_WithDestroyedEntities()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 77;
            source.DeltaTimeMs = 50;

            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            var e2 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            source.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            source.Add(e2, new OwnerComponent { OwnerId = 42 });
            source.DestroyEntity(e1);

            ulong hashSeparate = source.CalculateHash();
            var (data, hashCombined) = source.SerializeToWithHash();

            Assert.AreEqual(hashSeparate, hashCombined);

            var target = new Frame(MaxEntities, _logger);
            target.DeserializeFrom(data);
            Assert.AreEqual(hashSeparate, target.CalculateHash());
        }

        // --- OPT-4: DeserializeFrom bool[] reuse ---

        [Test]
        public void OPT4_DeserializeFrom_RepeatedCalls_NoGrowth()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 1;
            source.DeltaTimeMs = 50;
            var e0 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            byte[] data = source.SerializeTo();

            var target = new Frame(MaxEntities, _logger);

            // Two consecutive deserializations — bool[] reused, no GC
            target.DeserializeFrom(data);
            ulong hash1 = target.CalculateHash();

            source.Tick = 2;
            byte[] data2 = source.SerializeTo();
            target.DeserializeFrom(data2);
            ulong hash2 = target.CalculateHash();

            Assert.AreEqual(source.CalculateHash(), hash2);
            Assert.AreNotEqual(hash1, hash2); // Tick changed so hashes differ
        }

        [Test]
        public void OPT4_DeserializeFrom_ClearsAbsentStorages()
        {
            // source has Health only, target has Health+Owner → verify Owner is cleared
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 1;
            source.DeltaTimeMs = 50;
            var e0 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var target = new Frame(MaxEntities, _logger);
            var te0 = target.CreateEntity();
            target.Add(te0, new HealthComponent { MaxHealth = 50, CurrentHealth = 50 });
            target.Add(te0, new OwnerComponent { OwnerId = 99 });

            byte[] data = source.SerializeTo();
            target.DeserializeFrom(data);

            // Health is restored to source value
            Assert.IsTrue(target.Has<HealthComponent>(e0));
            Assert.AreEqual(100, target.Get<HealthComponent>(e0).MaxHealth);

            // Owner is not in source so it is cleared
            Assert.IsFalse(target.Has<OwnerComponent>(e0));
        }
    }
}
