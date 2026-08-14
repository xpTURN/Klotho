using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Hexagons, end to end.
    ///
    /// The requirement was "hexagon (= circular) buildings", and the completion criterion for it
    /// is NOT that a hexagon gets carved. It is that hexagons can be PACKED — that is what the
    /// shape is for. Carving is the easy half; packing needs the footprint to tile, the expansion
    /// to still tile after the miter, and the rebaker to accept the contact. This file runs all
    /// three through the real placement API.
    /// </summary>
    [TestFixture]
    public class FPNavMeshHexagonPlacementTests
    {
        private const int Unit = (int)FPGeoPredicates.SNAP_UNITS_PER_WORLD;
        private const double Radius = 0.5;

        /// <summary>
        /// THE SHIPPED HEXAGON, not a fixture copy of one. The completion criterion here is
        /// "hexagons can be built wall to wall", so these have to run on the shape the engine
        /// actually hands to a game, or they would be proving it about something else.
        ///
        /// a = 1 world unit; b comes out of the constructor as round(a*sqrt(3)) in exact integers
        /// (see FPBuildingShapeCatalog.HexagonOffsets for why that value and not a rounder one).
        /// </summary>
        private static FPBuildingShapeCatalog HexCatalog()
        {
            FPBuildingShapeCatalog.HexagonOffsets(2 * Unit, out long[] x, out long[] z);
            return new FPBuildingShapeCatalog(x, z, new[] { 0, 6 });
        }

        /// <summary>40x40 slab, vertices every 2 units, with a real agent radius.</summary>
        private static FPNavMesh BuildSlab()
        {
            var pts = new List<(int x, int z)>();
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: Radius);
        }

        private static FPNavMeshRebakeSnapshot Snapshot() =>
            FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false, shapeCatalog: HexCatalog());

        private static FPBuildingPlacement At(double x, double z) =>
            new FPBuildingPlacement(0, FP64.FromDouble(x), FP64.FromDouble(z), FP64.Zero);

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        private static (bool ok, string message) Try(
            FPNavMeshRebakeSnapshot snap, FPBuildingPlacement[] p, FPBuildingPlacementRules rules = default)
        {
            try { FPNavMeshRebaker.RebakePlacements(snap, p, null, rules); return (true, null); }
            catch (Exception e) { return (false, e.Message); }
        }

        // ── carving, the easy half ───────────────────────────────────────────

        [Test]
        public void SingleHexagon_IsCarved()
        {
            FPNavMeshRebakeSnapshot snap = Snapshot();
            FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(snap, new[] { At(0, 0) });

            Assert.IsFalse(Walkable(mesh, 0, 0), "the centre is blocked");
            Assert.IsFalse(Walkable(mesh, 2.4, 0), "and out to the expanded left/right vertices");
            Assert.IsFalse(Walkable(mesh, 0, 2.2), "and top/bottom");
            Assert.IsTrue(Walkable(mesh, 2.9, 2.4),
                "a corner of the bounding box, outside the hexagon — still walkable");
        }

        // ── packing, the half the requirement is actually about ──────────────

        [Test]
        public void TwoHexagons_PackFlush_AtTheTilingDelta()
        {
            // The completion criterion. The delta comes from the EXPANSION, not the footprint —
            // the game has to place neighbours where the carved shapes meet, not where the drawn
            // shapes meet, and those differ by 2r.
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);
            var expansion = snap.ShapeExpansion;

            Assert.IsTrue(expansion.TryTilingDelta(0, 0, 0, out long dx, out long dz));
            double wx = dx / (double)Unit, wz = dz / (double)Unit;

            var (ok, message) = Try(snap, new[] { At(0, 0), At(wx, wz) }, touching);
            Assert.IsTrue(ok, $"two hexagons at the tiling delta must be accepted: {message}");

            FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(snap, new[] { At(0, 0), At(wx, wz) }, null, touching);
            Assert.IsFalse(Walkable(mesh, 0, 0), "first hexagon blocked");
            Assert.IsFalse(Walkable(mesh, wx, wz), "second hexagon blocked");
            Assert.IsFalse(Walkable(mesh, wx / 2, wz / 2),
                "and the seam between them — if this were walkable an agent could thread the pack");
        }

        [Test]
        public void QuantisedPlacement_ReachesTheTilingLattice_WhichWholeUnitsCannot()
        {
            // The placement grid is 1/1024 of a world unit, and that resolution is not a nicety —
            // it is what makes a flush pack expressible at all. No tiling delta is a whole world
            // unit (the hexagon's carry a factor of sqrt(3)), so a placement path that rounded to
            // integers could never put two neighbours flush however permissive the rules were.
            //
            // This pins both halves: the deltas really are fractional, and the fractional centre
            // they imply really is accepted.
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var expansion = snap.ShapeExpansion;

            bool anyWhole = false;
            for (int edge = 0; edge < 3; edge++)
            {
                Assert.IsTrue(expansion.TryTilingDelta(0, 0, edge, out long dx, out long dz));
                if (dx % Unit == 0 && dz % Unit == 0)
                    anyWhole = true;
            }
            Assert.IsFalse(anyWhole,
                "no tiling delta is a whole world unit — rounding placements to integers would "
                + "make flush packing unreachable");

            // And the fractional centre is a legal placement: the catalog's offsets are integers
            // from the centre, so any centre on the 1/1024 grid keeps every vertex on it.
            Assert.IsTrue(expansion.TryTilingDelta(0, 0, 0, out long ax, out long az));
            var pair = new[]
            {
                At(0, 0),
                new FPBuildingPlacement(0, FPGeoPredicates.Unsnap(ax), FPGeoPredicates.Unsnap(az), FP64.Zero),
            };
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);
            var (ok, message) = Try(snap, pair, touching);
            Assert.IsTrue(ok, $"a fractional, on-grid centre must be accepted: {message}");
        }

        [Test]
        public void ThreeLatticeNeighbours_PackWithoutGaps()
        {
            // What the quantised placement path is for: walking the lattice builds a honeycomb.
            // Each step is centre + an integer-snap-unit delta, so the centres stay on the grid no
            // matter how many rings are added — the accuracy does not drift with distance.
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var expansion = snap.ShapeExpansion;
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);

            var places = new List<FPBuildingPlacement> { At(0, 0) };
            for (int edge = 0; edge < 3; edge++)
            {
                Assert.IsTrue(expansion.TryTilingDelta(0, 0, edge, out long dx, out long dz));
                places.Add(new FPBuildingPlacement(
                    0, FPGeoPredicates.Unsnap(dx), FPGeoPredicates.Unsnap(dz), FP64.Zero));
            }

            var (ok, message) = Try(snap, places.ToArray(), touching);
            Assert.IsTrue(ok, $"a lattice cluster must be accepted: {message}");

            FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(snap, places.ToArray(), null, touching);
            foreach (var p in places)
                Assert.IsFalse(Walkable(mesh, p.CentreX.ToDouble(), p.CentreZ.ToDouble()),
                    "every hexagon in the cluster is solid");
            for (int i = 1; i < places.Count; i++)
                Assert.IsFalse(
                    Walkable(mesh, places[i].CentreX.ToDouble() / 2, places[i].CentreZ.ToDouble() / 2),
                    $"and the seam to neighbour {i} — a walkable seam would mean they are not flush");
        }

        [Test]
        public void PackedHexagons_AreRejectedWithoutTheGameRule()
        {
            // Packing is contact, and contact is a game choice. The
            // default still refuses it — shipping hexagons does not quietly change that policy.
            FPNavMeshRebakeSnapshot snap = Snapshot();
            Assert.IsTrue(snap.ShapeExpansion.TryTilingDelta(0, 0, 0, out long dx, out long dz));

            Assert.IsFalse(Try(snap, new[] { At(0, 0), At(dx / (double)Unit, dz / (double)Unit) }).ok,
                "flush neighbours are contact, and contact is off by default");
        }

        [Test]
        public void HexagonRing_SurroundsTheCentre_AndTheCentreStaysBlocked()
        {
            // This test used to pin a defect: close a ring of six neighbours around a cell and the
            // cell in the middle turned WALKABLE. Measured then: five neighbours left it blocked
            // (2 obstacle rings), the sixth flipped it (3 rings). Rectangles did the same with
            // four neighbours around a fifth, so it was never about hexagons.
            //
            // Mechanism: even-odd erasure counted a
            // COINCIDENT edge once. A fully surrounded building shares every edge, so a ray
            // leaving its interior crossed the shared edge once instead of twice and the parity
            // came out odd = keep. It was fixed by splitting the
            // constrained flag into a wall bit (OR) and a crossing parity (XOR) — a shared edge
            // is still a wall, but crossing it no longer changes walkability.
            //
            // Kept as the regression: it needs AllowBuildingTouch (without contact no edges are
            // shared) and Brawler has that on, so this is the shipping configuration.
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);
            var expansion = snap.ShapeExpansion;

            var places = new List<FPBuildingPlacement> { At(0, 0) };
            for (int edge = 0; edge < 3; edge++)
            {
                Assert.IsTrue(expansion.TryTilingDelta(0, 0, edge, out long dx, out long dz));
                places.Add(At(dx / (double)Unit, dz / (double)Unit));
                places.Add(At(-dx / (double)Unit, -dz / (double)Unit));
            }

            var (ok, message) = Try(snap, places.ToArray(), touching);
            Assert.IsTrue(ok, $"the ring is accepted — nothing rejects it: {message}");

            FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(snap, places.ToArray(), null, touching);

            Assert.IsFalse(Walkable(mesh, 0, 0),
                "the fully surrounded hexagon is solid — this is the assertion the pinned-defect "
                + "test always wanted to make");

            for (int i = 1; i < places.Count; i++)
                Assert.IsFalse(Walkable(mesh, places[i].CentreX.ToDouble(), places[i].CentreZ.ToDouble()),
                    "and the ring members with it — the whole pack is one solid block");
        }

        [Test]
        public void FiveNeighbours_LeaveTheCentreBlocked()
        {
            // The other side of the measurement, and what makes the mechanism legible: one edge
            // left unshared is enough. The defect is not "packing is broken" — it is "closing the
            // ring is".
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);
            var expansion = snap.ShapeExpansion;

            var places = new List<FPBuildingPlacement> { At(0, 0) };
            for (int edge = 0; edge < 3 && places.Count < 6; edge++)
            {
                Assert.IsTrue(expansion.TryTilingDelta(0, 0, edge, out long dx, out long dz));
                places.Add(At(dx / (double)Unit, dz / (double)Unit));
                if (places.Count < 6) places.Add(At(-dx / (double)Unit, -dz / (double)Unit));
            }

            FPNavMesh mesh = FPNavMeshRebaker.RebakePlacements(snap, places.ToArray(), null, touching);
            Assert.IsFalse(Walkable(mesh, 0, 0), "five neighbours: still blocked");
        }

        // ── the guards ───────────────────────────────────────────────────────

        [Test]
        public void OffGridCentre_IsRefused()
        {
            // The anchor guard. Offsets are integers about the centre, so an off-grid centre puts
            // every vertex off-grid — and then the shared edges that make packing work stop
            // lining up. Same reason CreateSnapshot refuses an off-grid base mesh, and the same
            // kind of failure if it were let through: quiet drift nobody attributes correctly.
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var offGrid = new[]
            {
                new FPBuildingPlacement(0, FP64.FromRaw(FP64.FromDouble(1.0).RawValue + 1),
                                        FP64.Zero, FP64.Zero),
            };

            var ex = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.RebakePlacements(snap, offGrid));
            StringAssert.Contains("snap grid", ex.Message);
        }

        [Test]
        public void PlacingWithoutACatalog_IsRefused()
        {
            FPNavMeshRebakeSnapshot bare = FPNavMeshRebaker.CreateSnapshot(BuildSlab(), null, prewarm: false);
            var ex = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.RebakePlacements(bare, new[] { At(0, 0) }));
            StringAssert.Contains("without a shape catalog", ex.Message);
        }

        [Test]
        public void OverlappingHexagons_AreStillRejected()
        {
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);
            var (ok, message) = Try(snap, new[] { At(0, 0), At(1, 0) }, touching);

            Assert.IsFalse(ok, "one unit apart is deep overlap for a 4-unit-wide hexagon");
            StringAssert.Contains("overlap", message);
        }

        [Test]
        public void Placements_AreDeterministic()
        {
            FPNavMeshRebakeSnapshot snap = Snapshot();
            var p = new[] { At(0, 0), At(6, 0) };
            FPNavMesh a = FPNavMeshRebaker.RebakePlacements(snap, p);
            FPNavMesh b = FPNavMeshRebaker.RebakePlacements(snap, p);
            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(a), FPNavMeshRebaker.ComputeFingerprint(b));
        }

        // ── placementCount: a reused buffer instead of an exact-size copy ────

        [Test]
        public void PlacementCount_LetsAnOversizedBufferStandInForAnExactOne()
        {
            // The array's LENGTH used to be the count, which forced every caller holding a reused
            // buffer to trim to an exact-size copy first — one allocation per placement, directly
            // in front of a rebaker built to allocate nothing. placementCount removes the trim, so
            // the two forms have to produce the same mesh bit for bit.
            FPNavMeshRebakeSnapshot exactSnap = Snapshot();
            FPNavMeshRebakeSnapshot paddedSnap = Snapshot();

            // 8 units apart: the catalog hexagon is circumradius 2 (4 across the points) and the
            // bake radius adds 0.5 a side, so anything under ~5 is a touch rejection.
            var exact = new[] { At(-8, 0), At(0, 0) };

            // Same two placements at the front, then tail entries that must be ignored entirely.
            // They are deliberately placements that WOULD change the mesh if they were read.
            var padded = new FPBuildingPlacement[5];
            padded[0] = exact[0];
            padded[1] = exact[1];
            padded[2] = At(-8, 12);   // legal placements on their own, on the 40x40 slab — the point
            padded[3] = At(0, 12);    // is that they are never read, not that they would be
            padded[4] = At(8, 12);    // rejected.

            FPNavMesh fromExact = FPNavMeshRebaker.RebakePlacements(exactSnap, exact, null);
            FPNavMesh fromPadded = FPNavMeshRebaker.RebakePlacements(paddedSnap, padded, null, default, 2);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(fromExact),
                FPNavMeshRebaker.ComputeFingerprint(fromPadded),
                "the tail past placementCount must not reach the rebake at all");

            // Control: without the count the same buffer is a different input, so a green above
            // cannot be an accident of the tail happening not to matter.
            FPNavMesh fromWholeBuffer = FPNavMeshRebaker.RebakePlacements(Snapshot(), padded, null);
            Assert.AreNotEqual(
                FPNavMeshRebaker.ComputeFingerprint(fromExact),
                FPNavMeshRebaker.ComputeFingerprint(fromWholeBuffer),
                "the padding was chosen to change the mesh — if it does not, this test proves nothing");
        }

        [Test]
        public void PlacementCount_PastTheArray_IsRefused()
        {
            // The failure this prevents is silent: reading past the live prefix would feed the
            // rebake whatever the buffer's tail holds, which for a pooled buffer is the previous
            // placement's data. Refusing names the caller's error instead.
            var two = new[] { At(-8, 0), At(0, 0) };
            var ex = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.RebakePlacements(Snapshot(), two, null, default, 3));
            StringAssert.Contains("placementCount", ex.Message);
        }

        [Test]
        public void PlacementCount_NegativeOtherThanTheSentinel_IsRefused()
        {
            // Only -1 means "the whole array". Any other negative used to fold into that sentinel
            // silently, so a caller whose count arithmetic went wrong got the buffer's stale tail
            // rebaked — and a reusable buffer's tail is the previous rebake's placements, already
            // known to be mutually legal, so no validation would object.
            var two = new[] { At(-8, 0), At(0, 0) };

            Assert.DoesNotThrow(() => FPNavMeshRebaker.RebakePlacements(Snapshot(), two, null, default, -1));

            var ex = Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.RebakePlacements(Snapshot(), two, null, default, -2));
            StringAssert.Contains("placementCount", ex.Message);
        }
    }
}
