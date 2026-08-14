using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The context-form preview: same verdict as the list-taking form, without being handed a list.
    ///
    /// <para>The list form has a precondition nothing checks — the set you pass must be one the
    /// rebaker already accepted — and breaking it produces a confident wrong answer rather than an
    /// error. The context form removes it by remembering what it carved. That only helps if what it
    /// remembers is right, which is what these gates are for: every failure here is silent, and
    /// most of them look like the game being fussy about where you may build.</para>
    /// </summary>
    [TestFixture]
    public class FPBuildingPreviewContextTests
    {
        #region Fixture

        private static FPNavMesh BuildSlab() => BuildGrid(10, 5, hole: false);

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

        /// <summary>
        /// 16x16 with a [-2,2] pillar — the only fixture here that can produce a swallowed ring.
        ///
        /// <para>Copied from <c>FPBuildingRejectionGoldenTests</c> rather than reusing the
        /// hole variant of BuildGrid above, and the difference matters: that one constrains only
        /// the pillar loop, so the erase pass takes the pillar for the outer boundary and leaves a
        /// 4x4 map. Every placement outside it is then refused as OUTSIDE, which two forms will
        /// happily agree on while never reaching the check the fixture was named for.</para>
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

        private static FPBuildingRect R(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static FPNavMeshRebakeContext Ctx(FPNavMesh m, FPBuildingShapeCatalog catalog = null) =>
            new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(m, null, prewarm: false, catalog));

        /// <summary>Carries the context through a rebake of <paramref name="placed"/>.</summary>
        private static FPNavMeshRebakeContext Carve(
            FPNavMeshRebakeContext ctx, FPBuildingRect[] placed,
            FPBuildingPlacementRules rules = default)
        {
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, placed, null, rules));
            return ctx;
        }

        /// <summary>
        /// The two forms side by side. The list form is fed exactly what the context carved, which
        /// is the only input for which the two are supposed to agree.
        /// </summary>
        private static void AssertFormsAgree(
            FPNavMeshRebakeContext ctx, FPBuildingRect[] placed, FPBuildingRect ghost, string what,
            FPBuildingRejection expected, FPBuildingPlacementRules rules = default)
        {
            bool okList = FPNavMeshRebaker.TryValidateOne(
                ctx.Snapshot, placed, placed.Length, ghost, out var infoList,
                new FPBuildingPreviewScratch(), rules);
            bool okCtx = FPNavMeshRebaker.TryValidateOne(ctx, ghost, out var infoCtx, rules);

            // Named, not just matched. Two forms agreeing on the WRONG reason is a green test that
            // proves nothing — and the fixture that produces a given reason is easy to get wrong.
            Assert.AreEqual(expected, infoList.Reason, $"{what}: the fixture reaches a different check");
            Assert.AreEqual(okList, okCtx, $"{what}: accept/refuse differs");
            Assert.AreEqual(infoList.Reason, infoCtx.Reason, $"{what}: reason differs");
            Assert.AreEqual(infoList.IndexA, infoCtx.IndexA, $"{what}: IndexA differs");
            Assert.AreEqual(infoList.IndexB, infoCtx.IndexB, $"{what}: IndexB differs");
            Assert.AreEqual(infoList.Site, infoCtx.Site, $"{what}: Site differs");
        }

        #endregion

        // ── W1 — context form ≡ list form, per reason ────────────────────────────

        [Test]
        public void W1_MatchesTheListForm_OnEveryReachableReason()
        {
            // Per reason rather than "a few cases": the two forms share the checks but not the
            // storage, and the storage is where they can differ. SwallowsBakedHole is the one that
            // reads an AABB, and the accepted set deliberately keeps only the ghost's — so that
            // reason is not one case among five, it is the case that tests the decision.
            var placed = new[] { R(-9, -9, -7, -7) };

            var slab = Carve(Ctx(BuildSlab()), placed);
            AssertFormsAgree(slab, placed, R(-4, -4, -2, -2), "accepted", FPBuildingRejection.None);
            AssertFormsAgree(slab, placed, R(-9, -9, -7, -7), "overlap", FPBuildingRejection.BuildingsOverlap);
            AssertFormsAgree(slab, placed, R(20, 20, 22, 22), "outside", FPBuildingRejection.OutsideWalkableRegion);
            // Opposite corner from the placed building: expanded, this reaches x = z = 10 exactly.
            // Put it next to the placed one and the separation check answers first — which is
            // correct, and would have made this case a second overlap test in disguise.
            AssertFormsAgree(slab, placed, R(8, 8, 9.5, 9.5), "boundary", FPBuildingRejection.TouchesWalkableBoundary);

            // Clear of the ghost once both are expanded — at (5,5) they meet at exactly (4.5, 4.5)
            // and the separation check answers before the base mesh is ever consulted.
            var near = new[] { R(6, 6, 7, 7) };
            var ring = Carve(Ctx(BuildAnnulus()), near);
            AssertFormsAgree(ring, near, R(-4, -4, 4, 4), "swallow", FPBuildingRejection.SwallowsBakedHole);
        }

        [Test]
        public void W1_EmptyContext_MatchesAnEmptyList()
        {
            // W9 in the plan: the first building a player places is the commonest call there is,
            // and it is also the only moment the accepted set is empty.
            var ctx = Ctx(BuildSlab());
            AssertFormsAgree(ctx, Array.Empty<FPBuildingRect>(), R(-4, -4, -2, -2),
                "virgin/accepted", FPBuildingRejection.None);
            AssertFormsAgree(ctx, Array.Empty<FPBuildingRect>(), R(20, 20, 22, 22),
                "virgin/outside", FPBuildingRejection.OutsideWalkableRegion);
        }

        [Test]
        public void W1_RestackingOnAStandingBuildingIsRefused_UnderEitherRule()
        {
            // The commonest click a placement UI sees: the player aims at a building that is
            // already there. Worth pinning on its own because contact-allowed is the setting where
            // it could plausibly slip through — the ghost and the standing building share every
            // edge, and "sharing an edge" is exactly what that rule permits.
            //
            // It does not slip through. Two coincident polygons project to the same interval on
            // every axis, so the touch test's `aMax <= bMin` reduces to `aMax <= aMin`, which no
            // rectangle satisfies. Contact means a shared boundary, not a shared interior — and the
            // message the strict path formats says so.
            FPBuildingRect standing = R(-4, -4, -2, -2);

            foreach (bool touch in new[] { false, true })
            {
                var rules = new FPBuildingPlacementRules(allowBuildingTouch: touch);
                var ctx = Carve(Ctx(BuildSlab()), new[] { standing }, rules);

                Assert.IsFalse(FPNavMeshRebaker.TryValidateOne(ctx, standing, out var info),
                    $"allowBuildingTouch={touch}: a building was allowed on top of another");
                Assert.AreEqual(FPBuildingRejection.BuildingsOverlap, info.Reason);
                Assert.AreEqual(0, info.IndexA, "the standing building — what the UI points at");
                Assert.AreEqual(1, info.IndexB, "the ghost");
            }
        }

        // ── W2 — a refused rebake must not move the cache ────────────────────────

        [Test]
        public void W2_ARefusedRebakeLeavesThePreviewAnswerAlone()
        {
            // Looks tautological — the copy happens on the success path — and is not. If the cache
            // held a REFERENCE to the rebake's buffers instead of a copy, the refused rebake below
            // would already have overwritten them: BuildRectPolygons fills those buffers BEFORE
            // anything is validated, so returning null afterwards restores nothing.
            //
            // The refused set is placed where the ghost is, so a cache that took it would refuse
            // the ghost. That is the difference this test reads.
            var placed = new[] { R(-9, -9, -7, -7) };
            var ctx = Carve(Ctx(BuildSlab()), placed);
            FPBuildingRect ghost = R(-4, -4, -2, -2);

            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, ghost, out _),
                "precondition: the ghost is placeable against the carved set");

            bool rebaked = FPNavMeshRebaker.TryRebake(
                ctx, new[] { R(-4, -4, -2, -2), R(-3, -3, -1, -1) }, out _, out var why);
            Assert.IsFalse(rebaked, "fixture: that pair must be refused");
            Assert.AreEqual(FPBuildingRejection.BuildingsOverlap, why.Reason);

            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, ghost, out _),
                "the refused set reached the cache — the preview is now answering about buildings "
                + "that were never carved");
        }

        // ── W8 — the set shrinks, and the ghost slot does not leak ───────────────

        [Test]
        public void W8_ADemolitionShrinksTheSet()
        {
            // The known failure mode of every oversized buffer in this repository: read it through
            // Length and the previous call's contents are still live. Here that would be a
            // demolished building still refusing placements — invisible, and permanent.
            var five = new[]
            {
                R(-9, -9, -7, -7), R(-9, -4, -7, -2), R(-9, 1, -7, 3),
                R(-4, -9, -2, -7), R(-4, -4, -2, -2),
            };
            var three = new[] { five[0], five[1], five[2] };

            var ctx = Carve(Ctx(BuildSlab()), five);
            Assert.IsFalse(FPNavMeshRebaker.TryValidateOne(ctx, five[4], out _),
                "precondition: that spot is taken while five are standing");

            Carve(ctx, three);
            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, five[4], out _),
                "the demolished buildings are still refusing placements");

            AssertFormsAgree(ctx, three, five[4], "after demolition", FPBuildingRejection.None);
        }

        [Test]
        public void W8_ConsecutivePreviewsDoNotSeeEachOther()
        {
            // The ghost occupies slot N of the same arrays the accepted set lives in. If a count
            // were ever read as N+1, the previous frame's cursor position would stand there as a
            // building — the ghost would start refusing itself as the player moved.
            var placed = new[] { R(-9, -9, -7, -7) };
            var ctx = Carve(Ctx(BuildSlab()), placed);

            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, R(-4, -4, -2, -2), out _));
            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, R(-3, -3, -1, -1), out _),
                "the previous ghost is still standing in the way");
            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, R(-4, -4, -2, -2), out _),
                "and it is not order-dependent either");
        }

        [Test]
        public void W8_AHexagonGhostFitsACacheFilledByRectangles()
        {
            // The accepted set holds polygons, not rects, so the ghost's shape need not match what
            // was carved. It also means the headroom cannot be read off what is already there: four
            // vertices per building is what rectangles need, and a hexagon needs six.
            var catalog = Hexagon();
            var ctx = Carve(Ctx(BuildSlab(), catalog), new[] { R(-9, -9, -7, -7) });

            var ghost = new FPBuildingPlacement(0, 0, FP64.Zero, FP64.Zero, FP64.Zero);
            Assert.IsTrue(FPNavMeshRebaker.TryValidateOnePlacement(ctx, ghost, out var info),
                $"a hexagon in open ground was refused: {info.Reason}");
        }

        /// <summary>One hexagon, six vertices — enough to be wider than a rectangle.</summary>
        private static FPBuildingShapeCatalog Hexagon()
        {
            var offX = new long[6];
            var offZ = new long[6];
            // Radius 2, on the 1/1024 grid: the catalog's offsets are integers about the centre.
            var pts = new (double x, double z)[]
            {
                (2, 0), (1, 1.75), (-1, 1.75), (-2, 0), (-1, -1.75), (1, -1.75),
            };
            for (int i = 0; i < 6; i++)
            {
                offX[i] = FPGeoPredicates.Snap(FP64.FromDouble(pts[i].x));
                offZ[i] = FPGeoPredicates.Snap(FP64.FromDouble(pts[i].z));
            }
            return new FPBuildingShapeCatalog(offX, offZ, new[] { 0, 6 }, new[] { 0, 1 });
        }

        // ── W7 — the recorded rules, and the override ────────────────────────────

        [Test]
        public void W7_RulesComeFromTheCarvingRebake_AndCanBeOverridden()
        {
            // Two rects separated by exactly twice the bake radius, so their EXPANDED footprints
            // meet along one edge and overlap nowhere — the only configuration the touch rule
            // actually decides.
            FPBuildingRect a = R(-6, -6, -4, -4), b = R(-3, -6, -1, -4);
            var touch = new FPBuildingPlacementRules(allowBuildingTouch: true);

            var ctx = Carve(Ctx(BuildSlab()), new[] { a }, touch);

            Assert.IsTrue(FPNavMeshRebaker.TryValidateOne(ctx, b, out _),
                "with no rules passed, the preview must use the ones the rebake ran under");
            // Spelled out rather than `default`: this parameter is FPBuildingPlacementRules?, so
            // `default` is null — "use what was recorded" — and not the strict ruleset that the
            // same word means on the snapshot-form overloads. Worth knowing when porting a call.
            Assert.IsFalse(
                FPNavMeshRebaker.TryValidateOne(
                    ctx, b, out _, new FPBuildingPlacementRules(allowBuildingTouch: false)),
                "an explicitly passed rules value must win over the recorded one");

            // And the fixture straddles the rule, or neither assertion above proves anything.
            var strict = Carve(Ctx(BuildSlab()), new[] { a });
            Assert.IsFalse(FPNavMeshRebaker.TryValidateOne(strict, b, out _),
                "fixture: the default rules must refuse this pair");
        }

        // ── W3 / W6 — allocation ─────────────────────────────────────────────────

        [Test]
        public void W3_RebakeThenTwoPreviews_AllocatesNothingAfterTheFirst()
        {
            // Rebake first, deliberately. Measuring two previews back to back would miss a cache
            // sized to the accepted vertices with no room for a ghost: that one grows on the FIRST
            // preview after each rebake and is quiet forever after.
            var placed = new[] { R(-9, -9, -7, -7) };
            var ctx = Carve(Ctx(BuildSlab()), placed);
            FPBuildingRect g1 = R(-4, -4, -2, -2), g2 = R(-3, -3, -1, -1);

            for (int i = 0; i < 4; i++)
            {
                FPNavMeshRebaker.TryValidateOne(ctx, g1, out _);
                FPNavMeshRebaker.TryValidateOne(ctx, g2, out _);
            }

            Carve(ctx, placed);
            long before = GC.GetAllocatedBytesForCurrentThread();
            FPNavMeshRebaker.TryValidateOne(ctx, g1, out _);
            FPNavMeshRebaker.TryValidateOne(ctx, g2, out _);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Less(alloc, 64,
                $"a preview allocated {alloc} B — the point of this form is that the caller keeps "
                + "no list, which is worth nothing if the engine allocates instead");
        }

        [Test]
        public void W6_RepeatedRebakesAtTheSameSizeDoNotGrowTheCache()
        {
            // The cache is grow-only and every context has one, servers included. If it grew per
            // rebake it would be a leak per room, and the existing perf tests report rebake
            // allocation without asserting on it.
            var placed = new[] { R(-9, -9, -7, -7), R(-9, -4, -7, -2) };
            var ctx = Carve(Ctx(BuildSlab()), placed);

            // Identity, not a byte count. A rebake allocates plenty on its own and the cache's
            // share of it is small enough to hide in the noise; whether the same arrays came back
            // is the actual question, and the test assembly can just look.
            long[] x = ctx.Accepted.PolyX;
            long[] z = ctx.Accepted.PolyZ;
            int[] starts = ctx.Accepted.PolyStart;

            for (int i = 0; i < 5; i++) Carve(ctx, placed);

            Assert.AreSame(x, ctx.Accepted.PolyX, "the cache reallocated PolyX on a rebake that needed no more room");
            Assert.AreSame(z, ctx.Accepted.PolyZ, "the cache reallocated PolyZ");
            Assert.AreSame(starts, ctx.Accepted.PolyStart, "the cache reallocated PolyStart");

            // And a preview does not grow it either — that is where the ghost headroom pays off.
            FPNavMeshRebaker.TryValidateOne(ctx, R(-4, -4, -2, -2), out _);
            Assert.AreSame(x, ctx.Accepted.PolyX, "a preview grew the cache — the headroom is missing");
        }

        // ── W10 — a malformed ghost still throws ─────────────────────────────────

        [Test]
        public void W10_AMalformedGhostThrowsHereToo()
        {
            // Automatic, because both forms write the polygon with the same function. Gated anyway:
            // "automatic" is precisely the kind of thing that stops being true without a red test,
            // and the distinction it protects — the player's fault comes back as a value, the
            // game's own fault throws — is the one this API is built around.
            var ctx = Carve(Ctx(BuildSlab()), new[] { R(-9, -9, -7, -7) });

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOne(ctx, R(1, 1, 1, 1), out _),
                "a zero-area ghost is a malformed request, not a refused placement");

            var withCatalog = Carve(Ctx(BuildSlab(), Hexagon()), new[] { R(-9, -9, -7, -7) });
            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOnePlacement(
                    withCatalog, new FPBuildingPlacement(7, 0, FP64.Zero, FP64.Zero, FP64.Zero), out _),
                "shape 7 is not in a one-shape catalog");

            Assert.Throws<ArgumentException>(
                () => FPNavMeshRebaker.TryValidateOnePlacement(
                    ctx, new FPBuildingPlacement(0, 0, FP64.Zero, FP64.Zero, FP64.Zero), out _),
                "this stage has no catalog at all");
        }
    }
}
