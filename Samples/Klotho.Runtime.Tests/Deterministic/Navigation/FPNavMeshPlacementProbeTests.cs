using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// <see cref="FPNavMeshPlacementProbe"/> — the data layer behind the NavMesh visualizer's
    /// placement tool.
    ///
    /// <para><b>Why these run here and not in Unity.</b> The visualizer's own types are internal to
    /// an editor assembly no test assembly references, so logic left there could not be gated at
    /// all. The probe sits in the runtime for exactly this reason, and these tests need no
    /// editor.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshPlacementProbeTests
    {
        #region State survives a refusal — the list is always a set that bakes

        /// <summary>
        /// A removal that the rebake refuses must leave the list as it was.
        ///
        /// <para>The old note said a removal "cannot be refused — the remaining set is a subset of
        /// one that was already accepted". That holds only while <c>Rules</c> is fixed, and
        /// <c>Rules</c> is a public setter both editors expose. Here A overhangs the boundary: it
        /// is accepted under <c>ClipOverlap</c> and refused under <c>Reject</c>. Accept the pair,
        /// tighten the policy, then remove B — the surviving subset {A} is judged again under the
        /// NEW rules and loses.</para>
        ///
        /// <para>Without the undo the probe would hold {} while the scene still shows A, and the
        /// next rebake that succeeded would ship a mesh missing a building nobody removed.</para>
        /// </summary>
        [Test]
        public void TryRemoveAt_WhenTheRebakeRefuses_LeavesTheListUntouched()
        {
            const double OverhangX = 1.5, OverhangZ = 8;   // clipped by Reject, accepted by ClipOverlap

            var probe = Probe();
            probe.Rules = new FPBuildingPlacementRules(
                allowBuildingTouch: false, boundaryPolicy: FPBoundaryPlacementPolicy.ClipOverlap);

            Assert.IsTrue(Place(probe, OverhangX, OverhangZ, retain: false, out _, out _),
                "fixture: the overhanging building is accepted under ClipOverlap");
            Assert.IsTrue(Place(probe, Bx, Bz, retain: false, out _, out _),
                "fixture: the interior building is accepted too");
            Assert.AreEqual(2, probe.Count);

            probe.Rules = new FPBuildingPlacementRules(
                allowBuildingTouch: false, boundaryPolicy: FPBoundaryPlacementPolicy.Reject);

            Assert.IsFalse(probe.TryRemoveAt(1, out FPNavMesh mesh, out FPBuildingRejectionInfo rejection),
                "fixture: the surviving subset must lose under the tightened policy, or this gate "
                + "is testing nothing");
            Assert.AreEqual(FPBuildingRejection.TouchesWalkableBoundary, rejection.Reason);
            Assert.IsNull(mesh);

            Assert.AreEqual(2, probe.Count,
                "a refused removal must put the placement back — otherwise the list no longer holds "
                + "a building the scene still shows");
        }

        /// <summary>
        /// An unsupported base is refused as a VALUE from every entry point, not thrown, and the
        /// reason names the stage rather than a building. Returning <c>false</c> with
        /// <c>None</c> would make <c>IsRejected</c> answer "no" for a call that just failed.
        /// </summary>
        [Test]
        public void UnsupportedBase_IsRefusedAsAValue_NamingTheStage()
        {
            var probe = new FPNavMeshPlacementProbe(
                NavAgentTestHelper.CreateStackedFloorsNavMesh(Cells, floorGap: 5.0),
                NavAgentTestHelper.SquareBuildingCatalog);

            Assert.IsFalse(probe.TryPrepare(out string why));
            Assert.IsNotNull(why, "the refusal must carry a message");

            Assert.IsFalse(
                probe.TryPlace(0, 0, FP64.FromDouble(Mx), FP64.FromDouble(Mz), FP64.Zero, false,
                    out _, out FPBuildingRejectionInfo rejection),
                "a placement on an unsupported base is refused, not thrown");
            Assert.AreEqual(FPBuildingRejection.BaseMeshUnsupported, rejection.Reason);
            Assert.IsTrue(rejection.IsRejected,
                "a call that returned false must not report itself as unrejected");
            Assert.AreEqual(-1, rejection.IndexA, "no building is at fault — the stage is");
        }

        /// <summary>
        /// The refusal is remembered. Building the snapshot is the expensive part of this class, and
        /// a base that failed once fails identically every time — re-running it per click was the
        /// cost that made an unsupported stage unusable rather than merely gated.
        /// </summary>
        [Test]
        public void UnsupportedBase_IsOnlyAttemptedOnce()
        {
            var probe = new FPNavMeshPlacementProbe(
                NavAgentTestHelper.CreateStackedFloorsNavMesh(Cells, floorGap: 5.0),
                NavAgentTestHelper.SquareBuildingCatalog);

            Assert.IsFalse(probe.TryPrepare(out string first));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
                Assert.IsFalse(probe.TryPrepare(out string again),
                    "a remembered refusal stays a refusal");
            watch.Stop();

            Assert.Less(watch.ElapsedMilliseconds, 50,
                "50 repeats of a cached answer must not cost 50 snapshot builds — the first attempt "
                + $"alone is measured in tens of ms (reason: {first})");
        }

        [Test]
        public void TryRemoveAt_OutOfRange_Throws()
        {
            var probe = Probe();
            Assert.IsTrue(Place(probe, Ax, Az, retain: false, out _, out _));

            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => probe.TryRemoveAt(1, out _, out _), "index == Count");
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => probe.TryRemoveAt(-1, out _, out _), "negative index");
            Assert.AreEqual(1, probe.Count, "a refused call must not have changed the list");
        }

        #endregion

        #region Fixture

        private const int Cells = 8;    // 8 x 8 cells of 2 units

        // The field spans 0..16 in world units, NOT -8..8 — CreateOpenFieldNavMesh lays cells out
        // from the origin. Two well-separated interior spots, and the centre.
        private const double Ax = 4, Az = 8;
        private const double Bx = 12, Bz = 8;
        private const double Mx = 8, Mz = 8;

        private static FPNavMesh Field() => NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);

        private static FPNavMeshPlacementProbe Probe(FPNavMesh baseMesh = null) =>
            new FPNavMeshPlacementProbe(
                baseMesh ?? Field(), NavAgentTestHelper.SquareBuildingCatalog);

        /// <summary>Places the tool's one box shape, unrotated, at (x, z).</summary>
        private static bool Place(
            FPNavMeshPlacementProbe probe, double x, double z, bool retain,
            out FPNavMesh mesh, out FPBuildingRejectionInfo rejection) =>
            probe.TryPlace(0, 0, FP64.FromDouble(x), FP64.FromDouble(z), FP64.Zero, retain,
                out mesh, out rejection);

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        private static int TriangleAt(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z)));

        #endregion

        // ── V-1 ─────────────────────────────────────────────────────────────

        [Test]
        public void V1_RetainStaysStampedGround_AndCarveBecomesAHole()
        {
            var probe = Probe();

            Assert.IsTrue(Place(probe, Ax, Az, retain: true, out FPNavMesh mesh, out _),
                "the retain placement was refused");
            int retained = TriangleAt(mesh, Ax, Az);
            Assert.GreaterOrEqual(retained, 0, "retain: the footprint must stay ground");
            Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[retained].areaMask,
                "retain: the footprint must be stamped — if the probe dropped the flag on the way "
                + "to the rebaker this is where it shows");

            Assert.IsTrue(Place(probe, Bx, Bz, retain: false, out mesh, out _),
                "the carve placement was refused");
            Assert.IsFalse(Walkable(mesh, Bx, Bz), "carve: the footprint must be a hole");

            // Both are in the list and both kept their own mode.
            Assert.AreEqual(2, probe.Count);
            Assert.IsTrue(probe.PlacementAt(0).Retain);
            Assert.IsFalse(probe.PlacementAt(1).Retain);
        }

        // ── V-2 ─────────────────────────────────────────────────────────────

        [Test]
        public void V2_ListOrderIsPreserved_AndTheOrderMatters()
        {
            // The teeth are NOT "the rebaker is deterministic" — it is, so a same-order comparison
            // passes whatever the probe does with its storage. What can go wrong is the storage
            // reordering (a Dictionary or a HashSet would), so the gate has to show that order is
            // observable in the first place.
            ulong ab = BakeInOrder((Ax, Az), (Bx, Bz));
            ulong ba = BakeInOrder((Bx, Bz), (Ax, Az));
            Assert.AreNotEqual(ab, ba,
                "placement order does not reach the output at all, so this fixture cannot tell an "
                + "order-preserving list from a set. Re-derive the gate before trusting it: the "
                + "rebaker appends each footprint's vertices in list order, which is what is "
                + "supposed to make the two differ");

            Assert.AreEqual(ab, BakeInOrder((Ax, Az), (Bx, Bz)), "the same order produced a different mesh");
            Assert.AreEqual(ba, BakeInOrder((Bx, Bz), (Ax, Az)), "the same order produced a different mesh");
        }

        private static ulong BakeInOrder(params (double x, double z)[] centres)
        {
            var probe = Probe();
            FPNavMesh mesh = probe.BaseMesh;
            foreach (var c in centres)
            {
                Assert.IsTrue(Place(probe, c.x, c.z, retain: true, out mesh, out var rejection),
                    $"fixture: ({c.x}, {c.z}) was refused as {rejection.Reason}");
            }
            return FPNavMeshRebaker.ComputeFingerprint(mesh);
        }

        // ── V-5 ─────────────────────────────────────────────────────────────

        [Test]
        public void V5_RevertHandsBackTheBaseMesh_AndRemovingOneLeavesTheOther()
        {
            var probe = Probe();
            ulong baseline = FPNavMeshRebaker.ComputeFingerprint(probe.BaseMesh);

            Assert.IsTrue(Place(probe, Ax, Az, retain: true, out _, out _));
            Assert.IsTrue(Place(probe, Bx, Bz, retain: false, out _, out _));

            // Removing the first leaves exactly the set that "only the second" would have baked.
            Assert.IsTrue(probe.TryRemoveAt(0, out FPNavMesh afterRemove, out _));
            Assert.AreEqual(1, probe.Count);

            var only = Probe();
            Assert.IsTrue(Place(only, Bx, Bz, retain: false, out FPNavMesh onlyMesh, out _));
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(onlyMesh),
                FPNavMeshRebaker.ComputeFingerprint(afterRemove),
                "a removal must leave the mesh the remaining set alone would produce");

            // Revert is the base mesh itself, not a zero-placement rebake.
            FPNavMesh reverted = probe.Revert();
            Assert.AreEqual(0, probe.Count);
            Assert.AreSame(probe.BaseMesh, reverted, "revert must hand back the base instance");
            Assert.AreEqual(baseline, FPNavMeshRebaker.ComputeFingerprint(reverted));
        }

        // ── V-7 ─────────────────────────────────────────────────────────────

        [Test]
        public void V7_ARefusalIsAValue_AndTheListIsLeftBakeable()
        {
            var probe = Probe();
            Assert.IsTrue(Place(probe, Mx, Mz, retain: true, out _, out _));

            // Same spot: the pair cannot be separated after radius expansion.
            Assert.IsFalse(Place(probe, Mx, Mz, retain: true, out FPNavMesh mesh,
                out FPBuildingRejectionInfo rejection), "an overlapping placement was accepted");
            Assert.IsNull(mesh);
            Assert.AreEqual(FPBuildingRejection.BuildingsOverlap, rejection.Reason);

            // And the refusal did not leave the second placement in the list.
            Assert.AreEqual(1, probe.Count, "a refused placement must not stay in the list");
            Assert.IsTrue(probe.TryRebake(out FPNavMesh again, out _),
                "the list is no longer bakeable after a refusal");
            Assert.IsNotNull(again);
        }

        [Test]
        public void V7_AnOffGridCentreIsUnreachable_BecauseTheProbeQuantises()
        {
            // A 0.1 offset is NOT on the predicate grid (it is a binary-fraction grid), and an off-grid
            // centre is a malformed request the rebaker throws on — through the non-throwing entry
            // points too. Quantising on the way in is what makes that throw unreachable from a UI.
            var probe = Probe();
            Assert.IsFalse(FPGeoPredicates.IsOnGrid(FP64.FromDouble(Mx + 0.1)),
                "fixture: the offset centre was expected to be off the grid");

            Assert.DoesNotThrow(() =>
            {
                Assert.IsTrue(Place(probe, Mx + 0.1, Mz + 0.1, retain: true, out _, out _));
            });

            FPBuildingPlacement stored = probe.PlacementAt(0);
            Assert.IsTrue(FPGeoPredicates.IsOnGrid(stored.CentreX), "the stored centre is off-grid");
            Assert.IsTrue(FPGeoPredicates.IsOnGrid(stored.CentreZ), "the stored centre is off-grid");
        }

        // ── V-9 (신설) — flush ──────────────────────────────────────────────

        /// <summary>
        /// One lattice step for the shape THIS fixture places. Derived from the fixture's own
        /// catalog, not the tool's — they are different footprints with different deltas, and
        /// mixing them is a mistake that reads as a failing engine.
        /// </summary>
        private static double LatticeStep()
        {
            var exp = new FPBuildingShapeExpansion(
                NavAgentTestHelper.SquareBuildingCatalog, Field().BakeAgentRadius);
            Assert.IsTrue(exp.TryTilingDelta(0, 0, 0, out _, out long dz),
                "fixture: the shape must tile, or there is no flush spacing to snap to");
            return System.Math.Abs(dz) / (double)FPGeoPredicates.SNAP_UNITS_PER_WORLD;
        }

        [Test]
        public void V9_NeighboursMeetWithNoWalkableSliver_WhenSnappedToTheLattice()
        {
            // The symptom this exists for: two buildings that look adjacent but leave a hairline of
            // walkable ground between them, which the engine cannot tell from a door. Flush contact
            // happens at exactly ONE spacing — the footprint's tiling delta — and that delta is not
            // a round number, because the expansion pads two snap units per side.
            double step = LatticeStep();
            Assert.AreNotEqual(System.Math.Round(step * 2) / 2, step,
                "the padding vanished, so a hand-picked spacing could land flush and this fixture "
                + "no longer reproduces the bug it was written for");

            // Asked for a deliberately ROUND offset; the snap moves it onto the lattice.
            var probe = Probe();
            Assert.IsTrue(probe.SnapToTilingLattice, "snapping is meant to be the default");
            Assert.IsTrue(Place(probe, Mx, Mz, retain: false, out _, out _));
            Assert.IsTrue(Place(probe, Mx, Mz + 3.5, retain: false, out FPNavMesh mesh, out _),
                "the second placement was refused");

            // Measured between the two PLACED centres, not against the request: the lattice is
            // anchored at the origin, so the first centre moved too. What has to hold is that the
            // two sit exactly one lattice step apart.
            FPBuildingPlacement a = probe.PlacementAt(0), b = probe.PlacementAt(1);
            Assert.AreEqual(a.CentreX.ToDouble(), b.CentreX.ToDouble(), 1e-9,
                "both were asked for on the same x; the lattice should not have split them");
            Assert.AreEqual(step, b.CentreZ.ToDouble() - a.CentreZ.ToDouble(), 1e-9,
                "the two are not one lattice step apart, so they cannot be flush");

            Assert.IsFalse(
                Walkable(mesh, a.CentreX.ToDouble(),
                    (a.CentreZ.ToDouble() + b.CentreZ.ToDouble()) / 2.0),
                "a walkable sliver survives on the seam — the two carves did not meet");
        }

        [Test]
        public void V9_WithoutTheSnap_TheSliverIsBackAndThatIsTheReportedBug()
        {
            // The control group, and it needs a MARGIN rather than a round number: the acceptance
            // window is one exact value. A hair under the delta is refused as BuildingsOverlap; a
            // hair over is accepted and leaves the sliver. That narrowness is the whole problem —
            // by hand you land on one side or the other, never on it.
            double step = LatticeStep();
            var free = Probe();
            free.SnapToTilingLattice = false;

            Assert.IsTrue(Place(free, Mx, Mz, retain: false, out _, out _));
            Assert.IsTrue(Place(free, Mx, Mz + step + 0.01, retain: false, out FPNavMesh mesh, out _),
                "fixture: a placement just past the flush spacing should be accepted");
            Assert.AreEqual(step + 0.01, free.PlacementAt(1).CentreZ.ToDouble() - Mz, 1e-3,
                "snapping was off, so the centre should be where it was asked for");

            Assert.IsTrue(Walkable(mesh, Mx, Mz + (step + 0.01) / 2.0),
                "the seam is sealed even without the snap, so the fixture no longer reproduces the "
                + "reported gap and the positive gate above proves nothing");

            // And a hair UNDER is refused — the other wall of that one-value window.
            var tight = Probe();
            tight.SnapToTilingLattice = false;
            Assert.IsTrue(Place(tight, Mx, Mz, retain: false, out _, out _));
            Assert.IsFalse(Place(tight, Mx, Mz + step - 0.01, retain: false, out _,
                out FPBuildingRejectionInfo rejection));
            Assert.AreEqual(FPBuildingRejection.BuildingsOverlap, rejection.Reason);
        }

        // ── V-8 ─────────────────────────────────────────────────────────────

        [Test]
        public void V8_SwappingRebindsTheCallersOwnTrio()
        {
            // The tool's design leans on this: the agent system is built FROM the visualizer's
            // query/pathfinder/funnel, so SwapNavMesh rebinds the very instances the path tool
            // uses and there is no second set to keep in step. If a future swap allocated its own
            // trio instead, the path tool would keep answering on the old mesh and the only symptom
            // would be a window disagreeing with itself. This is that contract, pinned.
            FPNavMesh baseMesh = Field();
            var query = new FPNavMeshQuery(baseMesh, null);
            var pathfinder = new FPNavMeshPathfinder(baseMesh, query, null);
            var funnel = new FPNavMeshFunnel(baseMesh, query, null);
            var system = new FPNavAgentSystem(baseMesh, query, pathfinder, funnel, null);

            var probe = new FPNavMeshPlacementProbe(baseMesh, NavAgentTestHelper.SquareBuildingCatalog);
            Assert.IsTrue(Place(probe, Mx, Mz, retain: false, out FPNavMesh carved, out _));

            // Ground before, hole after — a point that discriminates the two meshes.
            var centre = new FPVector2(FP64.FromDouble(Mx), FP64.FromDouble(Mz));
            Assert.GreaterOrEqual(query.FindTriangle(centre), 0, "fixture: the centre starts as ground");

            system.SwapNavMesh(carved);

            Assert.Less(query.FindTriangle(centre), 0,
                "the externally held query still answers on the old mesh — SwapNavMesh no longer "
                + "rebinds the caller's trio, and the visualizer's path tool would go stale silently");
            Assert.AreSame(carved, system.CurrentMesh);
        }
    }
}
