using System;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class FrameSnapshotTests
    {
        private const int MaxEntities = 16;

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

        // --- Frame.CopyFrom snapshot accuracy ---

        [Test]
        public void CopyFrom_RestoresEntityAndComponents()
        {
            var source = new Frame(MaxEntities, _logger);
            source.Tick = 10;
            source.DeltaTimeMs = 16;

            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            source.Add(e0, new OwnerComponent { OwnerId = 1 });
            source.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });

            var target = new Frame(MaxEntities, _logger);
            target.CopyFrom(source);

            Assert.AreEqual(10, target.Tick);
            Assert.AreEqual(16, target.DeltaTimeMs);
            Assert.AreEqual(2, target.Entities.Count);
            Assert.IsTrue(target.Has<HealthComponent>(e0));
            Assert.IsTrue(target.Has<OwnerComponent>(e0));
            Assert.IsTrue(target.Has<HealthComponent>(e1));
            Assert.AreEqual(80, target.Get<HealthComponent>(e0).CurrentHealth);
            Assert.AreEqual(1, target.Get<OwnerComponent>(e0).OwnerId);
            Assert.AreEqual(200, target.Get<HealthComponent>(e1).CurrentHealth);
        }

        [Test]
        public void CopyFrom_IsDeepCopy_SourceMutationDoesNotAffectTarget()
        {
            var source = new Frame(MaxEntities, _logger);
            var entity = source.CreateEntity();
            source.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var target = new Frame(MaxEntities, _logger);
            target.CopyFrom(source);

            // mutate source
            ref var sourceHealth = ref source.Get<HealthComponent>(entity);
            sourceHealth.CurrentHealth = 0;

            // target is unaffected
            Assert.AreEqual(100, target.Get<HealthComponent>(entity).CurrentHealth);
        }

        [Test]
        public void Snapshot_Mutate_Restore_MatchesOriginal()
        {
            var frame = new Frame(MaxEntities, _logger);
            var e0 = frame.CreateEntity();
            frame.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            frame.Add(e0, new OwnerComponent { OwnerId = 1 });
            frame.Tick = 5;

            // snapshot
            var snapshot = new Frame(MaxEntities, _logger);
            snapshot.CopyFrom(frame);

            // mutate
            ref var health = ref frame.Get<HealthComponent>(e0);
            health.CurrentHealth = 0;
            frame.Add(frame.CreateEntity(), new HealthComponent { MaxHealth = 50, CurrentHealth = 50 });
            frame.Tick = 6;

            // restore
            frame.CopyFrom(snapshot);

            Assert.AreEqual(5, frame.Tick);
            Assert.AreEqual(1, frame.Entities.Count);
            Assert.AreEqual(100, frame.Get<HealthComponent>(e0).CurrentHealth);
            Assert.AreEqual(1, frame.Get<OwnerComponent>(e0).OwnerId);
        }

        [Test]
        public void CopyFrom_HandlesEmptySource()
        {
            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            var empty = new Frame(MaxEntities, _logger);
            frame.CopyFrom(empty);

            Assert.AreEqual(0, frame.Tick);
            Assert.AreEqual(0, frame.Entities.Count);
        }

        [Test]
        public void CopyFrom_AfterEntityDestroy_RestoresCorrectly()
        {
            var source = new Frame(MaxEntities, _logger);
            var e0 = source.CreateEntity();
            var e1 = source.CreateEntity();
            source.Add(e0, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            source.Add(e1, new HealthComponent { MaxHealth = 200, CurrentHealth = 200 });
            source.DestroyEntity(e0);

            var target = new Frame(MaxEntities, _logger);
            target.CopyFrom(source);

            Assert.AreEqual(1, target.Entities.Count);
            Assert.IsFalse(target.Entities.IsAlive(e0));
            Assert.IsTrue(target.Entities.IsAlive(e1));
            Assert.AreEqual(200, target.Get<HealthComponent>(e1).CurrentHealth);
        }

        // --- FrameRingBuffer ---

        [Test]
        public void RingBuffer_SaveAndRestore()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);

            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            frame.Tick = 0;
            ring.SaveFrame(0, frame);

            frame.Get<HealthComponent>(entity).CurrentHealth = 80;
            frame.Tick = 1;
            ring.SaveFrame(1, frame);

            frame.Get<HealthComponent>(entity).CurrentHealth = 60;
            frame.Tick = 2;
            ring.SaveFrame(2, frame);

            // restore tick 0
            ring.RestoreFrame(0, frame);
            Assert.AreEqual(0, frame.Tick);
            Assert.AreEqual(100, frame.Get<HealthComponent>(entity).CurrentHealth);

            // restore tick 1
            ring.RestoreFrame(1, frame);
            Assert.AreEqual(1, frame.Tick);
            Assert.AreEqual(80, frame.Get<HealthComponent>(entity).CurrentHealth);

            // restore tick 2
            ring.RestoreFrame(2, frame);
            Assert.AreEqual(2, frame.Tick);
            Assert.AreEqual(60, frame.Get<HealthComponent>(entity).CurrentHealth);
        }

        [Test]
        public void RingBuffer_OverwritesOldFrames()
        {
            var ring = new FrameRingBuffer(2, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);

            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            // save ticks 0, 1, 2 — capacity is 2 so tick 0 gets overwritten by tick 2
            frame.Tick = 0;
            ring.SaveFrame(0, frame);

            frame.Tick = 1;
            frame.Get<HealthComponent>(entity).CurrentHealth = 90;
            ring.SaveFrame(1, frame);

            frame.Tick = 2;
            frame.Get<HealthComponent>(entity).CurrentHealth = 80;
            ring.SaveFrame(2, frame);

            // slot 0 now holds tick 2 (2 % 2 == 0)
            ring.RestoreFrame(2, frame);
            Assert.AreEqual(2, frame.Tick);
            Assert.AreEqual(80, frame.Get<HealthComponent>(entity).CurrentHealth);

            // slot 1 still holds tick 1
            ring.RestoreFrame(1, frame);
            Assert.AreEqual(1, frame.Tick);
            Assert.AreEqual(90, frame.Get<HealthComponent>(entity).CurrentHealth);
        }

        [Test]
        public void RingBuffer_HasFrame_ReturnsCorrectly()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);

            // always false before SaveFrame is called
            Assert.IsFalse(ring.HasFrame(0, 0));

            // save ticks 0~3
            for (int t = 0; t <= 3; t++)
            {
                frame.Tick = t;
                ring.SaveFrame(t, frame);
            }

            Assert.IsTrue(ring.HasFrame(0, 3));
            Assert.IsTrue(ring.HasFrame(3, 3));
            Assert.IsTrue(ring.HasFrame(0, 4)); // latestSaved=3, (3-0)=3 < 4 → tick 0 still valid
            Assert.IsFalse(ring.HasFrame(5, 3)); // future
            Assert.IsFalse(ring.HasFrame(-1, 3)); // negative

            // save tick 4 → overwrite slot 0 (4 % 4 == 0) → tick 0 evicted
            frame.Tick = 4;
            ring.SaveFrame(4, frame);
            Assert.IsFalse(ring.HasFrame(0, 4)); // latestSaved=4, (4-0)=4 >= 4 → too old
        }

        [Test]
        public void RingBuffer_HasFrame_AfterClear_ReturnsFalse()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);

            frame.Tick = 0;
            ring.SaveFrame(0, frame);
            Assert.IsTrue(ring.HasFrame(0, 0));

            ring.Clear();
            Assert.IsFalse(ring.HasFrame(0, 0));
        }

        // --- GetNearestAvailableTick ---

        [Test]
        public void GetNearestAvailableTick_ExactMatch()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);
            for (int t = 0; t <= 3; t++)
            {
                frame.Tick = t;
                ring.SaveFrame(t, frame);
            }

            Assert.AreEqual(2, ring.GetNearestAvailableTick(2, 3));
        }

        [Test]
        public void GetNearestAvailableTick_FallsBackToOlder()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);
            for (int t = 0; t <= 5; t++)
            {
                frame.Tick = t;
                ring.SaveFrame(t, frame);
            }
            // capacity=4, latestSaved=5 → valid: 2,3,4,5

            // targetTick=1 is out of range, oldest valid tick=2
            Assert.AreEqual(-1, ring.GetNearestAvailableTick(1, 5));
            Assert.AreEqual(2, ring.GetNearestAvailableTick(2, 5));
            Assert.AreEqual(5, ring.GetNearestAvailableTick(5, 5));
        }

        [Test]
        public void GetNearestAvailableTick_EmptyBuffer_ReturnsNegative()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);

            Assert.AreEqual(-1, ring.GetNearestAvailableTick(3, 3));
        }

        [Test]
        public void GetNearestAvailableTick_BoundaryTick()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);
            for (int t = 0; t <= 3; t++)
            {
                frame.Tick = t;
                ring.SaveFrame(t, frame);
            }

            // oldest valid tick = 0, boundary exact match
            Assert.AreEqual(0, ring.GetNearestAvailableTick(0, 3));
        }

        // --- GetSavedTicks ---

        [Test]
        public void GetSavedTicks_EmptyBuffer()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var output = new System.Collections.Generic.List<int>();

            ring.GetSavedTicks(3, output);

            Assert.AreEqual(0, output.Count);
        }

        [Test]
        public void GetSavedTicks_FullBuffer()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);
            for (int t = 0; t <= 5; t++)
            {
                frame.Tick = t;
                ring.SaveFrame(t, frame);
            }
            // capacity=4, latestSaved=5 → valid: 2,3,4,5
            var output = new System.Collections.Generic.List<int>();
            ring.GetSavedTicks(5, output);

            Assert.AreEqual(4, output.Count);
            Assert.AreEqual(2, output[0]);
            Assert.AreEqual(5, output[3]);
        }

        [Test]
        public void GetSavedTicks_ExcludesOutOfRange()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);
            // Save only ticks 3,4,5
            for (int t = 3; t <= 5; t++)
            {
                frame.Tick = t;
                ring.SaveFrame(t, frame);
            }
            // currentTick=5, capacity=4 → range [2..5], but latestSaved=5
            // tick 2 → latestSaved(5)-2=3 < 4 → true, but tick 2 > latestSaved? no (2<=5)
            // tick 2 was never actually saved but passes the HasFrame range check.
            // However with _latestSavedTick=5: (5-2)=3 < 4 → true.
            // This is the known "contiguous SaveFrame precondition" — tick 2 holds stale data.
            // In production, ticks are always contiguous so this situation does not occur.
            var output = new System.Collections.Generic.List<int>();
            ring.GetSavedTicks(5, output);

            // includes only actually saved ticks 3,4,5 (based on dirty flag)
            Assert.AreEqual(3, output.Count);
        }

        [Test]
        public void RingBuffer_RollbackScenario()
        {
            var ring = new FrameRingBuffer(8, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);

            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });

            // simulate 5 ticks
            for (int tick = 0; tick < 5; tick++)
            {
                frame.Tick = tick;
                ring.SaveFrame(tick, frame);
                frame.Get<HealthComponent>(entity).CurrentHealth -= 10;
            }

            // current frame is tick 5, hp=50
            Assert.AreEqual(50, frame.Get<HealthComponent>(entity).CurrentHealth);

            // rollback to tick 2 (hp should be 80)
            ring.RestoreFrame(2, frame);
            Assert.AreEqual(2, frame.Tick);
            Assert.AreEqual(80, frame.Get<HealthComponent>(entity).CurrentHealth);

            // re-simulate from tick 2
            for (int tick = 2; tick < 5; tick++)
            {
                frame.Tick = tick;
                ring.SaveFrame(tick, frame);
                frame.Get<HealthComponent>(entity).CurrentHealth -= 5; // different delta this time
            }

            // after re-simulation: hp = 80 - 5*3 = 65
            Assert.AreEqual(65, frame.Get<HealthComponent>(entity).CurrentHealth);
        }

        // --- RingBuffer constructor validation ---

        [Test]
        public void RingBuffer_InvalidCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRingBuffer(0, MaxEntities, _logger));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRingBuffer(-1, MaxEntities, _logger));
        }

        // --- RingBuffer with multiple component types ---

        [Test]
        public void RingBuffer_MultipleComponentTypes_SaveAndRestore()
        {
            var ring = new FrameRingBuffer(4, MaxEntities, _logger);
            var frame = new Frame(MaxEntities, _logger);

            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
            frame.Add(entity, new OwnerComponent { OwnerId = 1 });
            frame.Add(entity, new TransformComponent
            {
                Position = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.FromInt(20)),
                Rotation = FP64.Zero,
                Scale = FPVector3.One
            });
            frame.Tick = 0;
            ring.SaveFrame(0, frame);

            // mutate all components
            frame.Get<HealthComponent>(entity).CurrentHealth = 50;
            frame.Get<OwnerComponent>(entity).OwnerId = 2;
            ref var t = ref frame.Get<TransformComponent>(entity);
            t.Position = new FPVector3(FP64.FromInt(99), FP64.Zero, FP64.FromInt(99));
            frame.Tick = 1;
            ring.SaveFrame(1, frame);

            // restore tick 0
            ring.RestoreFrame(0, frame);
            Assert.AreEqual(0, frame.Tick);
            Assert.AreEqual(100, frame.Get<HealthComponent>(entity).CurrentHealth);
            Assert.AreEqual(1, frame.Get<OwnerComponent>(entity).OwnerId);
            Assert.AreEqual(FP64.FromInt(10).RawValue,
                frame.Get<TransformComponent>(entity).Position.x.RawValue);
        }

        // --- snapshot hash preservation ---

        [Test]
        public void Snapshot_RestorePreservesHash()
        {
            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            frame.Add(entity, new HealthComponent { MaxHealth = 100, CurrentHealth = 80 });
            frame.Add(entity, new OwnerComponent { OwnerId = 1 });
            frame.Tick = 5;

            var snapshot = new Frame(MaxEntities, _logger);
            snapshot.CopyFrom(frame);
            ulong hashBefore = frame.CalculateHash();

            // mutate
            frame.Get<HealthComponent>(entity).CurrentHealth = 0;
            frame.Tick = 99;

            // restore
            frame.CopyFrom(snapshot);
            ulong hashAfter = frame.CalculateHash();

            Assert.AreEqual(hashBefore, hashAfter);
        }
    }
}
