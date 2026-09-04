using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// The set of building footprints a game can place.
    ///
    /// A footprint is not arbitrary geometry — it is an entry in this table, and the table holds
    /// EXACT INTEGER vertex offsets on the predicate snap grid. Nothing rotates or scales at
    /// runtime, so there is no sin/cos and no snapping, and therefore no snapping error. More
    /// precisely: the integers ARE the shape. A "square rotated 30 degrees" comes out with edges
    /// of slightly unequal length, and that is simply what that entry is; it only reads as an
    /// error if you insist the ideal rotated square is the ground truth.
    ///
    /// Offsets are measured FROM THE SHAPE CENTRE, not from a corner. That is what lets the
    /// symmetry invariants below be written as `v + v_opposite = 0` instead of something that
    /// depends on where the anchor sits — and the anchor is a wire concern (which corner the game
    /// sends), not a geometry one.
    ///
    /// DETERMINISM. <see cref="Hash"/> exists because this table is inside the envelope: a
    /// SizeIndex is a reference, so if two peers or two builds disagree about the table, the same
    /// component means a different shape. Unlike a layout break, nothing fails loudly — the peers
    /// quietly carve different navmeshes and only diverge at the nav fingerprint. Carry the hash in
    /// the match config (or fold it into StaticFingerprint) so the mismatch surfaces at load.
    ///
    /// NOTE the hash covers the whole table, so adding an entry invalidates every recording made
    /// before. That is a recurring cost, not a one-off; when it starts to hurt, hash only the
    /// entries a match actually references, or give entries stable ids so appending does not move
    /// the existing ones.
    /// </summary>
    public sealed class FPBuildingShapeCatalog
    {
        private readonly long[] _offX;
        private readonly long[] _offZ;
        private readonly int[] _entryStart;
        private readonly int[] _shapeFirstEntry;

        /// <summary>Number of footprints in the table — one per (shape, orientation) pair.</summary>
        public int EntryCount => _entryStart.Length - 1;

        /// <summary>
        /// Vertices in the widest entry — what a buffer must reserve to hold ANY one footprint from
        /// this table.
        ///
        /// <para>Exists for the placement preview, which keeps room for one unplaced building at
        /// the end of a buffer sized by a rebake. That rebake may have carved nothing but
        /// rectangles while the ghost under the cursor is a hexagon, so the headroom cannot be read
        /// off what is already there.</para>
        ///
        /// <para>Walked once and kept — the table is immutable and per-stage.</para>
        /// </summary>
        public int MaxVertexCount
        {
            get
            {
                if (_maxVertexCount == 0)
                {
                    int max = 0;
                    for (int e = 0; e < EntryCount; e++)
                    {
                        int n = _entryStart[e + 1] - _entryStart[e];
                        if (n > max) max = n;
                    }
                    _maxVertexCount = max;
                }
                return _maxVertexCount;
            }
        }
        private int _maxVertexCount;

        /// <summary>
        /// Number of SHAPES. A shape owns a contiguous run of entries, one per orientation, so
        /// this is what a game's "which building is this" id counts.
        /// </summary>
        public int ShapeCount => _shapeFirstEntry.Length - 1;

        /// <summary>How many orientations <paramref name="shape"/> offers. 1 for a shape that does
        /// not turn.</summary>
        public int DirectionCount(int shape) => _shapeFirstEntry[shape + 1] - _shapeFirstEntry[shape];

        /// <summary>
        /// Resolves (shape, orientation) to the flat entry index, or -1 when either is out of
        /// range.
        ///
        /// <para>The range check is the point. With a flat index a game writes
        /// <c>largeBox + orientation</c>, and an orientation one past the end is still a VALID
        /// index — it addresses the next shape in the table. The failure is then "the wrong
        /// building appeared", with nothing thrown and nothing logged. Splitting the two and
        /// bounding the orientation turns that into a refusal that names the cause.</para>
        /// </summary>
        public int TryResolveEntry(int shape, int orientation)
        {
            if ((uint)shape >= (uint)ShapeCount)
                return -1;
            int first = _shapeFirstEntry[shape];
            if ((uint)orientation >= (uint)(_shapeFirstEntry[shape + 1] - first))
                return -1;
            return first + orientation;
        }

        /// <summary>
        /// Identity of the table's contents, for the determinism envelope. Two catalogs with the
        /// same hash describe the same geometry.
        /// </summary>
        public ulong Hash { get; }

        /// <summary>
        /// Builds and validates the table. Offsets are CSR-packed: entry e owns
        /// [entryStart[e], entryStart[e+1]), CCW, in snap units relative to the shape centre.
        ///
        /// Throws on a malformed entry rather than accepting it — a broken footprint would
        /// otherwise reach the rebaker as a placement failure, which is a different thing and
        /// would send whoever is debugging it to the wrong place.
        ///
        /// <paramref name="shapeFirstEntry"/> groups entries into shapes: shape s owns entries
        /// [shapeFirstEntry[s], shapeFirstEntry[s+1]), one per orientation. Null means every entry
        /// is its own shape with a single orientation, which is what a hand-built table wants.
        /// </summary>
        public FPBuildingShapeCatalog(long[] offX, long[] offZ, int[] entryStart, int[] shapeFirstEntry = null)
        {
            if (offX == null || offZ == null || entryStart == null)
                throw new ArgumentException("FPBuildingShapeCatalog: null input");
            if (offX.Length != offZ.Length)
                throw new ArgumentException("FPBuildingShapeCatalog: offset arrays differ in length");
            if (entryStart.Length < 2 || entryStart[0] != 0 || entryStart[entryStart.Length - 1] != offX.Length)
                throw new ArgumentException("FPBuildingShapeCatalog: entryStart is not a valid CSR index");

            // Ascending, which the three endpoint conditions above do not imply — they say nothing
            // about the values in between. shapeFirstEntry below has always been checked this way;
            // entryStart was not, and the difference showed up as ValidateEntry blaming the SHAPE
            // for a broken INDEX: [0,6,3,8] reported "entry 0 is not strictly convex" about a
            // perfectly good square, [0,-3,8] reported "entry 0 has -3 vertices", and [0,12,8] read
            // off the end of the offsets with an IndexOutOfRangeException. Whoever got one of those
            // would go looking at their polygon.
            //
            // Ascending is the whole check: the last element is pinned to offX.Length above, so
            // every earlier one is then necessarily inside the array too.
            for (int i = 1; i < entryStart.Length; i++)
            {
                if (entryStart[i] <= entryStart[i - 1])
                    throw new ArgumentException(
                        $"FPBuildingShapeCatalog: entryStart is not ascending at {i} "
                        + $"({entryStart[i - 1]} then {entryStart[i]}) — the CSR index is malformed, "
                        + "which is not a problem with any shape's vertices");
            }

            _offX = (long[])offX.Clone();
            _offZ = (long[])offZ.Clone();
            _entryStart = (int[])entryStart.Clone();

            if (shapeFirstEntry == null)
            {
                _shapeFirstEntry = new int[_entryStart.Length];
                for (int i = 0; i < _shapeFirstEntry.Length; i++)
                    _shapeFirstEntry[i] = i;
            }
            else
            {
                if (shapeFirstEntry.Length < 2 || shapeFirstEntry[0] != 0
                    || shapeFirstEntry[shapeFirstEntry.Length - 1] != EntryCount)
                    throw new ArgumentException("FPBuildingShapeCatalog: shapeFirstEntry is not a valid group index");
                for (int i = 1; i < shapeFirstEntry.Length; i++)
                {
                    if (shapeFirstEntry[i] <= shapeFirstEntry[i - 1])
                        throw new ArgumentException($"FPBuildingShapeCatalog: shape {i - 1} owns no entries");
                }
                _shapeFirstEntry = (int[])shapeFirstEntry.Clone();
            }

            for (int e = 0; e < EntryCount; e++)
                ValidateEntry(e);

            Hash = ComputeHash(_offX, _offZ, _entryStart, _shapeFirstEntry);
        }

        internal long[] OffsetsX => _offX;
        internal long[] OffsetsZ => _offZ;
        internal int[] EntryStart => _entryStart;

        public int VertexCount(int entry) => _entryStart[entry + 1] - _entryStart[entry];

        private void ValidateEntry(int e)
        {
            int s = _entryStart[e], t = _entryStart[e + 1];
            int n = t - s;
            if (n < 3)
                throw new ArgumentException($"FPBuildingShapeCatalog: entry {e} has {n} vertices");

            long m = FPGeoPredicates.MAX_SNAPPED_COORD;
            for (int i = s; i < t; i++)
            {
                if (_offX[i] < -m || _offX[i] > m || _offZ[i] < -m || _offZ[i] > m)
                    throw new ArgumentException($"FPBuildingShapeCatalog: entry {e} vertex {i - s} outside the snapped domain");
            }

            for (int i = 0; i < n; i++)
            {
                int a = s + i, b = s + (i + 1) % n, c = s + (i + 2) % n;
                if (_offX[a] == _offX[b] && _offZ[a] == _offZ[b])
                    throw new ArgumentException($"FPBuildingShapeCatalog: entry {e} has a duplicate vertex at {i}");
                if (FPGeoPredicates.Orient2D(_offX[a], _offZ[a], _offX[b], _offZ[b], _offX[c], _offZ[c]) <= 0)
                    throw new ArgumentException(
                        $"FPBuildingShapeCatalog: entry {e} is not strictly convex and CCW at vertex {(i + 1) % n}");
            }

            // The loop above is LOCAL: it says every vertex turns left, which on a closed polygon
            // only makes the turning number a positive integer. Convex is turning number exactly 1;
            // a self-intersecting star polygon turns 2 or more and passes every triple. A pentagram
            // did pass, and nothing downstream caught it either — FPConvexOffset.Expand returned
            // true and Validate reported no reason — so the first complaint came from the CDT at
            // rebake time, as "constraint crosses an existing constraint", which names neither the
            // catalog nor the shape.
            //
            // Turning number, in exact integers: walk the edge direction vectors and count how many
            // times the sweep crosses the +x axis upward. A CCW convex polygon sweeps 360 degrees
            // and crosses once; the pentagram sweeps 720 and crosses twice. No floating point,
            // because these offsets reach the determinism envelope through Hash.
            int upwardCrossings = 0;
            for (int i = 0; i < n; i++)
            {
                int a = s + i, b = s + (i + 1) % n, c = s + (i + 2) % n;
                bool belowNow = IsBelowPositiveXAxis(_offX[b] - _offX[a], _offZ[b] - _offZ[a]);
                bool belowNext = IsBelowPositiveXAxis(_offX[c] - _offX[b], _offZ[c] - _offZ[b]);
                if (belowNow && !belowNext)
                    upwardCrossings++;
            }
            if (upwardCrossings != 1)
                throw new ArgumentException(
                    $"FPBuildingShapeCatalog: entry {e} is locally convex but self-intersecting — its "
                    + $"edges wind around {upwardCrossings} times instead of once (a star polygon). "
                    + "Every vertex turns left, so the per-vertex check cannot see this; the shape "
                    + "would be accepted here and fail later inside the CDT with an unrelated message.");
        }

        /// <summary>
        /// Half-plane side of a direction vector, with the +x axis itself counted as above so the
        /// split is a clean cut and every direction lands on exactly one side.
        /// </summary>
        private static bool IsBelowPositiveXAxis(long dx, long dz)
        {
            return dz < 0 || (dz == 0 && dx < 0);
        }

        /// <summary>
        /// Builds the offsets of a hexagon that TILES — the round footprint that can still be
        /// packed. CCW, centred on the origin; pass the result straight to the constructor as one
        /// entry.
        ///
        /// <para><b>Size.</b> <paramref name="circumradius"/> is the radius of the circle this
        /// approximates: the six vertices sit on it. In snap units, so 1024 is one world unit.
        /// The rest follows from it:</para>
        ///
        /// <list type="bullet">
        ///   <item><description>width across the points = <c>2 * circumradius</c></description></item>
        ///   <item><description>depth across the flats = <c>circumradius * sqrt(3)</c>, about 0.866 of the width</description></item>
        ///   <item><description>inradius (centre to a flat edge) = <c>circumradius * sqrt(3)/2</c></description></item>
        /// </list>
        ///
        /// <para>The vertices are <c>(+-2a, 0)</c> and <c>(+-a, +-b)</c> with <c>a = circumradius/2</c>
        /// and <c>b = round(a*sqrt(3))</c>, so the realised circumradius is the requested one
        /// rounded down to an even number of snap units — a granularity of 1/512 world unit, which
        /// is finer than anything a footprint cares about.</para>
        ///
        /// <para><b>Why this exists rather than "compute six vertices and snap them".</b> A regular
        /// hexagon has no integer form, so it always arrives through a rounding, and WHICH rounding
        /// decides whether it can be packed — see <see cref="IsCentrallySymmetric"/>. The engine's
        /// own Snap is floor, which breaks the cancellation, so the obvious construction produces a
        /// hexagon that carves perfectly and can never be tiled. That failure only shows up when
        /// someone tries to build wall to wall, which may be long after the shape was authored.</para>
        ///
        /// <para><b>b = round(a*sqrt(3)) is the choice, computed in integers.</b> Equal edges need
        /// exactly b = a*sqrt(3), so rounding to the grid is the whole error: measured at a = 1
        /// world unit it leaves the two edge lengths 0.016% apart, against 0.775% for the b/a = 7/4
        /// that reads nicer as a decimal. The accuracy is free, and the catalog is code rather than
        /// something a designer types, so legibility does not buy anything here.</para>
        ///
        /// <para><b>Why the square root is taken the long way.</b> This value reaches the
        /// determinism envelope through <see cref="Hash"/>, so it has to come out identical on
        /// every peer. <c>FPConvexOffset.CeilSqrt</c> gives the exact integer root, and it gets
        /// there by seeding with <c>Math.Sqrt</c> and then walking to the smallest <c>s</c> with
        /// <c>s*s &gt;= n</c> — so the seed is a starting guess and cannot influence the answer.
        /// Floating point IS therefore involved, contrary to what this note used to claim; the
        /// exactness comes from the correction walk, not from its absence. Worth stating
        /// accurately: "no floating point here" is exactly the sentence that stops the next person
        /// auditing a routine that does have some.</para>
        ///
        /// </summary>
        /// <param name="circumradius">Radius of the approximated circle, in snap units (1024 = one world unit).</param>
        public static void HexagonOffsets(long circumradius, out long[] offX, out long[] offZ)
        {
            if (circumradius < 2)
                throw new ArgumentException(
                    "FPBuildingShapeCatalog.HexagonOffsets: circumradius must be at least 2 snap units");
            long m = FPGeoPredicates.MAX_SNAPPED_COORD;
            if (circumradius > m)
                throw new ArgumentException(
                    "FPBuildingShapeCatalog.HexagonOffsets: circumradius is too large for the snapped domain");

            long a = circumradius / 2;

            // round(a*sqrt(3)) = round(sqrt(3*a*a)): take the floor root and step up when the
            // remainder puts the true root past the halfway point. Exact — CeilSqrt walks its
            // Math.Sqrt seed to the true root, and everything after it is integer arithmetic.
            long n = 3 * a * a;
            long s = FPConvexOffset.CeilSqrt(n);
            if (s * s > n) s--;                       // floor root
            if (n - s * s > s) s++;                   // sqrt(n) - s > 0.5
            long b = s;

            offX = new[] { 2 * a, a, -a, -2 * a, -a, a };
            offZ = new[] { 0L, b, b, 0L, -b, -b };
        }

        /// <summary>
        /// Builds a quantized oriented box: <paramref name="directions"/> entries, entry k being
        /// the box turned by k * 360/M degrees, CCW and centred on the origin. Returns a full CSR
        /// triple ready for the constructor; an "orientationIndex" is then just an offset into
        /// these entries.
        ///
        /// <para><b>M must be a multiple of 4, and the four-fold return is exact by construction.</b>
        /// Players expect four turns to come back to where they started, and "almost" is not good
        /// enough — a box that drifts by one snap unit per turn stops being placeable against the
        /// neighbour it was flush with. Only the first quarter of the directions is derived from
        /// trigonometry; the rest come from the previous quarter by the integer rotation
        /// (x, z) -> (-z, x), so advancing by M/4 four times applies that map four times and lands
        /// on exactly the original integers. Nothing accumulates.</para>
        ///
        /// <para><b>Determinism.</b> The seed quarter uses FP64 trigonometry, never
        /// <c>System.Math</c> — FP64.Sin/Cos are a LUT plus fixed-point interpolation, so every
        /// build and every peer gets the same integers. Rounding is round-half-away-from-zero into
        /// snap units, and only two corners are rounded: the opposite pair is NEGATED, which makes
        /// central symmetry exact rather than probable (an independently rounded opposite corner
        /// would sometimes land a unit off and quietly cost the entry its tiling delta).</para>
        ///
        /// <para><b>A turned box is not the ideal turned box, and that is fine</b> — its edges come
        /// out slightly unequal because the integers ARE the shape (see the class remarks). What is
        /// NOT fine is the entry failing to validate, and thin boxes are where that happens: at
        /// high aspect ratio the short edge is short in absolute terms, and a short edge can round
        /// to collinear. The constructor below refuses those, so an unusable aspect ratio fails at
        /// load with a message instead of carving something subtly wrong.</para>
        ///
        /// <param name="halfWidth">Half extent along the box's own long axis, in snap units.</param>
        /// <param name="halfDepth">Half extent across it, in snap units.</param>
        /// <param name="directions">
        /// How many steps divide a FULL circle, so entry k sits at k * 360/M degrees. Must be
        /// positive and a multiple of 4 (that is what makes four quarter-turns exact).
        ///
        /// Note the box is symmetric under a half turn, so entry k and entry k + M/2 are the same
        /// footprint reached from the other side — M steps offer M/2 shapes a player can tell
        /// apart. Harmless for a rotate button, surprising for a palette of M previews.
        /// </param>
        /// </summary>
        public static void ObbOffsets(
            long halfWidth, long halfDepth, int directions,
            out long[] offX, out long[] offZ, out int[] entryStart)
        {
            if (halfWidth <= 0 || halfDepth <= 0)
                throw new ArgumentException("FPBuildingShapeCatalog.ObbOffsets: half extents must be positive");
            if (directions <= 0 || (directions & 3) != 0)
                throw new ArgumentException(
                    "FPBuildingShapeCatalog.ObbOffsets: directions must be a positive multiple of 4 "
                    + "so that four turns return exactly to the start");
            long m = FPGeoPredicates.MAX_SNAPPED_COORD;
            if (halfWidth > m / 2 || halfDepth > m / 2)
                throw new ArgumentException("FPBuildingShapeCatalog.ObbOffsets: half extents outside the snapped domain");

            int quarter = directions / 4;
            offX = new long[directions * 4];
            offZ = new long[directions * 4];
            entryStart = new int[directions + 1];
            for (int e = 0; e <= directions; e++)
                entryStart[e] = e * 4;

            // Seed quarter: angles k * 2pi/M for k in [0, M/4). Corners are +-hw*u +- hd*w with
            // u = (cos, sin) and w = (-sin, cos); only two are computed, the other two negated.
            for (int k = 0; k < quarter; k++)
            {
                FP64 ang = FP64.TwoPi * FP64.FromInt(k) / FP64.FromInt(directions);
                FP64 c = FP64.Cos(ang), s = FP64.Sin(ang);
                FP64 hw = FP64.FromRaw(halfWidth << (FP64.FRACTIONAL_BITS - FPGeoPredicates.SNAP_FRAC_BITS));
                FP64 hd = FP64.FromRaw(halfDepth << (FP64.FRACTIONAL_BITS - FPGeoPredicates.SNAP_FRAC_BITS));

                long c0x = RoundToSnap(hw * c - hd * s), c0z = RoundToSnap(hw * s + hd * c);
                long c1x = RoundToSnap(-hw * c - hd * s), c1z = RoundToSnap(-hw * s + hd * c);

                int b = k * 4;
                offX[b + 0] = c0x; offZ[b + 0] = c0z;
                offX[b + 1] = c1x; offZ[b + 1] = c1z;
                offX[b + 2] = -c0x; offZ[b + 2] = -c0z;
                offX[b + 3] = -c1x; offZ[b + 3] = -c1z;
            }

            // Remaining three quarters: exact integer 90-degree rotation of the quarter before.
            // Determinant +1, so convexity and CCW winding carry over untouched.
            for (int k = quarter; k < directions; k++)
            {
                int src = (k - quarter) * 4, dst = k * 4;
                for (int i = 0; i < 4; i++)
                {
                    offX[dst + i] = -offZ[src + i];
                    offZ[dst + i] = offX[src + i];
                }
            }
        }

        /// <summary>Round-half-away-from-zero from FP64 into snap units. Unlike
        /// <see cref="FPGeoPredicates.Snap"/> (an arithmetic shift, i.e. floor) this is symmetric
        /// about zero, which is what lets a shape keep its central symmetry.</summary>
        private static long RoundToSnap(FP64 v)
        {
            const int shift = FP64.FRACTIONAL_BITS - FPGeoPredicates.SNAP_FRAC_BITS;
            long raw = v.RawValue;
            long half = 1L << (shift - 1);
            return raw >= 0 ? (raw + half) >> shift : -((-raw + half) >> shift);
        }

        /// <summary>
        /// True when the shape can fill the plane by translation alone — the property "build these
        /// wall to wall with no gaps" actually needs.
        ///
        /// <para>Central symmetry is necessary but NOT sufficient, and the difference is a real
        /// trap: a convex polygon tiles by translation only if it is a parallelogram or a
        /// centrally symmetric HEXAGON. An octagon or a 16-gon passes
        /// <see cref="IsCentrallySymmetric"/> and <see cref="FPBuildingShapeExpansion.TryTilingDelta"/>
        /// still hands out a per-edge delta — placing ONE neighbour across one edge is genuinely
        /// flush — but the lattice those deltas generate is over-dense, so building outward in two
        /// directions produces overlap rather than a honeycomb. Measured: the footprint-to-cell
        /// area ratio is 1.00 at N = 4 and N = 6, and 1.17 / 1.61 / 2.08 at N = 8 / 12 / 16.</para>
        ///
        /// <para>So a "round" building of 8+ sides is a different feature from a hexagon, not a
        /// smoother version of one: it can be placed and carved, it just cannot be packed.</para>
        /// </summary>
        public bool TilesThePlane(int shape, int orientation)
        {
            int entry = TryResolveEntry(shape, orientation);
            if (entry < 0)
                return false;
            int n = VertexCount(entry);
            return (n == 4 || n == 6) && IsCentrallySymmetricEntry(entry);
        }

        /// <summary>
        /// True when opposite vertices cancel — the shape is symmetric about its centre.
        ///
        /// This is what makes every edge of the shape offerable as a flush contact (see
        /// <see cref="FPBuildingShapeExpansion.TryTilingDelta"/>). It is NOT on its own enough to
        /// fill the plane — for that see <see cref="TilesThePlane"/>.
        ///
        /// A regular hexagon has no integer form — its vertices are R*(+-1,0) and
        /// R*(+-1/2, +-sqrt(3)/2) — so it always arrives through a rounding, and WHICH rounding
        /// decides whether it survives. Measured: symmetric rounding keeps opposite vertices
        /// cancelling and the shape still tiles (with slightly unequal edges, which is
        /// unavoidable); FPGeoPredicates.Snap is an arithmetic shift, i.e. FLOOR, which is not
        /// symmetric, and the cancellation is gone. So the obvious path — compute the vertices in
        /// FP64 and let Snap convert them — is exactly the one that loses tiling.
        ///
        /// Hence integers in the table and this check, rather than trusting the conversion. The
        /// family that is symmetric by construction is (+-2a, 0), (+-a, +-b), which looks like a
        /// regular hexagon when b/a is near sqrt(3) — <see cref="HexagonOffsets"/> builds it.
        ///
        /// The cost of any integer hexagon, worth knowing before building a rotate button: it is
        /// symmetric under 180 degrees but NOT under 60, and no integer hexagon is.
        /// </summary>
        public bool IsCentrallySymmetric(int shape, int orientation)
        {
            int entry = TryResolveEntry(shape, orientation);
            return entry >= 0 && IsCentrallySymmetricEntry(entry);
        }

        internal bool IsCentrallySymmetricEntry(int entry)
        {
            int s = _entryStart[entry], n = VertexCount(entry);
            if ((n & 1) != 0)
                return false;
            int half = n / 2;
            for (int i = 0; i < half; i++)
            {
                if (_offX[s + i] + _offX[s + i + half] != 0) return false;
                if (_offZ[s + i] + _offZ[s + i + half] != 0) return false;
            }
            return true;
        }

        private static ulong ComputeHash(long[] x, long[] z, int[] start, int[] shapeFirstEntry)
        {
            // FNV-1a over the exact bytes that define the geometry. Order matters and is fixed.
            //
            // BYTE at a time, which is FNV-1a as specified and is why this differs from both
            // FPHash.Hash (one 64-bit round) and FPNavMeshRebaker.ComputeFingerprint (two rounds,
            // low 64 then high 32). Three folds, three shapes, on purpose: what they have in
            // common is the constants, not the algorithm.
            //
            // This value is inside the determinism envelope — the class note above says to carry
            // it in the match config so a table mismatch surfaces at load — so it cannot be
            // changed to match one of the others without invalidating every build that already
            // shipped a config carrying it.
            ulong h = FPHash.FNV_OFFSET;
            void Mix(long v)
            {
                for (int b = 0; b < 8; b++)
                    h = (h ^ (ulong)((v >> (b * 8)) & 0xFF)) * FPHash.FNV_PRIME;
            }
            Mix(start.Length);
            foreach (int s in start) Mix(s);
            // The grouping is part of the table's identity, not a view of it: the same geometry
            // split into 2 shapes of 16 vs 1 shape of 32 gives (shape, orientation) different
            // meanings, so a placement recorded against one is wrong against the other.
            Mix(shapeFirstEntry.Length);
            foreach (int s in shapeFirstEntry) Mix(s);
            for (int i = 0; i < x.Length; i++) { Mix(x[i]); Mix(z[i]); }
            return h;
        }
    }

    /// <summary>
    /// Assembles a <see cref="FPBuildingShapeCatalog"/> from several shapes — the normal way to
    /// build one, because a real game wants more than one size and more than one type in the same
    /// table and there is only ever ONE table per stage.
    ///
    /// <para>Each <c>Add…</c> returns a SHAPE id, which is what the game stores. A shape owns one
    /// entry per orientation internally, but that is the catalog's bookkeeping — a placement names
    /// the shape and the orientation separately and the catalog resolves them, so a game never does
    /// index arithmetic and an out-of-range orientation is refused instead of silently addressing
    /// the next shape.</para>
    ///
    /// <para><b>Call order defines the shape ids, and the ids are inside the determinism
    /// envelope.</b> A placement names a shape by number, so reordering the calls silently
    /// repoints every stored placement — and changes <see cref="FPBuildingShapeCatalog.Hash"/>,
    /// which is the thing that catches it. Build the table from straight-line code, never from a
    /// loop over something whose order can vary (a dictionary, a scanned directory, a config file
    /// read in file-system order).</para>
    ///
    /// <example><code>
    /// var b = new FPBuildingShapeCatalogBuilder();
    /// int smallBox  = b.AddObb(512, 512, directions: 16);
    /// int largeBox  = b.AddObb(2048, 1024, directions: 16);
    /// int smallDisc = b.AddHexagon(1024);
    /// int largeDisc = b.AddHexagon(3072);
    /// FPBuildingShapeCatalog catalog = b.Build();          // 4 shapes, 34 entries
    ///
    /// // placing the large box turned two steps:
    /// new FPBuildingPlacement(largeBox, orientation: 2, centreX, centreZ, y);
    /// </code></example>
    /// </summary>
    public sealed class FPBuildingShapeCatalogBuilder
    {
        private readonly List<long> _x = new List<long>();
        private readonly List<long> _z = new List<long>();
        private readonly List<int> _start = new List<int> { 0 };
        private readonly List<int> _shapeFirst = new List<int> { 0 };

        /// <summary>Entries appended so far (one per shape-and-orientation pair).</summary>
        public int EntryCount => _start.Count - 1;

        /// <summary>Shapes appended so far. This is what the <c>Add…</c> methods return.</summary>
        public int ShapeCount => _shapeFirst.Count - 1;

        /// <summary>
        /// Appends one shape from explicit offsets — CCW, strictly convex, in snap units measured
        /// from the shape's centre. Returns its shape id; it has a single orientation (0).
        /// </summary>
        public int Add(long[] offX, long[] offZ)
        {
            if (offX == null || offZ == null || offX.Length != offZ.Length)
                throw new ArgumentException("FPBuildingShapeCatalogBuilder: offset arrays must be non-null and equal length");
            int shape = ShapeCount;
            _x.AddRange(offX);
            _z.AddRange(offZ);
            _start.Add(_x.Count);
            _shapeFirst.Add(EntryCount);
            return shape;
        }

        /// <summary>
        /// Appends an oriented box in <paramref name="directions"/> orientations and returns its
        /// shape id. Orientations are then <c>0 .. directions-1</c> on that shape.
        ///
        /// <para><paramref name="directions"/> divides a full circle and must be a multiple of 4;
        /// because a box is symmetric under a half turn, the entries offer <c>directions/2</c>
        /// visually distinct footprints. See <see cref="FPBuildingShapeCatalog.ObbOffsets"/>.</para>
        /// </summary>
        public int AddObb(long halfWidth, long halfDepth, int directions)
        {
            FPBuildingShapeCatalog.ObbOffsets(
                halfWidth, halfDepth, directions, out long[] x, out long[] z, out int[] start);
            int shape = ShapeCount;
            _x.AddRange(x);
            _z.AddRange(z);
            int baseOffset = _start[_start.Count - 1];
            for (int e = 1; e < start.Length; e++)
                _start.Add(baseOffset + start[e]);
            _shapeFirst.Add(EntryCount);
            return shape;
        }

        /// <summary>
        /// Appends a hexagon — the round footprint that can still be packed — and returns its shape
        /// id. It has a single orientation (0): no integer hexagon is symmetric under 60 degrees,
        /// so the turns a rotate button would offer do not exist.
        /// </summary>
        /// <param name="circumradius">
        /// Radius of the approximated circle, in snap units (1024 = one world unit). The six
        /// vertices sit on it, so the shape is <c>2 * circumradius</c> across the points and about
        /// 0.866 of that across the flats. See <see cref="FPBuildingShapeCatalog.HexagonOffsets"/>.
        /// </param>
        public int AddHexagon(long circumradius)
        {
            FPBuildingShapeCatalog.HexagonOffsets(circumradius, out long[] x, out long[] z);
            return Add(x, z);
        }

        /// <summary>Validates every entry and produces the table. Throws on a malformed shape.</summary>
        public FPBuildingShapeCatalog Build()
        {
            if (EntryCount == 0)
                throw new ArgumentException("FPBuildingShapeCatalogBuilder: no entries were added");
            return new FPBuildingShapeCatalog(
                _x.ToArray(), _z.ToArray(), _start.ToArray(), _shapeFirst.ToArray());
        }
    }

    /// <summary>
    /// One building placed from the catalog: which shape, which turn of it, and where its CENTRE
    /// sits.
    ///
    /// <para><b>The shape and the turn are separate on purpose.</b> A flat entry index would let a
    /// game write <c>largeBox + orientation</c>, and an orientation one past the end is then still
    /// a VALID index — it addresses the NEXT shape in the table. Nothing throws and nothing logs;
    /// the wrong building simply appears. Kept apart, the catalog bounds the orientation against
    /// that shape's own direction count and refuses by name.</para>
    ///
    /// The centre, not a corner. Offsets are stored about the centre (see
    /// <see cref="FPBuildingShapeCatalog"/>) so that the symmetry invariants are independent of
    /// where the game chose to anchor, and a centre is also stable under rotation — an anchor at
    /// a bounding-box corner moves when the shape turns, which would make "rotate in place" a
    /// position change too.
    ///
    /// <para><b>The centre must be on the placement grid</b>
    /// (<see cref="FPGeoPredicates.IsOnGrid"/>), and the rebaker refuses it by name if it is not.
    /// The offsets are integers about the centre, so an off-grid centre would put every vertex
    /// off-grid and two shapes meant to sit flush would stop lining up.</para>
    ///
    /// <para><b>WHERE the game quantises matters more than that it does.</b> Quantise BEFORE the
    /// position becomes the authoritative record — the command payload, the stored component —
    /// not on the way into the rebaker. Doing it later leaves two answers to "where is this
    /// building": the raw value in the payload and state hash, and the quantised one the navmesh
    /// was carved from. Both are internally consistent, so nothing catches the split — the state
    /// hash agrees across peers, the nav fingerprint agrees across peers, and the only symptom is
    /// that contact offsets computed from the stored value quietly fail to line up. Quantise once,
    /// at the point the position is decided, and everything downstream sees the same number.</para>
    /// </summary>
    public readonly struct FPBuildingPlacement
    {
        /// <summary>Which shape, as returned by the catalog builder.</summary>
        public readonly int ShapeId;

        /// <summary>Which turn of it, in <c>[0, DirectionCount(ShapeId))</c>. 0 for a shape that
        /// does not turn.</summary>
        public readonly int Orientation;

        public readonly FP64 CentreX;
        public readonly FP64 CentreZ;
        public readonly FP64 Y;

        /// <summary>
        /// RETAIN mode: keep the footprint as triangulated ground instead of carving it out.
        /// False (the default, and what every existing constructor produces) carves.
        ///
        /// <para>The rebaker still inserts the footprint as exact constraint edges, so no triangle
        /// straddles its boundary — it simply inserts each edge TWICE, which leaves the ring a wall
        /// to the triangulator and parity-neutral to the erase pass. The interior therefore
        /// survives, and every query the engine ships reports it as WALKABLE. Nothing about a
        /// retained placement makes a unit path around the building; it makes the triangles exist
        /// so that something above can decide. See <c>Docs/Navigation.Rebake.md</c>.</para>
        ///
        /// <para><b>This flag is a determinism input, not a presentation choice.</b> It changes the
        /// geometry the rebake produces, so it must be a pure function of frame state and agree on
        /// every peer — exactly like the placement centre or <c>FPNavMeshTimedPlacement.Sequence</c>.
        /// A mode read from local config, a UI toggle or wall-clock diverges the navmesh while the
        /// state hash still matches, which is the shape of desync nothing else catches.</para>
        /// </summary>
        public readonly bool Retain;

        public FPBuildingPlacement(
            int shapeId, int orientation, FP64 centreX, FP64 centreZ, FP64 y, bool retain)
        {
            ShapeId = shapeId;
            Orientation = orientation;
            CentreX = centreX;
            CentreZ = centreZ;
            Y = y;
            Retain = retain;
        }

        /// <summary>Carve mode (<see cref="Retain"/> false) — the behaviour before retain existed.</summary>
        public FPBuildingPlacement(int shapeId, int orientation, FP64 centreX, FP64 centreZ, FP64 y)
            : this(shapeId, orientation, centreX, centreZ, y, false)
        {
        }

        /// <summary>A shape that does not turn.</summary>
        public FPBuildingPlacement(int shapeId, FP64 centreX, FP64 centreZ, FP64 y)
            : this(shapeId, 0, centreX, centreZ, y, false)
        {
        }

        /// <summary>A shape that does not turn, with an explicit mode.</summary>
        public FPBuildingPlacement(int shapeId, FP64 centreX, FP64 centreZ, FP64 y, bool retain)
            : this(shapeId, 0, centreX, centreZ, y, retain)
        {
        }
    }

    /// <summary>
    /// A catalog expanded by one stage's bake radius.
    ///
    /// The expansion happens ONCE per (catalog, radius) and every placement of an entry then
    /// shares the exact same offsets. Doing it per placement instead would break the thing the
    /// catalog exists for: two buildings meant to sit flush would have their shared edge rounded
    /// in opposite directions — one ceils toward the other, the other floors — and since the
    /// offset carries an irrational factor the coordinates almost never land back on the grid.
    /// A snap unit or two of overlap is enough for the pairwise test to refuse them, so hexagons
    /// could never be placed wall to wall no matter what the game allowed.
    /// </summary>
    public sealed class FPBuildingShapeExpansion
    {
        private readonly long[] _expX;
        private readonly long[] _expZ;

        public FPBuildingShapeCatalog Catalog { get; }
        public FP64 Radius { get; }

        /// <summary>
        /// Derives and proves the expanded table. Throws if any entry cannot be offset, if the
        /// result is not conservative, or if a centrally symmetric footprint lost that symmetry
        /// on the way out — the last one is what would silently make tiling impossible.
        /// </summary>
        public FPBuildingShapeExpansion(FPBuildingShapeCatalog catalog, FP64 radius)
        {
            Catalog = catalog ?? throw new ArgumentException("FPBuildingShapeExpansion: catalog is null");
            Radius = radius;

            long[] sx = catalog.OffsetsX, sz = catalog.OffsetsZ;
            int[] start = catalog.EntryStart;
            _expX = new long[sx.Length];
            _expZ = new long[sz.Length];

            for (int e = 0; e < catalog.EntryCount; e++)
            {
                int s = start[e], t = start[e + 1];
                if (!FPConvexOffset.Expand(sx, sz, s, t, radius, _expX, _expZ, s))
                    throw new ArgumentException(
                        $"FPBuildingShapeExpansion: entry {e} could not be offset by radius {radius.ToDouble():F3}");
                if (!FPConvexOffset.Validate(sx, sz, s, t, _expX, _expZ, s, radius, out string why))
                    throw new ArgumentException($"FPBuildingShapeExpansion: entry {e} — {why}");

                if (catalog.IsCentrallySymmetricEntry(e) && !ExpandedIsCentrallySymmetric(s, t))
                    throw new ArgumentException(
                        $"FPBuildingShapeExpansion: entry {e} is centrally symmetric but its expansion is not — "
                        + "opposite edges must be rounded together or the shape can no longer tile");
            }
        }

        internal long[] ExpandedX => _expX;
        internal long[] ExpandedZ => _expZ;

        private bool ExpandedIsCentrallySymmetric(int s, int t)
        {
            int n = t - s;
            if ((n & 1) != 0) return false;
            int half = n / 2;
            for (int i = 0; i < half; i++)
            {
                if (_expX[s + i] + _expX[s + i + half] != 0) return false;
                if (_expZ[s + i] + _expZ[s + i + half] != 0) return false;
            }
            return true;
        }

        /// <summary>
        /// The centre-to-centre offset that makes two copies of a shape, turned
        /// <paramref name="orientation"/>, share edge <paramref name="edge"/> exactly, after
        /// expansion.
        ///
        /// Defined only for centrally symmetric footprints — for those, edge i and edge i + n/2 are
        /// parallel and opposite, so sliding a copy by the sum of that edge's two vertices lands
        /// its opposite edge exactly on this one. Both vertices are integers, so the delta is too:
        /// contact is authorable as an integer anchor difference, which is the whole point.
        /// </summary>
        public bool TryTilingDelta(int shape, int orientation, int edge, out long dx, out long dz)
        {
            dx = 0; dz = 0;
            int entry = Catalog.TryResolveEntry(shape, orientation);
            if (entry < 0 || !Catalog.IsCentrallySymmetricEntry(entry))
                return false;
            int s = Catalog.EntryStart[entry], n = Catalog.VertexCount(entry);
            if (edge < 0 || edge >= n / 2)
                return false;
            int a = s + edge, b = s + (edge + 1) % n;
            dx = _expX[a] + _expX[b];
            dz = _expZ[a] + _expZ[b];
            return true;
        }

        /// <summary>
        /// The nearest point of the shape's own tiling lattice, in the orientation asked for, to a
        /// desired position — what a placement UI needs to put a shape flush against its neighbours
        /// instead of merely near them.
        ///
        /// <para>The lattice is spanned by two of that footprint's tiling deltas; a third, where it
        /// exists, is their sum or difference, so two are a basis. Finding the nearest point is
        /// solving <c>M * (m, n) = target</c> over the integers. Inverting the basis and rounding
        /// is not enough on its own — for a non-orthogonal basis, and a hexagon lattice is about as
        /// non-orthogonal as they come, the rounded solution can be a neighbour of the true
        /// nearest — so the nine integer pairs around it are compared and the closest wins.</para>
        ///
        /// <para>Integer arithmetic throughout: the deltas are whole snap units and the target is
        /// quantised to the grid on the way in, so the answer is exact and identical on every peer.
        /// The result is on the placement grid by construction, and stays there however far the
        /// lattice is walked — the accuracy does not drift with distance from the origin.</para>
        ///
        /// <para>Returns false for a footprint that cannot tile — and for a shape or orientation
        /// that does not exist — in which case the out parameters hold the quantised target and the
        /// caller can place there unchanged.</para>
        /// </summary>
        public bool TrySnapToLattice(
            int shape, int orientation, FP64 x, FP64 z, out FP64 snappedX, out FP64 snappedZ)
        {
            long tx = FPGeoPredicates.Snap(x);
            long tz = FPGeoPredicates.Snap(z);
            snappedX = FPGeoPredicates.Unsnap(tx);
            snappedZ = FPGeoPredicates.Unsnap(tz);

            if (!Catalog.TilesThePlane(shape, orientation)
                || !TryTilingDelta(shape, orientation, 0, out long ax, out long az)
                || !TryTilingDelta(shape, orientation, 1, out long bx, out long bz))
                return false;

            long det = ax * bz - az * bx;
            if (det == 0)
                return false;   // the two deltas are parallel — not a basis

            // Reduce the basis before searching. The 3x3 sweep below is only enough when the two
            // vectors are not far from orthogonal, and "round, then look at the neighbours" is a
            // heuristic without that: measured over 4,000 targets, every footprint the catalog
            // realistically holds (hexagons, diamond, rectangles) missed the true nearest 0% of the
            // time, but a deliberately skewed hexagon whose deltas are nearly parallel missed 81%,
            // by as much as 31 world units. Reduction removes the precondition instead of hoping
            // for it — after it, the rounded solution is provably within one step.
            ReduceBasis(ref ax, ref az, ref bx, ref bz);
            det = ax * bz - az * bx;

            long m0 = RoundDiv(tx * bz - tz * bx, det);
            long n0 = RoundDiv(ax * tz - az * tx, det);

            long bestX = 0, bestZ = 0, bestDist = long.MaxValue;
            for (long dm = -1; dm <= 1; dm++)
            {
                for (long dn = -1; dn <= 1; dn++)
                {
                    long m = m0 + dm, n = n0 + dn;
                    long px = m * ax + n * bx, pz = m * az + n * bz;
                    long ex = px - tx, ez = pz - tz;
                    long dist = ex * ex + ez * ez;
                    if (dist < bestDist || (dist == bestDist && (px < bestX || (px == bestX && pz < bestZ))))
                    {
                        bestDist = dist;
                        bestX = px;
                        bestZ = pz;
                    }
                }
            }

            snappedX = FPGeoPredicates.Unsnap(bestX);
            snappedZ = FPGeoPredicates.Unsnap(bestZ);
            return true;
        }

        /// <summary>
        /// Lagrange-Gauss reduction of a 2D lattice basis, in exact integers.
        ///
        /// <para>Repeatedly makes the shorter vector first and subtracts the nearest integer
        /// multiple of it from the other. Swapping and subtracting integer multiples generate the
        /// same lattice, so the points reachable afterwards are exactly the points reachable
        /// before — only the coordinates used to name them change.</para>
        ///
        /// <para>Terminates: every subtraction that does anything strictly shortens the longer
        /// vector, and the lengths are positive integers. The iteration cap is a backstop, not the
        /// exit condition — reduction converges in a handful of steps for any basis this catalog
        /// can produce.</para>
        ///
        /// <para>Deterministic by construction: integer arithmetic only, and the rounding uses the
        /// same <see cref="RoundDiv"/> tie rule as everything else here, so two peers cannot
        /// reduce the same basis differently.</para>
        /// </summary>
        private static void ReduceBasis(ref long ax, ref long az, ref long bx, ref long bz)
        {
            for (int iter = 0; iter < 64; iter++)
            {
                long aLenSq = ax * ax + az * az;
                long bLenSq = bx * bx + bz * bz;
                if (bLenSq < aLenSq)
                {
                    long tx2 = ax; ax = bx; bx = tx2;
                    long tz2 = az; az = bz; bz = tz2;
                    aLenSq = bLenSq;
                }

                long q = RoundDiv(ax * bx + az * bz, aLenSq);
                if (q == 0)
                    return;   // b is already as short as subtracting multiples of a can make it

                bx -= q * ax;
                bz -= q * az;
            }
        }

        /// <summary>Round-half-away-from-zero. The tie rule has to be a function of the value
        /// alone, or two peers could round a boundary case apart.</summary>
        private static long RoundDiv(long num, long den)
        {
            if (den < 0) { num = -num; den = -den; }
            long half = den / 2;
            return num >= 0 ? (num + half) / den : -((-num + half) / den);
        }
    }
}
