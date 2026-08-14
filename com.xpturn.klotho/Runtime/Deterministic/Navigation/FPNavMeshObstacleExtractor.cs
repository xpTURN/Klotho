using System;
using System.Collections.Generic;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Extracts ORCA static-obstacle rings from a NavMesh boundary (edges with no walkable
    /// neighbor, <c>neighbor == -1</c>). Load-time / build-time only — never on the simulation
    /// hot path — so <see cref="List{T}"/> allocation is fine.
    ///
    /// Winding convention (matches FPNavAvoidance): free (walkable) space is on the RIGHT of each
    /// edge's unitDir. Boundary edges are oriented so the triangle interior (walkable) is on the
    /// right, which makes outer boundaries come out clockwise and holes counter-clockwise
    /// automatically — no per-loop classification needed and no assumption about triangle winding
    /// (the orientation is derived per-edge from the opposite vertex, like ComputePortalLeftRight).
    ///
    /// Rings are chained by rotating around the shared vertex through the walkable triangle fan
    /// (not by coordinate endpoint matching), so pinch/bowtie vertices — where an edge-manifold
    /// mesh still has more than two boundary edges meeting at one vertex — split into independent
    /// rings correctly. All chaining is in vertex/triangle-index space (no FP tolerance).
    /// </summary>
    public static class FPNavMeshObstacleExtractor
    {
        /// <summary>
        /// Builds obstacle rings from the NavMesh boundary. Output is the flat representation
        /// consumed by <see cref="FPNavAvoidance.LoadObstacles"/>: <paramref name="vertices"/> is
        /// all ring vertices concatenated, <paramref name="polygonOffsets"/> is the start index of
        /// each ring. Deterministic: rings are seeded in ascending (triangle, edge) order and the
        /// fan walk is topologically fixed.
        /// </summary>
        public static void Extract(FPNavMesh navMesh, out FPVector2[] vertices, out int[] polygonOffsets)
            => Extract(navMesh, out vertices, out polygonOffsets, out _, out _);

        /// <summary>
        /// Same as <see cref="Extract(FPNavMesh, out FPVector2[], out int[])"/> plus a forward
        /// triangle-to-segment CSR for graph-local obstacle queries. A segment's index equals its
        /// start-vertex index in <paramref name="vertices"/> (which <c>LoadObstacles</c> copies
        /// unshuffled into <c>_obstacles</c>, so the index also indexes <c>_obstacles</c>). Each
        /// boundary segment belongs to exactly one triangle (its <c>neighbor == -1</c> edge), so
        /// the CSR is a partition: segments owned by triangle <c>t</c> are
        /// <c>triSegList[triSegStart[t] .. triSegStart[t+1])</c>. Load-time only.
        /// </summary>
        public static void Extract(FPNavMesh navMesh, out FPVector2[] vertices, out int[] polygonOffsets,
            out int[] triSegStart, out int[] triSegList)
            => Extract(navMesh, null, out vertices, out polygonOffsets, out triSegStart, out triSegList);

        /// <summary>
        /// Reusable working set for a caller that extracts repeatedly from the same room — today
        /// <see cref="FPNavAgentSystem"/>, which re-extracts on every navmesh swap. Holds the
        /// arrays that never leave as length-bearing outputs, so re-extraction stops allocating
        /// them: the visited flags, the segment-to-triangle map, the counting-sort cursor, and the
        /// CSR pair the agent system keeps for the life of the mesh anyway.
        ///
        /// <para>Caller-owned rather than a process-wide pool, and that is not a style choice.
        /// <see cref="FPNavMeshRebaker.CreateSnapshot"/> also extracts, and a snapshot is
        /// documented as shareable read-only across rooms and worker threads — two rooms building
        /// one concurrently would trample a shared pool. Ownership by the thing that re-extracts
        /// is what makes "used serially" true by construction instead of by convention.</para>
        ///
        /// <para><b>The CSR pair comes back oversized.</b> Consumers must read it as a CSR —
        /// <c>triSegList[triSegStart[t] .. triSegStart[t+1])</c> — and never take its
        /// <c>Length</c>. <c>vertices</c> and <c>polygonOffsets</c> are still exact-size, because
        /// <see cref="FPNavAvoidance.LoadObstacles"/> reads their length as the count.</para>
        /// </summary>
        internal sealed class ExtractScratch
        {
            internal bool[] Visited;
            internal int[] SegTri;
            internal int[] FillCursor;
            internal int[] TriSegStart;
            internal int[] TriSegList;
            // Ring starts. A List because the ring count is only known by walking, and Clear()
            // keeps the capacity — so after the first extract this stops allocating entirely.
            internal List<int> Offsets;
            // Ring vertices. The single biggest allocation this method used to make (245 KB on
            // the shipped Field asset), kept because LoadObstacles now takes an explicit count.
            internal FPVector2[] Verts;
            internal int[] OffsetsArray;
        }

        /// <inheritdoc cref="Extract(FPNavMesh, out FPVector2[], out int[], out int[], out int[])"/>
        /// <param name="scratch">
        /// Reusable working set, or null to allocate everything fresh. Non-null hands the CSR pair
        /// back oversized — see <see cref="ExtractScratch"/>.
        /// </param>
        internal static void Extract(FPNavMesh navMesh, ExtractScratch scratch,
            out FPVector2[] vertices, out int[] polygonOffsets,
            out int[] triSegStart, out int[] triSegList)
        {
            Extract(navMesh, scratch, out vertices, out int vertexCount,
                out polygonOffsets, out int polygonCount, out triSegStart, out triSegList);

            // The array-only forms promise exact-size outputs, so a scratch-backed call has to
            // trim. Nothing does that today — the scratch overload is only reached through the
            // counted signature — but leaving the promise unenforced would make the two shapes
            // silently different for whoever adds the next caller.
            if (vertices.Length != vertexCount)
                vertices = TrimTo(vertices, vertexCount);
            if (polygonOffsets.Length != polygonCount)
            {
                var exact = new int[polygonCount];
                Array.Copy(polygonOffsets, exact, polygonCount);
                polygonOffsets = exact;
            }
        }

        /// <summary>
        /// <inheritdoc cref="Extract(FPNavMesh, ExtractScratch, out FPVector2[], out int[], out int[], out int[])" path="/summary/node()"/>
        ///
        /// <para><b>With a scratch, <paramref name="vertices"/> and <paramref name="polygonOffsets"/>
        /// come back oversized too</b> — read them through <paramref name="vertexCount"/> and
        /// <paramref name="polygonCount"/>, never through <c>Length</c>. That is what removes the
        /// last per-swap allocation; the exact-size promise lives on the array-only overloads.</para>
        /// </summary>
        internal static void Extract(FPNavMesh navMesh, ExtractScratch scratch,
            out FPVector2[] vertices, out int vertexCount,
            out int[] polygonOffsets, out int polygonCount,
            out int[] triSegStart, out int[] triSegList)
        {
            // Held by the scratch for the same reason verts and segTri no longer double-buffer:
            // 1,679 rings on the shipped Field asset means the doubling walk throws away 16 KB of
            // intermediate capacity, which is more than the finished array costs. Clear() keeps
            // that capacity, so the growth happens once for the life of the room.
            List<int> offsets;
            if (scratch == null)
            {
                offsets = new List<int>();
            }
            else
            {
                offsets = scratch.Offsets ?? (scratch.Offsets = new List<int>());
                offsets.Clear();
            }

            if (navMesh == null || navMesh.Triangles == null || navMesh.Triangles.Length == 0
                || navMesh.Vertices == null)
            {
                vertices = Array.Empty<FPVector2>();
                polygonOffsets = Array.Empty<int>();
                triSegStart = Array.Empty<int>();
                triSegList = Array.Empty<int>();
                vertexCount = 0;
                polygonCount = 0;
                return;
            }

            var tris = navMesh.Triangles;
            int triCount = tris.Length;

            // Count the boundary edges first, then fill exact-size arrays directly. verts and
            // segTri used to be List<T>, which cost twice over on a large mesh: the doubling walk
            // allocates every intermediate capacity (16 B x 32,764 for 15,317 ring vertices on the
            // shipped Field asset), and ToArray then allocates the result a second time. Measured,
            // those two were half of everything this method spends.
            //
            // The count is exact whenever no seed is dropped below, and it is an upper bound
            // otherwise — never short. The walk marks `visited` and appends to both arrays in the
            // SAME iteration, so one visited boundary edge is one entry; the only path that marks
            // without appending is the degenerate-seed drop, whose own comment records that a
            // Build-pipeline mesh cannot reach it.
            int boundaryEdges = 0;
            for (int t = 0; t < triCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    if (tris[t].GetNeighbor(e) == -1)
                        boundaryEdges++;
                }
            }

            FPVector2[] verts = scratch == null
                ? new FPVector2[boundaryEdges]
                : RentVerts(ref scratch.Verts, boundaryEdges);
            // segment index (== verts index) -> source triangle. Written over [0, vertCount)
            // before anything reads it, so a reused array needs no clearing.
            int[] segTri = scratch == null
                ? new int[boundaryEdges]
                : Rent(ref scratch.SegTri, boundaryEdges);
            int vertCount = 0;
            // Boundary-edge visited flags (triIdx*3 + localEdge). Unlike segTri this is READ
            // before it is written, so a reused array has to start false again.
            bool[] visited = scratch == null
                ? new bool[triCount * 3]
                : RentCleared(ref scratch.Visited, triCount * 3);

            for (int t = 0; t < triCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    if (visited[t * 3 + e])
                        continue;
                    if (tris[t].GetNeighbor(e) != -1)
                        continue; // interior edge, not a wall

                    // --- New ring: orient the seed so the walkable interior (opp) is on the right ---
                    tris[t].GetEdgeVertices(e, out int va, out int vb);
                    int opp = ThirdVertexOfEdge(tris[t], e);
                    FPVector2 a = navMesh.Vertices[va].ToXZ();
                    FPVector2 b = navMesh.Vertices[vb].ToXZ();
                    FPVector2 o = navMesh.Vertices[opp].ToXZ();

                    // Seed orientation is the sign of the seed triangle's XZ area (== 2*TriangleArea2D).
                    // When it is exactly 0 the triangle is XZ-degenerate (collinear projection, e.g. a
                    // vertical wall triangle): "walkable on the right" is undefined and reversing
                    // arbitrarily could flip the whole ring's winding. A Build-pipeline mesh never
                    // reaches here (RemoveDegenerateTriangles drops |2D area| < 0.0001, so
                    // |seedCross| >= 0.0002), but a corrupt / hand-built mesh could — drop the
                    // ambiguous edge instead of emitting a reverse-wound ring.
                    FP64 seedCross = FPVector2.Cross(b - a, o - a);
                    if (seedCross == FP64.Zero)
                    {
                        visited[t * 3 + e] = true;
                        continue;
                    }

                    offsets.Add(vertCount);

                    int startV, endV;
                    if (seedCross < FP64.Zero)
                    {
                        // opp is on the right of a->b → keep, walkable already on the right.
                        startV = va;
                        endV = vb;
                    }
                    else
                    {
                        // opp on the left → reverse so walkable ends up on the right.
                        startV = vb;
                        endV = va;
                    }

                    int curTri = t;
                    int curEdge = e;
                    int cs = startV;
                    int ce = endV;
                    int seedStart = startV;

                    int guard = 0;
                    int guardMax = triCount * 3 + 8;
                    while (true)
                    {
                        visited[curTri * 3 + curEdge] = true;
                        verts[vertCount] = navMesh.Vertices[cs].ToXZ();
                        segTri[vertCount] = curTri; // this ring vertex starts boundary edge (curTri, curEdge)
                        vertCount++;

                        if (ce == seedStart)
                            break; // ring closed

                        // Rotate around V = ce through the walkable fan to the next boundary edge.
                        if (!FindNextBoundaryEdge(tris, curTri, ce, cs,
                                out int nTri, out int nEdge, out int nOther))
                        {
                            throw new InvalidOperationException(
                                "FPNavMeshObstacleExtractor: open boundary chain (non-manifold NavMesh)");
                        }

                        curTri = nTri;
                        curEdge = nEdge;
                        cs = ce;
                        ce = nOther;

                        if (++guard > guardMax)
                        {
                            throw new InvalidOperationException(
                                "FPNavMeshObstacleExtractor: boundary chain did not close (degenerate topology)");
                        }
                    }
                }
            }

            // Handed out as-is. The array may be longer than vertCount — either because a
            // degenerate seed was dropped, or because it is a scratch buffer sized by an earlier,
            // larger mesh. Both cases are the same to a caller reading through the count, which is
            // why this no longer trims: the array-only overloads do that on the way out.
            vertices = verts;
            vertexCount = vertCount;

            polygonCount = offsets.Count;
            if (scratch == null)
            {
                polygonOffsets = offsets.ToArray();
            }
            else
            {
                polygonOffsets = Rent(ref scratch.OffsetsArray, polygonCount);
                for (int i = 0; i < polygonCount; i++)
                    polygonOffsets[i] = offsets[i];
            }

            // Build forward triangle->segment CSR by counting sort on the source triangle.
            // triSegStart is accumulated into, so a reused one has to start at zero; triSegList
            // and fillCursor are fully written before they are read.
            int segCount = vertCount;
            triSegStart = scratch == null
                ? new int[triCount + 1]
                : RentCleared(ref scratch.TriSegStart, triCount + 1);
            for (int s = 0; s < segCount; s++)
                triSegStart[segTri[s] + 1]++;
            for (int ti = 0; ti < triCount; ti++)
                triSegStart[ti + 1] += triSegStart[ti];
            triSegList = scratch == null
                ? new int[segCount]
                : Rent(ref scratch.TriSegList, segCount);
            int[] fillCursor = scratch == null
                ? new int[triCount]
                : Rent(ref scratch.FillCursor, triCount);
            for (int ti = 0; ti < triCount; ti++)
                fillCursor[ti] = triSegStart[ti];
            for (int s = 0; s < segCount; s++)
            {
                int owner = segTri[s];
                triSegList[fillCursor[owner]++] = s;
            }
        }

        // Grow-if-too-small, keep otherwise — the same rule FPNavAgentSystem's _bfsStamp uses, and
        // for the same reason: a rebake that removes a building shrinks the triangle count, and
        // reallocating on every shrink would give back what reuse is for.
        private static int[] Rent(ref int[] slot, int minSize)
        {
            if (slot == null || slot.Length < minSize)
                slot = new int[minSize];
            return slot;
        }

        private static FPVector2[] RentVerts(ref FPVector2[] slot, int minSize)
        {
            if (slot == null || slot.Length < minSize)
                slot = new FPVector2[minSize];
            return slot;
        }

        // For the two that are read before they are written. A fresh array is already zeroed, so
        // the clear only costs anything on the reuse path — which is the path that saves the
        // allocation.
        private static int[] RentCleared(ref int[] slot, int minSize)
        {
            if (slot == null || slot.Length < minSize)
            {
                slot = new int[minSize];
                return slot;
            }
            Array.Clear(slot, 0, minSize);
            return slot;
        }

        private static bool[] RentCleared(ref bool[] slot, int minSize)
        {
            if (slot == null || slot.Length < minSize)
            {
                slot = new bool[minSize];
                return slot;
            }
            Array.Clear(slot, 0, minSize);
            return slot;
        }

        private static FPVector2[] TrimTo(FPVector2[] source, int count)
        {
            var trimmed = new FPVector2[count];
            Array.Copy(source, trimmed, count);
            return trimmed;
        }

        /// <summary>Third vertex of a triangle, opposite the given local edge (0,1,2).</summary>
        private static int ThirdVertexOfEdge(in FPNavMeshTriangle tri, int edgeIndex)
        {
            switch (edgeIndex)
            {
                case 0: return tri.v2; // edge (v0,v1)
                case 1: return tri.v0; // edge (v1,v2)
                default: return tri.v1; // edge (v2,v0)
            }
        }

        /// <summary>The vertex of a triangle that is neither <paramref name="p"/> nor <paramref name="q"/>.</summary>
        private static int ThirdVertexExcept(in FPNavMeshTriangle tri, int p, int q)
        {
            if (tri.v0 != p && tri.v0 != q) return tri.v0;
            if (tri.v1 != p && tri.v1 != q) return tri.v1;
            return tri.v2;
        }

        /// <summary>Local edge index of the edge joining vertices <paramref name="x"/> and <paramref name="y"/>.</summary>
        private static int LocalEdge(in FPNavMeshTriangle tri, int x, int y)
        {
            if ((tri.v0 == x && tri.v1 == y) || (tri.v0 == y && tri.v1 == x)) return 0;
            if ((tri.v1 == x && tri.v2 == y) || (tri.v1 == y && tri.v2 == x)) return 1;
            return 2;
        }

        /// <summary>
        /// From <paramref name="startTri"/>, rotate around vertex V through the walkable fan
        /// (crossing interior edges) until the next boundary edge incident to V is found.
        /// <paramref name="through"/> is the other endpoint of the edge we arrived on.
        /// Returns the boundary edge's triangle/local-edge and its other endpoint (the ring's next vertex).
        /// </summary>
        private static bool FindNextBoundaryEdge(
            ReadOnlySpan<FPNavMeshTriangle> tris, int startTri, int v, int through,
            out int outTri, out int outEdge, out int outOther)
        {
            int rTri = startTri;
            int thr = through;

            for (int i = 0; i < tris.Length + 1; i++)
            {
                int x = ThirdVertexExcept(tris[rTri], v, thr);
                int rEdge = LocalEdge(tris[rTri], v, x);

                if (tris[rTri].GetNeighbor(rEdge) == -1)
                {
                    outTri = rTri;
                    outEdge = rEdge;
                    outOther = x;
                    return true;
                }

                rTri = tris[rTri].GetNeighbor(rEdge);
                thr = x; // in the neighbor we arrived along edge (v, x)
            }

            outTri = -1;
            outEdge = -1;
            outOther = -1;
            return false;
        }
    }
}
