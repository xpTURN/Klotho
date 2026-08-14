using System;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Offsets a snapped convex polygon outward by the agent radius.
    ///
    /// The expansion is the Minkowski sum with a disc, so every convex corner is really an arc.
    /// An arc cannot be represented, and the direction of the approximation is not a matter of
    /// taste: an INSCRIBED polygon is smaller than the true offset, which leaves a gap an agent
    /// can slip through at the corner. It has to CONTAIN the true offset, so the edges are pushed
    /// out and their intersections taken — a miter.
    ///
    /// The miter is built from OFFSET LINES, not from a sec(pi/N) scale factor. The scale form
    /// needs an irrational constant per shape (which would then have to ride in the catalog hash)
    /// and only works for regular polygons; the line form is exact rational arithmetic and works
    /// for any convex polygon, which is what P4's long thin boxes need.
    ///
    /// WHERE THE ROUNDING IS, smallest first — the order is the opposite of the intuition:
    ///   * ceil(r|n|)  — under a snap unit of over-expansion, effectively nothing.
    ///   * the outward vertex snap — up to sqrt(2) snap units, and it can push a SUPPORT LINE
    ///     inward, which is the one that matters.
    ///   * the padding — deliberately the largest, sized to swallow the one above.
    ///
    /// That last point is the subtle one. "Snapping outward never shrinks the shape" is a
    /// property of AXIS-ALIGNED rectangles, not of snapping: for a slanted edge the per-axis
    /// outward move can have a negative component along that edge's own normal. And the miter has
    /// NO slack at an edge's midpoint — the miter edge lies exactly on the Minkowski boundary
    /// there — so any inward nudge already breaks containment. The padding is what buys the slack
    /// back, and <see cref="Validate"/> is what proves it was enough.
    /// </summary>
    internal static class FPConvexOffset
    {
        /// <summary>
        /// Extra radius, in snap units, added before mitering to absorb the outward vertex snap.
        /// The snap moves a vertex by less than one unit per axis, so its worst inward component
        /// along any edge normal is under sqrt(2); two units covers it with room to spare.
        /// </summary>
        internal const long SnapPaddingUnits = 2;

        /// <summary>
        /// Expands the CCW convex polygon in [srcStart, srcEnd) by <paramref name="radius"/> and
        /// writes the same number of vertices starting at <paramref name="dstStart"/>.
        ///
        /// Returns false when the result is not usable — a vertex outside the snapped domain, or
        /// two offset lines too close to parallel to intersect. The caller decides what that
        /// means; at catalog-build time it is a throw.
        /// </summary>
        internal static bool Expand(
            long[] srcX, long[] srcZ, int srcStart, int srcEnd,
            FP64 radius,
            long[] dstX, long[] dstZ, int dstStart)
        {
            int n = srcEnd - srcStart;
            if (n < 3)
                return false;

            // Everything below is integer. radius is Q32.32 world units; one snap unit is
            // 2^-SNAP_FRAC_BITS world units, so the radius in Q32.32 SNAP units is the raw value
            // shifted up. Padding is added here, before mitering, so the whole outline moves out
            // together — padding afterwards would move vertices without moving the edges.
            RequireMiterDomain(radius);
            long rSnapRaw = (radius.RawValue << FPGeoPredicates.SNAP_FRAC_BITS)
                          + (SnapPaddingUnits << FP64.FRACTIONAL_BITS);
            try
            {
                return ExpandChecked(srcX, srcZ, srcStart, srcEnd, rSnapRaw, dstX, dstZ, dstStart, n);
            }
            catch (OverflowException e) { throw TooLarge(e); }
        }

        /// <summary>
        /// The miter itself, in a <c>checked</c> context so a product that runs out of Int64 throws
        /// instead of wrapping into a plausible-looking wrong polygon.
        /// </summary>
        private static bool ExpandChecked(
            long[] srcX, long[] srcZ, int srcStart, int srcEnd, long rSnapRaw,
            long[] dstX, long[] dstZ, int dstStart, int n)
        {
          checked
          {

            // Centroid of the source, used only to pick the rounding direction. Any interior
            // point would do; the sum is exact and the division is by a small integer.
            long cxSum = 0, czSum = 0;
            for (int i = srcStart; i < srcEnd; i++) { cxSum += srcX[i]; czSum += srcZ[i]; }

            for (int i = 0; i < n; i++)
            {
                // Vertex i is shared by edge (i-1 -> i) and edge (i -> i+1); its offset position
                // is where those two offset lines meet.
                int prev = srcStart + (i + n - 1) % n;
                int cur = srcStart + i;
                int next = srcStart + (i + 1) % n;

                if (!OffsetLine(srcX[prev], srcZ[prev], srcX[cur], srcZ[cur], rSnapRaw,
                                out long n1x, out long n1z, out long c1))
                    return false;
                if (!OffsetLine(srcX[cur], srcZ[cur], srcX[next], srcZ[next], rSnapRaw,
                                out long n2x, out long n2z, out long c2))
                    return false;

                long det = n1x * n2z - n1z * n2x;
                if (det == 0)
                    return false;   // collinear edges — the source is degenerate

                // Exact rational intersection; every coefficient is an integer.
                long numX = c1 * n2z - c2 * n1z;
                long numZ = n1x * c2 - n2x * c1;

                // Round away from the centroid. Comparing n*centroidSum against the scaled
                // coordinate keeps this in integers (centroid = cxSum / n).
                bool awayPosX = numX * n * (det > 0 ? 1 : -1) >= cxSum * det * (det > 0 ? 1 : -1);
                bool awayPosZ = numZ * n * (det > 0 ? 1 : -1) >= czSum * det * (det > 0 ? 1 : -1);
                long vx = awayPosX ? CeilDiv(numX, det) : FloorDiv(numX, det);
                long vz = awayPosZ ? CeilDiv(numZ, det) : FloorDiv(numZ, det);

                long m = FPGeoPredicates.MAX_SNAPPED_COORD;
                if (vx < -m || vx > m || vz < -m || vz > m)
                    return false;

                dstX[dstStart + i] = vx;
                dstZ[dstStart + i] = vz;
            }
            return true;
          }
        }

        /// <summary>
        /// Refuses a radius whose snap-scaled form would not fit in Int64.
        /// </summary>
        private static void RequireMiterDomain(FP64 radius)
        {
            if (radius.RawValue < 0)
                throw new ArgumentException("FPConvexOffset: radius must not be negative");
            if (radius.RawValue > (long.MaxValue >> FPGeoPredicates.SNAP_FRAC_BITS) - (SnapPaddingUnits << FP64.FRACTIONAL_BITS))
                throw new ArgumentException(
                    $"FPConvexOffset: radius {radius.ToDouble():F3} is too large to scale into snap units");
        }

        /// <summary>
        /// Refuses a polygon whose longest edge would overflow <c>r * |n|</c>.
        ///
        /// <para>This exists because the failure without it is MISLEADING rather than silent. The
        /// product wraps, the offset lines come out at nonsense positions, and
        /// <see cref="Validate"/> then reports "not strictly convex/CCW" or "closer than the agent
        /// radius" — sending whoever hit it to look at their shape, when the shape is fine and the
        /// arithmetic simply ran out of room. Measured (radius 0.5): the ceiling lands between
        /// 1500 and 2000 world units of half-extent, and it is NOT specific to boxes — a hexagon of
        /// the same scale fails identically, so this is a limit of the shared miter that P4's size
        /// sweep merely surfaced.</para>
        ///
        /// <para>Far outside any building (real footprints are single-digit world units), so this
        /// is a diagnostic rather than a restriction. Stated as a hard refusal because the wrapped
        /// arithmetic is exactly the class of quiet-wrong-navmesh that is hardest to find.</para>
        ///
        /// <para><b>Detected, not predicted.</b> The first attempt here bounded the products
        /// analytically and refused anything that MIGHT overflow. That traded one wrong answer for
        /// another: the bound has to assume the worst alignment of normal and vertex, so it came out
        /// several times pessimistic and refused shapes that compute perfectly (measured: it
        /// rejected half-extent 700–1500, which the arithmetic handles). The products are checked
        /// where they happen instead, so the refusal boundary is exactly the arithmetic's own.</para>
        /// </summary>
        private static ArgumentException TooLarge(OverflowException inner)
        {
            return new ArgumentException(
                "FPConvexOffset: shape is too large for the miter arithmetic at this radius — an "
                + "intermediate product overflows Int64. This is an engine limit, not a problem "
                + "with the polygon; real footprints are single-digit world units and the ceiling "
                + "is around a thousand.", inner);
        }

        /// <summary>
        /// The line carrying the edge (ax,az)-(bx,bz) pushed out by <paramref name="rSnap"/>:
        /// n . x = c, with n the un-normalised outward normal of a CCW edge.
        ///
        /// c = n . a + ceil(r * |n|), with |n| rounded UP by an exact integer square root — short
        /// is the direction that opens a gap, so every rounding here leans long.
        /// </summary>
        private static bool OffsetLine(
            long ax, long az, long bx, long bz, long rSnapRaw,
            out long nx, out long nz, out long c)
        {
            // Checked for the same reason the miter below is, and it has to be its OWN block:
            // `checked` is lexical, so ExpandChecked's block does not reach into this callee and
            // the one below starts after these lines. That left the entry arithmetic as the only
            // products in this file that could wrap instead of throwing TooLarge.
            //
            // A wrapped lenSq does not fail loudly, it degrades: negative lands on CeilSqrt's
            // `value <= 0` early return, so push becomes 0 and the polygon is emitted with NO
            // agent-radius expansion; near long.MaxValue the Math.Sqrt seed squares past the
            // range and CeilSqrt's correction loops lose their meaning — spin or wrong root,
            // unspecified which. Either way the "exact integer square root" contract is gone.
            //
            // Unreachable today: the production entry (FPBuildingShapeExpansion) validates its
            // offsets against MAX_SNAPPED_COORD, which leaves an edge delta 16x below the wrap
            // threshold, and the entry that skips that validation — FPNavMeshRebaker
            // .RebakeFromFootprints — is internal with no caller but a test. This closes the gap
            // before that entry is ever opened up. Cost is irrelevant: the expansion runs at
            // catalog build time over a few dozen vertices per shape.
            long lenSq;
            checked
            {
                nx = bz - az;
                nz = -(bx - ax);
                lenSq = nx * nx + nz * nz;
            }
            c = 0;
            if (lenSq == 0)
                return false;

            // push = ceil(r * |n|), computed with an EXACT integer square root rounded up. No
            // FP64.Sqrt: its result would have to be squared to be verified, and that product
            // overflows Q32.32 long before shapes get interesting (a gap of 2e6 squares to 5e12
            // against a 2.1e9 integer range). Rounding |n| up keeps the whole thing conservative
            // — the direction that never opens a gap.
            checked
            {
                long push = CeilShift(rSnapRaw * CeilSqrt(lenSq), FP64.FRACTIONAL_BITS);
                c = nx * ax + nz * az + push;
            }
            return true;
        }

        /// <summary>Smallest integer s with s*s &gt;= value. Exact.</summary>
        internal static long CeilSqrt(long value)
        {
            if (value <= 0) return 0;
            long s = (long)System.Math.Sqrt(value);
            while (s > 0 && s * s > value) s--;          // correct a high estimate
            while (s * s < value) s++;                    // then round up
            return s;
        }

        /// <summary>ceil(value / 2^shift) for value &gt;= 0.</summary>
        private static long CeilShift(long value, int shift)
        {
            long mask = (1L << shift) - 1;
            return (value >> shift) + ((value & mask) != 0 ? 1 : 0);
        }

        private static long FloorDiv(long a, long b)
        {
            if (b < 0) { a = -a; b = -b; }
            long q = a / b;
            return (a % b != 0 && a < 0) ? q - 1 : q;
        }

        private static long CeilDiv(long a, long b)
        {
            if (b < 0) { a = -a; b = -b; }
            long q = a / b;
            return (a % b != 0 && a > 0) ? q + 1 : q;
        }

        /// <summary>
        /// Proves the expanded polygon is usable, at build time, in integers.
        ///
        /// This is the FIRST line of defence and the only one at its scale. The sampling test in
        /// the suite catches an inscribed approximation (centimetres); the snap jitter this
        /// guards against is a thousandth of a unit, far under any sampling grid. If this check
        /// is ever deleted as "redundant with the sampling test", the failure it was holding back
        /// comes straight back and shows up only as an agent clipping a corner.
        ///
        /// Checks, in order: non-degenerate, convex and CCW, and every source edge's support line
        /// pushed out by at least r.
        /// </summary>
        internal static bool Validate(
            long[] srcX, long[] srcZ, int srcStart, int srcEnd,
            long[] dstX, long[] dstZ, int dstStart, FP64 radius, out string failure)
        {
            int n = srcEnd - srcStart;
            failure = null;

            for (int i = 0; i < n; i++)
            {
                int a = dstStart + i, b = dstStart + (i + 1) % n, cc = dstStart + (i + 2) % n;
                if (dstX[a] == dstX[b] && dstZ[a] == dstZ[b])
                {
                    failure = $"expanded vertex {i} collapsed onto {(i + 1) % n}";
                    return false;
                }
                if (FPGeoPredicates.Orient2D(dstX[a], dstZ[a], dstX[b], dstZ[b], dstX[cc], dstZ[cc]) <= 0)
                {
                    failure = $"expanded polygon is not strictly convex/CCW at vertex {(i + 1) % n}";
                    return false;
                }
            }

            // The containment claim, edge by edge: the expanded polygon must lie entirely on the
            // outer side of each source edge pushed out by r. Checking the two expanded vertices
            // that the edge produced is enough — the offset polygon is convex, so if its extreme
            // points on this normal clear the line, all of it does.
            // No padding here — the padding is slack this check is entitled to spend.
            RequireMiterDomain(radius);
            long rSnapRaw = radius.RawValue << FPGeoPredicates.SNAP_FRAC_BITS;
            // Same Int64 ceiling as the miter itself, and checked for the same reason: a wrapped
            // product would turn into a nonsense `need` and this loop would report a containment
            // failure that is really an arithmetic one.
            for (int i = 0; i < n; i++)
            {
                int s0 = srcStart + i, s1 = srcStart + (i + 1) % n;
                long nx = srcZ[s1] - srcZ[s0];
                long nz = -(srcX[s1] - srcX[s0]);
                long need, baseDot;
                try
                {
                    checked
                    {
                        need = CeilShift(rSnapRaw * CeilSqrt(nx * nx + nz * nz), FP64.FRACTIONAL_BITS);
                        baseDot = nx * srcX[s0] + nz * srcZ[s0];
                    }
                }
                catch (OverflowException e) { throw TooLarge(e); }

                for (int k = 0; k < 2; k++)
                {
                    int d = dstStart + (i + k) % n;
                    long gap = nx * dstX[d] + nz * dstZ[d] - baseDot;
                    if (gap < need)
                    {
                        failure = $"expanded edge {i} sits closer than the agent radius " +
                                  $"(support gap {gap}, need {need})";
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
