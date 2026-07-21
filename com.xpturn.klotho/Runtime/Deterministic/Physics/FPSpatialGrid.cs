using System;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// (int, int) tuple comparer. Used to sort cell keys in the spatial grid.
    /// </summary>
    internal struct IntPairComparer : IComparer<(int, int)>
    {
        public int Compare((int, int) a, (int, int) b)
        {
            int c = a.Item1.CompareTo(b.Item1);
            return c != 0 ? c : a.Item2.CompareTo(b.Item2);
        }

        // Allocation-free, deterministic in-place ascending sort for the small broadphase pair lists.
        // List<T>.Sort(IComparer<T>) is avoided on purpose: passing this struct boxes it at the call
        // site, and the runtime additionally allocates a Comparison<T> delegate (comparer.Compare)
        // per sort with more than one element. Span<T>.Sort with a struct comparer would avoid both
        // but needs CollectionsMarshal (net5+), which is not on the Unity netstandard2.1 profile.
        // Heap sort keeps this O(n log n) with zero allocation; the total ordering above makes the
        // result deterministic across peers (equal pairs collapse in the callers' dedup pass).
        public static void Sort(List<(int, int)> list)
        {
            int n = list.Count;
            if (n < 2) return;

            for (int i = n / 2 - 1; i >= 0; i--)
                SiftDown(list, i, n);
            for (int end = n - 1; end > 0; end--)
            {
                (list[0], list[end]) = (list[end], list[0]);
                SiftDown(list, 0, end);
            }
        }

        private static void SiftDown(List<(int, int)> list, int root, int count)
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= count) break;
                if (child + 1 < count && Less(list[child], list[child + 1])) child++;
                if (!Less(list[root], list[child])) break;
                (list[root], list[child]) = (list[child], list[root]);
                root = child;
            }
        }

        private static bool Less((int, int) a, (int, int) b)
            => a.Item1 != b.Item1 ? a.Item1 < b.Item1 : a.Item2 < b.Item2;
    }

    /// <summary>
    /// Fixed-point spatial partitioning grid. Used for broadphase collision detection.
    /// </summary>
    public struct FPSpatialGrid
    {
        FP64 cellSize;
        FP64 inverseCellSize;
        Dictionary<(int, int, int), List<int>> cells;
        List<(int, int)> pairs;

        public FPSpatialGrid(FP64 cellSize)
        {
            this.cellSize = cellSize;
            this.inverseCellSize = FP64.One / cellSize;
            this.cells = new Dictionary<(int, int, int), List<int>>();
            this.pairs = new List<(int, int)>();
        }

        public void Clear()
        {
            foreach (var kvp in cells)
            {
                ListPool<int>.Return(kvp.Value);
            }
            cells.Clear();
        }

        public void Insert(int entityId, FPBounds3 bounds)
        {
            int minX = FP64.Floor(bounds.min.x * inverseCellSize).ToInt();
            int minY = FP64.Floor(bounds.min.y * inverseCellSize).ToInt();
            int minZ = FP64.Floor(bounds.min.z * inverseCellSize).ToInt();
            int maxX = FP64.Floor(bounds.max.x * inverseCellSize).ToInt();
            int maxY = FP64.Floor(bounds.max.y * inverseCellSize).ToInt();
            int maxZ = FP64.Floor(bounds.max.z * inverseCellSize).ToInt();

            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var key = (x, y, z);
                        if (!cells.TryGetValue(key, out var list))
                        {
                            list = ListPool<int>.Get();
                            cells[key] = list;
                        }
                        list.Add(entityId);
                    }
        }

        public int GetPairs(List<(int, int)> output)
        {
            output.Clear();
            pairs.Clear();

            foreach (var kvp in cells)
            {
                var list = kvp.Value;
                int count = list.Count;
                for (int i = 0; i < count; i++)
                    for (int j = i + 1; j < count; j++)
                    {
                        int a = list[i];
                        int b = list[j];
                        if (a > b) (a, b) = (b, a);
                        pairs.Add((a, b));
                    }
            }

            IntPairComparer.Sort(pairs);

            for (int i = 0; i < pairs.Count; i++)
            {
                if (i == 0 || pairs[i] != pairs[i - 1])
                {
                    output.Add(pairs[i]);
                }
            }

            return output.Count;
        }
    }
}
