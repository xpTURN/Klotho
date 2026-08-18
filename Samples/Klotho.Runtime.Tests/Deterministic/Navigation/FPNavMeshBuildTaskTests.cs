using System;
using SysRandom = System.Random;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Split-invariance for the resumable build: however the caller paces
    /// <see cref="FPNavMeshBuildPipeline.BuildTask.Step"/>, the mesh must equal what
    /// <c>BuildFromConformingTriangulation</c> produces in one call.
    ///
    /// <para>The comparison is element-wise via <c>DescribeFirstDifference</c>, not the
    /// fingerprint. Three of the four ways a split can go wrong — radix stability, pairing
    /// parity, and the grid CSR cursor — change adjacency, portals or the grid, and
    /// <c>ComputeFingerprint</c> covers none of those. A fingerprint-only test would pass a
    /// broken split. The fingerprint is asserted too, since it covers what the other comparator
    /// does not (the vertex array).</para>
    ///
    /// <para>Budgets are randomised per run rather than fixed at {1, few, all}: a cursor that is
    /// only wrong when a stage boundary falls at a particular point is invisible to three fixed
    /// schedules. Same reason <c>FPNavMeshIncrementalPatchTests.RandomisedSequences_*</c> exists.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshBuildTaskTests
    {
        private static void BuildInput(
            int half, out FPVector3[] vertices, out int[] indices, out int[] areas)
        {
            int side = half * 2 + 1;
            vertices = new FPVector3[side * side];
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
            indices = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            areas = new int[indices.Length / 3];
        }

        private static FPNavMesh RunJob(
            FPVector3[] vertices, int[] indices, int[] areas, Func<int> budget,
            FPNavMeshRebakeBufferPool pool)
        {
            var job = FPNavMeshBuildPipeline.BeginBuild(
                vertices, -1, indices, indices.Length, areas, FP64.One, null,
                bakeAgentRadius: FP64.Half, bakeMaxSlopeDeg: FP64.FromInt(45),
                bakeAgentHeight: FP64.FromInt(2), bakeAgentClimb: FP64.Half, pool);

            // Units are triangles in some phases and records (3 per triangle) in others, and the
            // sort alone is three record-wide passes, so a budget of 1 needs roughly 15*triCount
            // calls. The guard is generous but finite — it exists to fail a stalled phase.
            int guard = 0;
            int limit = indices.Length * 8 + 4096;
            while (!job.Step(budget()))
            {
                Assert.Less(++guard, limit, "the job did not converge — a phase failed to advance");
            }
            return job.Result;
        }

        private static void AssertSameMesh(FPNavMesh expected, FPNavMesh actual, string what)
        {
            string diff = FPNavMeshBuildPipeline.DescribeFirstDifference(actual, expected);
            Assert.IsNull(diff, $"{what}: {diff}");
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(expected),
                FPNavMeshRebaker.ComputeFingerprint(actual),
                $"{what}: fingerprint differs (vertex array — DescribeFirstDifference does not read it)");
        }

        [TestCase(4)]
        [TestCase(12)]
        [TestCase(24)]
        public void SteppedBuild_EqualsOneShot_AtEveryFixedBudget(int half)
        {
            BuildInput(half, out var vertices, out var indices, out var areas);
            FPNavMesh oneShot = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, indices, areas, FP64.One, null,
                bakeAgentRadius: FP64.Half, bakeMaxSlopeDeg: FP64.FromInt(45),
                bakeAgentHeight: FP64.FromInt(2), bakeAgentClimb: FP64.Half);

            foreach (int budget in new[] { 1, 2, 3, 100, int.MaxValue })
            {
                FPNavMesh stepped = RunJob(vertices, indices, areas, () => budget, new FPNavMeshRebakeBufferPool());
                AssertSameMesh(oneShot, stepped, $"half={half}, budget={budget}");
            }
        }

        [TestCase(4, 20)]
        [TestCase(16, 20)]
        public void SteppedBuild_EqualsOneShot_AtRandomBudgets(int half, int seeds)
        {
            BuildInput(half, out var vertices, out var indices, out var areas);
            FPNavMesh oneShot = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, indices, areas, FP64.One, null,
                bakeAgentRadius: FP64.Half, bakeMaxSlopeDeg: FP64.FromInt(45),
                bakeAgentHeight: FP64.FromInt(2), bakeAgentClimb: FP64.Half);

            for (int seed = 0; seed < seeds; seed++)
            {
                var rng = new SysRandom(seed);
                FPNavMesh stepped = RunJob(
                    vertices, indices, areas, () => rng.Next(1, 4), new FPNavMeshRebakeBufferPool());
                AssertSameMesh(oneShot, stepped, $"half={half}, seed={seed}");
            }
        }

        [Test]
        public void SteppedBuild_OnAPooledContext_MatchesUnpooled()
        {
            // The pool is what a job actually runs on, and buffer reuse across jobs is where a
            // stale tail would show up. Unpooled is the reference.
            BuildInput(12, out var vertices, out var indices, out var areas);
            FPNavMesh unpooled = RunJob(vertices, indices, areas, () => 1, null);

            var pool = new FPNavMeshRebakeBufferPool();
            for (int i = 0; i < 4; i++)
            {
                FPNavMesh pooled = RunJob(vertices, indices, areas, () => 2, pool);
                AssertSameMesh(unpooled, pooled, $"pooled run {i}");
            }
        }

        [Test]
        public void Step_AfterCompletion_IsANoOp()
        {
            BuildInput(4, out var vertices, out var indices, out var areas);
            var job = FPNavMeshBuildPipeline.BeginBuild(
                vertices, -1, indices, indices.Length, areas, FP64.One, null,
                FP64.Half, FP64.FromInt(45), FP64.FromInt(2), FP64.Half, null);

            // int.MaxValue is the "just finish it" budget — it must not overflow the cursor.
            Assert.IsTrue(job.Step(int.MaxValue));
            FPNavMesh first = job.Result;
            Assert.IsTrue(job.Step(int.MaxValue));
            Assert.AreSame(first, job.Result);
            Assert.AreEqual(0, job.RemainingStages);
        }
    }
}
