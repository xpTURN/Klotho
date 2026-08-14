using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Touching placements as a game choice.
    ///
    /// The rebaker used to reject any building whose EXPANDED rect grazed the walkable boundary
    /// or another building. For a BUILDING PAIR that was never a CDT requirement (the triangulator
    /// only refuses a transversal crossing) nor a geometric one (the expansion already encodes "an
    /// agent cannot fit"), so it is a game rule, and FPBuildingPlacementRules is where the game
    /// states it.
    ///
    /// Relaxing BOUNDARY contact was dropped after measurement, and these tests pin why: a
    /// hole ring that shares an edge with the outer ring is not a hole. Even-odd erasure counts
    /// the coincident edge once, so the notch keeps odd depth and stays WALKABLE — the building is
    /// accepted and carves nothing. Silently ineffective is worse than rejected, so flush placement
    /// stays rejected in every mode.
    ///
    /// The default stays the old behaviour — these tests exist as much to pin THAT as to pin the
    /// relaxation, because a policy that leaks into the default is the failure this design is
    /// arranged to avoid.
    /// </summary>
    [TestFixture]
    public class FPBuildingTouchPolicyTests
    {
        #region Fixture

        private static readonly FPBuildingPlacementRules Strict = default;
        private static readonly FPBuildingPlacementRules TouchOk = new FPBuildingPlacementRules(allowBuildingTouch: true);

        /// <summary>Solid 16x16 slab, vertices every 2 units, bake radius 0.5.</summary>
        private static FPNavMesh BuildBase()
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>Rect given in ORIGINAL units; the rebaker expands it by the bake radius.</summary>
        private static FPBuildingRect R(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        /// <summary>
        /// Two 1x1 buildings whose EXPANDED rects share an edge exactly: radius 0.5 grows each by
        /// 0.5 per side, so footprints 1 apart end up flush.
        /// </summary>
        private static FPBuildingRect[] EdgeSharingPair() =>
            new[] { R(-2, -0.5, -1, 0.5), R(0, -0.5, 1, 0.5) };

        private static FPBuildingRect[] NestedPair() =>
            new[] { R(-3, -3, 3, 3), R(-1, -1, 1, 1) };

        private static FPBuildingRect[] PartialOverlapPair() =>
            new[] { R(-2, -2, 1, 1), R(-1, -1, 2, 2) };

        private static (bool accepted, FPNavMesh mesh) Try(
            FPNavMesh baseMesh, FPBuildingRect[] buildings, FPBuildingPlacementRules rules)
        {
            try { return (true, FPNavMeshRebaker.Rebake(baseMesh, buildings, null, rules)); }
            catch (Exception) { return (false, null); }
        }

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        #endregion

        // ── the default must not move ────────────────────────────────────────

        [Test]
        public void Default_RejectsTouching_ExactlyAsBefore()
        {
            // Claim 1 of "this adds a choice, it does not change policy": the accept/reject set
            // is unchanged when no rules are passed.
            FPNavMesh baseMesh = BuildBase();

            Assert.IsFalse(Try(baseMesh, EdgeSharingPair(), Strict).accepted,
                "default must still reject buildings that touch after expansion");
            Assert.IsFalse(Try(baseMesh, new[] { R(-7.5, -0.5, -6.5, 0.5) }, Strict).accepted,
                "a building flush against the boundary stays rejected in every mode — see the "
                + "comment at the boundary check: the notch would not be carved at all");
        }

        [Test]
        public void Default_AcceptedPlacements_AreBitIdentical()
        {
            // Claim 2: for placements that were already legal, passing the (default) rules must
            // not perturb the result at all — the relaxation must not touch the accepting path.
            FPNavMesh baseMesh = BuildBase();
            var apart = new[] { R(-4, -0.5, -3, 0.5), R(3, -0.5, 4, 0.5) };

            FPNavMesh a = FPNavMeshRebaker.Rebake(baseMesh, apart);
            FPNavMesh b = FPNavMeshRebaker.Rebake(baseMesh, apart, null, Strict);

            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(a), FPNavMeshRebaker.ComputeFingerprint(b),
                "the default path must be bit-identical to the no-rules path");
        }

        // ── what the relaxation buys ─────────────────────────────────────────

        [Test]
        public void TouchAllowed_EdgeSharingPair_IsAcceptedAndActuallyBlocks()
        {
            // "No exception" and "the hole is carved" are different claims; the second is the one
            // that matters. Both interiors AND the shared edge must be blocked — if the shared
            // edge were left walkable the two buildings would have a seam an agent could thread.
            FPNavMesh baseMesh = BuildBase();
            var (accepted, mesh) = Try(baseMesh, EdgeSharingPair(), TouchOk);

            Assert.IsTrue(accepted, "touching pair must be accepted once the game allows it");
            Assert.IsFalse(Walkable(mesh, -1.5, 0), "first building's interior must be blocked");
            Assert.IsFalse(Walkable(mesh, 0.5, 0), "second building's interior must be blocked");
            Assert.IsFalse(Walkable(mesh, -0.5, 0), "the shared edge must be blocked, not a seam");
            Assert.IsTrue(Walkable(mesh, 5, 0), "ground away from the buildings stays walkable");
        }

        // ── what must stay rejected ──────────────────────────────────────────

        [Test]
        public void TouchAllowed_NestedPair_IsStillRejected()
        {
            // Nesting is not a near miss. Even-odd erasure counts one more ring crossing, so the
            // inner rect flips back to WALKABLE — a building with a courtyard. The CDT accepts it
            // without complaint, so this rejection is the only thing standing in the way, and it
            // must survive the relaxation being switched ON.
            FPNavMesh baseMesh = BuildBase();
            Assert.IsFalse(Try(baseMesh, NestedPair(), TouchOk).accepted,
                "nesting must stay rejected even with touching allowed — interiors overlap");
        }

        [Test]
        public void TouchAllowed_PartialOverlap_IsStillRejectedByValidation()
        {
            // Transversal overlap is the one thing the CDT genuinely cannot take. It must be
            // refused by the VALIDATION, not by the triangulator: a T1 NotAllowed reaching the
            // game reads as "a constraint crosses an existing constraint", which tells a player
            // nothing about where they tried to build.
            FPNavMesh baseMesh = BuildBase();
            var ex = Assert.Catch(() => FPNavMeshRebaker.Rebake(baseMesh, PartialOverlapPair(), null, TouchOk));
            StringAssert.Contains("FPNavMeshRebaker", ex.Message,
                "the rejection must come from placement validation, not from the CDT");
        }

        [Test]
        public void TouchAllowed_CrossingTheBoundary_IsStillRejectedByValidation()
        {
            FPNavMesh baseMesh = BuildBase();
            var ex = Assert.Catch(() => FPNavMeshRebaker.Rebake(baseMesh, new[] { R(-9, -0.5, -7, 0.5) }, null, TouchOk));
            StringAssert.Contains("FPNavMeshRebaker", ex.Message,
                "crossing the boundary must be refused by validation, not by the CDT");
        }

        [Test]
        public void TouchAllowed_OutsideWalkable_IsStillRejected()
        {
            FPNavMesh baseMesh = BuildBase();
            Assert.IsFalse(Try(baseMesh, new[] { R(20, 20, 21, 21) }, TouchOk).accepted,
                "a building outside the walkable region must stay rejected");
        }

        // ── determinism ──────────────────────────────────────────────────────

        [Test]
        public void SameRules_ProduceBitIdenticalMeshes()
        {
            FPNavMesh baseMesh = BuildBase();
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);

            FPNavMesh a = FPNavMeshRebaker.Rebake(snapshot, EdgeSharingPair(), null, TouchOk);
            FPNavMesh b = FPNavMeshRebaker.Rebake(snapshot, EdgeSharingPair(), null, TouchOk);

            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(a), FPNavMeshRebaker.ComputeFingerprint(b));
            Assert.AreEqual(a.TriangleCount, b.TriangleCount);
            for (int t = 0; t < a.TriangleCount; t++)
            {
                Assert.AreEqual(a.Triangles[t].v0, b.Triangles[t].v0, $"tri {t}.v0");
                Assert.AreEqual(a.Triangles[t].neighbor0, b.Triangles[t].neighbor0, $"tri {t}.n0");
            }
        }

        [Test]
        public void DifferentRules_ChangeTheAcceptedSet()
        {
            // This is why the value has to live inside the determinism envelope: it is not a
            // rendering preference, it decides whether a BuildingComponent gets created at all.
            // Peers that disagree diverge in state, not merely in navmesh.
            FPNavMesh baseMesh = BuildBase();
            Assert.IsFalse(Try(baseMesh, EdgeSharingPair(), Strict).accepted);
            Assert.IsTrue(Try(baseMesh, EdgeSharingPair(), TouchOk).accepted);
        }

        // ── obstacle topology, pinned ────────────────────────────────────────

        [Test]
        public void TouchingHoles_MergeIntoOneObstacleRing()
        {
            // Touching placements produce a boundary shape the extractor had never seen, because
            // validation used to forbid it: two holes sharing an edge. Measured before
            // implementing — the extractor merges them into ONE ring. Pinned because ORCA
            // correctness rides on it and a regression here would be silent: every peer would
            // avoid the same phantom wall, so no desync check would ever fire.
            FPNavMesh baseMesh = BuildBase();

            var (okDisjoint, disjoint) = Try(
                baseMesh, new[] { R(-4, -0.5, -3, 0.5), R(3, -0.5, 4, 0.5) }, TouchOk);
            Assert.IsTrue(okDisjoint);
            FPNavMeshObstacleExtractor.Extract(disjoint, out _, out int[] disjointRings);
            Assert.AreEqual(3, disjointRings.Length, "outer ring + two separate holes");

            var (okShared, shared) = Try(baseMesh, EdgeSharingPair(), TouchOk);
            Assert.IsTrue(okShared);
            FPNavMeshObstacleExtractor.Extract(shared, out _, out int[] sharedRings);
            Assert.AreEqual(2, sharedRings.Length,
                "an edge-sharing pair is ONE merged obstacle ring, not two");

            Assert.IsFalse(Try(baseMesh, new[] { R(-7.5, -0.5, -6.5, 0.5) }, TouchOk).accepted,
                "flush-against-boundary is rejected even with touching allowed — the notch would "
                + "not be carved (even-odd counts the coincident edge once)");
        }
    }
}
