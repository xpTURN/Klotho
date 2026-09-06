using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Deterministic batch constrained Delaunay triangulation on the snapped predicate grid.
    ///
    /// Written from scratch against textbook algorithms (Lawson incremental insertion +
    /// pseudo-polygon constraint re-triangulation). The obvious assumption on reading that
    /// description is that it was ported from the well-known MPL-2.0 CDT library implementing the
    /// same pair; it was not, and no MPL-licensed source was read while writing it. That is what
    /// keeps this file under the package's own licence — MPL-2.0 is file-level copyleft, so a
    /// derived file would have to carry it and be isolated from the rest of the package.
    ///
    /// Numeric layer: FPGeoPredicates exact integer orient2d/inCircle only — float-free,
    /// division-free, no Steiner points (constraints connect existing vertices; a vertex lying
    /// exactly on a constraint splits it). Cross-runtime bit-identical output by construction.
    ///
    /// Determinism contract:
    ///  - Insertion order: vertices sorted by (x, z) ascending (input order irrelevant).
    ///    Snapshot-resume path (runtime rebake): base sorted -> base constraints
    ///    [= snapshot boundary] -> hole vertices sorted -> hole constraints — the only
    ///    spec delta vs the one-shot path is the vertex insertion order; outputs may be
    ///    different (valid) CDTs, which is official per spec (never a cross-peer divergence).
    ///  - Point location: visibility walk with fixed edge order on the maintained-Delaunay
    ///    triangulation (terminates on Delaunay; step budget + linear-scan fallback as guard).
    ///  - Bounding structure: axis-aligned square at +-2*MAX_SNAPPED_COORD (4 ghost vertices,
    ///    2 seed triangles, fixed diagonal) — real coordinates inside the predicate domain.
    ///  - Cocircular tie-break: inCircle == 0 never flips; pseudo-polygon pick = first
    ///    polyline-order candidate whose circumcircle strictly contains no other candidate.
    ///  - Duplicate vertices: welded to the first occurrence (exact x,z match).
    ///  - Output: triangles rotated to smallest-vertex-first (winding preserved, CCW),
    ///    sorted lexicographically.
    ///  - Hole/outer erasure: 0-1 BFS parity from ghost triangles (depth = min constraint
    ///    crossings; keep odd) — winding-agnostic.
    ///
    /// Build-time / discrete-event scope only (List allocations allowed; never per-tick).
    /// </summary>
    public static class FPConstrainedDelaunay
    {
        /// <summary>
        /// Measurement hooks for the constraint-insertion allocation work. Off by default, so the
        /// Release cost is one predictable branch per carve — that is what lets the same build
        /// serve both the byte gates (diagnostics off, or the counters' own allocation would
        /// pollute the number) and the distribution collection (diagnostics on).
        ///
        /// These answer questions the source cannot: how many triangles a carve actually removes,
        /// whether the rented triangle array ever overflows into an Array.Resize, and how much of
        /// the per-carve allocation each collection accounts for.
        ///
        /// <para><b>One triangulating thread while enabled — never in a multi-room process.</b>
        /// Every field here is a process-global written without an interlock, so two rooms carving
        /// at once do not merely blur the numbers: <see cref="ChannelLens"/> is a lock-free
        /// <see cref="List{T}"/>, and concurrent <c>Add</c> overwrites its backing array and throws
        /// <see cref="IndexOutOfRangeException"/> out of code that has nothing to do with
        /// diagnostics. It is also unbounded until <see cref="Reset"/> — a long-lived process that
        /// leaves this on leaks.</para>
        ///
        /// <para>This is a runtime flag rather than a <c>#if DEBUG</c> block on purpose: the
        /// allocation measurements it feeds are only meaningful in Release (DEBUG adds the CDT
        /// SelfCheck and its own allocations), so the switch has to survive into the build being
        /// measured. The cost of that choice is this paragraph — the guard is a convention, not a
        /// compiler.</para>
        /// </summary>
        internal static class Diag
        {
            /// <summary>
            /// Off by default. Turn on around a measurement and turn it off in a <c>finally</c>;
            /// see the type note for why "around" has to mean a single triangulating thread.
            /// </summary>
            internal static bool Enabled;

            internal static int CarveCount;
            internal static long ChannelLenSum;
            internal static int ChannelLenMax;
            internal static readonly List<int> ChannelLens = new List<int>();

            /// <summary>Times <c>NewTri</c> had to Array.Resize the rented triangle array.</summary>
            internal static int TrisGrowthEvents;
            internal static int LastTrisCapacity;
            internal static int LastTriCount;
            internal static int RentedTrisCapacity;

            internal static void Reset()
            {
                CarveCount = 0;
                ChannelLenSum = 0;
                ChannelLenMax = 0;
                ChannelLens.Clear();
                TrisGrowthEvents = 0;
                LastTrisCapacity = 0;
                LastTriCount = 0;
                RentedTrisCapacity = 0;
            }
        }

        /// <summary>
        /// Triangulates snapped-grid points with constraint edges and returns triangle index
        /// triplets referencing the input arrays (duplicates weld to their first occurrence).
        /// Output triangles are CCW and canonically ordered.
        ///
        /// <para><b>Constraint multiplicity is meaningful — submitting a segment twice is a
        /// contract, not an accident.</b> An edge marked more than once is still a wall
        /// (<c>Constrained</c> is OR over markings), but crossing it does not change walkability
        /// (the erase pass reads an XOR parity). So a segment submitted an EVEN number of times
        /// stays geometrically present while carving nothing, which is how an open seam — a cliff
        /// join a unit may climb, a bridge rail, a wall footing — is expressed. Duplicates are
        /// deliberately not folded away, and this holds for hole constraints on
        /// <see cref="TriangulateFromSnapshot(CdtSnapshot, long[], long[], int[], bool, IKLogger)"/>
        /// too.</para>
        ///
        /// <para>Two limits ride with it. An <b>odd-multiplicity open chain is undefined</b>: the
        /// erase pass reads a min-depth parity that is path-independent only when the marked set is
        /// a sum of closed curves, and doubling is what makes an open chain satisfy that (every
        /// edge even ⇒ the odd set is empty). And a pair whose two endpoints <b>weld to the same
        /// vertex is dropped entirely</b> rather than becoming a parity-neutral wall — welding
        /// happens before constraint insertion, so a caller with coincident input coordinates can
        /// reach this.</para>
        /// </summary>
        /// <param name="xs">Snapped X coordinates (|x| &lt;= FPGeoPredicates.MAX_SNAPPED_COORD).</param>
        /// <param name="zs">Snapped Z coordinates (same length as <paramref name="xs"/>).</param>
        /// <param name="constraintPairs">Constraint edges as index pairs (v0,v1, v2,v3, ...). May be null/empty.</param>
        /// <param name="eraseOuterAndHoles">
        /// True: keep only triangles at odd constraint-crossing depth from the outside
        /// (walkable interior; hole interiors erased). False: keep every non-ghost triangle
        /// (plain Delaunay output for diagnostics/tests).
        /// </param>
        /// <param name="logger">Optional diagnostic logger (IKLogger).</param>
        /// <exception cref="ArgumentException">Out-of-domain coordinates or malformed input.</exception>
        /// <exception cref="FPConstraintCrossingException">A constraint crosses another constraint (T1 NotAllowed). It derives from <see cref="InvalidOperationException"/>, so an existing catch still applies.</exception>
        public static int[] Triangulate(
            long[] xs, long[] zs, int[] constraintPairs,
            bool eraseOuterAndHoles = true, IKLogger logger = null)
        {
            if (xs == null || zs == null || xs.Length != zs.Length)
                throw new ArgumentException("FPConstrainedDelaunay: xs/zs must be non-null and equal length");
            if (xs.Length < 3)
                throw new ArgumentException("FPConstrainedDelaunay: at least 3 vertices required");

            var cdt = new Cdt(xs, zs, logger);
            cdt.InsertAllVertices();
            cdt.InsertConstraints(constraintPairs, constraintPairs == null ? 0 : constraintPairs.Length);
            cdt.SelfCheck();
            // NonPooling pool => the extract buffer is exact-size, so the historical
            // "Length == triangle count * 3" contract of this entry point is preserved.
            int[] result = cdt.Extract(eraseOuterAndHoles, out int count);
            System.Diagnostics.Debug.Assert(result.Length == count,
                "FPConstrainedDelaunay.Triangulate: NonPooling must return exact-size buffers");
            return result;
        }

        /// <summary>
        /// Builds a reusable base snapshot for a caller-authored constraint set: the full CDT state
        /// after inserting all base vertices and base constraints. Runs the exact same code path as
        /// <see cref="Triangulate"/> up to (but excluding) Extract, so
        /// "BuildSnapshot + Extract == Triangulate" bit-identically (freeze-consistency gate).
        ///
        /// <para>Feed the result to
        /// <see cref="TriangulateFromSnapshot(CdtSnapshot, long[], long[], int[], bool, IKLogger)"/>
        /// once per set of holes; the expensive half is this build and it is shared across resumes.
        /// The returned triangle indices go to
        /// <c>FPNavMeshBuildPipeline.BuildFromConformingTriangulation</c>, which also takes a
        /// <c>previous</c> mesh — so a caller-authored pipeline keeps the incremental patch path.
        /// What it does not get is slicing: this entry point runs to completion.</para>
        ///
        /// <para><b>Also attaches a base coordinate map</b>, which is what lets the public resume
        /// check its input cheaply (see <c>CdtSnapshot.CoordToIndex</c>). That costs one pass over
        /// the base vertices here, once, rather than on every resume.</para>
        /// </summary>
        /// <param name="xs">Snapped X coordinates (|x| &lt;= FPGeoPredicates.MAX_SNAPPED_COORD).</param>
        /// <param name="zs">Snapped Z coordinates (same length as <paramref name="xs"/>).</param>
        /// <param name="constraintPairs">Base constraint edges as index pairs. May be null/empty.</param>
        /// <param name="logger">Optional diagnostic logger.</param>
        /// <exception cref="ArgumentException">Out-of-domain coordinates or malformed input.</exception>
        /// <exception cref="FPConstraintCrossingException">A constraint crosses another constraint.</exception>
        public static CdtSnapshot BuildSnapshot(
            long[] xs, long[] zs, int[] constraintPairs, IKLogger logger = null)
        {
            CdtSnapshot snap = BuildSnapshotCore(xs, zs, constraintPairs, logger);

            // [0, RealCount): the ghost block trails Xs/Zs and is not caller geometry.
            var map = new Dictionary<(long, long), int>(snap.RealCount);
            for (int i = 0; i < snap.RealCount; i++)
                map[(snap.Xs[i], snap.Zs[i])] = i;   // duplicates welded already; last wins, unused

            return snap.WithCoordMap(map);
        }

        /// <summary>
        /// The snapshot build itself, without the public factory's coordinate map. This is what the
        /// rebaker uses: it already keeps its own coordinate map and must not pay for a second one,
        /// and its hole vertices are deduped upstream so the public resume's check would find nothing.
        /// </summary>
        internal static CdtSnapshot BuildSnapshotCore(
            long[] xs, long[] zs, int[] constraintPairs, IKLogger logger = null)
        {
            if (xs == null || zs == null || xs.Length != zs.Length)
                throw new ArgumentException("FPConstrainedDelaunay: xs/zs must be non-null and equal length");
            if (xs.Length < 3)
                throw new ArgumentException("FPConstrainedDelaunay: at least 3 vertices required");

            var cdt = new Cdt(xs, zs, logger);
            cdt.InsertAllVertices();
            cdt.InsertConstraints(constraintPairs, constraintPairs == null ? 0 : constraintPairs.Length);
            cdt.SelfCheck();
            return cdt.Freeze();
        }

        /// <summary>
        /// Resumes from a base snapshot with a set of holes and returns triangle index triplets.
        /// Sizes come from the arrays, and the returned array is exact-size.
        ///
        /// <para><b>Hole vertices must not duplicate any coordinate</b> — neither a base coordinate
        /// nor another hole's. Unlike <see cref="Triangulate"/>, a resume does <b>not</b> weld the
        /// vertices you append: the snapshot's weld map covers the BASE range only and appended
        /// holes get an identity mapping, and your constraint indices are positional, so welding
        /// them would silently renumber your pairs. A violation therefore throws rather than
        /// quietly producing a different mesh. Hole constraint indices live in the combined
        /// space — base <c>0..RealCount-1</c> first, then holes appended in the order given.</para>
        ///
        /// <para>The check is O(holes) against the map the public
        /// <see cref="BuildSnapshot"/> attached, so it does not scale with the base mesh.</para>
        /// </summary>
        /// <param name="snapshot">A snapshot from <see cref="BuildSnapshot"/>.</param>
        /// <param name="holeXs">Snapped hole X coordinates.</param>
        /// <param name="holeZs">Snapped hole Z coordinates (same length as <paramref name="holeXs"/>).</param>
        /// <param name="holeConstraintPairs">Hole constraint edges as index pairs in the combined space. May be null/empty.</param>
        /// <param name="eraseOuterAndHoles">See <see cref="Triangulate"/>.</param>
        /// <param name="logger">Optional diagnostic logger.</param>
        /// <exception cref="ArgumentException">Null arguments, mismatched lengths, or out-of-domain coordinates.</exception>
        /// <exception cref="InvalidOperationException">A duplicate hole coordinate, or a constraint crossing another constraint.</exception>
        public static int[] TriangulateFromSnapshot(
            CdtSnapshot snapshot, long[] holeXs, long[] holeZs, int[] holeConstraintPairs,
            bool eraseOuterAndHoles = true, IKLogger logger = null)
        {
            if (snapshot == null)
                throw new ArgumentException("FPConstrainedDelaunay: snapshot is null");
            if (holeXs == null || holeZs == null)
                throw new ArgumentException("FPConstrainedDelaunay: holeXs/holeZs must be non-null");
            if (holeXs.Length != holeZs.Length)
                throw new ArgumentException("FPConstrainedDelaunay: holeXs/holeZs must be equal length");

            ValidateHoleCoordinates(snapshot, holeXs, holeZs, holeXs.Length);

            int[] result = TriangulateFromSnapshot(
                snapshot, holeXs, holeZs, holeXs.Length,
                holeConstraintPairs, holeConstraintPairs == null ? 0 : holeConstraintPairs.Length,
                out int count, eraseOuterAndHoles, logger, pool: null);
            System.Diagnostics.Debug.Assert(result.Length == count,
                "FPConstrainedDelaunay.TriangulateFromSnapshot: NonPooling must return exact-size buffers");
            return result;
        }

        /// <summary>
        /// The no-duplicate-hole-coordinate contract, enforced where a caller can actually break it.
        ///
        /// <para>The same check exists inside the resume as a <c>#if DEBUG</c> scan, and it is
        /// deliberately left there rather than promoted: that scan is O(holes x base vertices) —
        /// measured at ~9.4 us per hole against a 17k-vertex base, 91% of the whole hole-proportional
        /// cost — and the rebaker provably cannot trip it, so making it unconditional would tax the
        /// runtime rebake path for a defense it does not need. This one runs only on the public
        /// entry, and only against the map the public factory already built.</para>
        ///
        /// <para>Hole-to-hole duplicates need their own set: the map holds base coordinates only.</para>
        /// </summary>
        private static void ValidateHoleCoordinates(
            CdtSnapshot snapshot, long[] holeXs, long[] holeZs, int holeCount)
        {
            var baseMap = snapshot.CoordToIndex;
            if (baseMap == null)
                return;   // internal-path snapshot: the DEBUG scan inside the resume still covers it

            var seen = new HashSet<(long, long)>(holeCount);
            for (int i = 0; i < holeCount; i++)
            {
                var key = (holeXs[i], holeZs[i]);
                if (baseMap.ContainsKey(key))
                    throw new InvalidOperationException(
                        $"FPConstrainedDelaunay: hole vertex {i} duplicates a base coordinate " +
                        $"({key.Item1}, {key.Item2}) — a resume does not weld, so this would renumber " +
                        $"your constraint indices");
                if (!seen.Add(key))
                    throw new InvalidOperationException(
                        $"FPConstrainedDelaunay: hole vertex {i} duplicates an earlier hole coordinate " +
                        $"({key.Item1}, {key.Item2})");
            }
        }

        /// <summary>
        /// Resumes from a base snapshot: clone (+ ghost rebase), insert hole vertices in
        /// (x,z) order, insert hole constraints, extract. Revised T3 order:
        /// base sorted -> base constraints [snapshot boundary] -> holes sorted -> hole
        /// constraints. Hole vertices must not duplicate any existing coordinate — the rebaker
        /// dedups against base vertices before calling, and a violation throws IN DEBUG (the scan
        /// that catches it is O(holes x base vertices) and the production caller cannot trip it, so
        /// it is compiled out of release builds). Hole constraint indices are in the combined space
        /// (base 0..r-1 first, holes r..r+h-1 appended).
        /// </summary>
        internal static int[] TriangulateFromSnapshot(
            CdtSnapshot snapshot, long[] holeXs, long[] holeZs, int holeCount,
            int[] holeConstraintPairs, int holeConstraintCount, out int indexCount,
            bool eraseOuterAndHoles = true, IKLogger logger = null,
            FPNavMeshRebakeBufferPool pool = null)
        {
            if (snapshot == null)
                throw new ArgumentException("FPConstrainedDelaunay: snapshot is null");
            if (holeXs == null || holeZs == null)
                throw new ArgumentException("FPConstrainedDelaunay: holeXs/holeZs must be non-null");
            // Counts, not Length: both may be oversized pool buffers.
            // Deriving the hole count from Length here would pull the ghost block into the hole
            // range and produce a valid-looking but wrong mesh — silently.
            if (holeCount < 0 || holeCount > holeXs.Length || holeCount > holeZs.Length)
                throw new ArgumentException("FPConstrainedDelaunay: holeCount outside holeXs/holeZs");

            Cdt cdt = ResumeFromSnapshot(
                snapshot, holeXs, holeZs, holeCount, holeConstraintPairs, holeConstraintCount,
                logger, pool);
            return cdt.Extract(eraseOuterAndHoles, out indexCount);
        }

        /// <summary>
        /// Everything <see cref="TriangulateFromSnapshot"/> does EXCEPT the extract: clone the
        /// frozen base, rebase the ghosts, insert the hole vertices and their constraints.
        /// Returned so a caller that wants to slice the extract can hold the CDT across calls —
        /// the insertion is not worth slicing (0.11 ms of a 2.5 ms extract on a 22k stage,
        /// because the base insertion is already frozen into the snapshot).
        /// </summary>
        internal static Cdt ResumeFromSnapshot(
            CdtSnapshot snapshot, long[] holeXs, long[] holeZs, int holeCount,
            int[] holeConstraintPairs, int holeConstraintCount,
            IKLogger logger, FPNavMeshRebakeBufferPool pool)
        {
            var cdt = new Cdt(snapshot, holeXs, holeZs, holeCount, logger, pool);
            cdt.SelfCheck();
            cdt.InsertRange(snapshot.RealCount, snapshot.RealCount + holeCount);
            cdt.InsertConstraints(holeConstraintPairs, holeConstraintCount);
            cdt.SelfCheck();
            if (Diag.Enabled)
            {
                Diag.LastTrisCapacity = cdt.TrisCapacityForDiagnostics;
                Diag.LastTriCount = cdt.TriCountForDiagnostics;
            }
            return cdt;
        }

        /// <summary>
        /// Immutable frozen CDT state after base vertices + base constraints
        /// Never transmitted — a peer-local derivative of the base mesh; determinism follows from
        /// the deterministic build chain, with the nav fingerprint as the final cross-peer check. Shared read-only across rebakes/workers;
        /// every resume clones it.
        ///
        /// <para><b>Opaque handle.</b> Public so a caller can hold one between
        /// <see cref="BuildSnapshot"/> and <see cref="TriangulateFromSnapshot(CdtSnapshot, long[], long[], int[], bool, IKLogger)"/>;
        /// every member stays internal because they are the internal representation, not a format.
        /// Reading the base geometry is what <c>FPNavMesh</c> is for.</para>
        /// </summary>
        public sealed class CdtSnapshot
        {
            internal readonly long[] Xs;        // realCount + 4 (ghosts trailing)
            internal readonly long[] Zs;
            internal readonly int[] Weld;       // realCount
            internal readonly Cdt.Tri[] Tris;   // TriCount entries used
            internal readonly int TriCount;
            internal readonly int[] VertexTri;  // realCount + 4
            internal readonly int LastLocate;   // determinism material (walk start hint)
            internal readonly int RealCount;

            /// <summary>
            /// Base coordinate -> index, over [0, RealCount) only (the ghost block trails Xs/Zs and
            /// must stay out). Non-null ONLY on snapshots handed out by the public
            /// <see cref="BuildSnapshot"/>, which is what lets the public resume enforce the
            /// no-duplicate-hole-vertex contract in O(holes) instead of O(holes x base).
            ///
            /// <para>Built eagerly at construction and never written after, because a snapshot is
            /// shared: rooms sharing a stage hold one, and a server ticks its rooms on the thread
            /// pool. A lazy cache here would be a race in a type whose whole contract is
            /// immutability.</para>
            ///
            /// <para><c>null</c> on the internal <see cref="BuildSnapshotCore"/> path — the rebaker
            /// keeps its own map (<c>FPNavMeshRebakeSnapshot.CoordToIndex</c>) and must not pay for a
            /// second one. That asymmetry is the whole reason the two factories have different names.</para>
            /// </summary>
            internal readonly Dictionary<(long, long), int> CoordToIndex;

            internal CdtSnapshot(
                long[] xs, long[] zs, int[] weld, Cdt.Tri[] tris, int triCount,
                int[] vertexTri, int lastLocate, int realCount,
                Dictionary<(long, long), int> coordToIndex = null)
            {
                Xs = xs; Zs = zs; Weld = weld; Tris = tris; TriCount = triCount;
                VertexTri = vertexTri; LastLocate = lastLocate; RealCount = realCount;
                CoordToIndex = coordToIndex;
            }

            /// <summary>
            /// Same frozen state with a base coordinate map attached. Shares every array — O(1)
            /// besides the map itself — so the public factory can add the map without a second freeze.
            /// </summary>
            internal CdtSnapshot WithCoordMap(Dictionary<(long, long), int> coordToIndex)
                => new CdtSnapshot(Xs, Zs, Weld, Tris, TriCount, VertexTri, LastLocate, RealCount,
                                   coordToIndex);
        }

        // ------------------------------------------------------------------------------------
        // Implementation. Internal (not private nested in method) so the dotnet test assembly
        // (InternalsVisibleTo) can drive lower-level checks if ever needed.
        // ------------------------------------------------------------------------------------
        internal sealed class Cdt
        {
            internal struct Tri
            {
                public int V0, V1, V2;      // CCW; edge k = (Vk, V(k+1)%3)
                public int N0, N1, N2;      // neighbor across edge k (-1 = none)
                // Edge marks, packed: bit k = constrained, bit k+3 = cross parity. Three bools
                // per bit would push Tri from 28 to 32 bytes; one byte lands back on 28 because
                // int*6 = 24 is already 4-aligned.
                public byte E;
                public bool Alive;
            }

            /// <summary>
            /// One edge's constraint state — BOTH bits, moved as a unit.
            ///
            /// <para><b>Constrained</b> is OR over markings: an edge marked at least once is a
            /// wall and never flips. <b>CrossParity</b> is XOR: it answers a different question —
            /// "does walkability change when you cross here?" — and for an edge two rings share,
            /// the answer is no. A single boolean cannot hold both, which is exactly why an
            /// enclosed building's interior used to turn walkable.</para>
            ///
            /// <para>The two bits travel together because the failure mode of this change is a
            /// propagation site that carries one and drops the other — the type is what makes
            /// that hard to write.</para>
            /// </summary>
            internal readonly struct EdgeMark
            {
                internal readonly bool Constrained;
                internal readonly bool CrossParity;

                internal EdgeMark(bool constrained, bool crossParity)
                {
                    Constrained = constrained;
                    CrossParity = crossParity;
                }

                internal bool Same(EdgeMark other)
                {
                    return Constrained == other.Constrained && CrossParity == other.CrossParity;
                }
            }

            /// <summary>
            /// Canonicalized output triangle. A struct array + <see cref="Array.Sort{T}(T[],int,int)"/>
            /// replaces List&lt;(int,int,int)&gt; + Sort(Comparison) so the buffer can be pooled;
            /// the comparison is the same lexicographic order and equal elements are identical
            /// values, so sort stability cannot affect the emitted indices.
            /// </summary>
            internal readonly struct Triple : IComparable<Triple>
            {
                internal readonly int A, B, C;

                internal Triple(int a, int b, int c) { A = a; B = b; C = c; }

                public int CompareTo(Triple other)
                {
                    int cmp = A.CompareTo(other.A);
                    if (cmp != 0) return cmp;
                    cmp = B.CompareTo(other.B);
                    return cmp != 0 ? cmp : C.CompareTo(other.C);
                }
            }

            private readonly long[] _xs;   // length = realCount + 4 ghosts
            private readonly long[] _zs;
            private readonly int _realCount;
            private readonly int[] _weld;  // input index -> canonical (first-occurrence) index
            private readonly IKLogger _logger;

            private Tri[] _tris = new Tri[64];
            private int _triCount;

            internal int TrisCapacityForDiagnostics => _tris.Length;
            internal int TriCountForDiagnostics => _triCount;
            private readonly int[] _vertexTri;  // any alive triangle containing the vertex
            private int _lastLocate;

            private readonly Stack<(int tri, int va, int vb, int apex)> _legalizeStack
                = new Stack<(int, int, int, int)>();

            // Work-buffer source. NonPooling on the load-time
            // path, which is what Freeze() requires: a snapshot takes array ownership, so it
            // must never be handed pooled storage that a later rebake would overwrite.
            private readonly FPNavMeshRebakeBufferPool _pool = FPNavMeshRebakeBufferPool.NonPooling;

            internal Cdt(long[] xs, long[] zs, IKLogger logger)
            {
                _logger = logger;
                _realCount = xs.Length;
                _xs = new long[_realCount + 4];
                _zs = new long[_realCount + 4];
                _weld = new int[_realCount];

                long m = FPGeoPredicates.MAX_SNAPPED_COORD;
                var seen = new Dictionary<(long, long), int>(_realCount);
                for (int i = 0; i < _realCount; i++)
                {
                    long x = xs[i], z = zs[i];
                    if (x < -m || x > m || z < -m || z > m)
                        throw new ArgumentException(
                            $"FPConstrainedDelaunay: vertex {i} outside snapped domain (|coord| <= {m})");
                    _xs[i] = x;
                    _zs[i] = z;
                    if (seen.TryGetValue((x, z), out int first))
                        _weld[i] = first;
                    else
                    {
                        seen[(x, z)] = i;
                        _weld[i] = i;
                    }
                }

                // Ghost bounding square at +-2M (fixed diagonal g0-g2).
                long g = FPGeoPredicates.DOMAIN_ABS_MAX;
                int g0 = _realCount, g1 = _realCount + 1, g2 = _realCount + 2, g3 = _realCount + 3;
                _xs[g0] = -g; _zs[g0] = -g;
                _xs[g1] = g; _zs[g1] = -g;
                _xs[g2] = g; _zs[g2] = g;
                _xs[g3] = -g; _zs[g3] = g;

                _vertexTri = new int[_realCount + 4];
                for (int i = 0; i < _realCount + 4; i++)
                    _vertexTri[i] = -1;

                int t0 = NewTri(g0, g1, g2);
                int t1 = NewTri(g0, g2, g3);
                SetNeighborByPair(t0, g0, g2, t1);
                SetNeighborByPair(t1, g0, g2, t0);
                _lastLocate = t0;
            }

            /// <summary>
            /// Resume ctor: clone the frozen base state and
            /// rebase the trailing ghost block so hole vertices take indices r..r+h-1
            /// (layout [base, holes, ghosts] — Tri vertex refs >= r shift by +h; neighbor
            /// fields are triangle indices and stay). This is the single clone function:
            /// every mutable field of Cdt must be copied here (add a field => update this).
            /// </summary>
            internal Cdt(CdtSnapshot snap, long[] holeXs, long[] holeZs, int holeCount,
                IKLogger logger, FPNavMeshRebakeBufferPool pool = null)
            {
                _logger = logger;
                _pool = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
                int r = snap.RealCount;
                // holeCount, never holeXs.Length: the arrays may be oversized pool buffers and
                // the tail is last rebake's data.
                int h = holeCount;
                _realCount = r + h;

                long m = FPGeoPredicates.MAX_SNAPPED_COORD;
                // Rented buffers are valid over their logical range only — every element in
                // [0, _realCount + 4) is written below, which is the count contract.
                _xs = _pool.CdtXs(_realCount + 4);
                _zs = _pool.CdtZs(_realCount + 4);
                Array.Copy(snap.Xs, 0, _xs, 0, r);
                Array.Copy(snap.Zs, 0, _zs, 0, r);
                for (int g = 0; g < 4; g++)
                {
                    _xs[_realCount + g] = snap.Xs[r + g];
                    _zs[_realCount + g] = snap.Zs[r + g];
                }
                for (int i = 0; i < h; i++)
                {
                    long x = holeXs[i], z = holeZs[i];
                    if (x < -m || x > m || z < -m || z > m)
                        throw new ArgumentException(
                            $"FPConstrainedDelaunay: hole vertex {i} outside snapped domain (|coord| <= {m})");
#if DEBUG
                    // Contract: hole vertices must not duplicate base or other hole
                    // coordinates (the weld map is not preserved in the snapshot; the rebaker
                    // dedups upstream — this is the explicit defense line).
                    //
                    // DEBUG-only, because the production caller cannot break it. The rebaker's
                    // AddHoleVertex looks a corner up in the snapshot's base coordinate map FIRST
                    // and returns the base index when it hits, so a coinciding corner never becomes
                    // a hole vertex at all; a second map does the same for hole-to-hole. This entry
                    // point is internal and that rebaker is its only non-test caller, so the scans
                    // below can only ever run to completion and find nothing.
                    //
                    // They are not free while doing it. The base scan is O(holes x base vertices),
                    // and Field has 17,142 base vertices: measured on the clone constructor, 128
                    // holes cost 1.395 ms with these loops and 0.126 ms without — 91% of the whole
                    // hole-proportional cost, at ~9.4 us per hole, which is exactly one pass over
                    // the base coordinates.
                    //
                    // Kept rather than deleted because tests DO reach this entry point directly
                    // with hand-built arrays (InternalsVisibleTo), and there the check is the only
                    // net there is.
                    for (int j = 0; j < i; j++)
                    {
                        if (holeXs[j] == x && holeZs[j] == z)
                            throw new InvalidOperationException(
                                "FPConstrainedDelaunay: duplicate hole vertex coordinate (snapshot resume contract)");
                    }
                    for (int j = 0; j < r; j++)
                    {
                        if (snap.Xs[j] == x && snap.Zs[j] == z)
                            throw new InvalidOperationException(
                                "FPConstrainedDelaunay: hole vertex duplicates a base coordinate (snapshot resume contract)");
                    }
#endif
                    _xs[r + i] = x;
                    _zs[r + i] = z;
                }

                _weld = _pool.CdtWeld(_realCount);
                Array.Copy(snap.Weld, 0, _weld, 0, r);
                for (int i = r; i < _realCount; i++)
                    _weld[i] = i;

                _vertexTri = _pool.CdtVertexTri(_realCount + 4);
                Array.Copy(snap.VertexTri, 0, _vertexTri, 0, r);
                for (int i = r; i < _realCount; i++)
                    _vertexTri[i] = -1;
                for (int g = 0; g < 4; g++)
                    _vertexTri[_realCount + g] = snap.VertexTri[r + g];

                _triCount = snap.TriCount;
                _tris = _pool.CdtTris(System.Math.Max(64, _triCount + h * 2 + 8));
                if (Diag.Enabled)
                    Diag.RentedTrisCapacity = _tris.Length;
                for (int t = 0; t < _triCount; t++)
                {
                    Tri tri = snap.Tris[t];   // struct copy — the snapshot is never mutated
                    if (tri.V0 >= r) tri.V0 += h;
                    if (tri.V1 >= r) tri.V1 += h;
                    if (tri.V2 >= r) tri.V2 += h;
                    _tris[t] = tri;
                }
                _lastLocate = snap.LastLocate;
            }

            /// <summary>
            /// Freezes this instance into an immutable snapshot (takes array ownership — the
            /// Cdt must be discarded afterwards). The legalize stack must be drained first.
            /// </summary>
            internal CdtSnapshot Freeze()
            {
                System.Diagnostics.Debug.Assert(_legalizeStack.Count == 0,
                    "Freeze: legalize stack must be drained");
                System.Diagnostics.Debug.Assert(
                    ReferenceEquals(_pool, FPNavMeshRebakeBufferPool.NonPooling),
                    "Freeze: a snapshot takes array ownership and must never hold pooled buffers");
                return new CdtSnapshot(_xs, _zs, _weld, _tris, _triCount, _vertexTri, _lastLocate, _realCount);
            }

            /// <summary>Inserts vertices [from, to) in (x, z) order — hole insertion resume step (revised T3).</summary>
            internal void InsertRange(int from, int to)
            {
                var order = new List<int>(to - from);
                for (int i = from; i < to; i++)
                    order.Add(i);
                order.Sort((a, b) =>
                {
                    int c = _xs[a].CompareTo(_xs[b]);
                    return c != 0 ? c : _zs[a].CompareTo(_zs[b]);
                });
                foreach (int p in order)
                    InsertPoint(p);
            }

            #region Predicate wrappers

            private int O(int a, int b, int c)
            {
                return FPGeoPredicates.Orient2D(_xs[a], _zs[a], _xs[b], _zs[b], _xs[c], _zs[c]);
            }

            private int IC(int a, int b, int c, int d)
            {
                return FPGeoPredicates.InCircle(_xs[a], _zs[a], _xs[b], _zs[b], _xs[c], _zs[c], _xs[d], _zs[d]);
            }

            /// <summary>p strictly between a and b, all three collinear (caller guarantees O==0).</summary>
            private bool StrictlyBetween(int a, int b, int p)
            {
                if (p == a || p == b)
                    return false;
                long bx = (_xs[p] - _xs[a]) * (_xs[p] - _xs[b]);
                long bz = (_zs[p] - _zs[a]) * (_zs[p] - _zs[b]);
                return bx <= 0 && bz <= 0;
            }

            #endregion

            #region Triangle store helpers

            private int NewTri(int a, int b, int c)
            {
                System.Diagnostics.Debug.Assert(O(a, b, c) > 0,
                    "FPConstrainedDelaunay: new triangle must be CCW and non-degenerate");
                if (_triCount == _tris.Length)
                {
                    if (Diag.Enabled)
                        Diag.TrisGrowthEvents++;
                    Array.Resize(ref _tris, _tris.Length * 2);
                }
                ref Tri t = ref _tris[_triCount];
                t.V0 = a; t.V1 = b; t.V2 = c;
                t.N0 = -1; t.N1 = -1; t.N2 = -1;
                t.E = 0;
                t.Alive = true;
                _vertexTri[a] = _triCount;
                _vertexTri[b] = _triCount;
                _vertexTri[c] = _triCount;
                return _triCount++;
            }

            // The edge-indexed accessors below all FOLD an out-of-range k rather than failing:
            // the `?:` reads land on slot 2, the `if/else if/else` writes land on slot 2, and the
            // bit accessors have their shift count masked to 5 bits — for k = -1 that is bit 31,
            // which against a byte is always 0. They are deliberately left that way: they sit in
            // the CDT's inner loops, and guarding all nine measured +2.9% on the Triangulate phase
            // of the Field asset (30.99 -> 31.88 ms best-of-3, with the unaffected Build phase flat
            // as a control) for a check that can only fire on an already-broken invariant.
            //
            // Instead the guard sits where the bad index is BORN. Every out-of-range k has exactly
            // one origin — EdgeIndexUndirected returning -1 for a pair that is not an edge of that
            // triangle — so refusing it there is complete for the real hazard and costs one compare
            // per pair lookup instead of one per accessor call. See RequireEdgeIndex.

            private static int Vk(in Tri t, int k) { return k == 0 ? t.V0 : k == 1 ? t.V1 : t.V2; }
            private static int Nk(in Tri t, int k) { return k == 0 ? t.N0 : k == 1 ? t.N1 : t.N2; }

            private static void SetV(ref Tri t, int k, int v) { if (k == 0) t.V0 = v; else if (k == 1) t.V1 = v; else t.V2 = v; }
            private static void SetN(ref Tri t, int k, int n) { if (k == 0) t.N0 = n; else if (k == 1) t.N1 = n; else t.N2 = n; }

            #region Edge marks

            // Bit layout of Tri.E: bit k = constrained (edge k), bit k+3 = cross parity (edge k).
            private const int ParityShift = 3;

            /// <summary>Both bits of edge k. Use this whenever a mark is COPIED — split
            /// inheritance, channel capture/restore — so neither bit can be dropped alone.</summary>
            private static EdgeMark Mk(in Tri t, int k)
            {
                return new EdgeMark((t.E & (1 << k)) != 0, (t.E & (1 << (k + ParityShift))) != 0);
            }

            private static void SetMk(ref Tri t, int k, EdgeMark m)
            {
                int clear = ~((1 << k) | (1 << (k + ParityShift)));
                int set = (m.Constrained ? 1 << k : 0) | (m.CrossParity ? 1 << (k + ParityShift) : 0);
                t.E = (byte)((t.E & clear) | set);
            }

            /// <summary>Assigns all three edges at once — the shape the old `t.C0 = ..; t.C1 = ..;
            /// t.C2 = ..` rewrites had, kept so a re-wire cannot leave a stale bit behind.</summary>
            private static void SetMarks(ref Tri t, EdgeMark m0, EdgeMark m1, EdgeMark m2)
            {
                t.E = 0;
                SetMk(ref t, 0, m0);
                SetMk(ref t, 1, m1);
                SetMk(ref t, 2, m2);
            }

            /// <summary>Is edge k a wall? Flip prevention and the crossing check ask this — a
            /// twice-marked edge is STILL a wall, so this is the OR bit, never the parity.</summary>
            private static bool IsConstrained(in Tri t, int k)
            {
                return (t.E & (1 << k)) != 0;
            }

            /// <summary>Does crossing edge k change walkability? The erase BFS asks this — for an
            /// edge two rings share the answer is no, which is the whole point of the change.</summary>
            private static bool CrossesParity(in Tri t, int k)
            {
                return (t.E & (1 << (k + ParityShift))) != 0;
            }

            /// <summary>Records one more constraint on edge k: OR the wall bit, TOGGLE the parity.
            /// This is the ONLY place the two bits diverge; every other site copies.</summary>
            private static void MarkConstraint(ref Tri t, int k)
            {
                t.E = (byte)((t.E | (1 << k)) ^ (1 << (k + ParityShift)));
            }

            #endregion

            /// <summary>Directed edge index k with (Vk, Vk+1) == (va, vb); -1 if absent.</summary>
            private int EdgeIndexDirected(int tri, int va, int vb)
            {
                ref Tri t = ref _tris[tri];
                for (int k = 0; k < 3; k++)
                {
                    if (Vk(in t, k) == va && Vk(in t, (k + 1) % 3) == vb)
                        return k;
                }
                return -1;
            }

            /// <summary>Undirected edge index; -1 if absent.</summary>
            private int EdgeIndexUndirected(int tri, int va, int vb)
            {
                int k = EdgeIndexDirected(tri, va, vb);
                return k >= 0 ? k : EdgeIndexDirected(tri, vb, va);
            }

            /// <summary>
            /// Same lookup, for the callers that have no answer for "not an edge of this triangle".
            /// Use this unless the call site genuinely branches on the -1.
            ///
            /// <para>The -1 used to be guarded by <c>Debug.Assert</c> and then handed straight to
            /// an accessor, which FOLDS it onto slot 2 (see the note above Vk). So the dedicated
            /// server and IL2CPP builds — where the assert is compiled out — kept going and emitted
            /// a plausible but wrong navmesh. The wrongness is deterministic, so it is identical on
            /// every peer: neither the state hash nor the nav fingerprint can see it, and
            /// <see cref="SelfCheck"/>, the one net that would catch the corrupted topology
            /// afterwards, is itself DEBUG-only. Silent in exactly the build that ships.</para>
            ///
            /// <para>The sharpest of the four sites was CarveChannel's crossing test: folded,
            /// <c>IsConstrained(t, -1)</c> answers "not a wall" and the T1-NotAllowed refusal is
            /// skipped, so a constraint is driven through a wall one step BEFORE the erase BFS gets
            /// a chance to be wrong. FPNavMeshTriangle.SetPortal already refuses unconditionally
            /// for this same class of quiet-wrong-answer; this is that decision, applied here.</para>
            /// </summary>
            private int RequireEdgeIndex(int tri, int va, int vb, string site)
            {
                int k = EdgeIndexUndirected(tri, va, vb);
                if (k < 0)
                    ThrowEdgeNotFound(tri, va, vb, site);
                return k;
            }

            // Out of line: the message build must not sit in the caller's inlined body.
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            private static void ThrowEdgeNotFound(int tri, int va, int vb, string site)
            {
                throw new InvalidOperationException(
                    $"FPConstrainedDelaunay: {site} — ({va},{vb}) is not an edge of triangle {tri}, "
                    + "so the topology invariant is already broken. Folding the -1 onto edge 2 would "
                    + "emit a plausible but wrong navmesh that no cross-peer check can detect.");
            }

            private void SetNeighborByPair(int tri, int va, int vb, int neighbor)
            {
                int k = RequireEdgeIndex(tri, va, vb, "SetNeighborByPair");
                SetN(ref _tris[tri], k, neighbor);
            }

            private void MarkConstraintByPair(int tri, int va, int vb)
            {
                int k = RequireEdgeIndex(tri, va, vb, "MarkConstraintByPair");
                MarkConstraint(ref _tris[tri], k);
            }

            #endregion

            #region Point location (visibility walk on Delaunay)

            private enum LocateKind { Inside, OnEdge, OnVertex }

            private (int tri, LocateKind kind, int edge) Locate(int p)
            {
                int budget = 4 * _triCount + 16;
                int cur = _lastLocate;
                if (cur < 0 || !_tris[cur].Alive)
                    cur = FirstAliveTri();

                for (int step = 0; step < budget; step++)
                {
                    ref Tri t = ref _tris[cur];
                    int zeroEdge = -1;
                    int zeroCount = 0;
                    bool moved = false;

                    for (int k = 0; k < 3; k++)
                    {
                        int o = O(Vk(in t, k), Vk(in t, (k + 1) % 3), p);
                        if (o < 0)
                        {
                            int n = Nk(in t, k);
                            System.Diagnostics.Debug.Assert(n >= 0,
                                "Locate: walked out of the bounding square (input outside domain?)");
                            cur = n;
                            moved = true;
                            break;
                        }
                        if (o == 0)
                        {
                            zeroEdge = k;
                            zeroCount++;
                        }
                    }

                    if (moved)
                        continue;

                    _lastLocate = cur;
                    if (zeroCount == 0)
                        return (cur, LocateKind.Inside, -1);
                    if (zeroCount == 1)
                        return (cur, LocateKind.OnEdge, zeroEdge);
                    return (cur, LocateKind.OnVertex, -1);
                }

                // Deterministic fallback: linear scan (should not happen on a Delaunay walk).
                _logger?.KWarning($"[FPConstrainedDelaunay] Locate walk exceeded budget; falling back to linear scan");
                for (int i = 0; i < _triCount; i++)
                {
                    if (!_tris[i].Alive)
                        continue;
                    ref Tri t = ref _tris[i];
                    int zeroEdge = -1, zeroCount = 0;
                    bool outside = false;
                    for (int k = 0; k < 3; k++)
                    {
                        int o = O(Vk(in t, k), Vk(in t, (k + 1) % 3), p);
                        if (o < 0) { outside = true; break; }
                        if (o == 0) { zeroEdge = k; zeroCount++; }
                    }
                    if (outside)
                        continue;
                    _lastLocate = i;
                    if (zeroCount == 0) return (i, LocateKind.Inside, -1);
                    if (zeroCount == 1) return (i, LocateKind.OnEdge, zeroEdge);
                    return (i, LocateKind.OnVertex, -1);
                }
                throw new InvalidOperationException("FPConstrainedDelaunay: point location failed");
            }

            private int FirstAliveTri()
            {
                for (int i = 0; i < _triCount; i++)
                {
                    if (_tris[i].Alive)
                        return i;
                }
                throw new InvalidOperationException("FPConstrainedDelaunay: no alive triangles");
            }

            #endregion

            #region Vertex insertion (Lawson)

            internal void InsertAllVertices()
            {
                // Canonical vertices only, sorted by (x, z) — insertion order is input-order-free.
                var order = new List<int>(_realCount);
                for (int i = 0; i < _realCount; i++)
                {
                    if (_weld[i] == i)
                        order.Add(i);
                }
                order.Sort((a, b) =>
                {
                    int c = _xs[a].CompareTo(_xs[b]);
                    return c != 0 ? c : _zs[a].CompareTo(_zs[b]);
                });

                foreach (int p in order)
                    InsertPoint(p);

                if (_realCount != order.Count)
                    _logger?.KInformation($"[FPConstrainedDelaunay] welded {_realCount - order.Count} duplicate vertices");
            }

            private void InsertPoint(int p)
            {
                var (tri, kind, edge) = Locate(p);
                System.Diagnostics.Debug.Assert(kind != LocateKind.OnVertex,
                    "InsertPoint: duplicate vertex should have been welded");

                if (kind == LocateKind.Inside)
                    SplitInterior(tri, p);
                else
                    SplitOnEdge(tri, edge, p);

                RunLegalize();
            }

            private void SplitInterior(int tIdx, int p)
            {
                int a = _tris[tIdx].V0, b = _tris[tIdx].V1, c = _tris[tIdx].V2;
                int nAB = _tris[tIdx].N0, nBC = _tris[tIdx].N1, nCA = _tris[tIdx].N2;
                EdgeMark mAB = Mk(in _tris[tIdx], 0), mBC = Mk(in _tris[tIdx], 1), mCA = Mk(in _tris[tIdx], 2);

                // Reuse tIdx as (a, b, p); allocate (b, c, p), (c, a, p).
                int t2 = NewTri(b, c, p);
                int t3 = NewTri(c, a, p);

                ref Tri t = ref _tris[tIdx];
                t.V0 = a; t.V1 = b; t.V2 = p;
                t.N0 = nAB; t.N1 = t2; t.N2 = t3;
                SetMarks(ref t, mAB, default, default);

                ref Tri u = ref _tris[t2];
                u.N0 = nBC; u.N1 = t3; u.N2 = tIdx;
                SetMk(ref u, 0, mBC);

                ref Tri w = ref _tris[t3];
                w.N0 = nCA; w.N1 = tIdx; w.N2 = t2;
                SetMk(ref w, 0, mCA);

                if (nBC >= 0) SetNeighborByPair(nBC, b, c, t2);
                if (nCA >= 0) SetNeighborByPair(nCA, c, a, t3);

                _vertexTri[a] = tIdx; _vertexTri[b] = tIdx; _vertexTri[c] = t2; _vertexTri[p] = tIdx;

                PushLegalize(tIdx, a, b, p);
                PushLegalize(t2, b, c, p);
                PushLegalize(t3, c, a, p);
            }

            private void SplitOnEdge(int tIdx, int edge, int p)
            {
                ref Tri told = ref _tris[tIdx];
                int a = Vk(in told, edge);
                int b = Vk(in told, (edge + 1) % 3);
                int c = Vk(in told, (edge + 2) % 3);
                int uIdx = Nk(in told, edge);
                EdgeMark mSplit = Mk(in told, edge);
                System.Diagnostics.Debug.Assert(uIdx >= 0,
                    "SplitOnEdge: point on a hull edge is impossible inside the bounding square");

                int nBC = told.N1, nCA = told.N2; // careful: recompute by pair below instead
                // Re-derive t's other neighbors/flags by explicit edge positions.
                int eBC = EdgeIndexDirected(tIdx, b, c);
                int eCA = EdgeIndexDirected(tIdx, c, a);
                nBC = Nk(in told, eBC); EdgeMark mBC = Mk(in told, eBC);
                nCA = Nk(in told, eCA); EdgeMark mCA = Mk(in told, eCA);

                ref Tri uold = ref _tris[uIdx];
                int fBA = EdgeIndexDirected(uIdx, b, a);
                System.Diagnostics.Debug.Assert(fBA >= 0, "SplitOnEdge: neighbor lost shared edge");
                int d = Vk(in uold, (fBA + 2) % 3);
                int eAD = EdgeIndexDirected(uIdx, a, d);
                int eDB = EdgeIndexDirected(uIdx, d, b);
                int nAD = Nk(in uold, eAD); EdgeMark mAD = Mk(in uold, eAD);
                int nDB = Nk(in uold, eDB); EdgeMark mDB = Mk(in uold, eDB);

                // t := (a, p, c), t2 := (p, b, c), u := (b, p, d), u2 := (p, a, d)
                int t2 = NewTri(p, b, c);
                int u2 = NewTri(p, a, d);

                ref Tri t = ref _tris[tIdx];
                t.V0 = a; t.V1 = p; t.V2 = c;
                t.N0 = u2; t.N1 = t2; t.N2 = nCA;
                SetMarks(ref t, mSplit, default, mCA);

                ref Tri tt2 = ref _tris[t2];
                tt2.N0 = uIdx; tt2.N1 = nBC; tt2.N2 = tIdx;
                SetMarks(ref tt2, mSplit, mBC, default);

                ref Tri u = ref _tris[uIdx];
                u.V0 = b; u.V1 = p; u.V2 = d;
                u.N0 = t2; u.N1 = u2; u.N2 = nDB;
                SetMarks(ref u, mSplit, default, mDB);

                ref Tri uu2 = ref _tris[u2];
                uu2.N0 = tIdx; uu2.N1 = nAD; uu2.N2 = uIdx;
                SetMarks(ref uu2, mSplit, mAD, default);

                if (nBC >= 0) SetNeighborByPair(nBC, b, c, t2);
                if (nAD >= 0) SetNeighborByPair(nAD, a, d, u2);

                _vertexTri[a] = tIdx; _vertexTri[b] = uIdx; _vertexTri[c] = tIdx;
                _vertexTri[d] = uIdx; _vertexTri[p] = tIdx;

                PushLegalize(tIdx, c, a, p);   // edge (c,a) opposite p
                PushLegalize(t2, b, c, p);     // edge (b,c) opposite p
                PushLegalize(uIdx, d, b, p);   // edge (d,b) opposite p
                PushLegalize(u2, a, d, p);     // edge (a,d) opposite p
            }

            private void PushLegalize(int tri, int va, int vb, int apex)
            {
                _legalizeStack.Push((tri, va, vb, apex));
            }

            private void RunLegalize()
            {
                while (_legalizeStack.Count > 0)
                {
                    var (tri, va, vb, apex) = _legalizeStack.Pop();
                    if (!_tris[tri].Alive)
                        continue;
                    int e = EdgeIndexDirected(tri, va, vb);
                    if (e < 0)
                        continue; // stale (a later flip rewrote this triangle)
                    ref Tri t = ref _tris[tri];
                    if (Vk(in t, (e + 2) % 3) != apex)
                        continue; // stale
                    if (IsConstrained(in t, e))
                        continue; // constrained edges never flip — the OR bit, not the parity:
                                  // an edge two rings share is still a wall
                    int n = Nk(in t, e);
                    if (n < 0)
                        continue;

                    int f = EdgeIndexDirected(n, vb, va);
                    System.Diagnostics.Debug.Assert(f >= 0, "Legalize: neighbor lost shared edge");
                    int d = Vk(in _tris[n], (f + 2) % 3);

                    // t = (va, vb, apex) CCW; flip iff d strictly inside its circumcircle.
                    if (IC(va, vb, apex, d) > 0)
                        Flip(tri, e, n, f);
                }
            }

            /// <summary>
            /// Flips shared edge (A,B) between t=(A,B,P) and n=(B,A,D):
            /// t := (A,D,P), n := (D,B,P). Pushes the two new suspect edges (opposite P).
            /// </summary>
            private void Flip(int tIdx, int e, int nIdx, int f)
            {
                ref Tri told = ref _tris[tIdx];
                ref Tri nold = ref _tris[nIdx];

                int a = Vk(in told, e);
                int b = Vk(in told, (e + 1) % 3);
                int p = Vk(in told, (e + 2) % 3);
                int d = Vk(in nold, (f + 2) % 3);

                int ePA = EdgeIndexDirected(tIdx, p, a);
                int eBP = EdgeIndexDirected(tIdx, b, p);
                int nPA = Nk(in told, ePA); EdgeMark mPA = Mk(in told, ePA);
                int nBP = Nk(in told, eBP); EdgeMark mBP = Mk(in told, eBP);

                int fAD = EdgeIndexDirected(nIdx, a, d);
                int fDB = EdgeIndexDirected(nIdx, d, b);
                int nADn = Nk(in nold, fAD); EdgeMark mAD = Mk(in nold, fAD);
                int nDBn = Nk(in nold, fDB); EdgeMark mDB = Mk(in nold, fDB);

                // t := (A, D, P)
                ref Tri t = ref _tris[tIdx];
                t.V0 = a; t.V1 = d; t.V2 = p;
                t.N0 = nADn; t.N1 = nIdx; t.N2 = nPA;
                SetMarks(ref t, mAD, default, mPA);

                // n := (D, B, P)
                ref Tri n = ref _tris[nIdx];
                n.V0 = d; n.V1 = b; n.V2 = p;
                n.N0 = nDBn; n.N1 = nBP; n.N2 = tIdx;
                SetMarks(ref n, mDB, mBP, default);

                if (nADn >= 0) SetNeighborByPair(nADn, a, d, tIdx);
                if (nBP >= 0) SetNeighborByPair(nBP, b, p, nIdx);

                _vertexTri[a] = tIdx; _vertexTri[b] = nIdx;
                _vertexTri[d] = tIdx; _vertexTri[p] = tIdx;

                PushLegalize(tIdx, a, d, p);
                PushLegalize(nIdx, d, b, p);
            }

            #endregion

            #region Constraint insertion (channel + pseudo-polygon)

            /// <summary>
            /// Inserts constraint edges. Multiplicity is MEANINGFUL, not redundant: an edge
            /// carrying an odd number of
            /// constraints changes walkability when crossed, an even number does not. That is what
            /// makes two buildings placed wall to wall leave a solid block rather than a courtyard.
            ///
            /// <para>The consequence for callers: sending the same logical wall twice ERASES it
            /// from the erase pass — it stays a wall for flips, but the interior on the far side
            /// stops being carved. There is deliberately no duplicate check here, because at this
            /// layer a repeat is indistinguishable from the legitimate case: two rings sharing an
            /// edge emit (p,q) and (q,p), which weld and normalise to the same pair. The invariant
            /// lives one layer up, where a ring is emitted once per placement, and it is pinned
            /// there by a constraint-count test.</para>
            ///
            /// <para>Rings must be CLOSED. The erase pass reads a min-depth parity and that is
            /// path-independent only because the marked set is a sum of closed curves; an open
            /// chain would make the result depend on which way the BFS arrived.</para>
            /// </summary>
            internal void InsertConstraints(int[] constraintPairs, int pairArrayCount)
            {
                if (constraintPairs == null || pairArrayCount == 0)
                    return;
                // pairArrayCount, not Length: the array may be an oversized pool buffer whose
                // tail is last rebake's indices.
                if (pairArrayCount < 0 || pairArrayCount > constraintPairs.Length)
                    throw new ArgumentException("FPConstrainedDelaunay: pairArrayCount outside constraintPairs");
                if ((pairArrayCount & 1) != 0)
                    throw new ArgumentException("FPConstrainedDelaunay: constraintPairs must have even length");

                for (int i = 0; i < pairArrayCount; i += 2)
                {
                    int a = constraintPairs[i];
                    int b = constraintPairs[i + 1];
                    if (a < 0 || a >= _realCount || b < 0 || b >= _realCount)
                        throw new ArgumentException($"FPConstrainedDelaunay: constraint index out of range ({a},{b})");
                    a = _weld[a];
                    b = _weld[b];
                    if (a == b)
                        continue;
                    InsertConstraint(a, b);
                }
            }

            private void InsertConstraint(int a, int b)
            {
                int guard = 0;
                while (a != b)
                {
                    if (++guard > _realCount + 8)
                        throw new InvalidOperationException("FPConstrainedDelaunay: constraint insertion made no progress (corrupt topology?)");
                    a = ConstraintStep(a, b);
                }
            }

            /// <summary>
            /// Advances the constraint (a → b) by one stop: marks an existing (possibly collinear
            /// sub-)edge, or carves one channel and re-triangulates it. Returns the reached vertex.
            /// </summary>
            private int ConstraintStep(int a, int b)
            {
                // Walk the triangle fan around `a` looking for: the edge (a,b) itself, a vertex
                // exactly on the segment, or the cone triangle whose far edge the segment crosses.
                int start = _vertexTri[a];
                System.Diagnostics.Debug.Assert(start >= 0 && _tris[start].Alive, "ConstraintStep: bad vertexTri");
                int cur = start;
                int steps = 0;

                while (true)
                {
                    if (++steps > 4 * _triCount + 8)
                        throw new InvalidOperationException("FPConstrainedDelaunay: constraint fan walk did not terminate (corrupt topology?)");
                    ref Tri t = ref _tris[cur];
                    int i = t.V0 == a ? 0 : t.V1 == a ? 1 : 2;
                    System.Diagnostics.Debug.Assert(Vk(in t, i) == a, "ConstraintStep: fan lost the pivot vertex");
                    int vB = Vk(in t, (i + 1) % 3);
                    int vC = Vk(in t, (i + 2) % 3);

                    if (vB == b)
                    {
                        MarkConstrained(cur, a, b);
                        return b;
                    }

                    int oB = O(a, b, vB);
                    if (oB == 0 && StrictlyBetween(a, b, vB))
                    {
                        MarkConstrained(cur, a, vB);
                        return vB;
                    }

                    int oC = O(a, b, vC);
                    if (vC == b || (oC == 0 && StrictlyBetween(a, b, vC)))
                    {
                        // Handled when this fan triangle is visited with vC as the leading edge
                        // vertex; rotate one more step so the (a, vC) edge case is caught above.
                        int nextTri0 = Nk(in t, (i + 2) % 3);
                        System.Diagnostics.Debug.Assert(nextTri0 >= 0, "ConstraintStep: open fan around interior vertex");
                        cur = nextTri0;
                        continue;
                    }

                    // Segment enters strictly between rays a→vB (right side) and a→vC (left side)?
                    if (oB < 0 && oC > 0)
                        return CarveChannel(cur, i, a, b);

                    // Rotate CCW around a: cross edge (vC, a).
                    int nextTri = Nk(in t, (i + 2) % 3);
                    System.Diagnostics.Debug.Assert(nextTri >= 0, "ConstraintStep: open fan around interior vertex");
                    cur = nextTri;
                    System.Diagnostics.Debug.Assert(cur != start || steps < 2 || true, "fan wrap");
                    if (cur == start && steps > 1)
                        throw new InvalidOperationException(
                            "FPConstrainedDelaunay: constraint direction not found around vertex (degenerate input?)");
                }
            }

            /// <summary>
            /// Records ONE constraint on the edge (va, vb): the wall bit ORs, the crossing parity
            /// TOGGLES. This is the only site where the two
            /// bits diverge — everywhere else a mark is copied.
            ///
            /// <para>Both triangles are updated, and they must stay in step: SelfCheck compares
            /// neighbours' marks, and a one-sided toggle is exactly the asymmetry it looks for.</para>
            ///
            /// <para>Callers must not fold duplicates away. This is reached once per constraint
            /// even when the edge is already marked (the `vB == b` branch in ConstraintStep does
            /// not test the flag first, and InsertConstraints does not dedupe), which is what
            /// lets a second, coincident constraint bring the parity back to 0.</para>
            /// </summary>
            private void MarkConstrained(int tri, int va, int vb)
            {
                MarkConstraintByPair(tri, va, vb);
                int k = RequireEdgeIndex(tri, va, vb, "MarkConstrained");
                int n = Nk(in _tris[tri], k);
                System.Diagnostics.Debug.Assert(n >= 0, "MarkConstrained: constraint on hull edge");
                if (n >= 0)
                    MarkConstraintByPair(n, va, vb);
            }

            private int CarveChannel(int startTri, int pivotEdge, int a, int b)
            {
                // Channel walk state: crossing edge (l = left of a→b, r = right of a→b).
                var removed = new List<int>();
                var left = new List<int>();
                var right = new List<int>();

                ref Tri t0 = ref _tris[startTri];
                int r = Vk(in t0, (pivotEdge + 1) % 3); // oB < 0 → right
                int l = Vk(in t0, (pivotEdge + 2) % 3); // oC > 0 → left
                left.Add(l);
                right.Add(r);
                removed.Add(startTri);

                int cur = startTri;
                int stop;
                int guard = 0;
                while (true)
                {
                    // Real throw (not assert-only): a corrupt walk must fail fast in Release too —
                    // the alternative is an unbounded loop on the server tick thread.
                    if (++guard > 2 * _triCount + 8)
                        throw new InvalidOperationException("FPConstrainedDelaunay: channel walk did not terminate (corrupt topology?)");

                    int e = RequireEdgeIndex(cur, l, r, "CarveChannel: crossing edge lost");
                    // The wall bit, not the parity: an edge two rings share is still a wall, and
                    // driving a third constraint through it is still a crossing, which is refused.
                    if (IsConstrained(in _tris[cur], e))
                        throw new FPConstraintCrossingException(
                            _xs[a], _zs[a], _xs[b], _zs[b],
                            _xs[l], _zs[l], _xs[r], _zs[r]);

                    int next = Nk(in _tris[cur], e);
                    System.Diagnostics.Debug.Assert(next >= 0, "CarveChannel: crossed a hull edge");
                    ref Tri u = ref _tris[next];
                    int x = u.V0 != l && u.V0 != r ? u.V0 : u.V1 != l && u.V1 != r ? u.V1 : u.V2;

                    removed.Add(next);

                    if (x == b)
                    {
                        stop = b;
                        break;
                    }
                    int ox = O(a, b, x);
                    if (ox == 0 && StrictlyBetween(a, b, x))
                    {
                        stop = x;
                        break;
                    }
                    if (ox > 0)
                        l = x;   // apex joins the left polyline
                    else
                        r = x;   // apex joins the right polyline
                    if (ox > 0) left.Add(x); else right.Add(x);
                    cur = next;
                }

                if (Diag.Enabled)
                {
                    Diag.CarveCount++;
                    Diag.ChannelLenSum += removed.Count;
                    if (removed.Count > Diag.ChannelLenMax)
                        Diag.ChannelLenMax = removed.Count;
                    Diag.ChannelLens.Add(removed.Count);
                }

                // Kill BEFORE collecting the boundary. The
                // "is this neighbour part of the channel?" test then reads the Alive flag the
                // triangles already carry, which is why the HashSet that used to answer it is
                // gone entirely — not pooled, not replaced by a stamp array, just unnecessary.
                //
                // What makes the substitution sound is that a removed triangle can never
                // neighbour a triangle an EARLIER carve killed: WireChannel re-points every
                // outside triangle at the new triangles, so nothing live still refers to a
                // corpse. That was machine-checked over the whole suite before the change
                // (stage 0), and the assert below keeps checking it.
                foreach (int tri in removed)
                    _tris[tri].Alive = false;

                // Record channel boundary: outer neighbor + BOTH edge-mark bits per boundary edge.
                // A channel cannot cross a constrained edge, but its boundary can run along one,
                // so a mark captured here must come back intact — carrying only the wall bit
                // would silently drop the coincidence.
                var boundary = new Dictionary<(int, int), (int tri, EdgeMark mark)>();
                foreach (int tri in removed)
                {
                    ref Tri t = ref _tris[tri];
                    for (int k = 0; k < 3; k++)
                    {
                        int n = Nk(in t, k);
                        if (n >= 0 && !_tris[n].Alive)
                        {
#if DEBUG
                            // The invariant above, stated where it is relied on: a dead
                            // neighbour must be one of THIS carve's triangles. If an earlier
                            // carve's corpse ever showed up here it would be silently treated
                            // as interior and its boundary edge would vanish from the map.
                            System.Diagnostics.Debug.Assert(removed.Contains(n),
                                "CarveChannel: dead neighbour is not part of this channel — the " +
                                "Alive-based interior test would drop a boundary edge");
#endif
                            continue;
                        }
                        int va = Vk(in t, k);
                        int vb = Vk(in t, (k + 1) % 3);
                        var key = va < vb ? (va, vb) : (vb, va);
                        boundary[key] = (n, Mk(in t, k));
                    }
                }

                // Re-triangulate both pseudo-polygons. The walk collects both polylines in a→stop
                // order; the right side's base is (stop, a), so its point list must be reversed to
                // stay in polyline order (a backwards list mis-splits the Anglada recursion once a
                // side has 2+ points — degenerate triangles / corrupt topology).
                var triples = new List<(int, int, int)>();
                TriangulatePseudo(a, stop, left, 0, left.Count, triples);
                right.Reverse();
                TriangulatePseudo(stop, a, right, 0, right.Count, triples);

                WireChannel(a, stop, triples, boundary);
                return stop;
            }

            /// <summary>
            /// Anglada-style pseudo-polygon triangulation over pts[from..to) with base (pa, pb).
            /// All points lie strictly left of pa→pb. Pick = first polyline-order candidate whose
            /// circumcircle strictly contains no other candidate (deterministic; cocircular safe).
            /// </summary>
            private void TriangulatePseudo(int pa, int pb, List<int> pts, int from, int to, List<(int, int, int)> outTriples)
            {
                int count = to - from;
                if (count <= 0)
                    return;

                int pick = -1;
                for (int i = from; i < to; i++)
                {
                    bool ok = true;
                    for (int j = from; j < to; j++)
                    {
                        if (j == i)
                            continue;
                        if (IC(pa, pb, pts[i], pts[j]) > 0)
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                    {
                        pick = i;
                        break;
                    }
                }
                System.Diagnostics.Debug.Assert(pick >= 0, "TriangulatePseudo: no Delaunay candidate (corrupt channel)");

                int c = pts[pick];
                System.Diagnostics.Debug.Assert(O(pa, pb, c) > 0, "TriangulatePseudo: candidate not left of base");
                outTriples.Add((pa, pb, c));
                TriangulatePseudo(pa, c, pts, from, pick, outTriples);
                TriangulatePseudo(c, pb, pts, pick + 1, to, outTriples);
            }

            private void WireChannel(
                int a, int stop, List<(int, int, int)> triples,
                Dictionary<(int, int), (int tri, EdgeMark mark)> boundary)
            {
                // Allocate new triangles, wire them among themselves and to the channel boundary.
                var local = new Dictionary<(int, int), (int tri, int edge)>();

                // NewTri appends and returns _triCount++, so the triangles this call creates are
                // exactly [baseTri, baseTri + triples.Count) — the list that used to record them
                // was storing a range. Read the base BEFORE the
                // loop; reading it after would silently shift the range by its own length.
                int baseTri = _triCount;
                foreach (var (ta, tb, tc) in triples)
                    NewTri(ta, tb, tc);
                int newTriCount = triples.Count;

                for (int idx = 0; idx < newTriCount; idx++)
                {
                    int tri = baseTri + idx;
                    ref Tri t = ref _tris[tri];
                    for (int k = 0; k < 3; k++)
                    {
                        int va = Vk(in t, k);
                        int vb = Vk(in t, (k + 1) % 3);
                        var key = va < vb ? (va, vb) : (vb, va);

                        if (local.TryGetValue(key, out var other))
                        {
                            SetN(ref _tris[tri], k, other.tri);
                            SetN(ref _tris[other.tri], other.edge, tri);
                        }
                        else
                        {
                            local[key] = (tri, k);
                        }
                    }
                }

                foreach (var kv in local)
                {
                    var key = kv.Key;
                    var (tri, edge) = kv.Value;
                    if (Nk(in _tris[tri], edge) >= 0)
                        continue; // wired internally above

                    if (key == (a < stop ? (a, stop) : (stop, a)))
                        continue; // base edge is internal (left/right pair) — handled by local pairing

                    bool found = boundary.TryGetValue(key, out var outer);
                    System.Diagnostics.Debug.Assert(found, "WireChannel: boundary edge missing");
                    SetN(ref _tris[tri], edge, outer.tri);
                    SetMk(ref _tris[tri], edge, outer.mark);
                    if (outer.tri >= 0)
                        SetNeighborByPair(outer.tri, key.Item1, key.Item2, tri);
                }

                MarkConstrained(FindTriWithEdge(baseTri, newTriCount, a, stop), a, stop);
            }

            /// <summary>Scans the triangles a channel re-triangulation just appended, as a range.</summary>
            private int FindTriWithEdge(int baseTri, int count, int va, int vb)
            {
                for (int tri = baseTri; tri < baseTri + count; tri++)
                {
                    if (EdgeIndexUndirected(tri, va, vb) >= 0)
                        return tri;
                }
                throw new InvalidOperationException("FPConstrainedDelaunay: base edge not present after channel re-triangulation");
            }

            #endregion

            #region Extraction

            /// <summary>
            /// Circular-buffer deque for the 0-1 BFS. Replaces LinkedList&lt;int&gt;, whose
            /// per-node heap allocation made extraction the single largest allocator in the
            /// rebake path (~2.4MB of the 3.0MB Extract total
            /// at 22k tris) and which cannot be pooled. Push/pop semantics are unchanged, so
            /// the traversal order — and therefore the output — is identical.
            /// </summary>
            private struct IntDeque
            {
                private int[] _buf;
                private int _head;
                private int _count;

                /// <summary>
                /// Takes a rented buffer whose length must be a power of two (the pool
                /// guarantees it for this slot) — the mask arithmetic depends on it. Growth
                /// beyond the rented capacity allocates and is not written back to the pool;
                /// it is a safety valve, not the expected path (the seeded frontier of the
                /// 0-1 BFS stays well under the triangle count).
                /// </summary>
                internal IntDeque(int[] buffer)
                {
                    System.Diagnostics.Debug.Assert(
                        buffer.Length > 0 && (buffer.Length & (buffer.Length - 1)) == 0,
                        "IntDeque: capacity must be a power of two");
                    _buf = buffer;
                    _head = 0;
                    _count = 0;
                }

                internal int Count { get { return _count; } }

                internal void AddLast(int v)
                {
                    if (_count == _buf.Length)
                        Grow();
                    _buf[(_head + _count) & (_buf.Length - 1)] = v;
                    _count++;
                }

                internal void AddFirst(int v)
                {
                    if (_count == _buf.Length)
                        Grow();
                    _head = (_head - 1) & (_buf.Length - 1);
                    _buf[_head] = v;
                    _count++;
                }

                internal int RemoveFirst()
                {
                    int v = _buf[_head];
                    _head = (_head + 1) & (_buf.Length - 1);
                    _count--;
                    return v;
                }

                private void Grow()
                {
                    var next = new int[_buf.Length * 2];
                    int mask = _buf.Length - 1;
                    for (int i = 0; i < _count; i++)
                        next[i] = _buf[(_head + i) & mask];
                    _buf = next;
                    _head = 0;
                }
            }

            /// <summary>
            /// Extracts the kept triangles. The returned array is a POOL BUFFER and is only
            /// valid over <c>[0, count)</c> — <see cref="Array.Length"/> is capacity
            /// It also stays valid only until the next rebake on the
            /// same pool: the pool is slot-holding and has no return call, so the next rent
            /// hands out this same storage. Consume it before then; never store it.
            ///
            /// <see cref="Triangulate"/> is unaffected because it runs on
            /// <see cref="FPNavMeshRebakeBufferPool.NonPooling"/>, which allocates exact-size
            /// buffers — so there <c>Length == count</c> and the old array contract holds. That
            /// is an implicit dependency on the pool's sizing policy, pinned by a test.
            /// </summary>
            internal int[] Extract(bool eraseOuterAndHoles, out int count)
            {
                ExtractBegin(eraseOuterAndHoles);
                while (!ExtractStep(int.MaxValue)) { }
                return ExtractResult(out count);
            }

            // ── Resumable Extract. Same stages in the same order as the one-shot above; the
            // caller decides how many work units run per call. A unit is the natural item of the
            // phase — a triangle while labelling and while rotating, a deque pop while relaxing,
            // a kept triangle while emitting. The sort is one unit: it is a radix pass set whose
            // counters double as cursors, and at 0.2 ms on a 22k stage it is not the long pole.
            private enum ExPhase { InitDepth, Seed, Relax, MarkKeep, Triples, Sort, Emit, Done }

            private ExPhase _exPhase;
            private bool _exErase;
            private int _exCursor;
            private bool[] _exKeep;
            private int[] _exDepth;
            private IntDeque _exDeque;
            private Triple[] _exTriples, _exSorted;
            private int _exKept;
            private int[] _exResult;
            private int _exCount;

            /// <summary>Arms a resumable extract. Rents nothing until the first <see cref="ExtractStep"/>.</summary>
            internal void ExtractBegin(bool eraseOuterAndHoles)
            {
                _exErase = eraseOuterAndHoles;
                _exPhase = eraseOuterAndHoles ? ExPhase.InitDepth : ExPhase.MarkKeep;
                _exCursor = 0;
                _exKept = 0;
                _exResult = null;
                _exCount = 0;
            }

            /// <summary>Runs up to <paramref name="units"/> work units; true once the output is ready.</summary>
            internal bool ExtractStep(int units)
            {
                while (units > 0 && _exPhase != ExPhase.Done)
                {
                    switch (_exPhase)
                    {
                        case ExPhase.InitDepth:
                        {
                            if (_exCursor == 0)
                            {
                                _exKeep = ExtractBeginKeep();
                                _exDepth = ExtractBeginDepth();
                            }
                            // Clamp by remaining, not cursor + units: "finish it" is int.MaxValue
                            // and cursor + that overflows negative.
                            int end = units >= _triCount - _exCursor ? _triCount : _exCursor + units;
                            ExtractInitDepth(_exDepth, _exCursor, end);
                            units -= end - _exCursor;
                            _exCursor = end;
                            if (_exCursor >= _triCount)
                            {
                                _exCursor = 0;
                                _exDeque = ExtractBeginDeque();
                                _exPhase = ExPhase.Seed;
                            }
                            break;
                        }

                        case ExPhase.Seed:
                        {
                            int end = units >= _triCount - _exCursor ? _triCount : _exCursor + units;
                            ExtractSeedGhosts(_exDepth, ref _exDeque, _exCursor, end);
                            units -= end - _exCursor;
                            _exCursor = end;
                            if (_exCursor >= _triCount)
                            {
                                _exCursor = 0;
                                _exPhase = ExPhase.Relax;
                            }
                            break;
                        }

                        case ExPhase.Relax:
                        {
                            // Pops are the unit. Interrupting is safe because the loop converges
                            // to the unique shortest-distance labelling regardless of pop order.
                            bool drained = ExtractRelax(_exDepth, ref _exDeque, units);
                            units = 0;
                            if (drained)
                                _exPhase = ExPhase.MarkKeep;
                            break;
                        }

                        case ExPhase.MarkKeep:
                        {
                            if (_exCursor == 0 && !_exErase)
                                _exKeep = ExtractBeginKeep();
                            int end = units >= _triCount - _exCursor ? _triCount : _exCursor + units;
                            if (_exErase)
                                ExtractMarkKeepParity(_exKeep, _exDepth, _exCursor, end);
                            else
                                ExtractMarkKeepReal(_exKeep, _exCursor, end);
                            units -= end - _exCursor;
                            _exCursor = end;
                            if (_exCursor >= _triCount)
                            {
                                _exCursor = 0;
                                _exPhase = ExPhase.Triples;
                            }
                            break;
                        }

                        case ExPhase.Triples:
                        {
                            if (_exCursor == 0)
                            {
                                _exTriples = _pool.ExtractTriples(_triCount);
                                _exKept = 0;
                            }
                            int end = units >= _triCount - _exCursor ? _triCount : _exCursor + units;
                            ExtractRotateRange(_exKeep, _exTriples, _exCursor, end, ref _exKept);
                            units -= end - _exCursor;
                            _exCursor = end;
                            if (_exCursor >= _triCount)
                            {
                                _exCursor = 0;
                                _exPhase = ExPhase.Sort;
                            }
                            break;
                        }

                        case ExPhase.Sort:
                            _exSorted = SortTriples(_exTriples, _exKept);
                            units--;
                            _exPhase = ExPhase.Emit;
                            break;

                        case ExPhase.Emit:
                        {
                            if (_exCursor == 0)
                            {
                                _exCount = _exKept * 3;
                                _exResult = _pool.ExtractOutput(_exCount);
                            }
                            int end = units >= _exKept - _exCursor ? _exKept : _exCursor + units;
                            ExtractEmitRange(_exSorted, _exResult, _exCursor, end);
                            units -= end - _exCursor;
                            _exCursor = end;
                            if (_exCursor >= _exKept)
                            {
                                _exCursor = 0;
                                _exPhase = ExPhase.Done;
                            }
                            break;
                        }
                    }
                }
                return _exPhase == ExPhase.Done;
            }

            /// <summary>The extracted indices once <see cref="ExtractStep"/> has returned true.</summary>
            internal int[] ExtractResult(out int count)
            {
                count = _exCount;
                return _exResult;
            }

            /// <summary>
            /// Stage 3 of <see cref="Extract"/> — LSD radix sort of the canonical triples,
            /// ascending by (A, B, C). Replaces <see cref="Array.Sort{T}(T[],int,int)"/>, which
            /// measured 61% of Extract (1.52 ms of 2.49 ms on Field).
            ///
            /// <para>The key is a three-digit number whose digits are vertex indices, so the radix
            /// is the vertex count rather than a power of two and the sort is exactly three
            /// passes: C, then B, then A. Three is odd, so the result lands in the scratch buffer
            /// — it is returned rather than copied back.</para>
            ///
            /// <para><b>Scan direction must match counter direction</b> (forward scan with an
            /// exclusive prefix sum and <c>count[digit]++</c>) or ties reverse. Here that is a
            /// belt-and-braces property rather than the contract: equal keys are identical
            /// <see cref="Triple"/> values, so order among them cannot reach the output — the
            /// same reason <see cref="Array.Sort{T}(T[],int,int)"/>'s instability was safe. The
            /// discipline is kept anyway because the sibling
            /// <c>FPNavMeshBuildPipeline.RadixSortByKey</c> does depend on it, and a reader who
            /// copies this one should copy the correct form.</para>
            /// </summary>
            internal Triple[] SortTriples(Triple[] triples, int kept)
            {
                int buckets = _realCount;
                if (kept < 2 || buckets <= 0)
                    return triples;

                Triple[] scratch = _pool.ExtractTriplesScratch(kept);
                int[] counts = _pool.ExtractTripleCounts(3 * buckets);
                Array.Clear(counts, 0, 3 * buckets);

                int offA = 0, offB = buckets, offC = 2 * buckets;
                for (int i = 0; i < kept; i++)
                {
                    counts[offA + triples[i].A]++;
                    counts[offB + triples[i].B]++;
                    counts[offC + triples[i].C]++;
                }
                PrefixExclusive(counts, offA, buckets);
                PrefixExclusive(counts, offB, buckets);
                PrefixExclusive(counts, offC, buckets);

                for (int i = 0; i < kept; i++)          // pass 1: C (low digit)
                    scratch[counts[offC + triples[i].C]++] = triples[i];
                for (int i = 0; i < kept; i++)          // pass 2: B
                    triples[counts[offB + scratch[i].B]++] = scratch[i];
                for (int i = 0; i < kept; i++)          // pass 3: A (high digit)
                    scratch[counts[offA + triples[i].A]++] = triples[i];

                return scratch;
            }

            private static void PrefixExclusive(int[] counts, int off, int buckets)
            {
                int sum = 0;
                for (int b = 0; b < buckets; b++)
                {
                    int c = counts[off + b];
                    counts[off + b] = sum;
                    sum += c;
                }
            }

            /// <summary>
            /// Stage 1 of <see cref="Extract"/> — the parity keep mask. Split out because the
            /// stage costs can only be measured by difference: the loops read private state, so a
            /// test cannot repeat them in place. Calling the stages separately is a diagnostics
            /// path; the returned array is the pool's keep buffer and carries the same
            /// "valid until the next rebake" contract as <see cref="Extract"/>'s output.
            /// </summary>
            internal bool[] ExtractKeepMask(bool eraseOuterAndHoles)
            {
                bool[] keep = ExtractBeginKeep();
                if (eraseOuterAndHoles)
                {
                    int[] depth = ExtractBeginDepth();
                    ExtractInitDepth(depth, 0, _triCount);
                    IntDeque deque = ExtractBeginDeque();
                    ExtractSeedGhosts(depth, ref deque, 0, _triCount);
                    ExtractRelax(depth, ref deque, int.MaxValue);
                    ExtractMarkKeepParity(keep, depth, 0, _triCount);
                }
                else
                {
                    ExtractMarkKeepReal(keep, 0, _triCount);
                }
                return keep;
            }

            // ── Stage-1 pieces. Split so a caller can drive them a slice at a time; each takes an
            // explicit range (or a pop budget) and touches nothing outside it. The 0-1 BFS is safe
            // to interrupt for a reason worth stating: the relaxation runs to a FIXED POINT, and
            // that fixed point is the unique shortest-distance labelling — it does not depend on
            // pop order. Cutting it costs only the O(V+E) bound (it degrades toward SPFA), never
            // the result. The deque is a STRUCT whose Grow() allocates outside the pool, so it is
            // carried by value and never re-derived from the pool slot.

            /// <summary>Rents the keep mask (pre-cleared — the tail must read "do not keep").</summary>
            private bool[] ExtractBeginKeep() { return _pool.ExtractKeep(_triCount); }

            /// <summary>Rents the depth buffer.</summary>
            private int[] ExtractBeginDepth() { return _pool.ExtractDepth(_triCount); }

            /// <summary>Rents the deque backing store; the caller owns the struct.</summary>
            private IntDeque ExtractBeginDeque() { return new IntDeque(_pool.ExtractDeque(_triCount + 16)); }

            private void ExtractInitDepth(int[] depth, int from, int to)
            {
                for (int i = from; i < to; i++)
                    depth[i] = int.MaxValue;
            }

            /// <summary>Seeds the ghost-touching triangles at depth 0.</summary>
            private void ExtractSeedGhosts(int[] depth, ref IntDeque deque, int from, int to)
            {
                for (int i = from; i < to; i++)
                {
                    if (!_tris[i].Alive)
                        continue;
                    ref Tri t = ref _tris[i];
                    if (t.V0 >= _realCount || t.V1 >= _realCount || t.V2 >= _realCount)
                    {
                        depth[i] = 0;
                        deque.AddLast(i);
                    }
                }
            }

            /// <summary>
            /// Relaxes up to <paramref name="pops"/> deque entries. Returns true once the queue is
            /// empty, i.e. the labelling has reached its fixed point.
            /// </summary>
            private bool ExtractRelax(int[] depth, ref IntDeque deque, int pops)
            {
                while (pops > 0 && deque.Count > 0)
                {
                    int cur = deque.RemoveFirst();
                    ref Tri t = ref _tris[cur];
                    for (int k = 0; k < 3; k++)
                    {
                        int n = Nk(in t, k);
                        if (n < 0 || !_tris[n].Alive)
                            continue;
                        // CrossesParity, not the wall bit: an edge two rings share does not
                        // change walkability, so it must cost 0 here AND go to the front of
                        // the deque. Both uses have to move together or the 0-1 invariant
                        // (weight 1 => back) breaks and this degrades to SPFA.
                        bool crosses = CrossesParity(in t, k);
                        int nd = depth[cur] + (crosses ? 1 : 0);
                        if (nd < depth[n])
                        {
                            depth[n] = nd;
                            if (crosses)
                                deque.AddLast(n);
                            else
                                deque.AddFirst(n);
                        }
                    }
                    pops--;
                }
                return deque.Count == 0;
            }

            private void ExtractMarkKeepParity(bool[] keep, int[] depth, int from, int to)
            {
                for (int i = from; i < to; i++)
                    keep[i] = _tris[i].Alive && depth[i] != int.MaxValue && (depth[i] & 1) == 1;
            }

            private void ExtractMarkKeepReal(bool[] keep, int from, int to)
            {
                for (int i = from; i < to; i++)
                {
                    if (!_tris[i].Alive)
                        continue;
                    ref Tri t = ref _tris[i];
                    keep[i] = t.V0 < _realCount && t.V1 < _realCount && t.V2 < _realCount;
                }
            }

            /// <summary>
            /// Stage 2 of <see cref="Extract"/> — canonical rotation into the triple buffer.
            /// Returns the kept count; the buffer is UNSORTED on return, in triangle creation
            /// order. See <see cref="ExtractKeepMask"/> for the buffer lifetime contract.
            /// </summary>
            internal int ExtractTriples(bool[] keep, out Triple[] triples)
            {
                // Canonicalize: rotate min-vertex-first (winding preserved); the caller sorts.
                triples = _pool.ExtractTriples(_triCount);
                int kept = 0;
                ExtractRotateRange(keep, triples, 0, _triCount, ref kept);
                return kept;
            }

            /// <summary>
            /// Rotation over <c>[from, to)</c>. <paramref name="kept"/> is the running write
            /// position and must be carried across calls — it is the output cursor, not a count
            /// that can be re-derived from the range.
            /// </summary>
            private void ExtractRotateRange(bool[] keep, Triple[] triples, int from, int to, ref int kept)
            {
                for (int i = from; i < to; i++)
                {
                    if (!keep[i])
                        continue;
                    ref Tri t = ref _tris[i];
                    int a = t.V0, b = t.V1, c = t.V2;
                    while (!(a < b && a < c))
                    {
                        int tmp = a; a = b; b = c; c = tmp; // cyclic rotation keeps CCW
                    }
                    triples[kept++] = new Triple(a, b, c);
                }
            }

            /// <summary>
            /// Stage 4 of <see cref="Extract"/> — flatten the sorted triples into the output
            /// buffer. See <see cref="ExtractKeepMask"/> for the buffer lifetime contract.
            /// </summary>
            internal int[] ExtractEmit(Triple[] triples, int kept, out int count)
            {
                // Rented, not allocated: the triangle count now travels
                // as an explicit out parameter instead of being read off the array's Length, so
                // the buffer may be larger than the content.
                count = kept * 3;
                int[] result = _pool.ExtractOutput(count);
                ExtractEmitRange(triples, result, 0, kept);
                return result;
            }

            /// <summary>Flattens sorted triples <c>[from, to)</c> into the output buffer.</summary>
            private void ExtractEmitRange(Triple[] triples, int[] result, int from, int to)
            {
                for (int i = from; i < to; i++)
                {
                    result[i * 3] = triples[i].A;
                    result[i * 3 + 1] = triples[i].B;
                    result[i * 3 + 2] = triples[i].C;
                }
            }

            #endregion

            #region Debug self-check

            [System.Diagnostics.Conditional("DEBUG")]
            internal void SelfCheck()
            {
                for (int i = 0; i < _triCount; i++)
                {
                    if (!_tris[i].Alive)
                        continue;
                    ref Tri t = ref _tris[i];
                    System.Diagnostics.Debug.Assert(O(t.V0, t.V1, t.V2) > 0,
                        $"SelfCheck: triangle {i} not CCW");
                    for (int k = 0; k < 3; k++)
                    {
                        int n = Nk(in t, k);
                        if (n < 0)
                            continue;
                        System.Diagnostics.Debug.Assert(_tris[n].Alive, $"SelfCheck: dead neighbor at {i}.{k}");
                        int va = Vk(in t, k);
                        int vb = Vk(in t, (k + 1) % 3);
                        int back = EdgeIndexUndirected(n, va, vb);
                        System.Diagnostics.Debug.Assert(back >= 0, $"SelfCheck: neighbor {n} lost edge of {i}");
                        System.Diagnostics.Debug.Assert(Nk(in _tris[n], back) == i,
                            $"SelfCheck: asymmetric adjacency {i}<->{n}");
                        // BOTH bits. Catches a propagation site that carried one and dropped the
                        // other — but only when the loss is one-sided; a bit dropped identically
                        // on both triangles is symmetric and passes here.
                        System.Diagnostics.Debug.Assert(Mk(in _tris[n], back).Same(Mk(in t, k)),
                            $"SelfCheck: asymmetric edge mark {i}<->{n}");
                    }
                }
            }

            #endregion
        }
    }
}
