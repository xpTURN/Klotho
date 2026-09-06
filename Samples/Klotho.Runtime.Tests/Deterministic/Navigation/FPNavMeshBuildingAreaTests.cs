using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The building area (<see cref="FPNavMeshAreas"/>): a retained footprint comes out of the
    /// rebaker stamped <c>BUILDING_MASK</c> exclusively, the agent system's default mask excludes
    /// exactly that bit, and <c>ALL_AREAS</c> plans through it.
    ///
    /// <para><b>The oracle is not the stamp.</b> "Inside the footprint" is computed here, in doubles,
    /// from the engine's own expansion and the triangle centroid — so the assertions say what the
    /// stamp should have done, not what it did. That works because the ring went in as constraint
    /// edges: no triangle straddles it, so a centroid is never near the boundary.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshBuildingAreaTests
    {
        #region Fixture

        /// <summary>8 x 8 cells of 2 units: a 16 x 16 field, area 0 everywhere.</summary>
        private const int Cells = 8;

        /// <summary>Building centre, mid-field.</summary>
        private const double Bx = 8.0, Bz = 8.0;

        private static FPNavMesh Field() => NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);

        private static FPNavMesh WithRetained() =>
            NavAgentTestHelper.RebakeWithBuildings(Field(), NavAgentTestHelper.Building(Bx, Bz, retain: true));

        private static FPVector3 P(double x, double z) =>
            new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        private static bool InsideFootprint(double x, double z, double half) =>
            System.Math.Abs(x - Bx) < half - 1e-6 && System.Math.Abs(z - Bz) < half - 1e-6;

        private static bool CentroidInsideFootprint(FPNavMesh mesh, int t, double half)
        {
            ref readonly var tri = ref mesh.Triangles[t];
            var vs = mesh.Vertices;
            double cx = (vs[tri.v0].x.ToDouble() + vs[tri.v1].x.ToDouble() + vs[tri.v2].x.ToDouble()) / 3.0;
            double cz = (vs[tri.v0].z.ToDouble() + vs[tri.v1].z.ToDouble() + vs[tri.v2].z.ToDouble()) / 3.0;
            return InsideFootprint(cx, cz, half);
        }

        private sealed class CapturingLogger : IKLogger
        {
            public readonly List<string> Errors = new List<string>();
            public bool IsEnabled(KLogLevel level) => true;
            public void Log(KLogLevel level, string message, Exception exception)
            {
                if (level == KLogLevel.Error) Errors.Add(message);
            }
        }

        #endregion

        // ── C-1 ─────────────────────────────────────────────────────────────

        [Test]
        public void C1_RetainedFootprint_IsStampedBuildingMask_Exclusively_AndNothingElseMoves()
        {
            var baseMesh = Field();
            int baseMask = baseMesh.Triangles[0].areaMask;
            Assert.AreEqual(1 << 0, baseMask, "fixture: the field is area 0");

            var mesh = NavAgentTestHelper.RebakeWithBuildings(
                baseMesh, NavAgentTestHelper.Building(Bx, Bz, retain: true));
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            int inside = 0, outside = 0;
            for (int t = 0; t < mesh.Triangles.Length; t++)
            {
                int mask = mesh.Triangles[t].areaMask;
                if (CentroidInsideFootprint(mesh, t, half))
                {
                    inside++;
                    // Exclusive, not OR-ed: a base bit left in place would intersect every default
                    // query's mask and the stamp would block nothing.
                    Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mask,
                        $"triangle {t} is inside the retained footprint but its mask is {mask}");
                }
                else
                {
                    outside++;
                    Assert.AreEqual(baseMask, mask,
                        $"triangle {t} is outside the footprint and must keep the base area");
                }
            }
            Assert.Greater(inside, 0, "fixture: no triangle inside the footprint");
            Assert.Greater(outside, 0, "fixture: no triangle outside the footprint");
        }

        [Test]
        public void C1_CarveAndBase_CarryNoBuildingBit()
        {
            var baseMesh = Field();
            var carved = NavAgentTestHelper.RebakeWithBuildings(
                baseMesh, NavAgentTestHelper.Building(Bx, Bz, retain: false));

            foreach (var m in new[] { baseMesh, carved })
            {
                for (int t = 0; t < m.Triangles.Length; t++)
                {
                    Assert.AreEqual(0, m.Triangles[t].areaMask & FPNavMeshAreas.BUILDING_MASK,
                        $"triangle {t} carries the building bit without a retained building");
                }
            }
        }

        [Test]
        public void C1_PatchAndFullPaths_StampTheSame()
        {
            // The patch path rebuilds areaMask from the bake areas — it deliberately carries none of
            // an installed mesh's post-build writes — so the stamp has to be re-applied after it.
            // Running it in Finish, after the inherit, is what guarantees that; this is the check.
            var first = new[] { NavAgentTestHelper.Building(Bx, Bz, retain: true) };
            var second = new[]
            {
                NavAgentTestHelper.Building(Bx, Bz, retain: true),
                NavAgentTestHelper.Building(3.0, 12.0, retain: false),
            };

            var context = FPNavMeshRebaker.CreateContext(
                Field(), null, prewarm: false, shapeCatalog: NavAgentTestHelper.SquareBuildingCatalog);
            Assert.IsTrue(FPNavMeshRebaker.TryRebakePlacements(
                context, first, out FPNavMesh firstMesh, out _, rules: NavAgentTestHelper.TouchRules));
            context.CommitSwap(firstMesh);
            Assert.IsTrue(FPNavMeshRebaker.TryRebakePlacements(
                context, second, out FPNavMesh patched, out _, rules: NavAgentTestHelper.TouchRules));
            Assert.Greater(context.PatchOutcome.Incremental, 0,
                "fixture: the second rebake did not take the patch path");

            var full = NavAgentTestHelper.RebakeWithBuildings(Field(), second);

            // The fingerprint mixes areaMask, so a stamp the patch path lost lands here.
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(full), FPNavMeshRebaker.ComputeFingerprint(patched),
                "the patched mesh and the from-scratch mesh differ");
        }

        // ── C-2 ─────────────────────────────────────────────────────────────

        [Test]
        public void C2_TheBuildPipeline_RefusesABakedTriangleInTheBuildingArea()
        {
            var vertices = new[] { P(0, 0), P(4, 0), P(0, 4), P(4, 4) };
            var indices = new[] { 0, 1, 2, 2, 1, 3 };
            var log = new CapturingLogger();

            var e = Assert.Throws<ArgumentException>(() => FPNavMeshBuildPipeline.Build(
                vertices, indices, new[] { 0, FPNavMeshAreas.BUILDING_AREA }, 1.0, log));
            Assert.That(e.Message, Does.Contain("triangle 1"), "the message must name the triangle");
            Assert.That(e.Message, Does.Contain("BUILDING_AREA"), "the message must name the constant");
            Assert.AreEqual(1, log.Errors.Count, "one KError, before the throw");

            // Any other index is an ordinary area — including 3, which the two-area fixture uses.
            Assert.IsNotNull(FPNavMeshBuildPipeline.Build(vertices, indices, new[] { 0, 3 }, 1.0, null));
        }

        // ── C-3 ─────────────────────────────────────────────────────────────

        [Test]
        public void C3_TheConstants_AgreeWithEachOther()
        {
            Assert.AreEqual(1, FPNavMeshAreas.BUILDING_AREA);
            Assert.AreEqual(1 << 1, FPNavMeshAreas.BUILDING_MASK);
            Assert.AreEqual(~FPNavMeshAreas.BUILDING_MASK, FPNavMeshAreas.DEFAULT_AGENT_MASK);
            Assert.AreEqual(~0, FPNavMeshAreas.ALL_AREAS);
            Assert.AreEqual(FPNavMeshAreas.DEFAULT_AGENT_MASK, FPNavAgentSystem.DEFAULT_AREA_MASK,
                "the agent system must pass the mask that excludes the building area");
        }

        [Test]
        public void C3_AnAgent_NeitherPlansThroughNorWalksInto_ARetainedFootprint()
        {
            var mesh = WithRetained();
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            var query = new FPNavMeshQuery(mesh, null);
            var system = NavAgentTestHelper.CreateSystem(mesh, null, out var pathfinder);

            // Start and goal on opposite sides of the building, on the line through its centre —
            // the straight route crosses the footprint, so any route that stays out is a detour.
            FPVector3 start = P(2.0, Bz), goal = P(14.0, Bz);
            int startTri = query.FindTriangle(start.ToXZ());
            Assert.GreaterOrEqual(startTri, 0, "fixture: start is off-mesh");

            // The plan: no corridor triangle is a building triangle.
            Assert.IsTrue(pathfinder.FindPath(start, goal, FPNavAgentSystem.DEFAULT_AREA_MASK,
                out int[] corridor, out int corridorLength), "the detour must exist");
            for (int i = 0; i < corridorLength; i++)
            {
                Assert.AreNotEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[corridor[i]].areaMask,
                    $"corridor triangle {i} is inside the retained footprint");
            }

            // The walk: the agent gets there, and is never inside the footprint on the way.
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, startTri, out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, goal);

            int ticksInside = 0, tick;
            for (tick = 1; tick <= 1200; tick++)
            {
                system.Update(ref frame, entities, entities.Length, tick, NavAgentTestHelper.DT);
                ref var cur = ref frame.Get<NavAgentComponent>(entity);
                Assert.AreNotEqual(FPNavMeshAreas.BUILDING_MASK,
                    mesh.Triangles[cur.CurrentTriangleIndex].areaMask,
                    $"tick {tick}: the agent stands on a retained triangle");
                if (InsideFootprint(cur.Position.x.ToDouble(), cur.Position.z.ToDouble(), half))
                    ticksInside++;
                if ((FPNavAgentStatus)cur.Status == FPNavAgentStatus.Arrived)
                    break;
            }

            ref var final = ref frame.Get<NavAgentComponent>(entity);
            Assert.AreEqual(FPNavAgentStatus.Arrived, (FPNavAgentStatus)final.Status,
                $"the agent never got around the building (tick {tick}, pos {final.Position})");
            Assert.AreEqual(0, ticksInside, "the agent was inside the footprint");
            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount,
                "both endpoints are outside the footprint — nothing should have been refused");
        }

        [Test]
        public void C3_ADestinationInsideTheFootprint_IsRefused_AndCounted()
        {
            var mesh = WithRetained();
            var query = new FPNavMeshQuery(mesh, null);
            var system = NavAgentTestHelper.CreateSystem(mesh, null, out var pathfinder);

            FPVector3 start = P(2.0, Bz);
            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ()), out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));

            system.Update(ref frame, entities, entities.Length, 1, NavAgentTestHelper.DT);

            ref var cur = ref frame.Get<NavAgentComponent>(entity);
            Assert.AreEqual(FPNavAgentStatus.PathFailed, (FPNavAgentStatus)cur.Status);
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "the refusal must be visible in the counter — this is the one way an agent trips it");
        }

        // ── C-4 ─────────────────────────────────────────────────────────────

        [Test]
        public void C4_AllAreas_StillPlansThroughTheFootprint()
        {
            // The asymmetry that makes retain worth having over carve: the same mesh, the same
            // call, and the verdict depends only on the mask. Three forms, each with a retained
            // triangle at an endpoint, because that is the geometry-independent one: a corridor
            // between two OUTSIDE points is free to skirt a small footprint under either mask —
            // the corridor cost is a polyline through portal midpoints, and the fan the ring's
            // corners make inside the footprint costs about what the slivers along its edge do.
            // That is a fact about the cost model, not about the area filter.
            var mesh = NavAgentTestHelper.RebakeWithBuildings(Field(),
                NavAgentTestHelper.Building(Bx, Bz, retain: true),
                NavAgentTestHelper.Building(3.0, 3.0, retain: true));
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            FPVector3 insideA = P(Bx, Bz), insideB = P(3.0, 3.0), outside = P(14.0, Bz);

            // Out of a footprint — and this one INVERTED when the start exemption landed.
            // It used to assert that the default mask had no
            // route out, which made a unit a building had been dropped on permanently immobile:
            // the agent system only walks along a corridor, so no corridor meant no movement.
            // The start is now exempt and the escape rule carries the path off the footprint,
            // while the END stays refused — the pair below is what pins "only the start".
            Assert.IsTrue(pathfinder.FindPath(insideA, outside, FPNavAgentSystem.DEFAULT_AREA_MASK,
                out _, out _), "an agent inside a retained footprint must be able to route OUT by "
                + "its own mask — the start triangle is exempt, as it is for the walk");
            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount,
                "and that is not an area-mask rejection; the endpoint counter is end-only now");
            Assert.AreEqual(1, pathfinder.DebugMaskedStartCount,
                "it is reported separately, because otherwise nothing says the unit was inside a "
                + "building at all");
            Assert.IsTrue(pathfinder.FindPath(insideA, outside, FPNavMeshAreas.ALL_AREAS,
                out int[] corridor, out int corridorLength), "ALL_AREAS must route out of the footprint");
            Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[corridor[0]].areaMask,
                "the corridor must start on the retained triangle the start point lies in");
            Assert.AreNotEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[corridor[corridorLength - 1]].areaMask);

            // Into one.
            Assert.IsFalse(pathfinder.FindPath(outside, insideA, FPNavAgentSystem.DEFAULT_AREA_MASK,
                out _, out _), "a destination inside a retained footprint is refused by the agent's mask");
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "the END is still gated — this is the asymmetry with the clause above");
            Assert.IsTrue(pathfinder.FindPath(outside, insideA, FPNavMeshAreas.ALL_AREAS,
                out corridor, out corridorLength), "ALL_AREAS must route into the footprint");
            Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[corridor[corridorLength - 1]].areaMask);

            // Through: from inside one building to inside the other, the corridor leaves A's ring,
            // crosses ordinary ground and enters B's — building triangles at both ends, base
            // triangles in between.
            Assert.IsTrue(pathfinder.FindPath(insideA, insideB, FPNavMeshAreas.ALL_AREAS,
                out corridor, out corridorLength), "ALL_AREAS must route from one footprint to another");
            int building = CountBuildingTriangles(mesh, corridor, corridorLength);
            Assert.Greater(building, 1, "Corridor: " + Describe(mesh, corridor, corridorLength));
            Assert.Greater(corridorLength - building, 0,
                "the two footprints are apart — the corridor must cross base triangles between them. Corridor: "
                + Describe(mesh, corridor, corridorLength));
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount, "ALL_AREAS refused nothing");
        }

        private static string Describe(FPNavMesh mesh, int[] corridor, int corridorLength)
        {
            var sb = new System.Text.StringBuilder();
            var vs = mesh.Vertices;
            for (int i = 0; i < corridorLength; i++)
            {
                ref readonly var tri = ref mesh.Triangles[corridor[i]];
                double cx = (vs[tri.v0].x.ToDouble() + vs[tri.v1].x.ToDouble() + vs[tri.v2].x.ToDouble()) / 3.0;
                double cz = (vs[tri.v0].z.ToDouble() + vs[tri.v1].z.ToDouble() + vs[tri.v2].z.ToDouble()) / 3.0;
                sb.Append($"[{corridor[i]} ({cx:F2},{cz:F2}) m={tri.areaMask}] ");
            }
            return sb.ToString();
        }

        private static int CountBuildingTriangles(FPNavMesh mesh, int[] corridor, int corridorLength)
        {
            int through = 0;
            for (int i = 0; i < corridorLength; i++)
            {
                if (mesh.Triangles[corridor[i]].areaMask == FPNavMeshAreas.BUILDING_MASK)
                    through++;
            }
            return through;
        }

        // ── C-5 ─────────────────────────────────────────────────────────────

        [Test]
        public void C5_TheStamp_IsDeterministic_AndAFingerprintInput()
        {
            var a = WithRetained();
            var b = WithRetained();
            ulong stamped = FPNavMeshRebaker.ComputeFingerprint(a);
            Assert.AreEqual(stamped, FPNavMeshRebaker.ComputeFingerprint(b),
                "the same placement set must produce the same fingerprint");

            // Undo the stamp by hand — same geometry, base area everywhere — and the fingerprint
            // must move: peers that disagree on the stamp have to be told, and the fingerprint
            // cross-check is where they are told.
            var tris = a.TrianglesMutable;
            for (int t = 0; t < tris.Length; t++)
                tris[t].areaMask = 1 << 0;
            Assert.AreNotEqual(stamped, FPNavMeshRebaker.ComputeFingerprint(a),
                "the stamp is not part of the fingerprint");
        }

        // ── C-6: the stamp's TRAVERSAL ───────────────────────────────────────
        //
        // C-1 pins one building at one position on one stage. What the stamp DOES with the mesh —
        // which triangles it even looks at — is a separate axis, and these widen C-1's oracle along
        // it: building count, position relative to the broadphase grid, and stage size.
        //
        // The oracle is deliberately C-1's and not a second implementation. "Inside" is recomputed
        // here in doubles from the engine's own expansion, so these assert what the stamp SHOULD
        // have done rather than that two implementations agree — a duplicated implementation would
        // be green against the implementation it duplicates and would verify nothing.
        //
        // The grid matters because the field's broadphase cell is 8 world units (CELL * 4) while a
        // footprint is ~3.5 across: a footprint sits inside one cell, straddles two, or straddles
        // four depending on where it lands, and a traversal that walks cells has to get all three
        // right.

        /// <summary>Field extent in world units for a `cells` x `cells` lattice.</summary>
        private static double Extent(int cells) => cells * 2.0;

        private static FPNavMesh RetainedAt(int cells, params (double x, double z)[] centres)
        {
            var placements = new FPBuildingPlacement[centres.Length];
            for (int i = 0; i < centres.Length; i++)
                placements[i] = NavAgentTestHelper.Building(centres[i].x, centres[i].z, retain: true);
            return NavAgentTestHelper.RebakeWithBuildings(
                NavAgentTestHelper.CreateOpenFieldNavMesh(cells), placements);
        }

        /// <summary>
        /// C-1's assertion over an arbitrary footprint set: every triangle, both directions.
        /// <paramref name="half"/> is the engine's own expanded half extent, and the footprints are
        /// axis-aligned squares of it, so "inside any" is the union test.
        /// </summary>
        private static void AssertStampedExactly(
            FPNavMesh mesh, double half, params (double x, double z)[] centres)
        {
            int baseMask = 1 << 0;
            int inside = 0, outside = 0;
            var vs = mesh.Vertices;
            for (int t = 0; t < mesh.Triangles.Length; t++)
            {
                ref readonly FPNavMeshTriangle tri = ref mesh.Triangles[t];
                double cx = (vs[tri.v0].x.ToDouble() + vs[tri.v1].x.ToDouble() + vs[tri.v2].x.ToDouble()) / 3.0;
                double cz = (vs[tri.v0].z.ToDouble() + vs[tri.v1].z.ToDouble() + vs[tri.v2].z.ToDouble()) / 3.0;

                bool within = false;
                foreach ((double x, double z) c in centres)
                {
                    if (System.Math.Abs(cx - c.x) < half - 1e-9 && System.Math.Abs(cz - c.z) < half - 1e-9)
                    {
                        within = true;
                        break;
                    }
                }

                if (within)
                {
                    inside++;
                    Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, tri.areaMask,
                        $"triangle {t} (centroid {cx:F3}, {cz:F3}) is inside a retained footprint " +
                        "but was not stamped — the traversal missed it");
                }
                else
                {
                    outside++;
                    Assert.AreEqual(baseMask, tri.areaMask,
                        $"triangle {t} (centroid {cx:F3}, {cz:F3}) is outside every footprint " +
                        "but carries the building bit");
                }
            }
            Assert.Greater(inside, 0, "fixture: nothing inside a footprint");
            Assert.Greater(outside, 0, "fixture: nothing outside every footprint");
        }

        /// <summary>Non-overlapping lattice of centres, stepped wider than a full footprint.</summary>
        private static (double x, double z)[] Lattice(int count, double first, double step)
        {
            int perSide = (int)System.Math.Ceiling(System.Math.Sqrt(count));
            var result = new (double x, double z)[count];
            for (int i = 0; i < count; i++)
                result[i] = (first + (i % perSide) * step, first + (i / perSide) * step);
            return result;
        }

        [Test]
        public void C6_OneFootprint_OnACellBoundary_IsStampedExactly()
        {
            // Centre (8,8) with an 8-unit cell: the footprint straddles FOUR cells.
            var centres = new[] { (8.0, 8.0) };
            FPNavMesh mesh = RetainedAt(8, centres);
            AssertStampedExactly(mesh, NavAgentTestHelper.ExpandedBuildingHalf(mesh), centres);
        }

        [Test]
        public void C6_OneFootprint_InsideASingleCell_IsStampedExactly()
        {
            // Centre (4,4): the whole footprint [2.25, 5.75] lives in cell (0,0).
            var centres = new[] { (4.0, 4.0) };
            FPNavMesh mesh = RetainedAt(8, centres);
            AssertStampedExactly(mesh, NavAgentTestHelper.ExpandedBuildingHalf(mesh), centres);
        }

        [Test]
        public void C6_OneFootprint_AtTheMapCorner_IsStampedExactly()
        {
            // As close to (0,0) as the expansion allows: cell (0,0) is also the grid's first cell,
            // so a range computed with a sign slip lands outside and stamps nothing.
            var centres = new[] { (2.0, 2.0) };
            FPNavMesh mesh = RetainedAt(8, centres);
            AssertStampedExactly(mesh, NavAgentTestHelper.ExpandedBuildingHalf(mesh), centres);
        }

        [Test]
        public void C6_AFlushPair_IsStampedExactly()
        {
            // Two footprints sharing an edge — the shared run is interior ground, and a triangle
            // there belongs to exactly one of the two rings.
            FPNavMesh probe = RetainedAt(8, (4.0, 4.0));
            double half = NavAgentTestHelper.ExpandedBuildingHalf(probe);

            var centres = new[] { (4.0, 4.0), (4.0 + 2 * half, 4.0) };
            FPNavMesh mesh = RetainedAt(8, centres);
            AssertStampedExactly(mesh, half, centres);
        }

        [Test]
        public void C6_TwoFootprints_InDifferentCells_AreStampedExactly()
        {
            var centres = new[] { (4.0, 4.0), (12.0, 12.0) };
            FPNavMesh mesh = RetainedAt(8, centres);
            AssertStampedExactly(mesh, NavAgentTestHelper.ExpandedBuildingHalf(mesh), centres);
        }

        [Test]
        public void C6_EightFootprints_OnALargerStage_AreStampedExactly()
        {
            var centres = Lattice(8, first: 4.0, step: 6.0);
            FPNavMesh mesh = RetainedAt(32, centres);
            AssertStampedExactly(mesh, NavAgentTestHelper.ExpandedBuildingHalf(mesh), centres);
        }

        [Test]
        public void C6_ThirtyTwoFootprints_AreStampedExactly()
        {
            // The policy cap. The building loop is what this change restructures, so the count is
            // the axis that matters most and no other test asserts masks with more than one.
            var centres = Lattice(32, first: 4.0, step: 6.0);
            FPNavMesh mesh = RetainedAt(32, centres);
            AssertStampedExactly(mesh, NavAgentTestHelper.ExpandedBuildingHalf(mesh), centres);
        }

        // ── C-7: the defensive edges, reached by calling the stamp directly ──
        //
        // A footprint outside the mesh cannot be PLACED — validation refuses it — so these go
        // through the internal entry point. That is the honest shape: this is a guard, not a
        // reachable defect, and a test that pretended otherwise would be asserting on a rejection.

        private const byte RetainByte = 1;   // FPNavMeshRebaker.RETAIN — private const, hence the literal
        private const byte CarveByte = 0;    // FPNavMeshRebaker.CARVE

        private static void Ring(
            double minX, double minZ, double maxX, double maxZ,
            out long[] px, out long[] pz, out int[] start)
        {
            long u = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
            long x0 = (long)(minX * u), z0 = (long)(minZ * u);
            long x1 = (long)(maxX * u), z1 = (long)(maxZ * u);
            px = new[] { x0, x1, x1, x0 };
            pz = new[] { z0, z0, z1, z1 };
            start = new[] { 0, 4 };
        }

        [Test]
        public void C7_AFootprintWhollyOutsideTheGrid_StampsNothing()
        {
            FPNavMesh mesh = RetainedAt(8, (8.0, 8.0));
            var before = new int[mesh.Triangles.Length];
            for (int t = 0; t < before.Length; t++)
                before[t] = mesh.Triangles[t].areaMask;

            foreach ((double x0, double z0, double x1, double z1) box in new[]
            {
                (-100.0, -100.0, -90.0, -90.0),   // below the origin in both axes
                (100.0, 100.0, 110.0, 110.0),     // past the far corner
                (-100.0, 4.0, -90.0, 12.0),       // left of the grid, overlapping in z
            })
            {
                Ring(box.x0, box.z0, box.x1, box.z1, out long[] px, out long[] pz, out int[] start);
                FPNavMeshRebaker.StampRetainedFootprints(
                    mesh, px, pz, start, 1, new[] { RetainByte });
            }

            for (int t = 0; t < before.Length; t++)
                Assert.AreEqual(before[t], mesh.Triangles[t].areaMask,
                    $"triangle {t} moved for a footprint that is entirely outside the mesh");
        }

        [Test]
        public void C7_AFootprintHalfOutsideTheGrid_StampsTheHalfInside()
        {
            // Clamping the cell range must not lose the part that IS on the mesh.
            FPNavMesh mesh = RetainedAt(8, (12.0, 12.0));
            Ring(-4.0, -4.0, 4.0, 4.0, out long[] px, out long[] pz, out int[] start);
            FPNavMeshRebaker.StampRetainedFootprints(
                mesh, px, pz, start, 1, new[] { RetainByte });

            // Union of the placed footprint and the hand-made ring's on-mesh part.
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            int stamped = 0;
            var vs = mesh.Vertices;
            for (int t = 0; t < mesh.Triangles.Length; t++)
            {
                ref readonly FPNavMeshTriangle tri = ref mesh.Triangles[t];
                double cx = (vs[tri.v0].x.ToDouble() + vs[tri.v1].x.ToDouble() + vs[tri.v2].x.ToDouble()) / 3.0;
                double cz = (vs[tri.v0].z.ToDouble() + vs[tri.v1].z.ToDouble() + vs[tri.v2].z.ToDouble()) / 3.0;
                bool inRing = cx > -4.0 && cx < 4.0 && cz > -4.0 && cz < 4.0;
                bool inPlaced = System.Math.Abs(cx - 12.0) < half - 1e-9
                             && System.Math.Abs(cz - 12.0) < half - 1e-9;
                if (inRing || inPlaced)
                {
                    stamped++;
                    Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, tri.areaMask,
                        $"triangle {t} (centroid {cx:F3}, {cz:F3}) is inside the clipped ring but was not stamped");
                }
            }
            Assert.Greater(stamped, 0, "fixture: the half-outside ring covered no triangle");
        }

        [Test]
        public void C7_ACarveSlot_IsNotStamped()
        {
            FPNavMesh mesh = RetainedAt(8, (12.0, 12.0));
            var before = new int[mesh.Triangles.Length];
            for (int t = 0; t < before.Length; t++)
                before[t] = mesh.Triangles[t].areaMask;

            Ring(2.0, 2.0, 6.0, 6.0, out long[] px, out long[] pz, out int[] start);
            FPNavMeshRebaker.StampRetainedFootprints(
                mesh, px, pz, start, 1, new[] { CarveByte });

            for (int t = 0; t < before.Length; t++)
                Assert.AreEqual(before[t], mesh.Triangles[t].areaMask,
                    $"triangle {t} moved for a CARVE slot");
        }

        // ── C-8: the premise the traversal argument hangs on ─────────────────

        [Test]
        public void C8_TheBroadphaseCellFunction_IsMonotone()
        {
            // "The centroid is inside the ring's AABB" only implies "its cell is inside the ring's
            // CELL RANGE" if the coordinate -> cell map is monotone. It is — FP64 division
            // truncates toward zero and ToInt() floors, and both are non-decreasing — but nothing
            // pinned it, and the whole traversal argument rests on it.
            FPNavMesh mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            int prevCol = int.MinValue, prevRow = int.MinValue;
            for (double v = -40.0; v <= 40.0; v += 0.37)
            {
                mesh.GetCellCoords(
                    new FPVector2(FP64.FromDouble(v), FP64.FromDouble(v)), out int col, out int row);
                Assert.GreaterOrEqual(col, prevCol, $"col went backwards at {v}");
                Assert.GreaterOrEqual(row, prevRow, $"row went backwards at {v}");
                prevCol = col;
                prevRow = row;
            }
        }
    }
}
