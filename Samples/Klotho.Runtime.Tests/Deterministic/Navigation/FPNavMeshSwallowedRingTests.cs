using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// A building that fully contains a baked hole ring is rejected.
    ///
    /// Left alone, such a placement is accepted by every other check and produces a WRONG navmesh:
    /// even-odd erasure counts one more ring crossing, so the hole's interior flips back to
    /// walkable and agents walk into a pillar. Nothing detects it — every peer computes the same
    /// wrong mesh, so no desync check fires either.
    /// </summary>
    [TestFixture]
    public class FPNavMeshSwallowedRingTests
    {
        #region Fixture

        private const double R = 0.5;   // bake agent radius of every fixture here

        /// <summary>
        /// 16x16 slab, vertices every 2 units, with a square pillar hole [-2,2] carved at bake
        /// time — the shape that makes the defect possible. Ring constraints run through every
        /// grid vertex on the boundary so no vertex lands in the middle of a constraint edge.
        /// </summary>
        private static FPNavMesh BuildAnnulus() => BuildBase(withPillar: true);

        /// <summary>Same slab with no hole — the control: the check has nothing to find.</summary>
        private static FPNavMesh BuildSolid() => BuildBase(withPillar: false);

        private static FPNavMesh BuildBase(bool withPillar)
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

            if (withPillar)
                Ring((-2, -2), (0, -2), (2, -2), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0));

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: R);
        }

        /// <summary>Rect in ORIGINAL units; the rebaker expands it by the bake radius.</summary>
        private static FPBuildingRect B(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        /// <summary>Building whose EXPANDED rect is [-4.5,4.5]^2 — it swallows the [-2,2] pillar.</summary>
        private static FPBuildingRect[] SwallowsPillar() => new[] { B(-4, -4, 4, 4) };

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        private static (bool ok, FPNavMesh mesh, string message) Try(
            FPNavMesh baseMesh, FPBuildingRect[] buildings, FPBuildingPlacementRules rules = default)
        {
            try { return (true, FPNavMeshRebaker.Rebake(baseMesh, buildings, null, rules), null); }
            catch (Exception e) { return (false, null, e.Message); }
        }

        #endregion

        // ── ⑴ the defect is blocked — synthetic ─────────────────────────────

        [Test]
        public void Annulus_BuildingSwallowingThePillar_IsRejected()
        {
            FPNavMesh baseMesh = BuildAnnulus();
            Assert.IsFalse(Walkable(baseMesh, 0, 0), "the pillar interior is blocked in the base");
            Assert.IsTrue(Walkable(baseMesh, 5, 0), "the annulus around it is walkable");

            var (ok, _, message) = Try(baseMesh, SwallowsPillar());

            Assert.IsFalse(ok, "a building that fully contains the pillar must be rejected");
            StringAssert.Contains("contains a baked hole ring", message,
                "the reason must name the actual problem, not a generic placement error");
            StringAssert.Contains("-2.000", message,
                "the message must carry the swallowed ring's coordinates — on a real stage there "
                + "are ~1700 pillars and 'a hole ring' alone identifies none of them");
        }

        [Test]
        public void Annulus_ControlPlacements_StillAccepted()
        {
            // The check must be about containment, not about "there is a hole somewhere".
            FPNavMesh baseMesh = BuildAnnulus();

            var (ok, mesh, message) = Try(baseMesh, new[] { B(4, -1, 6, 1) });
            Assert.IsTrue(ok, $"a building beside the pillar must still be accepted: {message}");
            Assert.IsFalse(Walkable(mesh, 5, 0), "and it must block");
            Assert.IsFalse(Walkable(mesh, 0, 0), "the pillar stays blocked");
        }

        [Test]
        public void SolidBase_SameBuilding_IsAccepted()
        {
            // Identical rect, hole-free base: accepted. Proves the rejection is driven by the
            // swallowed ring and not by the rect's size or position.
            FPNavMesh solid = BuildSolid();
            var (ok, mesh, message) = Try(solid, SwallowsPillar());

            Assert.IsTrue(ok, $"on a hole-free base the same building must be accepted: {message}");
            Assert.IsFalse(Walkable(mesh, 0, 0), "and its interior is blocked");
        }

        // ── ⑴ the defect is blocked — the shipped Field asset ───────────────

        [Test]
        public void FieldAsset_SwallowingAPillar_IsRejected()
        {
            // The original headline ("this fires on a real stage") was arithmetic from ring
            // statistics, not a measurement. This is the measurement: Field carries 1,678 pillar
            // rings of ~2.17m and a 1.2m building centred on one used to be ACCEPTED with the
            // pillar interior turning WALKABLE. A synthetic fixture cannot stand in for it —
            // whether such a placement survives snapping and neighbour spacing is an asset fact.
            string path = Path.Combine(
                RepoRoot(), "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field.NavMeshData.bytes not present");

            FPNavMesh field = FPNavMeshSerializer.Deserialize(path);
            FPNavMeshObstacleExtractor.Extract(field, out FPVector2[] verts, out int[] offsets);
            Assert.Greater(offsets.Length, 100, "Field is expected to carry many pillar rings");

            // ring 0 is the outer boundary; ring 1 is the first pillar.
            int start = offsets[1], end = offsets[2];
            double minX = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxZ = double.MinValue;
            for (int i = start; i < end; i++)
            {
                double x = verts[i].x.ToDouble(), z = verts[i].y.ToDouble();
                minX = System.Math.Min(minX, x); maxX = System.Math.Max(maxX, x);
                minZ = System.Math.Min(minZ, z); maxZ = System.Math.Max(maxZ, z);
            }
            double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
            Assert.IsFalse(Walkable(field, cx, cz), "the pillar interior is blocked in the base asset");

            var snapshot = FPNavMeshRebaker.CreateSnapshot(field, null, prewarm: false);
            var swallowing = new[] { B(cx - 0.6, cz - 0.6, cx + 0.6, cz + 0.6) };

            var ex = Assert.Catch(() => FPNavMeshRebaker.Rebake(snapshot, swallowing, null));
            StringAssert.Contains("contains a baked hole ring", ex.Message);
        }

        [Test]
        public void FieldAsset_CorridorPlacement_StillAccepted()
        {
            // What the rejection actually costs on Field. The pillar grid leaves ~1.67m corridors
            // between columns, so after the radius expansion footprints up to ~0.64m still fit —
            // measured. Pinned because the cost of this rejection is a design fact someone will
            // want to know, and because it proves the check did not simply close the pillar area.
            string path = Path.Combine(
                RepoRoot(), "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field.NavMeshData.bytes not present");

            FPNavMesh field = FPNavMeshSerializer.Deserialize(path);
            var snapshot = FPNavMeshRebaker.CreateSnapshot(field, null, prewarm: false);

            // centre of the gap between the first two pillar columns
            var corridor = new[] { B(-89.47, -83.22, -88.87, -82.62) };
            Assert.DoesNotThrow(() => FPNavMeshRebaker.Rebake(snapshot, corridor, null),
                "a small building in the corridor between pillars must still be accepted");
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "com.xpturn.klotho")))
                d = d.Parent;
            return d?.FullName ?? ".";
        }

        // ── ⑵ the invariant ─────────────────────────────────────────────────

        [Test]
        public void AcceptedRebake_BlocksEveryPointInsideTheExpandedRects()
        {
            // The invariant this plan is really about:
            //
            //     every point inside an EXPANDED building rect is non-walkable.
            //
            // Not "points outside are unchanged" — the swallowed pillar lies INSIDE the rect, so
            // an outside-only invariant would pass before and after the fix and prove nothing.
            // This one survives a change of strategy too: if the engine ever excludes swallowed
            // rings instead of rejecting them, the placement becomes legal and this must still
            // hold. It pins the promise, not the implementation.
            FPNavMesh baseMesh = BuildAnnulus();
            var buildings = new[] { B(4, -1, 6, 1), B(-6, 3, -4, 5) };
            var (ok, mesh, message) = Try(baseMesh, buildings);
            Assert.IsTrue(ok, message);

            int probed = 0;
            foreach (var b in buildings)
            {
                double x0 = b.MinX.ToDouble() - R, x1 = b.MaxX.ToDouble() + R;
                double z0 = b.MinZ.ToDouble() - R, z1 = b.MaxZ.ToDouble() + R;
                // 0.137 keeps samples off the exact rect edges, where "inside" is not the claim.
                for (double x = x0 + 0.137; x < x1; x += 0.137)
                    for (double z = z0 + 0.137; z < z1; z += 0.137)
                    {
                        Assert.IsFalse(Walkable(mesh, x, z),
                            $"({x:F3},{z:F3}) is inside an expanded building rect and must be blocked");
                        probed++;
                    }
            }
            Assert.Greater(probed, 100, "the sampling must actually cover the rects");
            Assert.IsFalse(Walkable(mesh, 0, 0), "and the pillar is still blocked");
        }

        // ── ⑶ completeness: several buildings cannot conspire ───────────────

        [Test]
        public void DonutOfTouchingBuildings_IsAcceptedAndThePillarStaysBlocked()
        {
            // The check looks at one building at a time. That is not a simplification but a
            // theorem: a ring flips only when an ODD number of rects contain it, and
            // building interiors are disjoint, so at most one can. Here four touching buildings
            // form a closed square ring around the pillar without any of them containing it —
            // the shape that would break a per-building check if the theorem were wrong.
            //
            // Crossing count from inside the pillar: pillar ring (1) + enter/exit one building
            // rect (2) + outer ring (1) = 4, even -> erased. The single-swallow case is
            // 1 + 1 + 1 = 3, odd -> kept. If this test ever fails, the fix is not another check —
            // it is that the theorem is wrong.
            FPNavMesh baseMesh = BuildAnnulus();
            var touching = new FPBuildingPlacementRules(allowBuildingTouch: true);

            // chosen so the EXPANDED rects tile [-5,5]^2 minus the [-3,3]^2 courtyard
            var donut = new[]
            {
                B(-4.5,  3.5, 4.5,  4.5),   // expanded: x[-5,5]  z[3,5]
                B(-4.5, -4.5, 4.5, -3.5),   // expanded: x[-5,5]  z[-5,-3]
                B(-4.5, -2.5, -3.5, 2.5),   // expanded: x[-5,-3] z[-3,3]
                B( 3.5, -2.5,  4.5, 2.5),   // expanded: x[3,5]   z[-3,3]
            };

            var (ok, mesh, message) = Try(baseMesh, donut, touching);
            Assert.IsTrue(ok, $"a ring of touching buildings around a pillar must be accepted: {message}");
            Assert.IsFalse(Walkable(mesh, 0, 0),
                "the pillar must STILL be blocked — four rects contain it zero times, not once");
            Assert.IsTrue(Walkable(mesh, 2.7, 0), "the courtyard between pillar and buildings is walkable");
            Assert.IsFalse(Walkable(mesh, 4, 0), "the buildings themselves block");
        }

        // ── ⑶ rejection priority ────────────────────────────────────────────

        [Test]
        public void BuildingCrossingTheRing_IsRejectedAsCrossing_NotAsSwallowing()
        {
            // Order is load-bearing. This rect has pillar vertices inside it AND cuts across the
            // ring; if the swallow test ran first it would report "contains a hole ring", which is
            // false — the ring is not contained. In a defect class whose only clue is the
            // rejection reason, a false diagnosis is worse than silence.
            FPNavMesh baseMesh = BuildAnnulus();
            var (ok, _, message) = Try(baseMesh, new[] { B(-1, -1, 4, 4) });

            Assert.IsFalse(ok);
            StringAssert.Contains("touches or crosses the walkable boundary", message);
            StringAssert.DoesNotContain("contains a baked hole ring", message,
                "a ring that is cut, not contained, must not be reported as swallowed");
        }

        [Test]
        public void BuildingOutsideWalkable_IsRejectedAsOutside_NotAsSwallowing()
        {
            FPNavMesh baseMesh = BuildAnnulus();
            var (ok, _, message) = Try(baseMesh, new[] { B(20, 20, 21, 21) });

            Assert.IsFalse(ok);
            StringAssert.Contains("outside the walkable region", message);
        }

        // ── ⑸ determinism ───────────────────────────────────────────────────

        [Test]
        public void RejectionAndAcceptance_AreDeterministic()
        {
            FPNavMesh baseMesh = BuildAnnulus();
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            var accepted = new[] { B(4, -1, 6, 1) };

            FPNavMesh a = FPNavMeshRebaker.Rebake(snapshot, accepted, null);
            FPNavMesh b = FPNavMeshRebaker.Rebake(snapshot, accepted, null);
            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(a), FPNavMeshRebaker.ComputeFingerprint(b));

            // The rejection message names the FIRST swallowed ring vertex in ringEdges order;
            // that order is fixed, so the message is a function of the input alone.
            string m1 = Try(baseMesh, SwallowsPillar()).message;
            string m2 = Try(baseMesh, SwallowsPillar()).message;
            Assert.AreEqual(m1, m2, "the same input must produce the same rejection reason");
        }
    }
}
