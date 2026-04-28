using System.Linq;
using NUnit.Framework;
using xpTURN.Klotho.Core;


namespace xpTURN.Klotho.State.Tests
{
    /// <summary>
    /// Unit test: verify RingSnapshotManager ring buffer behavior.
    /// </summary>
    [TestFixture]
    public class RingSnapshotManagerTests
    {
        [SetUp]
        public void SetUp()
        {
            StreamPool.Clear();
        }

        #region SaveAndGet_ExactTick

        [Test]
        public void SaveAndGet_ExactTick()
        {
            var manager = new RingSnapshotManager(10);
            var snapshot = new StateSnapshot(10, new byte[] { 1, 2, 3 });

            manager.SaveSnapshot(10, snapshot);

            Assert.AreSame(snapshot, manager.GetSnapshot(10));
        }

        #endregion

        #region SaveAndGet_MissingTick

        [Test]
        public void SaveAndGet_MissingTick()
        {
            var manager = new RingSnapshotManager(10);
            manager.SaveSnapshot(5, new StateSnapshot(5, new byte[] { 1 }));

            Assert.IsNull(manager.GetSnapshot(999));
        }

        #endregion

        #region RingBuffer_Eviction

        [Test]
        public void RingBuffer_Eviction()
        {
            // capacity = 4+2 = 6
            var manager = new RingSnapshotManager(4);

            // Save 7 snapshots (exceeds capacity of 6)
            for (int i = 0; i < 7; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            // First snapshot (tick 0) should be evicted
            Assert.IsNull(manager.GetSnapshot(0));
            // Latest 6 should exist
            for (int i = 1; i <= 6; i++)
                Assert.IsNotNull(manager.GetSnapshot(i), $"Tick {i} should exist");
        }

        #endregion

        #region RingBuffer_FullCycle

        [Test]
        public void RingBuffer_FullCycle()
        {
            var manager = new RingSnapshotManager(4); // capacity = 6

            // Fill exactly to capacity
            for (int i = 0; i < 6; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            Assert.AreEqual(6, manager.MaxSnapshots);

            // All 6 should be accessible
            for (int i = 0; i < 6; i++)
                Assert.IsNotNull(manager.GetSnapshot(i), $"Tick {i} should exist");

            // Continue saving beyond capacity
            for (int i = 6; i < 12; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            // Only the latest 6 should exist (ticks 6-11)
            for (int i = 0; i < 6; i++)
                Assert.IsNull(manager.GetSnapshot(i), $"Tick {i} should be evicted");
            for (int i = 6; i < 12; i++)
                Assert.IsNotNull(manager.GetSnapshot(i), $"Tick {i} should exist");
        }

        #endregion

        #region ClearSnapshotsAfter

        [Test]
        public void ClearSnapshotsAfter()
        {
            var manager = new RingSnapshotManager(10); // capacity = 12

            for (int i = 1; i <= 10; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            manager.ClearSnapshotsAfter(5);

            // T1~T5 should exist
            for (int i = 1; i <= 5; i++)
                Assert.IsTrue(manager.HasSnapshot(i), $"Tick {i} should exist");

            // T6~T10 should be removed
            for (int i = 6; i <= 10; i++)
                Assert.IsFalse(manager.HasSnapshot(i), $"Tick {i} should be removed");
        }

        #endregion

        #region ClearAll

        [Test]
        public void ClearAll()
        {
            var manager = new RingSnapshotManager(10);

            for (int i = 0; i < 5; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            manager.ClearAll();

            for (int i = 0; i < 5; i++)
                Assert.IsNull(manager.GetSnapshot(i));
        }

        #endregion

        #region Overwrite_SameTick

        [Test]
        public void Overwrite_SameTick()
        {
            var manager = new RingSnapshotManager(10);
            var snapshotA = new StateSnapshot(5, new byte[] { 1, 2 });
            var snapshotB = new StateSnapshot(5, new byte[] { 3, 4 });

            manager.SaveSnapshot(5, snapshotA);
            manager.SaveSnapshot(5, snapshotB);

            Assert.AreSame(snapshotB, manager.GetSnapshot(5));
        }

        #endregion

        #region GetNearestSnapshot

        [Test]
        public void GetNearestSnapshot()
        {
            var manager = new RingSnapshotManager(10);
            manager.SaveSnapshot(1, new StateSnapshot(1, new byte[] { 1 }));
            manager.SaveSnapshot(3, new StateSnapshot(3, new byte[] { 3 }));
            manager.SaveSnapshot(5, new StateSnapshot(5, new byte[] { 5 }));

            var nearest = manager.GetNearestSnapshot(4);
            Assert.IsNotNull(nearest);
            Assert.AreEqual(3, nearest.Tick);
        }

        #endregion

        #region HasSnapshot

        [Test]
        public void HasSnapshot()
        {
            var manager = new RingSnapshotManager(10);
            manager.SaveSnapshot(5, new StateSnapshot(5, new byte[] { 1 }));

            Assert.IsTrue(manager.HasSnapshot(5));
            Assert.IsFalse(manager.HasSnapshot(6));
        }

        #endregion

        #region SavedTicks_Order

        [Test]
        public void SavedTicks_Order()
        {
            var manager = new RingSnapshotManager(10);
            manager.SaveSnapshot(3, new StateSnapshot(3, new byte[] { 3 }));
            manager.SaveSnapshot(1, new StateSnapshot(1, new byte[] { 1 }));
            manager.SaveSnapshot(5, new StateSnapshot(5, new byte[] { 5 }));

            var ticks = manager.SavedTicks.ToList();
            Assert.AreEqual(3, ticks.Count);
            // Order should match insertion order (ring buffer FIFO)
            Assert.AreEqual(3, ticks[0]);
            Assert.AreEqual(1, ticks[1]);
            Assert.AreEqual(5, ticks[2]);
        }

        #endregion

        #region DirectIndex_Lookup

        [Test]
        public void DirectIndex_Lookup()
        {
            var manager = new RingSnapshotManager(10); // capacity = 12

            // Sequential ticks → O(1) direct index calculation
            for (int i = 100; i < 112; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            // All should be accessible by direct index
            for (int i = 100; i < 112; i++)
            {
                var snapshot = manager.GetSnapshot(i);
                Assert.IsNotNull(snapshot, $"Tick {i} should be accessible");
                Assert.AreEqual(i, snapshot.Tick);
            }
        }

        #endregion

        #region BufferReturn_OnEviction

        [Test]
        public void BufferReturn_OnEviction()
        {
            StreamPool.Clear();
            var manager = new RingSnapshotManager(2); // capacity = 4

            // Fill with pooled buffers
            for (int i = 0; i < 4; i++)
            {
                var buffer = StreamPool.GetBuffer(16);
                buffer[0] = (byte)i;
                var snapshot = new StateSnapshot(i);
                snapshot.SetData(buffer, 16);
                manager.SaveSnapshot(i, snapshot);
            }

            // Add tick 4 to evict tick 0
            var newBuffer = StreamPool.GetBuffer(16);
            newBuffer[0] = 4;
            var newSnapshot = new StateSnapshot(4);
            newSnapshot.SetData(newBuffer, 16);
            manager.SaveSnapshot(4, newSnapshot);

            // Tick 0 should not exist
            Assert.IsNull(manager.GetSnapshot(0));
            // Tick 4 should exist
            Assert.IsNotNull(manager.GetSnapshot(4));

            // ClearAll should return all remaining buffers
            manager.ClearAll();
            Assert.IsNull(manager.GetSnapshot(4));
        }

        #endregion

        #region Additional: Rollback scenario

        [Test]
        public void Rollback_ClearAfter_ThenResave()
        {
            var manager = new RingSnapshotManager(10); // capacity = 12

            // Save ticks 0-9
            for (int i = 0; i < 10; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)i }));

            // Rollback to tick 5
            manager.ClearSnapshotsAfter(5);

            // T0~T5 should exist
            for (int i = 0; i <= 5; i++)
                Assert.IsTrue(manager.HasSnapshot(i), $"Tick {i} should exist after rollback");

            // Resave ticks 6-9 with new data
            for (int i = 6; i <= 9; i++)
                manager.SaveSnapshot(i, new StateSnapshot(i, new byte[] { (byte)(i + 100) }));

            // All ticks 0-9 should exist
            for (int i = 0; i <= 9; i++)
                Assert.IsTrue(manager.HasSnapshot(i), $"Tick {i} should exist after resave");
        }

        [Test]
        public void GetNearestSnapshot_EmptyRing_ReturnsNull()
        {
            var manager = new RingSnapshotManager(10);
            Assert.IsNull(manager.GetNearestSnapshot(5));
        }

        [Test]
        public void GetSnapshot_EmptyRing_ReturnsNull()
        {
            var manager = new RingSnapshotManager(10);
            Assert.IsNull(manager.GetSnapshot(0));
        }

        [Test]
        public void MaxSnapshots_SetterIsNoOp()
        {
            var manager = new RingSnapshotManager(10);
            int original = manager.MaxSnapshots;
            manager.MaxSnapshots = 999;
            Assert.AreEqual(original, manager.MaxSnapshots);
        }

        #endregion
    }
}
