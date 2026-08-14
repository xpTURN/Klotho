using System;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The miter expansion, and specifically its CONSERVATIVENESS.
    ///
    /// This is the quietest failure in the whole shape plan. An expansion that comes out a hair
    /// too small throws nothing, carves a shape that looks right, and passes every test that only
    /// asks "was it accepted" or "is the middle blocked". It shows up as an agent clipping a
    /// corner, in a build, months later — and every peer clips the same corner, so no desync
    /// check ever fires.
    ///
    /// The defence is two layers at two scales, and they are not redundant:
    ///   * the SAMPLING test below — catches an inscribed approximation, centimetres;
    ///   * FPConvexOffset.Validate — catches snap jitter, a thousandth of a unit, which is far
    ///     under any sampling grid.
    /// </summary>
    [TestFixture]
    public class FPConvexOffsetTests
    {
        private const int Unit = (int)FPGeoPredicates.SNAP_UNITS_PER_WORLD;

        /// <summary>Diamond with half-diagonal d (world units), CCW, exactly on the grid.</summary>
        private static (long[] x, long[] z) Diamond(double d)
        {
            long dd = (long)(d * Unit);
            return (new[] { dd, 0L, -dd, 0L }, new[] { 0L, dd, 0L, -dd });
        }

        /// <summary>Axis-aligned box, CCW, exactly on the grid.</summary>
        private static (long[] x, long[] z) Box(double w, double h)
        {
            long hw = (long)(w * Unit / 2), hh = (long)(h * Unit / 2);
            return (new[] { -hw, hw, hw, -hw }, new[] { -hh, -hh, hh, hh });
        }

        private static (long[] x, long[] z) Expand((long[] x, long[] z) src, double radius)
        {
            int n = src.x.Length;
            var dx = new long[n];
            var dz = new long[n];
            Assert.IsTrue(
                FPConvexOffset.Expand(src.x, src.z, 0, n, FP64.FromDouble(radius), dx, dz, 0),
                "expansion must succeed for a well-formed convex polygon");
            Assert.IsTrue(
                FPConvexOffset.Validate(src.x, src.z, 0, n, dx, dz, 0, FP64.FromDouble(radius), out string why),
                $"construction validation: {why}");
            return (dx, dz);
        }

        /// <summary>Squared distance from a point to a segment, in snap units, as a double.</summary>
        private static double DistToSegment(double px, double pz, double ax, double az, double bx, double bz)
        {
            double vx = bx - ax, vz = bz - az;
            double wx = px - ax, wz = pz - az;
            double len2 = vx * vx + vz * vz;
            double t = len2 == 0 ? 0 : System.Math.Max(0, System.Math.Min(1, (wx * vx + wz * vz) / len2));
            double cx = ax + t * vx, cz = az + t * vz;
            return System.Math.Sqrt((px - cx) * (px - cx) + (pz - cz) * (pz - cz));
        }

        private static bool InsideConvex(long[] x, long[] z, double px, double pz)
        {
            int n = x.Length;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double cross = (double)(x[j] - x[i]) * (pz - z[i]) - (double)(z[j] - z[i]) * (px - x[i]);
                if (cross < 0) return false;
            }
            return true;
        }

        // ── the claim that matters ───────────────────────────────────────────

        [TestCase(0.5)]
        [TestCase(1.0)]
        [TestCase(0.25)]
        public void ExpandedDiamond_ContainsTheWholeMinkowskiSum(double radius)
        {
            // The real completion criterion for the expansion: EVERY point within r of the
            // footprint must end up inside the expanded polygon. Not "points near the boundary" —
            // the Minkowski sum is the footprint itself plus the r-collar, and the collar is
            // where an inscribed approximation loses.
            var src = Diamond(3.0);
            var exp = Expand(src, radius);
            double rSnap = radius * Unit;

            int checkedPoints = 0;
            for (double a = 0; a < 360; a += 3.0)
            {
                double rad = a * System.Math.PI / 180.0;
                // Walk the source outline, then step outward by exactly r along every direction.
                for (int e = 0; e < 4; e++)
                {
                    int f = (e + 1) % 4;
                    for (double t = 0; t <= 1.0; t += 0.1)
                    {
                        double bx = src.x[e] + t * (src.x[f] - src.x[e]);
                        double bz = src.z[e] + t * (src.z[f] - src.z[e]);
                        double px = bx + rSnap * System.Math.Cos(rad);
                        double pz = bz + rSnap * System.Math.Sin(rad);
                        // Only points that really are within r of the footprint are claimed.
                        double d = double.MaxValue;
                        for (int k = 0; k < 4; k++)
                            d = System.Math.Min(d, DistToSegment(px, pz, src.x[k], src.z[k],
                                                                 src.x[(k + 1) % 4], src.z[(k + 1) % 4]));
                        if (d > rSnap) continue;
                        Assert.IsTrue(InsideConvex(exp.x, exp.z, px, pz),
                            $"({px / Unit:F3},{pz / Unit:F3}) is within r of the footprint but "
                            + "fell outside the expanded polygon — the expansion is not conservative");
                        checkedPoints++;
                    }
                }
            }
            Assert.Greater(checkedPoints, 500, "the sampling must actually cover the collar");
        }

        [Test]
        public void InscribedApproximation_WouldBeCaught()
        {
            // A mutation guard for the test itself: shrink the expanded diamond back toward the
            // source and the sampling above must reject it. Without this, a sampling test that
            // silently covers nothing looks identical to one that passes.
            var src = Diamond(3.0);
            var exp = Expand(src, 0.5);
            double rSnap = 0.5 * Unit;

            // Pull every expanded vertex 20% of the way back — a plausible "inscribed" error.
            var shrunk = (x: new long[4], z: new long[4]);
            for (int i = 0; i < 4; i++)
            {
                shrunk.x[i] = src.x[i] + (exp.x[i] - src.x[i]) * 8 / 10;
                shrunk.z[i] = src.z[i] + (exp.z[i] - src.z[i]) * 8 / 10;
            }

            bool foundEscape = false;
            for (int e = 0; e < 4 && !foundEscape; e++)
            {
                double bx = (src.x[e] + src.x[(e + 1) % 4]) / 2.0;
                double bz = (src.z[e] + src.z[(e + 1) % 4]) / 2.0;
                // Straight out from the edge midpoint — where the miter has zero slack.
                double nx = src.z[(e + 1) % 4] - src.z[e], nz = -(src.x[(e + 1) % 4] - src.x[e]);
                double len = System.Math.Sqrt(nx * nx + nz * nz);
                double px = bx + rSnap * nx / len, pz = bz + rSnap * nz / len;
                if (!InsideConvex(shrunk.x, shrunk.z, px, pz)) foundEscape = true;
            }
            Assert.IsTrue(foundEscape,
                "a 20% inscribed error must leave a point of the collar outside — if this fails "
                + "the sampling above is not looking where the miter is tight");
        }

        // ── the construction check has to be able to fail ────────────────────

        [Test]
        public void Validate_RejectsAnExpansionThatIsTooSmall()
        {
            var src = Diamond(3.0);
            var exp = Expand(src, 0.5);
            // Nudge one vertex inward by a couple of snap units — far below any sampling grid,
            // exactly the scale Validate exists for.
            exp.x[0] -= 3;

            Assert.IsFalse(
                FPConvexOffset.Validate(src.x, src.z, 0, 4, exp.x, exp.z, 0, FP64.FromDouble(0.5), out string why),
                "a support line pulled inside r must be refused");
            StringAssert.Contains("closer than the agent radius", why);
        }

        [Test]
        public void Validate_RejectsANonConvexResult()
        {
            var src = Diamond(3.0);
            var exp = Expand(src, 0.5);
            exp.x[0] = 0; exp.z[0] = 0;   // fold a vertex into the middle

            Assert.IsFalse(
                FPConvexOffset.Validate(src.x, src.z, 0, 4, exp.x, exp.z, 0, FP64.FromDouble(0.5), out string why),
                "a folded result must be refused");
            StringAssert.Contains("convex", why);
        }

        // ── shape-independence and determinism ───────────────────────────────

        [Test]
        public void BoxExpansion_MatchesTheAxisAlignedFormula_UpToThePadding()
        {
            // The rect path keeps its own SnapFloor/SnapCeil expansion (that is what makes the
            // goldens hold), so this is not used for shapeId 0. It is still worth knowing the two
            // agree: the miter of an axis-aligned box is the box grown by r on each side, and the
            // only difference is the deliberate padding.
            var src = Box(4.0, 2.0);
            var exp = Expand(src, 0.5);

            long expectedHalfW = 2 * Unit + Unit / 2 + FPConvexOffset.SnapPaddingUnits;
            long expectedHalfH = 1 * Unit + Unit / 2 + FPConvexOffset.SnapPaddingUnits;
            Assert.AreEqual(expectedHalfW, exp.x[1], "right side");
            Assert.AreEqual(-expectedHalfW, exp.x[0], "left side");
            Assert.AreEqual(expectedHalfH, exp.z[2], "top side");
            Assert.AreEqual(-expectedHalfH, exp.z[0], "bottom side");
        }

        [Test]
        public void Expansion_IsDeterministic()
        {
            var src = Diamond(3.0);
            var a = Expand(src, 0.5);
            var b = Expand(src, 0.5);
            CollectionAssert.AreEqual(a.x, b.x);
            CollectionAssert.AreEqual(a.z, b.z);
        }

        [Test]
        public void ShapeBeyondInt64_IsRefused_NotSilentlyUnexpanded()
        {
            // The outcome this file exists to prevent is not a crash, it is a polygon that LOOKS
            // right and carves wrong. An expansion whose |n| came back as 0 has push = 0 — no
            // agent radius at all — and nothing downstream would notice. So a shape whose
            // arithmetic leaves Int64 must be REFUSED.
            //
            // Honest about what this pins. It gates the refusal, NOT any single `checked` block:
            // reverting the entry-arithmetic guard added for C-10 leaves this green, because the
            // miter's own long-standing checked block catches these magnitudes too. Two shapes
            // were tried to separate them — a wide box, and this needle whose span is all in z so
            // that nx*nx wraps while nx*ax (ax is 0 or ±1) stays small — and both are refused
            // either way. That is the finding rather than a gap in the test: the entry guard is
            // convention-completion, and its value is that "is the next product always large
            // enough to catch it?" stops being an argument anyone has to re-derive.
            long tall = 2_000_000_000L;   // one edge spans 4e9 -> nx*nx is ~1.6e19, past Int64
            var x = new[] { 0L, 1L, -1L };
            var z = new[] { -tall, tall, tall };
            var dx = new long[3];
            var dz = new long[3];

            Assert.Throws<ArgumentException>(
                () => FPConvexOffset.Expand(x, z, 0, 3, FP64.FromDouble(0.5), dx, dz, 0),
                "a shape whose arithmetic leaves Int64 must be refused (TooLarge), never folded into "
                + "an unexpanded polygon");
        }

        [Test]
        public void LargeButInDomainShape_StillExpands()
        {
            // The control for the refusal above — without it, a change that rejected everything
            // would satisfy that test.
            //
            // Sized at 500 world units, not at MAX_SNAPPED_COORD: the binding limit here is the
            // MITER's, not the coordinate domain's. Its products overflow at roughly a thousand
            // world units (the TooLarge message says so), which is far below the ~3e9 snap units
            // an edge delta needs to wrap lenSq. Worth knowing, because it means lenSq's wrap
            // takes a shape the miter's own long-standing `checked` would refuse anyway — the new
            // guard's job is to make that refusal happen for the RIGHT reason instead of letting
            // the first product through unchecked.
            var expanded = Expand(Box(500.0, 500.0), 0.5);
            Assert.AreEqual(4, expanded.x.Length);
        }
    }
}
