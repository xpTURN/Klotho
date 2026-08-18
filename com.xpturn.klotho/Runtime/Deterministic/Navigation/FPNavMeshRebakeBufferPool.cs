using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Reusable work buffers for the rebake hot path.
    /// A full rebake at 22k triangles allocates ~10MB of throwaway arrays, most of them going
    /// straight to the large object heap; buildings are a discrete event so this never touches
    /// the per-tick budget, but at high build frequency or on large maps it is Gen2/LOH
    /// pressure on the server and a frame hitch on the client. This pool keeps one buffer per
    /// role alive and hands the same storage back on every rebake.
    ///
    /// Ownership: one pool per room / game seam. Safe without locking because rebakes run in
    /// the command phase, which is serial within a room — a worker-threaded rebake would move
    /// pool ownership to the worker (the API does not change). Never share one pool between
    /// rooms.
    ///
    /// Determinism: the pool only reuses capacity — every buffer is fully overwritten before
    /// it is read, so output is bit-identical with or without a pool (asserted by the A/B
    /// gate). Contrast with <see cref="FPNavMeshRebakeSnapshot"/>, which is immutable and
    /// shared; a pool is mutable and room-local, so the two are never held together.
    ///
    /// Contract for callers: a rented buffer is only valid over <c>[0, count)</c> where count
    /// is what the caller just wrote — <see cref="Array.Length"/> is the pooled capacity and
    /// says nothing about the logical size. In DEBUG builds every rent poisons the buffer so
    /// that reading stale data diverges immediately instead of silently returning last
    /// rebake's values. Two rents state their own exception in place and say why —
    /// <see cref="ExtractKeep"/> clears instead (the tail has to read "do not keep") and
    /// <see cref="PatchAddedCellPairs"/> preserves instead (a fill would wipe the prefix its
    /// grow just copied forward).
    ///
    /// Lifetime: hold it for as long as the room lives and drop it with the room — the
    /// buffers stay resident at peak size (that is the point), so a leaked pool is a leaked
    /// multi-megabyte working set.
    /// </summary>
    public sealed class FPNavMeshRebakeBufferPool
    {
        private const int POISON_INT = unchecked((int)0xCDCDCDCD);
        private const long POISON_LONG = unchecked((long)0xCDCDCDCDCDCDCDCDUL);

        /// <summary>
        /// Shared non-pooling instance: every rent allocates a fresh exact-size buffer, which
        /// is precisely the pre-pool behaviour. Callers resolve a null pool to this once at
        /// the entry point, so no code below has to branch on null.
        /// </summary>
        internal static readonly FPNavMeshRebakeBufferPool NonPooling = new FPNavMeshRebakeBufferPool(false);

        private readonly bool _reuse;

        // DEBUG overlap guard. Zero when idle; a second entrant means two consumers are holding
        // the same one-per-role buffers at once.
        private int _useDepth;

        /// <summary>
        /// Marks the pool as in use for the duration of one rebake (DEBUG only).
        ///
        /// <para>Work buffers have no return call — each role is a single slot that the next rent
        /// hands straight back — so two overlapping consumers do not fail, they overwrite. Worse,
        /// they overwrite PARTIALLY: <c>Array.Resize</c> inside the CDT replaces a grown array
        /// without writing it back to the slot, so some fields survive and some do not. The result
        /// is a wrong mesh, and <c>ComputeFingerprint</c> does not cover adjacency, portals or the
        /// grid, so it can pass. This guard is what turns that into a loud failure.</para>
        ///
        /// <para>A second consumer is legitimate — the placement preview is one — but it must
        /// bring its own pool. The shared <see cref="NonPooling"/> instance is exempt because
        /// every rent there allocates fresh storage, which is precisely what makes sharing safe.</para>
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        internal void EnterUse()
        {
            if (!_reuse)
                return;
            if (System.Threading.Interlocked.Increment(ref _useDepth) != 1)
            {
                System.Threading.Interlocked.Decrement(ref _useDepth);
                throw new InvalidOperationException(
                    "FPNavMeshRebakeBufferPool: overlapping use. Work buffers are one slot per role " +
                    "with no return call, so the second consumer overwrites the first (partially — " +
                    "the CDT's Array.Resize does not write back to the slot). Give it its own pool, " +
                    "the way the placement preview does.");
            }
        }

        /// <summary><see cref="EnterUse"/>'s counterpart; call it from a finally block.</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        internal void ExitUse()
        {
            if (_reuse)
                System.Threading.Interlocked.Decrement(ref _useDepth);
        }

        /// <summary>
        /// Arrays this pool has allocated (as opposed to handed back from a slot). This is the
        /// counter gate for zero-allocation rebakes: after warmup, a rebake that repeats at the same
        /// size must allocate NOTHING here, and unlike a byte measurement the count is immune to
        /// the DEBUG-only diagnostic allocations (VerifyConformingContract's HashSets, the CDT
        /// SelfCheck) that make byte gates meaningless outside Release.
        ///
        /// Scope, so the gate is not mistaken for more than it is: this counts what the POOL
        /// allocates. A raw `new int[...]` written somewhere in the rebake path is invisible
        /// here — that is what the byte gate is for. The two watch different things and
        /// neither replaces the other.
        ///
        /// Growth is not a regression: the slots start empty and buildings accumulate within a
        /// match, so the first rebakes and every size-growth rebake legitimately allocate. The
        /// gate asserts zero only for repeated same-size rebakes after warmup.
        /// </summary>
        internal int AllocatedArrayCount => _allocatedArrayCount;

        internal void ResetAllocatedArrayCount() => _allocatedArrayCount = 0;

        private int _allocatedArrayCount;

        // CDT resume clone.
        private long[] _cdtXs;
        private long[] _cdtZs;
        private int[] _cdtWeld;
        private int[] _cdtVertexTri;
        private FPConstrainedDelaunay.Cdt.Tri[] _cdtTris;

        // CDT extraction.
        private bool[] _extractKeep;
        private int[] _extractDepth;
        private int[] _extractDeque;
        private FPConstrainedDelaunay.Cdt.Triple[] _extractTriples;
        private FPConstrainedDelaunay.Cdt.Triple[] _extractTriplesScratch;
        private int[] _extractTripleCounts;
        private int[] _extractOutput;

        // Rebake hole assembly. Arrays rather than List<T> on purpose:
        // a reused List still allocates on ToArray() at the CDT boundary, so the count travels
        // explicitly instead and no conversion happens at all.
        private long[] _polyXs;
        private long[] _polyZs;
        private int[] _polyStarts;
        private long[] _polyBounds;
        private FP64[] _polyYs;
        private long[] _holeXs;
        private long[] _holeZs;
        private FP64[] _holeYs;
        private int[] _holeConstraints;

        // Build pipeline.
        private FPNavMeshBuildPipeline.EdgeRecord[] _buildEdges;
        private FPNavMeshBuildPipeline.EdgeRecord[] _buildEdgesScratch;
        private int[] _buildEdgeCountsMax;
        private int[] _buildEdgeCountsMin;
        private List<int>[] _gridCellLists;

        // Incremental build-stage patch.
        private int[] _patchNewIndexByOld;
        private int[] _patchAdded;
        private int[] _patchIndices;
        private long[] _patchAddedCellPairs;
        private FPNavMeshBuildPipeline.EdgeRecord[] _patchBoundaryEdges;
        private FPNavMeshBuildPipeline.EdgeRecord[] _patchEdges;

        // Retired output arrays. Unlike the work buffers above these
        // are the mesh's OWN storage, so they may only be reused once the mesh that held them is
        // no longer live. The pool therefore holds exactly one RETIRED generation: arrays land
        // here at swap-commit time (FPNavMeshRebakeContext.CommitSwap) and are handed out to the
        // rebake AFTER the next one. That one-generation lag is what keeps a live mesh from ever
        // being written through.
        private FPVector3[] _retiredVertices;
        private FPNavMeshTriangle[] _retiredTriangles;
        private int[] _retiredGridCells;
        private int[] _retiredGridTriangles;

        public FPNavMeshRebakeBufferPool() : this(true)
        {
        }

        private FPNavMeshRebakeBufferPool(bool reuse)
        {
            _reuse = reuse;
        }

        #region Rent

        internal long[] CdtXs(int minSize) { return RentLongs(ref _cdtXs, minSize); }
        internal long[] CdtZs(int minSize) { return RentLongs(ref _cdtZs, minSize); }
        internal int[] CdtWeld(int minSize) { return RentInts(ref _cdtWeld, minSize); }
        internal int[] CdtVertexTri(int minSize) { return RentInts(ref _cdtVertexTri, minSize); }

        internal FPConstrainedDelaunay.Cdt.Tri[] CdtTris(int minSize)
        {
            if (!_reuse)
                return Counted(new FPConstrainedDelaunay.Cdt.Tri[minSize]);
            if (_cdtTris == null || _cdtTris.Length < minSize)
                _cdtTris = Counted(new FPConstrainedDelaunay.Cdt.Tri[Grow(_cdtTris == null ? 0 : _cdtTris.Length, minSize)]);
#if DEBUG
            var poison = new FPConstrainedDelaunay.Cdt.Tri
            {
                V0 = POISON_INT, V1 = POISON_INT, V2 = POISON_INT,
                N0 = POISON_INT, N1 = POISON_INT, N2 = POISON_INT,
                E = 0x3F, Alive = true,   // every edge mark bit set: constrained AND parity x3
            };
            Array.Fill(_cdtTris, poison);
#endif
            return _cdtTris;
        }

        internal bool[] ExtractKeep(int minSize)
        {
            if (!_reuse)
                return Counted(new bool[minSize]);
            if (_extractKeep == null || _extractKeep.Length < minSize)
                _extractKeep = Counted(new bool[Grow(_extractKeep == null ? 0 : _extractKeep.Length, minSize)]);
            // Not poison but the contract itself: Extract writes keep[i] only for live
            // triangles, so the tail must read as "do not keep".
            Array.Clear(_extractKeep, 0, _extractKeep.Length);
            return _extractKeep;
        }

        internal int[] ExtractDepth(int minSize) { return RentInts(ref _extractDepth, minSize); }

        /// <summary>
        /// 0-1 BFS deque storage. Always a power of two in both modes — the deque masks
        /// indices with (length - 1), so an exact-size buffer would corrupt it.
        /// </summary>
        internal int[] ExtractDeque(int minSize)
        {
            int cap = 16;
            while (cap < minSize)
                cap *= 2;
            return RentInts(ref _extractDeque, cap);
        }

        internal FPConstrainedDelaunay.Cdt.Triple[] ExtractTriples(int minSize)
        {
            if (!_reuse)
                return Counted(new FPConstrainedDelaunay.Cdt.Triple[minSize]);
            if (_extractTriples == null || _extractTriples.Length < minSize)
                _extractTriples = Counted(new FPConstrainedDelaunay.Cdt.Triple[
                    Grow(_extractTriples == null ? 0 : _extractTriples.Length, minSize)]);
#if DEBUG
            Array.Fill(_extractTriples, new FPConstrainedDelaunay.Cdt.Triple(POISON_INT, POISON_INT, POISON_INT));
#endif
            return _extractTriples;
        }

        /// <summary>
        /// Ping-pong partner of <see cref="ExtractTriples"/> for the radix sort. Three passes is
        /// odd, so the sorted result ends up in whichever of the two the last pass wrote — the
        /// sort returns that array rather than copying back.
        /// </summary>
        internal FPConstrainedDelaunay.Cdt.Triple[] ExtractTriplesScratch(int minSize)
        {
            if (!_reuse)
                return Counted(new FPConstrainedDelaunay.Cdt.Triple[minSize]);
            if (_extractTriplesScratch == null || _extractTriplesScratch.Length < minSize)
                _extractTriplesScratch = Counted(new FPConstrainedDelaunay.Cdt.Triple[
                    Grow(_extractTriplesScratch == null ? 0 : _extractTriplesScratch.Length, minSize)]);
#if DEBUG
            Array.Fill(_extractTriplesScratch, new FPConstrainedDelaunay.Cdt.Triple(POISON_INT, POISON_INT, POISON_INT));
#endif
            return _extractTriplesScratch;
        }

        /// <summary>
        /// Digit histograms for the triple radix sort — one array holding three contiguous
        /// bucket ranges (A, B, C). The caller clears the range it uses: this outlives a rebake
        /// and the bucket count shrinks as well as grows, so a stale tail is live data from an
        /// earlier, larger mesh.
        /// </summary>
        internal int[] ExtractTripleCounts(int minSize) { return RentInts(ref _extractTripleCounts, minSize); }

        /// <summary>
        /// Storage for <see cref="FPConstrainedDelaunay.Cdt.Extract"/>'s result. This one leaves
        /// the CDT as a return value, so unlike the other work buffers it carries a lifetime the
        /// caller can violate: it is valid until the next rebake on this pool and must not be
        /// stored. The count travels with it.
        /// </summary>
        internal int[] ExtractOutput(int minSize) { return RentInts(ref _extractOutput, minSize); }

        // The placement preview appends its ghost to the caller's list, and doing that in place
        // would make the game's own array grow by one every frame. These hold the joined copy.
        // Pooled for the same reason the hole arrays are — a per-preview `new` would show up in
        // the byte gate (FPBuildingPreviewTests.V3_ReusedScratch_AllocatesNothing), and the
        // counter gate sees it through Counted like every other buffer here.
        private FPBuildingRect[] _previewRects;
        private FPBuildingPlacement[] _previewPlacements;

        internal FPBuildingRect[] PreviewRects(int minSize)
        {
            if (!_reuse)
                return Counted(new FPBuildingRect[minSize]);
            if (_previewRects == null || _previewRects.Length < minSize)
                _previewRects = Counted(new FPBuildingRect[
                    Grow(_previewRects == null ? 0 : _previewRects.Length, minSize)]);
#if DEBUG
            FP64 poison = FP64.FromRaw(POISON_LONG);
            Array.Fill(_previewRects, new FPBuildingRect(poison, poison, poison, poison, poison));
#endif
            return _previewRects;
        }

        internal FPBuildingPlacement[] PreviewPlacements(int minSize)
        {
            if (!_reuse)
                return Counted(new FPBuildingPlacement[minSize]);
            if (_previewPlacements == null || _previewPlacements.Length < minSize)
                _previewPlacements = Counted(new FPBuildingPlacement[
                    Grow(_previewPlacements == null ? 0 : _previewPlacements.Length, minSize)]);
#if DEBUG
            // ShapeId is poisoned too, not just the coordinates: a stale entry that reaches
            // BuildPlacementPolygons then throws at ResolveEntryOrThrow instead of quietly
            // resolving to whatever shape the previous preview had there.
            FP64 poison = FP64.FromRaw(POISON_LONG);
            Array.Fill(_previewPlacements, new FPBuildingPlacement(
                POISON_INT, POISON_INT, poison, poison, poison));
#endif
            return _previewPlacements;
        }

        internal long[] PolyXs(int minSize) { return RentLongs(ref _polyXs, minSize); }
        internal long[] PolyZs(int minSize) { return RentLongs(ref _polyZs, minSize); }
        internal int[] PolyStarts(int minSize) { return RentInts(ref _polyStarts, minSize); }
        /// <summary>Per-building AABB of the expanded polygon, packed as 4 longs each.</summary>
        internal long[] PolyBounds(int minSize) { return RentLongs(ref _polyBounds, minSize); }
        /// <summary>Per-building hole-vertex height, one entry per building.</summary>
        internal FP64[] PolyYs(int minSize) { return RentFP64s(ref _polyYs, minSize); }

        internal long[] HoleXs(int minSize) { return RentLongs(ref _holeXs, minSize); }
        internal long[] HoleZs(int minSize) { return RentLongs(ref _holeZs, minSize); }
        internal int[] HoleConstraints(int minSize) { return RentInts(ref _holeConstraints, minSize); }

        internal FP64[] HoleYs(int minSize) { return RentFP64s(ref _holeYs, minSize); }

        internal FPNavMeshBuildPipeline.EdgeRecord[] BuildEdges(int minSize) { return RentEdgeRecords(ref _buildEdges, minSize); }

        /// <summary>
        /// The other half of the adjacency radix sort's double buffer. Two passes, so src and dst
        /// swap twice and the result lands back in <see cref="BuildEdges"/>'s array — no copy.
        ///
        /// Oversized like every pooled buffer: the sort reads and writes [0, n) of BOTH arrays and
        /// nothing else. That is the same contract the comparison sort stated as
        /// <c>Array.Sort(records, 0, n)</c>.
        /// </summary>
        internal FPNavMeshBuildPipeline.EdgeRecord[] BuildEdgesScratch(int minSize) { return RentEdgeRecords(ref _buildEdgesScratch, minSize); }

        /// <summary>
        /// Digit histograms for the adjacency radix sort — one per pass, both filled while the
        /// edge records are built.
        ///
        /// The caller clears [0, B+1) before use and must not assume anything about the rest: this
        /// array is reused across rebakes and B shrinks as well as grows, so a stale tail is live
        /// data from an earlier, larger mesh.
        /// </summary>
        internal int[] BuildEdgeCountsMax(int minSize) { return RentInts(ref _buildEdgeCountsMax, minSize); }

        /// <inheritdoc cref="BuildEdgeCountsMax"/>
        internal int[] BuildEdgeCountsMin(int minSize) { return RentInts(ref _buildEdgeCountsMin, minSize); }

        /// <summary>
        /// old triangle index -> new index, or -1 for a triangle the rebake removed. Written in
        /// full by the diff before it is read, so the stale tail is never live.
        /// </summary>
        internal int[] PatchNewIndexByOld(int minSize) { return RentInts(ref _patchNewIndexByOld, minSize); }

        /// <summary>New indices of the triangles this rebake added, ascending.</summary>
        internal int[] PatchAdded(int minSize) { return RentInts(ref _patchAdded, minSize); }

        /// <summary>
        /// The triangulation the patch is building against, copied because the patch now spans
        /// frames and a <c>ReadOnlySpan</c> cannot be a field.
        ///
        /// <para>Pooled rather than <c>ToArray()</c>d, which is not a micro-optimisation: the
        /// first version of the resumable patch allocated the copy and the steady-state allocation
        /// gate went from 2,280 B to 270,696 B per rebake. Renting makes the copy a memcpy into
        /// storage the room already owns.</para>
        /// </summary>
        internal int[] PatchIndices(int minSize) { return RentInts(ref _patchIndices, minSize); }

        /// <summary>
        /// (grid cell, new triangle index) pairs for the added triangles, one packed long each.
        /// Grows by content rather than by a count known up front — a triangle's cell footprint
        /// depends on its extent — so the caller may ask for a bigger array mid-loop.
        /// <para><b>This does NOT guarantee the prefix survives that mid-loop re-rent, and cannot:</b>
        /// non-pooling mode retains nothing between rents, so it returns a fresh zeroed array with
        /// no source to copy from. The pooled branch below happens to carry the old contents over,
        /// but only the caller knows how much of the buffer is live, so <b>the caller must copy its
        /// own prefix</b> and must not rely on either branch. See the re-rent site in
        /// <see cref="FPNavMeshBuildPipeline"/>'s grid patch.</para>
        /// <para><b>The one rent that is NOT poisoned in DEBUG</b>, and the only place the class
        /// note's "every rent poisons" does not hold. It cannot: the pooled branch grows by
        /// copying the old contents forward, and a poison fill after that copy would destroy
        /// exactly the prefix the copy is there to keep. The two contracts are in direct conflict,
        /// so this one states which of them wins rather than quietly dropping the fill.</para>
        /// </summary>
        internal long[] PatchAddedCellPairs(int minSize)
        {
            if (!_reuse)
                return Counted(new long[minSize]);
            if (_patchAddedCellPairs == null || _patchAddedCellPairs.Length < minSize)
            {
                var grown = Counted(new long[Grow(_patchAddedCellPairs == null ? 0 : _patchAddedCellPairs.Length, minSize)]);
                if (_patchAddedCellPairs != null)
                    Array.Copy(_patchAddedCellPairs, grown, _patchAddedCellPairs.Length);
                _patchAddedCellPairs = grown;
            }
            return _patchAddedCellPairs;
        }

        /// <summary>Boundary-edge records for the duplicate-boundary scan.</summary>
        internal FPNavMeshBuildPipeline.EdgeRecord[] PatchBoundaryEdges(int minSize) { return RentEdgeRecords(ref _patchBoundaryEdges, minSize); }

        /// <summary>Edge records for the subset of edges the patch re-pairs.</summary>
        internal FPNavMeshBuildPipeline.EdgeRecord[] PatchEdges(int minSize) { return RentEdgeRecords(ref _patchEdges, minSize); }

        /// <summary>
        /// Per-cell triangle lists for the spatial grid. Cleared, not poisoned: the List
        /// instances themselves are the reused storage and Count is authoritative, so there is
        /// no stale tail to misread. The array is re-created when the cell count changes,
        /// which for a fixed stage it does not (buildings never move the base bounds).
        /// </summary>
        internal List<int>[] GridCellLists(int cellCount)
        {
            if (!_reuse)
            {
                var fresh = Counted(new List<int>[cellCount]);
                for (int i = 0; i < cellCount; i++)
                    fresh[i] = new List<int>();
                return fresh;
            }

            if (_gridCellLists == null || _gridCellLists.Length != cellCount)
            {
                _gridCellLists = Counted(new List<int>[cellCount]);
                for (int i = 0; i < cellCount; i++)
                    _gridCellLists[i] = new List<int>();
                return _gridCellLists;
            }

            for (int i = 0; i < cellCount; i++)
                _gridCellLists[i].Clear();
            return _gridCellLists;
        }

        #endregion

        #region Output arrays (v2)

        /// <summary>
        /// Takes the retired array if it is large enough, otherwise allocates with the same
        /// doubling policy as the work buffers (so a growing mesh amortises to O(1) allocations
        /// per size doubling instead of one per rebake). The caller writes <c>[0, count)</c> and
        /// tells <see cref="FPNavMesh"/> the count — everything past it is the previous mesh's
        /// data and is invisible through the mesh's spans.
        /// </summary>
        internal FPVector3[] RentOutputVertices(int minSize) => TakeRetired(ref _retiredVertices, minSize);
        internal FPNavMeshTriangle[] RentOutputTriangles(int minSize) => TakeRetired(ref _retiredTriangles, minSize);
        internal int[] RentOutputGridCells(int minSize) => TakeRetired(ref _retiredGridCells, minSize);
        internal int[] RentOutputGridTriangles(int minSize) => TakeRetired(ref _retiredGridTriangles, minSize);

        /// <summary>
        /// Hands a retired mesh's storage back. MUST only be called for a mesh this pool's context
        /// produced AND that is no longer installed — see
        /// <see cref="FPNavMeshRebakeContext.CommitSwap"/>, which is the only caller and enforces
        /// both conditions. In DEBUG the arrays are poisoned on retire, so any holder still
        /// reading the old mesh produces visibly wrong output instead of silently plausible
        /// output — the failure class that is undetectable in production.
        /// </summary>
        internal void RetireOutput(FPNavMesh mesh)
        {
            if (!_reuse || mesh == null)
                return;

            mesh.GetBackingArrays(out var vertices, out var triangles, out var gridCells, out var gridTriangles);
            // Mark before poisoning: from here on, any read of this mesh is a bug, and the flag
            // says so even after the arrays have been overwritten by their next owner.
            mesh.MarkRetired();
#if DEBUG
            Array.Fill(vertices, new FPVector3(FP64.FromInt(-99999), FP64.FromInt(-99999), FP64.FromInt(-99999)));
            Array.Fill(triangles, new FPNavMeshTriangle
            {
                v0 = 0, v1 = 0, v2 = 0,
                neighbor0 = POISON_INT, neighbor1 = POISON_INT, neighbor2 = POISON_INT,
                areaMask = POISON_INT,
                // Set explicitly rather than left at 0: POISON_INT is negative, so GetPortal
                // already reports (-1, -1) for every edge of a poisoned triangle and a stale
                // reader gets a visibly portal-less mesh. That is the outcome we want, but it
                // falls out of the neighbor poison by accident — pinning the flag says so.
                portalFlip = 0x07,
            });
            Array.Fill(gridCells, POISON_INT);
            Array.Fill(gridTriangles, POISON_INT);
#endif
            _retiredVertices = vertices;
            _retiredTriangles = triangles;
            _retiredGridCells = gridCells;
            _retiredGridTriangles = gridTriangles;
        }

        private T[] TakeRetired<T>(ref T[] retired, int minSize)
        {
            if (_reuse && retired != null && retired.Length >= minSize)
            {
                T[] taken = retired;
                retired = null;   // handed out — never lend the same storage twice
                return taken;
            }
            return Counted(new T[_reuse ? OutputCapacity(minSize) : minSize]);
        }

        /// <summary>
        /// Headroom for a freshly allocated output array. Deliberately NOT the doubling policy the
        /// work buffers use: a rebake output grows by a handful of triangles per building, so
        /// rounding up to the next power of two wasted ~47% of the largest array (22,321 → 32,768
        /// at 22k tris) to buy growth room that would take a thousand more buildings to consume.
        /// 25% covers a long match's growth at a quarter of the resident cost — and the cost is
        /// resident, paid once, while the saving is per rebake.
        /// </summary>
        private static int OutputCapacity(int minSize) => minSize + (minSize >> 2) + 16;

        #endregion

        #region Internals

        private long[] RentLongs(ref long[] slot, int minSize)
        {
            if (!_reuse)
                return Counted(new long[minSize]);
            if (slot == null || slot.Length < minSize)
                slot = Counted(new long[Grow(slot == null ? 0 : slot.Length, minSize)]);
#if DEBUG
            Array.Fill(slot, POISON_LONG);
#endif
            return slot;
        }

        private int[] RentInts(ref int[] slot, int minSize)
        {
            if (!_reuse)
                return Counted(new int[minSize]);
            if (slot == null || slot.Length < minSize)
                slot = Counted(new int[Grow(slot == null ? 0 : slot.Length, minSize)]);
#if DEBUG
            Array.Fill(slot, POISON_INT);
#endif
            return slot;
        }

        // The FP64 and EdgeRecord equivalents of the two above. They exist for the same reason:
        // every buffer that goes through a shared rent gets the DEBUG poison automatically, and
        // the ones that did NOT go through one are exactly the ones that lost it. PolyYs,
        // PatchEdges and PatchBoundaryEdges were each written out by hand and each forgot the
        // fill, while their siblings — PolyXs/PolyZs/PolyStarts/PolyBounds through RentLongs and
        // RentInts, HoleYs and BuildEdges by hand but remembering — kept it. Two more helpers is
        // what stops the next hand-written rent from being the next omission.
        private FP64[] RentFP64s(ref FP64[] slot, int minSize)
        {
            if (!_reuse)
                return Counted(new FP64[minSize]);
            if (slot == null || slot.Length < minSize)
                slot = Counted(new FP64[Grow(slot == null ? 0 : slot.Length, minSize)]);
#if DEBUG
            Array.Fill(slot, FP64.FromRaw(POISON_LONG));
#endif
            return slot;
        }

        private FPNavMeshBuildPipeline.EdgeRecord[] RentEdgeRecords(ref FPNavMeshBuildPipeline.EdgeRecord[] slot, int minSize)
        {
            if (!_reuse)
                return Counted(new FPNavMeshBuildPipeline.EdgeRecord[minSize]);
            if (slot == null || slot.Length < minSize)
                slot = Counted(new FPNavMeshBuildPipeline.EdgeRecord[Grow(slot == null ? 0 : slot.Length, minSize)]);
#if DEBUG
            Array.Fill(slot, new FPNavMeshBuildPipeline.EdgeRecord(POISON_LONG, POISON_INT));
#endif
            return slot;
        }

        /// <summary>
        /// Single funnel for every array this pool creates, so <see cref="AllocatedArrayCount"/>
        /// cannot drift from reality by someone adding a slot and forgetting the counter — the
        /// only way to produce an array here is through this method.
        /// </summary>
        private T[] Counted<T>(T[] array)
        {
            _allocatedArrayCount++;
            return array;
        }

        /// <summary>
        /// Doubling growth that never shrinks: building count only goes up within a match, so
        /// the first few rebakes may grow and then the pool is stable (which is what the
        /// "successive rebakes allocate ≈nothing" gate measures).
        /// </summary>
        private static int Grow(int current, int minSize)
        {
            int next = current < 16 ? 16 : current;
            while (next < minSize)
                next *= 2;
            return next;
        }

        #endregion
    }
}
