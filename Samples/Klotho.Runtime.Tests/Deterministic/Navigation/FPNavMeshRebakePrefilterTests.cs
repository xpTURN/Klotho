using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The validation-loop AABB prefilter.
    ///
    /// The prefilter itself is measured by the goldens (same fingerprints before and after) and
    /// by the full suite. What THOSE cannot see is the direction the prefilter actually goes
    /// wrong in: a REJECTION turning into an acceptance. A fingerprint only exists when a rebake
    /// succeeds, so a rejection that stops happening is a fingerprint appearing where there was
    /// none — invisible to any golden comparison. These tests own that half.
    ///
    /// ONLY ONE DIRECTION IS TESTABLE. The filter is a conservative reject, so WEAKENING it
    /// (dropping an axis, widening the box) keeps more edges alive and is behaviour-neutral by
    /// construction — measured: removing the z comparisons entirely fails nothing. There is no
    /// test to write for that, and writing one wastes the next person's time. What can go wrong
    /// is TIGHTENING, and that is what the first test below pins.
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakePrefilterTests
    {
        private const double R = 0.5;

        /// <summary>Solid 16x16 slab, vertices every 2 units, boundary ring at +-8.</summary>
        private static FPNavMesh BuildSlab()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -8; x <= 8; x += 2)
                for (int z = -8; z <= 8; z += 2)
                    pts.Add((x, z));

            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: R);
        }

        private static FPBuildingRect B(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static (bool ok, string message) Try(FPNavMesh baseMesh, FPBuildingRect[] b)
        {
            try { FPNavMeshRebaker.Rebake(baseMesh, b, null); return (true, null); }
            catch (Exception e) { return (false, e.Message); }
        }

        [Test]
        public void RingEdgeLyingExactlyOnARectSide_SurvivesThePrefilter()
        {
            // The prefilter's comparisons are strict for exactly this case. The expanded rect is
            // [-8,-6] x [-1,1], so the outer ring's x = -8 edges lie ON its left side — their
            // AABBs meet the rect at a single line, nothing more. Loosening `<` to `<=` skips
            // them, the touch goes unseen, and the placement is accepted: a rejection silently
            // becomes an acceptance, which no golden can catch.
            FPNavMesh baseMesh = BuildSlab();
            var (ok, message) = Try(baseMesh, new[] { B(-7.5, -0.5, -6.5, 0.5) });

            Assert.IsFalse(ok, "a building flush against the boundary must still be rejected");
            StringAssert.Contains("touches or crosses the walkable boundary", message);
        }

        [Test]
        public void BoundaryPrecision_FlushRejected_OneStepOffAccepted()
        {
            // The accept/reject line sits exactly where the expanded rect stops touching the
            // ring, and the prefilter must not move it. Two placements a single grid step apart
            // straddle that line.
            FPNavMesh baseMesh = BuildSlab();

            Assert.IsFalse(Try(baseMesh, new[] { B(-7.5, 0.5, -6.5, 1.5) }).ok,
                "expanded to x = -8: flush against the boundary ring, rejected");
            Assert.IsTrue(Try(baseMesh, new[] { B(-6.5, 0.5, -5.5, 1.5) }).ok,
                "one step off: accepted");
        }

        [Test]
        public void SwallowedRing_IsStillSeen_ThroughThePrefilter()
        {
            // Regression guard, not a verification: it is proved that the prefilter cannot hide a
            // swallowed ring (a vertex strictly inside the rect forces the AABBs to meet). The
            // guard exists so that a future change to the filter has to notice.
            FPNavMesh baseMesh = BuildAnnulus();
            var (ok, message) = Try(baseMesh, new[] { B(-4, -4, 4, 4) });

            Assert.IsFalse(ok);
            StringAssert.Contains("contains a baked hole ring", message,
                "the swallow test must still see the ring the prefilter walked past");
        }

        private static FPNavMesh BuildAnnulus()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -8; x <= 8; x += 2)
                for (int z = -8; z <= 8; z += 2)
                    pts.Add((x, z));

            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            var index = new Dictionary<(int, int), int>();
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
                index[pts[i]] = i;
            }

            var constraints = new List<int>();
            void Ring(params (int x, int z)[] loop)
            {
                for (int i = 0; i < loop.Length; i++)
                {
                    constraints.Add(index[loop[i]]);
                    constraints.Add(index[loop[(i + 1) % loop.Length]]);
                }
            }
            var outer = new List<(int, int)>();
            for (int x = -8; x <= 8; x += 2) outer.Add((x, -8));
            for (int z = -6; z <= 8; z += 2) outer.Add((8, z));
            for (int x = 6; x >= -8; x -= 2) outer.Add((x, 8));
            for (int z = 6; z >= -6; z -= 2) outer.Add((-8, z));
            Ring(outer.ToArray());
            Ring((-2, -2), (0, -2), (2, -2), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0));

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: R);
        }
    }
}
