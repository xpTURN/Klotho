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
        {
            var verts = new List<FPVector2>();
            var offsets = new List<int>();
            var segTri = new List<int>(); // segment index (== verts index) -> source triangle

            if (navMesh == null || navMesh.Triangles == null || navMesh.Triangles.Length == 0
                || navMesh.Vertices == null)
            {
                vertices = Array.Empty<FPVector2>();
                polygonOffsets = Array.Empty<int>();
                triSegStart = Array.Empty<int>();
                triSegList = Array.Empty<int>();
                return;
            }

            var tris = navMesh.Triangles;
            int triCount = tris.Length;
            var visited = new bool[triCount * 3]; // boundary-edge visited flags (triIdx*3 + localEdge)

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

                    offsets.Add(verts.Count);

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
                        verts.Add(navMesh.Vertices[cs].ToXZ());
                        segTri.Add(curTri); // this ring vertex starts boundary edge (curTri, curEdge)

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

            vertices = verts.ToArray();
            polygonOffsets = offsets.ToArray();

            // Build forward triangle->segment CSR by counting sort on the source triangle.
            int segCount = segTri.Count;
            triSegStart = new int[triCount + 1];
            for (int s = 0; s < segCount; s++)
                triSegStart[segTri[s] + 1]++;
            for (int ti = 0; ti < triCount; ti++)
                triSegStart[ti + 1] += triSegStart[ti];
            triSegList = new int[segCount];
            var fillCursor = new int[triCount];
            for (int ti = 0; ti < triCount; ti++)
                fillCursor[ti] = triSegStart[ti];
            for (int s = 0; s < segCount; s++)
            {
                int owner = segTri[s];
                triSegList[fillCursor[owner]++] = s;
            }
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
            FPNavMeshTriangle[] tris, int startTri, int v, int through,
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
