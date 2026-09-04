using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Retain mode: a placement that keeps its footprint as triangulated ground instead of carving
    /// it out (<c>FPBuildingPlacement.Retain</c>).
    ///
    /// <para><b>What retain is, mechanically.</b> The ring goes in TWICE. Constraint marking ORs
    /// the wall bit and TOGGLES the parity bit, so a twice-marked edge conforms the triangulation
    /// exactly as a carve does while contributing nothing to the erase pass — the interior keeps
    /// the depth it had and survives. The arithmetic itself is pinned one layer down, in
    /// <see cref="FPCoincidentConstraintParityTests"/>; what this file proves is the PLUMBING:
    /// that a per-placement flag reaches the emission, that nothing else moves, and that the two
    /// cache keys see the mode.</para>
    ///
    /// <para><b>Retain carves nothing; it marks.</b> The footprint stays ground — <c>FindTriangle</c>
    /// reports it, the ORCA extractor stays silent about it (V-8) — and the rebaker stamps it
    /// <see cref="FPNavMeshAreas.BUILDING_MASK"/>, so the default agent mask treats it as a wall
    /// while <see cref="FPNavMeshAreas.ALL_AREAS"/> plans through it. The stamp's gates live in
    /// <see cref="FPNavMeshBuildingAreaTests"/>; this file is about the ring.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshRetainPlacementTests
    {
        #region Fixture

        private const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
        private const double Radius = 0.5;

        private static readonly FPBuildingPlacementRules TouchOk =
            new FPBuildingPlacementRules(allowBuildingTouch: true);

        /// <summary>
        /// One square shape, half extent 1.25 world units, no rotation.
        ///
        /// <para><b>1.25 is the teeth of V-1, not a round number.</b> The base slab has a vertex
        /// every 2 units, and the expansion adds the bake radius — so a half extent of 1.5 would
        /// put the EXPANDED corners at 2.0, exactly on base vertices, where
        /// <c>AddHoleVertex</c> reuses the base index. "The corner exists as a mesh vertex" would
        /// then be true whether or not the ring was ever emitted, and the conform half of V-1 —
        /// the only half that sees a dropped flag — would pass vacuously.</para>
        /// </summary>
        private static FPBuildingShapeCatalog SquareCatalog()
        {
            long h = 5 * Unit / 4;   // 1.25 world units, exact on the snap grid
            FPBuildingShapeCatalog.ObbOffsets(h, h, 4, out long[] x, out long[] z, out int[] entryStart);
            return new FPBuildingShapeCatalog(x, z, entryStart);
        }

        private static readonly FPBuildingShapeCatalog Catalog = SquareCatalog();

        /// <summary>
        /// Half extent of the footprint the rebaker ACTUALLY carves or retains, in world units —
        /// read out of the engine's own expansion rather than computed here.
        ///
        /// <para>Computing it as <c>1.25 + Radius</c> is wrong, and wrong in a way that only shows
        /// up as a failing probe: the expansion is CONSERVATIVE, so it adds the radius plus a snap
        /// unit or two of padding (measured 1.751953125, not 1.75). A fixture that hardcodes the
        /// arithmetic re-derives an engine decision and goes stale the day the padding changes.</para>
        /// </summary>
        private static readonly double ExpandedHalf =
            new FPBuildingShapeExpansion(Catalog, FP64.FromDouble(Radius)).ExpandedX[0] / (double)Unit;

        /// <summary>40x40 slab, vertices every 2 units, real agent radius.</summary>
        private static FPNavMesh BuildSlab()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -20; x <= 20; x += 2)
                for (int z = -20; z <= 20; z += 2)
                    pts.Add((x, z));
            return BuildFromPoints(pts, null);
        }

        /// <summary>
        /// The same slab with a 4x4 pond carved at the origin by the BAKE — a hole that exists
        /// before any placement. V-3 is about this hole surviving, because that is precisely what
        /// the tempting shortcut (keep every triangle at depth &gt;= 1) destroys.
        /// </summary>
        private static FPNavMesh BuildSlabWithPond()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -20; x <= 20; x += 2)
                for (int z = -20; z <= 20; z += 2)
                    pts.Add((x, z));

            // Outer ring + pond ring, as constraints over the point set; erase keeps odd depth.
            var outer = new[] { (-20, -20), (20, -20), (20, 20), (-20, 20) };
            var pond = new[] { (-4, -4), (4, -4), (4, 4), (-4, 4) };
            var cons = new List<int>();
            AppendRing(cons, pts, outer);
            AppendRing(cons, pts, pond);
            return BuildFromPoints(pts, cons.ToArray());
        }

        private static void AppendRing(List<int> into, List<(int x, int z)> pts, (int x, int z)[] ring)
        {
            for (int i = 0; i < ring.Length; i++)
            {
                int a = pts.IndexOf(ring[i]);
                int b = pts.IndexOf(ring[(i + 1) % ring.Length]);
                Assert.GreaterOrEqual(a, 0, "ring corner is not in the point set");
                Assert.GreaterOrEqual(b, 0, "ring corner is not in the point set");
                into.Add(a);
                into.Add(b);
            }
        }

        private static FPNavMesh BuildFromPoints(List<(int x, int z)> pts, int[] constraints)
        {
            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints, eraseOuterAndHoles: constraints != null);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: Radius);
        }

        private static FPNavMeshRebakeSnapshot Snapshot(FPNavMesh baseMesh = null) =>
            FPNavMeshRebaker.CreateSnapshot(
                baseMesh ?? BuildSlab(), null, prewarm: false, shapeCatalog: Catalog);

        private static FPBuildingPlacement At(double x, double z, bool retain) =>
            new FPBuildingPlacement(0, FP64.FromDouble(x), FP64.FromDouble(z), FP64.Zero, retain);

        private static readonly FPBuildingPlacementRules ClipOk =
            new FPBuildingPlacementRules(allowBuildingTouch: true, FPBoundaryPlacementPolicy.ClipOverlap);

        /// <summary>
        /// A footprint that crosses the slab's outer boundary at x = 20. Under ClipOverlap a carve
        /// of it is CLIPPED and accepted; the same footprint asked to retain is the one combination
        /// the clip stage cannot serve — a retained ring has no defined way to close against the
        /// lattice-conforming rings the clip rewrites a crossing footprint into.
        /// </summary>
        private static FPBuildingPlacement Crossing(bool retain) =>
            At(20.0 - ExpandedHalf / 2, 0.5, retain);

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        private static FPNavMesh Rebake(
            FPBuildingPlacement[] placements, FPNavMesh baseMesh = null,
            FPBuildingPlacementRules rules = default) =>
            FPNavMeshRebaker.RebakePlacements(Snapshot(baseMesh), placements, null, rules);

        /// <summary>Is (x, z) a vertex of the mesh, exactly?</summary>
        private static bool HasVertex(FPNavMesh mesh, double x, double z)
        {
            long px = FPGeoPredicates.Snap(FP64.FromDouble(x));
            long pz = FPGeoPredicates.Snap(FP64.FromDouble(z));
            var vs = mesh.Vertices;
            for (int i = 0; i < vs.Length; i++)
            {
                if (FPGeoPredicates.Snap(vs[i].x) == px && FPGeoPredicates.Snap(vs[i].z) == pz)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Is the segment (ax, az)-(bx, bz) an EDGE of some triangle? This is the conform half of
        /// V-1 and the only assertion a dropped flag cannot satisfy: without the emission the
        /// interior is still walkable (nothing carved it) and the corners may still be vertices,
        /// but the triangulation has no reason to run an edge along the footprint side.
        /// </summary>
        private static bool HasEdge(FPNavMesh mesh, double ax, double az, double bx, double bz)
        {
            long sax = FPGeoPredicates.Snap(FP64.FromDouble(ax));
            long saz = FPGeoPredicates.Snap(FP64.FromDouble(az));
            long sbx = FPGeoPredicates.Snap(FP64.FromDouble(bx));
            long sbz = FPGeoPredicates.Snap(FP64.FromDouble(bz));

            var tris = mesh.Triangles;
            var vs = mesh.Vertices;
            for (int t = 0; t < tris.Length; t++)
            {
                ref readonly var tri = ref tris[t];
                int[] v = { tri.v0, tri.v1, tri.v2 };
                for (int e = 0; e < 3; e++)
                {
                    long x0 = FPGeoPredicates.Snap(vs[v[e]].x), z0 = FPGeoPredicates.Snap(vs[v[e]].z);
                    long x1 = FPGeoPredicates.Snap(vs[v[(e + 1) % 3]].x);
                    long z1 = FPGeoPredicates.Snap(vs[v[(e + 1) % 3]].z);
                    if ((x0 == sax && z0 == saz && x1 == sbx && z1 == sbz)
                        || (x0 == sbx && z0 == sbz && x1 == sax && z1 == saz))
                        return true;
                }
            }
            return false;
        }

        private static int ObstacleRingCount(FPNavMesh mesh)
        {
            FPNavMeshObstacleExtractor.Extract(mesh, out _, out int[] polygonOffsets);
            return polygonOffsets.Length - 1;
        }

        #endregion

        // ── V-1 ─────────────────────────────────────────────────────────────

        [Test]
        public void V1_Retain_CarvesNothing_AndTheRingStillConforms()
        {
            FPNavMesh mesh = Rebake(new[] { At(0.5, 0.5, retain: true) });
            Assert.IsNotNull(mesh, "the retain placement was refused");

            Assert.IsTrue(Walkable(mesh, 0.5, 0.5),
                "the footprint interior must stay walkable — retain carves nothing");

            double x0 = 0.5 - ExpandedHalf, x1 = 0.5 + ExpandedHalf;
            double z0 = 0.5 - ExpandedHalf, z1 = 0.5 + ExpandedHalf;
            Assert.IsTrue(HasVertex(mesh, x0, z0), "the expanded corner is not a mesh vertex");
            Assert.IsTrue(HasVertex(mesh, x1, z1), "the expanded corner is not a mesh vertex");

            // The half that bites. See SquareCatalog for why the corners are deliberately off the
            // base lattice: on it, the vertex assertions above would hold with no emission at all.
            Assert.IsTrue(HasEdge(mesh, x0, z0, x1, z0),
                "the footprint side is not an edge of the triangulation — the ring was never "
                + "emitted, so the flag did not reach the emission");
            Assert.IsTrue(HasEdge(mesh, x1, z0, x1, z1), "the footprint side is not an edge");
        }

        // ── V-2 ─────────────────────────────────────────────────────────────

        [Test]
        public void V2_MixedWithCarve_EachPlacementKeepsItsOwnMode()
        {
            // The request, literally: "terrain and authored obstacles should keep carving".
            FPNavMesh mesh = Rebake(new[]
            {
                At(-8.5, 0.5, retain: true),
                At(8.5, 0.5, retain: false),
            });
            Assert.IsNotNull(mesh, "the mixed set was refused");

            Assert.IsTrue(Walkable(mesh, -8.5, 0.5), "the retained footprint must survive");
            Assert.IsFalse(Walkable(mesh, 8.5, 0.5), "the carved footprint must still be a hole");
        }

        [Test]
        public void V2_FlushRetainPair_SharesAnEdgeAtMultiplicityFour_AndStaysWalkable()
        {
            // Two retained footprints flush against each other: the shared run is marked FOUR
            // times (twice per ring). Parity is back to 0, so both interiors survive — and the
            // engine has never run a marking multiplicity above 2 before this mode existed.
            double step = 2 * ExpandedHalf;
            FPNavMesh mesh = Rebake(
                new[] { At(0.5, 0.5, retain: true), At(0.5 + step, 0.5, retain: true) },
                rules: TouchOk);
            Assert.IsNotNull(mesh, "the flush retain pair was refused");

            Assert.IsTrue(Walkable(mesh, 0.5, 0.5), "left retained footprint");
            Assert.IsTrue(Walkable(mesh, 0.5 + step, 0.5), "right retained footprint");
            Assert.IsTrue(Walkable(mesh, 0.5 + ExpandedHalf, 0.5),
                "the shared run itself is interior ground now, not a boundary");
        }

        [Test]
        public void V2_FlushRetainAndCarve_LeavesTheCarveIntactAtMultiplicityThree()
        {
            // The asymmetric case, and the one worth stating: the shared run is marked THREE times
            // (twice by the retained ring, once by the carved one), so parity is 1 and the carve
            // keeps its meaning. The odd set is exactly the carved ring — a closed curve — which
            // is what keeps the erase pass defined.
            double step = 2 * ExpandedHalf;
            FPNavMesh mesh = Rebake(
                new[] { At(0.5, 0.5, retain: true), At(0.5 + step, 0.5, retain: false) },
                rules: TouchOk);
            Assert.IsNotNull(mesh, "the flush mixed pair was refused");

            Assert.IsTrue(Walkable(mesh, 0.5, 0.5), "the retained half must survive");
            Assert.IsFalse(Walkable(mesh, 0.5 + step, 0.5),
                "the carved half must still be a hole — a neighbouring retain must not fill it in");
        }

        [Test]
        public void V2_RetainFlushAgainstTheWalkableBoundary_UnderTouch()
        {
            // A third multiplicity-3 configuration, reached through a DIFFERENT code path than the
            // pair above: the outer boundary ring contributes the single marking, and boundary
            // contact runs through the policy check rather than the pairwise test.
            double centre = 20 - ExpandedHalf;
            FPNavMesh mesh = Rebake(
                new[] { At(centre, 0, retain: true) },
                rules: new FPBuildingPlacementRules(
                    allowBuildingTouch: true, FPBoundaryPlacementPolicy.Touch));
            Assert.IsNotNull(mesh, "the boundary-flush retain placement was refused under Touch");

            Assert.IsTrue(Walkable(mesh, centre, 0),
                "a retained footprint flush on the wall keeps its ground");
        }

        // ── V-3 ─────────────────────────────────────────────────────────────

        [Test]
        public void V3_ABakedHole_IsUntouchedByARetainPlacement()
        {
            // The property the tempting shortcut destroys. Keeping every triangle at depth >= 1
            // would resurrect the pond too (measured at +54% triangles on the shipped Field), which
            // is why the mode lives at emission and not in the keep predicate.
            FPNavMesh pondMesh = BuildSlabWithPond();
            Assert.IsFalse(Walkable(pondMesh, 0, 0), "fixture: the pond is not walkable to begin with");

            FPNavMesh mesh = Rebake(new[] { At(10.5, 10.5, retain: true) }, pondMesh);
            Assert.IsNotNull(mesh, "the retain placement was refused on the pond slab");

            Assert.IsTrue(Walkable(mesh, 10.5, 10.5), "the retained footprint survives");
            Assert.IsFalse(Walkable(mesh, 0, 0),
                "the BAKED pond must stay a hole — a retain placement elsewhere may not revive it");
        }

        // ── V-4 ─────────────────────────────────────────────────────────────

        /// <summary>A table the test writes directly — the driver's seam, faked.</summary>
        private sealed class FakeSource : IFPNavMeshPlacementSource
        {
            public readonly List<FPNavMeshTimedPlacement> Entries = new List<FPNavMeshTimedPlacement>();
            public int Capacity => 8;

            public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
            {
                int n = 0;
                for (int i = 0; i < Entries.Count && n < buffer.Length; i++)
                    buffer[n++] = Entries[i];
                eligible = Entries.Count;
                return n;
            }

            public void DestroyDue(ref Frame frame, int tick) { }
        }

        private sealed class RecordingInstaller : IFPNavMeshInstaller
        {
            public readonly List<ulong> Fingerprints = new List<ulong>();

            public void Install(ref Frame frame, FPNavMesh mesh)
                => Fingerprints.Add(FPNavMeshRebaker.ComputeFingerprint(mesh));

            public void Reseed(ref Frame frame) { }
        }

        private static FPNavMeshTimedPlacement Entry(int sequence, double x, double z, bool retain) =>
            new FPNavMeshTimedPlacement
            {
                Sequence = sequence,
                Placement = At(x, z, retain),
                EffectiveTick = 0,
                RemovalEffectiveTick = int.MaxValue,
            };

        private static Frame FrameAt(int tick)
        {
            var frame = new Frame(64, null);
            frame.Tick = tick;
            return frame;
        }

        [Test]
        public void V4_TheModeReachesTheDigest()
        {
            // The cheap half, asserted directly: the digest is what tells the driver "the set
            // changed". A mode left out of it reads as "nothing changed", and the driver keeps the
            // mesh it has — with no symptom until a unit walks through a building.
            var carve = new[] { Entry(1, 0.5, 0.5, retain: false) };
            var retain = new[] { Entry(1, 0.5, 0.5, retain: true) };

            long a = FPNavMeshPlacementTableOps.Digest(carve, carve.Length, atTick: 0);
            long b = FPNavMeshPlacementTableOps.Digest(retain, retain.Length, atTick: 0);
            Assert.AreNotEqual(a, b, "the digest cannot see the mode");
        }

        [Test]
        public void V4_TogglingTheMode_MissesTheMeshCache_AndComingBackHitsIt()
        {
            // The half that matters, and the reason it is asserted through the DRIVER rather than
            // by reading SameSet: the mesh cache is per-peer local history. A mode the comparison
            // cannot see would hand a carved mesh to a retain set on the peer that happens to hold
            // one, while a peer without that cache entry rebuilds correctly — two navmeshes, one
            // state hash, no log line.
            var source = new FakeSource();
            var installer = new RecordingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(Snapshot());

            source.Entries.Add(Entry(1, 0.5, 0.5, retain: false));
            Frame f0 = FrameAt(0);
            driver.Update(ref f0);
            int rebuildsAfterCarve = driver.RebuildInstalls;
            ulong carveFingerprint = installer.Fingerprints[installer.Fingerprints.Count - 1];

            // Same shape, same centre, same sequence — only the mode differs.
            source.Entries[0] = Entry(1, 0.5, 0.5, retain: true);
            Frame f1 = FrameAt(1);
            driver.CorrectNow(ref f1);
            Assert.Greater(driver.RebuildInstalls, rebuildsAfterCarve,
                "the mode toggle hit the mesh cache — the comparison cannot see it");
            ulong retainFingerprint = installer.Fingerprints[installer.Fingerprints.Count - 1];
            Assert.AreNotEqual(carveFingerprint, retainFingerprint,
                "the installed mesh did not change, so the toggle produced the wrong geometry");

            // And the cache still works: going back to a set it has seen must NOT rebuild.
            source.Entries[0] = Entry(1, 0.5, 0.5, retain: false);
            int rebuildsBeforeReturn = driver.RebuildInstalls;
            Frame f2 = FrameAt(2);
            driver.CorrectNow(ref f2);
            Assert.AreEqual(rebuildsBeforeReturn, driver.RebuildInstalls,
                "returning to a cached set rebuilt anyway — then the assertion above proves "
                + "nothing about the mode, only that the cache is broken");
            Assert.AreEqual(
                carveFingerprint, installer.Fingerprints[installer.Fingerprints.Count - 1],
                "the cached mesh that came back is not the one that went in");
        }

        // ── V-6 ─────────────────────────────────────────────────────────────

        [Test]
        public void V6_RetainAddsNoPerRebakeAllocationOverCarve()
        {
#if DEBUG
            Assert.Ignore(
                "byte gate: DEBUG runs the CDT SelfCheck and the conforming-contract scan, which "
                + "allocate hundreds of KB per rebake and swamp the signal. Run: "
                + "dotnet test -c Release --filter FullyQualifiedName~V6_RetainAddsNo");
#else
            // A COMPARISON, not an absolute zero, and the repo's own byte gate is why: a rebake
            // legitimately allocates a few KB per call — the FPNavMesh result is unpoolable by
            // construction and the CDT's channel carve holds per-carve collections. Asserting 0
            // here would be asserting something untrue of carve as well, which proves nothing
            // about retain.
            //
            // What IS retain's claim: the mode adds no per-rebake term of its own. The PolyRetain
            // buffer and the wider constraint budget both come out of the pool, so the delta
            // against the same placement carved should be noise.
            long carve = MeasurePerRebake(new[] { At(0.5, 0.5, retain: false) });
            long retain = MeasurePerRebake(new[] { At(0.5, 0.5, retain: true) });

            TestContext.Out.WriteLine(
                $"steady-state rebake allocation: carve {carve} B · retain {retain} B "
                + $"(delta {retain - carve} B)");

            Assert.Less(retain - carve, 4L * 1024,
                $"retain added {retain - carve} B per rebake over the same placement carved — the "
                + "mode's buffers are pooled, so a growing delta means something in the retain "
                + "path is allocating per call");
#endif
        }

#if !DEBUG
        /// <summary>Steady-state bytes per rebake, warmed so pool growth is not counted.</summary>
        private static long MeasurePerRebake(FPBuildingPlacement[] placements)
        {
            var context = new FPNavMeshRebakeContext(Snapshot());

            void Once()
            {
                FPNavMeshRebaker.TryRebakePlacements(context, placements, out FPNavMesh mesh, out _);
                context.CommitSwap(mesh);
            }

            for (int warm = 0; warm < 16; warm++)
                Once();

            const int Reps = 8;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < Reps; rep++)
                Once();
            return (GC.GetAllocatedBytesForCurrentThread() - before) / Reps;
        }
#endif

        // ── V-7 ─────────────────────────────────────────────────────────────

        [Test]
        public void V7_TheModeIsPartOfTheGeometry_AndTheRebakeIsDeterministic()
        {
            var retain = new[] { At(0.5, 0.5, retain: true) };
            var carve = new[] { At(0.5, 0.5, retain: false) };

            ulong a = FPNavMeshRebaker.ComputeFingerprint(Rebake(retain));
            ulong b = FPNavMeshRebaker.ComputeFingerprint(Rebake(retain));
            ulong c = FPNavMeshRebaker.ComputeFingerprint(Rebake(carve));

            Assert.AreEqual(a, b, "the same placement set must rebake to the same mesh");
            Assert.AreNotEqual(a, c,
                "retain and carve are different geometry, so they must be different fingerprints — "
                + "if these matched, the mode would not be reaching the mesh at all");
        }

        // ── V-8 ─────────────────────────────────────────────────────────────

        [Test]
        public void V8_ARetainedFootprintEmitsNoOrcaObstacle()
        {
            // The contract with a test behind it. The extractor seeds rings from edges whose
            // neighbour is -1, and a retained footprint has walkable triangles on both sides of
            // every edge — so local avoidance simply does not know the building is there. A game
            // that wants units to bump into it owns that layer.
            int baseRings = ObstacleRingCount(BuildSlab());
            int retainRings = ObstacleRingCount(Rebake(new[] { At(0.5, 0.5, retain: true) }));
            int carveRings = ObstacleRingCount(Rebake(new[] { At(0.5, 0.5, retain: false) }));

            Assert.AreEqual(baseRings, retainRings,
                "retain must not add an obstacle ring — the contract says the rebaker makes no "
                + "claim about a retained placement, and this is where that becomes observable");
            Assert.AreEqual(baseRings + 1, carveRings,
                "control: a carve DOES add a ring, so the assertion above is not vacuous");
        }

        // ── V-9 (assert half; the timing half is [Explicit] measurement) ────

        [Test]
        public void V9_RetainAddsTheFootprintInterior_AndNothingElse()
        {
            int baseTris = BuildSlab().Triangles.Length;
            int carveTris = Rebake(new[] { At(0.5, 0.5, retain: false) }).Triangles.Length;
            int retainTris = Rebake(new[] { At(0.5, 0.5, retain: true) }).Triangles.Length;

            TestContext.Out.WriteLine(
                $"triangles: base {baseTris} · carve {carveTris} · retain {retainTris} "
                + $"(retain − carve = {retainTris - carveTris})");

            Assert.Greater(retainTris, carveTris, "retain must keep triangles a carve discards");
            Assert.LessOrEqual(retainTris - carveTris, 20,
                "the retained interior of one small footprint should be a handful of triangles — "
                + "a large delta means something other than the footprint survived");
        }

        // ── V-10 ────────────────────────────────────────────────────────────

        [Test]
        public void V10_PreviewAgreesWithTheRealPlacement_ForARetainPlacement()
        {
            // Acceptance does not depend on the mode — the rejection set is unchanged — and
            // that is exactly why the preview has to be checked rather than assumed: the preview
            // path builds its own polygons, so a mode that reached one and not the other would
            // show up here first.
            var context = FPNavMeshRebaker.CreateContext(BuildSlab(), null, prewarm: false, shapeCatalog: Catalog);
            var placements = new[] { At(0.5, 0.5, retain: true) };

            bool previewOk = FPNavMeshRebaker.TryPreviewPlacements(
                context, placements, out FPBuildingRejectionInfo previewInfo);
            context.DiscardProduced();

            bool realOk = FPNavMeshRebaker.TryRebakePlacements(
                context, placements, out FPNavMesh mesh, out FPBuildingRejectionInfo realInfo);

            Assert.AreEqual(previewOk, realOk, "preview and real placement disagree on acceptance");
            Assert.AreEqual(previewInfo.Reason, realInfo.Reason, "they disagree on the reason");
            Assert.IsTrue(realOk, "fixture: this placement is meant to be accepted");
            Assert.IsNotNull(mesh);
        }

        [Test]
        public void V10_ATrialDoesNotChangeWhatTheRealRebakeProduces()
        {
            var placements = new[] { At(0.5, 0.5, retain: true) };

            var clean = FPNavMeshRebaker.CreateContext(BuildSlab(), null, prewarm: false, shapeCatalog: Catalog);
            Assert.IsTrue(FPNavMeshRebaker.TryRebakePlacements(
                clean, placements, out FPNavMesh direct, out _));

            var trialed = FPNavMeshRebaker.CreateContext(BuildSlab(), null, prewarm: false, shapeCatalog: Catalog);
            Assert.IsTrue(FPNavMeshRebaker.TryPreviewPlacements(trialed, placements, out _));
            trialed.DiscardProduced();
            Assert.IsTrue(FPNavMeshRebaker.TryRebakePlacements(
                trialed, placements, out FPNavMesh afterTrial, out _));

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(direct),
                FPNavMeshRebaker.ComputeFingerprint(afterTrial),
                "a discarded trial must leave the next rebake bit-identical");
        }

        [Test]
        public void V10_BothGhostOnlyForms_AgreeWithTheWholeListPreview_UnderClipOverlap()
        {
            // Wiring plan B-6 (U-2). The whole-list preview goes through the rebake's own checks;
            // the two ghost-only forms run ValidateGhostOnly, which had no notion of the mode — so
            // retain + ClipOverlap was the one input on which the public preview forms answered
            // differently (one threw, the others accepted). Both outcomes, all three forms.
            AssertPreviewFormsAgree(At(0.5, 0.5, retain: true), FPBuildingRejection.None);
            AssertPreviewFormsAgree(Crossing(retain: true), FPBuildingRejection.TouchesWalkableBoundary);
        }

        private static void AssertPreviewFormsAgree(FPBuildingPlacement ghost, FPBuildingRejection expected)
        {
            var context = FPNavMeshRebaker.CreateContext(BuildSlab(), null, prewarm: false, shapeCatalog: Catalog);

            // Ghost-only forms FIRST. A successful whole-list trial captures its set as what the
            // context last carved (DiscardProduced drops only the mesh), and against that set the
            // same ghost is an overlap with itself — a different question, not a disagreement.
            bool okCtx = FPNavMeshRebaker.TryValidateOnePlacement(
                context, ghost, out FPBuildingRejectionInfo infoCtx, ClipOk);
            bool okSnap = FPNavMeshRebaker.TryValidateOnePlacement(
                context.Snapshot, new FPBuildingPlacement[0], 0, ghost,
                out FPBuildingRejectionInfo infoSnap, new FPBuildingPreviewScratch(), ClipOk);
            bool okList = FPNavMeshRebaker.TryPreviewPlacements(
                context, new[] { ghost }, out FPBuildingRejectionInfo infoList, rules: ClipOk);

            // Named, not just matched — three forms agreeing on the wrong reason prove nothing.
            Assert.AreEqual(expected, infoList.Reason, "fixture: the whole-list preview reaches a different check");
            Assert.AreEqual(okList, okCtx, "context form: accept/refuse differs from the whole-list preview");
            Assert.AreEqual(okList, okSnap, "list form: accept/refuse differs from the whole-list preview");
            Assert.AreEqual(infoList.Reason, infoCtx.Reason, "context form: reason differs");
            Assert.AreEqual(infoList.Reason, infoSnap.Reason, "list form: reason differs");
            Assert.AreEqual(infoList.IndexA, infoCtx.IndexA, "context form: IndexA differs");
            Assert.AreEqual(infoList.IndexA, infoSnap.IndexA, "list form: IndexA differs");
        }

        // ── V-11 ────────────────────────────────────────────────────────────

        [Test]
        public void V11_ManyRetainPlacements_DoNotOverrunTheConstraintBuffer()
        {
            // The only gate that walks into the capacity arithmetic. A retained ring contributes
            // its edges twice, so the pair budget is (vertices + retained vertices) * 2 — sized as
            // vertices * 2, which is correct for every carve-only set, this throws
            // IndexOutOfRangeException, and only on a stage that places retained buildings.
            var placements = new List<FPBuildingPlacement>();
            double step = 2 * ExpandedHalf + 1;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    placements.Add(At(-9 + i * step, -9 + j * step, retain: true));
            Assert.AreEqual(16, placements.Count, "fixture: 16 placements");

            FPNavMesh mesh = Rebake(placements.ToArray(), rules: TouchOk);
            Assert.IsNotNull(mesh, "16 retained placements were refused");

            foreach (var p in placements)
            {
                Assert.IsTrue(
                    Walkable(mesh, p.CentreX.ToDouble(), p.CentreZ.ToDouble()),
                    "every retained footprint keeps its ground");
            }
        }

        // ── V-12 ────────────────────────────────────────────────────────────

        // The policy no longer decides retain under ClipOverlap; the building's clip TRANSITIONS
        // do. Transition-free retain is admitted and
        // retains; retain with transitions is a rejection value, not a throw.

        [Test]
        public void V12_RetainUnderClipOverlap_TransitionFree_IsAdmitted_AndRetains()
        {
            FPNavMesh mesh = Rebake(new[] { At(0.5, 0.5, retain: true) }, rules: ClipOk);
            Assert.IsNotNull(mesh, "a transition-free retain placement must be admitted under ClipOverlap");
            Assert.IsTrue(Walkable(mesh, 0.5, 0.5), "retain under ClipOverlap still carves nothing");

            // The identity path has its own emission loop — the doubled ring has to reach it too.
            double x0 = 0.5 - ExpandedHalf, x1 = 0.5 + ExpandedHalf, z0 = 0.5 - ExpandedHalf;
            Assert.IsTrue(HasEdge(mesh, x0, z0, x1, z0),
                "the footprint side is not an edge — the identity path dropped the doubled ring");

            // And the same identity path still carves when asked to.
            Assert.IsFalse(
                Walkable(Rebake(new[] { At(0.5, 0.5, retain: false) }, rules: ClipOk), 0.5, 0.5),
                "carve under ClipOverlap must be unaffected");
        }

        [Test]
        public void V12_RetainUnderClipOverlap_WithTransitions_IsRefused_AsTouchesWalkableBoundary()
        {
            // The fixture's teeth first: this footprint really does cross, and a carve of it is
            // really clipped rather than refused — otherwise the retain refusal below would be the
            // ordinary boundary rejection wearing a new name.
            Assert.IsNotNull(Rebake(new[] { Crossing(retain: false) }, rules: ClipOk),
                "fixture: the crossing carve must be clipped and accepted");

            var context = FPNavMeshRebaker.CreateContext(BuildSlab(), null, prewarm: false, shapeCatalog: Catalog);
            bool ok = FPNavMeshRebaker.TryRebakePlacements(
                context, new[] { Crossing(retain: true) }, out FPNavMesh mesh,
                out FPBuildingRejectionInfo info, rules: ClipOk);

            Assert.IsFalse(ok, "a retain placement with clip transitions must be refused, not clipped");
            Assert.IsNull(mesh);
            Assert.AreEqual(FPBuildingRejection.TouchesWalkableBoundary, info.Reason,
                "a rejection VALUE, not a throw — the player caused this and is meant to see it");
            Assert.AreEqual(0, info.IndexA, "the rejection must name the retained building");
        }
    }
}
