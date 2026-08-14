using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Engine-agnostic geometry pipeline for building an <see cref="FPNavMesh"/> from welded
    /// vertices + triangle indices. Shared by the Unity (FPNavMeshExporter) and Godot
    /// (GodotFPNavMeshExporter) editor tools.
    ///
    /// Build-time only — no runtime call path. Lives in the Runtime assembly because that is the
    /// single shared dependency both editor adapters reference. Uses List&lt;&gt; allocations, which
    /// is fine because it never runs on the simulation hot path. Must be public (not internal):
    /// the callers (xpTURN.Klotho.Editor, xpTURN.Klotho.Runtime.Godot) are separate assemblies not
    /// listed in Runtime's InternalsVisibleTo.
    /// </summary>
    public static class FPNavMeshBuildPipeline
    {
        // Geometry robustness constants — consumed only inside this pipeline (single definition).
        private const double DEGENERATE_AREA_EPSILON = 0.0001f;
        private const double T_JUNCTION_EPSILON = 0.002;
        private const double T_JUNCTION_HEIGHT_EPSILON = 0.5;

        /// <summary>
        /// Builds an <see cref="FPNavMesh"/> from welded vertices and triangle indices.
        /// Vertex welding + index remap is engine-specific and performed by the caller.
        /// </summary>
        /// <param name="vertices">Welded vertices (FP64).</param>
        /// <param name="indices">Triangle indices (triplets), referencing <paramref name="vertices"/>.</param>
        /// <param name="areas">Per-triangle area index.</param>
        /// <param name="cellSize">Spatial grid cell size.</param>
        /// <param name="logger">Optional diagnostic logger (IKLogger). Null = silent.</param>
        public static FPNavMesh Build(
            FPVector3[] vertices, int[] indices, int[] areas, double cellSize,
            IKLogger logger = null,
            double bakeAgentRadius = 0, double bakeMaxSlopeDeg = 0,
            double bakeAgentHeight = 0, double bakeAgentClimb = 0)
        {
            return Build(vertices, indices, areas, FP64.FromDouble(cellSize), logger,
                FP64.FromDouble(bakeAgentRadius), FP64.FromDouble(bakeMaxSlopeDeg),
                FP64.FromDouble(bakeAgentHeight), FP64.FromDouble(bakeAgentClimb));
        }

        /// <summary>
        /// FP64 overload — used by the runtime rebaker to inherit bake metadata
        /// bit-exactly from an existing mesh (double round-trips would lose raw bits).
        /// </summary>
        public static FPNavMesh Build(
            FPVector3[] vertices, int[] indices, int[] areas, FP64 cellSize,
            IKLogger logger,
            FP64 bakeAgentRadius, FP64 bakeMaxSlopeDeg,
            FP64 bakeAgentHeight, FP64 bakeAgentClimb)
        {
            // 0. Snap X/Z to the exact-predicate grid + re-weld coincidences.
            //    Idempotent for on-grid input. Downstream steps never create new coordinates
            //    (T-junction splits reuse existing vertices), so the output mesh stays on-grid.
            vertices = SnapToPredicateGrid(vertices, ref indices, logger);

            // 1. Remove degenerate triangles (shrink indices + areas together)
            RemoveDegenerateTriangles(vertices, ref indices, ref areas, DEGENERATE_AREA_EPSILON);

            // 2. T-Junction detection & edge splitting
            SplitTJunctions(vertices, ref indices, ref areas,
                T_JUNCTION_EPSILON, T_JUNCTION_HEIGHT_EPSILON, logger);

            // 2.1 Re-check degenerate triangles after splitting
            RemoveDegenerateTriangles(vertices, ref indices, ref areas, DEGENERATE_AREA_EPSILON);

            return BuildCore(vertices, -1, indices, areas, cellSize, logger,
                bakeAgentRadius, bakeMaxSlopeDeg, bakeAgentHeight, bakeAgentClimb, null);
        }

        /// <summary>
        /// Fast path for conforming triangulations.
        /// Caller contract: the input is already (a) on the predicate grid and weld-complete,
        /// (b) exactly non-degenerate, (c) free of exact T-junctions — an
        /// <see cref="FPConstrainedDelaunay"/> output satisfies all three by construction.
        /// Skips the snap and T-junction scan stages — the pipeline's only input-dependent
        /// double/epsilon arithmetic — keeping the runtime rebake path float-free; degenerate
        /// removal is kept (conservative option (i), preserves full-path sliver semantics).
        /// In DEBUG builds the contract is machine-verified (the skipped checks must be no-ops).
        /// NOTE: <paramref name="vertices"/> is stored by reference in the returned mesh
        /// (ownership transfers to the mesh); the full <see cref="Build(FPVector3[],int[],int[],FP64,IKLogger,FP64,FP64,FP64,FP64)"/>
        /// stores a snapped copy instead.
        /// </summary>
        public static FPNavMesh BuildFromConformingTriangulation(
            FPVector3[] vertices, ReadOnlySpan<int> indices, int[] areas, FP64 cellSize,
            IKLogger logger,
            FP64 bakeAgentRadius, FP64 bakeMaxSlopeDeg,
            FP64 bakeAgentHeight, FP64 bakeAgentClimb,
            FPNavMeshRebakeBufferPool pool = null,
            int vertexCount = -1,
            FPNavMesh previous = null,
            IncrementalOutcome outcome = null)
        {
            // The count, not the array length: `vertices` is routinely a POOLED oversize
            // array whose tail is stale (see VertexGridIndex.TryBuild).
            VerifyConformingContract(vertices, vertexCount < 0 ? vertices.Length : vertexCount, indices);

            // Degenerate removal, fast-path form. The full path's
            // `ref int[]` reassignment is deliberately NOT used here: `indices` may be a pool
            // buffer the caller still owns, and rebinding a local span leaves that ownership
            // intact. Almost always a no-op — but "almost" is the point: CDT output is exactly
            // non-degenerate, yet an exact-valid sliver can still be degenerate under the
            // epsilon predicate, so the check stays (conservative option (i)).
            ReadOnlySpan<int> effective = indices;
            if (CountDegenerate(vertices, indices, FP64.FromDouble(DEGENERATE_AREA_EPSILON)) != 0)
            {
                DegenerateCompactions++;
                CompactDegenerate(vertices, indices, areas, DEGENERATE_AREA_EPSILON,
                    out int[] keptIndices, out int[] keptAreas);
                effective = keptIndices;
                areas = keptAreas;
            }

            // The incremental path diffs against the PREVIOUS mesh's triangle array, and that
            // array is the post-compaction one — which is why this branch sits here and not
            // before the block above. The compaction is conditional, so diffing the raw CDT
            // output would agree almost always and disagree exactly when a sliver trips the
            // epsilon: right most of the time is the worst kind of wrong.
            if (previous != null)
            {
                FPNavMesh patched = TryBuildIncremental(
                    previous, vertices, vertexCount, effective, areas, cellSize,
                    bakeAgentRadius, bakeMaxSlopeDeg, bakeAgentHeight, bakeAgentClimb, pool, outcome, logger);
                if (patched != null)
                    return patched;
            }

            return BuildCore(vertices, vertexCount, effective, areas, cellSize, logger,
                bakeAgentRadius, bakeMaxSlopeDeg, bakeAgentHeight, bakeAgentClimb, pool);
        }

        /// <summary>
        /// How many times the conforming path has actually compacted degenerate triangles.
        ///
        /// <para>Exposed for one reason: that compaction is CONDITIONAL, and the incremental
        /// patch's diff has to run on its output rather than on the raw triangulation. A test that
        /// means to cover that interaction has to know whether the compaction ever fired — with
        /// integer-ish coordinates it essentially never does, so a sweep can silently stop
        /// exercising the case it was written for.</para>
        /// </summary>
        internal static int DegenerateCompactions;

        /// <summary>
        /// Why a rebake did or did not take the incremental path. Exists so a test can assert
        /// that the mechanism actually fired — every correctness gate here compares outputs, and
        /// output comparison stays green when the incremental path silently never runs.
        /// </summary>
        public sealed class IncrementalOutcome
        {
            public int Incremental;
            public int FallbackGridGeometry;
            /// <summary>
            /// The patch produced a mesh with an edge two triangles both call a boundary, so it was
            /// discarded and the full build used instead. Non-zero means a real patch bug — see the
            /// check at the end of TryBuildIncremental.
            /// </summary>
            public int FallbackDuplicateBoundaryEdge;
            /// <summary>Nothing to diff against — an empty mesh on one side or the other.</summary>
            public int FallbackEmpty;
            public int FallbackVertexShift;

            // There is deliberately no FallbackNoPrevious counter and no Reset().
            //
            // The first could never be non-zero: BuildFromConformingTriangulation tests
            // `previous != null` BEFORE calling TryBuildIncremental, so the only code that can
            // touch these counters is never reached without a previous mesh. A field that always
            // reads 0 is worse than absent in a type whose whole job is "assert the mechanism
            // fired" — a test could assert it and believe it had proved something.
            //
            // Reset() had no caller either, and the accumulate-and-snapshot shape is what
            // consumers actually use: FPNavMeshRebakeContext.PatchOutcome runs for the life of the
            // context and callers take a before/after difference around the rebake they care
            // about.
        }

        /// <summary>
        /// DEBUG-only machine verification of the conforming-input contract: (a) every
        /// referenced vertex is on the predicate grid and XZ-unique (weld-complete), (b)(c) the
        /// skipped epsilon checks must be no-ops — the T-junction scan (same predicate as the
        /// full path, grid-index accelerated) must find zero candidates. A candidate here means
        /// either misuse (non-CDT input) or a premise-divergence case
        /// (a near-edge sliver can appear even on exact-valid geometry).
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void VerifyConformingContract(FPVector3[] vertices, int vertexCount, ReadOnlySpan<int> indices)
        {
            var seenXZ = new HashSet<(long, long)>();
            var referenced = new HashSet<int>();
            for (int i = 0; i < indices.Length; i++)
                referenced.Add(indices[i]);
            foreach (int vi in referenced)
            {
                FPVector3 v = vertices[vi];
                long sx = FPGeoPredicates.Snap(v.x);
                long sz = FPGeoPredicates.Snap(v.z);
                System.Diagnostics.Debug.Assert(
                    FPGeoPredicates.Unsnap(sx).RawValue == v.x.RawValue &&
                    FPGeoPredicates.Unsnap(sz).RawValue == v.z.RawValue,
                    "BuildFromConformingTriangulation: referenced vertex off the predicate grid (contract (a))");
                System.Diagnostics.Debug.Assert(seenXZ.Add((sx, sz)),
                    "BuildFromConformingTriangulation: duplicate referenced XZ (weld contract (a))");
            }

            VertexGridIndex index = VertexGridIndex.TryBuild(vertices, vertexCount);
            var mids = new List<(int vertIdx, double tParam)>();
            double epsSq = T_JUNCTION_EPSILON * T_JUNCTION_EPSILON;
            for (int t = 0; t < indices.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int ea = indices[t + e];
                    int eb = indices[t + (e + 1) % 3];
                    mids.Clear();
                    FindVerticesOnEdge(vertices, vertexCount, index, ea, eb, epsSq, T_JUNCTION_HEIGHT_EPSILON, mids);
                    System.Diagnostics.Debug.Assert(mids.Count == 0,
                        "BuildFromConformingTriangulation: epsilon T-junction candidate on conforming input (contract (c) / premise divergence)");
                }
            }
        }

        /// <summary>Shared stages 3-6: triangle structs, adjacency/portals, bounds, spatial grid.</summary>
        private static FPNavMesh BuildCore(
            FPVector3[] vertices, int vertexCount, ReadOnlySpan<int> indices, int[] areas, FP64 cellSize,
            IKLogger logger,
            FP64 bakeAgentRadius, FP64 bakeMaxSlopeDeg,
            FP64 bakeAgentHeight, FP64 bakeAgentClimb,
            FPNavMeshRebakeBufferPool pool)
        {
            // vertexCount < 0 = "the array is exactly its content". A rebake handing in a pooled
            // (oversized) vertex array passes the real count — from here down NOTHING may use
            // vertices.Length, and the same holds for the triangle array rented just below.
            pool = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
            if (vertexCount < 0)
                vertexCount = vertices.Length;

            // 3. Create triangle structs + precompute data
            int triCount = indices.Length / 3;
            var triangles = pool.RentOutputTriangles(triCount);
            for (int i = 0; i < triCount; i++)
                triangles[i] = MakeTriangle(vertices, indices, i, areas[i]);

            // 4. Build adjacency + portals
            BuildAdjacency(triangles, triCount, vertices, pool);

            // 5. Compute XZ bounds
            FPBounds2 boundsXZ = ComputeBoundsXZ(vertices, vertexCount);

            // 6. Build spatial grid
            FP64 fpCellSize = cellSize;
            BuildSpatialGrid(vertices, triangles, triCount, boundsXZ, fpCellSize, pool,
                out int gridWidth, out int gridHeight, out FPVector2 gridOrigin,
                out int[] gridCells, out int[] gridTriangles, out int gridTriangleCount);

            return new FPNavMesh(
                vertices, triangles, boundsXZ,
                gridCells, gridTriangles,
                gridWidth, gridHeight, fpCellSize, gridOrigin,
                bakeAgentRadius, bakeMaxSlopeDeg,
                bakeAgentHeight, bakeAgentClimb,
                vertexCount, triCount, gridTriangleCount);
        }

        #region Incremental patch

        /// <summary>
        /// Rebuilds the mesh by PATCHING the previous one instead of recomputing it, or returns
        /// null when a guard says the patch does not apply (the caller then runs the full path).
        ///
        /// <para>What makes this possible: a rebake changes almost nothing. One building on a
        /// 22k-triangle stage adds 18 triangles, removes 12, and leaves 22,309 with their vertex
        /// triples intact — only their INDEX moves, by at most a handful. The canonical output is
        /// sorted by rotated vertex triple, so the old and new arrays can be merged in one linear
        /// pass that yields the added set, the removed set and the old→new index map at once.</para>
        ///
        /// <para><b>The previous mesh never reaches the output.</b> The added/removed sets are a
        /// function of the two canonical arrays, and the final array is determined by the new
        /// triangulation alone — the previous mesh only decides HOW this gets there, not WHAT it
        /// is. That is what makes the path a pure performance switch: a peer that joined and
        /// rebuilt from scratch, a peer that patched, and a peer that fell back to the full path
        /// must all produce byte-identical meshes.</para>
        /// </summary>
        private static FPNavMesh TryBuildIncremental(
            FPNavMesh previous, FPVector3[] vertices, int vertexCount, ReadOnlySpan<int> indices,
            int[] areas, FP64 cellSize, FP64 bakeAgentRadius, FP64 bakeMaxSlopeDeg,
            FP64 bakeAgentHeight, FP64 bakeAgentClimb,
            FPNavMeshRebakeBufferPool pool, IncrementalOutcome outcome, IKLogger logger = null)
        {
            pool = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
            if (vertexCount < 0)
                vertexCount = vertices.Length;

            int newTriCount = indices.Length / 3;
            int oldTriCount = previous.TriangleCount;
            if (newTriCount == 0 || oldTriCount == 0)
            {
                if (outcome != null) outcome.FallbackEmpty++;
                return null;
            }

            // Guard: the grid geometry must be the one the previous CSR was laid out on. It
            // normally is — the vertex array always carries every base vertex (only triangles get
            // filtered) and a building's corners are strictly inside the walkable region, so the
            // bounds are the base's — but that is an argument, not an invariant. Checking is a
            // handful of comparisons; being wrong would rewrite the grid against the wrong
            // dimensions.
            FPBounds2 boundsXZ = ComputeBoundsXZ(vertices, vertexCount);
            int gridWidth = FP64.Ceiling(boundsXZ.size.x / cellSize).ToInt() + 1;
            int gridHeight = FP64.Ceiling(boundsXZ.size.y / cellSize).ToInt() + 1;
            if (gridWidth != previous.GridWidth || gridHeight != previous.GridHeight
                || boundsXZ.min.x.RawValue != previous.GridOrigin.x.RawValue
                || boundsXZ.min.y.RawValue != previous.GridOrigin.y.RawValue
                || cellSize.RawValue != previous.GridCellSize.RawValue)
            {
                if (outcome != null) outcome.FallbackGridGeometry++;
                return null;
            }

            // Guard: the diff matches triangles by VERTEX INDEX triple, so an index must mean the
            // same point in both meshes. It usually does — base vertices come first and never
            // move, building corners are appended — but not always: remove a building and every
            // later building's corners shift down, after which an unchanged-looking triple names
            // different geometry. That is not a subtle difference either; it produces a triangle
            // with the right indices and the wrong shape.
            //
            // The cheap sufficient condition is that the shorter vertex array is a prefix of the
            // longer one. Checking it costs one pass over the vertices and turns the diff's key
            // from "usually meaningful" into "meaningful".
            ReadOnlySpan<FPVector3> oldVerts = previous.Vertices;
            int shared = oldVerts.Length < vertexCount ? oldVerts.Length : vertexCount;
            for (int i = 0; i < shared; i++)
            {
                if (oldVerts[i].x.RawValue != vertices[i].x.RawValue
                    || oldVerts[i].y.RawValue != vertices[i].y.RawValue
                    || oldVerts[i].z.RawValue != vertices[i].z.RawValue)
                {
                    if (outcome != null) outcome.FallbackVertexShift++;
                    return null;
                }
            }

            ReadOnlySpan<FPNavMeshTriangle> oldTris = previous.Triangles;

            // ── diff: one merge over two arrays sorted by the same key ──────────
            int[] newIndexByOld = pool.PatchNewIndexByOld(oldTriCount);
            int[] added = pool.PatchAdded(newTriCount);
            int addedCount = 0;
            {
                int i = 0, j = 0;
                while (i < oldTriCount && j < newTriCount)
                {
                    int c = CompareTriple(
                        oldTris[i].v0, oldTris[i].v1, oldTris[i].v2,
                        indices[j * 3], indices[j * 3 + 1], indices[j * 3 + 2]);
                    if (c == 0) { newIndexByOld[i] = j; i++; j++; }
                    else if (c < 0) { newIndexByOld[i] = -1; i++; }
                    else { added[addedCount++] = j; j++; }
                }
                while (i < oldTriCount) newIndexByOld[i++] = -1;
                while (j < newTriCount) added[addedCount++] = j++;
            }

            var triangles = pool.RentOutputTriangles(newTriCount);

            // ── survivors: copy, then remap the only fields that hold triangle indices ──
            // A neighbour that was deleted becomes -1 here and its edge is re-paired below.
            for (int o = 0; o < oldTriCount; o++)
            {
                int n = newIndexByOld[o];
                if (n < 0)
                    continue;
                FPNavMeshTriangle t = oldTris[o];

                // The copy brings the GEOMETRY-derived fields — vertex ids, area, centres, Y range —
                // which a survivor keeps by definition. It must not bring the fields the BUILD owns.
                // Those are re-derived from `areas` exactly as the full path derives them, because
                // `previous` is an INSTALLED mesh and carries post-build writes a from-scratch
                // rebuild would never reproduce: FPNavMeshRebaker.InheritUniformAreaAttributes
                // stamps areaMask/costMultiplier onto it after its own build, and isBlocked carries
                // whatever the runtime mutated. Carrying either across made the patched mesh differ
                // from the full build in precisely the fields nothing downstream re-derives — and
                // for isBlocked nothing downstream normalises it either, so a patching peer and a
                // from-scratch joiner ended up with different meshes.
                t.areaMask = 1 << areas[n];
                t.costMultiplier = FP64.One;
                t.isBlocked = false;

                for (int e = 0; e < 3; e++)
                {
                    int nb = t.GetNeighbor(e);
                    if (nb >= 0)
                    {
                        int mapped = newIndexByOld[nb];
                        if (mapped < 0)
                        {
                            t.SetNeighbor(e, -1);
                            t.SetPortal(e, -1, -1);
                        }
                        else
                        {
                            t.SetNeighbor(e, mapped);
                        }
                    }
                }
                triangles[n] = t;
            }

            // ── added: built exactly as the full path builds them ───────────────
            // Including the area index. Omitting it defaulted every added triangle to area 0 while
            // the full path used areas[i], so on any mesh with a non-default area the two paths
            // disagreed on the added band as well.
            for (int a = 0; a < addedCount; a++)
                triangles[added[a]] = MakeTriangle(vertices, indices, added[a], areas[added[a]]);

            // ── re-pair only the edges whose pairing can have changed ───────────
            // Three sources, and the third is the one that is easy to miss: a survivor's edge that
            // was ALREADY a boundary can gain a neighbour, when a change frees up the space on the
            // other side of it. That survivor is neither added nor a deleted triangle's neighbour,
            // so leaving it out leaves both sides unpaired and the space stays walled off.
            //
            // It is tempting to drop: on integer-aligned placements it never fires, and removing it
            // passes every fixed fixture here. It fires on placements at sub-unit offsets, which is
            // why the randomised sweep jitters its coordinates — and when it was tried without this
            // source, the duplicate-boundary-edge scan named the two triangles within seconds.
            // Keeping it also costs nothing measurable: the pass below already walks every triangle
            // to find them, and the extra records sort in the noise.
            int recordCap = addedCount * 3 + newTriCount * 3;
            EdgeRecord[] records = pool.PatchEdges(recordCap);
            int n2 = 0;
            int maxIndex = 0;
            for (int a = 0; a < addedCount; a++)
                AppendEdges(triangles, added[a], records, ref n2, ref maxIndex);
            // `added` is ascending and so is t, so one cursor replaces a scan per triangle.
            int addedCursor = 0;
            for (int t = 0; t < newTriCount; t++)
            {
                if (addedCursor < addedCount && added[addedCursor] == t)
                {
                    addedCursor++;
                    continue;
                }
                for (int e = 0; e < 3; e++)
                {
                    if (triangles[t].GetNeighbor(e) < 0)
                        AppendEdge(triangles, t, e, records, ref n2, ref maxIndex);
                }
            }

            RadixSortByKey(records, n2, maxIndex, pool);
            PairSortedEdges(triangles, records, n2, vertices);

            // ── grid CSR: remap in place, splice the added triangles in ─────────
            PatchSpatialGrid(previous, vertices, triangles, newTriCount, newIndexByOld, oldTriCount,
                added, addedCount, boundsXZ, cellSize, gridWidth, gridHeight, pool,
                out int[] gridCells, out int[] gridTriangles, out int gridTriangleCount);

            var patched = new FPNavMesh(
                vertices, triangles, boundsXZ,
                gridCells, gridTriangles,
                gridWidth, gridHeight, cellSize, boundsXZ.min,
                bakeAgentRadius, bakeMaxSlopeDeg,
                bakeAgentHeight, bakeAgentClimb,
                vertexCount, newTriCount, gridTriangleCount);

            // The one correctness check on this path that runs in RELEASE too.
            //
            // Everything else guarding the patch is [Conditional("DEBUG")], and comparing
            // fingerprints is not a substitute: ComputeFingerprint covers vertices, vertex indices,
            // areaMask, costMultiplier and isBlocked, and NOT neighbours, portals or the grid — a
            // patch that forgets to re-pair an edge hashes identical to a correct mesh. This
            // implementation has already shipped exactly that bug once.
            //
            // Adjacency symmetry cannot see it either: a MISSED pairing leaves both sides at -1,
            // and "if A says B then B says A" is satisfied by that. Two triangles that share an
            // edge and both call it boundary is the shape that betrays it, and no correct mesh
            // produces it. Measured on Field at 0.429 ms inside an 11.645 ms PATCH rebake — 3.7%,
            // with zero allocation, which is what makes it affordable outside DEBUG. The
            // alternative, rebuilding the whole mesh to compare, is not: that is the 9.5 ms full
            // build, i.e. the check would cost more than twenty times what it does.
            //
            // A violation DISCARDS the patch rather than throwing. The patch is an optimisation and
            // the full build is the reference, so the caller falling back is both correct and
            // already the contract for every other guard in this method. Throwing would abort a
            // placement over something the engine can simply do the slow way.
            string duplicateEdge = DescribeDuplicateBoundaryEdge(patched, pool);
            if (duplicateEdge != null)
            {
                logger?.KError($"[FPNavMeshBuildPipeline] incremental patch produced a duplicate "
                    + $"boundary edge and was discarded — {duplicateEdge}");
                if (outcome != null) outcome.FallbackDuplicateBoundaryEdge++;
                return null;
            }

            if (outcome != null) outcome.Incremental++;
            VerifyPatchMatchesFullBuild(patched, vertices, vertexCount, indices, areas, cellSize,
                bakeAgentRadius, bakeMaxSlopeDeg, bakeAgentHeight, bakeAgentClimb);
            return patched;
        }

        /// <summary>
        /// DEBUG-only: builds the same mesh the long way and compares it element by element.
        ///
        /// <para>This is not a sampled check — it runs on EVERY patched rebake, because the way
        /// this optimisation fails is not "slower" but "a mesh that differs from what a peer who
        /// rebuilt from scratch would get", and the first symptom of that is a desync at the next
        /// join. Sampling would turn a deterministic failure into an intermittent one.</para>
        ///
        /// <para>The reference build runs UNPOOLED. Both paths otherwise draw their output arrays
        /// from the same retirement chain, which hands out one generation — they would be handed
        /// the same arrays and the comparison would be reading what it just wrote.</para>
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void VerifyPatchMatchesFullBuild(
            FPNavMesh patched, FPVector3[] vertices, int vertexCount, ReadOnlySpan<int> indices,
            int[] areas, FP64 cellSize, FP64 bakeAgentRadius, FP64 bakeMaxSlopeDeg,
            FP64 bakeAgentHeight, FP64 bakeAgentClimb)
        {
            // The reference has to be built from the CALLER'S areas. It used to allocate a fresh
            // all-zero array, which made the comparison test the patch against a mesh nobody would
            // ever build — on a non-default area every survivor differed and the check threw on
            // valid input. Cloned for the same reason the vertices are: the reference build must
            // not be able to disturb the inputs the real build already consumed.
            FPNavMesh reference = BuildCore(
                (FPVector3[])vertices.Clone(), vertexCount, indices, (int[])areas.Clone(), cellSize, null,
                bakeAgentRadius, bakeMaxSlopeDeg, bakeAgentHeight, bakeAgentClimb, null);

            string diff = DescribeFirstDifference(patched, reference);
            if (diff != null)
                throw new InvalidOperationException("incremental patch: " + diff);
        }

        /// <summary>
        /// The first vertex pair claimed as a boundary edge by two different triangles, or null
        /// when there is none.
        ///
        /// <para><b>This is the invariant that catches an edge the patch forgot to re-pair</b>, and
        /// it exists because the obvious check does not. Adjacency symmetry — "if A says B, then B
        /// says A" — holds perfectly when a pairing is MISSED, because both sides then say -1. The
        /// failure is invisible to it. But two triangles that geometrically share an edge and both
        /// report it as boundary produce the same vertex pair twice, and no correct mesh does:
        /// an interior edge belongs to exactly two triangles, and either they point at each other
        /// or the mesh is wrong.</para>
        ///
        /// <para>O(T) plus a radix sort of the boundary edges — far cheaper than rebuilding the
        /// whole mesh to compare, which is what makes it the candidate for a check that could one
        /// day run outside DEBUG.</para>
        /// </summary>
        internal static string DescribeDuplicateBoundaryEdge(
            FPNavMesh mesh, FPNavMeshRebakeBufferPool pool = null)
        {
            pool = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
            int cap = mesh.TriangleCount * 3;
            if (cap == 0)
                return null;

            EdgeRecord[] records = pool.PatchBoundaryEdges(cap);
            int n = 0, maxIndex = 0;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                FPNavMeshTriangle tri = mesh.Triangles[t];
                for (int e = 0; e < 3; e++)
                {
                    if (tri.GetNeighbor(e) >= 0)
                        continue;
                    tri.GetEdgeVertices(e, out int va, out int vb);
                    int maxV = va < vb ? vb : va;
                    if (maxV > maxIndex)
                        maxIndex = maxV;
                    records[n] = new EdgeRecord(EdgeKey(va, vb), t * 3 + e);
                    n++;
                }
            }
            if (n < 2)
                return null;

            RadixSortByKey(records, n, maxIndex, pool);
            for (int i = 1; i < n; i++)
            {
                if (records[i].Key != records[i - 1].Key)
                    continue;
                int oa = records[i - 1].Order, ob = records[i].Order;
                return $"triangles {oa / 3} and {ob / 3} both claim vertex pair "
                    + $"({records[i].Key >> 32},{(int)records[i].Key}) as a boundary edge — "
                    + "an edge they share was never paired";
            }
            return null;
        }

        /// <summary>
        /// The first place two meshes differ, or null when they are identical. Compares everything
        /// the build stage produces: the triangle structs, the adjacency, the portals and the
        /// spatial grid.
        ///
        /// <para><b>Deliberately broader than <c>ComputeFingerprint</c>.</b> That hash covers the
        /// vertices, each triangle's vertex indices, areaMask, costMultiplier and isBlocked — it
        /// does NOT cover neighbours, portals, per-triangle geometry or the grid. A mesh can carry
        /// a wrong adjacency, or a grid that hides triangles from queries, and hash identical.
        /// Comparing fingerprints is therefore not a check on this code, and it was exactly the
        /// gap a stale copied triangle slipped through.</para>
        ///
        /// <para>Shared by the DEBUG always-on check and the release-mode gate so the two cannot
        /// drift into disagreeing about what "identical" means.</para>
        /// </summary>
        internal static string DescribeFirstDifference(FPNavMesh patched, FPNavMesh reference)
        {
            if (patched.TriangleCount != reference.TriangleCount)
                return $"triangle count {patched.TriangleCount} != {reference.TriangleCount}";

            for (int t = 0; t < reference.TriangleCount; t++)
            {
                FPNavMeshTriangle a = patched.Triangles[t], b = reference.Triangles[t];
                for (int e = 0; e < 3; e++)
                {
                    if (a.GetNeighbor(e) != b.GetNeighbor(e))
                        return $"triangle {t} edge {e} neighbour {a.GetNeighbor(e)} != {b.GetNeighbor(e)}";
                    a.GetPortal(e, out int al, out int ar);
                    b.GetPortal(e, out int bl, out int br);
                    if (al != bl || ar != br)
                        return $"triangle {t} edge {e} portal ({al},{ar}) != ({bl},{br})";
                }
                if (a.v0 != b.v0 || a.v1 != b.v1 || a.v2 != b.v2
                    || a.area.RawValue != b.area.RawValue
                    || a.centerXZ.x.RawValue != b.centerXZ.x.RawValue
                    || a.centerXZ.y.RawValue != b.centerXZ.y.RawValue
                    || a.minY.RawValue != b.minY.RawValue || a.maxY.RawValue != b.maxY.RawValue
                    || a.centerY.RawValue != b.centerY.RawValue
                    || a.areaMask != b.areaMask
                    || a.costMultiplier.RawValue != b.costMultiplier.RawValue
                    || a.isBlocked != b.isBlocked)
                {
                    // Name the field that actually differs. The old message printed a fixed subset
                    // — v/area/areaMask/minY — so a costMultiplier or isBlocked difference reported
                    // four identical-looking pairs and sent the reader hunting. That is precisely
                    // how this check reads when it fires, so it has to say what it found.
                    string field =
                        a.v0 != b.v0 || a.v1 != b.v1 || a.v2 != b.v2
                            ? $"vertices ({a.v0},{a.v1},{a.v2}) vs ({b.v0},{b.v1},{b.v2})"
                        : a.area.RawValue != b.area.RawValue
                            ? $"area {a.area.RawValue} vs {b.area.RawValue}"
                        : a.centerXZ.x.RawValue != b.centerXZ.x.RawValue || a.centerXZ.y.RawValue != b.centerXZ.y.RawValue
                            ? $"centerXZ ({a.centerXZ.x.RawValue},{a.centerXZ.y.RawValue}) vs ({b.centerXZ.x.RawValue},{b.centerXZ.y.RawValue})"
                        : a.minY.RawValue != b.minY.RawValue
                            ? $"minY {a.minY.RawValue} vs {b.minY.RawValue}"
                        : a.maxY.RawValue != b.maxY.RawValue
                            ? $"maxY {a.maxY.RawValue} vs {b.maxY.RawValue}"
                        : a.centerY.RawValue != b.centerY.RawValue
                            ? $"centerY {a.centerY.RawValue} vs {b.centerY.RawValue}"
                        : a.areaMask != b.areaMask
                            ? $"areaMask {a.areaMask} vs {b.areaMask}"
                        : a.costMultiplier.RawValue != b.costMultiplier.RawValue
                            ? $"costMultiplier {a.costMultiplier.RawValue} vs {b.costMultiplier.RawValue}"
                        : $"isBlocked {a.isBlocked} vs {b.isBlocked}";
                    return $"triangle {t} struct differs — {field}";
                }
            }

            if (patched.GridTriangleCount != reference.GridTriangleCount)
                return $"grid entries {patched.GridTriangleCount} != {reference.GridTriangleCount}";
            if (patched.GridWidth != reference.GridWidth || patched.GridHeight != reference.GridHeight
                || patched.GridCellSize.RawValue != reference.GridCellSize.RawValue
                || patched.GridOrigin.x.RawValue != reference.GridOrigin.x.RawValue
                || patched.GridOrigin.y.RawValue != reference.GridOrigin.y.RawValue)
                return "grid geometry differs";

            ReadOnlySpan<int> pc = patched.GridCells, rc = reference.GridCells;
            for (int i = 0; i < rc.Length; i++)
            {
                if (pc[i] != rc[i])
                    return $"grid cell slot {i} is {pc[i]}, full build says {rc[i]}";
            }
            ReadOnlySpan<int> pt = patched.GridTriangles, rt = reference.GridTriangles;
            for (int i = 0; i < rt.Length; i++)
            {
                if (pt[i] != rt[i])
                    return $"grid entry {i} is {pt[i]}, full build says {rt[i]}";
            }
            return null;
        }

        private static int CompareTriple(int a0, int a1, int a2, int b0, int b1, int b2)
        {
            if (a0 != b0) return a0 < b0 ? -1 : 1;
            if (a1 != b1) return a1 < b1 ? -1 : 1;
            if (a2 != b2) return a2 < b2 ? -1 : 1;
            return 0;
        }

        private static void AppendEdges(
            FPNavMeshTriangle[] triangles, int t, EdgeRecord[] records, ref int n, ref int maxIndex)
        {
            for (int e = 0; e < 3; e++)
                AppendEdge(triangles, t, e, records, ref n, ref maxIndex);
        }

        /// <summary>
        /// Identity of an undirected edge: the vertex pair ordered low-then-high, packed into one
        /// long. Injective for non-negative indices, so equal keys mean the same edge.
        ///
        /// <para>Shared by all three adjacency implementations on purpose, and that does NOT dent
        /// the independence <see cref="BuildAdjacencyDictionaryReference"/> exists for. That
        /// baseline is independent in its PAIRING algorithm — a hash map against two sorts. Edge
        /// identity is not an algorithm, it is the definition the three have to agree on before
        /// their outputs can be compared at all; three copies of it bought nothing and were three
        /// places to mistype <c>&lt;</c>.</para>
        /// </summary>
        private static long EdgeKey(int va, int vb)
        {
            int minV = va < vb ? va : vb;
            int maxV = va < vb ? vb : va;
            return ((long)minV << 32) | (uint)maxV;
        }

        private static void AppendEdge(
            FPNavMeshTriangle[] triangles, int t, int e, EdgeRecord[] records, ref int n, ref int maxIndex)
        {
            triangles[t].GetEdgeVertices(e, out int va, out int vb);
            int maxV = va < vb ? vb : va;
            if (maxV > maxIndex)
                maxIndex = maxV;
            records[n] = new EdgeRecord(EdgeKey(va, vb), t * 3 + e);
            n++;
        }

        #endregion

        /// <summary>
        /// One triangle's struct, from its three vertices. Shared by the full path and the
        /// incremental patch — the two must agree field for field, and a copy of this code would
        /// eventually disagree in a way that shows up only as a fingerprint split.
        /// </summary>
        private static FPNavMeshTriangle MakeTriangle(
            FPVector3[] vertices, ReadOnlySpan<int> indices, int i, int areaIndex = 0)
        {
            int v0 = indices[i * 3];
            int v1 = indices[i * 3 + 1];
            int v2 = indices[i * 3 + 2];

            FPVector2 a = vertices[v0].ToXZ();
            FPVector2 b = vertices[v1].ToXZ();
            FPVector2 c = vertices[v2].ToXZ();

            // Y range (multi-level space support)
            FP64 y0 = vertices[v0].y;
            FP64 y1 = vertices[v1].y;
            FP64 y2 = vertices[v2].y;
            FP64 minY = FP64.Min(FP64.Min(y0, y1), y2);
            FP64 maxY = FP64.Max(FP64.Max(y0, y1), y2);

            return new FPNavMeshTriangle
            {
                v0 = v0, v1 = v1, v2 = v2,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                // portalFlip stays 0 — meaningless until an edge gets a neighbor, and
                // GetPortal reports (-1, -1) for boundary edges regardless.
                centerXZ = new FPVector2(
                    (a.x + b.x + c.x) / FP64.FromInt(3),
                    (a.y + b.y + c.y) / FP64.FromInt(3)),
                area = FP64.Abs(FPNavMeshQuery.TriangleArea2D(a, b, c)),
                areaMask = 1 << areaIndex,
                costMultiplier = FP64.One,
                isBlocked = false,
                minY = minY,
                maxY = maxY,
                centerY = (minY + maxY) * FP64.Half,
            };
        }

        #region Predicate-grid snap

        /// <summary>
        /// Snaps vertex X/Z to the exact-predicate grid (<see cref="FPGeoPredicates"/>, floor to
        /// 1/2^SNAP_FRAC_BITS world units) and re-welds vertices that became exactly coincident,
        /// remapping indices deterministically (first occurrence wins).
        ///
        /// Y is intentionally untouched: the exact predicates are XZ-only, and Y carries
        /// multi-level data — the weld key includes Y so stacked floors sharing an XZ column
        /// are never merged. Collapsed slivers produced by the snap are absorbed by the
        /// existing degenerate-removal step that runs right after.
        ///
        /// Orientation flips (a sliver's apex crossing its base under the &lt;= 1-cell move) are
        /// detected against the pre-snap winding and reported via <paramref name="logger"/>;
        /// such triangles are typically sub-epsilon and removed by degenerate filtering, and the
        /// bake-validation gate requires zero on real assets.
        /// </summary>
        private static FPVector3[] SnapToPredicateGrid(
            FPVector3[] vertices, ref int[] indices, IKLogger logger)
        {
            var snapped = new FPVector3[vertices.Length];
            int movedCount = 0;

            for (int i = 0; i < vertices.Length; i++)
            {
                FPVector3 v = vertices[i];
                FP64 sx = FPGeoPredicates.Quantize(v.x);
                FP64 sz = FPGeoPredicates.Quantize(v.z);
                if (sx.RawValue != v.x.RawValue || sz.RawValue != v.z.RawValue)
                    movedCount++;
                snapped[i] = new FPVector3(sx, v.y, sz);
            }

            // Orientation-flip detection (XZ signed area, pre vs post) before any index remap.
            int flipCount = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int v0 = indices[i], v1 = indices[i + 1], v2 = indices[i + 2];
                FP64 pre = FPNavMeshQuery.TriangleArea2D(
                    vertices[v0].ToXZ(), vertices[v1].ToXZ(), vertices[v2].ToXZ());
                FP64 post = FPNavMeshQuery.TriangleArea2D(
                    snapped[v0].ToXZ(), snapped[v1].ToXZ(), snapped[v2].ToXZ());
                if ((pre > FP64.Zero && post < FP64.Zero) || (pre < FP64.Zero && post > FP64.Zero))
                    flipCount++;
            }
            if (flipCount > 0)
            {
                logger?.KError($"[FPNavMeshBuildPipeline] Predicate-grid snap flipped the orientation of " +
                    $"{flipCount} input triangle(s) (slivers thinner than one grid cell; degenerate filtering " +
                    $"removes sub-epsilon ones). Bake-validation gate expects zero on shipping assets.");
            }

            // Re-weld exact coincidences created by the snap (same X/Y/Z raw). First occurrence wins.
            var canonical = new Dictionary<(long x, long y, long z), int>(snapped.Length);
            var remap = new int[snapped.Length];
            int weldCount = 0;
            for (int i = 0; i < snapped.Length; i++)
            {
                var key = (snapped[i].x.RawValue, snapped[i].y.RawValue, snapped[i].z.RawValue);
                if (canonical.TryGetValue(key, out int first))
                {
                    remap[i] = first;
                    weldCount++;
                }
                else
                {
                    canonical[key] = i;
                    remap[i] = i;
                }
            }

            if (weldCount > 0)
            {
                var remapped = new int[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    remapped[i] = remap[indices[i]];
                indices = remapped;
            }

            if (movedCount > 0 || weldCount > 0)
            {
                logger?.KInformation($"[FPNavMeshBuildPipeline] Predicate-grid snap: {movedCount} vertices moved " +
                    $"(grid 1/{1 << FPGeoPredicates.SNAP_FRAC_BITS} world units), {weldCount} welded");
            }

            return snapped;
        }

        #endregion

        #region Degenerate triangle removal

        /// <summary>
        /// The single degeneracy predicate. <see cref="CountDegenerate"/>,
        /// <see cref="RemoveDegenerateTriangles"/> (full path) and
        /// <see cref="CompactDegenerate"/> (fast path) all route through here so the three can
        /// never disagree about what is degenerate — the invariant the count/remove pair was
        /// already documented to hold, now enforced by there being one implementation.
        /// </summary>
        private static bool IsDegenerate(FPVector3[] vertices, int v0, int v1, int v2, FP64 fpEpsilon)
        {
            if (v0 == v1 || v1 == v2 || v2 == v0)
                return true;

            FPVector2 a = vertices[v0].ToXZ();
            FPVector2 b = vertices[v1].ToXZ();
            FPVector2 c = vertices[v2].ToXZ();
            return FP64.Abs(FPNavMeshQuery.TriangleArea2D(a, b, c)) < fpEpsilon;
        }

        /// <summary>
        /// Fast-path degenerate removal: writes the survivors into fresh arrays instead of
        /// reassigning the caller's reference, so a pooled input buffer keeps its owner
        /// Only reached when <see cref="CountDegenerate"/> found something, so the allocation
        /// here is the rare path, not the steady state.
        /// </summary>
        private static void CompactDegenerate(
            FPVector3[] vertices, ReadOnlySpan<int> indices, int[] areas, double areaEpsilon,
            out int[] keptIndices, out int[] keptAreas)
        {
            FP64 fpEpsilon = FP64.FromDouble(areaEpsilon);
            int kept = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                if (!IsDegenerate(vertices, indices[i], indices[i + 1], indices[i + 2], fpEpsilon))
                    kept++;
            }

            keptIndices = new int[kept * 3];
            keptAreas = new int[kept];
            int w = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int v0 = indices[i], v1 = indices[i + 1], v2 = indices[i + 2];
                if (IsDegenerate(vertices, v0, v1, v2, fpEpsilon))
                    continue;
                keptIndices[w * 3] = v0;
                keptIndices[w * 3 + 1] = v1;
                keptIndices[w * 3 + 2] = v2;
                keptAreas[w] = areas[i / 3];
                w++;
            }
        }

        private static void RemoveDegenerateTriangles(FPVector3[] vertices, ref int[] indices, ref int[] areas, double areaEpsilon)
        {
            FP64 fpEpsilon = FP64.FromDouble(areaEpsilon);

            // Nothing to remove is the normal case on the conforming fast path (CDT output is
            // exactly non-degenerate), and rebuilding both arrays anyway cost ~1MB per rebake
            // at 22k triangles. Count first and keep the caller's arrays when the answer is
            // zero — the rebuilt arrays were element-wise identical in that case, and neither
            // array is stored by the mesh, so there is no ownership to transfer.
            if (CountDegenerate(vertices, indices, fpEpsilon) == 0)
                return;

            var valid = new List<int>();
            var validAreas = new List<int>();

            for (int i = 0; i < indices.Length; i += 3)
            {
                int v0 = indices[i];
                int v1 = indices[i + 1];
                int v2 = indices[i + 2];

                if (IsDegenerate(vertices, v0, v1, v2, fpEpsilon))
                    continue;

                valid.Add(v0);
                valid.Add(v1);
                valid.Add(v2);
                validAreas.Add(areas[i / 3]);
            }

            indices = valid.ToArray();
            areas = validAreas.ToArray();
        }

        /// <summary>
        /// Counts triangles the removal/compaction step would drop. Shares
        /// <see cref="IsDegenerate"/> with both of them, so the three can never disagree.
        /// </summary>
        private static int CountDegenerate(FPVector3[] vertices, ReadOnlySpan<int> indices, FP64 fpEpsilon)
        {
            int count = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                if (IsDegenerate(vertices, indices[i], indices[i + 1], indices[i + 2], fpEpsilon))
                    count++;
            }
            return count;
        }

        #endregion

        #region T-Junction splitting

        /// <summary>
        /// Detects T-Junctions and splits edges.
        /// When a vertex of one triangle lies on the edge of another,
        /// splits that triangle to restore 1-to-1 edge adjacency.
        /// </summary>
        // Test-only knob: 0 = auto (index when
        // vertex count warrants it), 1 = force index, 2 = force linear scan. The composite
        // (tParam, vertIdx) sort makes both modes output-identical.
        internal static int TJunctionIndexModeForTests = 0;
