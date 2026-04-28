using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Array-based binary min-heap for A* pathfinding.
    /// Pre-allocated, zero GC, deterministic.
    /// Warning: mutable struct + array reference. On value copy, arrays are shared while only _count is copied independently.
    /// Must be held only as a field; do not pass it as a method argument or copy by value.
    /// </summary>
    public struct FPNavMeshBinaryHeap
    {
        private int[] _heap;        // triangle indices
        private FP64[] _fScores;    // f-score
        private int[] _positions;   // triangle index -> heap position (-1 = absent)
        private int _count;

        public int Count => _count;

        /// <summary>
        /// Pre-allocate with capacity = total number of triangles.
        /// </summary>
        public FPNavMeshBinaryHeap(int capacity)
        {
            _heap = new int[capacity];
            _fScores = new FP64[capacity];
            _positions = new int[capacity];
            _count = 0;

            for (int i = 0; i < capacity; i++)
                _positions[i] = -1;
        }

        /// <summary>
        /// Initialize the heap (for reuse). Resets without reallocating arrays.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++)
                _positions[_heap[i]] = -1;
            _count = 0;
        }

        /// <summary>
        /// Insert a triangle index with its f-score into the heap. O(log n).
        /// </summary>
        public void Push(int triIdx, FP64 fScore)
        {
            int idx = _count;
            _heap[idx] = triIdx;
            _fScores[idx] = fScore;
            _positions[triIdx] = idx;
            _count++;

            BubbleUp(idx);
        }

        /// <summary>
        /// Extract the triangle index with the minimum f-score. O(log n).
        /// </summary>
        public int Pop()
        {
            int top = _heap[0];
            _positions[top] = -1;
            _count--;

            if (_count > 0)
            {
                _heap[0] = _heap[_count];
                _fScores[0] = _fScores[_count];
                _positions[_heap[0]] = 0;
                BubbleDown(0);
            }

            return top;
        }

        /// <summary>
        /// Check whether the triangle is in the heap. O(1).
        /// </summary>
        public bool Contains(int triIdx)
        {
            return _positions[triIdx] >= 0;
        }

        /// <summary>
        /// Decrease the f-score of a triangle already in the heap. O(log n).
        /// </summary>
        public void DecreaseKey(int triIdx, FP64 fScore)
        {
            int idx = _positions[triIdx];
            _fScores[idx] = fScore;
            BubbleUp(idx);
        }

        private void BubbleUp(int idx)
        {
            while (idx > 0)
            {
                int parent = (idx - 1) / 2;
                if (_fScores[idx] < _fScores[parent])
                {
                    Swap(idx, parent);
                    idx = parent;
                }
                else
                {
                    break;
                }
            }
        }

        private void BubbleDown(int idx)
        {
            while (true)
            {
                int left = idx * 2 + 1;
                int right = idx * 2 + 2;
                int smallest = idx;

                if (left < _count && _fScores[left] < _fScores[smallest])
                    smallest = left;
                if (right < _count && _fScores[right] < _fScores[smallest])
                    smallest = right;

                if (smallest == idx)
                    break;

                Swap(idx, smallest);
                idx = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            int tmpTri = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = tmpTri;

            FP64 tmpF = _fScores[a];
            _fScores[a] = _fScores[b];
            _fScores[b] = tmpF;

            _positions[_heap[a]] = a;
            _positions[_heap[b]] = b;
        }
    }
}
