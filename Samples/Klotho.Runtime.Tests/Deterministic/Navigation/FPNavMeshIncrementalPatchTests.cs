using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Gates for the incremental build-stage patch — a rebake that PATCHES the previous mesh
    /// instead of rebuilding it.
    ///
    /// <para>Everything here compares an incrementally-produced mesh against one built the full
    /// way, because the patch is only allowed to be faster: the two must be byte-identical.
    /// A peer that joined mid-match rebuilds from scratch while everyone else has been patching,
    /// so any difference between the paths is a desync waiting for the next join.</para>
    ///
    /// <para><b>Every assertion here also checks that the patch actually ran.</b> The guards can
    /// all fall back to the full path, and when they do these comparisons become "full path
    /// versus full path" — green, and measuring nothing. That failure mode is invisible without
    /// the counters.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshIncrementalPatchTests
    {
        #region Fixture

        private static FPNavMesh BuildBase(int half = 20)
        {
            int side = half * 2 + 1;
            var pts = new (int x, int z)[side * side];
            int n = 0;
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                    pts[n++] = (x, z);

            var vertices = new FPVector3[pts.Length];
            var xs = new long[pts.Length];
            var zs = new long[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>A grid of separated 1x1 footprints, so 32 of them still fit the base.</summary>
        private static FPBuildingRect[] Buildings(int count, long jitterSnap = 0)
        {
            var result = new FPBuildingRect[count];
            for (int i = 0; i < count; i++)
            {
                FP64 x = FP64.FromInt(-18 + (i % 8) * 5) + FPGeoPredicates.Unsnap(jitterSnap * (i % 3));
                FP64 z = FP64.FromInt(-8 + (i / 8) * 5) + FPGeoPredicates.Unsnap(jitterSnap * (i % 5));
                result[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
            }
            return result;
        }

        /// <summary>The same set with one entry dropped — which shifts every later building's
        /// corner vertices, unlike dropping from the end.</summary>
        private static FPBuildingRect[] BuildingsWithout(int count, int omit)
        {
            FPBuildingRect[] all = Buildings(count);
            var result = new FPBuildingRect[count - 1];
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                if (i != omit)
                    result[n++] = all[i];
            }
            return result;
        }

        private static ulong Patched(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings)
        {
            FPNavMesh mesh = FPNavMeshRebaker.Rebake(ctx, buildings);
            ulong fp = FPNavMeshRebaker.ComputeFingerprint(mesh);
            ctx.CommitSwap(mesh);
            return fp;
        }

        private static ulong FromScratch(FPNavMesh baseMesh, FPBuildingRect[] buildings) =>
            FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false),
                    buildings, null));

        #endregion

        [Test]
        public void PatchedMeshEqualsFullBuild_ElementByElement_InReleaseToo()
        {
            // The gate that survives a Release build.
            //
            // Everything else here compares fingerprints, and the always-on element-wise check is
            // compiled out when DEBUG is. That leaves Release with no check on this code at all —
            // and a fingerprint is not a substitute: ComputeFingerprint covers the vertices, each
            // triangle's vertex indices, areaMask, costMultiplier and isBlocked. It does not cover
            // NEIGHBOURS, PORTALS or the SPATIAL GRID. A mesh whose adjacency is wrong, or whose
            // grid hides triangles from queries, hashes identical to a correct one.
            //
            // That is not hypothetical: the bug this implementation actually had — a survivor
            // copied from the previous mesh while its vertex indices had shifted underneath —
            // produced matching fingerprints and a different triangle. So this gate compares
            // element by element, and it is not [Explicit] and not DEBUG-only.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            FPNavMeshRebakeSnapshot fresh = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);

            // The sequence has to include a MIDDLE removal. Growing and shrinking by count alone
            // keeps the building corners a prefix of the previous array, which is precisely the
            // case where every guard is a no-op — a gate built only from those steps passes with
            // the guards deleted.
            var sequence = new List<FPBuildingRect[]>
            {
                Buildings(0), Buildings(1), Buildings(4), Buildings(12), Buildings(32),
                Buildings(12),                       // drop from the end — prefix preserved
                BuildingsWithout(12, omit: 3),       // drop from the middle — corners shift
                BuildingsWithout(12, omit: 8),
                Buildings(4), Buildings(1),
            };

            for (int step = 0; step < sequence.Count; step++)
            {
                FPBuildingRect[] buildings = sequence[step];
                int n = buildings.Length;

                FPNavMesh patched = FPNavMeshRebaker.Rebake(ctx, buildings);
                FPNavMesh reference = FPNavMeshRebaker.Rebake(fresh, buildings, null);

                string diff = FPNavMeshBuildPipeline.DescribeFirstDifference(patched, reference);
                Assert.IsNull(diff, $"step {step} ({n} buildings): {diff}");

                // A second, independent net — and a much cheaper one, which is why it now DOES
                // run outside a test: the patch path calls it on every rebake and discards the
                // patch when it fires (0.429 ms inside an 11.645 ms patch rebake on Field, 3.7%,
                // zero alloc).
                // Kept here as well because this call sees the mesh the test drove, before any
                // fallback could have replaced it. Note what it catches that adjacency symmetry
                // cannot: a pairing the patch MISSED leaves both sides saying -1, and "if A says B
                // then B says A" is perfectly satisfied by that.
                string dup = FPNavMeshBuildPipeline.DescribeDuplicateBoundaryEdge(patched);
                Assert.IsNull(dup, $"step {step} ({n} buildings): {dup}");

                ctx.CommitSwap(patched);
            }

            Assert.Greater(ctx.PatchOutcome.Incremental, 0,
                "the patch never ran, so this compared the full path against itself");
            Assert.AreEqual(0, ctx.PatchOutcome.FallbackDuplicateBoundaryEdge,
                "the patch produced a mesh with an edge two triangles both call a boundary, so it "
                + "was discarded and the full build used instead. The output is still correct — "
                + "that is what the fallback is for — but a non-zero count here is a real patch bug "
                + "wearing a performance cost, and nothing else in this suite would notice it");
            Assert.Greater(ctx.PatchOutcome.FallbackVertexShift, 0,
                "no step shifted the vertex array, so the guard that protects the diff key was "
                + "never exercised — delete it and this gate would still pass");
        }

        [Test]
        public void ThePatchIsActuallyFasterThanRebuilding()
        {
            // A gate, not a benchmark. The numbers a benchmark reports belong in the perf fixture;
            // what has to be MAINTAINED is that this code is worth its complexity at all, and that
            // claim needs a check that runs on its own. The perf fixture is [Explicit] at class
            // level, so a threshold parked there would never run — which is exactly how a stale
            // assertion sat failing unnoticed in it before.
            //
            // Expressed as a ratio measured back to back in one process, because an absolute
            // millisecond bound is a statement about the machine, not about the code.
#if DEBUG
            Assert.Ignore("DEBUG builds verify every patched rebake against a full one, so the "
                + "patched path is slower here BY CONSTRUCTION. Timing it would assert the "
                + "opposite of the truth.");
#else
            FPNavMesh baseMesh = BuildBase();
            FPBuildingRect[] buildings = Buildings(8);

            var patchCtx = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            Action patched = () =>
            {
                FPNavMesh m = FPNavMeshRebaker.Rebake(patchCtx, buildings);
                patchCtx.CommitSwap(m);
            };

            // The baseline keeps the pool but never commits, so it always takes the full path —
            // isolating the patch from the pooling it rides on.
            var fullCtx = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            FPNavMeshRebaker.Rebake(fullCtx, buildings);
            Action full = () => FPNavMeshRebaker.Rebake(fullCtx, buildings);

            double Min(Action a)
            {
                for (int i = 0; i < 16; i++)
                    a();
                double best = double.MaxValue;
                var sw = new System.Diagnostics.Stopwatch();
                for (int i = 0; i < 9; i++)
                {
                    sw.Restart();
                    a();
                    sw.Stop();
                    if (sw.Elapsed.TotalMilliseconds < best)
                        best = sw.Elapsed.TotalMilliseconds;
                }
                return best;
            }

            double patchedMs = Min(patched);
            double fullMs = Min(full);

            Assert.Greater(patchCtx.PatchOutcome.Incremental, 0,
                "the patch never ran, so this timed the full path against itself");
            Assert.AreEqual(0, fullCtx.PatchOutcome.Incremental,
                "the baseline patched, so it is not a baseline");

            // Measured at 0.54 on the reference machine; the bound leaves room for a slower or
            // noisier one while still failing if the gain is gone.
            Assert.Less(patchedMs, fullMs * 0.75,
                $"patched {patchedMs:F2} ms vs full {fullMs:F2} ms — the patch is supposed to be "
                + "the reason this code exists");
#endif
        }

        [Test]
        public void TheBuildingListsORDERDecidesWhetherThePatchCanApplyAtAll()
        {
            // The measurement that decides what this optimisation is worth in a real game, and it
            // is not about the patch — it is about how the caller hands its buildings over.
            //
            // Hole vertices are appended in list order, so a list kept in PLACEMENT order grows at
            // the end and every existing index keeps its meaning. A list sorted by POSITION does
            // not: a building placed at a low x lands in the middle, every later building's
            // corners shift down, and the diff key stops meaning anything — the vertex-prefix
            // guard then sends the rebake down the full path.
            //
            // Both orders are equally deterministic, so this is a free choice, and the sample
            // currently makes the expensive one (PlatformerCommandSystem sorts by centre).
            //
            // Rollback rides on the same property: re-executing a tick reverts to an earlier set,
            // which in placement order is a prefix — patchable — and in position order is a middle
            // removal.
            FPNavMesh baseMesh = BuildBase();

            (int accepted, int patched, int shifted) Run(bool sortByPosition)
            {
                var ctx = new FPNavMeshRebakeContext(
                    FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
                var placed = new List<FPBuildingRect>();
                uint state = 12345u;
                int Next(int bound)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    return (int)(state % (uint)bound);
                }

                int accepted = 0;
                for (int i = 0; i < 60; i++)
                {
                    FP64 x = FP64.FromInt(-18 + Next(8) * 5);
                    FP64 z = FP64.FromInt(-8 + Next(4) * 5);
                    placed.Add(new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero));

                    var list = new List<FPBuildingRect>(placed);
                    if (sortByPosition)
                    {
                        list.Sort((a, b) =>
                        {
                            int c = a.MinX.RawValue.CompareTo(b.MinX.RawValue);
                            return c != 0 ? c : a.MinZ.RawValue.CompareTo(b.MinZ.RawValue);
                        });
                    }

                    try
                    {
                        FPNavMesh m = FPNavMeshRebaker.Rebake(ctx, list.ToArray());
                        ctx.CommitSwap(m);
                        accepted++;
                    }
                    catch (Exception) { placed.RemoveAt(placed.Count - 1); }
                }
                return (accepted, ctx.PatchOutcome.Incremental, ctx.PatchOutcome.FallbackVertexShift);
            }

            var byPlacement = Run(sortByPosition: false);
            var byPosition = Run(sortByPosition: true);

            TestContext.Out.WriteLine(
                $"placement order: accepted {byPlacement.accepted}, patched {byPlacement.patched}, "
                + $"vertex-shift fallbacks {byPlacement.shifted}");
            TestContext.Out.WriteLine(
                $"position order:  accepted {byPosition.accepted}, patched {byPosition.patched}, "
                + $"vertex-shift fallbacks {byPosition.shifted}");

            Assert.Greater(byPlacement.patched * 10, byPlacement.accepted * 8,
                "in placement order almost every rebake should patch");
            Assert.Less(byPosition.patched * 2, byPlacement.patched,
                "sorting by position should cost most of the patches — if it stopped doing so, the "
                + "guidance built on this measurement is out of date");
            Assert.Greater(byPosition.shifted, 0,
                "and the reason must be the vertex-prefix guard, not some other fallback");
        }

        [Test]
        public void GridGeometryIsInvariantAcrossRebakes_WhichIsWhyItsGuardNeverFires()
        {
            // The patch rewrites the previous mesh's spatial grid in place, which only works if
            // the grid it was laid out on is still the right one. There IS a guard for that, and
            // this test is not able to make it fire — deliberately, because the guard protects an
            // invariant rather than handling a case.
            //
            // The invariant: the vertex array always carries every base vertex (rebakes filter
            // triangles, never vertices) and a building's corners are strictly inside the walkable
            // region, so the bounds are the base's bounds no matter what is placed. Grid width,
            // height, origin and cell size all follow from those.
            //
            // So what is pinned here is the premise. If a change ever makes bounds move — a
            // placement allowed to reach past the boundary, say — this test fails first, which is
            // a much better place to find out than a silently mis-indexed grid.
            FPNavMesh baseMesh = BuildBase();
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);
            FPNavMesh empty = FPNavMeshRebaker.Rebake(snapshot, Array.Empty<FPBuildingRect>(), null);

            int checkedSets = 0;
            for (int seed = 0; seed < 40; seed++)
            {
                uint state = (uint)seed * 2654435761u + 1u;
                int Next(int bound)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    return (int)(state % (uint)bound);
                }

                int count = 1 + Next(12);
                var buildings = new FPBuildingRect[count];
                for (int i = 0; i < count; i++)
                {
                    FP64 x = FP64.FromInt(-18 + (i % 8) * 5) + FPGeoPredicates.Unsnap(Next(600));
                    FP64 z = FP64.FromInt(-8 + (i / 8) * 5) + FPGeoPredicates.Unsnap(Next(600));
                    buildings[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
                }

                FPNavMesh mesh;
                try { mesh = FPNavMeshRebaker.Rebake(snapshot, buildings, null); }
                catch (Exception) { continue; }
                checkedSets++;

                Assert.AreEqual(empty.GridWidth, mesh.GridWidth, $"seed {seed}: grid width moved");
                Assert.AreEqual(empty.GridHeight, mesh.GridHeight, $"seed {seed}: grid height moved");
                Assert.AreEqual(empty.GridOrigin.x.RawValue, mesh.GridOrigin.x.RawValue,
                    $"seed {seed}: grid origin x moved");
                Assert.AreEqual(empty.GridOrigin.y.RawValue, mesh.GridOrigin.y.RawValue,
                    $"seed {seed}: grid origin y moved");
                Assert.AreEqual(empty.GridCellSize.RawValue, mesh.GridCellSize.RawValue,
                    $"seed {seed}: cell size moved");
            }

            Assert.Greater(checkedSets, 20, "too few placeable sets to say anything");
        }

        [Test]
        public void DegenerateCompactionNeverFiresHere_SoTheDiffNeverMeetsIt()
        {
            // The conforming build path compacts degenerate triangles CONDITIONALLY, and the
            // patch's diff has to run on the result rather than on the raw triangulation — get
            // that wrong and it is right almost always and wrong occasionally, which is the worst
            // shape a bug can have.
            //
            // This test was meant to assert the compaction fires so that interaction is covered.
            // It cannot: the predicate is area < 1e-4 world units squared, vertices sit on the
            // 1/1024 grid, and the thinnest sliver those can form is one snap unit tall — so the
            // base would have to be under 0.2 units, while the closest vertices here are a whole
            // unit apart. Measured across near-touching placements at many sub-unit offsets: zero.
            //
            // So the assertion is inverted. It records that the compaction is unreachable on this
            // input class, and it fails the day that stops being true — at which point the diff's
            // interaction with it becomes live and needs the coverage this test could not give.
            FPNavMesh baseMesh = BuildBase();
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);
            int before = FPNavMeshBuildPipeline.DegenerateCompactions;

            int accepted = 0;
            for (int gap = 1; gap <= 6; gap++)
            {
                for (int off = 0; off < 24; off++)
                {
                    // Expanded by the bake radius on both sides, so the raw gap has to clear 1.0
                    // before anything is accepted — then a hair on top of that.
                    FP64 x0 = FP64.FromInt(-6);
                    FP64 x1 = x0 + FP64.One + FP64.One + FPGeoPredicates.Unsnap(gap);
                    FP64 z = FP64.FromInt(-4) + FPGeoPredicates.Unsnap(off * 41);
                    var pair = new[]
                    {
                        new FPBuildingRect(x0, z, x0 + FP64.One, z + FP64.One, FP64.Zero),
                        new FPBuildingRect(x1, z + FPGeoPredicates.Unsnap(off), x1 + FP64.One,
                            z + FP64.One + FPGeoPredicates.Unsnap(off), FP64.Zero),
                    };
                    try { FPNavMeshRebaker.Rebake(snapshot, pair, null); accepted++; }
                    catch (Exception) { }
                }
            }

            Assert.Greater(accepted, 50,
                $"the sweep only placed {accepted} pairs, so it proves little about reachability");
            Assert.AreEqual(before, FPNavMeshBuildPipeline.DegenerateCompactions,
                "degenerate compaction fired, which it has never done on grid-spaced geometry — "
                + "the incremental diff runs on the compacted array and that interaction is now "
                + "reachable and untested");
        }

        [Test]
        public void PatchRuns_AndAgreesWithTheFullPath_AsBuildingsComeAndGo()
        {
            // The main gate. The sequence goes up AND back down on purpose: removing a building
            // brings carved area back, which is the only way a survivor's boundary edge has to
            // pair with a brand-new triangle. A sequence that only adds never reaches that case.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            foreach (int n in new[] { 0, 1, 8, 32, 8, 1, 0 })
            {
                FPBuildingRect[] buildings = Buildings(n);
                Assert.AreEqual(FromScratch(baseMesh, buildings), Patched(ctx, buildings),
                    $"{n} buildings: the patched mesh must equal the one built from scratch");
            }

            Assert.Greater(ctx.PatchOutcome.Incremental, 0,
                "the patch never ran — every comparison above was full path against full path, so "
                + "this fixture proved nothing. Check the guards before trusting any of it.");
        }

        [Test]
        public void RemovingFromTheMiddle_FallsBackToTheFullPath()
        {
            // The limitation, pinned so it cannot regress into a correctness bug — and pinned
            // precisely, because the obvious statement of it is wrong.
            //
            // The diff matches triangles by vertex-index triple, and building corners are appended
            // after the base vertices in placement order. What breaks that key is not removal as
            // such: dropping the LAST building leaves every remaining corner where it was, so the
            // vertex array stays a prefix and the patch still applies. Dropping one from the
            // MIDDLE shifts every later corner down, and then an unchanged-looking triple names
            // different geometry — a triangle with the right indices and the wrong shape.
            //
            // The vertex-prefix guard is what tells those two apart.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            Patched(ctx, Buildings(8));
            Patched(ctx, Buildings(12));
            Assert.Greater(ctx.PatchOutcome.Incremental, 0, "adding buildings must patch");

            // Dropping from the end keeps the prefix — still patchable.
            int beforeTailDrop = ctx.PatchOutcome.Incremental;
            Assert.AreEqual(FromScratch(baseMesh, Buildings(9)), Patched(ctx, Buildings(9)));
            Assert.Greater(ctx.PatchOutcome.Incremental, beforeTailDrop,
                "dropping the last buildings leaves the vertex prefix intact, so it must still patch");

            // Dropping from the middle does not.
            int patchedBefore = ctx.PatchOutcome.Incremental;
            int shiftsBefore = ctx.PatchOutcome.FallbackVertexShift;
            FPBuildingRect[] gapped = BuildingsWithout(9, omit: 2);
            ulong got = Patched(ctx, gapped);

            Assert.AreEqual(FromScratch(baseMesh, gapped), got,
                "however it got there, the mesh must be the full path's");
            Assert.AreEqual(patchedBefore, ctx.PatchOutcome.Incremental,
                "a middle removal shifts later corners, so it must not patch");
            Assert.Greater(ctx.PatchOutcome.FallbackVertexShift, shiftsBefore,
                "and it must be the vertex-prefix guard that stopped it, not some other fallback");
        }

        [Test]
        public void PathIndependence_HowYouGotThereDoesNotShowInTheResult()
        {
            FPNavMesh baseMesh = BuildBase();

            var stepwise = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            Patched(stepwise, Buildings(8));
            ulong viaEight = Patched(stepwise, Buildings(32));

            var direct = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            ulong viaNothing = Patched(direct, Buildings(32));

            Assert.AreEqual(viaNothing, viaEight, "0→32 and 0→8→32 must land on the same mesh");
            Assert.Greater(stepwise.PatchOutcome.Incremental, 0, "the stepwise run must have patched");
        }

        [Test]
        public void Rollback_PreviousMeshMayBeAFutureThatDidNotHappen()
        {
            // Rollback re-runs a tick after restoring state, so the context's "previous" is a mesh
            // from a timeline that was discarded. The patch reads it; the result must not.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            ulong firstEight = Patched(ctx, Buildings(8));
            Patched(ctx, Buildings(32));          // the tick that gets rolled back
            ulong replayedEight = Patched(ctx, Buildings(8));

            Assert.AreEqual(firstEight, replayedEight,
                "re-running the same tick must give the same mesh, whatever the discarded timeline left behind");
            Assert.AreEqual(FromScratch(baseMesh, Buildings(8)), replayedEight);
            Assert.Greater(ctx.PatchOutcome.Incremental, 0);
        }

        [Test]
        public void JoinerRebuildsFromScratch_AndGetsTheSameMeshAsEveryoneElse()
        {
            // The case that makes a path difference a desync rather than a curiosity: the joiner
            // restores components and rebakes once, while the incumbents have been patching.
            FPNavMesh baseMesh = BuildBase();
            var incumbent = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            Patched(incumbent, Buildings(4));
            Patched(incumbent, Buildings(16));
            ulong incumbentFp = Patched(incumbent, Buildings(9));

            var joiner = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            ulong joinerFp = Patched(joiner, Buildings(9));

            Assert.AreEqual(incumbentFp, joinerFp);
            Assert.Greater(incumbent.PatchOutcome.Incremental, 0, "the incumbent must have patched");
            Assert.AreEqual(0, joiner.PatchOutcome.Incremental,
                "the joiner's first rebake has no previous mesh, so it must take the full path");
        }

        [Test]
        public void WithoutCommitSwap_ThePatchIsNotEvenAttempted()
        {
            // The generation guard. A caller that stops committing leaves the context holding an
            // ever-older mesh; diffing against it would still be correct but would grow without
            // bound. Refusing costs one reference comparison.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            Patched(ctx, Buildings(4));                       // committed — chain caught up

            // This one still patches, and should: the chain WAS caught up when it started. It is
            // the rebake AFTER it that must refuse, because by then an output has been produced
            // and never installed, so "previous" no longer means the mesh one step back.
            FPNavMeshRebaker.Rebake(ctx, Buildings(8));       // produced, never installed
            int beforeStale = ctx.PatchOutcome.Incremental;
            Assert.Greater(beforeStale, 0, "the rebake off a caught-up chain must patch");

            FPNavMesh third = FPNavMeshRebaker.Rebake(ctx, Buildings(12));

            Assert.AreEqual(FromScratch(baseMesh, Buildings(12)),
                FPNavMeshRebaker.ComputeFingerprint(third));
            Assert.AreEqual(beforeStale, ctx.PatchOutcome.Incremental,
                "with an output left uninstalled the chain is stale, so the patch must not run");
        }

        [Test]
        public void RandomisedSequences_PatchedEqualsFullPath()
        {
            // Fixed sequences only cover the shapes someone thought of. Two properties are forced
            // and then ASSERTED, because a generator that quietly stops producing them leaves this
            // test green and empty:
            //   - removals happen (the boundary-edge repair case),
            //   - centres sit off whole units (so the pipeline's degenerate compaction, which is
            //     conditional, actually has a chance to fire).
            // Kept deliberately small: this runs in the normal suite and every step rebakes
            // twice (patched + from scratch). Coverage comes from the forced properties below,
            // not from volume.
            const int Seeds = 20;
            FPNavMesh baseMesh = BuildBase();

            int removals = 0, applied = 0;
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            for (int seed = 0; seed < Seeds; seed++)
            {
                uint state = (uint)seed * 2654435761u + 1u;
                int Next(int bound)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    return (int)(state % (uint)bound);
                }

                int previousCount = 0;
                for (int step = 0; step < 3; step++)
                {
                    int count = Next(14);
                    long jitter = Next(3) * 64;   // 0, 1/16 or 1/8 of a world unit — on the grid
                    FPBuildingRect[] buildings = Buildings(count, jitter);

                    ulong expected, got;
                    try { expected = FromScratch(baseMesh, buildings); }
                    catch (Exception) { continue; }   // an illegal set — not this test's subject

                    // Counted only once the set is known to be placeable, so the coverage
                    // assertions below measure steps that actually ran both paths.
                    if (count < previousCount)
                        removals++;
                    previousCount = count;
                    applied++;
                    try { got = Patched(ctx, buildings); }
                    catch (Exception e)
                    {
                        Assert.Fail($"seed {seed} step {step} (n={count}, jitter={jitter}): "
                            + $"the full path accepted this set but the patched path threw:\n{e}");
                        return;
                    }

                    Assert.AreEqual(expected, got,
                        $"seed {seed} step {step} (n={count}, jitter={jitter}) diverged");
                }
            }

            Assert.Greater(removals, 0,
                "the generator never removed a building — the boundary-edge repair case, which is "
                + "the one this test exists for, was never exercised");
            Assert.Greater(applied, Seeds, "too few placeable sets — the sweep barely ran");
            Assert.Greater(ctx.PatchOutcome.Incremental, 0,
                $"nothing patched (applied {applied}, fallbacks: vertex-shift "
                + $"{ctx.PatchOutcome.FallbackVertexShift}, geometry "
                + $"{ctx.PatchOutcome.FallbackGridGeometry}); this test measured the full path "
                + "against itself");
            Assert.Greater(ctx.PatchOutcome.FallbackVertexShift, 0,
                "the sweep never shrank the building set, so the fallback that removals depend on "
                + "was never exercised");
        }

        // ── the survivor copy must not carry post-build writes across ────────────────

        [Test]
        public void UniformNonDefaultArea_PatchStillMatchesTheFullBuild()
        {
            // `previous` is an INSTALLED mesh, so it carries what
            // FPNavMeshRebaker.InheritUniformAreaAttributes stamped on it AFTER its own build.
            // Copying survivors verbatim carried that into the patch, while a full build derives
            // areaMask/costMultiplier from `areas` — so the two paths disagreed on exactly the
            // fields the DEBUG verifier compares, and the verifier (whose reference IS a full
            // build) threw on input that is perfectly valid.
            //
            // Nothing in the shipped assets uses a non-default area today, which is the only
            // reason this stayed quiet. It fires on the first stage that does.
            FPNavMesh baseMesh = BuildBase();
            for (int i = 0; i < baseMesh.TriangleCount; i++)
                baseMesh.TrianglesMutable[i].costMultiplier = FP64.FromDouble(2.5);

            var ctx = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
            Patched(ctx, Buildings(4));                       // no previous yet — full path

            FPNavMesh patched = FPNavMeshRebaker.Rebake(ctx, Buildings(9));
            ctx.CommitSwap(patched);
            Assert.Greater(ctx.PatchOutcome.Incremental, 0,
                "fixture: the second rebake must actually patch, or this measures the full path against itself");

            Assert.AreEqual(FromScratch(baseMesh, Buildings(9)),
                FPNavMeshRebaker.ComputeFingerprint(patched),
                "a patching peer and a from-scratch joiner must land on the same mesh");

            // The fix normalises the survivors to what the build produces; inheritance then stamps
            // the base's uniform value over the whole mesh. Pin that it still does — dropping the
            // base's area would be a different bug wearing the same fix.
            foreach (var t in patched.Triangles)
                Assert.AreEqual(FP64.FromDouble(2.5).RawValue, t.costMultiplier.RawValue,
                    "the base's uniform cost must still reach the patched mesh");
        }

        [Test]
        public void RuntimeIsBlocked_DoesNotSurviveIntoThePatch()
        {
            // The Release-silent arm. A rebake rebuilds from geometry, so a runtime isBlocked does
            // NOT survive it on the full path — MakeTriangle writes false and
            // InheritUniformAreaAttributes never touches the field. The patch used to preserve it
            // on survivors, so a patching peer kept blocks a from-scratch joiner did not; and
            // ComputeFingerprint hashes isBlocked, so that is two peers with two navmeshes.
            // DEBUG caught it via the verifier; Release had nothing.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(
                FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));

            FPNavMesh installed = FPNavMeshRebaker.Rebake(ctx, Buildings(4));
            ctx.CommitSwap(installed);
            // Every triangle, not just one: blocking a single index could land on a triangle the
            // next rebake deletes, which would make this pass without proving anything.
            for (int i = 0; i < installed.TriangleCount; i++)
                installed.TrianglesMutable[i].isBlocked = true;

            FPNavMesh patched = FPNavMeshRebaker.Rebake(ctx, Buildings(9));
            ctx.CommitSwap(patched);
            Assert.Greater(ctx.PatchOutcome.Incremental, 0,
                "fixture: the second rebake must actually patch, or no survivor was ever copied");

            foreach (var t in patched.Triangles)
                Assert.IsFalse(t.isBlocked,
                    "a rebake rebuilds from geometry — the patch must not carry a runtime block across");
            Assert.AreEqual(FromScratch(baseMesh, Buildings(9)),
                FPNavMeshRebaker.ComputeFingerprint(patched),
                "patching peer and from-scratch joiner must agree");
        }

        /// <summary>Square lattice of n x n vertices spaced `step` apart, every quad split in two.</summary>
        private static void Lattice(int step, int n, out FPVector3[] verts, out int[] tris)
        {
            verts = new FPVector3[n * n];
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    verts[r * n + c] = new FPVector3(FP64.FromInt(c * step), FP64.Zero, FP64.FromInt(r * step));

            tris = new int[(n - 1) * (n - 1) * 6];
            int k = 0;
            for (int r = 0; r < n - 1; r++)
                for (int c = 0; c < n - 1; c++)
                {
                    int a = r * n + c, b = r * n + c + 1, d = (r + 1) * n + c, e = (r + 1) * n + c + 1;
                    tris[k++] = a; tris[k++] = d; tris[k++] = b;
                    tris[k++] = b; tris[k++] = d; tris[k++] = e;
                }
        }

        [Test]
        public void NullPool_Patch_MustNotDropTheCellPairPrefix()
        {
            // The grid patch rents its (cell, triangle) pair buffer sized at 16 cells per added
            // triangle and re-rents mid-loop when a triangle's real footprint overruns that. The
            // prefix written so far has to survive that re-rent, and the caller is the only one
            // that can make it survive — the pool's non-pooling mode retains nothing between
            // rents, so it returns a fresh zeroed array with nothing to copy from.
            //
            // That mode is not exotic: `pool` DEFAULTS to null on the public
            // BuildFromConformingTriangulation, while `new FPNavMeshRebakeBufferPool()` gives the
            // pooling one. Omitting the argument — the natural call — takes this path.
            //
            // Three large added triangles is the real trigger, not "several". Grow sets the length
            // to need*2, so with a uniform footprint f: the first grow happens at acCount == 0 and
            // loses nothing, the second triangle fits exactly at 2f, and the THIRD needs 3f > 2f
            // and grows again — that one drops the 2f entries already written. Lost entries read
            // back as zero, which sorts to the front and rewrites the CSR against cell 0.
            //
            // Compared cell by cell rather than by fingerprint ON PURPOSE: ComputeFingerprint
            // hashes vertices and triangles only, so a wrecked grid passes it. That is what makes
            // this shape dangerous — a patching peer and a cold-building joiner would agree on the
            // static-geometry fingerprint and then disagree in FPNavMeshQuery, which reads
            // GridTriangles. The desync detector is structurally blind to it.
            Lattice(20, 4, out var verts, out var allTris);      // 18 triangles, each ~21x21 cells
            FP64 cell = FP64.One;
            FP64 radius = FP64.FromDouble(0.5), slope = FP64.FromInt(45);
            FP64 height = FP64.FromInt(2), climb = FP64.FromDouble(0.5);

            const int Added = 6;
            int keep = allTris.Length / 3 - Added;
            var prevTris = new int[keep * 3];
            Array.Copy(allTris, prevTris, prevTris.Length);

            FPNavMesh prev = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                (FPVector3[])verts.Clone(), prevTris, new int[keep], cell, null,
                radius, slope, height, climb);
            FPNavMesh full = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                (FPVector3[])verts.Clone(), allTris, new int[allTris.Length / 3], cell, null,
                radius, slope, height, climb);

            var outcome = new FPNavMeshBuildPipeline.IncrementalOutcome();
            FPNavMesh patched = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                (FPVector3[])verts.Clone(), allTris, new int[allTris.Length / 3], cell, null,
                radius, slope, height, climb,
                pool: null, vertexCount: -1, previous: prev, outcome: outcome);

            Assert.AreEqual(1, outcome.Incremental,
                $"the patch must actually have run — otherwise this compares the full path with "
                + $"itself. grid={outcome.FallbackGridGeometry} empty={outcome.FallbackEmpty} "
                + $"vshift={outcome.FallbackVertexShift}");
            Assert.AreEqual(Added, patched.TriangleCount - prev.TriangleCount, "added triangle count");

            Assert.AreEqual(full.GridTriangleCount, patched.GridTriangleCount, "grid entry count");
            ReadOnlySpan<int> pc = patched.GridCells, fc = full.GridCells;
            for (int i = 0; i < fc.Length; i++)
                if (pc[i] != fc[i])
                    Assert.Fail($"grid cell slot {i} is {pc[i]}, full build says {fc[i]} — the "
                        + "mid-loop re-rent dropped the pairs written before it");
            ReadOnlySpan<int> pt = patched.GridTriangles, ft = full.GridTriangles;
            for (int i = 0; i < ft.Length; i++)
                if (pt[i] != ft[i])
                    Assert.Fail($"grid entry {i} is {pt[i]}, full build says {ft[i]}");
        }
    }
}
