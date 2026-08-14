using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The polygon path, proved with a shape a rect cannot express.
    ///
    /// The rest of the suite (and the goldens) only ever feeds this path axis-aligned rectangles,
    /// so all of it would still pass if the "polygon" generalisation were AABB code wearing an
    /// array. The 45-degree diamond is the cheapest counter-example: its non-axis-aligned edges
    /// go through the separating-axis test, the n-side boundary crossing, the point-in-convex
    /// swallow test and the n-vertex hole emission, and none of those can shortcut to min/max.
    ///
    /// EVERY FIXTURE HERE USES bakeAgentRadius = 0, and that is the point of the split. With
    /// r = 0 the expansion is the identity, so a failure here is the GEOMETRY — not the miter,
    /// the padding, the snapped offset table or the tiling, which arrive with the expansion. Once
    /// the expansion lands,
    /// the same diamonds get re-run with r > 0 and a failure there means the expansion.
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakePolygonTests
    {
        /// <summary>20x20 slab, vertices every 2 units, NO agent radius (see the class note).</summary>
        private static FPNavMesh BuildSlab()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 2)
                for (int z = -10; z <= 10; z += 2)
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.0);
        }

        /// <summary>
        /// A diamond centred at (cx, cz) with half-diagonal d — corners at (+-d, 0) and (0, +-d)
        /// from the centre, so every vertex is exactly on the snap grid and no rounding enters.
        /// CCW.
        /// </summary>
        private static (double x, double z)[] Diamond(double cx, double cz, double d) => new[]
        {
            (cx + d, cz), (cx, cz + d), (cx - d, cz), (cx, cz - d),
        };

        private static FPNavMesh Rebake(
            FPNavMesh baseMesh, (double x, double z)[][] polys, FPBuildingPlacementRules rules = default)
        {
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);

            int total = 0;
            foreach (var p in polys) total += p.Length;
            var px = new long[System.Math.Max(1, total)];
            var pz = new long[System.Math.Max(1, total)];
            var start = new int[polys.Length + 1];
            var bounds = new long[System.Math.Max(1, polys.Length * 4)];
            var ys = new FP64[System.Math.Max(1, polys.Length)];

            int n = 0;
            for (int b = 0; b < polys.Length; b++)
            {
                start[b] = n;
                long minX = long.MaxValue, minZ = long.MaxValue, maxX = long.MinValue, maxZ = long.MinValue;
                foreach (var (x, z) in polys[b])
                {
                    px[n] = FPGeoPredicates.Snap(FP64.FromDouble(x));
                    pz[n] = FPGeoPredicates.Snap(FP64.FromDouble(z));
                    minX = System.Math.Min(minX, px[n]); maxX = System.Math.Max(maxX, px[n]);
                    minZ = System.Math.Min(minZ, pz[n]); maxZ = System.Math.Max(maxZ, pz[n]);
                    n++;
                }
                bounds[b * 4] = minX; bounds[b * 4 + 1] = minZ;
                bounds[b * 4 + 2] = maxX; bounds[b * 4 + 3] = maxZ;
            }
            start[polys.Length] = n;

            // The core reports a refused placement by VALUE now, so this helper re-raises it —
            // the fixture below is written around exceptions and that is still the presentation
            // the throwing overloads give. Rejected() is the one place the text lives, so going
            // through it keeps this fixture honest about what a caller would actually see.
            FPNavMesh mesh = FPNavMeshRebaker.RebakeFromPolygons(
                snapshot, px, pz, start, bounds, ys, polys.Length, n, null, null, rules,
                out FPBuildingRejectionInfo rejection);
            if (mesh == null)
                throw FPNavMeshRebaker.Rejected(rejection, rules);
            return mesh;
        }

        /// <summary>Same slab, but with a real agent radius — the expansion half.</summary>
        private static FPNavMesh BuildSlabWithRadius(double radius)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 2)
                for (int z = -10; z <= 10; z += 2)
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: radius);
        }

        /// <summary>Feeds FOOTPRINTS (unexpanded) through the expansion seam.</summary>
        private static FPNavMesh RebakeFootprints(
            FPNavMesh baseMesh, (double x, double z)[][] polys, FPBuildingPlacementRules rules = default)
        {
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);
            int total = 0;
            foreach (var p in polys) total += p.Length;
            var px = new long[System.Math.Max(1, total)];
            var pz = new long[System.Math.Max(1, total)];
            var start = new int[polys.Length + 1];
            var ys = new FP64[System.Math.Max(1, polys.Length)];

            int n = 0;
            for (int b = 0; b < polys.Length; b++)
            {
                start[b] = n;
                foreach (var (x, z) in polys[b])
                {
                    px[n] = FPGeoPredicates.Snap(FP64.FromDouble(x));
                    pz[n] = FPGeoPredicates.Snap(FP64.FromDouble(z));
                    n++;
                }
            }
            start[polys.Length] = n;
            FPNavMesh mesh = FPNavMeshRebaker.RebakeFromFootprints(
                snapshot, px, pz, start, ys, polys.Length, n, null, null, rules,
                out FPBuildingRejectionInfo rejection);
            if (mesh == null)
                throw FPNavMeshRebaker.Rejected(rejection, rules);
            return mesh;
        }

        private static (bool ok, string message) TryFootprints(
            FPNavMesh baseMesh, (double x, double z)[][] polys, FPBuildingPlacementRules rules = default)
        {
            try { RebakeFootprints(baseMesh, polys, rules); return (true, null); }
            catch (Exception e) { return (false, e.Message); }
        }

        private static (bool ok, string message) Try(
            FPNavMesh baseMesh, (double x, double z)[][] polys, FPBuildingPlacementRules rules = default)
        {
            try { Rebake(baseMesh, polys, rules); return (true, null); }
            catch (Exception e) { return (false, e.Message); }
        }

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        // ── the diamond actually carves ──────────────────────────────────────

        [Test]
        public void Diamond_IsCarved_AndOnlyWhereItCovers()
        {
            // "It was accepted" and "it blocks the right region" are different claims, and only
            // the second can tell an axis-aligned shortcut from real polygon handling: a min/max
            // implementation would block the diamond's BOUNDING BOX, so the corners of that box
            // are the discriminator.
            FPNavMesh baseMesh = BuildSlab();
            FPNavMesh mesh = Rebake(baseMesh, new[] { Diamond(0, 0, 4) });

            Assert.IsFalse(Walkable(mesh, 0, 0), "the diamond's centre is blocked");
            Assert.IsFalse(Walkable(mesh, 2, 0), "and points inside it");
            Assert.IsFalse(Walkable(mesh, 0, -2), "on both axes");

            Assert.IsTrue(Walkable(mesh, 3, 3),
                "inside the diamond's bounding box but OUTSIDE the diamond — an AABB "
                + "implementation would have blocked this");
            Assert.IsTrue(Walkable(mesh, -3, 3), "the other box corners too");
            Assert.IsTrue(Walkable(mesh, 3, -3));
            Assert.IsTrue(Walkable(mesh, -3, -3));
        }

        // ── SAT sees what min/max cannot ─────────────────────────────────────

        [Test]
        public void DiamondsWhoseBoxesOverlap_ButShapesDoNot_AreAccepted()
        {
            // The discriminator for the pairwise test. Centres (0,0) and (4,4), half-diagonal 3:
            // the bounding boxes overlap over the whole [1,3]^2 square, so an AABB pair test
            // rejects — but along the x+z axis the shapes span [-3,3] and [5,11], a clear gap.
            // Only a test that reads the polygons can accept this, and it must, with no game
            // rule needed.
            FPNavMesh baseMesh = BuildSlab();

            var (ok, message) = Try(baseMesh, new[] { Diamond(0, 0, 3), Diamond(4, 4, 3) });

            Assert.IsTrue(ok, $"boxes overlap over [1,3]^2 but the shapes are 2 apart: {message}");
        }

        [Test]
        public void SeparationFoundOnlyOnTheOtherShapesNormals_IsStillFound()
        {
            // SAT must test BOTH polygons' edge normals. Two diamonds cannot show that — their
            // normal sets are identical, so one side answers for both and dropping the other
            // changes nothing. It takes shapes whose normals differ.
            //
            // Diamond at (-3,1) half-diagonal 2 (normals: the diagonals) and the axis-aligned
            // square [0,2]^2 (normals: the axes). Verified: on the DIAMOND's normals the two
            // merely touch (x+z meets at 0, x-z at -2) — no separation. On the SQUARE's normals
            // they are a clear unit apart in x. Drop the square's side of the test and this
            // legal placement is rejected.
            FPNavMesh baseMesh = BuildSlab();
            var diamond = Diamond(-3, 1, 2);
            var square = new (double x, double z)[] { (0, 0), (2, 0), (2, 2), (0, 2) };

            var (ok, message) = Try(baseMesh, new[] { diamond, square });

            Assert.IsTrue(ok,
                $"separated on the square's axes but not on the diamond's diagonals: {message}");
        }

        [Test]
        public void DiamondsThatActuallyOverlap_AreStillRejected()
        {
            // Centres 2 apart on the diagonal, half-diagonal 3 — x+z spans [-3,3] and [1,7], and
            // x-z spans [-3,3] both. No separating axis, so the interiors really do meet.
            FPNavMesh baseMesh = BuildSlab();
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);

            var (ok, message) = Try(baseMesh, new[] { Diamond(0, 0, 3), Diamond(2, 2, 3) }, touching);

            Assert.IsFalse(ok, "interiors meet — must be rejected even with touching allowed");
            StringAssert.Contains("overlap", message);
        }

        [Test]
        public void TouchingDiamonds_FollowTheGameRule()
        {
            // Centres exactly 2d apart on the diagonal: x+z spans [-3,3] and [3,9], meeting at a
            // single value — the two diamonds share a full edge. The touch policy has to reach
            // the polygon path unchanged.
            FPNavMesh baseMesh = BuildSlab();
            var pair = new[] { Diamond(0, 0, 3), Diamond(3, 3, 3) };

            Assert.IsFalse(Try(baseMesh, pair).ok,
                "edge contact is rejected by default, exactly as for rects");
            Assert.IsTrue(Try(baseMesh, pair, new FPBuildingPlacementRules(allowBuildingTouch: true)).ok,
                "and accepted once the game allows touching");
        }

        // ── boundary and parity read the sides, not the box ──────────────────

        [Test]
        public void DiamondCrossingTheBoundary_IsRejected()
        {
            FPNavMesh baseMesh = BuildSlab();
            var (ok, message) = Try(baseMesh, new[] { Diamond(-9, 0, 3) });

            Assert.IsFalse(ok);
            StringAssert.Contains("walkable boundary", message);
        }

        [Test]
        public void DiamondWhoseBoxTouchesTheBoundary_ButShapeDoesNot_IsAccepted()
        {
            // Centre (-7, 0), half-diagonal 3: the bounding box reaches x = -10, which is exactly
            // the boundary ring — an AABB check would call this a touch and reject. The diamond
            // itself reaches x = -10 at a single vertex... which IS on the ring, so this must
            // still be rejected. Shift one unit in and the box still touches while no side does.
            FPNavMesh baseMesh = BuildSlab();

            Assert.IsFalse(Try(baseMesh, new[] { Diamond(-7, 0, 3) }).ok,
                "the left vertex lands exactly on the boundary ring — touch, rejected");
            Assert.IsTrue(Try(baseMesh, new[] { Diamond(-6, 0, 3) }).ok,
                "one unit in: no side and no vertex meets the ring");
        }

        [Test]
        public void DiamondOutsideWalkable_IsRejectedByParity()
        {
            FPNavMesh baseMesh = BuildSlab();
            var (ok, message) = Try(baseMesh, new[] { Diamond(30, 30, 2) });

            Assert.IsFalse(ok);
            StringAssert.Contains("outside the walkable region", message);
        }

        // ── determinism ──────────────────────────────────────────────────────

        // ── the same diamond, now through the expansion ─────────────────────

        [Test]
        public void ExpandedDiamond_BlocksTheCollar_NotJustTheFootprint()
        {
            // The expansion half of the proof. Same shape, same path, but r > 0 — so the miter, the
            // padding and the outward snap are all in play. What must hold is the promise the
            // expansion exists for: a point within r of the footprint is unreachable.
            //
            // The discriminator is the same as at r = 0 — the diamond's bounding-box corners stay
            // walkable — but now measured against the EXPANDED diamond, which is bigger.
            const double R = 0.5;
            FPNavMesh baseMesh = BuildSlabWithRadius(R);
            FPNavMesh mesh = RebakeFootprints(baseMesh, new[] { Diamond(0, 0, 4) });

            Assert.IsFalse(Walkable(mesh, 0, 0), "the footprint is blocked");
            Assert.IsFalse(Walkable(mesh, 4.2, 0),
                "and so is the collar just outside it — this is what the expansion buys");
            Assert.IsFalse(Walkable(mesh, 0, -4.2), "on every side");
            Assert.IsFalse(Walkable(mesh, 2.3, 2.0), "including out from an edge midpoint, "
                + "where the miter has no slack at all");

            Assert.IsTrue(Walkable(mesh, 6, 6),
                "well outside the expanded diamond but inside its bounding box — still walkable");
        }

        [Test]
        public void ExpandedDiamonds_RespectTheTouchPolicy()
        {
            // Expansion is what makes contact authorable at all: both diamonds get
            // the SAME offset, so a placement that touches after expansion is something a game
            // can actually aim for. Centres 2*(d + r_eff) apart on the diagonal would touch
            // exactly; here they are comfortably apart, which must be accepted either way, and
            // then brought close enough to overlap, which must not be.
            const double R = 0.5;
            FPNavMesh baseMesh = BuildSlabWithRadius(R);

            Assert.IsTrue(TryFootprints(baseMesh, new[] { Diamond(-4, -4, 2), Diamond(4, 4, 2) }).ok,
                "far apart: accepted");

            var (ok, message) = TryFootprints(
                baseMesh, new[] { Diamond(-1, -1, 2), Diamond(1, 1, 2) },
                new FPBuildingPlacementRules(allowBuildingTouch: true));
            Assert.IsFalse(ok, "expanded interiors overlap: rejected even with touching allowed");
            StringAssert.Contains("overlap", message);
        }

        [Test]
        public void ExpansionFailure_IsAnArgumentError_NotAPlacementRejection()
        {
            // A footprint that cannot be offset is a broken SHAPE, not a bad POSITION. The game
            // handles those differently — a rejected placement is a normal event that flows back
            // to the player, a broken shape is a bug in the catalog — so they must not arrive as
            // the same exception.
            const double R = 0.5;
            FPNavMesh baseMesh = BuildSlabWithRadius(R);

            // Three collinear points: no interior, no offset.
            var degenerate = new[] { new (double x, double z)[] { (0, 0), (1, 0), (2, 0) } };
            Assert.Throws<ArgumentException>(() => RebakeFootprints(baseMesh, degenerate));
        }

        [Test]
        public void SameDiamonds_ProduceBitIdenticalMeshes()
        {
            FPNavMesh baseMesh = BuildSlab();
            var polys = new[] { Diamond(0, 0, 4) };

            FPNavMesh a = Rebake(baseMesh, polys);
            FPNavMesh b = Rebake(baseMesh, polys);

            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(a), FPNavMeshRebaker.ComputeFingerprint(b));
            Assert.AreEqual(a.TriangleCount, b.TriangleCount);
        }
    }
}
