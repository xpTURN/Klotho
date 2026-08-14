using System;
using System.Numerics;
using SysRandom = System.Random;
using NUnit.Framework;

namespace xpTURN.Klotho.Deterministic.Math.Tests
{
    /// <summary>
    /// Exact geometric predicate tests.
    /// Orient2D / InCircle are pinned against BigInteger oracles over the full snapped
    /// predicate domain (super-triangle allowance included), plus the degenerate matrix
    /// that matter: collinear, cocircular (routine on integer grids -- axis-aligned
    /// rectangles), duplicate vertices, axis-aligned edges, and boundary stress at the
    /// exact domain extremes (random seeds never hit those).
    /// </summary>
    [TestFixture]
    public class FPGeoPredicatesTests
    {
        private const long D = FPGeoPredicates.DOMAIN_ABS_MAX;

        #region Oracles (test-only BigInteger)

        private static int OrientOracle(long ax, long ay, long bx, long by, long cx, long cy)
        {
            BigInteger abx = (BigInteger)bx - ax;
            BigInteger aby = (BigInteger)by - ay;
            BigInteger acx = (BigInteger)cx - ax;
            BigInteger acy = (BigInteger)cy - ay;
            return (abx * acy - aby * acx).Sign;
        }

        private static int InCircleOracle(long ax, long ay, long bx, long by, long cx, long cy, long dx, long dy)
        {
            BigInteger a0 = (BigInteger)ax - dx, a1 = (BigInteger)ay - dy;
            BigInteger b0 = (BigInteger)bx - dx, b1 = (BigInteger)by - dy;
            BigInteger c0 = (BigInteger)cx - dx, c1 = (BigInteger)cy - dy;

            BigInteger la = a0 * a0 + a1 * a1;
            BigInteger lb = b0 * b0 + b1 * b1;
            BigInteger lc = c0 * c0 + c1 * c1;

            BigInteger det = la * (b0 * c1 - b1 * c0)
                           - lb * (a0 * c1 - a1 * c0)
                           + lc * (a0 * b1 - a1 * b0);
            return det.Sign;
        }

        private static long NextCoord(SysRandom rng)
        {
            return rng.NextInt64(-D, D + 1);
        }

        #endregion

        #region Random sweep vs oracle (500+ seeds)

        [Test]
        public void Orient2D_RandomDomainSweep_MatchesOracle()
        {
            var rng = new SysRandom(96010);
            for (int i = 0; i < 2000; i++)
            {
                long ax = NextCoord(rng), ay = NextCoord(rng);
                long bx = NextCoord(rng), by = NextCoord(rng);
                long cx = NextCoord(rng), cy = NextCoord(rng);

                Assert.AreEqual(
                    OrientOracle(ax, ay, bx, by, cx, cy),
                    FPGeoPredicates.Orient2D(ax, ay, bx, by, cx, cy),
                    $"Orient2D iter {i}");
            }
        }

        [Test]
        public void InCircle_RandomDomainSweep_MatchesOracle()
        {
            var rng = new SysRandom(96011);
            for (int i = 0; i < 2000; i++)
            {
                long ax = NextCoord(rng), ay = NextCoord(rng);
                long bx = NextCoord(rng), by = NextCoord(rng);
                long cx = NextCoord(rng), cy = NextCoord(rng);
                long dx = NextCoord(rng), dy = NextCoord(rng);

                Assert.AreEqual(
                    InCircleOracle(ax, ay, bx, by, cx, cy, dx, dy),
                    FPGeoPredicates.InCircle(ax, ay, bx, by, cx, cy, dx, dy),
                    $"InCircle iter {i}");
            }
        }

        #endregion

        #region Degenerates: collinear, duplicates, axis-aligned

