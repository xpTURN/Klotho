using System;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Quantized oriented boxes.
    ///
    /// The polygon path and the shape catalog already existed, so this is table entries plus two
    /// re-checks the rotation makes necessary: the four-turn return has to be EXACT, and
    /// conservativeness has to be re-proved at the highest aspect ratio, because a long thin box is
    /// where per-coordinate outward snapping can push a support line inward.
    /// </summary>
    [TestFixture]
    public class FPBuildingObbTests
    {
        private const int Unit = (int)FPGeoPredicates.SNAP_UNITS_PER_WORLD;
        private static readonly FP64 R = FP64.FromDouble(0.5);

        /// <summary>One shape that turns m ways — shape id 0, orientations 0..m-1.</summary>
        private static FPBuildingShapeCatalog Obb(long hw, long hd, int m)
        {
            var b = new FPBuildingShapeCatalogBuilder();
            b.AddObb(hw, hd, m);
            return b.Build();
        }

        private const int Box = 0;

        // ── four turns return exactly ────────────────────────────────────────

        [Test]
        public void AdvancingByAQuarterFourTimes_ReturnsTheIdenticalIntegers()
        {
            // Players expect four turns to come back. "Almost" is not enough: a box that drifts one
            // snap unit per turn loses the flush contact it had with its neighbour, so the drift
            // shows up as a placement rejection much later and nowhere near its cause.
            //
            // Exact because only the first quarter of the directions comes from trigonometry — the
            // rest are integer 90-degree rotations of the quarter before, so four advances apply
            // that map four times and land on the original integers by construction.
            foreach (int m in new[] { 4, 8, 16, 32, 64 })
            {
                FPBuildingShapeCatalog.ObbOffsets(2 * Unit, Unit, m, out long[] x, out long[] z, out _);
                int quarter = m / 4;
                for (int k = 0; k < m; k++)
                {
                    int cur = k;
                    for (int turn = 0; turn < 4; turn++)
                        cur = (cur + quarter) % m;
                    Assert.AreEqual(k, cur, $"M={m}: four quarter-turns must be the identity on the index");
                    for (int i = 0; i < 4; i++)
                    {
                        Assert.AreEqual(x[k * 4 + i], x[cur * 4 + i], $"M={m} entry {k} vertex {i} x");
                        Assert.AreEqual(z[k * 4 + i], z[cur * 4 + i], $"M={m} entry {k} vertex {i} z");
                    }
                }
            }
        }

        [Test]
        public void EveryOrientation_IsAValidTilingEntry()
        {
            // Central symmetry has to survive rotation, or the turned box loses its tiling delta
            // and can no longer be built flush against its neighbour. It survives here because
            // opposite corners are NEGATED rather than rounded independently, and because the
            // integer 90-degree map takes -v to -R(v).
            var cat = Obb(2 * Unit, Unit, 16);
            Assert.AreEqual(1, cat.ShapeCount, "one shape...");
            Assert.AreEqual(16, cat.DirectionCount(Box), "...that turns 16 ways");
            Assert.AreEqual(16, cat.EntryCount);
            var expansion = new FPBuildingShapeExpansion(cat, R);
            for (int o = 0; o < 16; o++)
            {
                Assert.AreEqual(4, cat.VertexCount(cat.TryResolveEntry(Box, o)),
                    $"orientation {o} is a quadrilateral");
                Assert.IsTrue(cat.IsCentrallySymmetric(Box, o), $"orientation {o} is centrally symmetric");
                Assert.IsTrue(cat.TilesThePlane(Box, o), $"orientation {o} can fill the plane");
                Assert.IsTrue(expansion.TryTilingDelta(Box, o, 0, out long dx, out long dz),
                    $"orientation {o} delta");
                Assert.AreNotEqual(0, dx | dz, $"orientation {o} delta is a real translation");
            }
        }

        [Test]
        public void EveryOrientation_HasItsOwnVertexArray()
        {
            // Directions that snapped onto identical integers would make two entries literally
            // interchangeable — the sign that M is finer than the grid can express at this size.
            //
            // NOT the same as "M distinct footprints": see the next test.
            for (int m = 4; m <= 64; m *= 2)
            {
                FPBuildingShapeCatalog.ObbOffsets(2 * Unit, Unit, m, out long[] x, out long[] z, out _);
                var seen = new System.Collections.Generic.HashSet<(long, long, long, long)>();
                for (int e = 0; e < m; e++)
                    seen.Add((x[e * 4], z[e * 4], x[e * 4 + 1], z[e * 4 + 1]));
                Assert.AreEqual(m, seen.Count, $"M={m}: no two entries may share a vertex array");
            }
        }

        [Test]
        public void HalfTheDirectionsAreTheSameFootprintTurnedAround()
        {
            // A box is centrally symmetric, so turning it 180 degrees maps it onto itself. Entry k
            // and entry k + M/2 are therefore the SAME footprint carved in the same place — the
            // vertex arrays differ only in where the ring starts.
            //
            // Which means M is the number of steps around a FULL circle, and the number of shapes a
            // player can actually see is M/2. A rotate button offering all M is fine (rotation
            // feels continuous through 360 degrees), but a UI that expects M distinct previews
            // would show each one twice.
            const int M = 16;
            FPBuildingShapeCatalog.ObbOffsets(2 * Unit, Unit, M, out long[] x, out long[] z, out _);

            string Footprint(int e)
            {
                var v = new System.Collections.Generic.List<(long, long)>();
                for (int i = 0; i < 4; i++) v.Add((x[e * 4 + i], z[e * 4 + i]));
                v.Sort();
                return string.Join(";", v);
            }

            var distinct = new System.Collections.Generic.HashSet<string>();
            for (int e = 0; e < M; e++) distinct.Add(Footprint(e));
            Assert.AreEqual(M / 2, distinct.Count, "a box gives M/2 distinct footprints, not M");

            for (int e = 0; e < M / 2; e++)
                Assert.AreEqual(Footprint(e), Footprint(e + M / 2),
                    $"entry {e} and {e + M / 2} are the same footprint turned 180 degrees");
        }

        [Test]
        public void PlacementWithABadShapeOrOrientation_IsRefusedByName()
        {
            // The reason a placement names shape and orientation separately. With one flat index a
            // game writes smallBox + orientation, and an orientation one past the end is STILL a
            // valid index — it addresses the NEXT shape. Nothing throws, nothing logs, a different
            // building appears, and because every peer computes it the same way no desync check
            // fires either. Here it is refused, and the message says which of the two was wrong.
            var b = new FPBuildingShapeCatalogBuilder();
            int smallBox = b.AddObb(Unit / 2, Unit / 2, directions: 8);
            int bigBox = b.AddObb(2 * Unit, Unit, directions: 16);
            var cat = b.Build();
            Assert.AreEqual(8, cat.TryResolveEntry(bigBox, 0), "entry 8 is a real entry...");
            Assert.AreEqual(-1, cat.TryResolveEntry(smallBox, 8), "...but not orientation 8 of the small box");

            var snap = FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false, shapeCatalog: cat);

            var overturned = new[] { new FPBuildingPlacement(smallBox, 8, FP64.Zero, FP64.Zero, FP64.Zero) };
            var e = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.RebakePlacements(snap, overturned, null));
            StringAssert.Contains("orientation 8", e.Message);

            var unknown = new[] { new FPBuildingPlacement(7, 0, FP64.Zero, FP64.Zero, FP64.Zero) };
            var e2 = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.RebakePlacements(snap, unknown, null));
            StringAssert.Contains("shape 7", e2.Message);
        }

        [Test]
        public void DirectionCount_MustBeAMultipleOfFour()
        {
            // The four-turn guarantee is structural, so a count that cannot express it is refused at
            // build time rather than silently giving three-and-a-bit turns.
            foreach (int bad in new[] { 0, -4, 1, 2, 3, 6, 10 })
                Assert.Throws<ArgumentException>(
                    () => FPBuildingShapeCatalog.ObbOffsets(2 * Unit, Unit, bad, out _, out _, out _),
                    $"directions = {bad}");
        }

        // ── conservativeness at the worst aspect ratio ───────────────────────

        [Test]
        public void ThinBoxes_StayConservative_AtEveryOrientation()
        {
            // The long thin box is the worst case: its edge normals are near
            // the axes and its vertices are far from the centre, which is the arrangement where a
            // per-coordinate outward snap has a NEGATIVE component along the edge's own normal — and
            // the miter has no slack at an edge midpoint, so any inward nudge breaks containment.
            //
            // The expansion's own construction check is what proves it (support line of every source
            // edge pushed at least r, in integers) — sample-based checks cannot see a wobble this
            // small. Measured: the 2-snap-unit padding holds all the way to 128:1, so the worry
            // does not bite at any aspect ratio a building could have.
            foreach (var (hw, hd) in new[]
            {
                (1L * Unit, 1L * Unit),          //   1:1
                (8L * Unit, 1L * Unit),          //   8:1
                (8L * Unit, Unit / 4),           //  32:1
                (8L * Unit, Unit / 8),           //  64:1
                (8L * Unit, Unit / 16),          // 128:1
            })
            {
                var cat = Obb(hw, hd, 16);
                Assert.DoesNotThrow(() => new FPBuildingShapeExpansion(cat, R),
                    $"aspect {hw / (double)hd:F0}:1 must expand conservatively at all 16 orientations");
            }
        }

        [Test]
        public void DegenerateExtents_AreRefused()
        {
            Assert.Throws<ArgumentException>(() => Obb(0, Unit, 16), "zero half-width");
            Assert.Throws<ArgumentException>(() => Obb(Unit, 0, 16), "zero half-depth");
            Assert.Throws<ArgumentException>(() => Obb(-Unit, Unit, 16), "negative");
        }

        // ── the size ceiling the sweep surfaced ──────────────────────────────

        [Test]
        public void OversizedShapes_AreRefused_AsAnArithmeticLimit_NotAShapeDefect()
        {
            // The sweep found a ceiling, and finding it mattered for a reason beyond documentation:
            // before this, an oversized shape did not fail — the intermediate products WRAPPED, the
            // miter produced a polygon from wrapped comparisons, and the conservativeness check
            // wrapped the same way and agreed with it. A self-consistent wrong answer, which is the
            // worst shape this failure could take, and a hexagon of the same scale reached it too.
            // The arithmetic is `checked` now, so it throws.
            //
            // The message has to name the real cause. Reporting an Int64 overflow as "not strictly
            // convex" sends whoever hit it to inspect their polygon, which is fine.
            //
            // Real footprints are single-digit world units, so the ceiling (around a thousand) is a
            // diagnostic and not a restriction.
            var small = Obb(400 * Unit, 200 * Unit, 4);
            Assert.DoesNotThrow(() => new FPBuildingShapeExpansion(small, R),
                "a few hundred world units still expands");

            var huge = Obb(2000 * Unit, 1000 * Unit, 4);
            var e = Assert.Throws<ArgumentException>(() => new FPBuildingShapeExpansion(huge, R));
            StringAssert.Contains("too large for the miter arithmetic", e.Message,
                "the refusal must say it is an engine limit, not blame the polygon");

            // Not specific to boxes: the same ceiling is in the shared miter, so a hexagon of
            // comparable scale is refused identically. The box sweep surfaced it; it was never
            // specific to boxes.
            FPBuildingShapeCatalog.HexagonOffsets(4000 * Unit, out long[] hx, out long[] hz);
            var hugeHex = new FPBuildingShapeCatalog(hx, hz, new[] { 0, 6 });
            var e2 = Assert.Throws<ArgumentException>(() => new FPBuildingShapeExpansion(hugeHex, R));
            StringAssert.Contains("too large for the miter arithmetic", e2.Message);
        }

        // ── end to end: a turned box is placed, carved, and packed ───────────

        private static FPNavMesh BuildSlab()
        {
            var pts = new System.Collections.Generic.List<(int x, int z)>();
            for (int x = -20; x <= 20; x += 2)
                for (int z = -20; z <= 20; z += 2)
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        [Test]
        public void TheDocumentedPlacementIdiom_StaysOnTheGridAndPacks()
        {
            // Pins the idiom the guide tells games to use, because it is easy to write a variant
            // that looks equivalent and is not: going through a double (delta / 1024.0 back into
            // FP64) can land off the grid, and the rebaker then refuses the placement.
            //
            //     centre  = FPGeoPredicates.Quantize(cursor)
            //     neighbour = centre + FPGeoPredicates.Unsnap(tilingDelta)
            //
            // Adding an Unsnap'd delta is exact: the delta is a whole number of snap units, so a
            // centre already on the grid stays on it — and keeps staying on it as the pattern is
            // extended, which is what lets a row of boxes be built one delta at a time.
            var cat = Obb(2 * Unit, Unit, 16);
            var snap = FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false, shapeCatalog: cat);
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);
            const int orientation = 2;   // 45 degrees — genuinely off-axis

            // An arbitrary cursor position, quantised the way the guide says.
            FP64 cx = FPGeoPredicates.Quantize(FP64.FromDouble(-3.31));
            FP64 cz = FPGeoPredicates.Quantize(FP64.FromDouble(1.07));
            Assert.IsTrue(FPGeoPredicates.IsOnGrid(cx), "the quantised cursor is on the grid");
            Assert.IsTrue(FPGeoPredicates.IsOnGrid(cz));

            Assert.IsTrue(snap.ShapeExpansion.TryTilingDelta(Box, orientation, 0, out long dx, out long dz));

            // Walk the pattern three times over: every step must stay exactly on the grid.
            var places = new System.Collections.Generic.List<FPBuildingPlacement>();
            FP64 x = cx, z = cz;
            for (int i = 0; i < 3; i++)
            {
                places.Add(new FPBuildingPlacement(Box, orientation, x, z, FP64.Zero));
                x += FPGeoPredicates.Unsnap(dx);
                z += FPGeoPredicates.Unsnap(dz);
                Assert.IsTrue(FPGeoPredicates.IsOnGrid(x), $"step {i + 1} drifted off the grid in x");
                Assert.IsTrue(FPGeoPredicates.IsOnGrid(z), $"step {i + 1} drifted off the grid in z");
            }

            FPNavMesh mesh = null;
            Assert.DoesNotThrow(
                () => mesh = FPNavMeshRebaker.RebakePlacements(snap, places.ToArray(), null, touching),
                "the documented idiom must produce an accepted row of flush boxes");

            foreach (var p in places)
                Assert.IsFalse(Walkable(mesh, p.CentreX.ToDouble(), p.CentreZ.ToDouble()),
                    "every box in the row is solid");
            for (int i = 1; i < places.Count; i++)
                Assert.IsFalse(Walkable(mesh,
                        (places[i - 1].CentreX + places[i].CentreX).ToDouble() / 2,
                        (places[i - 1].CentreZ + places[i].CentreZ).ToDouble() / 2),
                    $"and the seam between box {i - 1} and {i} — they must actually be flush");
        }

        [Test]
        public void ATurnedBox_IsCarved_AndTwoOfThemPackFlush()
        {
            // The completion criterion, same shape as P3's: not "it triangulates" but "it can be
            // built, and built against its neighbour". A turned box that carves but cannot be
            // packed would leave the feature unusable for the layout a player actually builds.
            var cat = Obb(2 * Unit, Unit, 16);
            var snap = FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false, shapeCatalog: cat);
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);

            // Orientation 2 of 16 = 45 degrees — genuinely off-axis, which is the whole point of
            // P4 (a 90-degree-only "rotation" would just be the axis-aligned box again).
            const int orientation = 2;
            var single = new[] { new FPBuildingPlacement(Box, orientation, FP64.Zero, FP64.Zero, FP64.Zero) };
            FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(snap, single, null);
            Assert.IsFalse(Walkable(mesh, 0, 0), "the turned box blocks its own centre");
            Assert.IsTrue(Walkable(mesh, -8, -8), "and nothing else");

            // Now flush against a neighbour at the tiling delta for that orientation.
            Assert.IsTrue(snap.ShapeExpansion.TryTilingDelta(Box, orientation, 0, out long dx, out long dz));
            double wx = dx / (double)Unit, wz = dz / (double)Unit;
            var pair = new[]
            {
                new FPBuildingPlacement(Box, orientation, FP64.Zero, FP64.Zero, FP64.Zero),
                new FPBuildingPlacement(
                    Box, orientation, FP64.FromDouble(wx), FP64.FromDouble(wz), FP64.Zero),
            };

            Assert.IsFalse(TryPlace(snap, pair, default), "contact is still off by default");

            FPNavMesh packed = FPNavMeshRebaker.RebakePlacements(snap, pair, null, touching);
            Assert.IsFalse(Walkable(packed, 0, 0), "first box solid");
            Assert.IsFalse(Walkable(packed, wx, wz), "second box solid");
            Assert.IsFalse(Walkable(packed, wx / 2, wz / 2),
                "and the seam — a walkable seam would let an agent thread the pack");
        }

        private static bool TryPlace(
            FPNavMeshRebakeSnapshot snap, FPBuildingPlacement[] p, FPBuildingPlacementRules rules)
        {
            try { FPNavMeshRebaker.RebakePlacements(snap, p, null, rules); return true; }
            catch (Exception) { return false; }
        }

        [Test]
        public void RandomOrientations_OnAGridOfCentres_AreAllAccepted()
        {
            // Mirrors the Brawler sample's wiring (BrawlerBuildingShapes): a 2x1 footprint in 16
            // orientations, placed on a grid of whole-unit centres. The reason this is a test and
            // not arithmetic in a comment: the SAMPLE picks the orientation at random, so the
            // spacing has to clear the WORST case rather than the axis-aligned one. A 2x1 footprint
            // expands to 3x2, but turned 45 degrees its expanded bounding box is about 3.5 across —
            // pick the spacing from the axis-aligned figure and a diagonal draw quietly turns every
            // placement into a rejection, which reads as "the demo stopped working" and not as a
            // spacing bug.
            //
            // Every orientation is exercised at every site, so this covers the whole table rather
            // than whichever draw the RNG happened to make.
            var cat = Obb(Unit, Unit / 2, 16);
            var snap = FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false, shapeCatalog: cat);

            var centres = new System.Collections.Generic.List<(int x, int z)>();
            for (int gx = -1; gx <= 1; gx++)
                for (int gz = -1; gz <= 1; gz++)
                    centres.Add((gx * 4, gz * 4));

            for (int orientation = 0; orientation < 16; orientation++)
            {
                var places = new FPBuildingPlacement[centres.Count];
                for (int i = 0; i < centres.Count; i++)
                    places[i] = new FPBuildingPlacement(
                        Box, orientation,
                        FP64.FromInt(centres[i].x), FP64.FromInt(centres[i].z), FP64.Zero);

                FPNavMesh mesh = null;
                Assert.DoesNotThrow(
                    () => mesh = FPNavMeshRebaker.RebakePlacements(snap, places, null),
                    $"orientation {orientation}: centres 4 apart must clear even the worst turn");
                foreach (var (x, z) in centres)
                    Assert.IsFalse(Walkable(mesh, x, z), $"orientation {orientation}: ({x},{z}) blocked");
            }
        }

        /// <summary>The sample's combined table: 16 box orientations then one hexagon
        /// (BrawlerBuildingShapes). Rebuilt here rather than referenced — this project does not see
        /// the sample — so the numbers are the contract being pinned.</summary>
        private static FPBuildingShapeCatalog SampleCatalog()
        {
            var b = new FPBuildingShapeCatalogBuilder();
            b.AddObb(Unit, Unit / 2, 16);
            b.AddHexagon(Unit);
            return b.Build();
        }

        [Test]
        public void BoxesAndHexagons_Coexist_OnTheSampleGrid()
        {
            // The sample's headless demo alternates the two place commands at the same 4-unit
            // spacing, so the two shapes end up as neighbours. Neither shape's own clearance says
            // anything about that pairing: what has to fit is a 45-degree box (expanded half-extent
            // about 1.77) beside a hexagon (about 1.5), and 4 clears it — but only just enough that
            // it is worth an assertion rather than an estimate in a comment.
            const int Hex = 1;
            var cat = SampleCatalog();
            Assert.AreEqual(2, cat.ShapeCount, "a box and a hexagon");
            Assert.AreEqual(17, cat.EntryCount, "16 box orientations + 1 hexagon");
            Assert.AreEqual(1, cat.DirectionCount(Hex), "the hexagon does not turn");
            Assert.IsTrue(cat.TilesThePlane(Hex, 0), "and it is the tiling one");

            var snap = FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false, shapeCatalog: cat);

            // Checkerboard the two families over the grid, once per box orientation so no single
            // draw of the demo's RNG is what the test happens to cover.
            for (int orientation = 0; orientation < 16; orientation++)
            {
                var places = new System.Collections.Generic.List<FPBuildingPlacement>();
                int i = 0;
                for (int gx = -1; gx <= 1; gx++)
                    for (int gz = -1; gz <= 1; gz++, i++)
                        places.Add((i & 1) == 0
                            ? new FPBuildingPlacement(
                                Box, orientation, FP64.FromInt(gx * 4), FP64.FromInt(gz * 4), FP64.Zero)
                            : new FPBuildingPlacement(
                                Hex, FP64.FromInt(gx * 4), FP64.FromInt(gz * 4), FP64.Zero));

                FPNavMesh mesh = null;
                Assert.DoesNotThrow(
                    () => mesh = FPNavMeshRebaker.RebakePlacements(snap, places.ToArray(), null),
                    $"box orientation {orientation} must coexist with hexagons at 4 apart");
                foreach (var p in places)
                    Assert.IsFalse(Walkable(mesh, p.CentreX.ToDouble(), p.CentreZ.ToDouble()),
                        $"orientation {orientation}: every placed shape blocks its own centre");
            }
        }

        // ── determinism ──────────────────────────────────────────────────────

        [Test]
        public void TheTable_IsReproducible_AndHashesDistinctly()
        {
            // Built from FP64 trigonometry (a LUT plus fixed-point interpolation), never
            // System.Math — so the integers are the same on every build and every peer, and the
            // catalog hash that rides in the determinism envelope is stable.
            var a = Obb(2 * Unit, Unit, 16);
            var b = Obb(2 * Unit, Unit, 16);
            Assert.AreEqual(a.Hash, b.Hash, "same inputs, same table");

            Assert.AreNotEqual(a.Hash, Obb(2 * Unit, Unit, 32).Hash, "M is part of the geometry");
            Assert.AreNotEqual(a.Hash, Obb(2 * Unit, Unit + 1, 16).Hash, "one snap unit must show");
        }
    }
}
