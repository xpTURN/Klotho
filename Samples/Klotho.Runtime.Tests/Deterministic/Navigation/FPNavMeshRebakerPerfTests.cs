using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SysRandom = System.Random;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Measurement harness for the runtime rebake: full-rebake cost against the server tick
    /// budget. Measures the real Brawler assets end to end, the phase split (extract vs CDT vs
    /// pipeline — which decides whether a partial-rebake optimisation would pay), synthetic
    /// scaling, allocation volume against large-object-heap pressure, and agent reseed cost.
    ///
    /// Building count barely moves the total (a rect adds 4 vertices to a full-mesh CDT +
    /// pipeline pass), so real assets are measured with 0 buildings — no placement probing,
    /// which would multiply full rebakes.
    ///
    /// Excluded from the normal suite: run explicitly, in Release (DEBUG builds run the CDT
    /// SelfCheck integrity scan and Debug.Asserts — numbers are meaningless there):
    ///   dotnet test -c Release --filter FullyQualifiedName~FPNavMeshRebakerPerfTests
    /// </summary>
    [TestFixture]
    [Explicit("perf measurement — run in Release with an explicit filter")]
    public class FPNavMeshRebakerPerfTests
    {
        #region Harness

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        // Warmup default 32: tiered JIT promotes at ~30 calls — fewer warmups measure tier-0
        // cold code (up to ~8x slower, and an early record here was inflated exactly that way).
        // Steady-state ≈ IL2CPP/AOT client cost; the cold first call (reported separately in
        // RealBrawlerAssets_RebakeCost) ≈ the FIRST in-match rebake on a CoreCLR server.
        private static (double minMs, double medianMs) Measure(Action action, int warmup = 32, int iterations = 9)
        {
            for (int i = 0; i < warmup; i++)
                action();

            var samples = new List<double>(iterations);
            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                action();
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return (samples[0], samples[samples.Count / 2]);
        }

        private static void Report(string label, (double minMs, double medianMs) m, string extra = "")
        {
            TestContext.Out.WriteLine($"{label,-46} min {m.minMs,9:F3} ms   median {m.medianMs,9:F3} ms   {extra}");
        }

        // Allocation counterpart of Measure, with the same warmup discipline: tier-0 code and
        // one-shot static caches inflate the first calls, so steady state is what the in-match
        // cost actually is.
        private static long MeasureAlloc(Action action, int warmup = 32, int iterations = 5)
        {
            for (int i = 0; i < warmup; i++)
                action();

            var samples = new List<long>(iterations);
            for (int i = 0; i < iterations; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                action();
                samples.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            }
            samples.Sort();
            return samples[samples.Count / 2];
        }

        private static void ReportAlloc(string label, long bytes, string extra = "")
        {
            TestContext.Out.WriteLine($"{label,-46} {bytes / 1024.0,10:F0} KB   {extra}");
        }

        #endregion

        [Test]
        public void RealBrawlerAssets_RebakeCost()
        {
            string root = RepoRoot();
            string[] assets =
            {
                "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes",
                "Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes",
                "Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes",
                "Samples/Brawler/Assets/NavMesh/Data/8_heightmesh.NavMeshData.bytes",
            };
            TestContext.Out.WriteLine("=== full rebake cost, real assets (tick budget: 16.7ms @60Hz / 33.3ms @30Hz) ===");

            bool coldReported = false;
            foreach (string rel in assets)
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path))
                {
                    TestContext.Out.WriteLine($"{rel}: MISSING — skipped");
                    continue;
                }
                FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
                string name = Path.GetFileNameWithoutExtension(rel);
                string size = $"({mesh.Triangles.Length} tris, {mesh.Vertices.Length} verts)";

                try
                {
                    if (!coldReported)
                    {
                        // Process-global cold first call (tier-0 JIT) — only meaningful once.
                        var swCold = Stopwatch.StartNew();
                        FPNavMeshRebaker.Rebake(mesh, null);
                        swCold.Stop();
                        TestContext.Out.WriteLine(
                            $"cold first-call ({name}): {swCold.Elapsed.TotalMilliseconds,9:F3} ms   (process-global, ≈ first in-match rebake on CoreCLR)");
                        coldReported = true;
                    }

                    var m = Measure(() => FPNavMeshRebaker.Rebake(mesh, null));

                    long before = GC.GetAllocatedBytesForCurrentThread();
                    FPNavMeshRebaker.Rebake(mesh, null);
                    long alloc = GC.GetAllocatedBytesForCurrentThread() - before;

                    Report($"{name} {size}", m, $"alloc {alloc / 1024.0:F0} KB");
                }
                catch (NotSupportedException e)
                {
                    TestContext.Out.WriteLine($"{name} {size}: NOT SUPPORTED — {e.Message}");
                }
            }
        }

        [Test]
        public void RealAsset_PhaseSplit_ExtractCdtPipeline()
        {
            // A partial rebake would save only the CDT stage, so it is worth doing only if the
            // CDT dominates this split.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            TestContext.Out.WriteLine($"=== Phase split, Field ({mesh.Triangles.Length} tris, {mesh.Vertices.Length} verts) ===");

            var mExtract = Measure(() => FPNavMeshObstacleExtractor.Extract(mesh, out _, out _));
            Report("extract rings", mExtract);

            FPNavMeshObstacleExtractor.Extract(mesh, out var ringVerts, out var ringOffsets);
            int n = mesh.Vertices.Length;
            var xs = new long[n];
            var zs = new long[n];
            var map = new Dictionary<(long, long), int>(n);
            for (int i = 0; i < n; i++)
            {
                xs[i] = FPGeoPredicates.Snap(mesh.Vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(mesh.Vertices[i].z);
                map[(xs[i], zs[i])] = i;
            }
            var constraints = new List<int>();
            for (int p = 0; p < ringOffsets.Length; p++)
            {
                int start = ringOffsets[p];
                int end = p + 1 < ringOffsets.Length ? ringOffsets[p + 1] : ringVerts.Length;
                if (start >= end) continue;
                for (int i = start; i < end; i++)
                {
                    int j = i + 1 < end ? i + 1 : start;
                    constraints.Add(map[(FPGeoPredicates.Snap(ringVerts[i].x), FPGeoPredicates.Snap(ringVerts[i].y))]);
                    constraints.Add(map[(FPGeoPredicates.Snap(ringVerts[j].x), FPGeoPredicates.Snap(ringVerts[j].y))]);
                }
            }
            int[] constraintArr = constraints.ToArray();
            var mCdt = Measure(() => FPConstrainedDelaunay.Triangulate(xs, zs, constraintArr));
            Report("CDT (Triangulate)", mCdt, $"constraints {constraintArr.Length / 2}");

            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraintArr);
            var mBuild = Measure(() => FPNavMeshBuildPipeline.Build(
                mesh.Vertices.ToArray(), tris, new int[tris.Length / 3], mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb));
            Report("pipeline (Build, full)", mBuild, $"{tris.Length / 3} tris out");

            var mFast = Measure(() => FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                mesh.Vertices.ToArray(), tris, new int[tris.Length / 3], mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb));
            Report("pipeline (Build, fast path)", mFast);
        }

        // Field's CDT input (snapped coordinates + boundary-ring constraints), shared by the two
        // phase-split gates so the two measurements cannot drift apart.
        private static (FPNavMesh mesh, long[] xs, long[] zs, int[] constraints) LoadFieldCdtInput()
        {
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);

            FPNavMeshObstacleExtractor.Extract(mesh, out var ringVerts, out var ringOffsets);
            int n = mesh.Vertices.Length;
            var xs = new long[n];
            var zs = new long[n];
            var map = new Dictionary<(long, long), int>(n);
            for (int i = 0; i < n; i++)
            {
                xs[i] = FPGeoPredicates.Snap(mesh.Vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(mesh.Vertices[i].z);
                map[(xs[i], zs[i])] = i;
            }
            var cons = new List<int>();
            for (int p = 0; p < ringOffsets.Length; p++)
            {
                int start = ringOffsets[p];
                int end = p + 1 < ringOffsets.Length ? ringOffsets[p + 1] : ringVerts.Length;
                if (start >= end) continue;
                for (int i = start; i < end; i++)
                {
                    int j = i + 1 < end ? i + 1 : start;
                    cons.Add(map[(FPGeoPredicates.Snap(ringVerts[i].x), FPGeoPredicates.Snap(ringVerts[i].y))]);
                    cons.Add(map[(FPGeoPredicates.Snap(ringVerts[j].x), FPGeoPredicates.Snap(ringVerts[j].y))]);
                }
            }
            return (mesh, xs, zs, cons.ToArray());
        }

        [Test]
        public void CdtPhaseSplit_SnapshotCacheGate()
        {
            // The snapshot cache saves only vertex insertion (a) and constraint insertion (b);
            // Extract (d) runs on every rebake regardless. So the cache is worth having only if
            // (a)+(b) dominate. Internal Cdt access via InternalsVisibleTo; warmup 32 for the JIT.
            var (mesh, xs, zs, consArr) = LoadFieldCdtInput();
            int n = mesh.Vertices.Length;
            TestContext.Out.WriteLine($"=== CDT phase split, Field ({mesh.Triangles.Length} tris, {n} verts, {consArr.Length / 2} constraints) ===");

            var mVerts = Measure(() =>
            {
                var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
                cdt.InsertAllVertices();
            });
            Report("(a) ctor + vertex insertion", mVerts);

            var mCons = Measure(() =>
            {
                var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
                cdt.InsertAllVertices();
                cdt.InsertConstraints(consArr, consArr.Length);
            });
            Report("(a)+(b) + constraint insertion", mCons);

            var mAll = Measure(() =>
            {
                var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
                cdt.InsertAllVertices();
                cdt.InsertConstraints(consArr, consArr.Length);
                cdt.Extract(true, out _);
            });
            Report("(a)+(b)+(d) + Extract", mAll);
            TestContext.Out.WriteLine(
                $"gate: (a)+(b) = {mCons.minMs:F1} ms of {mAll.minMs:F1} ms total " +
                $"({mCons.minMs / mAll.minMs * 100:F0}%) — (d) Extract residual = {mAll.minMs - mCons.minMs:F1} ms");
        }

        [Test]
        public void ExtractPhaseSplit_RadixGate()
        {
            // Gate for radix-sorting Extract's canonicalize: it pays only
            // if Array.Sort is >= 1.0 ms of Extract. Below that the 0-1 BFS parity dominates and
            // radix is the wrong tool. The sort is inside Extract, so the split is measured by
            // difference across the stage boundaries (ExtractKeepMask / ExtractTriples / sort /
            // ExtractEmit) — a test cannot repeat those loops in place because they read private
            // state. The Cdt is built once and the stages re-run against it: they rewrite their
            // own buffers and never mutate _tris, so repetition is sound. ExtractTriples refills
            // in creation order every call, so the sort never sees an already-sorted input.
            var (mesh, xs, zs, consArr) = LoadFieldCdtInput();

            var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
            cdt.InsertAllVertices();
            cdt.InsertConstraints(consArr, consArr.Length);

            cdt.Extract(true, out int outCount);
            TestContext.Out.WriteLine(
                $"=== Extract phase split, Field ({mesh.Triangles.Length} base tris, " +
                $"{cdt.TriCountForDiagnostics} CDT tris, {outCount / 3} kept) ===");

            var mKeep = Measure(() => cdt.ExtractKeepMask(true));
            Report("(1) keep mask (0-1 BFS parity)", mKeep);

            var mTriples = Measure(() =>
            {
                bool[] keep = cdt.ExtractKeepMask(true);
                cdt.ExtractTriples(keep, out _);
            });
            Report("(1)+(2) + canonical rotation", mTriples);

            var mRadix = Measure(() =>
            {
                bool[] keep = cdt.ExtractKeepMask(true);
                int kept = cdt.ExtractTriples(keep, out var triples);
                cdt.SortTriples(triples, kept);
            });
            Report("(1)+(2)+(3) + radix sort", mRadix);

            // The replaced comparison sort, kept as the measurement baseline (production no longer
            // calls it; FPCdtExtractRadixTests pins the two against each other).
            var mComparison = Measure(() =>
            {
                bool[] keep = cdt.ExtractKeepMask(true);
                int kept = cdt.ExtractTriples(keep, out var triples);
                Array.Sort(triples, 0, kept);
            });
            Report("(1)+(2)+(3') + Array.Sort [baseline]", mComparison);

            var mFull = Measure(() => cdt.Extract(true, out _));
            Report("(1)+(2)+(3)+(4) = Extract", mFull);

            double bfs = mKeep.minMs;
            double rotate = mTriples.minMs - mKeep.minMs;
            double radix = mRadix.minMs - mTriples.minMs;
            double comparison = mComparison.minMs - mTriples.minMs;
            double emit = mFull.minMs - mRadix.minMs;
            TestContext.Out.WriteLine(
                $"split: BFS {bfs:F3} ms | rotate {rotate:F3} ms | sort {radix:F3} ms | emit {emit:F3} ms " +
                $"(Extract {mFull.minMs:F3} ms)");
            TestContext.Out.WriteLine(
                $"sort: radix {radix:F3} ms vs Array.Sort {comparison:F3} ms " +
                $"({(comparison > 0 ? comparison / System.Math.Max(radix, 1e-6) : 0):F1}x) — " +
                $"Phase 2 gate was 'Array.Sort >= 1.0 ms of Extract'");
        }

        [Test]
        public void RebakeCost_ByBuildingCount()
        {
            // What a placement actually costs as the board fills up — the figure a game budgets
            // against. Driven the way a match drives it: pooled context, CommitSwap after each
            // rebake, so this is steady state and not a first call.
            //
            // Positions are discovered rather than authored. A hand-picked centre on a real asset
            // is usually rejected (Field's walkable region is not a slab), and a rejected placement
            // measures the validation path instead of the rebake.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);

            var ctx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            var accepted = new List<FPBuildingRect>();
            var centres = new List<FPVector2>();
            FP64 half = FP64.FromDouble(0.5);

            // Candidates are the centroids of the LARGEST triangles: a big triangle is open floor,
            // which is where a placement clears the boundary. A lattice sweep finds far fewer, and
            // every miss costs a full rebake.
            var order = new List<int>();
            for (int i = 0; i < mesh.Triangles.Length; i++)
                order.Add(i);
            order.Sort((a, b) => mesh.Triangles[b].area.RawValue.CompareTo(mesh.Triangles[a].area.RawValue));

            foreach (int t in order)
            {
                if (accepted.Count >= 32)
                    break;
                FPVector2 c = mesh.Triangles[t].centerXZ;
                FP64 cx = FPGeoPredicates.Quantize(c.x), cz = FPGeoPredicates.Quantize(c.y);

                bool tooClose = false;
                foreach (FPVector2 p in centres)
                {
                    if (FP64.Abs(p.x - cx) < FP64.FromInt(4) && FP64.Abs(p.y - cz) < FP64.FromInt(4))
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose)
                    continue;

                accepted.Add(new FPBuildingRect(cx - half, cz - half, cx + half, cz + half, FP64.Zero));
                try
                {
                    FPNavMeshRebaker.Rebake(ctx, accepted.ToArray(), null);
                    centres.Add(new FPVector2(cx, cz));
                }
                catch (Exception) { accepted.RemoveAt(accepted.Count - 1); }
            }

            TestContext.Out.WriteLine(
                $"=== rebake cost by building count, Field ({mesh.Triangles.Length} tris), "
                + $"pooled + CommitSwap ===");
            if (accepted.Count < 32)
                TestContext.Out.WriteLine($"only {accepted.Count} of 32 candidate centres were accepted");

            // What is timed is THE REBAKE THAT PLACES THE Nth BUILDING, not a repeat of a set
            // that already contains it. The distinction matters now that a rebake can patch the
            // previous mesh: re-running an unchanged set diffs to nothing and is unrepresentatively
            // cheap, while what a game actually pays for is the step where the set grows.
            //
            // So each sample rebuilds the context, warms it to N-1 outside the timer, and times
            // one rebake to N.
            foreach (int n in new[] { 1, 2, 4, 8, 16, 32 })
            {
                if (n > accepted.Count)
                    continue;
                var before = accepted.GetRange(0, n - 1).ToArray();
                var after = accepted.GetRange(0, n).ToArray();

                double best = double.MaxValue;
                long alloc = 0;
                var sw = new Stopwatch();
                for (int rep2 = 0; rep2 < 12; rep2++)
                {
                    var run = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
                    // Twice, so the retirement chain has a generation to hand back — otherwise the
                    // measured rebake allocates its whole output fresh and the figure below reads
                    // megabytes instead of the steady state a match actually sees.
                    run.CommitSwap(FPNavMeshRebaker.Rebake(run, before, null));
                    run.CommitSwap(FPNavMeshRebaker.Rebake(run, before, null));

                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    sw.Restart();
                    FPNavMesh grown = FPNavMeshRebaker.Rebake(run, after, null);
                    sw.Stop();
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    run.CommitSwap(grown);

                    if (rep2 >= 4 && sw.Elapsed.TotalMilliseconds < best)
                    {
                        best = sw.Elapsed.TotalMilliseconds;
                        alloc = a1 - a0;
                    }
                }
                TestContext.Out.WriteLine(
                    $"{"placing building #" + n,-46} min {best,9:F3} ms   alloc {alloc / 1024.0:F1} KB");
            }
        }

        [Test]
        public void SnapshotRebake_RealField_60HzGate()
        {
            // Cached-snapshot rebake against the 60Hz budget.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);

            var swCreate = Stopwatch.StartNew();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(mesh);
            swCreate.Stop();
            TestContext.Out.WriteLine($"CreateSnapshot (once, load-time): {swCreate.Elapsed.TotalMilliseconds:F1} ms");

            var m0 = Measure(() => FPNavMeshRebaker.Rebake(snapshot, null));
            long before = GC.GetAllocatedBytesForCurrentThread();
            FPNavMeshRebaker.Rebake(snapshot, null);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Report($"snapshot rebake, 0 bldg ({mesh.Triangles.Length} tris)", m0, $"alloc {alloc / 1024.0:F0} KB");

            var buildings = new[]
            {
                new FPBuildingRect(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(6), FP64.FromInt(6), FP64.Zero),
            };
            try
            {
                var m1 = Measure(() => FPNavMeshRebaker.Rebake(snapshot, buildings));
                Report("snapshot rebake, 1 bldg", m1);
            }
            catch (InvalidOperationException e)
            {
                TestContext.Out.WriteLine($"1-bldg placement rejected on this asset: {e.Message}");
            }
        }

        [Test]
        public void OutputRecycling_SteadyStateAllocation()
        {
            // What recycling the mesh's own output arrays is worth.
            // The chain only recycles across CommitSwap, so this drives the seam the way a match
            // does — rebake, install, repeat.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);

            TestContext.Out.WriteLine($"=== v2 output recycling, Field ({mesh.Triangles.Length} tris, 0 bldg) ===");

            // Baseline is the pre-v2 path: no pool at all, so every output array is a fresh
            // exact-size allocation. That is what v2 has to beat.
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(mesh);
            long baseline = MeasureAlloc(() => FPNavMeshRebaker.Rebake(snapshot, null));
            ReportAlloc("pre-v2 (work-buffer pool only)", baseline);

            // v1 machinery only: work buffers pooled, outputs freshly allocated every time
            // (a context that never commits never retires anything). The gap to the line below is
            // v2's contribution; the gap to the line above is v1's.
            var noCommit = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(mesh));
            long workPooled = MeasureAlloc(() => FPNavMeshRebaker.Rebake(noCommit, null));
            ReportAlloc("v1 (work buffers pooled)", workPooled,
                $"{(baseline - workPooled) * 100.0 / baseline:F0}% below pre-v2");

            // With the swap committed each time — steady state after the chain fills.
            var recycling = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(mesh));
            long recycled = MeasureAlloc(() =>
            {
                FPNavMesh m = FPNavMeshRebaker.Rebake(recycling, null);
                recycling.CommitSwap(m);
            });
            ReportAlloc("recycling (rebake + CommitSwap)", recycled,
                $"{(workPooled - recycled) / 1024.0:F0} KB below v1 ({(workPooled - recycled) * 100.0 / workPooled:F0}%), {(baseline - recycled) * 100.0 / baseline:F0}% below pre-v2");

            var mNo = Measure(() => FPNavMeshRebaker.Rebake(snapshot, null));
            var mYes = Measure(() =>
            {
                FPNavMesh m = FPNavMeshRebaker.Rebake(recycling, null);
                recycling.CommitSwap(m);
            });
            Report("time, no recycling", mNo);
            Report("time, recycling", mYes, "must not regress");
        }

        [Test]
        public void TriggerGate_GcPressure_AC0()
        {
            // Decides whether recycling the OUTPUT arrays is worth attempting at all. Recycling
            // them carries a real failure mode — silent use-after-recycle — so it should not be
            // started without evidence of either (a) server Gen2/LOH pressure or (c) a much larger
            // map making the absolute volume a problem. This measures (a) directly: how many
            // collections a burst of rebakes actually costs with only the work-buffer pool in
            // place.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(mesh));

            // Drive the seam the way a match does — rebake AND install — so output recycling is
            // actually engaged. Measuring without CommitSwap would report the pre-v2 pressure.
            void RebakeAndInstall()
            {
                FPNavMesh m = FPNavMeshRebaker.Rebake(ctx, null);
                ctx.CommitSwap(m);
            }

            // Warm the pool + JIT so the burst measures steady state, not first-call growth.
            for (int i = 0; i < 32; i++)
                RebakeAndInstall();

            const int Burst = 100;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            long heapBefore = GC.GetTotalMemory(forceFullCollection: false);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < Burst; i++)
                RebakeAndInstall();

            sw.Stop();
            int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
            long allocated = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
            long heapAfter = GC.GetTotalMemory(forceFullCollection: false);

            TestContext.Out.WriteLine(
                $"=== trigger (a): GC pressure over {Burst} pooled rebakes, Field ({mesh.Triangles.Length} tris) ===");
            TestContext.Out.WriteLine(
                $"allocated {allocated / 1024.0 / 1024.0:F1} MB total, {allocated / Burst / 1024.0:F0} KB per rebake");
            TestContext.Out.WriteLine(
                $"collections: gen0={d0} gen1={d1} gen2={d2}  ({d2 / (double)Burst:F3} gen2 per rebake)");
            TestContext.Out.WriteLine(
                $"managed heap {heapBefore / 1024.0 / 1024.0:F1} -> {heapAfter / 1024.0 / 1024.0:F1} MB, burst wall {sw.Elapsed.TotalMilliseconds:F0} ms");

            var info = GC.GetGCMemoryInfo();
            if (info.GenerationInfo.Length > 3)
            {
                TestContext.Out.WriteLine(
                    $"LOH after burst: {info.GenerationInfo[3].SizeAfterBytes / 1024.0 / 1024.0:F1} MB");
            }

            // A verdict aid, not an assertion — this supplies the number, a human decides.
            TestContext.Out.WriteLine(d2 == 0
                ? "VERDICT: no gen2 collection across the burst — trigger (a) NOT met on this map size."
                : $"VERDICT: {d2} gen2 collection(s) — weigh against v2 risk before starting.");
        }

        #region P0 — large-map gate

        /// <summary>Square grid stage: (2*half+1)^2 vertices, unit cells, agent radius 0.5.</summary>
        private static FPNavMesh BuildGridStage(int half)
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

        /// <summary>
        /// <paramref name="count"/> separated 1x1 footprints on a lattice inside the stage, one
        /// cell clear of the border. Returns null when the lattice would not separate them —
        /// touching footprints are a rejection, not a rebake, and would measure the wrong thing.
        /// </summary>
        private static FPBuildingRect[] LatticePlacements(int count, int half)
        {
            if (count == 0)
                return null;
            int perSide = (int)System.Math.Ceiling(System.Math.Sqrt(count));
            int span = (half - 2) * 2;
            int step = span / System.Math.Max(1, perSide);
            if (step < 3)
                return null;

            var result = new FPBuildingRect[count];
            for (int i = 0; i < count; i++)
            {
                FP64 x = FP64.FromInt(-(half - 2) + (i % perSide) * step);
                FP64 z = FP64.FromInt(-(half - 2) + (i / perSide) * step);
                result[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
            }
            return result;
        }

        [Test]
        public void P0_ScaleSweep_RebakeCost()
        {
            // Gate (1): does a synchronous rebake break the tick budget at the target
            // scale, AFTER the Extract sort went radix? Measured on the real game path
            // (context + CommitSwap) so the patch chain stays live, which is what an in-match
            // placement actually does. Warmups shrink with size — the big stages cost seconds.
            TestContext.Out.WriteLine("=== P0 (1)+(4): rebake cost and patch rate vs map size ===");
            TestContext.Out.WriteLine($"{"tris",9} {"bldgs",6} {"rebake ms",10} {"alloc KB",10} {"patched",8} {"fallback",9}");

            foreach (int half in new[] { 40, 80, 120, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                int tris = baseMesh.Triangles.Length;
                int warm = tris > 200_000 ? 2 : tris > 60_000 ? 4 : 8;
                int iters = tris > 200_000 ? 3 : 5;

                foreach (int count in new[] { 0, 1, 64, 256, 512 })
                {
                    FPBuildingRect[] buildings = LatticePlacements(count, half);
                    if (count > 0 && buildings == null)
                        continue;

                    var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);

                    // Reject early rather than reporting a rejection as if it were a rebake.
                    if (!FPNavMeshRebaker.TryRebake(ctx, buildings, out FPNavMesh probe, out var rejection))
                    {
                        TestContext.Out.WriteLine($"{tris,9} {count,6}   rejected: {rejection.Reason}");
                        continue;
                    }
                    ctx.CommitSwap(probe);

                    void Cycle()
                    {
                        FPNavMesh m = FPNavMeshRebaker.Rebake(ctx, buildings);
                        ctx.CommitSwap(m);
                    }

                    // IncrementalOutcome is a CLASS held by the context, so a "before" copy of the
                    // reference tracks the same counters — snapshot the values, not the object.
                    var oc = ctx.PatchOutcome;
                    int inc0 = oc.Incremental;
                    int fb0 = oc.FallbackVertexShift + oc.FallbackGridGeometry
                            + oc.FallbackEmpty + oc.FallbackDuplicateBoundaryEdge;

                    var time = Measure(Cycle, warmup: warm, iterations: iters);
                    long alloc = MeasureAlloc(Cycle, warmup: 1, iterations: 3);

                    int patched = oc.Incremental - inc0;
                    int fell = oc.FallbackVertexShift + oc.FallbackGridGeometry
                             + oc.FallbackEmpty + oc.FallbackDuplicateBoundaryEdge - fb0;
                    TestContext.Out.WriteLine(
                        $"{tris,9} {count,6} {time.minMs,10:F2} {alloc / 1024.0,10:F0} {patched,8} {fell,9}");
                }
            }
        }

        [Test]
        public void P0_ScaleSweep_PhaseSplit()
        {
            // Gate (2): how the Extract-vs-Build split moves with size. The 22k
            // observation (Extract + Build = 82% of the rebake) is what makes splitting BOTH a
            // requirement rather than a nicety. Measured the way ResidualTimeBreakdown does it —
            // through the snapshot, which is the path a rebake actually takes; rebuilding the
            // stages from a fresh Triangulate measures a different (larger) triangulation.
            TestContext.Out.WriteLine("=== P0 (2): stage split vs map size, no buildings ===");
            TestContext.Out.WriteLine(
                $"{"tris",9} {"total ms",9} {"cdt ms",8} {"clone",7} {"extract",8} {"build ms",9} {"resid",7} {"E+B %",6}");

            var noXs = Array.Empty<long>();
            var noZs = Array.Empty<long>();
            var noCons = Array.Empty<int>();

            foreach (int half in new[] { 40, 80, 120, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                int tris = baseMesh.Triangles.Length;
                int warm = tris > 200_000 ? 2 : tris > 60_000 ? 4 : 8;
                int iters = tris > 200_000 ? 3 : 5;

                FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);
                var ctx = new FPNavMeshRebakeContext(snapshot);

                var fullCtx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
                FPNavMeshRebaker.Rebake(fullCtx, null);   // never committed -> always the full path
                var mTotal = Measure(() => FPNavMeshRebaker.Rebake(fullCtx, null), warm, iters);

                var mCdt = Measure(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                    snapshot.Cdt, noXs, noZs, 0, noCons, 0, out _, true, null, ctx.Pool), warm, iters);
                var mClone = Measure(() => new FPConstrainedDelaunay.Cdt(snapshot.Cdt, noXs, noZs, 0, null), warm, iters);

                int[] tri = FPConstrainedDelaunay.TriangulateFromSnapshot(
                    snapshot.Cdt, noXs, noZs, 0, noCons, 0, out int triCount, true, null);
                var vertices = new FPVector3[snapshot.BaseXs.Length];
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = new FPVector3(
                        FPGeoPredicates.Unsnap(snapshot.BaseXs[i]), snapshot.BaseYs[i],
                        FPGeoPredicates.Unsnap(snapshot.BaseZs[i]));
                var areas = new int[triCount / 3];
                var mBuild = Measure(() => FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                    vertices, new ReadOnlySpan<int>(tri, 0, triCount), areas, baseMesh.GridCellSize, null,
                    baseMesh.BakeAgentRadius, baseMesh.BakeMaxSlopeDeg, baseMesh.BakeAgentHeight,
                    baseMesh.BakeAgentClimb, ctx.Pool), warm, iters);

                double extract = mCdt.minMs - mClone.minMs;
                double residual = mTotal.minMs - mCdt.minMs - mBuild.minMs;
                double eb = (extract + mBuild.minMs) / mTotal.minMs * 100.0;
                TestContext.Out.WriteLine(
                    $"{tris,9} {mTotal.minMs,9:F2} {mCdt.minMs,8:F2} {mClone.minMs,7:F2} {extract,8:F2} " +
                    $"{mBuild.minMs,9:F2} {residual,7:F2} {eb,5:F0}%");
            }
        }

        [Test]
        public unsafe void P0_DerivedRebuildAndMemory()
        {
            // Two measurements. D: what rebuilding the derived set costs — the number that decides
            // whether a rollback round-trip across the swap boundary can afford to rebuild instead
            // of holding two copies (D6-(3)). E: resident bytes per copy, for the memory budget
            // budget, plus whether Extract's deque outgrows its pool slot — the pool's alloc
            // gate cannot see that growth because IntDeque.Grow allocates outside it.
            TestContext.Out.WriteLine("=== P0 (D): derived rebuild cost vs map size ===");
            TestContext.Out.WriteLine(
                $"{"tris",9} {"query ms",9} {"trio ms",8} {"rebind ms",10} {"trio KB",9} {"mesh KB",9}");

            foreach (int half in new[] { 40, 80, 120, 160 })
            {
                FPNavMesh mesh = BuildGridStage(half);
                int tris = mesh.Triangles.Length;
                int warm = tris > 200_000 ? 2 : tris > 60_000 ? 4 : 8;
                int iters = tris > 200_000 ? 3 : 5;

                var mQuery = Measure(() => new FPNavMeshQuery(mesh, null), warm, iters);
                var mTrio = Measure(() =>
                {
                    var q = new FPNavMeshQuery(mesh, null);
                    new FPNavMeshPathfinder(mesh, q, null);
                    new FPNavMeshFunnel(mesh, q, null);
                }, warm, iters);
                long trioAlloc = MeasureAlloc(() =>
                {
                    var q = new FPNavMeshQuery(mesh, null);
                    new FPNavMeshPathfinder(mesh, q, null);
                    new FPNavMeshFunnel(mesh, q, null);
                }, warmup: 2, iterations: 3);

                // SwapNavMesh(mesh) is the rebind a swap performs: it re-extracts the ORCA
                // obstacles, which is the part D6-(3) says must not run on every rollback bounce.
                var q0 = new FPNavMeshQuery(mesh, null);
                var system = new FPNavAgentSystem(
                    mesh, q0, new FPNavMeshPathfinder(mesh, q0, null),
                    new FPNavMeshFunnel(mesh, q0, null), null);
                system.SetAvoidance(new FPNavAvoidance());
                system.LoadNavMeshObstacles();
                var mRebind = Measure(() => system.SwapNavMesh(mesh), warm, iters);

                long meshBytes = (long)mesh.VertexCount * sizeof(FPVector3)
                               + (long)mesh.TriangleCount * sizeof(FPNavMeshTriangle);
                TestContext.Out.WriteLine(
                    $"{tris,9} {mQuery.minMs,9:F2} {mTrio.minMs,8:F2} {mRebind.minMs,10:F2} " +
                    $"{trioAlloc / 1024.0,9:F0} {meshBytes / 1024.0,9:F0}");
            }

            TestContext.Out.WriteLine("=== P0 (E): Extract allocation — pooled steady state ===");
            TestContext.Out.WriteLine($"{"tris",9} {"cdt tris",9} {"extract B",10}");
            foreach (int half in new[] { 40, 80, 120, 160 })
            {
                FPNavMesh mesh = BuildGridStage(half);
                var snapshot = FPNavMeshRebaker.CreateSnapshot(mesh, null, prewarm: false);
                var ctx = new FPNavMeshRebakeContext(snapshot);
                var noXs = Array.Empty<long>();
                var noZs = Array.Empty<long>();
                var noCons = Array.Empty<int>();

                // Steady state on one pool: every work buffer is already rented at full size, so
                // anything still allocating is growth the pool does not account for.
                long bytes = MeasureAlloc(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                    snapshot.Cdt, noXs, noZs, 0, noCons, 0, out _, true, null, ctx.Pool),
                    warmup: 6, iterations: 3);
                TestContext.Out.WriteLine($"{mesh.Triangles.Length,9} {"-",9} {bytes,10}");
            }
        }

        #endregion

        [Test]
        public void TriggerGate_SizeScaling_AC0()
        {
            // Trigger (c): does the per-rebake volume scale into a problem on a much larger
            // map? Synthetic grids stand in for map size; the question is whether allocation
            // grows linearly (expected) and where it would land for a map several times Field.
            TestContext.Out.WriteLine("=== trigger (c): pooled rebake allocation vs map size ===");
            foreach (int half in new[] { 20, 40, 60, 80 })
            {
                var pts = new List<(int x, int z)>();
                for (int x = -half; x <= half; x++)
                    for (int z = -half; z <= half; z++)
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
                FPNavMesh baseMesh = FPNavMeshBuildPipeline.Build(
                    vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);

                var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false));
                long pooled = MeasureAlloc(() => FPNavMeshRebaker.Rebake(ctx, null), warmup: 8, iterations: 3);
                var m = Measure(() => FPNavMeshRebaker.Rebake(ctx, null), warmup: 8, iterations: 5);

                TestContext.Out.WriteLine(
                    $"{baseMesh.Triangles.Length,7} tris: alloc {pooled / 1024.0,8:F0} KB   " +
                    $"{pooled / (double)baseMesh.Triangles.Length,6:F0} B/tri   time {m.minMs,7:F1} ms");
            }
        }

        [Test]
        public void AdjacencySortSegment_RadixVsComparison()
        {
            // The gate for the radix change, measured on the segment it replaces rather than on
            // the function that contains it — BuildAdjacency keeps ~2.2 ms of record building,
            // group scan, pairing and portal work that no sort can touch.
            //
            // Same process, interleaved, same input array: the run-to-run spread is ~10% and the
            // whole-rebake effect of this change is about the same size, so a before/after
            // comparison across runs cannot decide anything.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            var ctx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            FPNavMesh built = FPNavMeshRebaker.Rebake(ctx, null);

            var tris = new FPNavMeshTriangle[built.TriangleCount];
            built.Triangles.CopyTo(tris);
            int n = tris.Length * 3;

            // Pristine unsorted records, rebuilt before every sort — sorting an already-sorted
            // array is not the operation being measured, and introsort is much faster on one.
            var pristine = new FPNavMeshBuildPipeline.EdgeRecord[n];
            int maxIndex = 0;
            for (int t = 0; t < tris.Length; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    tris[t].GetEdgeVertices(e, out int va, out int vb);
                    int mn = va < vb ? va : vb, mx = va < vb ? vb : va;
                    if (mx > maxIndex)
                        maxIndex = mx;
                    pristine[t * 3 + e] = new FPNavMeshBuildPipeline.EdgeRecord(
                        ((long)mn << 32) | (uint)mx, t * 3 + e);
                }
            }
            var work = new FPNavMeshBuildPipeline.EdgeRecord[n];

            var refillOnly = Measure(() => Array.Copy(pristine, work, n));
            var refillPlusComparison = Measure(() =>
            {
                Array.Copy(pristine, work, n);
                Array.Sort(work, 0, n);
            });
            var refillPlusRadix = Measure(() =>
            {
                Array.Copy(pristine, work, n);
                FPNavMeshBuildPipeline.RadixSortByKey(work, n, maxIndex, ctx.Pool);
            });

            double comparisonMs = refillPlusComparison.minMs - refillOnly.minMs;
            double radixMs = refillPlusRadix.minMs - refillOnly.minMs;

            TestContext.Out.WriteLine(
                $"=== adjacency sort segment, Field (n = {n} records, {maxIndex + 1} buckets) ===");
            Report("refill only (subtracted from both)", refillOnly);
            TestContext.Out.WriteLine($"{"comparison sort (Array.Sort)",-46} min {comparisonMs,9:F3} ms");
            TestContext.Out.WriteLine($"{"radix sort (2-pass counting)",-46} min {radixMs,9:F3} ms");
            TestContext.Out.WriteLine(
                $"=> {comparisonMs / radixMs:F1}x faster, {comparisonMs - radixMs:F3} ms saved per rebake");

            var verts = new FPVector3[built.VertexCount];
            built.Vertices.CopyTo(verts);
            var whole = Measure(() => FPNavMeshBuildPipeline.BuildAdjacency(tris, tris.Length, verts, ctx.Pool));
            var wholeRef = Measure(() =>
                FPNavMeshBuildPipeline.BuildAdjacencyComparisonReference(tris, tris.Length, verts, ctx.Pool));
            Report("BuildAdjacency (radix)", whole);
            Report("BuildAdjacency (comparison)", wholeRef);
        }

        [Test]
        public void OneMoreBuilding_VsElevenAtOnce()
        {
            // Both rebakes end at the same 11-building mesh. What differs is how much of it the
            // previous mesh already had — one building's worth, or none of it. That is the only
            // knob the shortcut actually responds to, so this is where its sensitivity shows.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);

            var accepted = DiscoverPlacements(mesh, 11);
            if (accepted.Count < 11)
                Assert.Ignore($"only {accepted.Count} placeable centres found");
            var ten = accepted.GetRange(0, 10).ToArray();
            var eleven = accepted.GetRange(0, 11).ToArray();
            var none = Array.Empty<FPBuildingRect>();

            // Warmed twice so the retirement chain has a generation to hand back; otherwise the
            // measured rebake allocates its whole output fresh.
            (double ms, int patched) TimeStep(FPBuildingRect[] from, FPBuildingRect[] to)
            {
                double best = double.MaxValue;
                int patched = 0;
                var sw = new Stopwatch();
                for (int rep = 0; rep < 12; rep++)
                {
                    var ctx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
                    ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, from, null));
                    ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, from, null));
                    int before = ctx.PatchOutcome.Incremental;

                    sw.Restart();
                    FPNavMesh m = FPNavMeshRebaker.Rebake(ctx, to, null);
                    sw.Stop();
                    ctx.CommitSwap(m);

                    if (rep >= 4 && sw.Elapsed.TotalMilliseconds < best)
                    {
                        best = sw.Elapsed.TotalMilliseconds;
                        patched = ctx.PatchOutcome.Incremental - before;
                    }
                }
                return (best, patched);
            }

            var plusOne = TimeStep(ten, eleven);
            var allEleven = TimeStep(none, eleven);

            // No previous mesh at all — the full rebuild, for scale.
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(mesh, null, prewarm: false);
            var scratch = Measure(() => FPNavMeshRebaker.Rebake(snapshot, eleven, null));

            TestContext.Out.WriteLine(
                $"=== reaching an 11-building mesh, Field ({mesh.Triangles.Length} tris) ===");
            TestContext.Out.WriteLine(
                $"{"from 10 buildings (+1)",-46} min {plusOne.ms,9:F3} ms   shortcut {plusOne.patched}");
            TestContext.Out.WriteLine(
                $"{"from 0 buildings (+11)",-46} min {allEleven.ms,9:F3} ms   shortcut {allEleven.patched}");
            Report("no previous mesh (full rebuild)", scratch);
        }

        /// <summary>Placeable centres on a real asset — the widest triangles are open floor, and a
        /// hand-picked centre is usually rejected.</summary>
        private static List<FPBuildingRect> DiscoverPlacements(FPNavMesh mesh, int want)
        {
            var ctx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            var accepted = new List<FPBuildingRect>();
            var centres = new List<FPVector2>();
            FP64 half = FP64.FromDouble(0.5);

            var order = new List<int>();
            for (int i = 0; i < mesh.Triangles.Length; i++)
                order.Add(i);
            order.Sort((a, b) => mesh.Triangles[b].area.RawValue.CompareTo(mesh.Triangles[a].area.RawValue));

            foreach (int t in order)
            {
                if (accepted.Count >= want)
                    break;
                FPVector2 c = mesh.Triangles[t].centerXZ;
                FP64 cx = FPGeoPredicates.Quantize(c.x), cz = FPGeoPredicates.Quantize(c.y);

                bool tooClose = false;
                foreach (FPVector2 p in centres)
                {
                    if (FP64.Abs(p.x - cx) < FP64.FromInt(4) && FP64.Abs(p.y - cz) < FP64.FromInt(4))
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose)
                    continue;

                accepted.Add(new FPBuildingRect(cx - half, cz - half, cx + half, cz + half, FP64.Zero));
                try
                {
                    FPNavMeshRebaker.Rebake(ctx, accepted.ToArray(), null);
                    centres.Add(new FPVector2(cx, cz));
                }
                catch (Exception) { accepted.RemoveAt(accepted.Count - 1); }
            }
            return accepted;
        }

        [Test]
        public void IncrementalPatch_VsFullBuild()
        {
            // Same process, same building set, two paths — the full build is what the patch has to
            // beat AND what it has to agree with, so measuring them side by side is the only
            // comparison that means anything.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);

            // A placement the asset accepts, found the same way RebakeCost_ByBuildingCount finds
            // them: the widest triangle is open floor.
            var probe = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            FPNavMesh empty = FPNavMeshRebaker.Rebake(probe, null);
            int widest = 0;
            for (int t = 1; t < empty.TriangleCount; t++)
            {
                if (empty.Triangles[t].area.RawValue > empty.Triangles[widest].area.RawValue)
                    widest = t;
            }
            FP64 bx = FPGeoPredicates.Quantize(empty.Triangles[widest].centerXZ.x);
            FP64 bz = FPGeoPredicates.Quantize(empty.Triangles[widest].centerXZ.y);
            var one = new[] { new FPBuildingRect(bx - FP64.Half, bz - FP64.Half, bx + FP64.Half, bz + FP64.Half, FP64.Zero) };

            // Patched: a context whose swap chain is caught up, so every rebake after the first
            // has a previous mesh to diff against.
            var patchCtx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            Action patched = () =>
            {
                FPNavMesh m = FPNavMeshRebaker.Rebake(patchCtx, one);
                patchCtx.CommitSwap(m);
            };
            // Full, POOLED: a context that never commits keeps the same buffer pool but always
            // takes the full path, so this isolates the patch from the pooling it rides on. The
            // unpooled overload below is the same code without either.
            var fullCtx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            FPNavMeshRebaker.Rebake(fullCtx, one);       // leaves the chain uncommitted from here on
            Action fullPooled = () => FPNavMeshRebaker.Rebake(fullCtx, one);

            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(mesh, null, prewarm: false);
            Action fullUnpooled = () => FPNavMeshRebaker.Rebake(snapshot, one, null);

            var mPatched = Measure(patched);
            var mFullPooled = Measure(fullPooled);
            var mFullUnpooled = Measure(fullUnpooled);

            TestContext.Out.WriteLine(
                $"=== incremental patch vs full build, Field ({mesh.Triangles.Length} tris, 1 building) ===");
            Report("full build, unpooled", mFullUnpooled);
            Report("full build, pooled", mFullPooled);
            Report("patched", mPatched);
            TestContext.Out.WriteLine(
                $"=> vs pooled full: {mFullPooled.minMs / mPatched.minMs:F2}x, "
                + $"{mFullPooled.minMs - mPatched.minMs:F3} ms saved");
            Assert.AreEqual(0, fullCtx.PatchOutcome.Incremental,
                "the full-path baseline must not have patched — it would not be a baseline");
            TestContext.Out.WriteLine(
                $"patched {patchCtx.PatchOutcome.Incremental}, fallbacks: vertex-shift "
                + $"{patchCtx.PatchOutcome.FallbackVertexShift}, geometry "
                + $"{patchCtx.PatchOutcome.FallbackGridGeometry}");
            Assert.Greater(patchCtx.PatchOutcome.Incremental, 0,
                "the patch never ran — this measured the full path against itself");
        }

        [Test]
        public void ResidualTimeBreakdown_PooledRebake()
        {
            // Where the ~11 ms that survives every optimisation actually goes. The answer decides
            // whether a LOCAL rebake — re-triangulating only the neighbourhood of the building
            // instead of the whole stage — could pay: it can only ever attack the CDT stage, so
            // whatever the build pipeline costs is a floor it cannot go under.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(mesh);
            var ctx = new FPNavMeshRebakeContext(snapshot);
            var noXs = Array.Empty<long>();
            var noZs = Array.Empty<long>();
            var noCons = Array.Empty<int>();

            TestContext.Out.WriteLine(
                $"=== time breakdown of the pooled rebake, Field ({mesh.Triangles.Length} tris) ===");

            // Two totals in one run so the split below can be attributed to either path without
            // comparing across runs (the spread between runs is about the size of the effect).
            var patchCtx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            var mPatched = Measure(() =>
            {
                FPNavMesh m = FPNavMeshRebaker.Rebake(patchCtx, null);
                patchCtx.CommitSwap(m);
            });

            var fullCtx = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            FPNavMeshRebaker.Rebake(fullCtx, null);       // never committed -> always full rebuild
            var mTotal = Measure(() => FPNavMeshRebaker.Rebake(fullCtx, null));

            Report("total, full rebuild", mTotal);
            Report("total, patched", mPatched);
            Assert.Greater(patchCtx.PatchOutcome.Incremental, 0, "the patched total did not patch");
            Assert.AreEqual(0, fullCtx.PatchOutcome.Incremental, "the full total patched");

            var mCdt = Measure(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, noXs, noZs, 0, noCons, 0, out _, true, null, ctx.Pool));
            Report("  CDT TriangulateFromSnapshot", mCdt,
                $"{mCdt.minMs / mTotal.minMs * 100:F0}% of total");

            var mClone = Measure(() => new FPConstrainedDelaunay.Cdt(snapshot.Cdt, noXs, noZs, 0, null));
            Report("    clone ctor (+ ghost rebase)", mClone);
            TestContext.Out.WriteLine(
                $"{"    Extract residual",-46} min {mCdt.minMs - mClone.minMs,9:F3} ms   "
                + "(0-1 BFS parity + canonicalize + output)");

            int[] tris = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, noXs, noZs, 0, noCons, 0, out int trisCount, true, null);
            var vertices = new FPVector3[snapshot.BaseXs.Length];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new FPVector3(
                    FPGeoPredicates.Unsnap(snapshot.BaseXs[i]), snapshot.BaseYs[i],
                    FPGeoPredicates.Unsnap(snapshot.BaseZs[i]));
            var areas = new int[trisCount / 3];

            var mBuild = Measure(() => FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, new ReadOnlySpan<int>(tris, 0, trisCount), areas, mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb,
                ctx.Pool));
            Report("  BuildFromConformingTriangulation", mBuild,
                $"{mBuild.minMs / mTotal.minMs * 100:F0}% of total");

            // Inside the build pipeline: adjacency is the one stage with a public seam, and it is
            // the one an incremental patch would have to replace.
            FPNavMesh built = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, new ReadOnlySpan<int>(tris, 0, trisCount), areas, mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb);
            var triCopy = new FPNavMeshTriangle[built.TriangleCount];
            built.Triangles.CopyTo(triCopy);
            var vertCopy = new FPVector3[built.VertexCount];
            built.Vertices.CopyTo(vertCopy);
            var mAdj = Measure(() => FPNavMeshBuildPipeline.BuildAdjacency(
                triCopy, triCopy.Length, vertCopy, ctx.Pool));
            Report("    (of which) BuildAdjacency + portals", mAdj,
                $"{mAdj.minMs / mBuild.minMs * 100:F0}% of the build stage");
            TestContext.Out.WriteLine(
                $"{"    (of which) struct fill + bounds + grid",-46} min {mBuild.minMs - mAdj.minMs,9:F3} ms");

            double residual = mTotal.minMs - mCdt.minMs - mBuild.minMs;
            TestContext.Out.WriteLine(
                $"{"  rebaker residual",-46} min {residual,9:F3} ms   "
                + "(validation + vertices[] + mesh assembly)");
            TestContext.Out.WriteLine(
                $"{"  patch stage (by difference)",-46} min {mPatched.minMs - mCdt.minMs - residual,9:F3} ms   "
                + "(diff + survivor copy + re-pair + grid splice)");
            TestContext.Out.WriteLine(
                $"local-rebake ceiling: the build pipeline is {mBuild.minMs:F2} ms and is proportional "
                + $"to the WHOLE mesh, so localizing the CDT cannot take the rebake below it "
                + $"({mBuild.minMs / mTotal.minMs * 100:F0}% of today's cost).");

            // How much of the output a single building actually disturbs — the number that decides
            // whether patching the build stage incrementally could pay. Canonical output is sorted
            // by vertex triple, so old and new can be diffed by a linear merge.
            var ctx2 = FPNavMeshRebaker.CreateContext(mesh, null, prewarm: false);
            FPNavMesh before = FPNavMeshRebaker.Rebake(ctx2, null);
            // Same discovery as RebakeCost_ByBuildingCount: the biggest triangle is open floor.
            int widest = 0;
            for (int t = 1; t < before.TriangleCount; t++)
            {
                if (before.Triangles[t].area.RawValue > before.Triangles[widest].area.RawValue)
                    widest = t;
            }
            FP64 bx = FPGeoPredicates.Quantize(before.Triangles[widest].centerXZ.x);
            FP64 bz = FPGeoPredicates.Quantize(before.Triangles[widest].centerXZ.y);
            var one = new FPBuildingRect(
                bx - FP64.Half, bz - FP64.Half, bx + FP64.Half, bz + FP64.Half, FP64.Zero);
            FPNavMesh after;
            try { after = FPNavMeshRebaker.Rebake(ctx2, new[] { one }, null); }
            catch (Exception e)
            {
                TestContext.Out.WriteLine($"diff probe skipped — placement rejected: {e.Message}");
                return;
            }

            var seen = new HashSet<(int, int, int)>();
            for (int t = 0; t < before.TriangleCount; t++)
                seen.Add((before.Triangles[t].v0, before.Triangles[t].v1, before.Triangles[t].v2));
            int added = 0;
            for (int t = 0; t < after.TriangleCount; t++)
            {
                if (!seen.Contains((after.Triangles[t].v0, after.Triangles[t].v1, after.Triangles[t].v2)))
                    added++;
            }
            TestContext.Out.WriteLine(
                $"one building disturbs {added} of {after.TriangleCount} triangles "
                + $"({added * 100.0 / after.TriangleCount:F3}%); the other "
                + $"{after.TriangleCount - added} keep their vertex triple but not their index");
        }

        [Test]
        public void AllocBreakdown_SnapshotRebake_AC0()
        {
            // Decomposes the snapshot-path rebake allocation (~12MB at 22k tris) per stage, to
            // confirm the largest items are the ones the pool targets. Measured at 0 buildings —
            // hole count barely moves it (4 vertices per building); what scales is the base size.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(mesh);

            var noXs = Array.Empty<long>();
            var noZs = Array.Empty<long>();
            var noCons = Array.Empty<int>();

            TestContext.Out.WriteLine(
                $"=== alloc breakdown, Field ({mesh.Triangles.Length} tris, {mesh.Vertices.Length} verts, 0 bldg) ===");

            long total = MeasureAlloc(() => FPNavMeshRebaker.Rebake(snapshot, null));
            ReportAlloc("total Rebake(snapshot)", total);

            long cdt = MeasureAlloc(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, noXs, noZs, 0, noCons, 0, out _, true, null));
            ReportAlloc("  CDT TriangulateFromSnapshot", cdt, $"{cdt * 100.0 / total:F0}% of total");

            long clone = MeasureAlloc(() => new FPConstrainedDelaunay.Cdt(snapshot.Cdt, noXs, noZs, 0, null));
            ReportAlloc("    clone ctor (+ ghost rebase)", clone);

            long cloneInsert = MeasureAlloc(() =>
            {
                var c = new FPConstrainedDelaunay.Cdt(snapshot.Cdt, noXs, noZs, 0, null);
                c.InsertRange(snapshot.Cdt.RealCount, snapshot.Cdt.RealCount);
                c.InsertConstraints(noCons, 0);
            });
            ReportAlloc("    clone + insert + constraints", cloneInsert);
            ReportAlloc("    Extract residual", cdt - cloneInsert, "(0-1 BFS deque + canonicalize + output)");

            // Pipeline stage, fed exactly as the rebaker feeds it.
            int[] tris = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, noXs, noZs, 0, noCons, 0, out int trisCount, true, null);
            var vertices = new FPVector3[snapshot.BaseXs.Length];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new FPVector3(
                    FPGeoPredicates.Unsnap(snapshot.BaseXs[i]), snapshot.BaseYs[i],
                    FPGeoPredicates.Unsnap(snapshot.BaseZs[i]));
            var areas = new int[trisCount / 3];

            long build = MeasureAlloc(() => FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, new ReadOnlySpan<int>(tris, 0, trisCount), areas, mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb));
            ReportAlloc("  BuildFromConformingTriangulation", build, $"{build * 100.0 / total:F0}% of total");

            // The adjacency edge map in isolation (same capacity and the same
            // add/match/remove traffic as BuildAdjacency, without the portal math).
            long adjacency = MeasureAlloc(() =>
            {
                var edgeMap = new Dictionary<long, (int, int)>(trisCount);
                for (int t = 0; t * 3 < trisCount; t++)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int va = tris[t * 3 + e];
                        int vb = tris[t * 3 + (e + 1) % 3];
                        int minV = va < vb ? va : vb;
                        int maxV = va < vb ? vb : va;
                        long key = ((long)minV << 32) | (uint)maxV;
                        if (edgeMap.ContainsKey(key)) edgeMap.Remove(key);
                        else edgeMap[key] = (t, e);
                    }
                }
            });
            ReportAlloc("    (of which) adjacency Dictionary", adjacency, "pooling target");

            long triArray = MeasureAlloc(() => GC.KeepAlive(new FPNavMeshTriangle[tris.Length / 3]));
            ReportAlloc("    (of which) triangle struct array", triArray, "final output — output recycling, not work buffers");

            FPNavMesh built = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, tris, areas, mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb);
            int totalCells = built.GridWidth * built.GridHeight;
            long gridLists = MeasureAlloc(() =>
            {
                var cellLists = new List<int>[totalCells];
                for (int i = 0; i < totalCells; i++)
                    cellLists[i] = new List<int>();
                GC.KeepAlive(cellLists);
            });
            ReportAlloc("    (of which) grid per-cell List headers", gridLists,
                $"{totalCells} cells, excl. their growth arrays");

            ReportAlloc("  rebaker residual", total - cdt - build, "(vertices[] + areas[] + hole lists + mesh assembly)");

            // One pooled rebake against the 4MB target, and the steady-state increment once the
            // pool has stopped growing.
            var ctx = new FPNavMeshRebakeContext(snapshot);
            long pooled = MeasureAlloc(() => FPNavMeshRebaker.Rebake(ctx, null));
            TestContext.Out.WriteLine("--- pooled ---");
            ReportAlloc("total Rebake(snapshot, pool)", pooled,
                $"{(total - pooled) / 1024.0:F0} KB saved ({(total - pooled) * 100.0 / total:F0}%)");

            // v1 excludes the arrays the mesh keeps (reusing those is v2 — it needs a
            // "no stale reference to the old mesh" lifetime audit). So the floor a pooled
            // rebake can reach is the size of that final output, and the gate is how close
            // to the floor we got — not a round number picked before the breakdown existed.
            // triArray is measured, not computed: FPNavMeshTriangle is 120 bytes once FP64
            // alignment padding is counted, and hand-computing the stride silently inflated
            // this floor by 8 bytes/triangle (174 KB) the first time round.
            long floor =
                triArray +                                     // FPNavMeshTriangle[]
                (long)built.Vertices.Length * 24 +              // FPVector3
                (long)built.GridCells.Length * 4 +
                (long)built.GridTriangles.Length * 4;
            ReportAlloc("  final-output floor (v1 excludes)", floor,
                $"tris {built.Triangles.Length}, verts {built.Vertices.Length}, gridTris {built.GridTriangles.Length}");
            ReportAlloc("  work-buffer residual above floor", pooled - floor);

            // Where the residual lives, so it is a known quantity rather than a leftover.
            long cdtPooled = MeasureAlloc(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, noXs, noZs, 0, noCons, 0, out _, true, null, ctx.Pool));
            long buildPooled = MeasureAlloc(() => FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, new ReadOnlySpan<int>(tris, 0, trisCount), areas, mesh.GridCellSize, null,
                mesh.BakeAgentRadius, mesh.BakeMaxSlopeDeg, mesh.BakeAgentHeight, mesh.BakeAgentClimb,
                ctx.Pool));
            ReportAlloc("    pooled CDT stage", cdtPooled, $"Extract output int[] = {trisCount * 4L / 1024} KB of it");
            ReportAlloc("    pooled Build stage", buildPooled,
                $"final output {(triArray + built.GridCells.Length * 4L + built.GridTriangles.Length * 4L) / 1024} KB of it");
            ReportAlloc("    pooled rebaker residual", pooled - cdtPooled - buildPooled,
                $"vertices[] {built.Vertices.Length * 24L / 1024} KB (+25% rent headroom; areas is the shared zero array)");
            // The old form of this gate ("pooled - floor < 400 KB") no longer states anything
            // true, and had in fact been failing unnoticed at 974 KB — nobody saw it because the
            // whole fixture is [Explicit]. Two reasons it is wrong now: its stated expectation
            // ("≈ Extract output + areas, both deliberately unpooled") describes a state that
            // pooling those two removed, and the residual it measures is
            // dominated by the 25% output headroom that this harness pays on EVERY rebake because
            // it never calls CommitSwap — by design, not by regression.
            //
            // What is still worth asserting is the claim the work-buffer pool actually makes: the
            // CDT stage's buffers are reused, so that stage allocates essentially nothing. The
            // steady-state gates for the whole rebake live in FPNavMeshRebakeAllocGateTests.
            Assert.Less(cdtPooled, 64L * 1024,
                "with a pool the CDT stage's work buffers are reused, so the stage allocates "
                + "essentially nothing — its output buffer is pooled too");

            var mUnpooled = Measure(() => FPNavMeshRebaker.Rebake(snapshot, null));
            var mPooled = Measure(() => FPNavMeshRebaker.Rebake(ctx, null));
            Report("time, unpooled", mUnpooled);
            Report("time, pooled", mPooled, "must not regress");
        }

        [Test]
        public void SyntheticScaling_RebakeCost()
        {
            TestContext.Out.WriteLine("=== Synthetic scaling: N x N grid, 1 center building ===");
            foreach (int nSide in new[] { 10, 20, 40 })
            {
                var pts = new List<(int x, int z)>();
                int half = nSide / 2;
                for (int x = -half; x <= half; x++)
                {
                    for (int z = -half; z <= half; z++)
                        pts.Add((x, z));
                }
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
                FPNavMesh baseMesh = FPNavMeshBuildPipeline.Build(
                    vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);

                var building = new[]
                {
                    new FPBuildingRect(FP64.FromInt(-1), FP64.FromInt(-1), FP64.FromInt(1), FP64.FromInt(1), FP64.Zero),
                };
                var m = Measure(() => FPNavMeshRebaker.Rebake(baseMesh, building));
                Report($"{nSide}x{nSide} grid ({baseMesh.Triangles.Length} tris)", m);
            }
        }

        [Test]
        public unsafe void ShapeCost_AtTheAgentLayer()
        {
            // What a rounder footprint costs the AGENT layer, which is where it is actually paid:
            // ORCA obstacles are extracted from the navmesh boundary, so a building contributes as
            // many segments as it has edges — 4 for a box, 6 for a hexagon.
            //
            // The two runs are interleaved, so machine drift lands on both rather than on whichever
            // ran second.
            const int Buildings = 25;
            const int Agents = 64;
            const int Ticks = 200;
            const int Repeats = 7;

            FPNavMesh Slab()
            {
                var pts = new List<(int x, int z)>();
                for (int x = -20; x <= 20; x++)
                    for (int z = -20; z <= 20; z++)
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

            // Same bounding box for both families, so the only variable is the edge count.
            const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
            FPNavMesh Carve(bool hexagon)
            {
                var b = new FPBuildingShapeCatalogBuilder();
                int shape = hexagon
                    ? b.AddHexagon(Unit)
                    : b.Add(new[] { -Unit, Unit, Unit, -Unit }, new[] { -Unit, -Unit, Unit, Unit });
                FPNavMeshRebakeContext ctx = FPNavMeshRebaker.CreateContext(
                    Slab(), null, prewarm: false, shapeCatalog: b.Build());

                var places = new List<FPBuildingPlacement>();
                for (int i = 0; i < Buildings; i++)
                {
                    int gx = (i % 5) * 6 - 12, gz = (i / 5) * 6 - 12;
                    places.Add(new FPBuildingPlacement(
                        shape, FP64.FromInt(gx), FP64.FromInt(gz), FP64.Zero));
                }
                return FPNavMeshRebaker.RebakePlacements(ctx, places.ToArray(), null);
            }

            (double ms, int obstacles) Run(FPNavMesh mesh)
            {
                var query = new FPNavMeshQuery(mesh, null);
                var positions = new FPVector3[Agents];
                var destinations = new FPVector3[Agents];
                for (int i = 0; i < Agents; i++)
                {
                    // Deterministic spread, each agent crossing to the opposite corner so it stays
                    // Moving for the whole run and actually meets the buildings on the way.
                    int gx = (i % 8) * 4 - 14, gz = (i / 8) * 4 - 14;
                    positions[i] = new FPVector3(FP64.FromInt(gx), FP64.Zero, FP64.FromInt(gz));
                    destinations[i] = new FPVector3(FP64.FromInt(-gx), FP64.Zero, FP64.FromInt(-gz));
                }
                // Built here rather than via NavAgentTestHelper: its frame holds 32 entities and
                // this measurement wants 64.
                var frame = new Frame(Agents * 2, null);
                var entities = new EntityRef[Agents];
                for (int i = 0; i < Agents; i++)
                {
                    entities[i] = frame.CreateEntity();
                    frame.Add(entities[i], default(NavAgentComponent));
                    ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                    NavAgentComponent.Init(ref nav, positions[i]);
                    nav.Speed = FP64.FromInt(2);
                    nav.Radius = FP64.FromDouble(0.5);
                    nav.CurrentTriangleIndex = query.FindTriangle(positions[i].ToXZ(), positions[i].y);
                    NavAgentComponent.SetDestination(ref nav, destinations[i]);
                }

                FPNavAgentSystem system = NavAgentTestHelper.CreateSystem(mesh, null);
                system.SetAvoidance(new FPNavAvoidance());   // without this ORCA never runs
                system.LoadNavMeshObstacles();
                system.ReseedAgents(ref frame, entities, entities.Length);

                var sw = new Stopwatch();
                sw.Start();
                for (int t = 0; t < Ticks; t++)
                    system.Update(ref frame, entities, entities.Length, t, NavAgentTestHelper.DT);
                sw.Stop();
                return (sw.Elapsed.TotalMilliseconds / Ticks, system.DebugObstacleCount);
            }

            FPNavMesh box = Carve(hexagon: false), hex = Carve(hexagon: true);
            Run(box); Run(hex);   // warm up the JIT on both paths before recording

            var boxMs = new List<double>();
            var hexMs = new List<double>();
            int boxObs = 0, hexObs = 0;
            for (int r = 0; r < Repeats; r++)
            {
                var rb = Run(box); boxMs.Add(rb.ms); boxObs = rb.obstacles;
                var rh = Run(hex); hexMs.Add(rh.ms); hexObs = rh.obstacles;
            }
            boxMs.Sort();
            hexMs.Sort();

            TestContext.Out.WriteLine(
                $"=== shape cost at the agent layer: {Buildings} buildings, {Agents} agents, "
                + $"{Ticks} ticks, {Repeats} interleaved repeats ===");
            TestContext.Out.WriteLine(
                $"box (4 edges)   obstacles {boxObs,4}   min {boxMs[0]:F4} ms   median {boxMs[Repeats / 2]:F4} ms"
                + $"   spread {(boxMs[Repeats - 1] - boxMs[0]) / boxMs[0] * 100:F1}%");
            TestContext.Out.WriteLine(
                $"hex (6 edges)   obstacles {hexObs,4}   min {hexMs[0]:F4} ms   median {hexMs[Repeats / 2]:F4} ms"
                + $"   spread {(hexMs[Repeats - 1] - hexMs[0]) / hexMs[0] * 100:F1}%");
            TestContext.Out.WriteLine(
                $"delta: obstacles +{(hexObs - boxObs) * 100.0 / boxObs:F1}%   "
                + $"min +{(hexMs[0] - boxMs[0]) / boxMs[0] * 100:F1}%   "
                + $"median +{(hexMs[Repeats / 2] - boxMs[Repeats / 2]) / boxMs[Repeats / 2] * 100:F1}%");
        }

        [Test]
        public unsafe void ReseedAgents_Cost32Agents()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 5)
            {
                for (int z = -10; z <= 10; z += 5)
                    pts.Add((x, z));
            }
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
            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);

            var query = new FPNavMeshQuery(mesh, null);
            var system = new FPNavAgentSystem(mesh, query,
                new FPNavMeshPathfinder(mesh, query, null), new FPNavMeshFunnel(mesh, query, null), null);

            int count = NavAgentTestHelper.MAX_ENTITIES;
            var positions = new FPVector3[count];
            var triangles = new int[count];
            var rng = new SysRandom(96040);
            for (int i = 0; i < count; i++)
            {
                positions[i] = new FPVector3(
                    FP64.FromDouble(rng.NextDouble() * 16 - 8), FP64.Zero, FP64.FromDouble(rng.NextDouble() * 16 - 8));
                triangles[i] = query.FindTriangle(positions[i].ToXZ(), positions[i].y);
            }
            Frame frame = NavAgentTestHelper.CreateFrameWithAgents(positions, triangles, out EntityRef[] entities);

            var frameRef = frame;
            var m = Measure(() => system.ReseedAgents(ref frameRef, entities, count), warmup: 3, iterations: 9);
            Report($"ReseedAgents x{count}", m);
        }

        [Test]
        public void SwapCost_AllocationAgainstRebake()
        {
            // What a placement ACTUALLY allocates. The rebake is only step 2 of the three the
            // quick start prescribes, and the other steps are not the small ones: the query,
            // pathfinder and funnel are each sized from the triangle count, and SwapNavMesh
            // re-extracts the ORCA obstacles through LoadNavMeshObstacles.
            //
            // This exists because the published rebake documentation quotes it. That text used to
            // carry the rebake
            // figure alone under the heading "The cost of placing a building", which reads as
            // "a placement is not a garbage-collection event" — true of the rebake and false of
            // the placement it was standing for. The gate at the bottom is what keeps the
            // document and the code from drifting apart again.
            //
            // Avoidance is wired because the shipped sample wires it (Brawler, both the client
            // and the dedicated server call SetAvoidance) — without it LoadNavMeshObstacles
            // early-returns and the third column would read zero for a reason no game has.
            string root = RepoRoot();
            string[] assets =
            {
                "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes",
                "Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes",
                "Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes",
            };
            TestContext.Out.WriteLine(
                "=== allocation per placement: rebake vs swap, both overloads ===");

            long fieldRebake = 0, fieldSwap = 0, fieldSwapOld = 0;
            foreach (string rel in assets)
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path))
                {
                    TestContext.Out.WriteLine($"{rel}: MISSING — skipped");
                    continue;
                }
                FPNavMesh baseMesh = FPNavMeshSerializer.Deserialize(path);
                string name = Path.GetFileNameWithoutExtension(rel);

                FPNavMeshRebakeContext ctx;
                try
                {
                    ctx = FPNavMeshRebaker.CreateContext(baseMesh);
                }
                catch (NotSupportedException e)
                {
                    TestContext.Out.WriteLine($"{name}: NOT SUPPORTED — {e.Message}");
                    continue;
                }

                // Drive the seam the way a match does — rebake, install, repeat — so the
                // recycling chain is full before anything is measured.
                for (int i = 0; i < 3; i++)
                    ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null));

                long rebake = MeasureAlloc(() => ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, null)));

                // A live agent system on the current mesh, obstacles already loaded once, so the
                // swap below is measured in steady state rather than at first load.
                FPNavMesh cur = FPNavMeshRebaker.Rebake(ctx, null);
                var warmQuery = new FPNavMeshQuery(cur, null);
                var system = new FPNavAgentSystem(
                    cur, warmQuery, new FPNavMeshPathfinder(cur, warmQuery, null),
                    new FPNavMeshFunnel(cur, warmQuery, null), null);
                system.SetAvoidance(new FPNavAvoidance());
                system.LoadNavMeshObstacles();
                ctx.CommitSwap(cur);

                // Both overloads, side by side. The four-argument form is what a game had to
                // write before the rebind existed: build the trio, hand it over. The one-argument
                // form rebinds what the system already holds. Measuring both in one run is what
                // turns this from a number that gets stale into a gate — a regression in the
                // rebind shows up as the two costs converging.
                //
                // Warmup 4 rather than the harness default: allocation volume is not tier
                // dependent and it is byte-identical from the second call on (asserted below).
                FPNavMesh next = FPNavMeshRebaker.Rebake(ctx, null);
                FPNavMeshQuery q = null;
                FPNavMeshPathfinder p = null;
                FPNavMeshFunnel f = null;
                long oldWay = MeasureAlloc(() =>
                {
                    q = new FPNavMeshQuery(next, null);
                    p = new FPNavMeshPathfinder(next, q, null);
                    f = new FPNavMeshFunnel(next, q, null);
                    system.SwapNavMesh(next, q, p, f);
                }, warmup: 4);
                long newWay = MeasureAlloc(() => system.SwapNavMesh(next), warmup: 4);
                ctx.CommitSwap(next);

                ReportAlloc($"{name} ({baseMesh.Triangles.Length} tris) rebake + CommitSwap", rebake);
                ReportAlloc($"{name} swap — 4-arg (build trio + install)", oldWay, "before the rebind");
                ReportAlloc($"{name} swap — 1-arg (rebind in place)", newWay, "after");
                ReportAlloc($"{name} TOTAL per placement", rebake + newWay,
                    $"swap is {newWay / (double)rebake:F1}x the rebake; the rebind saved {(oldWay - newWay) / 1024.0:F0} KB");

                Assert.Less(newWay, oldWay,
                    $"{name}: rebinding must allocate less than rebuilding the trio. Equal costs " +
                    "mean the rebind stopped reusing its arrays.");

                if (name.StartsWith("Field", StringComparison.Ordinal))
                {
                    fieldRebake = rebake;
                    fieldSwap = newWay;
                    fieldSwapOld = oldWay;
                }
            }

            if (fieldRebake == 0)
                Assert.Ignore("Field asset missing — nothing to gate");

            // This gate has now read three ways, and the history is the point. It began as "the
            // swap dominates the rebake" (it did, by 283x). Rebinding the trio took out the
            // caller's 1,426 KB, and giving LoadObstacles an explicit count took out the
            // extractor's last 246 KB. So the claim is no longer a ratio at all: installing a
            // rebaked mesh allocates NOTHING, and a placement costs what the rebake costs.
            //
            // A kilobyte of slack rather than a hard zero, because the harness itself is inside
            // the measurement. Anything above it means a per-swap allocation came back.
            Assert.Less(fieldSwap, 1024,
                "installing a rebaked mesh must allocate nothing. Something in the swap path " +
                "started allocating again — check that the extractor still reads its scratch " +
                "through the counts rather than through Length, and rewrite the published numbers if "
                + "this is intended.");
            Assert.Less(fieldSwap * 4, fieldSwapOld,
                "the rebind should remove most of the swap cost, not shave it — if the gap has " +
                "narrowed this far, something started allocating per swap again");
        }

        [Test, Explicit]
        public void P0F_SliceBudgetCalibration()
        {
            // The shipped SliceBudgetUnits starts as a guess; this is what replaces it with a
            // number. Two things decide it, and only one of them is a choice:
            //
            //  - the FLOOR. Some units are indivisible, so no budget makes a step cheaper than the
            //    largest of them. If that floor already exceeds a frame, slicing cannot deliver on
            //    this stage at ANY setting, and the honest answer is to say so rather than tune;
            //  - the CEILING. Whatever is left has to fit in the frames the delay window buys.
            //
            // Repeated, minimum reported. A single run of this is worthless — the same
            // configuration was measured at 3.3 ms and at 18.2 ms minutes apart on an otherwise
            // idle machine. Minimum rather than mean because interference only ever adds.
            const int Reps = 5;
            TestContext.Out.WriteLine("=== P0 F: slice budget calibration (min of " + Reps + ") ===");
            TestContext.Out.WriteLine(
                $"{"tris",9} {"budget",9} {"steps",7} {"worst ms",9} {"total ms",9}");

            foreach (int half in new[] { 40, 80, 120, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                int tris = baseMesh.Triangles.Length;
                FPBuildingRect[] buildings = LatticePlacements(64, half);
                if (buildings == null)
                    continue;

                foreach (int budget in new[] { 1000, 5000, 20000, 100000, int.MaxValue })
                {
                    double bestWorst = double.MaxValue, bestTotal = double.MaxValue;
                    int steps = 0;

                    for (int rep = 0; rep < Reps; rep++)
                    {
                        // A context per run: the task holds its pool, and a fresh chain keeps the
                        // measurement about the rebake rather than about how well it patched.
                        var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: true);
                        if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var warm, out _))
                            break;
                        while (!warm.Step(int.MaxValue)) { }
                        warm.Install();
                        ctx.CommitSwap(warm.Result);

                        if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var task, out _))
                            break;

                        var sw = new System.Diagnostics.Stopwatch();
                        double worst = 0, total = 0;
                        int n = 0;
                        bool done = false;
                        while (!done)
                        {
                            sw.Restart();
                            done = task.Step(budget);
                            sw.Stop();
                            double ms = sw.Elapsed.TotalMilliseconds;
                            if (ms > worst) worst = ms;
                            total += ms;
                            n++;
                        }
                        task.Install();
                        ctx.CommitSwap(task.Result);

                        if (worst < bestWorst) bestWorst = worst;
                        if (total < bestTotal) bestTotal = total;
                        steps = n;
                    }

                    if (steps == 0)
                        continue;
                    string b = budget == int.MaxValue ? "whole" : budget.ToString();
                    TestContext.Out.WriteLine(
                        $"{tris,9} {b,9} {steps,7} {bestWorst,9:F2} {bestTotal,9:F2}");
                }
            }

            TestContext.Out.WriteLine(
                "worst = the single longest Step, which is what a frame actually feels. A worst "
                + "that does not fall as the budget falls is an indivisible unit, and no budget "
                + "reaches it.");
        }

        [Test]
        public unsafe void P0G_TwoContextResidentMemory()
        {
            // The gate for the boundary cache, which holds two meshes at once
            // by holding two contexts over ONE snapshot — the snapshot is the expensive half and it
            // is shared, so the second context costs a pool plus the mesh it keeps live. That is
            // negligible on Brawler's stage and is NOT obviously negligible on a big one, which is
            // why this is a gate rather than an assumption.
            //
            // Measured after each context has produced and committed once: a pool fills lazily, so
            // measuring a fresh one would report the cost of a cache that has not started working.
            TestContext.Out.WriteLine("=== P0 G: second context resident cost (shared snapshot) ===");
            TestContext.Out.WriteLine(
                $"{"tris",9} {"1 ctx KB",10} {"2 ctx KB",10} {"delta KB",10} {"mesh KB",9} {"delta/mesh",11}");

            foreach (int half in new[] { 40, 80, 120, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                int tris = baseMesh.Triangles.Length;
                FPBuildingRect[] buildings = LatticePlacements(64, half);
                if (buildings == null)
                    continue;

                var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh, null, prewarm: false);

                var ctxA = new FPNavMeshRebakeContext(snapshot);
                RunOnce(ctxA, buildings);
                long oneCtx = GC.GetTotalMemory(true);

                var ctxB = new FPNavMeshRebakeContext(snapshot);
                RunOnce(ctxB, buildings);
                long twoCtx = GC.GetTotalMemory(true);

                long meshBytes = (long)baseMesh.VertexCount * sizeof(FPVector3)
                               + (long)baseMesh.TriangleCount * sizeof(FPNavMeshTriangle);
                long delta = twoCtx - oneCtx;
                TestContext.Out.WriteLine(
                    $"{tris,9} {oneCtx / 1024.0,10:F0} {twoCtx / 1024.0,10:F0} {delta / 1024.0,10:F0} " +
                    $"{meshBytes / 1024.0,9:F0} {(double)delta / meshBytes,11:F2}");

                GC.KeepAlive(ctxA);
                GC.KeepAlive(ctxB);
            }

            TestContext.Out.WriteLine(
                "delta is the whole price of the second cached mesh: that context's pool plus the "
                + "one mesh it keeps live. Compare it against mesh KB — anything near 1.0 means the "
                + "pool is riding along for free and the cost IS the extra mesh.");

            static void RunOnce(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings)
            {
                if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var task, out _))
                    return;
                while (!task.Step(int.MaxValue)) { }
                task.Install();
                ctx.CommitSwap(task.Result);
            }
        }

        [Test, Explicit]
        public void Step0_PatchPassBreakdown()
        {
            // Inside the patch. Its total is known (9.84 ms at 205k after the degeneracy
            // scan moved to DEBUG); this is the distribution inside it, which decides between
            // DIVIDING the patch (A) and making it copy less (B).
            TestContext.Out.WriteLine("=== inside the incremental patch (min of 5) ===");

            var names = System.Enum.GetNames(typeof(FPNavMeshBuildPipeline.PatchPass));
            int passCount = (int)FPNavMeshBuildPipeline.PatchPass.Count;

            foreach (int half in new[] { 80, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                FPBuildingRect[] buildings = LatticePlacements(64, half);
                if (buildings == null)
                    continue;

                var best = new double[passCount];
                for (int i = 0; i < passCount; i++) best[i] = double.MaxValue;

                for (int rep = 0; rep < 5; rep++)
                {
                    var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: true);
                    ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings));   // a chain to patch

                    System.Array.Clear(FPNavMeshBuildPipeline.PatchPassTicks, 0, passCount);
                    FPNavMeshBuildPipeline.ProfilePatchPasses = true;
                    FPNavMesh m = FPNavMeshRebaker.Rebake(ctx, buildings);
                    FPNavMeshBuildPipeline.ProfilePatchPasses = false;
                    ctx.CommitSwap(m);

                    for (int i = 0; i < passCount; i++)
                    {
                        double ms = FPNavMeshBuildPipeline.PatchPassTicks[i]
                                  * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (ms < best[i]) best[i] = ms;
                    }
                }

                double sum = 0;
                for (int i = 0; i < passCount; i++) sum += best[i];
                TestContext.Out.WriteLine($"-- tris={baseMesh.Triangles.Length}, measured total {sum:F2} ms");
                for (int i = 0; i < passCount; i++)
                    TestContext.Out.WriteLine(
                        $"   {names[i],-18} {best[i],7:F3} ms  {100.0 * best[i] / sum,5:F1}%");
            }
        }

        [Test, Explicit]
        public void P0F_IsTheFloorTheIncrementalPatch()
        {
            // Attribution done at budget 1, which is the only granularity where the tag is
            // trustworthy: every phase decrements the budget to zero, so no two phases can chain
            // inside one Step. At budget 1000 the Extract tail, Vertices and Build all run in a
            // single call and the whole cost lands under whichever phase happened to be current on
            // entry — which is how an earlier version of this measurement reported a 12.85 ms
            // "Extract" step that was really the patch.
            //
            // The Build phase entry runs, in order: VerifyConformingContract (DEBUG only — absent
            // here, PRESENT in the Unity editor), CountDegenerate (unconditional, O(triangles)),
            // then either the incremental patch or the BuildTask constructor. Comparing a patchable
            // chain against a cold one therefore isolates the patch.
            TestContext.Out.WriteLine("=== P0 F: what the indivisible Build step is made of (budget 1, min of 2) ===");
            TestContext.Out.WriteLine($"{"tris",9} {"chain",10} {"Build ms",9} {"patched",8}");

            foreach (int half in new[] { 80, 120, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                FPBuildingRect[] buildings = LatticePlacements(64, half);
                if (buildings == null)
                    continue;

                double coldBuild = 0, warmBuild = 0;
                foreach (bool warmChain in new[] { true, false })
                {
                    double best = double.MaxValue;
                    int patched = 0;
                    for (int rep = 0; rep < 2; rep++)
                    {
                        var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: true);
                        if (warmChain)
                        {
                            if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var warm, out _))
                                break;
                            while (!warm.Step(int.MaxValue)) { }
                            warm.Install();
                            ctx.CommitSwap(warm.Result);
                        }

                        var oc = ctx.PatchOutcome;
                        int inc0 = oc.Incremental;
                        if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var task, out _))
                            break;

                        var sw = new System.Diagnostics.Stopwatch();
                        double build = 0;
                        bool done = false;
                        while (!done)
                        {
                            bool isBuildEntry = task.PhaseName == "Build";
                            sw.Restart();
                            done = task.Step(1);
                            sw.Stop();
                            if (isBuildEntry)
                                build = sw.Elapsed.TotalMilliseconds;
                        }
                        task.Install();
                        ctx.CommitSwap(task.Result);
                        if (build < best) best = build;
                        patched = oc.Incremental - inc0;
                    }

                    if (best == double.MaxValue)
                        continue;
                    if (warmChain) warmBuild = best; else coldBuild = best;
                    TestContext.Out.WriteLine(
                        $"{baseMesh.Triangles.Length,9} {(warmChain ? "patchable" : "cold"),10} "
                        + $"{best,9:F2} {patched,8}");
                }

                if (coldBuild > 0 && warmBuild > 0)
                    TestContext.Out.WriteLine(
                        $"{baseMesh.Triangles.Length,9} {"=>",10} CountDegenerate+ctor = {coldBuild:F2} ms, "
                        + $"patch = {warmBuild - coldBuild:F2} ms of the {warmBuild:F2} ms step");
            }
            TestContext.Out.WriteLine(
                "In the Unity editor VerifyConformingContract and VerifyPatchMatchesFullBuild also "
                + "run in this phase (both DEBUG-only), the latter rebuilding the entire mesh to "
                + "compare — editor numbers do not describe a shipped build.");
        }

        [Test, Explicit]
        public void P0F_WhichStepIsTheFloor()
        {
            // The sweep says the worst step does not shrink with the budget. This says WHICH step
            // that is, by position: run at budget 1 so every indivisible unit is its own step, then
            // report the slowest few and where they sit in the sequence. Extract steps come first
            // and are many; Vertices and BeginBuild are one step each, near the end.
            TestContext.Out.WriteLine("=== P0 F: where the floor lives ===");

            foreach (int half in new[] { 80, 160 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                FPBuildingRect[] buildings = LatticePlacements(64, half);
                if (buildings == null)
                    continue;

                var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: true);
                if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var warm, out _))
                    continue;
                while (!warm.Step(int.MaxValue)) { }
                warm.Install();
                ctx.CommitSwap(warm.Result);

                if (!FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var task, out _))
                    continue;

                var times = new System.Collections.Generic.List<double>();
                var phases = new System.Collections.Generic.List<string>();
                var sw = new System.Diagnostics.Stopwatch();
                bool done = false;
                while (!done)
                {
                    string phase = task.PhaseName;   // what THIS step is about to run
                    sw.Restart();
                    done = task.Step(1);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    phases.Add(phase);
                }
                task.Install();
                ctx.CommitSwap(task.Result);

                // Second pass over the same shape, keeping the per-step MINIMUM. One pass at this
                // granularity is a million stopwatch reads competing with whatever else the machine
                // is doing; the minimum of two is enough to tell a real 13 ms unit from a hiccup.
                if (FPNavMeshRebaker.TryBeginRebake(ctx, buildings, out var again, out _))
                {
                    int j = 0;
                    bool d2 = false;
                    while (!d2)
                    {
                        sw.Restart();
                        d2 = again.Step(1);
                        sw.Stop();
                        double ms = sw.Elapsed.TotalMilliseconds;
                        if (j < times.Count && ms < times[j]) times[j] = ms;
                        j++;
                    }
                    again.Install();
                    ctx.CommitSwap(again.Result);
                }

                int n = times.Count;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += times[i];

                var idx = new int[n];
                for (int i = 0; i < n; i++) idx[i] = i;
                System.Array.Sort(idx, (a, b) => times[b].CompareTo(times[a]));

                TestContext.Out.WriteLine(
                    $"tris={baseMesh.Triangles.Length} steps={n} total={sum:F2}ms");
                for (int k = 0; k < 5 && k < n; k++)
                {
                    int at = idx[k];
                    string where = at == n - 1 ? " (last)" : at >= n - 3 ? " (near end)" : "";
                    TestContext.Out.WriteLine(
                        $"   #{k + 1}: step {at} of {n} = {times[at]:F2} ms"
                        + $" [{100.0 * times[at] / sum:F1}% of total] entering {phases[at]}{where}");
                }
            }
        }

        /// <summary>
        /// T-9. The per-frame ghost preview against a growing accepted set — the shape an RTS
        /// placement UI actually has — under all three boundary policies, because the answer
        /// differs by an order of magnitude between them.
        ///
        /// <para><b>Two fixtures, not one, and that is forced.</b> A placement that clips a wall
        /// cannot be placed at all under <c>Reject</c>, and a placement <c>Reject</c> accepts is by
        /// definition not touching the boundary — so under <c>ClipOverlap</c> it carves its plain
        /// footprint and never enters the clip stage. There is no geometry that measures both
        /// paths. So the strict policies get footprints strictly inside the region, and
        /// <c>ClipOverlap</c> gets footprints that clip, which is the case it exists for.</para>
        ///
        /// <para>The assertion is a RATIO rather than a millisecond ceiling, because the ratio is
        /// what the caching and localisation layers changed and it survives being run on another
        /// machine. Measured on Field: 2.3x from N = 4 to N = 32 with them, 11.2x without.</para>
        /// </summary>
        [Test]
        public void GhostPreview_ClipOverlap_CostBarelyGrowsWithTheAcceptedSet()
        {
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");
            FPNavMesh baseMesh = FPNavMeshSerializer.Deserialize(path);

            // The two spot sets live in FieldPlacementSpots — the preview allocation gate quotes
            // the same placements in its own baseline, and a table copied into both files would let
            // one drift while the other's recorded numbers still claimed to describe it.
            TestContext.Out.WriteLine(
                $"=== Ghost preview, Field ({baseMesh.Triangles.Length} tris) ===");
            double at4 = 0, at32 = 0;
            foreach (FPBoundaryPlacementPolicy policy in new[]
                     {
                         FPBoundaryPlacementPolicy.Reject,
                         FPBoundaryPlacementPolicy.Touch,
                         FPBoundaryPlacementPolicy.ClipOverlap,
                     })
            {
                bool clipping = policy == FPBoundaryPlacementPolicy.ClipOverlap;
                var spots = clipping ? FieldPlacementSpots.Clipping : FieldPlacementSpots.Strict;
                var rules = new FPBuildingPlacementRules(allowBuildingTouch: false, policy);
                FPBuildingRect ghost = FieldPlacementSpots.Ghost(spots);

                foreach (int n in new[] { 1, 4, 16, 32 })
                {
                    FPBuildingRect[] existing = FieldPlacementSpots.Take(spots, n);
                    var context = FPNavMeshRebaker.CreateContext(baseMesh, null);
                    FPNavMeshRebaker.Rebake(context, existing, null, rules);

                    // The preview must be answering the question, not refusing early — a refusal
                    // that never reaches the boundary tests would measure the separation loop.
                    Assert.IsTrue(
                        FPNavMeshRebaker.TryValidateOne(context, ghost, out var rejection, rules),
                        $"the ghost was refused under {policy} at N = {n} ({rejection.Reason}), so "
                        + "this measures a rejection path rather than the real work");
                    // And the clipping row has to be clipping. Without this the fixture could drift
                    // to interior footprints and report the cheap path under the expensive label.
                    if (clipping)
                        Assert.Greater(context.Accepted.CachedTransitionStart[n], 0,
                            $"no existing building crosses a wall at N = {n}, so this row is "
                            + "measuring the identity carve rather than the clip stage");

                    var m = Measure(() => FPNavMeshRebaker.TryValidateOne(context, ghost, out _, rules));
                    long bytes = MeasureAlloc(
                        () => FPNavMeshRebaker.TryValidateOne(context, ghost, out _, rules));
                    Report($"preview under {policy}, {n} down", m, $"{bytes} B");
                    if (!clipping) continue;
                    if (n == 4) at4 = m.minMs;
                    if (n == 32) at32 = m.minMs;
                }
            }
            TestContext.Out.WriteLine($"ClipOverlap N=32 / N=4 = {at32 / at4:F2}x");
            Assert.Less(at32 / at4, 5.0,
                $"the preview cost grew {at32 / at4:F1}x from 4 buildings to 32 ({at4:F4} -> "
                + $"{at32:F4} ms). Either the accepted set's transition cache is not being filled "
                + "or the post-walk validation is no longer localised to the ghost's group — "
                + "without both it was 11.2x.");
        }

        /// <summary>
        /// The boundary policy's cost, which the guide's §8 claims two things about: that
        /// `ClipOverlap` costs what `Touch` costs while no footprint overhangs, and that a rebake
        /// which does emit a clip ring takes the full-rebuild path.
        ///
        /// <para>Written because those figures were quoted in the guide without a fixture behind
        /// them — §8 says every number there is reproducible from this class, and for that callout it
        /// was not true.</para>
        /// </summary>
        [Test]
        public void BoundaryPolicy_CostsWhatTouchCostsUntilAFootprintOverhangs()
        {
            TestContext.Out.WriteLine(
                "=== boundary policy cost: interior placements vs one that overhangs ===");
            TestContext.Out.WriteLine(
                $"{"tris",9} {"policy",12} {"case",10} {"ms",8} {"patched",8} {"fallback",9}");

            foreach (int half in new[] { 40, 80 })
            {
                FPNavMesh baseMesh = BuildGridStage(half);
                int tris = baseMesh.Triangles.Length;
                int warm = tris > 60_000 ? 4 : 8;
                int iters = tris > 60_000 ? 5 : 5;
                FPBuildingRect[] interior = LatticePlacements(4, half);

                // Overhanging: the last footprint's expanded box crosses the stage border, so the
                // clip stage actually runs. Under Touch or Reject this same set is refused, which is
                // the reason the comparison below has to change the SET and not just the policy.
                var over = new FPBuildingRect[interior.Length];
                System.Array.Copy(interior, over, interior.Length);
                FP64 edge = FP64.FromInt(half);
                over[over.Length - 1] = new FPBuildingRect(
                    edge - FP64.One, FP64.Zero, edge, FP64.One, FP64.Zero);

                void Row(string label, FPBuildingPlacementRules rules, FPBuildingRect[] set)
                {
                    var ctx = FPNavMeshRebaker.CreateContext(baseMesh, null, prewarm: false);
                    if (!FPNavMeshRebaker.TryRebake(ctx, set, out FPNavMesh probe,
                            out FPBuildingRejectionInfo rejection, null, rules))
                    {
                        TestContext.Out.WriteLine(
                            $"{tris,9} {rules.BoundaryPolicy,12} {label,10}   refused: {rejection.Reason}");
                        return;
                    }
                    ctx.CommitSwap(probe);

                    void Cycle()
                    {
                        FPNavMesh m = FPNavMeshRebaker.Rebake(ctx, set, null, rules);
                        ctx.CommitSwap(m);
                    }

                    var oc = ctx.PatchOutcome;
                    int inc0 = oc.Incremental;
                    int fb0 = oc.FallbackVertexShift + oc.FallbackGridGeometry
                            + oc.FallbackEmpty + oc.FallbackDuplicateBoundaryEdge;
                    var m2 = Measure(Cycle, warmup: warm, iterations: iters);
                    int inc = oc.Incremental - inc0;
                    int fb = oc.FallbackVertexShift + oc.FallbackGridGeometry
                           + oc.FallbackEmpty + oc.FallbackDuplicateBoundaryEdge - fb0;
                    TestContext.Out.WriteLine(
                        $"{tris,9} {rules.BoundaryPolicy,12} {label,10} {m2.minMs,8:F2} {inc,8} {fb,9}");
                }

                Row("interior", new FPBuildingPlacementRules(
                    false, FPBoundaryPlacementPolicy.Touch), interior);
                Row("interior", new FPBuildingPlacementRules(
                    false, FPBoundaryPlacementPolicy.ClipOverlap), interior);
                Row("overhang", new FPBuildingPlacementRules(
                    false, FPBoundaryPlacementPolicy.ClipOverlap), over);
            }
        }
    }
}