        [Test]
        public void Orient2D_CollinearAndDuplicates_ExactZero()
        {
            var rng = new SysRandom(96012);
            for (int i = 0; i < 500; i++)
            {
                long ax = rng.NextInt64(-(1L << 20), (1L << 20) + 1);
                long ay = rng.NextInt64(-(1L << 20), (1L << 20) + 1);
                long dx = rng.NextInt64(-(1L << 10), (1L << 10) + 1);
                long dy = rng.NextInt64(-(1L << 10), (1L << 10) + 1);
                long t1 = rng.NextInt64(-(1L << 10), (1L << 10) + 1);
                long t2 = rng.NextInt64(-(1L << 10), (1L << 10) + 1);

                // Three points on one line: exact zero, no epsilon.
                Assert.AreEqual(0, FPGeoPredicates.Orient2D(
                    ax, ay, ax + t1 * dx, ay + t1 * dy, ax + t2 * dx, ay + t2 * dy), $"collinear iter {i}");
            }

            // Duplicate vertices (grid coincidence is routine).
            Assert.AreEqual(0, FPGeoPredicates.Orient2D(7, -3, 7, -3, 100, 200));
            Assert.AreEqual(0, FPGeoPredicates.Orient2D(7, -3, 100, 200, 7, -3));
            Assert.AreEqual(0, FPGeoPredicates.Orient2D(7, -3, 100, 200, 100, 200));

            // Axis-aligned edges.
            Assert.AreEqual(0, FPGeoPredicates.Orient2D(0, 5, 100, 5, 200, 5));
            Assert.AreEqual(0, FPGeoPredicates.Orient2D(5, 0, 5, 100, 5, 200));
        }

        [Test]
        public void InCircle_AxisAlignedRectangleCorners_ExactlyCocircular()
        {
            // On integer grids cocircularity is the routine case, not a rare edge:
            // the four corners of any axis-aligned rectangle are exactly cocircular.
            var rng = new SysRandom(96013);
            for (int i = 0; i < 500; i++)
            {
                long cx = rng.NextInt64(-D / 2, D / 2 + 1);
                long cy = rng.NextInt64(-D / 2, D / 2 + 1);
                long w = rng.NextInt64(1, D / 4);
                long h = rng.NextInt64(1, D / 4);

                long x0 = cx - w, x1 = cx + w;
                long y0 = cy - h, y1 = cy + h;

                // CCW corners: (x0,y0) -> (x1,y0) -> (x1,y1) -> (x0,y1).
                Assert.AreEqual(0, FPGeoPredicates.InCircle(x0, y0, x1, y0, x1, y1, x0, y1), $"rect iter {i}");
            }
        }

        [Test]
        public void InCircle_DegenerateInputs_MatchOracle()
        {
            // Collinear (a,b,c): the circumcircle degenerates to a line -- the det is NOT
            // forced to zero (it reduces to a side-of-line measure), so pin the oracle.
            // (Out of contract for CDT anyway: callers gate triangles by Orient2D first.)
            Assert.AreEqual(
                InCircleOracle(0, 0, 10, 0, 20, 0, 5, 7),
                FPGeoPredicates.InCircle(0, 0, 10, 0, 20, 0, 5, 7));
            // All four points on one line: exactly "cocircular", det = 0.
            Assert.AreEqual(0, FPGeoPredicates.InCircle(0, 0, 10, 0, 20, 0, 5, 0));
            // Duplicate vertices.
            Assert.AreEqual(0, FPGeoPredicates.InCircle(3, 4, 3, 4, 10, 0, 5, 7));
            // d coincides with a triangle vertex: on the circle by definition.
            Assert.AreEqual(0, FPGeoPredicates.InCircle(1, 0, 0, 1, -1, 0, 1, 0));
        }

        #endregion

        #region Sign convention pin

        [Test]
        public void InCircle_SignConvention_CcwPositiveInside()
        {
            // (1,0) -> (0,1) -> (-1,0) is CCW (unit circumcircle centered at origin).
            Assert.AreEqual(1, FPGeoPredicates.Orient2D(1, 0, 0, 1, -1, 0), "triangle must be CCW");

            Assert.AreEqual(1, FPGeoPredicates.InCircle(1, 0, 0, 1, -1, 0, 0, 0), "origin inside -> +1");
            Assert.AreEqual(-1, FPGeoPredicates.InCircle(1, 0, 0, 1, -1, 0, 3, 0), "far point outside -> -1");
            Assert.AreEqual(0, FPGeoPredicates.InCircle(1, 0, 0, 1, -1, 0, 0, -1), "on circle -> 0");

            // CW orientation flips every sign (callers normalize or multiply by Orient2D).
            Assert.AreEqual(-1, FPGeoPredicates.InCircle(1, 0, -1, 0, 0, 1, 0, 0), "CW flips inside to -1");

            // Same shape scaled near the regular coordinate bound: signs are scale-invariant.
            long s = FPGeoPredicates.MAX_SNAPPED_COORD;
            Assert.AreEqual(1, FPGeoPredicates.InCircle(s, 0, 0, s, -s, 0, 0, 0));
            Assert.AreEqual(-1, FPGeoPredicates.InCircle(s, 0, 0, s, -s, 0, s, s));
        }

