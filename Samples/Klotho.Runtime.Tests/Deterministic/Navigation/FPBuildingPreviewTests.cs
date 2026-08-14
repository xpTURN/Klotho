using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The placement-preview entry points: same verdict as a rebake, without carving.
    ///
    /// <para>The whole feature rests on one claim — that preview and rebake cannot disagree,
    /// because they run the same validation and only differ in whether the carve follows. These
    /// tests are that claim. A drift here would not throw and would not desync; it would just
    /// paint a ghost the wrong colour, which is exactly the kind of wrong that ships.</para>
    /// </summary>
    [TestFixture]
    public class FPBuildingPreviewTests
    {
        #region Fixture

        /// <summary>Plain 20x20 slab.</summary>
        private static FPNavMesh BuildSlab() => BuildGrid(10, 5, hole: false);

        /// <summary>
        /// 16x16 with a [-2,2] pillar — the fixture the swallow case needs.
        ///
        /// <para>Constraining only the pillar loop does NOT give one: the erase pass then takes the
        /// pillar for the outer boundary and leaves a 4x4 map, on which every placement outside it
        /// is refused as OUTSIDE. Preview and rebake agree on that perfectly well while never
        /// reaching the swallow check at all — which is what this fixture used to do, and what the
        /// reason assertion in AssertSameVerdict now catches.</para>
        /// </summary>
        private static FPNavMesh BuildAnnulus()
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
            Ring((-2, -2), (0, -2), (2, -2), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0));

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        private static FPNavMesh BuildGrid(int half, int step, bool hole)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -half; x <= half; x += step)
                for (int z = -half; z <= half; z += step)
                    pts.Add((x, z));

            var index = new Dictionary<(int, int), int>();
            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
                index[pts[i]] = i;
            }

            List<int> constraints = null;
            if (hole)
            {
                constraints = new List<int>();
                var loop = new[] { (-2, -2), (2, -2), (2, 2), (-2, 2) };
                for (int i = 0; i < loop.Length; i++)
                {
                    constraints.Add(index[loop[i]]);
                    constraints.Add(index[loop[(i + 1) % loop.Length]]);
                }
            }

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints?.ToArray(), eraseOuterAndHoles: hole);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        private static FPBuildingRect R(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static FPNavMeshRebakeSnapshot Snap(FPNavMesh m) =>
            FPNavMeshRebaker.CreateSnapshot(m, null, prewarm: false);

        /// <summary>What the rebake says, as a (accepted, reason payload) pair.</summary>
        private static (bool ok, FPBuildingRejectionInfo info) ViaRebake(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] rects, FPBuildingPlacementRules rules = default)
        {
            var ctx = new FPNavMeshRebakeContext(snapshot);
            bool ok = FPNavMeshRebaker.TryRebake(ctx, rects, out _, out var info, null, rules);
            return (ok, info);
        }

        /// <summary>
        /// The preview's answer for the LAST rect in <paramref name="rects"/>, checked against the
        /// ones before it.
        ///
        /// <para>That split is the only one the ghost form can be asked about: it takes an already
        /// accepted set plus the building being placed. Every caller below therefore has to hand it
        /// a prefix the rebaker really would accept — which is a constraint on the fixtures, and
        /// the reason each case here puts its violation in the last rect.</para>
        /// </summary>
        private static (bool ok, FPBuildingRejectionInfo info) ViaPreview(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] rects, FPBuildingPlacementRules rules = default)
        {
            var scratch = new FPBuildingPreviewScratch();
            var accepted = new FPBuildingRect[rects.Length - 1];
            Array.Copy(rects, accepted, accepted.Length);
            bool ok = FPNavMeshRebaker.TryValidateOne(
                snapshot, accepted, accepted.Length, rects[rects.Length - 1],
                out var info, scratch, rules);
            return (ok, info);
        }

        private static void AssertSameVerdict(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] rects, string what,
            FPBuildingRejection expected, FPBuildingPlacementRules rules = default)
        {
            var (okR, infoR) = ViaRebake(snapshot, rects, rules);
            var (okP, infoP) = ViaPreview(snapshot, rects, rules);

            // Named, not merely matched. Two forms agreeing on the WRONG reason is a green test
            // that proves nothing, and the fixture that produces a given reason is easy to get
            // wrong — this repository has shipped one that never reached the check it was named
            // for, and nothing noticed until the reason was asserted.
            Assert.AreEqual(expected, infoR.Reason, $"{what}: the fixture reaches a different check");
            Assert.AreEqual(okR, okP, $"{what}: accept/refuse differs");
            Assert.AreEqual(infoR.Reason, infoP.Reason, $"{what}: reason differs");
            Assert.AreEqual(infoR.IndexA, infoP.IndexA, $"{what}: IndexA differs");
            Assert.AreEqual(infoR.IndexB, infoP.IndexB, $"{what}: IndexB differs");
            Assert.AreEqual(infoR.Site, infoP.Site, $"{what}: Site differs");
        }

        #endregion

        // ── V2 — preview ≡ rebake ────────────────────────────────────────────────

        [Test]
        public void V2_MatchesRebake_OnEveryReachableReason()
        {
            FPNavMeshRebakeSnapshot slab = Snap(BuildSlab());

            AssertSameVerdict(slab, new[] { R(-4, -4, -2, -2) },
                "accepted", FPBuildingRejection.None);
            AssertSameVerdict(slab, new[] { R(-4, -4, 0, 0), R(-2, -2, 2, 2) },
                "overlap", FPBuildingRejection.BuildingsOverlap);
            AssertSameVerdict(slab, new[] { R(20, 20, 22, 22) },
                "outside", FPBuildingRejection.OutsideWalkableRegion);
            AssertSameVerdict(slab, new[] { R(-10, -10, -8, -8) },
                "boundary", FPBuildingRejection.TouchesWalkableBoundary);

            AssertSameVerdict(Snap(BuildAnnulus()), new[] { R(-4, -4, 4, 4) },
                "swallow", FPBuildingRejection.SwallowsBakedHole);
        }

        [Test]
        public void V2_SimultaneousViolations_ReportTheSameReason()
        {
            // The order the checks run in is a contract, not an accident: with more than one
            // reason applicable, which one comes back is what the player is told. Splitting the
            // validation out of the rebake could have reordered it while leaving every
            // accept/refuse verdict — and every fingerprint — untouched.
            // Both violations must be the LAST rect's, because the preview is told the ones
            // before it were accepted. Two rects that both break something — which is what this
            // test used to use — is no longer a legal thing to ask.
            FPNavMeshRebakeSnapshot slab = Snap(BuildSlab());

            // Overlaps the standing building AND reaches the map edge.
            AssertSameVerdict(slab, new[] { R(-9, -9, -7, -7), R(-10, -10, -8, -8) },
                "overlap + boundary", FPBuildingRejection.BuildingsOverlap);

            // Overlaps the standing building AND swallows the pillar ring.
            AssertSameVerdict(Snap(BuildAnnulus()), new[] { R(6, 6, 7, 7), R(-4, -4, 7, 7) },
                "overlap + swallow", FPBuildingRejection.BuildingsOverlap);
        }

        [Test]
        public void V2_RulesAreHonoured_BothWays()
        {
            // The preview has to see the same rules value, and the test has to prove the value
            // actually reaches it — otherwise §6-⑸'s false-red trap would be invisible here.
            FPNavMeshRebakeSnapshot slab = Snap(BuildSlab());
            // A gap of exactly 2 x bakeAgentRadius (0.5 each), so the two EXPANDED footprints
            // meet along x = -3.5 and nowhere overlap. Sharing an edge before expansion would not
            // do — that expands into a real 1.0-wide overlap, which no rule allows.
            var touching = new[] { R(-6, -6, -4, -4), R(-3, -6, -1, -4) };

            AssertSameVerdict(slab, touching, "touching, contact forbidden",
                FPBuildingRejection.BuildingsOverlap);
            AssertSameVerdict(slab, touching, "touching, contact allowed",
                FPBuildingRejection.None, new FPBuildingPlacementRules(allowBuildingTouch: true));

            var (okStrict, _) = ViaPreview(slab, touching, default);
            var (okLoose, _) = ViaPreview(slab, touching,
                new FPBuildingPlacementRules(allowBuildingTouch: true));
            Assert.AreNotEqual(okStrict, okLoose,
                "the fixture must actually straddle the rule, or this test proves nothing");
        }

        // ── V2b — a malformed request still throws ───────────────────────────────

        [Test]
        public void V2b_MalformedRequest_ThrowsInThePreviewToo()
        {
            // The line the rejection design is drawn along: what a player caused comes back as a
            // value, what the game got wrong throws. If the preview softened a bug into `false`,
            // the game would paint it as "you cannot build there" — on every peer, silently.
            FPNavMeshRebakeSnapshot slab = Snap(BuildSlab());
            var scratch = new FPBuildingPreviewScratch();

            var none = Array.Empty<FPBuildingRect>();
            var one = new[] { R(-8, -8, -6, -6) };

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOne(slab, none, 0, R(1, 1, 1, 1), out _, scratch),
                "a zero-area rect is a malformed request, not a refused placement");

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOne(slab, one, 5, R(4, 4, 6, 6), out _, scratch),
                "a count past the array is a malformed request");

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOne(null, one, 1, R(4, 4, 6, 6), out _, scratch),
                "a null snapshot is a malformed request");

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOne(slab, one, 1, R(4, 4, 6, 6), out _, null),
                "a null scratch is a malformed request");
        }

        // ── V3 — the preview does not allocate ───────────────────────────────────

        [Test]
        public void V3_ReusedScratch_AllocatesNothing()
        {
            // Not a performance nicety — a contract. This runs as the cursor moves, so an
            // allocation per call is an allocation per frame.
            FPNavMeshRebakeSnapshot slab = Snap(BuildSlab());
            var scratch = new FPBuildingPreviewScratch();
            var existing = new[] { R(-8, -8, -6, -6) };
            var ghost = R(4, 4, 6, 6);

            for (int i = 0; i < 64; i++)
                FPNavMeshRebaker.TryValidateOne(slab, existing, 1, ghost, out _, scratch);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++)
                FPNavMeshRebaker.TryValidateOne(slab, existing, 1, ghost, out _, scratch);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0, after - before,
                $"64 previews allocated {after - before} B — the scratch stopped being reused");
        }

        // ── V5 — the snapshot stays clean ────────────────────────────────────────

        [Test]
        public void V5_PreviewDoesNotDirtyTheSnapshot()
        {
            // The preview reads the snapshot the rebake carves from. If it wrote anything there,
            // the damage would show up in a later rebake rather than here — so compare a rebake
            // taken before and after a batch of previews, byte for byte.
            FPNavMeshRebakeSnapshot slab = Snap(BuildSlab());
            var rects = new[] { R(-4, -4, -2, -2) };

            ulong before = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(new FPNavMeshRebakeContext(slab), rects, null));

            var scratch = new FPBuildingPreviewScratch();
            var none = Array.Empty<FPBuildingRect>();
            FPNavMeshRebaker.TryValidateOne(slab, none, 0, rects[0], out _, scratch);
            FPNavMeshRebaker.TryValidateOne(slab, none, 0, R(20, 20, 22, 22), out _, scratch);
            FPNavMeshRebaker.TryValidateOne(slab, rects, 1, R(4, 4, 6, 6), out _, scratch);

            ulong after = FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(new FPNavMeshRebakeContext(slab), rects, null));

            Assert.AreEqual(before, after, "a preview changed what the snapshot carves");
        }
    }
}
