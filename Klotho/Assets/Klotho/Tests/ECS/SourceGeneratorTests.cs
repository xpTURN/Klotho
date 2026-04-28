using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class SourceGeneratorTests
    {
        private const int MaxEntities = 16;

        // --- TYPE_ID constants ---

        [Test]
        public void TypeId_MatchesAttribute()
        {
            Assert.AreEqual(1, TransformComponent.TYPE_ID);
            Assert.AreEqual(2, OwnerComponent.TYPE_ID);
            Assert.AreEqual(21, HealthComponent.TYPE_ID);
            Assert.AreEqual(22, VelocityComponent.TYPE_ID);
            Assert.AreEqual(23, MovementComponent.TYPE_ID);
            Assert.AreEqual(24, CombatComponent.TYPE_ID);
        }

        // --- Serialize / Deserialize round trip (int fields) ---

        [Test]
        public void HealthComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new HealthComponent { MaxHealth = 100, CurrentHealth = 75 };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            Assert.AreEqual(original.GetSerializedSize(), writer.Position);

            var reader = new SpanReader(buffer);
            var restored = new HealthComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.MaxHealth, restored.MaxHealth);
            Assert.AreEqual(original.CurrentHealth, restored.CurrentHealth);
        }

        [Test]
        public void OwnerComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new OwnerComponent { OwnerId = 42 };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            Assert.AreEqual(original.GetSerializedSize(), writer.Position);

            var reader = new SpanReader(buffer);
            var restored = new OwnerComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.OwnerId, restored.OwnerId);
        }

        [Test]
        public void CombatComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new CombatComponent { AttackDamage = 15, AttackRange = 3 };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            Assert.AreEqual(original.GetSerializedSize(), writer.Position);

            var reader = new SpanReader(buffer);
            var restored = new CombatComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.AttackDamage, restored.AttackDamage);
            Assert.AreEqual(original.AttackRange, restored.AttackRange);
        }

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

        // --- Serialize / Deserialize round trip (FP types) ---

        [Test]
        public void TransformComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(10), FP64.FromInt(20), FP64.FromInt(30)),
                Rotation = FP64.FromInt(90),
                Scale = new FPVector3(FP64.One, FP64.One, FP64.One)
            };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            Assert.AreEqual(original.GetSerializedSize(), writer.Position);

            var reader = new SpanReader(buffer);
            var restored = new TransformComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.Position.x.RawValue, restored.Position.x.RawValue);
            Assert.AreEqual(original.Position.y.RawValue, restored.Position.y.RawValue);
            Assert.AreEqual(original.Position.z.RawValue, restored.Position.z.RawValue);
            Assert.AreEqual(original.Rotation.RawValue, restored.Rotation.RawValue);
            Assert.AreEqual(original.Scale.x.RawValue, restored.Scale.x.RawValue);
        }

        [Test]
        public void VelocityComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new VelocityComponent
            {
                Velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(-3))
            };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            Assert.AreEqual(original.GetSerializedSize(), writer.Position);

            var reader = new SpanReader(buffer);
            var restored = new VelocityComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.Velocity.x.RawValue, restored.Velocity.x.RawValue);
            Assert.AreEqual(original.Velocity.y.RawValue, restored.Velocity.y.RawValue);
            Assert.AreEqual(original.Velocity.z.RawValue, restored.Velocity.z.RawValue);
        }

        [Test]
        public void MovementComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new MovementComponent
            {
                MoveSpeed = FP64.FromInt(5),
                TargetPosition = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.FromInt(50)),
                IsMoving = true
            };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            Assert.AreEqual(original.GetSerializedSize(), writer.Position);

            var reader = new SpanReader(buffer);
            var restored = new MovementComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.MoveSpeed.RawValue, restored.MoveSpeed.RawValue);
            Assert.AreEqual(original.TargetPosition.x.RawValue, restored.TargetPosition.x.RawValue);
            Assert.AreEqual(original.TargetPosition.y.RawValue, restored.TargetPosition.y.RawValue);
            Assert.AreEqual(original.TargetPosition.z.RawValue, restored.TargetPosition.z.RawValue);
            Assert.AreEqual(original.IsMoving, restored.IsMoving);
        }

        // --- Serialized size ---

        [Test]
        public void GetSerializedSize_MatchesExpected()
        {
            Assert.AreEqual(8, new HealthComponent().GetSerializedSize());    // 2 x int = 8
            Assert.AreEqual(4, new OwnerComponent().GetSerializedSize());     // 1 x int = 4
            Assert.AreEqual(8, new CombatComponent().GetSerializedSize());    // 2 x int = 8
            Assert.AreEqual(92, new TransformComponent().GetSerializedSize()); // FPVector3(24) + FP64(8) + FPVector3(24) + FPVector3(24) + FP64(8) + int(4)
            Assert.AreEqual(24, new VelocityComponent().GetSerializedSize()); // FPVector3(24)
            Assert.AreEqual(33, new MovementComponent().GetSerializedSize()); // FP64(8) + FPVector3(24) + bool(1)
        }

        // --- Hash determinism ---

        [Test]
        public void GetHash_SameValues_ProduceSameHash()
        {
            var a = new HealthComponent { MaxHealth = 100, CurrentHealth = 50 };
            var b = new HealthComponent { MaxHealth = 100, CurrentHealth = 50 };

            ulong seed = FPHash.FNV_OFFSET;
            Assert.AreEqual(a.GetHash(seed), b.GetHash(seed));
        }

        [Test]
        public void GetHash_DifferentValues_ProduceDifferentHash()
        {
            var a = new HealthComponent { MaxHealth = 100, CurrentHealth = 50 };
            var b = new HealthComponent { MaxHealth = 100, CurrentHealth = 49 };

            ulong seed = FPHash.FNV_OFFSET;
            Assert.AreNotEqual(a.GetHash(seed), b.GetHash(seed));
        }

        [Test]
        public void GetHash_MovementComponent_Deterministic()
        {
            var a = new MovementComponent
            {
                MoveSpeed = FP64.FromInt(5),
                TargetPosition = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.FromInt(20)),
                IsMoving = true
            };
            var b = a;

            ulong seed = FPHash.FNV_OFFSET;
            Assert.AreEqual(a.GetHash(seed), b.GetHash(seed));

            var c = a;
            c.IsMoving = false;
            Assert.AreNotEqual(a.GetHash(seed), c.GetHash(seed));
        }

        [Test]
        public void GetHash_FPTypes_Deterministic()
        {
            var a = new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3)),
                Rotation = FP64.FromInt(45),
                Scale = FPVector3.One
            };
            var b = a; // copy

            ulong seed = FPHash.FNV_OFFSET;
            Assert.AreEqual(a.GetHash(seed), b.GetHash(seed));
        }

        // --- Frame.Components.g.cs: storage registration ---

        [Test]
        public void Frame_ComponentStorages_RegisteredByGenerator()
        {
            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();

            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            frame.Add(entity, new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(1), FP64.FromInt(2), FP64.FromInt(3)),
                Rotation = FP64.Zero,
                Scale = FPVector3.One
            });
            frame.Add(entity, new OwnerComponent { OwnerId = 1 });
            frame.Add(entity, new VelocityComponent { Velocity = FPVector3.Zero });
            frame.Add(entity, new CombatComponent { AttackDamage = 10, AttackRange = 2 });

            Assert.IsTrue(frame.Has<HealthComponent>(entity));
            Assert.IsTrue(frame.Has<TransformComponent>(entity));
            Assert.IsTrue(frame.Has<OwnerComponent>(entity));
            Assert.IsTrue(frame.Has<VelocityComponent>(entity));
            Assert.IsTrue(frame.Has<CombatComponent>(entity));

            Assert.AreEqual(100, frame.Get<HealthComponent>(entity).CurrentHealth);
            Assert.AreEqual(1, frame.Get<OwnerComponent>(entity).OwnerId);
        }

        // --- Frame.CalculateHash tests ---

        [Test]
        public void Frame_CalculateHash_Deterministic()
        {
            var frame1 = new Frame(MaxEntities, _logger);
            var frame2 = new Frame(MaxEntities, _logger);

            var e1 = frame1.CreateEntity();
            frame1.Add(e1, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            frame1.Add(e1, new OwnerComponent { OwnerId = 1 });
            frame1.Tick = 5;

            var e2 = frame2.CreateEntity();
            frame2.Add(e2, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            frame2.Add(e2, new OwnerComponent { OwnerId = 1 });
            frame2.Tick = 5;

            Assert.AreEqual(frame1.CalculateHash(), frame2.CalculateHash());
        }

        [Test]
        public void Frame_CalculateHash_DiffersOnChange()
        {
            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            frame.Tick = 1;

            ulong hash1 = frame.CalculateHash();

            frame.Get<HealthComponent>(entity).CurrentHealth = 99;
            ulong hash2 = frame.CalculateHash();

            Assert.AreNotEqual(hash1, hash2);
        }

        // --- CopyFrom preserves hash ---

        [Test]
        public void Frame_CopyFrom_PreservesHash()
        {
            var source = new Frame(MaxEntities, _logger);
            var entity = source.CreateEntity();
            source.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            source.Add(entity, new OwnerComponent { OwnerId = 1 });
            source.Tick = 10;

            var target = new Frame(MaxEntities, _logger);
            target.CopyFrom(source);

            Assert.AreEqual(source.CalculateHash(), target.CalculateHash());
        }

        [Test]
        public void Frame_CalculateHash_EmptyFrame_Deterministic()
        {
            var frame1 = new Frame(MaxEntities, _logger);
            var frame2 = new Frame(MaxEntities, _logger);

            frame1.Tick = 0;
            frame2.Tick = 0;

            ulong hash1 = frame1.CalculateHash();
            ulong hash2 = frame2.CalculateHash();

            Assert.AreEqual(hash1, hash2);
            Assert.AreNotEqual(0UL, hash1);
        }

        [Test]
        public void Frame_CalculateHash_AfterEntityDestroy()
        {
            var frame = new Frame(MaxEntities, _logger);
            var e0 = frame.CreateEntity();
            frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            frame.Tick = 1;

            ulong hashBefore = frame.CalculateHash();

            frame.DestroyEntity(e0);

            ulong hashAfter = frame.CalculateHash();

            // Entity count changed (1 -> 0), so the hash must differ
            Assert.AreNotEqual(hashBefore, hashAfter);
        }

        // --- Serialization boundary values ---

        [Test]
        public void Serialization_BoundaryValues_ZeroAndNegative()
        {
            var original = new HealthComponent { MaxHealth = 0, CurrentHealth = -1 };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            var reader = new SpanReader(buffer);
            var restored = new HealthComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(0, restored.MaxHealth);
            Assert.AreEqual(-1, restored.CurrentHealth);
        }

        [Test]
        public void Serialization_BoundaryValues_MaxInt()
        {
            var original = new HealthComponent { MaxHealth = int.MaxValue, CurrentHealth = int.MinValue };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            var reader = new SpanReader(buffer);
            var restored = new HealthComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(int.MaxValue, restored.MaxHealth);
            Assert.AreEqual(int.MinValue, restored.CurrentHealth);
        }

        [Test]
        public void Serialization_BoundaryValues_FP64_Extremes()
        {
            var original = new VelocityComponent
            {
                Velocity = new FPVector3(
                    FP64.FromRaw(long.MaxValue),
                    FP64.FromRaw(long.MinValue),
                    FP64.Zero)
            };

            Span<byte> buffer = stackalloc byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);

            var reader = new SpanReader(buffer);
            var restored = new VelocityComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(long.MaxValue, restored.Velocity.x.RawValue);
            Assert.AreEqual(long.MinValue, restored.Velocity.y.RawValue);
            Assert.AreEqual(0L, restored.Velocity.z.RawValue);
        }

        // --- HFSMComponent fixed array round trip ---

        [Test]
        public unsafe void HFSMComponent_GetSerializedSize_Is64()
        {
            // RootId(4) + ActiveStateIds(8*4=32) + ActiveDepth(4)
            // + PendingEventIds(4*4=16) + PendingEventCount(4) + StateElapsedTicks(4) = 64
            Assert.AreEqual(64, new FSM.HFSMComponent().GetSerializedSize());
        }

        [Test]
        public unsafe void HFSMComponent_SerializeDeserialize_RoundTrip()
        {
            var original = new FSM.HFSMComponent
            {
                RootId = 1,
                ActiveDepth = 3,
                PendingEventCount = 2,
                StateElapsedTicks = 42,
            };
            original.ActiveStateIds[0] = 10;
            original.ActiveStateIds[1] = 20;
            original.ActiveStateIds[2] = 30;
            original.ActiveStateIds[3] = -1;
            original.ActiveStateIds[4] = -1;
            original.ActiveStateIds[5] = -1;
            original.ActiveStateIds[6] = -1;
            original.ActiveStateIds[7] = -1;
            original.PendingEventIds[0] = 5;
            original.PendingEventIds[1] = 7;
            original.PendingEventIds[2] = 0;
            original.PendingEventIds[3] = 0;

            var buffer = new byte[original.GetSerializedSize()];
            var span = new Span<byte>(buffer);
            var writer = new SpanWriter(span);
            original.Serialize(ref writer);

            Assert.AreEqual(64, writer.Position);

            var reader = new SpanReader(span);
            var restored = new FSM.HFSMComponent();
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.RootId, restored.RootId);
            Assert.AreEqual(original.ActiveDepth, restored.ActiveDepth);
            Assert.AreEqual(original.PendingEventCount, restored.PendingEventCount);
            Assert.AreEqual(original.StateElapsedTicks, restored.StateElapsedTicks);

            for (int i = 0; i < 8; i++)
                Assert.AreEqual(original.ActiveStateIds[i], restored.ActiveStateIds[i]);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(original.PendingEventIds[i], restored.PendingEventIds[i]);
        }

        [Test]
        public unsafe void HFSMComponent_GetHash_Deterministic()
        {
            var a = new FSM.HFSMComponent { RootId = 1, ActiveDepth = 2, StateElapsedTicks = 10 };
            a.ActiveStateIds[0] = 100;
            a.ActiveStateIds[1] = 200;

            var b = a;

            ulong seed = FPHash.FNV_OFFSET;
            Assert.AreEqual(a.GetHash(seed), b.GetHash(seed));
        }

        [Test]
        public unsafe void HFSMComponent_GetHash_DiffersOnChange()
        {
            var a = new FSM.HFSMComponent { RootId = 1, ActiveDepth = 1, StateElapsedTicks = 0 };
            a.ActiveStateIds[0] = 100;

            var b = a;
            b.ActiveStateIds[0] = 999;

            ulong seed = FPHash.FNV_OFFSET;
            Assert.AreNotEqual(a.GetHash(seed), b.GetHash(seed));
        }
    }
}