        #endregion

        #region Boundary stress at exact domain extremes

        [Test]
        public void Predicates_DomainExtremes_ExactAtMaxMagnitude()
        {
            // Maximal-magnitude cocircular square: corners at +-DOMAIN_ABS_MAX. This drives
            // the degree-4 determinant to its budget ceiling and must still be exactly zero.
            Assert.AreEqual(0, FPGeoPredicates.InCircle(-D, -D, D, -D, D, D, -D, D));

            // Maximal-magnitude collinear diagonal.
            Assert.AreEqual(0, FPGeoPredicates.Orient2D(-D, -D, 0, 0, D, D));

            // Near-degenerate at the extreme: one grid step off the diagonal, both predicates
            // must resolve the sign exactly (matches oracle).
            Assert.AreEqual(
                OrientOracle(-D, -D, 0, 1, D, D),
                FPGeoPredicates.Orient2D(-D, -D, 0, 1, D, D));
            Assert.AreEqual(
                InCircleOracle(-D, -D, D, -D, D, D, -D, D - 1),
                FPGeoPredicates.InCircle(-D, -D, D, -D, D, D, -D, D - 1));

            // Random corners pinned to the exact domain boundary lines.
            var rng = new SysRandom(96014);
            for (int i = 0; i < 200; i++)
            {
                long x = rng.NextInt64(-D, D + 1);
                long y = rng.NextInt64(-D, D + 1);
                Assert.AreEqual(
                    InCircleOracle(-D, -D, D, -D, x, y, D, D),
                    FPGeoPredicates.InCircle(-D, -D, D, -D, x, y, D, D),
                    $"boundary iter {i}");
            }
        }

        #endregion

        #region Snap / Unsnap / domain

        [Test]
        public void Snap_OnGridValues_RoundTripExactly()
        {
            long[] grid = { 0L, 1L, -1L, 12345L, -12345L, FPGeoPredicates.MAX_SNAPPED_COORD, -FPGeoPredicates.MAX_SNAPPED_COORD };
            foreach (long snapped in grid)
            {
                FP64 world = FPGeoPredicates.Unsnap(snapped);
                Assert.AreEqual(snapped, FPGeoPredicates.Snap(world), $"round trip {snapped}");
                Assert.AreEqual(snapped << FPGeoPredicates.SNAP_SHIFT, world.RawValue);
            }
        }

        [Test]
        public void Snap_IsFloor_ForNegativesToo()
        {
            var rng = new SysRandom(96015);
            long cell = 1L << FPGeoPredicates.SNAP_SHIFT;
            for (int i = 0; i < 1000; i++)
            {
                long raw = rng.NextInt64(-(46340L << 32), (46340L << 32) + 1);
                long snapped = FPGeoPredicates.Snap(FP64.FromRaw(raw));
                long floorRaw = FPGeoPredicates.Unsnap(snapped).RawValue;

                // floor semantics: floorRaw <= raw < floorRaw + cell (holds for negatives).
                Assert.IsTrue(floorRaw <= raw && raw < floorRaw + cell, $"floor iter {i}: raw={raw}");
            }

            // Explicit negative floor: raw -1 snaps down to -1 grid cell, not 0.
            Assert.AreEqual(-1L, FPGeoPredicates.Snap(FP64.FromRaw(-1L)));
        }

        [Test]
        public void Snap_EngineBound_MapsToMaxSnappedCoord()
        {
            Assert.AreEqual(46340L << FPGeoPredicates.SNAP_FRAC_BITS, FPGeoPredicates.MAX_SNAPPED_COORD);
            Assert.AreEqual(FPGeoPredicates.MAX_SNAPPED_COORD, FPGeoPredicates.Snap(FP64.FromInt(46340)));
        }

        [Test]
        public void IsInDomain_BoundaryInclusive_SuperAllowance()
        {
            Assert.IsTrue(FPGeoPredicates.IsInDomain(D, -D));
            Assert.IsTrue(FPGeoPredicates.IsInDomain(-D, D));
            Assert.IsFalse(FPGeoPredicates.IsInDomain(D + 1, 0));
            Assert.IsFalse(FPGeoPredicates.IsInDomain(0, -(D + 1)));

            // Super allowance = 2x the regular coordinate bound.
            Assert.AreEqual(FPGeoPredicates.MAX_SNAPPED_COORD * 2, D);
        }

        #endregion
    }
}
