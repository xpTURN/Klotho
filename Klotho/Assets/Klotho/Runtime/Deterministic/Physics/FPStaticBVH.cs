using System;
using System.Collections.Generic;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// BVH (Bounding Volume Hierarchy) tree dedicated to static colliders.
    /// </summary>
    internal struct FPStaticBVH
    {
        internal FPBVHNode[] nodes;
        internal int rootIndex;
        internal int nodeCount;

        const int LeafThreshold = 4;

        // Path A: physics body (isStatic=true) → leafIndex >= 0
        internal static FPStaticBVH Build(FPPhysicsBody[] bodies, ReadOnlySpan<int> bodyIndices)
        {
            int count = bodyIndices.Length;
            if (count == 0)
                return default;

            var entries = new Entry[count];
            for (int i = 0; i < count; i++)
            {
                int idx = bodyIndices[i];
                entries[i] = new Entry(idx, bodies[idx].collider.GetWorldBounds(bodies[idx].meshData));
            }

            var nodes = new FPBVHNode[2 * count - 1];
            int nodeCount = 0;
            int rootIndex = BuildRange(nodes, ref nodeCount, entries, 0, count);

            return new FPStaticBVH { nodes = nodes, rootIndex = rootIndex, nodeCount = nodeCount };
        }

        // Path B: scene-exported static collider → leafIndex = ~colliderIndex (< 0)
        internal static FPStaticBVH Build(FPStaticCollider[] colliders, int count)
        {
            if (count == 0)
                return default;

            var entries = new Entry[count];
            for (int i = 0; i < count; i++)
                entries[i] = new Entry(~i, colliders[i].collider.GetWorldBounds(colliders[i].meshData));

            var nodes = new FPBVHNode[2 * count - 1];
            int nodeCount = 0;
            int rootIndex = BuildRange(nodes, ref nodeCount, entries, 0, count);

            return new FPStaticBVH { nodes = nodes, rootIndex = rootIndex, nodeCount = nodeCount };
        }

        // Path C: A + B mixed (both sources simultaneously)
        // When colliderCount = 0, colliders = null is allowed (no traversal)
        internal static FPStaticBVH Build(
            FPPhysicsBody[] bodies, ReadOnlySpan<int> bodyIndices,
            FPStaticCollider[] colliders, int colliderCount)
        {
            int bodyCount = bodyIndices.Length;
            int total = bodyCount + colliderCount;
            if (total == 0)
                return default;

            var entries = new Entry[total];
            for (int i = 0; i < bodyCount; i++)
            {
                int idx = bodyIndices[i];
                entries[i] = new Entry(idx, bodies[idx].collider.GetWorldBounds(bodies[idx].meshData));
            }
            for (int i = 0; i < colliderCount; i++)
                entries[bodyCount + i] = new Entry(~i, colliders[i].collider.GetWorldBounds(colliders[i].meshData));

            var nodes = new FPBVHNode[2 * total - 1];
            int nodeCount = 0;
            int rootIndex = BuildRange(nodes, ref nodeCount, entries, 0, total);

            return new FPStaticBVH { nodes = nodes, rootIndex = rootIndex, nodeCount = nodeCount };
        }

        // Returns the leafIndex of the closest leaf and the AABB hit distance t
        // t is AABB-based — caller is responsible for the narrowphase precision test
        internal bool RayCast(FPRay3 ray, out int hitLeafIndex, out FP64 t)
        {
            hitLeafIndex = 0;
            t = FP64.MaxValue;
            if (nodes == null || nodeCount == 0)
                return false;
            TraverseRay(rootIndex, ray, ref hitLeafIndex, ref t);
            return t < FP64.MaxValue;
        }

        void TraverseRay(int nodeIdx, FPRay3 ray, ref int hitLeafIndex, ref FP64 bestT)
        {
            ref FPBVHNode node = ref nodes[nodeIdx];
            if (!node.bounds.IntersectRay(ray, out FP64 tHit, out FP64 tHitMin))
                return;
            if (tHitMin >= FP64.Zero && tHit >= bestT)
                return;

            if (node.left == -1)
            {
                hitLeafIndex = node.leafIndex;
                bestT = tHit;
                return;
            }

            TraverseRay(node.left, ray, ref hitLeafIndex, ref bestT);
            TraverseRay(node.right, ray, ref hitLeafIndex, ref bestT);
        }

        // Collects the leafIndex of all leaves overlapping the sphere into output (fixed left → right order)
        internal void OverlapSphere(FPVector3 center, FP64 radius, List<int> output)
        {
            if (nodes == null || nodeCount == 0)
                return;
            FP64 sqrRadius = radius * radius;
            TraverseSphere(rootIndex, center, sqrRadius, output);
        }

        void TraverseSphere(int nodeIdx, FPVector3 center, FP64 sqrRadius, List<int> output)
        {
            ref FPBVHNode node = ref nodes[nodeIdx];
            if (node.bounds.SqrDistance(center) > sqrRadius)
                return;

            if (node.left == -1)
            {
                output.Add(node.leafIndex);
                return;
            }

            TraverseSphere(node.left, center, sqrRadius, output);
            TraverseSphere(node.right, center, sqrRadius, output);
        }

        // Collects the leafIndex of all leaves overlapping the query into output (fixed left → right order)
        internal void OverlapAABB(FPBounds3 query, List<int> output)
        {
            if (nodes == null || nodeCount == 0)
                return;
            TraverseAABB(rootIndex, query, output);
        }

        void TraverseAABB(int nodeIdx, FPBounds3 query, List<int> output)
        {
            ref FPBVHNode node = ref nodes[nodeIdx];
            if (!node.bounds.Intersects(query))
                return;

            if (node.left == -1)
            {
                output.Add(node.leafIndex);
                return;
            }

            TraverseAABB(node.left, query, output);
            TraverseAABB(node.right, query, output);
        }

        static int BuildRange(FPBVHNode[] nodes, ref int nodeCount, Entry[] entries, int start, int end)
        {
            int count = end - start;
            int nodeIdx = nodeCount++;

            FPBounds3 bounds = entries[start].bounds;
            for (int i = start + 1; i < end; i++)
                bounds.Encapsulate(entries[i].bounds);

            nodes[nodeIdx].bounds = bounds;

            if (count == 1)
            {
                nodes[nodeIdx].left = -1;
                nodes[nodeIdx].right = -1;
                nodes[nodeIdx].leafIndex = entries[start].leafIndex;
                return nodeIdx;
            }

            int axis = LongestAxis(bounds);
            Array.Sort(entries, start, count, new EntryComparer { axis = axis });

            int split = count <= LeafThreshold
                ? start + count / 2
                : FindSAHSplit(entries, start, end);

            int left = BuildRange(nodes, ref nodeCount, entries, start, split);
            int right = BuildRange(nodes, ref nodeCount, entries, split, end);

            nodes[nodeIdx].left = left;
            nodes[nodeIdx].right = right;
            return nodeIdx;
        }

        static int FindSAHSplit(Entry[] entries, int start, int end)
        {
            int count = end - start;

            var prefix = new FPBounds3[count];
            prefix[0] = entries[start].bounds;
            for (int i = 1; i < count; i++)
            {
                prefix[i] = prefix[i - 1];
                prefix[i].Encapsulate(entries[start + i].bounds);
            }

            var suffix = new FPBounds3[count];
            suffix[count - 1] = entries[end - 1].bounds;
            for (int i = count - 2; i >= 0; i--)
            {
                suffix[i] = suffix[i + 1];
                suffix[i].Encapsulate(entries[start + i].bounds);
            }

            // i=1: 1 on the left, (count-1) on the right
            FP64 bestCost = SurfaceArea(prefix[0]) + SurfaceArea(suffix[1]) * FP64.FromInt(count - 1);
            int bestSplit = start + 1;

            for (int i = 2; i < count; i++)
            {
                FP64 cost = SurfaceArea(prefix[i - 1]) * FP64.FromInt(i)
                           + SurfaceArea(suffix[i]) * FP64.FromInt(count - i);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSplit = start + i;
                }
            }

            return bestSplit;
        }

        static int LongestAxis(FPBounds3 bounds)
        {
            FPVector3 e = bounds.extents;
            if (e.x >= e.y && e.x >= e.z) return 0;
            if (e.y >= e.z) return 1;
            return 2;
        }

        static FP64 SurfaceArea(FPBounds3 b)
        {
            FPVector3 s = b.size;
            return s.x * s.y + s.y * s.z + s.z * s.x;
        }

        struct Entry
        {
            internal int leafIndex;
            internal FPBounds3 bounds;

            internal Entry(int leafIndex, FPBounds3 bounds)
            {
                this.leafIndex = leafIndex;
                this.bounds = bounds;
            }
        }

        class EntryComparer : IComparer<Entry>
        {
            internal int axis;

            public int Compare(Entry a, Entry b)
            {
                FP64 ca = axis == 0 ? a.bounds.center.x : axis == 1 ? a.bounds.center.y : a.bounds.center.z;
                FP64 cb = axis == 0 ? b.bounds.center.x : axis == 1 ? b.bounds.center.y : b.bounds.center.z;
                if (ca < cb) return -1;
                if (ca > cb) return 1;
                return a.leafIndex.CompareTo(b.leafIndex);
            }
        }
    }
}
