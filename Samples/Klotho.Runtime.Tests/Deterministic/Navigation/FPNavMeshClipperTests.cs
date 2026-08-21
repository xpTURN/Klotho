using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Unit tests for the clip walk core (Plan-BoundaryOverlapClip §2-ⓒ) — the geometry layer
    /// alone, driven directly through <see cref="FPNavMeshClipper.TryBuildClipRings"/> with
    /// hand-authored footprints. Integration (validation order, carving, goldens) is
    /// FPBoundaryClipOverlapTests' job. Fixtures reuse the ⓪-⑶ measurement geometry so the
    /// measured claims and the shipped behaviour stay one thing.
    /// </summary>
    [TestFixture]
    public class FPNavMeshClipperTests
    {
        #region Fixtures

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

        /// <summary>The ⓪-⑶ wall: one long arbitrary-angle edge (0,0)->(6,1).</summary>
        private static FPNavMesh SlantWall() => BuildFromRing(
            new[] { (0.0, 0.0), (6.0, 1.0), (6.0, 5.0), (0.0, 5.0) });

        /// <summary>The ⓪-⑶ pinch polyline: vertex V1=(0,0) between two wall edges.</summary>
        private static FPNavMesh TwoEdgeWall() => BuildFromRing(
            new[] { (-6.0, -1.0), (0.0, 0.0), (6.0, 1.0), (6.0, 5.0), (-6.0, 5.0) });

        /// <summary>Arbitrary-angle corridor: bottom wall (-8,-2)->(8,-1), top (8,3)->(-8,2).</summary>
        private static FPNavMesh SlantCorridor() => BuildFromRing(
            new[] { (-8.0, -2.0), (8.0, -1.0), (8.0, 3.0), (-8.0, 2.0) });

        private static long S(double v) => FPGeoPredicates.Snap(FP64.FromDouble(v));

        /// <summary>
        /// Outer square with three holes, so the boundary decomposes into FOUR rings. The other
        /// fixtures here are single-ring, which makes them useless for anything that filters BY
        /// ring — a prune with one ring to consider always keeps it.
        /// </summary>
        private static FPNavMesh HoledSlab()
        {
            var rings = new[]
            {
                new[] { (-8.0, -8.0), (8.0, -8.0), (8.0, 8.0), (-8.0, 8.0) },   // outer
                new[] { (-5.0, -5.0), (-3.0, -5.0), (-3.0, -3.0), (-5.0, -3.0) },
                new[] { ( 2.0, -6.0), ( 4.0, -6.0), ( 4.0, -4.0), ( 2.0, -4.0) },
                new[] { (-1.0,  3.0), ( 1.0,  3.0), ( 1.0,  5.0), (-1.0,  5.0) },
            };
            int total = 0; foreach (var r in rings) total += r.Length;
            var vertices = new FPVector3[total];
            var xs = new long[total]; var zs = new long[total];
            var cons = new List<int>();
            int at = 0;
            foreach (var r in rings)
            {
                int start = at;
                for (int i = 0; i < r.Length; i++)
                {
                    vertices[at] = new FPVector3(
                        FP64.FromDouble(r[i].Item1), FP64.Zero, FP64.FromDouble(r[i].Item2));
                    xs[at] = FPGeoPredicates.Snap(vertices[at].x);
                    zs[at] = FPGeoPredicates.Snap(vertices[at].z);
                    at++;
                }
                for (int i = 0; i < r.Length; i++)
                { cons.Add(start + i); cons.Add(start + (i + 1) % r.Length); }
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, cons.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>Hand-author the expanded footprints (CCW rects) the rebaker would build.</summary>
        private static (long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, FP64[] polyYs)
            Rects(params (double x0, double z0, double x1, double z1)[] rects)
        {
            var polyX = new long[rects.Length * 4];
            var polyZ = new long[rects.Length * 4];
            var polyStart = new int[rects.Length + 1];
            var polyBounds = new long[rects.Length * 4];
            var polyYs = new FP64[rects.Length];
            for (int b = 0; b < rects.Length; b++)
            {
                long x0 = S(rects[b].x0), z0 = S(rects[b].z0), x1 = S(rects[b].x1), z1 = S(rects[b].z1);
                int o = b * 4;
                polyX[o] = x0; polyZ[o] = z0;
                polyX[o + 1] = x1; polyZ[o + 1] = z0;
                polyX[o + 2] = x1; polyZ[o + 2] = z1;
                polyX[o + 3] = x0; polyZ[o + 3] = z1;
                polyStart[b] = o;
                polyBounds[o] = x0; polyBounds[o + 1] = z0; polyBounds[o + 2] = x1; polyBounds[o + 3] = z1;
                polyYs[b] = FP64.Zero;
            }
            polyStart[rects.Length] = rects.Length * 4;
            return (polyX, polyZ, polyStart, polyBounds, polyYs);
        }

        private static bool Clip(FPNavMesh mesh,
            (double x0, double z0, double x1, double z1)[] rects,
            out FPBuildingRejectionInfo rejection, out FPNavMeshClipper.Result result)
        {
            var snap = FPNavMeshRebaker.CreateSnapshot(mesh);
            var (px, pz, ps, pb, py) = Rects(rects);
            return FPNavMeshClipper.TryBuildClipRings(
                snap, px, pz, ps, pb, py, rects.Length, out rejection, out result);
        }

        private static List<(long x, long z)> Ring(in FPNavMeshClipper.Result r, int ring)
        {
            var list = new List<(long, long)>();
            for (int i = r.Starts[ring]; i < r.Starts[ring + 1]; i++)
                list.Add((r.Xs[i], r.Zs[i]));
            return list;
        }

        private static int CountOf(List<(long x, long z)> ring, double x, double z)
            => ring.Count(v => v.x == S(x) && v.z == S(z));

        private static bool Adjacent(List<(long x, long z)> ring, double ax, double az, double bx, double bz)
        {
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var v = ring[i]; var w = ring[(i + 1) % n];
                if ((v.x == S(ax) && v.z == S(az) && w.x == S(bx) && w.z == S(bz))
                    || (v.x == S(bx) && v.z == S(bz) && w.x == S(ax) && w.z == S(az)))
                    return true;
            }
            return false;
        }

        #endregion

        [Test]
        public void SingleOverhang_EmitsHexRing_RunCoversWholeEdge()
        {
            // The measured single-building shape: run = the WHOLE edge (V0, V1 welded), one X′
            // per transition, only the inside corners of the footprint.
            bool ok = Clip(SlantWall(), new[] { (1.0, -1.0, 2.0, 1.0) }, out var rej, out var r);
            Assert.IsTrue(ok, $"clip must succeed: {rej.Reason}");
            Assert.AreEqual(1, r.RingCount);
            var ring = Ring(r, 0);
            Assert.AreEqual(6, ring.Count, "X′ ×2 + inside corners ×2 + welded wall vertices ×2");
            Assert.AreEqual(1, CountOf(ring, 0, 0), "V0 welded once");
            Assert.AreEqual(1, CountOf(ring, 6, 1), "V1 welded once");
            Assert.AreEqual(1, CountOf(ring, 1, 1), "inside corner");
            Assert.AreEqual(1, CountOf(ring, 2, 1), "inside corner");
            Assert.AreEqual(0, CountOf(ring, 1, -1), "outside corner must not appear");
            Assert.AreEqual(0, CountOf(ring, 2, -1), "outside corner must not appear");
            Assert.IsTrue(Adjacent(ring, 0, 0, 6, 1),
                "the wall run must be the base edge itself — exact coincidence is the parity-0 premise");
        }

        [Test]
        public void SameEdge_TwoBuildings_MergeIntoOneRing()
        {
            // JA: same-edge runs overlap completely -> ONE merged ring; the shared wall edge
            // appears once; the strip between the buildings is closed by the X′ chord.
            bool ok = Clip(SlantWall(),
                new[] { (1.0, -1.0, 2.0, 1.0), (3.5, -1.0, 4.5, 1.0) }, out var rej, out var r);
            Assert.IsTrue(ok, $"clip must succeed: {rej.Reason}");
            Assert.AreEqual(1, r.RingCount, "shared run ⇒ union-find merges to one ring");
            var ring = Ring(r, 0);
            Assert.AreEqual(1, CountOf(ring, 0, 0));
            Assert.AreEqual(1, CountOf(ring, 6, 1));
            Assert.IsTrue(Adjacent(ring, 0, 0, 6, 1), "one wall run covering the whole edge");
            foreach (var (cx, cz) in new[] { (1.0, 1.0), (2.0, 1.0), (3.5, 1.0), (4.5, 1.0) })
                Assert.AreEqual(1, CountOf(ring, cx, cz), $"inside corner ({cx},{cz})");
            Assert.AreEqual(2 + 4 + 4, ring.Count, "2 welded + 4 corners + 4 X′");
        }

        [Test]
        public void AdjacentEdges_StayTwoRings_SharingOnlyTheVertex()
        {
            // ⓪-⑶'s second case: runs on adjacent edges touch at V1=(0,0) only — separate
            // groups, two rings, the shared vertex welded into both.
            bool ok = Clip(TwoEdgeWall(),
                new[] { (-4.0, -1.5, -3.0, 0.5), (2.0, -0.5, 3.0, 1.5) }, out var rej, out var r);
            Assert.IsTrue(ok, $"clip must succeed: {rej.Reason}");
            Assert.AreEqual(2, r.RingCount, "vertex-only contact must NOT merge");
            Assert.AreEqual(1, CountOf(Ring(r, 0), 0, 0), "V1 in ring 0");
            Assert.AreEqual(1, CountOf(Ring(r, 1), 0, 0), "V1 in ring 1 — same welded base vertex");
        }

        [Test]
        public void StraddlingBuilding_MergesBothNeighbours_TransitiveClosure()
        {
            // ⓪-⑶'s third case: C straddles V1, its run covers both edges entirely, and A·B —
            // which do not overlap each other — merge THROUGH it. Interior wall vertex V1 is
            // passed through (경유), not an endpoint.
            bool ok = Clip(TwoEdgeWall(), new[]
            {
                (-4.0, -1.5, -3.0, 0.5),   // A on e0 only
                (2.0, -0.5, 3.0, 1.5),     // B on e1 only
                (-1.0, -1.0, 1.0, 1.0),    // C straddling V1
            }, out var rej, out var r);
            Assert.IsTrue(ok, $"clip must succeed: {rej.Reason}");
            Assert.AreEqual(1, r.RingCount, "group = transitive closure of pairwise sharing");
            var ring = Ring(r, 0);
            Assert.AreEqual(1, CountOf(ring, -6, -1), "far overshoot: e0's outer vertex");
            Assert.AreEqual(1, CountOf(ring, 6, 1), "far overshoot: e1's outer vertex");
            Assert.AreEqual(1, CountOf(ring, 0, 0), "V1 passed through exactly once");
        }

        [Test]
        public void SlantCorridor_FullSpan_OneRingTwoRuns()
        {
            // The seal shape: a footprint spanning the corridor crosses BOTH walls; one ring,
            // two runs, each run the whole wall edge; no footprint corner is inside walkable.
            bool ok = Clip(SlantCorridor(), new[] { (-1.0, -3.0, 1.0, 4.0) }, out var rej, out var r);
            Assert.IsTrue(ok, $"clip must succeed: {rej.Reason}");
            Assert.AreEqual(1, r.RingCount);
            var ring = Ring(r, 0);
            foreach (var (cx, cz) in new[] { (-8.0, -2.0), (8.0, -1.0), (8.0, 3.0), (-8.0, 2.0) })
                Assert.AreEqual(1, CountOf(ring, cx, cz), $"welded corridor corner ({cx},{cz})");
            Assert.IsTrue(Adjacent(ring, -8, -2, 8, -1), "bottom run = the base edge");
            Assert.IsTrue(Adjacent(ring, 8, 3, -8, 2), "top run = the base edge");
            Assert.AreEqual(8, ring.Count, "4 welded + 4 X′ — no footprint corner is in walkable");
        }

        [Test]
        public void FlushOrInterior_TransitionFree_AreIdentityBuildings()
        {
            // Flush contact (corner incidences, zero proper crossings) and a fully interior
            // footprint both clip to themselves — no ring emitted here; the caller carves the
            // footprint verbatim after probing.
            bool ok = Clip(SlantWall(), new[]
            {
                (2.0, 2.0, 3.0, 3.0),      // strictly interior
                (0.0, 2.0, 1.0, 3.0),      // flush against the left wall x=0 (collinear side)
            }, out var rej, out var r);
            Assert.IsTrue(ok, $"clip must succeed: {rej.Reason}");
            Assert.AreEqual(0, r.RingCount);
            Assert.IsTrue(r.IdentityBuilding[0]);
            Assert.IsTrue(r.IdentityBuilding[1]);
        }

        [Test]
        public void CornerExactlyOnWall_WhileCrossing_RejectedDefined()
        {
            // (3, 0.5) lies exactly on the slant wall z = x/6 — a lattice-coincident contact.
            // Mixed with a proper crossing the alternation would silently break, so V1 rejects.
            bool ok = Clip(SlantWall(), new[] { (3.0, 0.5, 4.0, 1.5) }, out var rej, out _);
            Assert.IsFalse(ok);
            Assert.AreEqual(FPBuildingRejection.ExactBoundaryContact, rej.Reason);
        }

        [Test]
        public void ClipRings_GhostLocalisedValidation_AgreesWithCheckingEveryRing()
        {
            // D-4/D-5. Localising the post-walk validation to the ghost's group is the one change
            // in this stage that REMOVES checks, so the equivalence needs pinning against the form
            // that checks every ring — under the same precondition the preview has, namely that the
            // other buildings were already accepted.
            //
            // The trap it guards (F-13-b): the pair layer is written r2 = r + 1, complete only
            // because the unlocalised loop eventually reaches every ring as r. Restrict r to the
            // ghost's rings and every pair with a LOWER-numbered ring vanishes — and rings come out
            // in building order, so an existing building's ring is exactly the lower-numbered one.
            //
            // The sweep's shape is measured, not guessed: over 47,021 walks the post-walk layer
            // refused 92 times (77 self-simplicity, 15 pair) and merged the ghost 448 times, so the
            // footprint SIZE is swept as well as its position — a fixed-size ghost reaches neither.
            var widths = new[] { 0.72, 1.20, 1.68, 2.16, 2.48 };
            int compared = 0, merged = 0, refusals = 0, lowerPair = 0, remapped = 0;
            foreach (FPNavMesh mesh in new[] { HoledSlab(), SlantWall(), SlantCorridor(), TwoEdgeWall() })
            {
                var snap = FPNavMeshRebaker.CreateSnapshot(mesh);
                var existing = new[] { (-2.2, -1.3, -0.6, 0.4) };

                // The precondition. If the set without the ghost does not validate, no preview can
                // be in this state and the comparison would be against a value the API never sees.
                var (ex, ez, es, eb, ey) = Rects(existing);
                if (!FPNavMeshClipper.TryBuildClipRings(snap, ex, ez, es, eb, ey, 1, out _, out var solo))
                    continue;
                if (!FPNavMeshClipper.ValidateClipRings(snap, in solo, out _))
                    continue;

                for (int i = 0; i <= 40; i++)
                {
                    for (int j = 0; j <= 22; j++)
                    {
                        foreach (double w in widths)
                        {
                            double x = -2.4 + i * 0.18, z = -2.0 + j * 0.18;
                            const int ghost = 1;
                            var (px, pz, ps, pb, py) = Rects(
                                existing[0], (x, z, x + w, z + w + 0.3));
                            if (!FPNavMeshClipper.TryBuildClipRings(
                                    snap, px, pz, ps, pb, py, 2, out _, out var res))
                                continue;

                            bool full = FPNavMeshClipper.ValidateClipRings(snap, in res, out var fullRej);
                            bool local = FPNavMeshClipper.ValidateClipRings(
                                snap, in res, out var localRej, ghost);

                            string at = $"({x:F2}, {z:F2}) w={w:F2} on {mesh.Triangles.Length} tris";
                            Assert.AreEqual(full, local,
                                $"localised validation disagrees at {at}: full={full} "
                                + $"({fullRej.Reason}) localised={local} ({localRej.Reason})");
                            compared++;

                            int ghostRoot = res.Root[ghost];
                            if (ghostRoot != ghost) merged++;
                            for (int r = 0; r < res.RingCount && lowerPair == 0; r++)
                                if (res.GroupRoot[r] == ghostRoot)
                                    for (int r2 = 0; r2 < r; r2++)
                                        if (res.GroupRoot[r2] != ghostRoot) { lowerPair++; break; }

                            if (local) continue;
                            refusals++;
                            Assert.AreEqual(fullRej.Reason, localRej.Reason,
                                $"the two forms refused for different reasons at {at}");
                            // D-5: Union keeps the smaller index as root and the ghost is always
                            // the highest, so a merged group's root is ALWAYS an existing building —
                            // reporting it raw names an innocent neighbour.
                            Assert.AreEqual(ghost, localRej.IndexA,
                                $"a localised refusal at {at} named building {localRej.IndexA} "
                                + $"rather than the ghost ({localRej.Reason})");
                            if (fullRej.IndexA != localRej.IndexA) remapped++;
                        }
                    }
                }
            }
            // Non-vacuity, four ways — each of these was zero in an earlier draft of this sweep.
            Assert.Greater(compared, 0, "no sample satisfied the precondition");
            Assert.Greater(refusals, 0,
                "the post-walk layer never refused, so 'they agree' compared one branch only");
            Assert.Greater(merged, 0,
                "the ghost never merged with the existing building, so the below-r pair and the "
                + "index remap were never reachable");
            Assert.Greater(lowerPair, 0,
                "no ghost ring ever sat above a non-ghost ring — the r2 = r + 1 form would have "
                + "passed this sweep unchanged");
            Assert.Greater(remapped, 0,
                "no refusal was remapped, so D-5 is untested: every refusal already named the ghost");
            TestContext.Out.WriteLine(
                $"localised == full at {compared} samples ({merged} merged, {refusals} refusals, "
                + $"{remapped} remapped to the ghost)");
        }

        [Test]
        public void ClipRings_RingBoxPrune_NeverSkipsAnEdgeTheFlatScanWouldTest()
        {
            // The prune that skips a whole ring is the one change in this stage that can produce a
            // wrong mesh with every check still green — and the suite proved it: widening
            // RingBoxDisjoint by a single snap unit left all 2,289 tests passing. The tightness of
            // RingBounds (pinned in the pure-filter case) is not enough on its own, because that
            // says the DATA is right and says nothing about the test applied to it.
            //
            // So this pins the implication directly, through the production predicate: if the ring
            // box is called disjoint, no edge in that ring may pass the edge-level box test the
            // flat scan used to apply to all of them.
            long queried = 0, skipped = 0, kept = 0;
            foreach (FPNavMesh mesh in new[] { HoledSlab(), SlantWall(), SlantCorridor(), TwoEdgeWall() })
            {
                var snap = FPNavMeshRebaker.CreateSnapshot(mesh);
                for (int i = 0; i < 20; i++)
                {
                    for (int j = 0; j < 11; j++)
                    {
                        double x = -1.9 + i * 0.35, z = -1.6 + j * 0.33;
                        var (px, pz, ps, pb, py) = Rects((x, z, x + 1.3, z + 1.4));
                        if (!FPNavMeshClipper.TryBuildClipRings(
                                snap, px, pz, ps, pb, py, 1, out _, out var res))
                            continue;

                        for (int r = 0; r < res.RingCount; r++)
                        {
                            int lo = res.Starts[r], hi = res.Starts[r + 1];
                            for (int k = lo; k < hi; k++)
                            {
                                int k2 = k + 1 < hi ? k + 1 : lo;
                                long ax = res.Xs[k], az = res.Zs[k];
                                long bx = res.Xs[k2], bz = res.Zs[k2];
                                long minX = System.Math.Min(ax, bx), maxX = System.Math.Max(ax, bx);
                                long minZ = System.Math.Min(az, bz), maxZ = System.Math.Max(az, bz);

                                for (int rr = 0; rr < snap.RingCount; rr++)
                                {
                                    queried++;
                                    bool disjoint = FPNavMeshClipper.RingBoxDisjoint(
                                        snap.RingBounds, rr, minX, minZ, maxX, maxZ);
                                    if (!disjoint) { kept++; continue; }
                                    skipped++;
                                    for (int e = snap.RingStart[rr]; e < snap.RingStart[rr + 1]; e++)
                                    {
                                        var (ea, eb) = snap.RingEdges[e];
                                        long rax = snap.BaseXs[ea], raz = snap.BaseZs[ea];
                                        long rbx = snap.BaseXs[eb], rbz = snap.BaseZs[eb];
                                        bool edgeMeets =
                                            System.Math.Max(rax, rbx) >= minX && System.Math.Min(rax, rbx) <= maxX &&
                                            System.Math.Max(raz, rbz) >= minZ && System.Math.Min(raz, rbz) <= maxZ;
                                        Assert.IsFalse(edgeMeets,
                                            $"ring {rr} was skipped for the segment at ({x:F2}, {z:F2}) "
                                            + $"but its edge {e} meets that segment's box — the prune "
                                            + "dropped an edge the flat scan would have tested");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            // Non-vacuity both ways: the prune has to have been asked (kept > 0) and to have
            // actually skipped something (skipped > 0). A fixture set where it never skips makes
            // the assertion above unreachable.
            Assert.Greater(kept, 0, "the prune never kept a ring — nothing was scanned");
            Assert.Greater(skipped, 0,
                "the prune never skipped a ring, so the implication was never tested");
            TestContext.Out.WriteLine(
                $"ring-box prune: {queried} (segment, ring) pairs — {skipped} skipped, {kept} kept");
        }

        [Test]
        public void ClipRings_TransitionCacheAndPooling_ChangeNothingAboutTheOutput()
        {
            // The cache's whole contract is "replaying an accepted building's transitions produces
            // the same clip as recomputing them". The preview-level net (T-2b) can only see that
            // through a verdict, and a verdict is blunt: shifting a cached X' by one snap unit
            // leaves every verdict on that fixture unchanged and still emits a different ring.
            // Measured — that mutation passes T-2b and fails here.
            //
            // So this compares the RESULTS, element by element, cold against warm.
            int compared = 0, withRings = 0, cachedTx = 0;
            // ONE scratch for the whole sweep, on purpose: every call after the first then finds
            // another sample's leftovers in those buffers, which is the condition FPClipScratch's
            // rent methods exist for. Until this existed nothing in the suite reached the pooled
            // path at all — every clipper test passes `scratch: null`, and the preview tests that do
            // pool compare VERDICTS, which cannot see a ring range that shifted by one. Measured:
            // dropping the leading 0 from RentStarts left the whole suite green.
            var pooled = new FPClipScratch();
            foreach (FPNavMesh mesh in new[] { HoledSlab(), SlantWall(), SlantCorridor(), TwoEdgeWall() })
            {
                var snap = FPNavMeshRebaker.CreateSnapshot(mesh);
                for (int i = 0; i < 22; i++)
                {
                    for (int j = 0; j < 12; j++)
                    {
                        double x = -1.8 + i * 0.33, z = -1.5 + j * 0.31;
                        var footprints = new[]
                        {
                            (-1.9, -1.1, -0.9, 0.2),
                            (x, z, x + 1.2, z + 1.3),
                        };

                        // Warm-up: the first footprint alone, exporting its transitions. This is
                        // what Capture stores after a rebake accepts it.
                        var (wx, wz, ws, wb, wy) = Rects(footprints[0]);
                        if (!FPNavMeshClipper.TryBuildClipRings(
                                snap, wx, wz, ws, wb, wy, 1, out _, out var warm,
                                exportTransitions: true))
                            continue;

                        var (px, pz, ps, pb, py) = Rects(footprints);
                        bool coldOk = FPNavMeshClipper.TryBuildClipRings(
                            snap, px, pz, ps, pb, py, 2, out var coldRej, out var cold);

                        // COPY the cold result before the warm call, and do not simplify this away.
                        // The clip stage's buffers are being moved into a pool
                        // (IMP100/Plan-PreviewClipAlloc.md, stage 2+): once they are, two Results
                        // reference the SAME arrays and every comparison below becomes `x == x` —
                        // green no matter what the replay does. `IdentityBuilding` is the first to
                        // go, because it is the one field assigned without a `ToArray()`.
                        //
                        // The check that this copy is still doing its job: shift a cached X′ by one
                        // snap unit in the replay and this test must fail. If it passes, the copies
                        // have stopped isolating and this test is measuring nothing.
                        int coldRingCount = cold.RingCount;
                        int[] coldStarts = cold.Starts?.ToArray();
                        long[] coldXs = cold.Xs?.ToArray();
                        long[] coldZs = cold.Zs?.ToArray();
                        FP64[] coldYs = cold.Ys?.ToArray();
                        int[] coldGroupRoot = cold.GroupRoot?.ToArray();
                        bool[] coldIdentity = cold.IdentityBuilding?.ToArray();

                        bool warmOk = FPNavMeshClipper.TryBuildClipRings(
                            snap, px, pz, ps, pb, py, 2, out var warmRej, out var res,
                            warm.Transitions, warm.TransitionStart, 1);
                        // Same inputs, same cache, through the reused buffers.
                        bool pooledOk = FPNavMeshClipper.TryBuildClipRings(
                            snap, px, pz, ps, pb, py, 2, out var pooledRej, out var pooledRes,
                            warm.Transitions, warm.TransitionStart, 1, scratch: pooled);

                        string at = $"({x:F2}, {z:F2}) on {mesh.Triangles.Length} tris";
                        Assert.AreEqual(coldOk, warmOk, $"cache flipped the verdict at {at}");
                        Assert.AreEqual(coldRej.Reason, warmRej.Reason, $"cache changed the reason at {at}");
                        Assert.AreEqual(coldOk, pooledOk, $"pooling flipped the verdict at {at}");
                        Assert.AreEqual(coldRej.Reason, pooledRej.Reason,
                            $"pooling changed the reason at {at}");
                        compared++;
                        cachedTx += warm.TransitionStart[1];
                        if (!coldOk) continue;

                        Assert.AreEqual(coldRingCount, res.RingCount, $"ring count differs at {at}");
                        Assert.AreEqual(coldStarts, res.Starts, $"ring ranges differ at {at}");
                        Assert.AreEqual(coldXs, res.Xs, $"ring X differs at {at}");
                        Assert.AreEqual(coldZs, res.Zs, $"ring Z differs at {at}");
                        Assert.AreEqual(coldYs, res.Ys, $"ring Y differs at {at}");
                        Assert.AreEqual(coldGroupRoot, res.GroupRoot, $"group roots differ at {at}");
                        Assert.AreEqual(coldIdentity, res.IdentityBuilding,
                            $"identity flags differ at {at}");

                        Assert.AreEqual(coldRingCount, pooledRes.RingCount,
                            $"pooled ring count differs at {at}");
                        Assert.AreEqual(coldStarts, pooledRes.Starts,
                            $"pooled ring ranges differ at {at} — a stale or missing initial state "
                            + "in the reused buffers");
                        Assert.AreEqual(coldXs, pooledRes.Xs, $"pooled ring X differs at {at}");
                        Assert.AreEqual(coldZs, pooledRes.Zs, $"pooled ring Z differs at {at}");
                        Assert.AreEqual(coldYs, pooledRes.Ys, $"pooled ring Y differs at {at}");
                        Assert.AreEqual(coldGroupRoot, pooledRes.GroupRoot,
                            $"pooled group roots differ at {at}");
                        Assert.AreEqual(coldIdentity, pooledRes.IdentityBuilding,
                            $"pooled identity flags differ at {at}");
                        withRings += coldRingCount;
                    }
                }
            }
            // Non-vacuity twice over: the comparison has to have run, and the cached building has
            // to have had transitions to replay — a warm-up that exports an empty list makes the
            // cache path identical to the cold one for trivial reasons.
            Assert.Greater(compared, 0, "no placement pair was comparable — the sweep found nothing");
            Assert.Greater(withRings, 0, "no clip ring was ever emitted, so nothing was compared");
            Assert.Greater(cachedTx, 0,
                "the cached building never had a transition — the replay path was never exercised");
            TestContext.Out.WriteLine(
                $"cache vs recompute vs pooled: {compared} triples identical, {withRings} rings, "
                + $"{cachedTx} transitions replayed");
        }

        [Test]
        public void ClipRings_RingPruning_IsPureFilter()
        {
            // The ring-AABB prunes (①′) are the one optimisation in this stage that could be
            // silently WRONG rather than merely slow: a ring wrongly skipped takes its crossings
            // with it, and every other check here would still pass. Nothing else in the suite
            // looks at that — T-2's preview-vs-rebake comparison runs both sides through the
            // same prunes, so it agrees about a wrong answer.
            //
            // So this pins the equivalence directly, against a reference parity/scan that reads
            // EVERY ring. A sweep rather than a case: the prune's answer depends on where the
            // footprint sits relative to each ring's box, and a hand-picked spot exercises one
            // arrangement.
            int multiRing = -1;
            foreach (FPNavMesh mesh in new[] { HoledSlab(), SlantWall(), SlantCorridor(), TwoEdgeWall() })
            {
                var snap = FPNavMeshRebaker.CreateSnapshot(mesh);

                // The invariant every ring prune rests on — ①″ in ValidateClipRings included, and
                // that one has no reference form to compare against (its reference is 60 lines of
                // crossing checks). RingBounds must be the TIGHT box of exactly the edges RingStart
                // delimits; given that, "a segment whose box misses the ring's box misses every
                // edge in the ring" is arithmetic rather than a claim.
                for (int r = 0; r < snap.RingCount; r++)
                {
                    long bx0 = long.MaxValue, bz0 = long.MaxValue, bx1 = long.MinValue, bz1 = long.MinValue;
                    for (int e = snap.RingStart[r]; e < snap.RingStart[r + 1]; e++)
                    {
                        foreach (int v in new[] { snap.RingEdges[e].a, snap.RingEdges[e].b })
                        {
                            bx0 = System.Math.Min(bx0, snap.BaseXs[v]);
                            bx1 = System.Math.Max(bx1, snap.BaseXs[v]);
                            bz0 = System.Math.Min(bz0, snap.BaseZs[v]);
                            bz1 = System.Math.Max(bz1, snap.BaseZs[v]);
                        }
                        Assert.AreEqual(r, snap.RingOfEdge[e], $"edge {e} is filed under the wrong ring");
                    }
                    Assert.AreEqual(bx0, snap.RingBounds[r * 4], $"ring {r} minX is not tight");
                    Assert.AreEqual(bz0, snap.RingBounds[r * 4 + 1], $"ring {r} minZ is not tight");
                    Assert.AreEqual(bx1, snap.RingBounds[r * 4 + 2], $"ring {r} maxX is not tight");
                    Assert.AreEqual(bz1, snap.RingBounds[r * 4 + 3], $"ring {r} maxZ is not tight");
                }

                int probed = 0, emitted = 0;
                for (int i = 0; i < 26; i++)
                {
                    for (int j = 0; j < 14; j++)
                    {
                        double x = -2.0 + i * 0.31, z = -1.7 + j * 0.29;
                        var (px, pz, ps, pb, py) = Rects((x, z, x + 1.4, z + 1.5));

                        bool ok = FPNavMeshClipper.TryBuildClipRings(
                            snap, px, pz, ps, pb, py, 1, out var rej, out var res);
                        probed++;
                        if (ok) emitted += res.RingCount;

                        // Reference: the senses step's parity, read over every ring. If the pruned
                        // form ever disagrees the walk starts from a flipped sense and the emitted
                        // ring is a different shape — so this single value is the sharp end of the
                        // three prunes.
                        bool full = FPNavMeshRebaker.PointInRingsParityRaw(
                            px[ps[0]], pz[ps[0]], snap.BaseXs, snap.BaseZs, snap.RingEdges);
                        bool pruned = FPNavMeshRebaker.PointInRingsParityPruned(
                            px[ps[0]], pz[ps[0]], snap.BaseXs, snap.BaseZs, snap.RingEdges,
                            snap.RingStart, snap.RingBounds, snap.RingCount);
                        Assert.AreEqual(full, pruned,
                            $"pruned parity disagrees at ({x:F2}, {z:F2}) — a ring was skipped that "
                            + "contributes a crossing");

                        // And the decomposition itself: every edge's ring, and the ranges that
                        // define them, have to agree with walking the chains from scratch.
                        Assert.AreEqual(snap.RingEdges.Count, snap.RingStart[snap.RingCount],
                            "ring ranges do not cover the edge list");
                    }
                }
                Assert.Greater(emitted, 0,
                    "no clip ring was emitted on this fixture — the sweep is not reaching the walls");
                if (multiRing < 0) multiRing = 0;
                if (snap.RingCount > 1) multiRing++;
                TestContext.Out.WriteLine(
                    $"ring pruning: {probed} placements, {emitted} rings, {snap.RingCount} rings in the base");
            }
            // Non-vacuity: a prune that filters BY RING proves nothing on a single-ring base — it
            // always keeps the one ring there is. Every fixture here was single-ring when this test
            // was first written, and it passed.
            Assert.Greater(multiRing, 0,
                "no fixture had more than one boundary ring, so the ring prunes were never asked to "
                + "skip anything");
        }

        [Test]
        public void Determinism_SameInput_SameRings()
        {
            var inputs = new[] { (1.0, -1.0, 2.0, 1.0), (3.5, -1.0, 4.5, 1.0) };
            Assert.IsTrue(Clip(SlantWall(), inputs, out _, out var r1));
            Assert.IsTrue(Clip(SlantWall(), inputs, out _, out var r2));
            CollectionAssert.AreEqual(r1.Xs, r2.Xs);
            CollectionAssert.AreEqual(r1.Zs, r2.Zs);
            CollectionAssert.AreEqual(r1.Starts, r2.Starts);
        }
    }
}
