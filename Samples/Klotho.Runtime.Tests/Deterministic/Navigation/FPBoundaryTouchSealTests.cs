using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Boundary contact as a game choice — the corridor-seal half that
    /// FPBuildingTouchPolicyTests pins as rejected under the DEFAULT policy.
    ///
    /// The old rejection existed because a hole ring sharing an edge with the outer ring carved
    /// nothing (even-odd counted the coincident edge once). The coincident-constraint parity fix
    /// closed that, and the full rebaker path was measured before this feature was built: a flush
    /// building carves exactly its expanded footprint. Plan-BoundaryTouchSeal.md is the record —
    /// including why the discriminator is PROBES + AREA and never the ring count (a flush notch
    /// merges into the outer ring, so rings stays 1 even when fixed).
    ///
    /// Naming note: *_PolicyOpen tests pin behaviour that is a game decision, not an invariant —
    /// they are expected to flip if the game decides the other way.
    /// </summary>
    [TestFixture]
    public class FPBoundaryTouchSealTests
    {
        #region Fixtures

        private static readonly FPBuildingPlacementRules TouchWall =
            new FPBuildingPlacementRules(allowBuildingTouch: false, FPBoundaryPlacementPolicy.Touch);

        private static readonly FPBuildingPlacementRules TouchBoth =
            new FPBuildingPlacementRules(allowBuildingTouch: true, FPBoundaryPlacementPolicy.Touch);

        private static FPNavMesh BuildFromRing((int x, int z)[] ring, (int x, int z)[] hole = null)
        {
            var pts = new List<(int x, int z)>(ring);
            if (hole != null) pts.AddRange(hole);
            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            var cons = new List<int>();
            for (int i = 0; i < ring.Length; i++) { cons.Add(i); cons.Add((i + 1) % ring.Length); }
            if (hole != null)
                for (int i = 0; i < hole.Length; i++)
                {
                    cons.Add(ring.Length + i); cons.Add(ring.Length + (i + 1) % hole.Length);
                }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, cons.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>Solid 16x16 slab, vertices every 2 units, bake radius 0.5 — the exact
        /// FPBuildingTouchPolicyTests fixture, so its measurements carry over.</summary>
        private static FPNavMesh BuildSlab(int halfX = 8, int halfZ = 8)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -halfX; x <= halfX; x += 2)
                for (int z = -halfZ; z <= halfZ; z += 2)
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

        private static FPNavMesh SlabWithPillar() => BuildFromRing(
            new[] { (-8, -8), (8, -8), (8, 8), (-8, 8) },
            new[] { (-2, -2), (2, -2), (2, 2), (-2, 2) });

        /// <summary>Corridor whose walls slant to a pinch: at x = 0 the walkable band is exactly
        /// z in [-1, 1], with boundary VERTICES at (0, ±1). Arbitrary-angle walls (slope 1/8,
        /// gcd of the deltas > 1 is irrelevant here — what matters is the vertex placement).</summary>
        private static FPNavMesh PinchCorridor() => BuildFromRing(
            new[] { (-8, -2), (0, -1), (8, -2), (8, 2), (0, 1), (-8, 2) });

        /// <summary>Region whose boundary contains the diagonal edge (2,8)-(-2,4) — exactly
        /// corner-to-corner of the expanded footprint used in the probe-on-edge test.</summary>
        private static FPNavMesh DiagonalCut() => BuildFromRing(
            new[] { (-8, -8), (8, -8), (8, 8), (2, 8), (-2, 4), (-8, 4) });

        private static FPBuildingRect R(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static (bool accepted, FPNavMesh mesh, string message) Try(
            FPNavMesh baseMesh, FPBuildingRect[] buildings, FPBuildingPlacementRules rules)
        {
            try { return (true, FPNavMeshRebaker.Rebake(baseMesh, buildings, null, rules), null); }
            catch (Exception e) { return (false, null, e.Message); }
        }

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        private static bool PathExists(FPNavMesh mesh, double ax, double az, double bx, double bz)
        {
            var query = new FPNavMeshQuery(mesh, null);
            var pf = new FPNavMeshPathfinder(mesh, query, null);
            return pf.FindPath(
                new FPVector3(FP64.FromDouble(ax), FP64.Zero, FP64.FromDouble(az)),
                new FPVector3(FP64.FromDouble(bx), FP64.Zero, FP64.FromDouble(bz)),
                -1, out _, out _);
        }

        private static BigInteger DoubledArea(FPNavMesh m)
        {
            BigInteger sum = 0;
            foreach (var t in m.Triangles)
            {
                long ax = FPGeoPredicates.Snap(m.Vertices[t.v0].x), az = FPGeoPredicates.Snap(m.Vertices[t.v0].z);
                long bx = FPGeoPredicates.Snap(m.Vertices[t.v1].x), bz = FPGeoPredicates.Snap(m.Vertices[t.v1].z);
                long cx = FPGeoPredicates.Snap(m.Vertices[t.v2].x), cz = FPGeoPredicates.Snap(m.Vertices[t.v2].z);
                sum += BigInteger.Abs(
                    (BigInteger)(bx - ax) * (cz - az) - (BigInteger)(bz - az) * (cx - ax));
            }
            return sum;
        }

        #endregion

        // ── the default must not move ────────────────────────────────────────

        [Test]
        public void Default_FlushAgainstBoundary_StillRejected()
        {
            // The relaxation must be OPT-IN. FPBuildingTouchPolicyTests pins the wider set; this
            // pins the one placement this feature is about.
            var (accepted, _, message) = Try(BuildSlab(),
                new[] { R(-7.5, -0.5, -6.5, 0.5) }, default);
            Assert.IsFalse(accepted, "flush placement must stay rejected under the default policy");
            StringAssert.Contains("touches or crosses", message);
        }

        [Test]
        public void ClipOverlap_FlushPlacement_BitIdenticalToTouchGolden()
        {
            // "Touch is ClipOverlap's degenerate case" pinned to a VALUE, not a sentence: a
            // flush placement has zero transitions, so the clip is the identity and the carve
            // must reproduce the Touch golden bit-for-bit. (This test replaced the reserved-
            // value throw pin when the clip stage shipped — the reservation is over.)
            var rules = new FPBuildingPlacementRules(false, FPBoundaryPlacementPolicy.ClipOverlap);
            var (accepted, mesh, message) = Try(BuildSlab(), new[] { R(-7.5, -0.5, -6.5, 0.5) }, rules);
            Assert.IsTrue(accepted, $"flush must be accepted under ClipOverlap: {message}");
            Assert.AreEqual(0xAFD7ED3E06AAF5B2UL, FPNavMeshRebaker.ComputeFingerprint(mesh),
                "flush under ClipOverlap must be bit-identical to the Touch golden");
        }

        // ── the relaxation works, measured the way the plan demands ─────────

        [Test]
        public void Touch_FlushAgainstBoundary_CarvesExactly()
        {
            // Discriminators are PROBES + AREA, deliberately not the ring count: a flush notch
            // merges into the outer ring, so rings stays 1 whether the carve worked or not
            // (Plan-BoundaryTouchSeal BA — the original draft would have judged a working carve
            // as broken by waiting for ring 2).
            FPNavMesh baseMesh = BuildSlab();
            var (accepted, mesh, message) = Try(baseMesh,
                new[] { R(-7.5, -0.5, -6.5, 0.5) }, TouchWall);
            Assert.IsTrue(accepted, $"flush placement must be accepted under Touch: {message}");

            // All probe points inside the EXPANDED footprint (2x2 at x in [-8,-6]) + the seam.
            Assert.IsFalse(Walkable(mesh, -7.5, 0), "inside the carve, near the wall");
            Assert.IsFalse(Walkable(mesh, -7.0, 0), "centre of the carve");
            Assert.IsFalse(Walkable(mesh, -6.5, 0), "inside the carve, inner half");
            Assert.IsFalse(Walkable(mesh, -8.0, 0), "the shared seam itself");
            Assert.IsTrue(Walkable(mesh, 0, 0), "far interior stays walkable");

            // Area conservation, exact: the carve is the whole expanded footprint, nothing more,
            // nothing less. Catches partial-carve regressions the probes can miss.
            BigInteger carved = DoubledArea(baseMesh) - DoubledArea(mesh);
            Assert.AreEqual((BigInteger)2 * 2048 * 2048, carved,
                "carved doubled-area must equal the expanded 2x2 footprint exactly");
        }

        [Test]
        public void Touch_Flush_Fingerprint_Golden()
        {
            // Golden for the coincident-constraint + split-inheritance path (T-A: the building
            // corners at z = ±1 fall in the INTERIOR of base ring edges with vertices every 2,
            // so SplitOnEdge mark inheritance runs). The pre-existing 10 goldens are all
            // non-overlapping and never exercise it; losing this one silently disarms the only
            // full-pipeline regression guard for that path.
            var (accepted, mesh, _) = Try(BuildSlab(),
                new[] { R(-7.5, -0.5, -6.5, 0.5) }, TouchWall);
            Assert.IsTrue(accepted);
            Assert.AreEqual(0xAFD7ED3E06AAF5B2UL, FPNavMeshRebaker.ComputeFingerprint(mesh),
                "flush-carve fingerprint moved — bit-exact output of the coincident-constraint "
                + "path changed; if intended, re-pin and say why in the commit");
        }

        // ── what stays rejected with the policy ON ──────────────────────────

        [Test]
        public void Touch_TransversalCrossing_StillRejected()
        {
            var (accepted, _, message) = Try(BuildSlab(),
                new[] { R(-9, -0.5, -6, 0.5) }, TouchWall);
            Assert.IsFalse(accepted, "a footprint crossing the boundary must stay rejected");
            StringAssert.Contains("touches or crosses", message);
        }

        [Test]
        public void Touch_OutsideTouchingFromOutside_Rejected()
        {
            // The failure mode that killed the naive vertex probe: a building entirely OFF the
            // mesh whose side is collinear with the wall. Contact is allowed now, so only the
            // interior probe stands between this and "accepted while carving nothing".
            var (accepted, _, message) = Try(BuildSlab(),
                new[] { R(-9.5, -0.5, -8.5, 0.5) }, TouchWall);
            Assert.IsFalse(accepted, "outside placement touching the wall from outside must be rejected");
            StringAssert.Contains("outside the walkable region", message);
        }

        [Test]
        public void Touch_SwallowingPillar_StillRejected()
        {
            // AllowBoundary contact must not open the swallow trap: even-odd would flip the
            // pillar's interior back to walkable.
            var (accepted, _, message) = Try(SlabWithPillar(),
                new[] { R(-3, -3, 3, 3) }, TouchWall);
            Assert.IsFalse(accepted);
            StringAssert.Contains("fully contains a baked hole ring", message);
        }

        [Test]
        public void Touch_ProbeOnDiagonalChord_RejectedDefined()
        {
            // A boundary chord running corner-to-corner of a rect is a diagonal, and every
            // diagonal contains the centroid — parity is undefined there, so the placement is
            // rejected with its own reason instead of answered. This is also why "accepted while
            // partly outside" is unreachable for rects: this shape is the only candidate.
            var (accepted, _, message) = Try(DiagonalCut(),
                new[] { R(-1.5, 4.5, 1.5, 7.5) }, TouchWall);   // expanded [-2,2]x[4,8]
            Assert.IsFalse(accepted, "centroid on a boundary ring edge must be a defined rejection");
            StringAssert.Contains("probe lies on a boundary ring edge", message);
        }

        // ── sealing — the point of the feature ───────────────────────────────

        [Test]
        public void Touch_CorridorSeal_SplitsGraph_AndDemolitionRestores()
        {
            // Small fixture on purpose: A* stops at MAX_ITERATIONS and returns failure, so on a
            // big mesh "sealed" and "ran out of budget" are the same value (Plan DI).
            FPNavMesh corridor = BuildSlab(8, 2);
            ulong emptyBefore = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(corridor, new FPBuildingRect[0], null, TouchWall));

            var (accepted, sealedMesh, message) = Try(corridor,
                new[] { R(-1, -1.5, 0, 1.5) }, TouchWall);      // expanded [-1.5,0.5]x[-2,2]
            Assert.IsTrue(accepted, $"seal placement must be accepted: {message}");

            // CB's three pieces — path failure alone is not evidence of a seal.
            Assert.IsTrue(Walkable(sealedMesh, -6, 0), "west endpoint on-mesh");
            Assert.IsTrue(Walkable(sealedMesh, 6, 0), "east endpoint on-mesh");
            Assert.IsFalse(PathExists(sealedMesh, -6, 0, 6, 0), "no path across the seal");
            Assert.IsTrue(PathExists(sealedMesh, -6, 0, -4, 0), "control: same side still routes");

            // The seal is visible to the cross-peer fingerprint (CG), and demolition restores the
            // exact pre-seal mesh (CE: B' semantics — base + full list, never incremental).
            Assert.AreNotEqual(emptyBefore, FPNavMeshRebaker.ComputeFingerprint(sealedMesh));
            FPNavMesh demolished = FPNavMeshRebaker.Rebake(
                corridor, new FPBuildingRect[0], null, TouchWall);
            Assert.AreEqual(emptyBefore, FPNavMeshRebaker.ComputeFingerprint(demolished),
                "removing the sealing building must restore the empty-rebake mesh bit-exactly");
            Assert.IsTrue(PathExists(demolished, -6, 0, 6, 0));
        }

        [Test]
        public void Touch_OneSnapGap_PathSurvives_PolicyOpen()
        {
            // The claim three sections of the plan lean on (CC): a 1-snap strip IS a corridor.
            // The engine cannot tell a 1 mm gap from a doorway; closing it is a game rule. If a
            // minimum-width rule ever ships, THIS is the test that should flip.
            double snap = 1.0 / 1024.0;
            var (accepted, mesh, message) = Try(BuildSlab(8, 2),
                new[] { R(-1, -1.5 + snap, 0, 1.5) }, TouchWall); // flush top, 1-snap gap bottom
            Assert.IsTrue(accepted, $"one-snap-inside placement must be accepted: {message}");
            Assert.IsTrue(PathExists(mesh, -6, 0, 6, 0),
                "agents route through a 1-snap gap — sub-snap sealing is not a thing the engine promises");
        }

        [Test]
        public void Touch_ChainMiddleBuilding_InteriorStaysBlocked()
        {
            // The corridor-seal chain's middle building has ALL four sides shared (walls above and
            // below, neighbours left and right) — every side at parity 0. This is the boundary
            // variant of the enclosed-building defect family (Report-EnclosedBuildingTurnsWalkable):
            // every one of those was "accepted but the navmesh is wrong", and all looked fine on
            // paper. Pin it.
            var (accepted, mesh, message) = Try(BuildSlab(8, 2), new[]
            {
                R(-3, -1.5, -2, 1.5),   // expanded [-3.5,-1.5]
                R(-1, -1.5, 0, 1.5),    // expanded [-1.5, 0.5] — the fully-shared middle
                R(1, -1.5, 2, 1.5),     // expanded [ 0.5, 2.5]
            }, TouchBoth);
            Assert.IsTrue(accepted, $"chain must be accepted: {message}");
            Assert.IsFalse(Walkable(mesh, -0.5, 0), "middle building's interior must stay blocked");
            Assert.IsFalse(PathExists(mesh, -6, 0, 6, 0), "the chain seals");
        }

        [Test]
        public void Touch_PillarContact_MergesAndBothStayBlocked()
        {
            // ringEdges holds outer AND hole rings without distinction, so the policy opens
            // pillar-flush building too — and Field ships 1,678 hole rings, so this is a common
            // case, not a corner. The pillar must not read as swallowed (strict interior test),
            // and both interiors stay blocked.
            var (accepted, mesh, message) = Try(SlabWithPillar(),
                new[] { R(2.5, -0.5, 3.5, 0.5) }, TouchWall);   // expanded [2,4]x[-1,1], flush to pillar
            Assert.IsTrue(accepted, $"pillar-flush placement must be accepted: {message}");
            Assert.IsFalse(Walkable(mesh, 3, 0), "building interior blocked");
            Assert.IsFalse(Walkable(mesh, 0, 0), "pillar interior STAYS blocked — not misread as swallowed");
            Assert.IsFalse(Walkable(mesh, 2, 0), "the shared seam blocked");
            Assert.IsTrue(Walkable(mesh, 6, 0), "open floor unaffected");
        }

        [Test]
        public void Touch_PointContactPinch_SealsGraph()
        {
            // Arbitrary-angle walls: there are NO lattice points in the interior of a gcd-1 edge,
            // so flush contact is impossible there — the only always-available contact is a
            // corner ON a boundary VERTEX. Sealing still works because navmesh adjacency is
            // edge-based: triangles sharing only a vertex are not neighbours, so a pinch cuts the
            // graph. Half of this feature's usefulness on shipped assets rides on that property.
            var (accepted, mesh, message) = Try(PinchCorridor(),
                new[] { R(-1, -0.5, -0.5, 0.5) }, TouchWall);   // expanded [-1.5,0]x[-1,1] — corners AT (0,±1)
            Assert.IsTrue(accepted, $"point-contact placement must be accepted: {message}");
            Assert.IsTrue(Walkable(mesh, -5, 0), "west side on-mesh");
            Assert.IsTrue(Walkable(mesh, 5, 0), "east side on-mesh");
            Assert.IsFalse(PathExists(mesh, -5, 0, 5, 0), "vertex-only contact must cut the graph");
            Assert.IsTrue(PathExists(mesh, -5, 0, -7, 0), "control: same side still routes");
        }

        // ── trapped agents: what sealing means at the agent layer ────────────

        [Test]
        public void Touch_Seal_TrappedAgents_FailPathsCorrectly()
        {
            // Sealing creates two kinds of "stuck", and they must stay distinct:
            //   (a) an agent UNDER the building — off the new mesh. Existing contract
            //       (FPNavAgentOffMeshFreezeTests): frozen for good, PathFailed at reseed.
            //   (b) an agent on the SEALED-OFF side — on-mesh, destination unreachable.
            //       NEW with this feature: reseed puts it PathPending with the cooldown
            //       bypassed, and the next Update's A* fails it to PathFailed.
            // (b) turns PathFailed from "effectively an error" into a NORMAL gameplay signal
            // (RTS convention: attack the blocking building) — the game seam must handle it,
            // and this test is the engine-side half of that contract.
            FPNavMesh corridor = BuildSlab(8, 2);
            var (querySrc, systemSrc) = (new FPNavMeshQuery(corridor, null), (FPNavAgentSystem)null);
            var pathfinder = new FPNavMeshPathfinder(corridor, querySrc, null);
            var funnel = new FPNavMeshFunnel(corridor, querySrc, null);
            systemSrc = new FPNavAgentSystem(corridor, querySrc, pathfinder, funnel, null);
            systemSrc.SetAvoidance(new FPNavAvoidance());
            systemSrc.LoadNavMeshObstacles();

            var posA = new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.Zero);       // (b) east side
            var posB = new FPVector3(FP64.FromDouble(-0.5), FP64.Zero, FP64.Zero); // (a) under the building
            var posC = new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero);       // control, east side
            Frame frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { posA, posB, posC },
                new[]
                {
                    querySrc.FindTriangle(posA.ToXZ(), posA.y),
                    querySrc.FindTriangle(posB.ToXZ(), posB.y),
                    querySrc.FindTriangle(posC.ToXZ(), posC.y),
                },
                out EntityRef[] entities);

            var west = new FPVector3(FP64.FromInt(-6), FP64.Zero, FP64.Zero);
            var eastNear = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero);
            {
                ref var a = ref frame.Get<NavAgentComponent>(entities[0]);
                NavAgentComponent.SetDestination(ref a, west);
                ref var b2 = ref frame.Get<NavAgentComponent>(entities[1]);
                NavAgentComponent.SetDestination(ref b2, west);
                ref var c = ref frame.Get<NavAgentComponent>(entities[2]);
                NavAgentComponent.SetDestination(ref c, eastNear);
            }
            systemSrc.Update(ref frame, entities, 3, 1, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving,
                frame.Get<NavAgentComponent>(entities[0]).Status, "pre-seal: A routes west");
            Assert.AreEqual((byte)FPNavAgentStatus.Moving,
                frame.Get<NavAgentComponent>(entities[1]).Status, "pre-seal: B routes west");

            // Seal the corridor and swap it in the way the quick start prescribes.
            FPNavMesh sealedMesh = FPNavMeshRebaker.Rebake(
                corridor, new[] { R(-1, -1.5, 0, 1.5) }, null, TouchWall);
            var sealedQuery = new FPNavMeshQuery(sealedMesh, null);
            systemSrc.SwapNavMesh(sealedMesh, sealedQuery,
                new FPNavMeshPathfinder(sealedMesh, sealedQuery, null),
                new FPNavMeshFunnel(sealedMesh, sealedQuery, null));
            unsafe { systemSrc.ReseedAgents(ref frame, entities, 3); }

            // Precondition for (b)'s claim: the DESTINATION is on-mesh too — the coming failure
            // is "unreachable", not "off-mesh endpoint".
            Assert.GreaterOrEqual(sealedQuery.FindTriangle(west.ToXZ(), west.y), 0);

            // (a) under the building: off the new mesh, failed at reseed, frozen (contract held
            // by FPNavAgentOffMeshFreezeTests — re-pinned here in the seal scenario).
            {
                ref var b2 = ref frame.Get<NavAgentComponent>(entities[1]);
                Assert.Less(b2.CurrentTriangleIndex, 0, "(a) is off the new mesh");
                Assert.AreEqual((byte)FPNavAgentStatus.PathFailed, b2.Status,
                    "(a) fails at reseed, not on the next Update");
            }
            // (b) sealed-off side: still ON the mesh, queued for an immediate repath.
            {
                ref var a = ref frame.Get<NavAgentComponent>(entities[0]);
                Assert.GreaterOrEqual(a.CurrentTriangleIndex, 0, "(b) is on the new mesh");
                Assert.AreEqual((byte)FPNavAgentStatus.PathPending, a.Status,
                    "(b) is re-queued by the reseed");
                Assert.AreEqual(0, a.LastRepathTick, "cooldown bypassed: repath on the NEXT update");
            }

            systemSrc.Update(ref frame, entities, 3, 2, NavAgentTestHelper.DT);

            // (b): the destination is across the seal — A* fails, and that is now a normal
            // gameplay signal, not an error. No retry loop: repath runs on PathPending only,
            // so a failed agent stays PathFailed instead of burning a failed A* every tick.
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed,
                frame.Get<NavAgentComponent>(entities[0]).Status,
                "(b) sealed-off agent must fail its path on the first post-swap update");
            // Control: an agent whose destination is on its OWN side repaths fine — the seal
            // failed exactly the unreachable route, not the system.
            Assert.AreEqual((byte)FPNavAgentStatus.Moving,
                frame.Get<NavAgentComponent>(entities[2]).Status,
                "control agent on the same side must keep routing");

            // No retry loop (DD's confirmed half): another update must not flip (b) back.
            systemSrc.Update(ref frame, entities, 3, 3, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed,
                frame.Get<NavAgentComponent>(entities[0]).Status);
        }

        [Test]
        public void Touch_WholeRegionFlush_RejectedAsEmpty()
        {
            // Touch made EmptyWalkableRegion REACHABLE: a footprint flush on every side covers
            // the whole walkable region — no contact rejects it, nothing is swallowed (ring
            // vertices sit ON the footprint boundary, not strictly inside), the probe is inside.
            // The defensive "nothing walkable was left" refusal is the only thing standing
            // between that placement and shipping an empty mesh. Its doc used to claim "no
            // placement can cause this" — measured otherwise, so it is pinned here.
            var (accepted, _, message) = Try(BuildSlab(),
                new[] { R(-7.5, -7.5, 7.5, 7.5) }, TouchWall);   // expanded = the whole 16x16 slab
            Assert.IsFalse(accepted, "covering the entire walkable region must be refused");
            StringAssert.Contains("empty walkable region", message);
        }

        // ── determinism ──────────────────────────────────────────────────────

        [Test]
        public void Touch_SameInput_BitIdenticalAcrossContexts()
        {
            FPNavMesh corridor = BuildSlab(8, 2);
            var b = new[] { R(-1, -1.5, 0, 1.5) };
            ulong f1 = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(corridor, b, null, TouchWall));
            ulong f2 = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(corridor, b, null, TouchWall));
            Assert.AreEqual(f1, f2);
        }
    }
}