#if DEBUG
        // Query counter: per-vertex predicate tests during T-junction detection.
        internal static long TJunctionCandidateChecks;
        internal static bool LastTJunctionUsedIndex;
#endif

        private static void SplitTJunctions(
            FPVector3[] vertices, ref int[] indices, ref int[] areas,
            double epsilon, double heightEpsilon,
            IKLogger logger)
        {
            double epsSq = epsilon * epsilon;
            int maxIterations = 10;
            int initialTriCount = indices.Length / 3;
            int totalSplits = 0;

            // Vertex grid index (built once — the vertex array never changes across split
            // iterations; splits only reuse existing vertices). Null = linear-scan fallback.
            // vertices.Length is correct HERE and only here: the full path owns an exactly
            // sized array. The conforming path does not — it takes a pooled one.
            VertexGridIndex index = VertexGridIndex.TryBuild(vertices, vertices.Length);
#if DEBUG
            LastTJunctionUsedIndex = index != null;
#endif

            for (int iter = 0; iter < maxIterations; iter++)
            {
                int triCount = indices.Length / 3;

                // Safety limit on triangle count — prevent OOM
                if (triCount > initialTriCount * 20)
                {
                    logger?.KError($"[FPNavMeshBuildPipeline] T-Junction split aborted: triangle count " +
                        $"exceeded safety limit ({triCount} > {initialTriCount * 20}). " +
                        $"Possible false T-junction detection. Check mesh geometry.");
                    break;
                }

                bool anySplit = false;
                int splitCount = 0;

                var newIndices = new List<int>(indices.Length);
                var newAreas = new List<int>(areas.Length);

                for (int t = 0; t < triCount; t++)
                {
                    int i0 = indices[t * 3];
                    int i1 = indices[t * 3 + 1];
                    int i2 = indices[t * 3 + 2];

                    // Search for T-Junction vertices on each edge
                    int splitEdge = -1;
                    var midVertices = new List<(int vertIdx, double tParam)>();

                    for (int e = 0; e < 3; e++)
                    {
                        int ea, eb;
                        switch (e)
                        {
                            case 0: ea = i0; eb = i1; break;
                            case 1: ea = i1; eb = i2; break;
                            default: ea = i2; eb = i0; break;
                        }

                        midVertices.Clear();
                        FindVerticesOnEdge(vertices, vertices.Length, index,
                            ea, eb, epsSq, heightEpsilon, midVertices);

                        if (midVertices.Count > 0)
                        {
                            splitEdge = e;
                            break;
                        }
                    }

                    if (splitEdge >= 0)
                    {
                        // Composite (tParam, vertIdx) key: exact-tParam ties are real
                        // (multi-level stacked vertices within the height epsilon), and
                        // List.Sort is unstable — the index tie-break makes the result
                        // independent of candidate collection order (linear scan vs grid
                        // index visit the set in different orders).
                        midVertices.Sort((a, b) =>
                        {
                            int c = a.tParam.CompareTo(b.tParam);
                            return c != 0 ? c : a.vertIdx.CompareTo(b.vertIdx);
                        });

                        int eA, eB, eC;
                        switch (splitEdge)
                        {
                            case 0: eA = i0; eB = i1; eC = i2; break;
                            case 1: eA = i1; eB = i2; eC = i0; break;
                            default: eA = i2; eB = i0; eC = i1; break;
                        }

                        // Fan split: (eA, M0, eC), (M0, M1, eC), ..., (Mn, eB, eC)
                        int prev = eA;
                        for (int m = 0; m < midVertices.Count; m++)
                        {
                            newIndices.Add(prev);
                            newIndices.Add(midVertices[m].vertIdx);
                            newIndices.Add(eC);
                            newAreas.Add(areas[t]);
                            prev = midVertices[m].vertIdx;
                        }
                        newIndices.Add(prev);
                        newIndices.Add(eB);
                        newIndices.Add(eC);
                        newAreas.Add(areas[t]);

                        anySplit = true;
                        splitCount++;
                    }
                    else
                    {
                        newIndices.Add(i0);
                        newIndices.Add(i1);
                        newIndices.Add(i2);
                        newAreas.Add(areas[t]);
                    }
                }

                indices = newIndices.ToArray();
                areas = newAreas.ToArray();
                totalSplits += splitCount;

                logger?.KInformation($"[FPNavMeshBuildPipeline] T-Junction split: iteration {iter + 1}, " +
                    $"split {splitCount} triangles ({triCount} → {indices.Length / 3})");

                if (!anySplit) break;

                if (iter == maxIterations - 1)
                {
                    logger?.KError($"[FPNavMeshBuildPipeline] T-Junction split did not converge " +
                        $"after {maxIterations} iterations ({totalSplits} total splits, " +
                        $"{initialTriCount} → {indices.Length / 3} triangles). " +
                        $"Remaining T-Junctions may cause pathfinding disconnection.");
                }
            }
        }

        /// <summary>
        /// Finds vertices lying on edge (eA, eB). With a <see cref="VertexGridIndex"/> only
        /// the cells along the segment (plus a 1-cell dilation ring covering the epsilon band)
        /// are inspected — O(L/cell) candidates; without one it falls back to the full
        /// O(vertexCount) scan. Both paths run the identical predicate, and callers sort by
        /// the composite (tParam, vertIdx) key, so the result is mode-independent.
        /// </summary>
        private static void FindVerticesOnEdge(
            FPVector3[] vertices, int vertexCount, VertexGridIndex index,
            int eA, int eB,
            double epsSq, double heightEpsilon,
            List<(int vertIdx, double tParam)> result)
        {
            FPVector3 a3 = vertices[eA];
            FPVector3 b3 = vertices[eB];
            double ax = a3.x.ToDouble(), az = a3.z.ToDouble(), ay = a3.y.ToDouble();
            double bx = b3.x.ToDouble(), bz = b3.z.ToDouble(), by = b3.y.ToDouble();
            double abx = bx - ax, abz = bz - az, aby = by - ay;
            double abLenSq = abx * abx + abz * abz;

            if (abLenSq < epsSq) return;

            if (index == null)
            {
                // vertexCount, not vertices.Length — see VertexGridIndex.TryBuild. The linear
                // fallback is not a safe harbour: it walks the pooled array's tail just the same.
                for (int vi = 0; vi < vertexCount; vi++)
                    TestVertexOnEdge(vertices, vi, eA, eB, ax, az, ay, bx, bz, abx, abz, aby, abLenSq, epsSq, heightEpsilon, result);
            }
            else
            {
                List<int> candidates = index.CollectCandidates(eA, eB);
                for (int c = 0; c < candidates.Count; c++)
                    TestVertexOnEdge(vertices, candidates[c], eA, eB, ax, az, ay, bx, bz, abx, abz, aby, abLenSq, epsSq, heightEpsilon, result);
            }
        }

        /// <summary>The single on-edge predicate shared by the linear and indexed paths.</summary>
        private static void TestVertexOnEdge(
            FPVector3[] vertices, int vi, int eA, int eB,
            double ax, double az, double ay,
            double bx, double bz,
            double abx, double abz, double aby,
            double abLenSq, double epsSq, double heightEpsilon,
            List<(int vertIdx, double tParam)> result)
        {
            if (vi == eA || vi == eB) return;
#if DEBUG
            TJunctionCandidateChecks++;
#endif

            FPVector3 p3 = vertices[vi];
            double px = p3.x.ToDouble(), pz = p3.z.ToDouble(), py = p3.y.ToDouble();

            // point-on-segment (XZ)
            double apx = px - ax, apz = pz - az;
            double cross = apx * abz - apz * abx;
            if (cross * cross > epsSq * abLenSq) return;

            // Endpoint proximity check (actual distance based on epsilon)
            double distToASq = apx * apx + apz * apz;
            if (distToASq < epsSq) return;
            double bpx = px - bx, bpz = pz - bz;
            double distToBSq = bpx * bpx + bpz * bpz;
            if (distToBSq < epsSq) return;

            double dot = apx * abx + apz * abz;
            double tParam = dot / abLenSq;

            // Segment range validation
            if (tParam <= 0.0 || tParam >= 1.0) return;

            // Height validation
            double edgeY = ay + tParam * aby;
            if (System.Math.Abs(py - edgeY) > heightEpsilon) return;

            result.Add((vi, tParam));
        }

        /// <summary>
        /// Uniform grid hash over snapped vertex XZ for T-junction candidate reduction
        /// CSR layout (2-pass, no per-cell lists);
        /// cell = 2^shift snap units with shift >= 2 (>= 4 snap units, so the 0.002-world
        /// epsilon band — ~2.05 snap units — fits inside a 1-cell dilation ring). All traversal
        /// arithmetic is exact integer on snapped coordinates (edge endpoints are snapped after
        /// pipeline step 0; no doubles). The adaptive shift doubles the cell until the grid
        /// stays within the 4·V cell budget — this is the deterministic cell cap; tiny inputs
        /// fall back to the linear scan (index build would be pure overhead). Both fallbacks
        /// are output-safe: the composite sort makes candidate order irrelevant.
        /// </summary>
        private sealed class VertexGridIndex
        {
            private const int MIN_VERTS = 128;
            private const int MIN_SHIFT = 2;

            private readonly long[] _sx;
            private readonly long[] _sz;
            private readonly int _shift;
            private readonly long _minCX, _minCZ;
            private readonly int _width, _height;
            private readonly int[] _cellStart;   // CSR: length width*height + 1
            private readonly int[] _cellVerts;
            private readonly List<int> _scratch = new List<int>();

            /// <param name="vertexCount">
            /// Live prefix length. A rebake hands in a POOLED vertex array whose tail is either
            /// zeroes (first use) or the retire poison (-99999, every use after), and both wreck
            /// this index: the zeroes are bogus T-junction candidates, and the poison stretches
            /// the bounding box by ~1e8 snap units until every real vertex shares one cell and the
            /// acceleration degrades to a full scan per edge. Measured at ~1,600x on a 3.2k
            /// triangle mesh. Never use <c>vertices.Length</c> here.
            /// </param>
            public static VertexGridIndex TryBuild(FPVector3[] vertices, int vertexCount)
            {
                int mode = TJunctionIndexModeForTests;
                if (mode == 2)
                    return null;
                if (mode != 1 && vertexCount < MIN_VERTS)
                    return null;
                return new VertexGridIndex(vertices, vertexCount);
            }

            private VertexGridIndex(FPVector3[] vertices, int vertexCount)
            {
                int n = vertexCount;
                _sx = new long[n];
                _sz = new long[n];
                long minX = long.MaxValue, maxX = long.MinValue;
                long minZ = long.MaxValue, maxZ = long.MinValue;
                for (int i = 0; i < n; i++)
                {
                    long x = FPGeoPredicates.Snap(vertices[i].x);
                    long z = FPGeoPredicates.Snap(vertices[i].z);
                    _sx[i] = x;
                    _sz[i] = z;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }

                int shift = MIN_SHIFT;
                while (true)
                {
                    long w = (maxX >> shift) - (minX >> shift) + 1;
                    long h = (maxZ >> shift) - (minZ >> shift) + 1;
                    if (w * h <= 4L * n || shift >= 40)
                    {
                        _width = (int)w;
                        _height = (int)h;
                        break;
                    }
                    shift++;
                }
                _shift = shift;
                _minCX = minX >> shift;
                _minCZ = minZ >> shift;

                int cells = _width * _height;
                _cellStart = new int[cells + 1];
                for (int i = 0; i < n; i++)
                    _cellStart[CellOf(i) + 1]++;
                for (int c = 0; c < cells; c++)
                    _cellStart[c + 1] += _cellStart[c];
                _cellVerts = new int[n];
                var cursor = new int[cells];
                for (int i = 0; i < n; i++)
                {
                    int c = CellOf(i);
                    _cellVerts[_cellStart[c] + cursor[c]] = i;
                    cursor[c]++;
                }
            }

            private int CellOf(int vi)
            {
                int cx = (int)((_sx[vi] >> _shift) - _minCX);
                int cz = (int)((_sz[vi] >> _shift) - _minCZ);
                return cz * _width + cx;
            }

            /// <summary>
            /// Candidate vertex indices for edge (eA, eB): column walk over the segment's
            /// supercover cells + 1-cell dilation ring. Per-column z-range uses outward-floored
            /// integer interpolation (products fit in 64 bits: |coord| <= 2^27); the ring
            /// absorbs both the rounding slack and the epsilon band. Dilation columns beyond
            /// the segment's x-span clamp to the nearest endpoint's z.
            /// </summary>
            public List<int> CollectCandidates(int eA, int eB)
            {
                _scratch.Clear();
                long ax = _sx[eA], az = _sz[eA], bx = _sx[eB], bz = _sz[eB];
                if (ax > bx)
                {
                    long t = ax; ax = bx; bx = t;
                    t = az; az = bz; bz = t;
                }
                long dx = bx - ax, dz = bz - az;

                long cxLo = ClampCell((ax >> _shift) - _minCX - 1, _width);
                long cxHi = ClampCell((bx >> _shift) - _minCX + 1, _width);

                for (long cx = cxLo; cx <= cxHi; cx++)
                {
                    long zMin, zMax;
                    if (dx == 0)
                    {
                        zMin = az < bz ? az : bz;
                        zMax = az < bz ? bz : az;
                    }
                    else
                    {
                        long colX0 = (cx + _minCX) << _shift;
                        long colX1 = colX0 + (1L << _shift) - 1;
                        long x0 = colX0 > ax ? colX0 : ax;
                        long x1 = colX1 < bx ? colX1 : bx;
                        if (x0 > x1)
                        {
                            long xc = x0 > bx ? bx : ax;
                            x0 = xc;
                            x1 = xc;
                        }
                        long z0 = az + FloorDiv(dz * (x0 - ax), dx);
                        long z1 = az + FloorDiv(dz * (x1 - ax), dx);
                        zMin = z0 < z1 ? z0 : z1;
                        zMax = z0 < z1 ? z1 : z0;
                    }

                    long czLo = ClampCell((zMin >> _shift) - _minCZ - 1, _height);
                    long czHi = ClampCell((zMax >> _shift) - _minCZ + 1, _height);
                    for (long cz = czLo; cz <= czHi; cz++)
                    {
                        int c = (int)(cz * _width + cx);
                        for (int s = _cellStart[c]; s < _cellStart[c + 1]; s++)
                            _scratch.Add(_cellVerts[s]);
                    }
                }
                return _scratch;
            }

            private static long ClampCell(long v, int extent)
            {
                if (v < 0) return 0;
                if (v > extent - 1) return extent - 1;
                return v;
            }

            private static long FloorDiv(long a, long b)
            {
                long q = a / b;
                if (a % b != 0 && (a ^ b) < 0)
                    q--;
                return q;
            }
        }

        #endregion

        #region Adjacency building

        /// <summary>
        /// Edge-key comparer with mixed (Fibonacci) hashing. The default long.GetHashCode()
        /// is hi^lo, which collapses the edge key (minV&lt;&lt;32)|maxV to minV^maxV — mesh
        /// vertex indices are clustered, so that collides massively and degrades adjacency
        /// building to near-quadratic (measured: 22k tris ≈ 172ms → ~2ms with mixing).
        /// The hash choice cannot affect output: the map is only probed by exact key,
        /// never iterated.
        /// </summary>
        private sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();
            public bool Equals(long a, long b) { return a == b; }
            public int GetHashCode(long v) { return (int)((ulong)v * 0x9E3779B97F4A7C15UL >> 33); }
        }

        /// <summary>
        /// One (triangle, local edge) occurrence, sorted by (edge key, appearance order).
        /// Appearance order is <c>t * 3 + e</c>, so it carries the triangle and edge index and
        /// is unique — the sort is a total order and its stability is irrelevant
        /// (a parallel-array Array.Sort would have been
        /// unstable and would have scrambled same-key appearance order).
        /// </summary>
        internal readonly struct EdgeRecord : IComparable<EdgeRecord>
        {
            public readonly long Key;     // (minVertIdx << 32) | maxVertIdx
            public readonly int Order;    // t * 3 + e

            internal EdgeRecord(long key, int order)
            {
                Key = key;
                Order = order;
            }

            public int CompareTo(EdgeRecord other)
            {
                int c = Key.CompareTo(other.Key);
                return c != 0 ? c : Order.CompareTo(other.Order);
            }
        }

        /// <summary>
        /// Builds adjacency and portals by sorting edge occurrences and pairing consecutive
        /// same-key entries two at a time in appearance order (1-2, 3-4, …); an unpaired
        /// trailing occurrence stays a boundary edge (neighbor/portal = -1).
        ///
        /// Bit-identical to the previous Dictionary implementation
        /// (<see cref="BuildAdjacencyDictionaryReference"/>, kept for the A/B gate):
        /// (1) add→match→remove pairs occurrences in appearance order two at a time, which is
        /// exactly what the sorted grouping reproduces — including non-manifold edges (3+
        /// occurrences), where both leave the odd one out unpaired; (2) each (triangle, edge)
        /// slot belongs to exactly one pair and is written once, so the order in which pairs
        /// are processed cannot matter. On the rebake path the guarantee is stronger still:
        /// CDT output is a valid triangulation, so no edge occurs more than twice.
        ///
        /// <para>The ordering is produced by a two-pass LSD radix sort rather than a comparison
        /// sort, and the two agree ELEMENT BY ELEMENT rather than merely "sorted the same way".
        /// The comparison order is (Key, Order) with Order = t*3+e unique, so the permutation is
        /// unique; records are written at index Order, so array order IS Order ascending; a STABLE
        /// sort on Key alone therefore reproduces it exactly.
        /// <see cref="BuildAdjacencyComparisonReference"/> keeps the comparison form as the A/B
        /// side of that claim.</para>
        /// </summary>
        internal static void BuildAdjacency(
            FPNavMeshTriangle[] triangles, int triangleCount, FPVector3[] vertices,
            FPNavMeshRebakeBufferPool pool = null)
        {
            if (triangleCount <= 0)
                return;

            int n = triangleCount * 3;
            pool = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
            // Pooled: valid over [0, n) only — Length is the pooled capacity (count contract).
            EdgeRecord[] records = pool.BuildEdges(n);

            // One scan builds the records, the largest vertex index they reference (which sizes
            // the buckets — deriving it here is why no caller has to pass a vertex count), and
            // BOTH digit histograms. Counting here rather than per pass is what keeps the sort at
            // 2 reads + 2 writes per record instead of 4 + 2.
            int maxIndex = 0;
            int written = 0;
            for (int t = 0; t < triangleCount; t++)
                AppendEdges(triangles, t, records, ref written, ref maxIndex);

            RadixSortByKey(records, n, maxIndex, pool);
            PairSortedEdges(triangles, records, n, vertices);
        }

        /// <summary>
        /// Pairs consecutive same-key entries of a SORTED edge-record range two at a time in
        /// appearance order; an unpaired trailing occurrence stays a boundary edge.
        ///
        /// Shared by the full path and the incremental patch. The patch feeds it a small subset
        /// of the edges rather than all 3T, which is sound because an edge whose two occurrences
        /// are both outside that subset is one where neither side changed.
        /// </summary>
        internal static void PairSortedEdges(
            FPNavMeshTriangle[] triangles, EdgeRecord[] records, int n, FPVector3[] vertices)
        {
            int i = 0;
            while (i < n)
            {
                long key = records[i].Key;
                int end = i + 1;
                while (end < n && records[end].Key == key)
                    end++;

                for (int k = i; k + 1 < end; k += 2)
                {
                    int oa = records[k].Order;
                    int ob = records[k + 1].Order;
                    int ta = oa / 3, ea = oa - ta * 3;
                    int tb = ob / 3, eb = ob - tb * 3;

                    triangles[ta].SetNeighbor(ea, tb);
                    triangles[tb].SetNeighbor(eb, ta);

                    ComputePortalLeftRight(ref triangles[ta], ea, vertices, out int leftV, out int rightV);
                    triangles[ta].SetPortal(ea, leftV, rightV);

                    ComputePortalLeftRight(ref triangles[tb], eb, vertices, out leftV, out rightV);
                    triangles[tb].SetPortal(eb, leftV, rightV);
                }

                i = end;
            }
        }

        /// <summary>
        /// Sorts <paramref name="records"/>[0, n) ascending by Key, STABLY — ties keep their
        /// original relative order, which for this caller means Order ascending.
        ///
        /// <para>Key is (minV &lt;&lt; 32) | maxV, i.e. a two-digit number whose digits are vertex
        /// indices, so the radix is the vertex count rather than a power of two and the sort is
        /// exactly two passes: maxV (the low digit) then minV. Both digit histograms arrive
        /// already filled from the record-building scan.</para>
        ///
        /// <para><b>Stability is the whole contract, and what preserves it is that the scan
        /// direction MATCHES the counter direction.</b> Two forms are stable: an exclusive prefix
        /// sum scanned forward with <c>dst[count[digit]++]</c> (this one), and an inclusive prefix
        /// sum scanned backward with <c>dst[--count[digit]]</c>. Mixing them — forward with a
        /// decrementing counter, or backward with an incrementing one — reverses ties. That
        /// difference is invisible on any valid triangulation, because it only changes which
        /// occurrence is left unpaired when an edge appears three or more times; it was caught
        /// here by <c>FPNavMeshAdjacencyRadixTests</c> while the goldens stayed green.</para>
        /// </summary>
        internal static void RadixSortByKey(
            EdgeRecord[] records, int n, int maxIndex, FPNavMeshRebakeBufferPool pool)
        {
            int buckets = maxIndex + 1;
            EdgeRecord[] scratch = pool.BuildEdgesScratch(n);
            int[] countMax = pool.BuildEdgeCountsMax(buckets + 1);
            int[] countMin = pool.BuildEdgeCountsMin(buckets + 1);

            // Both arrays outlive a rebake and buckets shrinks as well as grows, so a stale tail
            // is live data from an earlier, larger mesh — clear the range this call uses.
            Array.Clear(countMax, 0, buckets + 1);
            Array.Clear(countMin, 0, buckets + 1);

            for (int i = 0; i < n; i++)
            {
                long key = records[i].Key;
                countMax[(int)(key & 0xFFFFFFFFL)]++;
                countMin[(int)(key >> 32)]++;
            }

            // Pass 1: low digit (maxV), records -> scratch.
            int sum = 0;
            for (int b = 0; b < buckets; b++)
            {
                int c = countMax[b];
                countMax[b] = sum;
                sum += c;
            }
            for (int i = 0; i < n; i++)
                scratch[countMax[(int)(records[i].Key & 0xFFFFFFFFL)]++] = records[i];

            // Pass 2: high digit (minV), scratch -> records. Two passes, so the result lands back
            // in the caller's array and nothing has to be copied.
            sum = 0;
            for (int b = 0; b < buckets; b++)
            {
                int c = countMin[b];
                countMin[b] = sum;
                sum += c;
            }
            for (int i = 0; i < n; i++)
                records[countMin[(int)(scratch[i].Key >> 32)]++] = scratch[i];
        }

        /// <summary>
        /// The comparison-sort form of <see cref="BuildAdjacency"/>, kept internal solely as the
        /// A/B side of the radix gate. Not called by the pipeline.
        ///
        /// This is what shipped before the radix sort and it is the tighter of the two baselines:
        /// it differs from the production path in the sort alone, so a mismatch localises there
        /// rather than anywhere in the function.
        /// </summary>
        internal static void BuildAdjacencyComparisonReference(
            FPNavMeshTriangle[] triangles, int triangleCount, FPVector3[] vertices,
            FPNavMeshRebakeBufferPool pool = null)
        {
            int n = triangleCount * 3;
            // Takes the pool for the same reason the shipped version did: without it the A/B
            // would be comparing a pooled implementation against an allocating one.
            EdgeRecord[] records = (pool ?? FPNavMeshRebakeBufferPool.NonPooling).BuildEdges(n);

            // Record building and pairing come from the SAME helpers the shipped path uses, so
            // the only thing that differs below is Array.Sort against RadixSortByKey. That is what
            // the summary claims and it has to be literally true: this function used to carry its
            // own copy of the pairing loop, which meant the gate compared one copy of the pairing
            // logic against another and would have passed happily if both were wrong. The
            // independent check on pairing is BuildAdjacencyDictionaryReference, which shares no
            // code with either sort — that is why its summary forbids deleting it.
            int maxIndex = 0;
            int written = 0;
            for (int t = 0; t < triangleCount; t++)
                AppendEdges(triangles, t, records, ref written, ref maxIndex);

            Array.Sort(records, 0, n);

            PairSortedEdges(triangles, records, n, vertices);
        }

        /// <summary>
        /// The original Dictionary-based adjacency builder, kept internal as the outermost
        /// reference of the adjacency A/B gate. Not called by the pipeline.
        ///
        /// <b>Do not delete it.</b> An earlier note here said to remove it once "the gate has
        /// shipped"; there is no such moment. The pipeline's pairing order has now been restated
        /// twice — Dictionary, then a comparison sort, then a radix sort — and each time this
        /// implementation is what pinned the answer to something independent of the machinery
        /// being replaced. It is the only baseline that shares no code with either sort.
        /// </summary>
        internal static void BuildAdjacencyDictionaryReference(
            FPNavMeshTriangle[] triangles, FPVector3[] vertices)
        {
            // (minVertIdx, maxVertIdx) → (triIdx, edgeLocalIdx)
            var edgeMap = new Dictionary<long, (int triIdx, int edgeIdx)>(
                triangles.Length * 3, EdgeKeyComparer.Instance);

            for (int t = 0; t < triangles.Length; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    triangles[t].GetEdgeVertices(e, out int va, out int vb);
                    long key = EdgeKey(va, vb);

                    if (edgeMap.TryGetValue(key, out var other))
                    {
                        // Set adjacency
                        triangles[t].SetNeighbor(e, other.triIdx);
                        triangles[other.triIdx].SetNeighbor(other.edgeIdx, t);

                        // Set portal: determine left/right via cross product from opposite vertex
                        ComputePortalLeftRight(ref triangles[t], e, vertices, out int leftV, out int rightV);
                        triangles[t].SetPortal(e, leftV, rightV);

                        ComputePortalLeftRight(ref triangles[other.triIdx], other.edgeIdx, vertices,
                            out leftV, out rightV);
                        triangles[other.triIdx].SetPortal(other.edgeIdx, leftV, rightV);

                        edgeMap.Remove(key);
                    }
                    else
                    {
                        edgeMap[key] = (t, e);
                    }
                }
            }
            // Edges remaining in edgeMap = NavMesh boundary (neighbor = -1, portal = -1 kept)
        }

        /// <summary>
        /// Determines left/right portal vertex indices for a triangle edge relative to travel direction.
        /// Approximates travel direction as opposite vertex → edge midpoint,
        /// then uses cross product sign to determine left/right.
        /// </summary>
        private static void ComputePortalLeftRight(
            ref FPNavMeshTriangle tri, int edgeIndex, FPVector3[] vertices,
            out int left, out int right)
        {
            tri.GetEdgeVertices(edgeIndex, out int va, out int vb);
            FPVector2 a = vertices[va].ToXZ();
            FPVector2 b = vertices[vb].ToXZ();

            int oppositeVert = edgeIndex == 0 ? tri.v2 : edgeIndex == 1 ? tri.v0 : tri.v1;
            FPVector2 opp = vertices[oppositeVert].ToXZ();

            FPVector2 edgeMid = (a + b) * FP64.Half;
            FPVector2 travelDir = edgeMid - opp;

            // Cross(travelDir, a - b) < 0 means a is on the left
            if (FPVector2.Cross(travelDir, a - b) < FP64.Zero)
            {
                left = va;
                right = vb;
            }
            else
            {
                left = vb;
                right = va;
            }
        }

        #endregion

        #region Bounds computation

        private static FPBounds2 ComputeBoundsXZ(FPVector3[] vertices, int vertexCount)
        {
            if (vertexCount == 0)
                return default;

            FPVector2 mn = vertices[0].ToXZ();
            FPVector2 mx = vertices[0].ToXZ();

            for (int i = 1; i < vertexCount; i++)
            {
                FPVector2 xz = vertices[i].ToXZ();
                mn = FPVector2.Min(mn, xz);
                mx = FPVector2.Max(mx, xz);
            }

            FPBounds2 bounds = default;
            bounds.SetMinMax(mn, mx);
            return bounds;
        }

        #endregion

        #region Spatial grid building

        /// <summary>
        /// The cells a triangle's XZ bounding box touches. Shared by the full grid build and the
        /// incremental splice — if the two ever computed this differently, the difference would
        /// surface only as a pathfinding query occasionally missing a triangle, which is about
        /// the hardest symptom in this file to trace back.
        /// </summary>
        private static void TriangleCellRange(
            FPVector3[] vertices, in FPNavMeshTriangle tri, FPVector2 gridOrigin, FP64 cellSize,
            int gridWidth, int gridHeight,
            out int colMin, out int colMax, out int rowMin, out int rowMax)
        {
            FPVector2 a = vertices[tri.v0].ToXZ();
            FPVector2 b = vertices[tri.v1].ToXZ();
            FPVector2 c = vertices[tri.v2].ToXZ();

            FPVector2 triMin = FPVector2.Min(FPVector2.Min(a, b), c);
            FPVector2 triMax = FPVector2.Max(FPVector2.Max(a, b), c);

            colMin = ((triMin.x - gridOrigin.x) / cellSize).ToInt();
            colMax = ((triMax.x - gridOrigin.x) / cellSize).ToInt();
            rowMin = ((triMin.y - gridOrigin.y) / cellSize).ToInt();
            rowMax = ((triMax.y - gridOrigin.y) / cellSize).ToInt();

            if (colMin < 0) colMin = 0;
            if (rowMin < 0) rowMin = 0;
            if (colMax >= gridWidth) colMax = gridWidth - 1;
            if (rowMax >= gridHeight) rowMax = gridHeight - 1;
        }

        /// <summary>
        /// The previous CSR, remapped and spliced, instead of rebuilt.
        ///
        /// <para>The full build appends triangles to each cell in ascending triangle index, and
        /// that order is part of the output — it reaches the fingerprint. The patch reproduces it
        /// without sorting: the old→new map is strictly increasing on survivors, so remapping a
        /// cell's entries preserves their order, and the added triangles are themselves visited in
        /// ascending order, which makes the splice a two-stream merge.</para>
        /// </summary>
        private static void PatchSpatialGrid(
            FPNavMesh previous, FPVector3[] vertices, FPNavMeshTriangle[] triangles, int triangleCount,
            int[] newIndexByOld, int oldTriCount, int[] added, int addedCount,
            FPBounds2 boundsXZ, FP64 cellSize, int gridWidth, int gridHeight,
            FPNavMeshRebakeBufferPool pool,
            out int[] gridCells, out int[] gridTriangles, out int gridTriangleCount)
        {
            int totalCells = gridWidth * gridHeight;
            ReadOnlySpan<int> oldCells = previous.GridCells;
            ReadOnlySpan<int> oldTriangles = previous.GridTriangles;

            // (cell, new triangle index) for every cell an added triangle touches, packed into
            // one long each and sorted. Sorted once here so the per-cell merge below is a cursor
            // walk rather than a rescan — the naive form costs cells x added x cellsPerAdded, and
            // on a 2.5k-cell grid that dominates the whole patch.
            long[] addedCellPairs = pool.PatchAddedCellPairs(addedCount == 0 ? 1 : addedCount * 16);
            int acCount = 0;
            for (int a = 0; a < addedCount; a++)
            {
                TriangleCellRange(vertices, triangles[added[a]], boundsXZ.min, cellSize,
                    gridWidth, gridHeight, out int colMin, out int colMax, out int rowMin, out int rowMax);
                int need = acCount + (rowMax - rowMin + 1) * (colMax - colMin + 1);
                if (need > addedCellPairs.Length)
                {
                    // Preserving the prefix is the CALLER's job, and it has to be: the pool's
                    // non-pooling mode retains nothing between rents, so it hands back a fresh
                    // array here and has no source to copy from. Only this loop knows how much of
                    // the buffer is live (acCount). Without the copy the entries written for the
                    // triangles already processed are silently replaced by zeroes, which sort to
                    // the front and rewrite the grid CSR against cell 0 / triangle 0 — a corruption
                    // ComputeFingerprint cannot see, because it hashes vertices and triangles only.
                    long[] grown = pool.PatchAddedCellPairs(need * 2);
                    if (!ReferenceEquals(grown, addedCellPairs))
                        Array.Copy(addedCellPairs, grown, acCount);
                    addedCellPairs = grown;
                }
                for (int r = rowMin; r <= rowMax; r++)
                    for (int col = colMin; col <= colMax; col++)
                        addedCellPairs[acCount++] = ((long)(r * gridWidth + col) << 32) | (uint)added[a];
            }
            Array.Sort(addedCellPairs, 0, acCount);
            int pairCursor = 0;

            // Size the output: survivors keep their entries, added ones bring theirs.
            int removedEntries = 0;
            for (int i = 0; i < oldTriangles.Length; i++)
            {
                if (newIndexByOld[oldTriangles[i]] < 0)
                    removedEntries++;
            }
            gridTriangleCount = oldTriangles.Length - removedEntries + acCount;

            gridCells = pool.RentOutputGridCells(totalCells * 2);
            gridTriangles = pool.RentOutputGridTriangles(gridTriangleCount == 0 ? 1 : gridTriangleCount);

            int offset = 0;
            for (int c = 0; c < totalCells; c++)
            {
                int oldStart = oldCells[c * 2];
                int oldCount = oldCells[c * 2 + 1];
                int written = 0;
                int cellStart = offset;

                // Two ascending streams: surviving old entries (remapped) and added triangles
                // that touch this cell. Merged by new index, which is what the full build's
                // ascending-t append produces.
                for (int k = 0; k < oldCount; k++)
                {
                    int mapped = newIndexByOld[oldTriangles[oldStart + k]];
                    if (mapped < 0)
                        continue;
                    while (pairCursor < acCount && (int)(addedCellPairs[pairCursor] >> 32) == c
                        && (int)addedCellPairs[pairCursor] < mapped)
                    {
                        gridTriangles[offset++] = (int)addedCellPairs[pairCursor++];
                        written++;
                    }
                    gridTriangles[offset++] = mapped;
                    written++;
                }
                while (pairCursor < acCount && (int)(addedCellPairs[pairCursor] >> 32) == c)
                {
                    gridTriangles[offset++] = (int)addedCellPairs[pairCursor++];
                    written++;
                }

                gridCells[c * 2] = cellStart;
                gridCells[c * 2 + 1] = written;
            }
            gridTriangleCount = offset;
        }



        private static void BuildSpatialGrid(
            FPVector3[] vertices,
            FPNavMeshTriangle[] triangles,
            int triangleCount,
            FPBounds2 boundsXZ,
            FP64 cellSize,
            FPNavMeshRebakeBufferPool pool,
            out int gridWidth, out int gridHeight, out FPVector2 gridOrigin,
            out int[] gridCells, out int[] gridTriangles, out int gridTriangleCount)
        {
            gridOrigin = boundsXZ.min;
            FPVector2 size = boundsXZ.size;

            gridWidth = FP64.Ceiling(size.x / cellSize).ToInt() + 1;
            gridHeight = FP64.Ceiling(size.y / cellSize).ToInt() + 1;

            int totalCells = gridWidth * gridHeight;

            // Pass 1: collect list of triangles per cell (pooled: the List instances are the
            // reused storage, cleared not poisoned — Count is authoritative).
            List<int>[] cellLists = pool.GridCellLists(totalCells);

            for (int t = 0; t < triangleCount; t++)
            {
                TriangleCellRange(vertices, triangles[t], gridOrigin, cellSize, gridWidth, gridHeight,
                    out int colMin, out int colMax, out int rowMin, out int rowMax);

                for (int r = rowMin; r <= rowMax; r++)
                {
                    for (int col = colMin; col <= colMax; col++)
                    {
                        int cellIdx = r * gridWidth + col;
                        cellLists[cellIdx].Add(t);
                    }
                }
            }

            // Pass 2: compress to flat arrays
            gridCells = pool.RentOutputGridCells(totalCells * 2);
            int totalTris = 0;
            for (int i = 0; i < totalCells; i++)
                totalTris += cellLists[i].Count;

            gridTriangles = pool.RentOutputGridTriangles(totalTris);
            gridTriangleCount = totalTris;
            int offset = 0;

            for (int i = 0; i < totalCells; i++)
            {
                gridCells[i * 2] = offset;
                gridCells[i * 2 + 1] = cellLists[i].Count;

                for (int j = 0; j < cellLists[i].Count; j++)
                    gridTriangles[offset + j] = cellLists[i][j];

                offset += cellLists[i].Count;
            }
        }

        #endregion
    }
}
