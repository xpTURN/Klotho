using System;
using SysRandom = System.Random;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Split-invariance for the whole rebake: a rebake advanced a slice at a
    /// time must equal the one-shot rebake, element for element.
    ///
    /// <para>The pieces are pinned separately — <c>FPNavMeshBuildTaskTests</c> for the build,
    /// <c>FPCdtExtractRadixTests</c> for the extract — but the task is where they meet, and the
    /// seams between phases (extract output handed to the vertex array, that handed to the build)
    /// are only exercised here.</para>
    ///
    /// <para>Comparison is <c>DescribeFirstDifference</c> plus the fingerprint: the former covers
    /// adjacency, portals and the grid, which the fingerprint does not read, and the latter covers
    /// the vertex array, which the former does not.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeTaskTests
    {
        private static FPNavMesh BuildBase(int half)
        {
            int side = half * 2 + 1;
            var vertices = new FPVector3[side * side];
            var xs = new long[side * side];
            var zs = new long[side * side];
            int n = 0;
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                {
                    vertices[n] = new FPVector3(FP64.FromInt(x), FP64.Zero, FP64.FromInt(z));
                    xs[n] = FPGeoPredicates.Snap(vertices[n].x);
                    zs[n] = FPGeoPredicates.Snap(vertices[n].z);
                    n++;
                }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>Separated 1x1 footprints, well clear of the border.</summary>
        private static FPBuildingRect[] Buildings(int count, int half)
        {
            var result = new FPBuildingRect[count];
            for (int i = 0; i < count; i++)
            {
                FP64 x = FP64.FromInt(-(half - 3) + (i % 4) * 4);
                FP64 z = FP64.FromInt(-(half - 3) + (i / 4) * 4);
                result[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
            }
            return result;
        }

        private static void AssertSteppedMatchesOneShot(int half, int buildingCount, Func<int> budget)
        {
            FPNavMesh baseMesh = BuildBase(half);
            FPBuildingRect[] buildings = buildingCount == 0 ? null : Buildings(buildingCount, half);

            // Separate contexts: the patch chain is context state, so sharing one would make the
            // second run diff against the first run's output instead of doing the same work.
            var oneShotCtx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            FPNavMesh expected = FPNavMeshRebaker.Rebake(oneShotCtx, buildings);

            var taskCtx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            Assert.IsTrue(
                FPNavMeshRebaker.TryBeginRebake(taskCtx, buildings, out var task, out var rejection),
                $"placement refused ({rejection.Reason}) — the fixture is wrong, not the code");

            int guard = 0;
            const int limit = 500_000;
            while (!task.Step(budget()))
                Assert.Less(++guard, limit, "the task did not converge — a phase failed to advance");

            FPNavMesh actual = task.Install();
            Assert.IsNotNull(actual, "the task rejected what the one-shot accepted");

            string diff = FPNavMeshBuildPipeline.DescribeFirstDifference(actual, expected);
            Assert.IsNull(diff, $"half={half}, buildings={buildingCount}: {diff}");
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(expected),
                FPNavMeshRebaker.ComputeFingerprint(actual),
                $"half={half}, buildings={buildingCount}: fingerprint differs (vertex array)");
        }

        [TestCase(8, 0)]
        [TestCase(8, 1)]
        [TestCase(12, 4)]
        [TestCase(16, 8)]
        public void SteppedRebake_EqualsOneShot_AtEveryFixedBudget(int half, int buildings)
        {
            foreach (int budget in new[] { 1, 3, 17, 100_000, int.MaxValue })
                AssertSteppedMatchesOneShot(half, buildings, () => budget);
        }

        /// <summary>
        /// The same invariance, but with a chain to PATCH.
        ///
        /// <para>The fixture above deliberately starts from a fresh context every time, so
        /// <c>PreviousForPatch</c> is null and only the cold BuildTask is ever sliced. That left the
        /// incremental patch — the path an in-match placement ALWAYS takes, and 80% of the work —
        /// with no slicing coverage at all. This is that coverage.</para>
        ///
        /// <para>Three assertions, because "the sliced patch equals the one-shot patch" alone would
        /// be satisfied by two identically wrong meshes: the sliced result must also equal what a
        /// peer that never patched builds from scratch, and the patch must actually have run.</para>
        /// </summary>
        private static void AssertSteppedPatchMatches(int half, int buildingCount, Func<int> budget)
        {
            FPNavMesh baseMesh = BuildBase(half);
            FPBuildingRect[] first = Buildings(buildingCount, half);
            FPBuildingRect[] second = Buildings(buildingCount + 1, half);

            // A peer that joined and rebuilt from scratch — the reference every path must match.
            var coldCtx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            FPNavMesh reference = FPNavMeshRebaker.Rebake(coldCtx, second);

            // A peer that patched, in one shot.
            var oneShotCtx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            oneShotCtx.CommitSwap(FPNavMeshRebaker.Rebake(oneShotCtx, first));
            int oneShotPatches = oneShotCtx.PatchOutcome.Incremental;
            FPNavMesh expected = FPNavMeshRebaker.Rebake(oneShotCtx, second);
            Assert.Greater(oneShotCtx.PatchOutcome.Incremental, oneShotPatches,
                $"half={half}: the patch never applied — the fixture is not testing what it claims");

            // A peer that patched, sliced.
            var taskCtx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            taskCtx.CommitSwap(FPNavMeshRebaker.Rebake(taskCtx, first));
            int slicedPatches = taskCtx.PatchOutcome.Incremental;

            Assert.IsTrue(
                FPNavMeshRebaker.TryBeginRebake(taskCtx, second, out var task, out var rejection),
                $"placement refused ({rejection.Reason}) — the fixture is wrong, not the code");

            int steps = 0;
            const int limit = 5_000_000;
            while (!task.Step(budget()))
                Assert.Less(++steps, limit, "the patch did not converge — a phase failed to advance");
            FPNavMesh actual = task.Install();

            Assert.Greater(taskCtx.PatchOutcome.Incremental, slicedPatches,
                $"half={half}: the SLICED run did not patch, so this compares two full builds");
            Assert.IsNull(FPNavMeshBuildPipeline.DescribeFirstDifference(actual, expected),
                $"half={half}: sliced patch differs from the one-shot patch");
            Assert.IsNull(FPNavMeshBuildPipeline.DescribeFirstDifference(actual, reference),
                $"half={half}: sliced patch differs from a from-scratch build — a patching peer and "
                + "a joining peer would hold different meshes");
        }

        [TestCase(8, 1)]
        [TestCase(12, 4)]
        [TestCase(16, 8)]
        public void SteppedPatch_EqualsOneShotAndFullBuild_AtEveryFixedBudget(int half, int buildings)
        {
            foreach (int budget in new[] { 1, 3, 17, 100_000, int.MaxValue })
                AssertSteppedPatchMatches(half, buildings, () => budget);
        }

        [TestCase(12, 4)]
        public void SteppedPatch_EqualsOneShotAndFullBuild_AtRandomBudgets(int half, int buildings)
        {
            // Randomised because a cursor that mishandles a boundary tends to do it only at
            // particular split points — the reason to randomise rather than
            // for a fixed {1, prime, whole}.
            for (int seed = 0; seed < 10; seed++)
            {
                var rng = new SysRandom(seed);
                AssertSteppedPatchMatches(half, buildings, () => rng.Next(1, 32));
            }
        }

        /// <summary>
        /// The budget has to REACH each patch phase. Every other assertion here compares meshes,
        /// and a cursor that ignores its budget and runs to the end produces a byte-identical mesh
        /// — so none of them can tell a sliced phase from an unsliced one.
        ///
        /// <para>Counting total steps is not enough either: measured, disabling any ONE cursor took
        /// the total from 19,998 to about 17,960, because Extract contributes most of the count and
        /// the other phases keep slicing. A 10% dip is not something to hang an assertion on. So
        /// the count is taken PER PHASE, where losing a cursor takes its phase from thousands of
        /// steps to one.</para>
        /// </summary>
        [Test]
        public void SteppedPatch_EveryPhaseHonoursItsBudget_WhichNoMeshComparisonCanShow()
        {
            FPNavMesh baseMesh = BuildBase(16);

            var probe = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            FPNavMesh installed = FPNavMeshRebaker.Rebake(probe, Buildings(8, 16));
            probe.CommitSwap(installed);
            int triangles = installed.TriangleCount;

            var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, Buildings(8, 16)));
            Assert.IsTrue(FPNavMeshRebaker.TryBeginRebake(ctx, Buildings(9, 16), out var task, out _));

            var perPhase = new System.Collections.Generic.Dictionary<string, int>();
            while (true)
            {
                string phase = task.PhaseName;
                perPhase.TryGetValue(phase, out int n);
                perPhase[phase] = n + 1;
                if (task.Step(1))
                    break;
            }
            ctx.CommitSwap(task.Install());

            // Each of these walks a whole triangle array one unit at a time, so at a budget of 1 it
            // must take thousands of steps. One is enough to say the cursor was ignored.
            int Floor = triangles / 4;
            foreach (string phase in new[] { "Patch/Diff", "Patch/Survivors", "Patch/EdgeBoundary" })
            {
                perPhase.TryGetValue(phase, out int n);
                Assert.Greater(n, Floor,
                    $"{phase} finished in {n} steps at a budget of 1, on a mesh of {triangles} "
                    + "triangles — that phase is running to completion regardless of its budget. "
                    + "The output is still byte-identical, which is why no mesh comparison sees it");
            }
        }

        [TestCase(12, 4)]
        public void SteppedRebake_EqualsOneShot_AtRandomBudgets(int half, int buildings)
        {
            for (int seed = 0; seed < 10; seed++)
            {
                var rng = new SysRandom(seed);
                AssertSteppedMatchesOneShot(half, buildings, () => rng.Next(1, 32));
            }
        }

        [Test]
        public void RefusedPlacement_IsRefusedAtBegin_BeforeAnySlice()
        {
            // Acceptance is decided synchronously: the caller learns on the same tick, and no work
            // buffer is held for a rebake that will not happen.
            FPNavMesh baseMesh = BuildBase(8);
            var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);

            // Far outside the stage.
            var outside = new[]
            {
                new FPBuildingRect(FP64.FromInt(500), FP64.FromInt(500),
                    FP64.FromInt(501), FP64.FromInt(501), FP64.Zero)
            };

            Assert.IsFalse(FPNavMeshRebaker.TryBeginRebake(ctx, outside, out var task, out _));
            Assert.IsNull(task);

            // The pool was not left marked in use: a following rebake still works.
            Assert.DoesNotThrow(() => ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null)));
        }

        [Test]
        public void DiscardedTask_LeavesThePatchChainAlone()
        {
            // Announcing at completion rather than at install would disable patching through
            // exactly the burst where latest-wins throws outputs away.
            FPNavMesh baseMesh = BuildBase(10);
            var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null));   // chain caught up

            Assert.IsTrue(FPNavMeshRebaker.TryBeginRebake(ctx, null, out var task, out _));
            while (!task.Step(int.MaxValue)) { }
            task.Discard();                                       // never installed

            int before = ctx.PatchOutcome.Incremental;
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null));
            Assert.Greater(ctx.PatchOutcome.Incremental, before,
                "the next rebake did not patch — the discarded task broke the chain");
        }

        [Test]
        public void PreviewPlacements_LeavesThePatchChainAlone_WithoutTheCallerRememberingAnything()
        {
            // A command validates a placement by asking whether the set
            // WOULD bake, and that trial must not become the context's most recent output: the next
            // real rebake would diff against a mesh nobody installed, so PreviousForPatch returns
            // null and patching stops for the rest of the match.
            //
            // The old shape was TryRebakePlacements + a separate DiscardProduced at the end of the
            // handler — correct, and correct only as long as every validating caller remembers.
            // Forgetting is silent: the meshes stay right and the chain quietly degrades to full
            // rebuilds. TryPreviewPlacements makes it structural, and this is the net that says so.
            const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
            var catalog = new FPBuildingShapeCatalogBuilder();
            int square = catalog.Add(
                new[] { -Unit, Unit, Unit, -Unit }, new[] { -Unit, -Unit, Unit, Unit });

            FPNavMesh baseMesh = BuildBase(10);
            var ctx = FPNavMeshRebaker.CreateContext(
                baseMesh, null, prewarm: false, shapeCatalog: catalog.Build());
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null));     // chain caught up

            var trial = new[]
            {
                new FPBuildingPlacement(square, FP64.FromInt(2), FP64.FromInt(2), FP64.Zero),
            };
            // Refused or accepted is not the point here — either way nothing may be left produced.
            FPNavMeshRebaker.TryPreviewPlacements(ctx, trial, out _, null, default, 1);

            int before = ctx.PatchOutcome.Incremental;
            ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null));
            Assert.Greater(ctx.PatchOutcome.Incremental, before,
                "the next rebake did not patch — the preview was left as the context's output, "
                + "which is exactly the silent regression TryPreviewPlacements exists to remove");
        }

        [Test]
        public void PreviewPlacements_HandsBackNoMesh_SoATrialResultCannotBeInstalled()
        {
            // The contract is the signature, not a comment: there is no overload that returns the
            // trial mesh. Reflection because what is being pinned is the ABSENCE of a way to get it
            // — a test that only called the method would pass just as well after someone added
            // `out FPNavMesh mesh` back.
            var overloads = typeof(FPNavMeshRebaker).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            foreach (var m in overloads)
            {
                if (m.Name != "TryPreviewPlacements")
                    continue;
                foreach (var p in m.GetParameters())
                    Assert.AreNotEqual(typeof(FPNavMesh).MakeByRefType(), p.ParameterType,
                        "TryPreviewPlacements hands back a mesh again. A trial result must not be "
                        + "installable: the tick that accepted the command may be a prediction a "
                        + "rollback discards, and the navmesh does not roll back with the frame");
                Assert.AreNotEqual(typeof(FPNavMesh), m.ReturnType,
                    "TryPreviewPlacements returns a mesh again — see above");
            }
        }
    }
}
