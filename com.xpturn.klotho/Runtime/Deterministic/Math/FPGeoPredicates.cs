using System;

namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// Exact integer geometric predicates on the snapped coordinate grid.
    ///
    /// Coordinate domain: FP64 (Q32.32) world coordinates snapped to a 1/2^SNAP_FRAC_BITS
    /// world-unit grid by arithmetic right shift (floor, deterministic). The snap invariant
    /// is global: every coordinate that enters a predicate must be snapped -- base navmesh
    /// vertices (bake input stage), building footprint vertices (command apply), area region
    /// polygons (bake), and, if the triangulator uses real super-triangle coordinates, those
    /// as well (fixed at +-2*MAX_SNAPPED_COORD). The domain guard therefore admits
    /// |coord| &lt;= DOMAIN_ABS_MAX = 2*MAX_SNAPPED_COORD.
    ///
    /// Bit budget (D = max coordinate spread = 2*DOMAIN_ABS_MAX ~= 2^28.5 with super allowance):
    ///   Orient2D (degree-2): differences &lt;= D, products &lt;= ~2^57 -> exact in Int64
    ///     (accumulated via FPInt128 anyway to keep one audited code path).
    ///   InCircle (degree-4): lifted terms and minors &lt;= 2*D^2 ~= 2^58 (Int64),
    ///     |det| &lt;= 12*(D/2)^4 * 16 ~= 2^113.6 -> exact in signed 128 bits, ~13 bits margin.
    /// Pure integer ops -> bit-identical across runtimes by construction; correctness is
    /// pinned against a BigInteger oracle in tests.
    /// </summary>
    public static class FPGeoPredicates
    {
        /// <summary>
        /// Fractional bits kept by the predicate grid (F). Grid cell = 1/2^F world units.
        ///
        /// <para>The engine itself is dimensionless — nothing here assumes a physical unit. In
        /// practice a world unit is a METRE, because navmesh geometry arrives from Unity or Godot
        /// and both use that convention; the bake settings that ride along with it (agent radius,
        /// height, climb) are recorded in metres for the same reason. So at F = 10 the grid is
        /// 1/1024 of a metre, just under a millimetre.</para>
        /// </summary>
        public const int SNAP_FRAC_BITS = 10;

        /// <summary>Arithmetic right-shift from Q32.32 raw to the snapped integer grid.</summary>
        public const int SNAP_SHIFT = FP64.FRACTIONAL_BITS - SNAP_FRAC_BITS;

        /// <summary>
        /// Snap-grid steps in one world unit — the number you multiply by to write a footprint or
        /// a fixture in world units and get snapped integers.
        ///
        /// <para>Exists so that callers stop writing <c>1 &lt;&lt; 10</c>. Six places did, one of
        /// them a shipped sample's building table, and none of them referenced
        /// <see cref="SNAP_FRAC_BITS"/> — so changing the grid resolution would have left them
        /// silently scaled by the old factor while everything around them moved.</para>
        /// </summary>
        public const long SNAP_UNITS_PER_WORLD = 1L << SNAP_FRAC_BITS;

        /// <summary>
        /// Max |snapped coordinate| for regular inputs: engine coordinate bound (46340,
        /// see FPNavAvoidance.COORD_ABS_MAX) expressed on the snap grid.
        /// </summary>
        public const long MAX_SNAPPED_COORD = 46340L << SNAP_FRAC_BITS;

        /// <summary>
        /// Predicate domain bound: regular inputs plus the real-coordinate super-triangle
        /// allowance (a fixed +-2M convention). Inputs beyond this break the bit budget.
        /// </summary>
        public const long DOMAIN_ABS_MAX = MAX_SNAPPED_COORD * 2;

        /// <summary>FP64 (Q32.32) -> snapped grid integer. Arithmetic shift = floor, deterministic.</summary>
        public static long Snap(FP64 value)
        {
            return value.RawValue >> SNAP_SHIFT;
        }

        /// <summary>Snapped grid integer -> FP64. Exact, lossless inverse embedding.</summary>
        public static FP64 Unsnap(long snapped)
        {
            return FP64.FromRaw(snapped << SNAP_SHIFT);
        }

        /// <summary>
        /// Puts an arbitrary coordinate ON the grid, by snapping and embedding back. Idempotent.
        ///
        /// <para>Anything that becomes geometry has to pass through here first, because the grid is
        /// where the exact predicates are defined — a coordinate off it cannot be reasoned about
        /// exactly. Consumers that hand coordinates to the navmesh (a placement centre, a bake
        /// input vertex) are refused outright if they skip it, so this is the conversion rather
        /// than a convenience.</para>
        ///
        /// <para>The grid is 2^-<see cref="SNAP_FRAC_BITS"/> of a world unit, NOT one world unit.
        /// Reading it as whole units is a live trap: it is a sufficient condition and not the
        /// requirement, and rounding to integers loses everything the fractional resolution is for
        /// (two shapes meeting flush, for one — no useful contact offset is a whole unit). The flip
        /// side is that "fractional" means BINARY fractions: 0.5, 0.25 and 0.125 are on the grid
        /// and 0.1 and 0.3 are not, so quantising is mandatory rather than defensive.</para>
        /// </summary>
        public static FP64 Quantize(FP64 value)
        {
            return Unsnap(Snap(value));
        }

        /// <summary>
        /// True when <paramref name="value"/> already lies on the grid, i.e. when
        /// <see cref="Quantize"/> would leave it unchanged.
        ///
        /// <para>Exposed so a caller can check before submitting rather than discover it as a
        /// thrown refusal. The refusals themselves test this, so there is one definition of the
        /// contract instead of a guard and a re-derivation that can drift apart.</para>
        /// </summary>
        public static bool IsOnGrid(FP64 value)
        {
            return Unsnap(Snap(value)).RawValue == value.RawValue;
        }

        /// <summary>True if (x, y) lies inside the predicate domain (super-triangle allowance included).</summary>
        public static bool IsInDomain(long x, long y)
        {
            return x >= -DOMAIN_ABS_MAX && x <= DOMAIN_ABS_MAX
                && y >= -DOMAIN_ABS_MAX && y <= DOMAIN_ABS_MAX;
        }

        /// <summary>
        /// Exact orientation of c relative to the directed line a->b:
        /// +1 = left turn (triangle a,b,c is CCW), -1 = right turn (CW), 0 = collinear.
        /// </summary>
        public static int Orient2D(long ax, long ay, long bx, long by, long cx, long cy)
        {
            AssertDomain(ax, ay);
            AssertDomain(bx, by);
            AssertDomain(cx, cy);

            long abx = bx - ax;
            long aby = by - ay;
            long acx = cx - ax;
            long acy = cy - ay;

            FPInt128 det = FPInt128.Sub(FPInt128.Mul64(abx, acy), FPInt128.Mul64(aby, acx));
            return det.Sign();
        }

        /// <summary>
        /// Exact incircle test of d against the circumcircle of (a, b, c).
        /// Sign convention: for CCW-oriented (a, b, c), +1 = d strictly inside,
        /// -1 = strictly outside, 0 = cocircular (routine on integer grids --
        /// axis-aligned rectangle corners are exactly cocircular). For CW-oriented
        /// (a, b, c) the sign flips; callers normalize orientation or multiply by
        /// Orient2D(a, b, c).
        /// </summary>
        public static int InCircle(long ax, long ay, long bx, long by, long cx, long cy, long dx, long dy)
        {
            AssertDomain(ax, ay);
            AssertDomain(bx, by);
            AssertDomain(cx, cy);
            AssertDomain(dx, dy);

            // Relative coordinates about the query point d (exact in Int64).
            long a0 = ax - dx;
            long a1 = ay - dy;
            long b0 = bx - dx;
            long b1 = by - dy;
            long c0 = cx - dx;
            long c1 = cy - dy;

            // Lifted terms and 2x2 minors -- all bounded by ~2^58, exact in Int64.
            long la = a0 * a0 + a1 * a1;
            long lb = b0 * b0 + b1 * b1;
            long lc = c0 * c0 + c1 * c1;
            long mbc = b0 * c1 - b1 * c0; // b' x c'
            long mac = a0 * c1 - a1 * c0; // a' x c'
            long mab = a0 * b1 - a1 * b0; // a' x b'

            // det = La*(b'xc') - Lb*(a'xc') + Lc*(a'xb'), accumulated exactly in 128 bits.
            FPInt128 det = FPInt128.Sub(FPInt128.Mul64(la, mbc), FPInt128.Mul64(lb, mac));
            det = FPInt128.Add(det, FPInt128.Mul64(lc, mab));
            return det.Sign();
        }

        private static void AssertDomain(long x, long y)
        {
            System.Diagnostics.Debug.Assert(IsInDomain(x, y),
                "FPGeoPredicates: coordinate outside the snapped predicate domain");
        }
    }
}
