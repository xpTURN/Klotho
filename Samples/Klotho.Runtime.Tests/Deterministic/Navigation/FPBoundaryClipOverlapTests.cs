using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// ClipOverlap end to end (Plan-BoundaryOverlapClip §3): overhanging placements clipped to
    /// the walkable region and carved, arbitrary-angle corridor sealing, run merging, the CNH
    /// dual-form area gates, and B′/D7/determinism — through the public Rebake surface.
    /// FPNavMeshClipperTests covers the walk geometry in isolation; FPBoundaryTouchSealTests
    /// pins the flush(identity) golden identity with Touch.
    /// </summary>
    [TestFixture]
    public class FPBoundaryClipOverlapTests
    {
        #region Fixtures

        private static readonly FPBuildingPlacementRules ClipRules =
            new FPBuildingPlacementRules(allowBuildingTouch: false, FPBoundaryPlacementPolicy.ClipOverlap);

        private static FPNavMesh BuildFromRing((double x, double z)[] ring)
        {
            var vertices = new FPVector3[ring.Length];
            var xs = new long[ring.Length];
            var zs = new long[ring.Length];
            for (int i = 0; i < ring.Length; i++)
            {
                vertices[i] = new FPVector3(
                    FP64.FromDouble(ring[i].x), FP64.Zero, FP64.FromDouble(ring[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            var cons = new List<int>();
            for (int i = 0; i < ring.Length; i++) { cons.Add(i); cons.Add((i + 1) % ring.Length); }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, cons.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>gcd == 1 in snap units — the mechanical device Plan §4-⑹(KD) demands: an
        /// axis-aligned "simplification" of a wall makes the FIXTURE fail, not the claim.</summary>
        private static void AssertGcd1(double ax, double az, double bx, double bz)
        {
            long dx = System.Math.Abs(FPGeoPredicates.Snap(FP64.FromDouble(bx)) - FPGeoPredicates.Snap(FP64.FromDouble(ax)));
            long dz = System.Math.Abs(FPGeoPredicates.Snap(FP64.FromDouble(bz)) - FPGeoPredicates.Snap(FP64.FromDouble(az)));
            Assert.AreEqual(1, (long)BigInteger.GreatestCommonDivisor(dx, dz),
                "fixture wall must be gcd = 1 — no interior lattice point, the geometry this plan exists for");
        }

        /// <summary>Single long arbitrary-angle wall (0,0)->(6,1); walkable above.
        /// gcd(6144, 1024) > 1 is fine here — these tests target overhang/merge shapes, and
        /// the gcd = 1 discipline is enforced on the corridor-seal fixture below.</summary>
        private static FPNavMesh SlantWall() => BuildFromRing(
            new[] { (0.0, 0.0), (6.0, 1.0), (6.0, 5.0), (0.0, 5.0) });

        private const double Snap = 1.0 / 1024.0;

        /// <summary>Arbitrary-angle corridor with gcd = 1 walls (deltas 16384 × 1025 snaps).</summary>
        private static FPNavMesh SlantCorridor() => BuildFromRing(
            new[] { (-8.0, -2.0), (8.0, -1.0 + Snap), (8.0, 3.0), (-8.0, 2.0 + Snap) });

        private static FPBuildingRect R(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static (bool accepted, FPNavMesh mesh, string message) Try(
            FPNavMesh baseMesh, FPBuildingRect[] buildings, FPBuildingPlacementRules rules)
        {
            try { return (true, FPNavMeshRebaker.Rebake(baseMesh, buildings, null, rules), null); }
            catch (InvalidOperationException e) { return (false, null, e.Message); }
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

        /// <summary>Σ doubled shoelace areas of the clip rings the rebake will carve — computed
        /// through the SAME walker on hand-expanded footprints (rects grow by the 0.5 bake
        /// radius), which is the CNH gate's reference value.</summary>
        private static (BigInteger doubledArea, int xPrimeCount) ClipReference(
            FPNavMesh baseMesh, params (double x0, double z0, double x1, double z1)[] expanded)
        {
            var snap = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            int n = expanded.Length;
            var polyX = new long[n * 4]; var polyZ = new long[n * 4];
            var polyStart = new int[n + 1]; var polyBounds = new long[n * 4];
            var polyYs = new FP64[n];
            for (int b = 0; b < n; b++)
            {
                long x0 = FPGeoPredicates.Snap(FP64.FromDouble(expanded[b].x0));
                long z0 = FPGeoPredicates.Snap(FP64.FromDouble(expanded[b].z0));
                long x1 = FPGeoPredicates.Snap(FP64.FromDouble(expanded[b].x1));
                long z1 = FPGeoPredicates.Snap(FP64.FromDouble(expanded[b].z1));
                int o = b * 4;
                polyX[o] = x0; polyZ[o] = z0; polyX[o + 1] = x1; polyZ[o + 1] = z0;
                polyX[o + 2] = x1; polyZ[o + 2] = z1; polyX[o + 3] = x0; polyZ[o + 3] = z1;
                polyStart[b] = o;
                polyBounds[o] = x0; polyBounds[o + 1] = z0; polyBounds[o + 2] = x1; polyBounds[o + 3] = z1;
            }
            polyStart[n] = n * 4;
            Assert.IsTrue(FPNavMeshClipper.TryBuildClipRings(
                snap, polyX, polyZ, polyStart, polyBounds, polyYs, n, out var rej, out var r),
                $"reference clip must succeed: {rej.Reason}");
            BigInteger area = 0;
            int xPrimes = 0;
            for (int ring = 0; ring < r.RingCount; ring++)
            {
                int lo = r.Starts[ring], hi = r.Starts[ring + 1];
                BigInteger sum = 0;
                for (int i = lo; i < hi; i++)
                {
                    int i2 = i + 1 < hi ? i + 1 : lo;
                    sum += (BigInteger)r.Xs[i] * r.Zs[i2] - (BigInteger)r.Xs[i2] * r.Zs[i];
                }
                area += BigInteger.Abs(sum);
                for (int i = lo; i < hi; i++)
                    if (!snap.CoordToIndex.ContainsKey((r.Xs[i], r.Zs[i])) && !IsCorner(polyX, polyZ, r.Xs[i], r.Zs[i]))
                        xPrimes++;
            }
            return (area, xPrimes);
        }

        private static bool IsCorner(long[] polyX, long[] polyZ, long x, long z)
        {
            for (int i = 0; i < polyX.Length; i++)
                if (polyX[i] == x && polyZ[i] == z) return true;
            return false;
        }

        #endregion

        [Test]
        public void DeepOverhang_CentroidOutside_AcceptedAndCarved()
        {
            // THE feature: most of the footprint hangs outside the walkable region — the old
            // probe answer is "outside", Touch rejects the crossing — and ClipOverlap accepts,
            // carving exactly the inside part. Building 1x3 at x∈[1.5,2.5]... expanded
            // x∈[1,3], z∈[-3.5,1]: centroid (2, -1.25) is BELOW the wall z = x/6.
            FPNavMesh baseMesh = SlantWall();
            var (accepted, mesh, message) = Try(baseMesh,
                new[] { R(1.5, -3.0, 2.5, 0.5) }, ClipRules);
            Assert.IsTrue(accepted, $"deep overhang must be accepted under ClipOverlap: {message}");
            Assert.IsFalse(Walkable(mesh, 2.0, 0.8), "inside footprint, above the wall: carved");
            Assert.IsTrue(Walkable(mesh, 2.0, 1.5), "just above the footprint: untouched");
            Assert.IsTrue(Walkable(mesh, 4.5, 1.2), "far side along the wall: untouched");

            // and the SAME placement under Touch is rejected — the accept-set change IS the feature
            var touch = new FPBuildingPlacementRules(false, FPBoundaryPlacementPolicy.Touch);
            var (touchAccepted, _, touchMsg) = Try(baseMesh, new[] { R(1.5, -3.0, 2.5, 0.5) }, touch);
            Assert.IsFalse(touchAccepted);
            StringAssert.Contains("touches or crosses", touchMsg);
        }

        [Test]
        public void DeepOverhang_AreaGate_TransitionForm()
        {
            // CNH's transition-fixture form: Area(rebaked) ≤ Area(base) − Σ shoelace(C), and
            // the deficit — undercarve pockets the degenerate filter absorbed — is bounded by
            // transitions × (4√2 snaps)² (doubled-area units: (4√2)² · 2 / 2 = 64 snap²… the
            // doubled bound per pocket is 2 · ½ · (4√2)² = 64).
            FPNavMesh baseMesh = SlantWall();
            var (accepted, mesh, _) = Try(baseMesh, new[] { R(1.5, -3.0, 2.5, 0.5) }, ClipRules);
            Assert.IsTrue(accepted);
            var (clipArea, xPrimes) = ClipReference(baseMesh, (1.0, -3.5, 3.0, 1.0));
            Assert.Greater(xPrimes, 0, "the reference must actually contain X′ vertices");

            BigInteger deficit = DoubledArea(baseMesh) - clipArea - DoubledArea(mesh);
            Assert.GreaterOrEqual(deficit, 0,
                "rebaked area above the reference means the carve LEAKED — under-carve");
            Assert.LessOrEqual(deficit, (BigInteger)64 * xPrimes,
                "deficit beyond the pocket bound means real walkable area vanished");
        }

        [Test]
        public void Flush_AreaGate_IdentityForm()
        {
            // CNH's transition-free form: exact equality, and in DEBUG the degenerate counter
            // must not tick — nothing is absorbed on the identity path.
            FPNavMesh baseMesh = SlantWall();
#if DEBUG
            int before = FPNavMeshBuildPipeline.DegenerateCompactions;
#endif
            var (accepted, mesh, _) = Try(baseMesh, new[] { R(0.5, 2.0, 1.5, 3.0) }, ClipRules);
            Assert.IsTrue(accepted, "flush against the left wall x=0 (expanded) is transition-free");
            Assert.AreEqual((BigInteger)2 * 2048 * 2048, DoubledArea(baseMesh) - DoubledArea(mesh),
                "identity carve must equal the expanded footprint exactly");
#if DEBUG
            Assert.AreEqual(before, FPNavMeshBuildPipeline.DegenerateCompactions,
                "no degenerate absorption on the identity path");
#endif
        }

        [Test]
        public void SlantCorridor_Seal_SplitsGraph_DemolitionRestores()
        {
            // The plan's headline: an arbitrary-angle corridor no flush placement can seal
            // (gcd = 1 walls — no interior lattice point to sit on) sealed by ONE overhanging
            // placement. CB's three pieces + B′ demolition + D7.
            AssertGcd1(-8.0, -2.0, 8.0, -1.0 + Snap);
            AssertGcd1(8.0, 3.0, -8.0, 2.0 + Snap);
            FPNavMesh corridor = SlantCorridor();
            ulong emptyBefore = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(corridor, new FPBuildingRect[0], null, ClipRules));

            var (accepted, sealedMesh, message) = Try(corridor,
                new[] { R(-0.5, -2.5, 0.5, 3.5) }, ClipRules);   // expanded [-1,1] x [-3,4]
            Assert.IsTrue(accepted, $"corridor-spanning overhang must be accepted: {message}");

            Assert.IsTrue(Walkable(sealedMesh, -5, 0.5), "west side on-mesh");
            Assert.IsTrue(Walkable(sealedMesh, 5, 0.5), "east side on-mesh");
            Assert.IsFalse(PathExists(sealedMesh, -5, 0.5, 5, 0.5), "no path across the seal");
            Assert.IsTrue(PathExists(sealedMesh, -5, 0.5, -7, 0.5), "control: same side routes");

            Assert.AreNotEqual(emptyBefore, FPNavMeshRebaker.ComputeFingerprint(sealedMesh),
                "D7 must see the seal");
            FPNavMesh demolished = FPNavMeshRebaker.Rebake(
                corridor, new FPBuildingRect[0], null, ClipRules);
            Assert.AreEqual(emptyBefore, FPNavMeshRebaker.ComputeFingerprint(demolished),
                "removing the seal must restore the empty-rebake mesh bit-exactly");
            Assert.IsTrue(PathExists(demolished, -5, 0.5, 5, 0.5));
        }

        [Test]
        public void SlantCorridor_PartialOverhang_DoesNotSeal_PathSurvives()
        {
            // The seal test's counterpart: a building overhanging ONE slanted wall of the same
            // corridor must carve only what it covers and leave the corridor routable — this is
            // the fixture that separates "clips exactly the crossing part" from an over-carve
            // that seals what the player did not seal (the LA failure direction). Expanded
            // footprint [-1,1] x [-3,0]: crosses the bottom wall (z ≈ -1.56..-1.44 there),
            // stays ~2.4 m short of the top wall.
            FPNavMesh corridor = SlantCorridor();
            var (accepted, mesh, message) = Try(corridor,
                new[] { R(-0.5, -2.5, 0.5, -0.5) }, ClipRules);
            Assert.IsTrue(accepted, $"partial overhang must be accepted: {message}");

            Assert.IsFalse(Walkable(mesh, 0, -1.0), "inside footprint above the bottom wall: carved");
            Assert.IsTrue(Walkable(mesh, 0, 1.5), "corridor above the footprint: open");
            Assert.IsTrue(Walkable(mesh, -5, 0.5), "west approach untouched");
            Assert.IsTrue(Walkable(mesh, 5, 0.5), "east approach untouched");
            Assert.IsTrue(PathExists(mesh, -5, 0.5, 5, 0.5),
                "a one-wall overhang must NOT seal the corridor — agents route over the remaining width");
        }

        [Test]
        public void SameEdgeNeighbours_Merge_PassageAwayFromWallSurvives()
        {
            // JA + LA: two buildings on one wall edge merge into one ring; the extra carve is
            // a snaps-thin band along the wall, so the passage BETWEEN the buildings away from
            // the wall must survive — merging must not seal what the player didn't seal.
            FPNavMesh baseMesh = SlantWall();
            var (accepted, mesh, message) = Try(baseMesh, new[]
            {
                R(1.5, -0.5, 2.0, 0.5),    // expanded [1,2.5] x [-1,1]
                R(4.0, -0.5, 4.5, 0.5),    // expanded [3.5,5] x [-1,1]
            }, ClipRules);
            Assert.IsTrue(accepted, $"same-edge neighbours must be accepted: {message}");
            Assert.IsFalse(Walkable(mesh, 1.75, 0.8), "inside building A (above wall): carved");
            Assert.IsFalse(Walkable(mesh, 4.25, 0.9), "inside building B (above wall): carved");
            Assert.IsTrue(Walkable(mesh, 3.0, 1.2), "between the buildings, off the wall: OPEN");
            Assert.IsTrue(PathExists(mesh, 0.5, 2.0, 5.5, 2.0),
                "the passage between the buildings must still route");
        }

        [Test]
        public void FullyOutside_ClipsToNothing_RejectedWithClipMessage()
        {
            var (accepted, _, message) = Try(SlantWall(),
                new[] { R(1.5, -3.0, 2.5, -2.0) }, ClipRules);   // entirely below the wall
            Assert.IsFalse(accepted);
            StringAssert.Contains("clips to nothing", message);
        }

        // ── the single-ghost preview under ClipOverlap ──────────────────────
        //
        // This replaced GhostPreview_UnderClipOverlap_ThrowsLoudGap. That test pinned a
        // deliberate gap: the preview threw because the accepted set was thought to need
        // cached clip RINGS to answer. It does not — rings cannot be cached at all (a ghost
        // that shares a wall run rewrites its neighbour's ring), and what the preview
        // actually lacked was the whole set's AABBs. The three below take the pin's place.

        [Test]
        public void GhostPreview_UnderClipOverlap_MatchesTheRebake()
        {
            // T-2, and the reason this API exists: a green ghost must mean an accepted command.
            // Swept along an arbitrary-angle wall so the ghost passes through interior, grazing,
            // clipped and refused positions rather than one hand-picked spot.
            FPNavMesh baseMesh = SlantWall();
            var context = FPNavMeshRebaker.CreateContext(baseMesh, null);
            int agree = 0, clipped = 0, refused = 0;
            for (int i = 0; i < 24; i++)
            {
                double x = 0.5 + i * 0.22;
                for (int j = 0; j < 10; j++)
                {
                    double z = -0.6 + j * 0.31;
                    FPBuildingRect ghost = R(x, z, x + 1.0, z + 1.0);

                    bool preview = FPNavMeshRebaker.TryValidateOne(
                        context, ghost, out FPBuildingRejectionInfo pr, ClipRules);
                    var (accepted, _, _) = Try(baseMesh, new[] { ghost }, ClipRules);

                    // Verdict and REASON, not the index — D-5 leaves the two paths free to number
                    // the same refusal differently, and comparing indices here would pin the
                    // opposite of that decision.
                    Assert.AreEqual(accepted, preview,
                        $"verdict disagrees at ({x:F2}, {z:F2}): preview={preview} rebake={accepted}"
                        + $" (preview reason {pr.Reason})");
                    agree++;
                    if (accepted) clipped++; else refused++;
                }
            }
            // Non-vacuity: a sweep that only ever refuses (or only ever accepts) would agree
            // perfectly while testing one branch. Both must appear.
            Assert.Greater(clipped, 0, "the sweep never accepted — it is testing one branch only");
            Assert.Greater(refused, 0, "the sweep never refused — same problem, other side");
            TestContext.Out.WriteLine($"preview == rebake at {agree} positions ({clipped} accepted, {refused} refused)");
        }

        [Test]
        public void GhostPreview_WithBuildingsAlreadyDown_StillMatchesTheRebake()
        {
            // T-2b. T-2 validates every ghost against an EMPTY accepted set, so it never once
            // reads the transition cache — the path where a preview can disagree with a rebake by
            // replaying stale per-building work is exactly the path T-2 does not take.
            FPNavMesh baseMesh = SlantWall();
            var existing = new[] { R(1.0, 0.0, 2.0, 1.0), R(4.0, 0.4, 5.0, 1.4) };

            var context = FPNavMeshRebaker.CreateContext(baseMesh, null);
            FPNavMeshRebaker.Rebake(context, existing, null, ClipRules);
            FPBuildingAcceptedSet accepted = context.Accepted;

            // The cache has to be there and hold something. Without this the test would pass on a
            // build where Capture never filled it — it would just be measuring T-2 again with two
            // extra buildings.
            Assert.AreEqual(existing.Length, accepted.CachedBuildingCount,
                "the carving rebake did not leave a clip cache covering its own buildings");
            Assert.Greater(accepted.CachedTransitionStart[accepted.CachedBuildingCount], 0,
                "the cache is empty — these buildings produced no transitions, so replaying them "
                + "proves nothing");

            int agree = 0, clipped = 0, refused = 0;
            for (int i = 0; i < 20; i++)
            {
                double x = 0.6 + i * 0.26;
                for (int j = 0; j < 8; j++)
                {
                    double z = 0.8 + j * 0.35;
                    FPBuildingRect ghost = R(x, z, x + 1.0, z + 1.0);

                    bool preview = FPNavMeshRebaker.TryValidateOne(
                        context, ghost, out FPBuildingRejectionInfo pr, ClipRules);
                    var all = new[] { existing[0], existing[1], ghost };
                    var (accept, _, _) = Try(baseMesh, all, ClipRules);

                    Assert.AreEqual(accept, preview,
                        $"verdict disagrees at ({x:F2}, {z:F2}) with 2 buildings down: "
                        + $"preview={preview} rebake={accept} (preview reason {pr.Reason})");
                    agree++;
                    if (accept) clipped++; else refused++;
                }
            }
            Assert.Greater(clipped, 0, "the sweep never accepted — it is testing one branch only");
            Assert.Greater(refused, 0, "the sweep never refused — same problem, other side");
            TestContext.Out.WriteLine(
                $"preview == rebake at {agree} positions over a 2-building cache "
                + $"({clipped} accepted, {refused} refused)");
        }

        [Test]
        public void GhostPreview_SnapshotFormAndContextForm_AgreeWhenGivenTheSameRules()
        {
            // T-1. The `rules` argument is what makes this comparable at all: the snapshot form
            // defaults to `default` (= Reject) and the context form to the rules the rebake ran
            // under, so omitting it compares two POLICIES rather than two forms.
            FPNavMesh baseMesh = SlantWall();
            var context = FPNavMeshRebaker.CreateContext(baseMesh, null);
            var scratch = new FPBuildingPreviewScratch();
            var existing = Array.Empty<FPBuildingRect>();

            for (int i = 0; i < 20; i++)
            {
                double x = 0.7 + i * 0.26;
                FPBuildingRect ghost = R(x, -0.4, x + 1.0, 0.9);

                bool ctxForm = FPNavMeshRebaker.TryValidateOne(
                    context, ghost, out FPBuildingRejectionInfo cr, ClipRules);
                bool snapForm = FPNavMeshRebaker.TryValidateOne(
                    baseMesh == null ? null : FPNavMeshRebaker.CreateSnapshot(baseMesh),
                    existing, 0, ghost, out FPBuildingRejectionInfo sr, scratch, ClipRules);

                Assert.AreEqual(ctxForm, snapForm, $"verdict differs at x={x:F2}");
                Assert.AreEqual(cr.Reason, sr.Reason, $"reason differs at x={x:F2}");
            }
        }

        [Test]
        public void GhostPreview_TransitionFreeGhost_AgreesWithTouchPreview()
        {
            // T-5. A ghost that crosses nothing is the identity case, so ClipOverlap's preview
            // must answer exactly as Touch's does. Site is excluded deliberately: the swallow
            // refusal has two producers and ClipOverlap reaches the clipper's first.
            var touch = new FPBuildingPlacementRules(false, FPBoundaryPlacementPolicy.Touch);
            FPNavMesh baseMesh = SlantWall();
            var clipCtx = FPNavMeshRebaker.CreateContext(baseMesh, null);
            var touchCtx = FPNavMeshRebaker.CreateContext(baseMesh, null);

            // The expanded footprint is 2x2 and SlantWall's walkable strip runs from the (0,0)->(6,1)
            // slant up to z = 5, so the ghost has to stay clear of both. A drifting fixture is what
            // made the first version of this test fail: at z = 3.58 the ghost crossed the top wall,
            // ClipOverlap clipped it and Touch refused the crossing — both correct, and nothing to
            // do with the identity path it meant to check.
            var strict = new FPBuildingPlacementRules(false, FPBoundaryPlacementPolicy.Reject);
            var strictCtx = FPNavMeshRebaker.CreateContext(baseMesh, null);
            for (int i = 0; i < 8; i++)
            {
                double z = 1.6 + i * 0.22;
                FPBuildingRect ghost = R(1.5, z, 2.5, z + 1.0);

                // Non-vacuity, black box: the DEFAULT policy refuses ANY contact with the boundary,
                // so its acceptance is proof this ghost touches nothing — which is exactly what
                // "transition-free" means. Asserting the geometry by hand is what drifted.
                Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(strictCtx, ghost, out var rs, strict),
                    $"fixture drifted: the ghost at z={z:F2} is not clear of the boundary ({rs.Reason})");

                bool a = FPNavMeshRebaker.TryValidateOne(clipCtx, ghost, out var ra, ClipRules);
                bool b = FPNavMeshRebaker.TryValidateOne(touchCtx, ghost, out var rb, touch);
                Assert.AreEqual(b, a, $"identity ghost verdict differs at z={z:F2}");
                Assert.AreEqual(rb.Reason, ra.Reason, $"identity ghost reason differs at z={z:F2}");
            }
        }

        [Test]
        public void Determinism_SameSeal_BitIdentical()
        {
            FPNavMesh corridor = SlantCorridor();
            var b = new[] { R(-0.5, -2.5, 0.5, 3.5) };
            ulong f1 = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(corridor, b, null, ClipRules));
            ulong f2 = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(corridor, b, null, ClipRules));
            Assert.AreEqual(f1, f2);
        }

        [Test]
        public void SlantCorridor_Seal_Fingerprint_Golden()
        {
            // Golden for the full clip path: X′ spiral, welded runs, coincident-constraint
            // parity, carve. Re-pin only with a written reason — this is the only bit-exact
            // regression guard over the whole arbitrary-angle pipeline.
            var (accepted, mesh, _) = Try(SlantCorridor(), new[] { R(-0.5, -2.5, 0.5, 3.5) }, ClipRules);
            Assert.IsTrue(accepted);
            Assert.AreEqual(0xF1E08BE7B1B3F2D9UL, FPNavMeshRebaker.ComputeFingerprint(mesh),
                "clip-seal fingerprint moved — if intended, re-pin and say why in the commit");
        }
    }
}
