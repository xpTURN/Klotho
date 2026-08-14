using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The shape catalog and its expansion.
    ///
    /// The completion criterion for the hexagon requirement is NOT "a hexagon gets carved". It is
    /// "hexagons can be built wall to wall", because that is what a hexagon is for. That needs
    /// three things to hold at once and this file pins all three:
    ///
    ///   1. the footprint tiles      — it is centrally symmetric, which for a hexagon means it
    ///                                 must be defined as integers rather than converted into
    ///                                 them (the engine's own Snap is floor, and floor breaks
    ///                                 the cancellation — measured below);
    ///   2. the EXPANSION tiles too  — opposite edges rounded together, or the shared edge ends
    ///                                 up an odd number of units apart and no integer anchor
    ///                                 delta puts the two neighbours flush;
    ///   3. the rebaker accepts it   — with the game's touch rule on.
    ///
    /// Miss any one and hexagons are carved perfectly and can never be packed.
    /// </summary>
    [TestFixture]
    public class FPBuildingShapeCatalogTests
    {
        private const int Unit = (int)FPGeoPredicates.SNAP_UNITS_PER_WORLD;

        /// <summary>
        /// Centrally symmetric integer hexagon: (+-2a, 0), (+-a, +-b), CCW. With b/a near sqrt(3)
        /// it is visually a regular hexagon; unlike one, it exists on the grid.
        /// </summary>
        private static (long[] x, long[] z) Hexagon(long a, long b) =>
            (new[] { 2 * a, a, -a, -2 * a, -a, a },
             new[] { 0L, b, b, 0L, -b, -b });

        private static (long[] x, long[] z) Diamond(long d) =>
            (new[] { d, 0L, -d, 0L }, new[] { 0L, d, 0L, -d });

        private static FPBuildingShapeCatalog Catalog(params (long[] x, long[] z)[] entries)
        {
            var xs = new List<long>();
            var zs = new List<long>();
            var start = new int[entries.Length + 1];
            for (int e = 0; e < entries.Length; e++)
            {
                start[e] = xs.Count;
                xs.AddRange(entries[e].x);
                zs.AddRange(entries[e].z);
            }
            start[entries.Length] = xs.Count;
            return new FPBuildingShapeCatalog(xs.ToArray(), zs.ToArray(), start);
        }

        // ── the table refuses what it cannot use ─────────────────────────────

        [Test]
        public void MalformedEntries_AreRefusedAtBuildTime()
        {
            // A broken footprint must not survive to become a placement failure later — that
            // would send whoever debugs it looking at positions instead of at the shape table.
            Assert.Throws<ArgumentException>(
                () => Catalog((new[] { 0L, 1L }, new[] { 0L, 0L })), "two vertices");
            Assert.Throws<ArgumentException>(
                () => Catalog((new[] { 0L, 1L, 2L }, new[] { 0L, 0L, 0L })), "collinear");
            Assert.Throws<ArgumentException>(
                () => Catalog((new[] { 0L, 0L, 1L }, new[] { 0L, 0L, 1L })), "duplicate vertex");

            // Clockwise: the whole pipeline assumes CCW (outward normals, the swallow test,
            // the CDT's ring orientation), so accepting it would be an inversion, not a variant.
            Assert.Throws<ArgumentException>(
                () => Catalog((new[] { 0L, 0L, Unit }, new[] { 0L, Unit, 0L })), "clockwise");
        }

        [Test]
        public void Hash_TracksTheGeometry_AndNothingElse()
        {
            var a = Catalog(Hexagon(Unit, 7 * Unit / 4));
            var b = Catalog(Hexagon(Unit, 7 * Unit / 4));
            var c = Catalog(Hexagon(Unit, 7 * Unit / 4 + 1));

            Assert.AreEqual(a.Hash, b.Hash, "same geometry, same hash");
            Assert.AreNotEqual(a.Hash, c.Hash, "one snap unit of difference must show");

            // Appending an entry changes the hash — the recurring cost the class note calls out.
            var appended = Catalog(Hexagon(Unit, 7 * Unit / 4), Diamond(Unit));
            Assert.AreNotEqual(a.Hash, appended.Hash,
                "adding a shape moves the hash, which is why replays made before it are invalid");
        }

        // ── the hexagon decision ─────────────────────────────────────────────

        [Test]
        public void IntegerHexagon_IsCentrallySymmetric_AndSurvivesExpansion()
        {
            // Part 1 and 2 of the requirement. Symmetry is what makes it tile, and it has to
            // still be there AFTER the miter — the outward vertex snap is per-axis, so opposite
            // edges rounded independently would drift apart.
            var catalog = Catalog(Hexagon(Unit, 7 * Unit / 4));
            Assert.IsTrue(catalog.IsCentrallySymmetric(0, 0), "the footprint is symmetric by construction");

            var expanded = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            Assert.IsTrue(expanded.TryTilingDelta(0, 0, 0, out long dx, out long dz),
                "a symmetric entry must offer a tiling delta");
            Assert.AreNotEqual(0, dx | dz, "and it must be a real translation");
        }

        [Test]
        public void ExpandedHexagons_TileExactly_AtTheReportedDelta()
        {
            // Part 2 proved end to end, in integers: place a second copy at the tiling delta and
            // the two expanded hexagons must share the edge exactly — every vertex of the shared
            // edge coinciding, not merely touching somewhere.
            var catalog = Catalog(Hexagon(Unit, 7 * Unit / 4));
            var expanded = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            long[] ex = expanded.ExpandedX, ez = expanded.ExpandedZ;
            int n = catalog.VertexCount(0);

            for (int edge = 0; edge < n / 2; edge++)
            {
                Assert.IsTrue(expanded.TryTilingDelta(0, 0, edge, out long dx, out long dz));

                // Our edge (edge, edge+1) must coincide with the neighbour's opposite edge,
                // which is its (edge + n/2, edge + n/2 + 1) shifted by the delta.
                int a0 = edge, a1 = (edge + 1) % n;
                int b0 = (edge + n / 2) % n, b1 = (edge + n / 2 + 1) % n;

                Assert.AreEqual(ex[a1], ex[b0] + dx, $"edge {edge}: x of first shared vertex");
                Assert.AreEqual(ez[a1], ez[b0] + dz, $"edge {edge}: z of first shared vertex");
                Assert.AreEqual(ex[a0], ex[b1] + dx, $"edge {edge}: x of second shared vertex");
                Assert.AreEqual(ez[a0], ez[b1] + dz, $"edge {edge}: z of second shared vertex");
            }
        }

        [Test]
        public void RegularHexagon_TilesOrNot_DependingOnHowItReachesIntegers()
        {
            // The reason the catalog stores integers instead of trusting a conversion. A regular
            // hexagon has no integer form (sqrt(3)/2 is irrational), so it always arrives here
            // through some rounding — and WHICH rounding decides whether it can be packed.
            //
            // Symmetric rounding keeps opposite vertices cancelling, so the shape stays centrally
            // symmetric and still tiles (with slightly unequal edges — that part is unavoidable).
            // The engine's own FPGeoPredicates.Snap is an arithmetic shift, i.e. FLOOR, which is
            // not symmetric: it pushes every vertex the same way in space rather than the same
            // way relative to the centre, and the cancellation is gone.
            //
            // So "define the shape in FP64 and let Snap convert it" — the obvious path — is
            // exactly the one that loses tiling. Hence: integers in the table, and a check.
            const double R = 4.0;

            var rounded = (x: new long[6], z: new long[6]);
            var floored = (x: new long[6], z: new long[6]);
            for (int i = 0; i < 6; i++)
            {
                double ang = System.Math.PI / 3.0 * i;
                double vx = R * Unit * System.Math.Cos(ang), vz = R * Unit * System.Math.Sin(ang);
                rounded.x[i] = (long)System.Math.Round(vx);
                rounded.z[i] = (long)System.Math.Round(vz);
                floored.x[i] = FPGeoPredicates.Snap(FP64.FromDouble(vx / Unit));
                floored.z[i] = FPGeoPredicates.Snap(FP64.FromDouble(vz / Unit));
            }

            Assert.IsTrue(Catalog(rounded).IsCentrallySymmetric(0, 0),
                "symmetric rounding keeps the cancellation — this one still tiles");
            Assert.IsFalse(Catalog(floored).IsCentrallySymmetric(0, 0),
                "the engine's Snap is floor, and floor is not symmetric — this one cannot be "
                + "packed, and it is what you get by computing vertices in FP64 and snapping");
        }

        // ── P3: the shipped hexagon constructor ──────────────────────────────

        [Test]
        public void HexagonOffsets_BuildsATilingHexagon_WithoutFloatingPoint()
        {
            // P3's deliverable. The catalog is game-supplied, so what the engine owes is the
            // CONSTRUCTION — the rule that "compute the vertices and Snap them" loses tiling
            // (RegularHexagon_TilesOrNot_DependingOnHowItReachesIntegers below) is engine
            // knowledge, and a game that re-derives it will mostly get it wrong and not find out
            // until it tries to pack.
            FPBuildingShapeCatalog.HexagonOffsets(2 * Unit, out long[] x, out long[] z);
            var catalog = new FPBuildingShapeCatalog(x, z, new[] { 0, 6 });

            Assert.IsTrue(catalog.IsCentrallySymmetric(0, 0));
            Assert.IsTrue(catalog.TilesThePlane(0, 0));

            // b = round(a*sqrt(3)) derived in exact integers: 1024*1.7320508 = 1773.62 -> 1774.
            Assert.AreEqual(1774, z[1], "b must be the rounded integer root, not a floor or a ceil");
            CollectionAssert.AreEqual(new[] { 2 * Unit, Unit, -Unit, -2 * Unit, -Unit, Unit }, x);

            // And it survives the miter with its symmetry — the constraint that actually limits
            // the choice of b.
            var expanded = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            for (int edge = 0; edge < 3; edge++)
                Assert.IsTrue(expanded.TryTilingDelta(0, 0, edge, out _, out _), $"edge {edge}");
        }

        [Test]
        public void HexagonOffsets_IsMoreRegularThanTheHandWrittenRatio()
        {
            // The b/a decision, on the record. Equal edges need exactly b = a*sqrt(3), so the
            // rounding IS the whole error. Measured at a = 1 world unit: round(a*sqrt(3)) leaves
            // the two edge lengths 0.016% apart; b/a = 7/4 — the value that reads nicely as a
            // decimal, and what the first hexagon fixture used — leaves them 0.775% apart.
            //
            // 48x the error to buy legibility in a table that is code, not something a designer
            // types. Rejected on those grounds. (Coarser candidates are worse still: b/a = 2 is
            // 11.1%. All of them expand and tile — this is purely about how regular it looks.)
            FPBuildingShapeCatalog.HexagonOffsets(2 * Unit, out long[] x, out long[] z);

            double Unequal(long a, long b)
            {
                double longEdge = System.Math.Sqrt((double)a * a + (double)b * b) / Unit;
                double flatEdge = 2.0 * a / Unit;
                return System.Math.Abs(longEdge - flatEdge) / ((longEdge + flatEdge) / 2) * 100;
            }

            double chosen = Unequal(Unit, z[1]);
            double sevenQuarters = Unequal(Unit, 7 * Unit / 4);

            Assert.Less(chosen, 0.02, "the shipped hexagon is within 0.02% of regular");
            Assert.Greater(sevenQuarters, 0.7, "b/a = 7/4 is not");
            Assert.Less(chosen * 40, sevenQuarters, "and the difference is more than an order of magnitude");
        }

        [Test]
        public void CentralSymmetry_DoesNotMeanTheShapeCanFillThePlane()
        {
            // The trap behind there being no separate "circle" catalog entry.
            //
            // A convex polygon tiles by translation only if it is a parallelogram or a centrally
            // symmetric HEXAGON. An octagon or 16-gon passes IsCentrallySymmetric and even offers
            // per-edge tiling deltas — ONE flush neighbour across ONE edge is genuine — but the
            // lattice those deltas span is over-dense, so building outward in two directions
            // overlaps instead of packing. Measured footprint/cell ratios: 1.00 at N = 4 and 6;
            // 1.17, 1.61, 2.08 at N = 8, 12, 16.
            //
            // So "hexagon (= circular)" is answered by the hexagon alone: a rounder building would
            // be a DIFFERENT feature (placeable and carvable, but not packable), and packing was
            // the entire reason the shape was asked for.
            for (int n = 4; n <= 16; n += 2)
            {
                var x = new long[n];
                var z = new long[n];
                for (int i = 0; i < n; i++)
                {
                    double ang = 2 * System.Math.PI * i / n;
                    x[i] = (long)System.Math.Round(4.0 * Unit * System.Math.Cos(ang));
                    z[i] = (long)System.Math.Round(4.0 * Unit * System.Math.Sin(ang));
                }
                var cat = new FPBuildingShapeCatalog(x, z, new[] { 0, n });

                Assert.IsTrue(cat.IsCentrallySymmetric(0, 0), $"N={n} is centrally symmetric");
                Assert.AreEqual(n == 4 || n == 6, cat.TilesThePlane(0, 0),
                    $"N={n}: only 4 and 6 can fill the plane, whatever the symmetry says");
            }
        }

        // ── assembling a table of several sizes and types ────────────────────

        [Test]
        public void Builder_ConcatenatesSizesAndTypes_AndReportsUsableIndices()
        {
            // A real game wants several sizes and both types in ONE table, because there is only
            // ever one table per stage. Done by hand that is array concatenation plus CSR offset
            // arithmetic; the point of the builder is that the bookkeeping comes back as a return
            // value instead of being recomputed at every call site.
            var b = new FPBuildingShapeCatalogBuilder();
            int smallBox = b.AddObb(Unit / 2, Unit / 2, directions: 8);
            int bigBox = b.AddObb(2 * Unit, Unit, directions: 16);
            int smallDisc = b.AddHexagon(Unit);
            int bigDisc = b.AddHexagon(4 * Unit);

            // Shape ids count SHAPES, not entries — a shape that turns 16 ways still advances the
            // id by one. That is the whole point: the caller never sees the block layout.
            Assert.AreEqual(0, smallBox);
            Assert.AreEqual(1, bigBox);
            Assert.AreEqual(2, smallDisc);
            Assert.AreEqual(3, bigDisc);
            Assert.AreEqual(4, b.ShapeCount);
            Assert.AreEqual(26, b.EntryCount, "8 + 16 + 1 + 1 orientations underneath");

            var cat = b.Build();
            Assert.AreEqual(4, cat.ShapeCount);
            Assert.AreEqual(26, cat.EntryCount);

            Assert.AreEqual(8, cat.DirectionCount(smallBox));
            Assert.AreEqual(16, cat.DirectionCount(bigBox));
            Assert.AreEqual(1, cat.DirectionCount(smallDisc), "a hexagon does not turn");
            Assert.AreEqual(1, cat.DirectionCount(bigDisc));

            // Every entry is a real shape, and the two families kept their vertex counts.
            for (int o = 0; o < 8; o++)
                Assert.AreEqual(4, cat.VertexCount(cat.TryResolveEntry(smallBox, o)));
            for (int o = 0; o < 16; o++)
                Assert.AreEqual(4, cat.VertexCount(cat.TryResolveEntry(bigBox, o)));
            Assert.AreEqual(6, cat.VertexCount(cat.TryResolveEntry(smallDisc, 0)));
            Assert.AreEqual(6, cat.VertexCount(cat.TryResolveEntry(bigDisc, 0)));

            // And the shape + orientation addressing works end to end.
            var exp = new FPBuildingShapeExpansion(cat, FP64.FromDouble(0.5));
            Assert.IsTrue(exp.TryTilingDelta(bigBox, 3, 0, out _, out _),
                "orientation 3 of the large box is addressable without index arithmetic");
        }

        [Test]
        public void OrientationPastTheEnd_IsRefused_NotSilentlyTheNextShape()
        {
            // The reason shape and orientation are separate parameters. With one flat index a game
            // writes smallBox + orientation, and an orientation one past the end is STILL a valid
            // index — it addresses the next shape in the table. Nothing throws, nothing logs, and a
            // different building appears; the two peers agree, so no desync check fires either.
            var b = new FPBuildingShapeCatalogBuilder();
            int smallBox = b.AddObb(Unit / 2, Unit / 2, directions: 8);
            int bigBox = b.AddObb(2 * Unit, Unit, directions: 16);
            var cat = b.Build();

            // The overflow that used to be silent: entry 8 exists, and it is the OTHER box.
            Assert.AreEqual(8, cat.TryResolveEntry(bigBox, 0), "entry 8 is a real entry...");
            Assert.AreEqual(-1, cat.TryResolveEntry(smallBox, 8), "...but not orientation 8 of the small box");

            Assert.AreEqual(-1, cat.TryResolveEntry(smallBox, -1));
            Assert.AreEqual(-1, cat.TryResolveEntry(bigBox, 16));
            Assert.AreEqual(-1, cat.TryResolveEntry(2, 0), "no third shape");
            Assert.AreEqual(-1, cat.TryResolveEntry(-1, 0));

            // The rebaker refuses the same pair by name — see
            // FPBuildingObbTests.PlacementWithABadShapeOrOrientation_IsRefusedByName.
        }

        [Test]
        public void Builder_ProducesTheSameTableAsHandConcatenation()
        {
            // The builder must be a pure convenience — same bytes, therefore the same Hash, which
            // is what rides in the determinism envelope. If it differed, adopting it would silently
            // invalidate every recording made before.
            FPBuildingShapeCatalog.ObbOffsets(Unit, Unit / 2, 16, out long[] bx, out long[] bz, out int[] bs);
            FPBuildingShapeCatalog.HexagonOffsets(Unit, out long[] hx, out long[] hz);

            var x = new List<long>(bx); x.AddRange(hx);
            var z = new List<long>(bz); z.AddRange(hz);
            var start = new List<int>(bs) { x.Count };
            var byHand = new FPBuildingShapeCatalog(
                x.ToArray(), z.ToArray(), start.ToArray(), new[] { 0, 16, 17 });

            var built = new FPBuildingShapeCatalogBuilder();
            built.AddObb(Unit, Unit / 2, 16);
            built.AddHexagon(Unit);

            Assert.AreEqual(byHand.Hash, built.Build().Hash,
                "the builder must produce a byte-identical table");

            // The grouping is part of that identity, not decoration on top of it. Same offsets,
            // same CSR, every entry its own shape — a DIFFERENT table, because a placement that
            // names shape 3 means something else in each.
            var ungrouped = new FPBuildingShapeCatalog(x.ToArray(), z.ToArray(), start.ToArray());
            Assert.AreNotEqual(byHand.Hash, ungrouped.Hash,
                "how the entries are grouped into shapes must show in the hash");
        }

        [Test]
        public void Builder_OrderDefinesIndices_AndTheHashCatchesAReorder()
        {
            // Entry indices are inside the determinism envelope: a placement names one by number,
            // so reordering the Add calls silently repoints every stored placement. The hash is
            // what turns that into a load-time failure instead of a quietly different navmesh.
            var a = new FPBuildingShapeCatalogBuilder();
            a.AddObb(Unit, Unit / 2, 4);
            a.AddHexagon(2 * Unit);

            var bb = new FPBuildingShapeCatalogBuilder();
            bb.AddHexagon(2 * Unit);
            bb.AddObb(Unit, Unit / 2, 4);

            Assert.AreNotEqual(a.Build().Hash, bb.Build().Hash,
                "the same shapes in a different order are a different table");
        }

        [Test]
        public void Builder_RefusesAnEmptyTableAndMalformedOffsets()
        {
            Assert.Throws<ArgumentException>(() => new FPBuildingShapeCatalogBuilder().Build(),
                "a table with no entries is not usable");
            Assert.Throws<ArgumentException>(
                () => new FPBuildingShapeCatalogBuilder().Add(new[] { 0L, 1L }, new[] { 0L }),
                "mismatched offset arrays");

            // A malformed shape is still caught, just at Build() — the validation lives in the
            // catalog and the builder does not get to skip it.
            var b = new FPBuildingShapeCatalogBuilder();
            b.Add(new[] { 0L, 1L, 2L }, new[] { 0L, 0L, 0L });   // collinear
            Assert.Throws<ArgumentException>(() => b.Build());
        }

        // ── lattice snapping, what a placement UI needs ──────────────────────

        [Test]
        public void SnapToLattice_LandsOnTheLattice_AndOnThePlacementGrid()
        {
            // A UI hands over wherever the cursor was; this is what turns that into a position two
            // hexagons can actually meet at. Every result must be an integer combination of the
            // two basis deltas — otherwise the neighbours are near each other rather than flush —
            // and must sit on the 1/1024 placement grid, or the rebaker refuses it outright.
            var catalog = Catalog(Hexagon(Unit, 7 * Unit / 4));
            var exp = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            Assert.IsTrue(exp.TryTilingDelta(0, 0, 0, out long ax, out long az));
            Assert.IsTrue(exp.TryTilingDelta(0, 0, 1, out long bx, out long bz));
            long det = ax * bz - az * bx;

            foreach (var (wx, wz) in new[]
            {
                (0.0, 0.0), (0.3, 0.1), (-7.7, 2.2), (13.0, -5.0), (0.001, -0.001), (100.5, -60.25),
            })
            {
                Assert.IsTrue(exp.TrySnapToLattice(
                    0, 0, FP64.FromDouble(wx), FP64.FromDouble(wz), out FP64 sx, out FP64 sz),
                    $"({wx},{wz}) must snap");

                long px = FPGeoPredicates.Snap(sx), pz = FPGeoPredicates.Snap(sz);
                Assert.IsTrue(FPGeoPredicates.IsOnGrid(sx), $"({wx},{wz}) must land on the placement grid");
                Assert.IsTrue(FPGeoPredicates.IsOnGrid(sz));

                // Integer combination check: solving the basis must come out whole.
                Assert.AreEqual(0, (px * bz - pz * bx) % det, $"({wx},{wz}) is not a lattice point");
                Assert.AreEqual(0, (ax * pz - az * px) % det, $"({wx},{wz}) is not a lattice point");
            }
        }

        [Test]
        public void SnapToLattice_PicksTheNearestPoint_NotMerelyANearOne()
        {
            // Rounding the inverted basis is the obvious implementation and it is not enough: on a
            // basis this far from orthogonal the rounded pair can be a neighbour of the true
            // nearest, which shows up as a placement one cell away from where the player aimed.
            // Brute-force the neighbourhood and require an exact match.
            var catalog = Catalog(Hexagon(Unit, 7 * Unit / 4));
            var exp = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            exp.TryTilingDelta(0, 0, 0, out long ax, out long az);
            exp.TryTilingDelta(0, 0, 1, out long bx, out long bz);

            for (int i = 0; i < 40; i++)
            {
                // Deterministic spread of targets, deliberately off-lattice.
                long tx = (i * 7919) % 20011 - 10000;
                long tz = (i * 6271) % 17021 - 8500;
                Assert.IsTrue(exp.TrySnapToLattice(
                    0, 0, FPGeoPredicates.Unsnap(tx), FPGeoPredicates.Unsnap(tz),
                    out FP64 sx, out FP64 sz));

                long gotX = FPGeoPredicates.Snap(sx), gotZ = FPGeoPredicates.Snap(sz);
                long gotDist = (gotX - tx) * (gotX - tx) + (gotZ - tz) * (gotZ - tz);

                long bestDist = long.MaxValue;
                for (long m = -12; m <= 12; m++)
                {
                    for (long n = -12; n <= 12; n++)
                    {
                        long px = m * ax + n * bx, pz = m * az + n * bz;
                        long d = (px - tx) * (px - tx) + (pz - tz) * (pz - tz);
                        if (d < bestDist) bestDist = d;
                    }
                }
                Assert.AreEqual(bestDist, gotDist,
                    $"target ({tx},{tz}): snapped to a lattice point that is not the nearest");
            }
        }

        [Test]
        public void SnapToLattice_RefusesAnEntryThatCannotTile()
        {
            // A triangle has no tiling delta, so there is no lattice to snap to. The contract is
            // that the out parameters still hold the quantised target, so a caller can place there
            // unchanged instead of having to special-case the failure.
            var tri = Catalog((new[] { 0L, 4L * Unit, 2L * Unit }, new[] { 0L, 0L, 3L * Unit }));
            var exp = new FPBuildingShapeExpansion(tri, FP64.FromDouble(0.5));

            FP64 x = FP64.FromDouble(3.7), z = FP64.FromDouble(-1.2);
            Assert.IsFalse(exp.TrySnapToLattice(0, 0, x, z, out FP64 sx, out FP64 sz));
            Assert.AreEqual(FPGeoPredicates.Quantize(x).RawValue, sx.RawValue,
                "the fallback is the quantised target");
            Assert.AreEqual(FPGeoPredicates.Quantize(z).RawValue, sz.RawValue);
        }

        // ── expansion refuses what it cannot prove ───────────────────────────

        [Test]
        public void Expansion_IsDeterministic_AndDependsOnTheRadius()
        {
            var catalog = Catalog(Hexagon(Unit, 7 * Unit / 4));
            var a = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            var b = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.5));
            var c = new FPBuildingShapeExpansion(catalog, FP64.FromDouble(0.75));

            CollectionAssert.AreEqual(a.ExpandedX, b.ExpandedX);
            CollectionAssert.AreEqual(a.ExpandedZ, b.ExpandedZ);
            CollectionAssert.AreNotEqual(a.ExpandedX, c.ExpandedX,
                "a different stage radius is a different table — that is why it is derived per "
                + "snapshot and not baked into the catalog");
        }

        [Test]
        public void TilingDelta_IsOnlyOfferedForSymmetricEntries()
        {
            // A diamond is centrally symmetric (4 vertices) and does tile; a triangle is not and
            // must not pretend to.
            var tri = Catalog((new[] { 0L, 4L * Unit, 2L * Unit }, new[] { 0L, 0L, 3L * Unit }));
            var expanded = new FPBuildingShapeExpansion(tri, FP64.FromDouble(0.5));
            Assert.IsFalse(expanded.TryTilingDelta(0, 0, 0, out _, out _));

            var dia = Catalog(Diamond(3 * Unit));
            Assert.IsTrue(new FPBuildingShapeExpansion(dia, FP64.FromDouble(0.5))
                .TryTilingDelta(0, 0, 0, out _, out _));
        }

        // ── locally convex is not convex ─────────────────────────────────────

        /// <summary>{5/2} star, on the grid: the five points of a regular pentagon visited two at
        /// a time. Every vertex turns left; the edges cross each other.</summary>
        private static (long[] x, long[] z) Pentagram(long r)
        {
            var x = new long[5];
            var z = new long[5];
            for (int k = 0; k < 5; k++)
            {
                double ang = 2.0 * System.Math.PI * ((2 * k) % 5) / 5.0 + System.Math.PI / 2;
                x[k] = (long)System.Math.Round(r * System.Math.Cos(ang));
                z[k] = (long)System.Math.Round(r * System.Math.Sin(ang));
            }
            return (x, z);
        }

        [Test]
        public void SelfIntersectingStar_IsRefusedAtLoad()
        {
            // The per-vertex orientation test cannot see this shape. Turning left at every vertex
            // only forces the turning number to be a POSITIVE INTEGER; convex is exactly 1 and a
            // pentagram is 2. It used to be accepted here, and then accepted again by
            // FPConvexOffset — Expand returned true, Validate gave no reason — so the first
            // complaint arrived from the CDT during a rebake, phrased as "constraint crosses an
            // existing constraint": a command-tick throw that names neither the catalog nor the
            // shape. Refusing at load is the whole point of the catalog validating at all.
            var star = Pentagram(4 * Unit);

            // Premise: the shape really does pass the per-vertex check, otherwise this test would
            // be pinning the wrong mechanism.
            for (int i = 0; i < 5; i++)
            {
                int a = i, b = (i + 1) % 5, c = (i + 2) % 5;
                Assert.Greater(
                    FPGeoPredicates.Orient2D(star.x[a], star.z[a], star.x[b], star.z[b], star.x[c], star.z[c]),
                    0, $"triple ({a},{b},{c}) must turn left — otherwise the local check alone would catch it");
            }

            var ex = Assert.Throws<ArgumentException>(() => Catalog(star));
            StringAssert.Contains("self-intersecting", ex.Message);
            StringAssert.Contains("2 times", ex.Message);
        }

        [Test]
        public void ConvexShapes_AreNotRefusedByTheWindingCheck()
        {
            // Control for the refusal above: it must reject star polygons, not polygons. Covers a
            // quad, a hexagon and the catalog's own generated hexagon, whose first edge starts on
            // the +x axis — the tie-break case for the half-plane split.
            Assert.DoesNotThrow(() => Catalog(Diamond(3 * Unit)));
            Assert.DoesNotThrow(() => Catalog(Hexagon(2 * Unit, 3 * Unit)));

            FPBuildingShapeCatalog.HexagonOffsets(4 * Unit, out long[] hx, out long[] hz);
            Assert.DoesNotThrow(() => Catalog((hx, hz)));
        }

        // ── the lattice snap really is the nearest point ─────────────────────

        /// <summary>
        /// Sweeps targets and compares <c>TrySnapToLattice</c> against a brute-force search of the
        /// lattice, returning how many times it missed the true nearest point and by how much.
        /// </summary>
        private static (int total, int miss, long worstGapSq) SweepSnapAccuracy(
            (long[] x, long[] z) shape)
        {
            var exp = new FPBuildingShapeExpansion(Catalog(shape), FP64.FromDouble(0.5));
            bool hasBasis = exp.TryTilingDelta(0, 0, 0, out long ax, out long az);
            hasBasis &= exp.TryTilingDelta(0, 0, 1, out long bx, out long bz);
            Assert.IsTrue(hasBasis, "the shape must tile, otherwise this measures nothing");

            int total = 0, miss = 0;
            long worst = 0, seed = 12345;
            for (int i = 0; i < 600; i++)
            {
                seed = seed * 6364136223846793005L + 1442695040888963407L;
                long tx = (seed >> 20) % (40 * Unit);
                seed = seed * 6364136223846793005L + 1442695040888963407L;
                long tz = (seed >> 20) % (40 * Unit);

                if (!exp.TrySnapToLattice(0, 0, FPGeoPredicates.Unsnap(tx), FPGeoPredicates.Unsnap(tz),
                        out FP64 sx, out FP64 sz))
                    continue;
                total++;
                long gx = FPGeoPredicates.Snap(sx), gz = FPGeoPredicates.Snap(sz);
                long got = (gx - tx) * (gx - tx) + (gz - tz) * (gz - tz);

                long best = long.MaxValue;
                for (long m = -80; m <= 80; m++)
                    for (long n = -80; n <= 80; n++)
                    {
                        long px = m * ax + n * bx, pz = m * az + n * bz;
                        long d = (px - tx) * (px - tx) + (pz - tz) * (pz - tz);
                        if (d < best) best = d;
                    }
                if (got > best)
                {
                    miss++;
                    if (got - best > worst) worst = got - best;
                }
            }
            return (total, miss, worst);
        }

        [Test]
        public void SnapToLattice_IsTheNearestPoint_EvenForASkewedBasis()
        {
            // "Round the inverse, then check the 3x3 around it" is only enough when the basis is
            // near-orthogonal, and the docstring promised the nearest point unconditionally. Every
            // footprint the catalog realistically holds does have a near-orthogonal basis — its
            // tiling deltas come out 60-90 degrees apart — so the gap was invisible until a shape
            // whose deltas are nearly parallel was tried: this one missed the true nearest on 81%
            // of targets, by up to 31 world units. Lattice reduction removes the precondition.
            //
            // 200 x 2 world units is not a plausible building. It is here because it is the cheapest
            // shape that violates the precondition while still being a legal catalog entry (convex,
            // centrally symmetric, on the grid) — the test has to fail without the fix.
            var skewed = (new[] { 100L * Unit, 99L * Unit, -1L * Unit, -100L * Unit, -99L * Unit, 1L * Unit },
                          new[] { 0L, 1L * Unit, 1L * Unit, 0L, -1L * Unit, -1L * Unit });

            var (total, miss, worstGapSq) = SweepSnapAccuracy(skewed);
            Assert.Greater(total, 500, "the sweep must actually be snapping");
            Assert.AreEqual(0, miss,
                $"{miss}/{total} targets did not get the nearest lattice point "
                + $"(worst gap {System.Math.Sqrt(worstGapSq) / Unit:F2} world units)");
        }

        [Test]
        public void SnapToLattice_IsTheNearestPoint_ForShippableShapes()
        {
            // Control. These already measured 0 before reduction, which is the point: the fix must
            // not change what a realistic footprint snaps to, only what a skewed one does.
            foreach (var (name, shape) in new[]
            {
                ("hexagon", Hexagon(2 * Unit, 3 * Unit)),
                ("diamond", Diamond(3 * Unit)),
            })
            {
                var (total, miss, _) = SweepSnapAccuracy(shape);
                Assert.Greater(total, 500, $"{name}: the sweep must actually be snapping");
                Assert.AreEqual(0, miss, $"{name}: {miss}/{total} targets missed the nearest point");
            }
        }

        // ── a broken index is reported as a broken index ─────────────────────

        [Test]
        public void MalformedEntryStart_IsNamedAsAnIndexProblem()
        {
            // Two valid squares laid end to end: eight offsets, so [0,4,8] is the correct index.
            long[] x = { -Unit, Unit, Unit, -Unit, -Unit, Unit, Unit, -Unit };
            long[] z = { -Unit, -Unit, Unit, Unit, -Unit, -Unit, Unit, Unit };

            Assert.DoesNotThrow(() => new FPBuildingShapeCatalog(x, z, new[] { 0, 4, 8 }),
                "control: the well-formed index must still be accepted");

            // The three endpoint conditions the constructor checks — length, first, last — say
            // nothing about the values in between, so all of these used to get past them and fail
            // somewhere that blamed the geometry. The squares here are perfectly good; only the
            // index is wrong, and that is what the message has to say.
            foreach (int[] malformed in new[]
            {
                new[] { 0, 6, 3, 8 },    // decreasing — used to report "entry 0 is not strictly convex"
                new[] { 0, -3, 8 },      // negative — used to report "entry 0 has -3 vertices"
                new[] { 0, 4, 4, 8 },    // empty entry — used to report "entry 1 has 0 vertices"
                new[] { 0, 12, 8 },      // past the end — used to throw IndexOutOfRangeException
            })
            {
                var ex = Assert.Throws<ArgumentException>(
                    () => new FPBuildingShapeCatalog(x, z, malformed),
                    $"[{string.Join(",", malformed)}] must be refused");
                StringAssert.Contains("entryStart is not ascending", ex.Message);
                StringAssert.Contains("not a problem with any shape's vertices", ex.Message);
            }
        }

        [Test]
        public void CatalogHash_IsPinnedToAnAbsoluteValue()
        {
            // Every other hash assertion in this file is relative — same geometry gives the same
            // hash, different geometry gives a different one. Those all survive a change to the
            // FOLD, because both sides move together. This one does not, and that is its job.
            //
            // The value is inside the determinism envelope: the class note says to carry it in the
            // match config so a table mismatch surfaces at load, which means a build that computes
            // it differently disagrees with every config already in the wild. The standing
            // temptation is to "remove the duplicate FNV" by routing this through FPHash.Hash —
            // three FNV folds do live in this engine — but they are three different algorithms,
            // not three copies: FPHash folds a 64-bit word once, FPNavMeshRebaker folds low-64 then
            // high-32, and this one folds a byte at a time. Only the CONSTANTS are shared, and this
            // test is what tells whoever tries that their change is not cosmetic.
            long U = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
            var hex = Catalog(Hexagon(2 * U, 3 * U));

            Assert.AreEqual(0xB29C4E77408CCA84UL, hex.Hash,
                "the catalog hash changed. If the geometry above is untouched then the FOLD moved, "
                + "and this value ships in match configs — see the remarks on this test before "
                + "re-baselining it");
        }
    }
}
