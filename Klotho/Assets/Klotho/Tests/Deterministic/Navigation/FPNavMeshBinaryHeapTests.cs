using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class FPNavMeshBinaryHeapTests
    {
        #region Push / Pop

        [Test]
        public void Push_SingleElement_PopReturnsIt()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(3, FP64.FromInt(10));

            Assert.AreEqual(1, heap.Count);
            Assert.AreEqual(3, heap.Pop());
            Assert.AreEqual(0, heap.Count);
        }

        [Test]
        public void Pop_ReturnsMinFScore()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(0, FP64.FromInt(5));
            heap.Push(1, FP64.FromInt(2));
            heap.Push(2, FP64.FromInt(8));
            heap.Push(3, FP64.FromInt(1));

            Assert.AreEqual(3, heap.Pop()); // f=1
            Assert.AreEqual(1, heap.Pop()); // f=2
            Assert.AreEqual(0, heap.Pop()); // f=5
            Assert.AreEqual(2, heap.Pop()); // f=8
        }

        [Test]
        public void Pop_AllElements_CountIsZero()
        {
            var heap = new FPNavMeshBinaryHeap(4);
            heap.Push(0, FP64.FromInt(3));
            heap.Push(1, FP64.FromInt(1));
            heap.Push(2, FP64.FromInt(2));

            heap.Pop();
            heap.Pop();
            heap.Pop();

            Assert.AreEqual(0, heap.Count);
        }

        [Test]
        public void Pop_ManyElements_SortedOrder()
        {
            var heap = new FPNavMeshBinaryHeap(16);

            // Insert in reverse order
            for (int i = 15; i >= 0; i--)
                heap.Push(i, FP64.FromInt(i));

            for (int i = 0; i < 16; i++)
                Assert.AreEqual(i, heap.Pop());
        }

        #endregion

        #region Contains

        [Test]
        public void Contains_AfterPush_ReturnsTrue()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(5, FP64.FromInt(10));

            Assert.IsTrue(heap.Contains(5));
            Assert.IsFalse(heap.Contains(3));
        }

        [Test]
        public void Contains_AfterPop_ReturnsFalse()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(5, FP64.FromInt(10));
            heap.Pop();

            Assert.IsFalse(heap.Contains(5));
        }

        #endregion

        #region DecreaseKey

        [Test]
        public void DecreaseKey_ChangesOrder()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(0, FP64.FromInt(10));
            heap.Push(1, FP64.FromInt(5));
            heap.Push(2, FP64.FromInt(8));

            // Decrease f-score of 0 from 10 -> 1
            heap.DecreaseKey(0, FP64.FromInt(1));

            Assert.AreEqual(0, heap.Pop()); // f=1 (decreased)
            Assert.AreEqual(1, heap.Pop()); // f=5
            Assert.AreEqual(2, heap.Pop()); // f=8
        }

        [Test]
        public void DecreaseKey_ToSameMinimum_StillWorks()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(0, FP64.FromInt(3));
            heap.Push(1, FP64.FromInt(5));

            heap.DecreaseKey(1, FP64.FromInt(3));

            // Both have f=3, either is valid
            int first = heap.Pop();
            int second = heap.Pop();
            Assert.IsTrue((first == 0 && second == 1) || (first == 1 && second == 0));
        }

        #endregion

        #region Clear

        [Test]
        public void Clear_ResetsState()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(0, FP64.FromInt(1));
            heap.Push(1, FP64.FromInt(2));
            heap.Push(2, FP64.FromInt(3));

            heap.Clear();

            Assert.AreEqual(0, heap.Count);
            Assert.IsFalse(heap.Contains(0));
            Assert.IsFalse(heap.Contains(1));
            Assert.IsFalse(heap.Contains(2));
        }

        [Test]
        public void Clear_ThenReuse_Works()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(0, FP64.FromInt(5));
            heap.Push(1, FP64.FromInt(3));
            heap.Pop();
            heap.Pop();

            heap.Clear();

            heap.Push(2, FP64.FromInt(7));
            heap.Push(3, FP64.FromInt(1));

            Assert.AreEqual(3, heap.Pop()); // f=1
            Assert.AreEqual(2, heap.Pop()); // f=7
        }

        #endregion

        #region Edge Cases

        [Test]
        public void Push_CapacityElements_Works()
        {
            int cap = 8;
            var heap = new FPNavMeshBinaryHeap(cap);

            for (int i = 0; i < cap; i++)
                heap.Push(i, FP64.FromInt(cap - i));

            Assert.AreEqual(cap, heap.Count);

            // Pop in ascending f-score order: triIdx = 7(f=1), 6(f=2), ..., 0(f=8)
            for (int i = cap - 1; i >= 0; i--)
                Assert.AreEqual(i, heap.Pop());
        }

        [Test]
        public void DecreaseKey_LeafNode_BubblesUp()
        {
            var heap = new FPNavMeshBinaryHeap(8);
            heap.Push(0, FP64.FromInt(1));
            heap.Push(1, FP64.FromInt(3));
            heap.Push(2, FP64.FromInt(5));
            heap.Push(3, FP64.FromInt(7));

            // Set leaf node 3's f-score to minimum
            heap.DecreaseKey(3, FP64.Zero);

            Assert.AreEqual(3, heap.Pop());
        }

        #endregion
    }
}
