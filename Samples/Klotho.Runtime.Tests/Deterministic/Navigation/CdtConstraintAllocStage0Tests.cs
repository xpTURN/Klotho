using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The measurement that decides whether pooling the constraint-insertion collections is worth
    /// doing at all. It answers exactly two questions, which are the entry thresholds:
    ///
    ///   (A) Does the allocation cause real pressure? — gen0/1/2 collections over a burst at a
    ///       realistic building count. An earlier measurement saw 0/0/0 at 3 KB per rebake, so
    ///       "the number is bigger now" is not by itself a reason to act.
    ///   (B) Or does the rented triangle array actually overflow into an Array.Resize? The cliff
    ///       position depends on where a map's CDT triangle count falls relative to a power of
    ///       two, so it must be checked per asset rather than extrapolated from one.
    ///
    /// Release-only and [Explicit]: DEBUG runs the CDT SelfCheck and the premise assert added for
    /// this stage, which make both the byte numbers and the timings meaningless.
    ///   dotnet test -c Release --filter FullyQualifiedName~CdtConstraintAllocStage0Tests
    /// </summary>
    [TestFixture]
    [Explicit("allocation measurement — run in Release with an explicit filter")]
    public class CdtConstraintAllocStage0Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho", "package.json")))
                dir = dir.Parent;
            return dir?.FullName;
        }

        /// <summary>The distinct bake assets in the repo (bin/ copies excluded — they are duplicates).</summary>
        private static readonly string[] AssetPaths =
        {
            "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes",
            "Samples/Brawler/Assets/NavMesh/Data/8_heightmesh.NavMeshData.bytes",
            "Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes",
            "Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes",
            "Samples/GodotPolySample/NavigationRegion3D.NavMeshData.bytes",
        };

        private static IEnumerable<(string name, FPNavMesh mesh)> LoadAssets()
        {
            string root = RepoRoot();
            Assert.IsNotNull(root, "repo root not found");
            foreach (string rel in AssetPaths)
            {
                string full = Path.Combine(root, rel);
                if (!File.Exists(full))
                {
                    TestContext.Out.WriteLine($"  (missing) {rel}");
                    continue;
                }
                yield return (Path.GetFileName(rel), FPNavMeshSerializer.Deserialize(full));
            }
        }

        /// <summary>
        /// Buildings laid out on a grid inside the mesh bounds, inset so the bake-radius expansion
        /// stays clear of the boundary. Placement is rejected by throwing, so the caller keeps the
        /// ones that survive a trial rebake — that is also how the game seam validates placement.
        /// </summary>
        private static FPBuildingRect[] PlaceBuildings(
            FPNavMesh mesh, FPNavMeshRebakeSnapshot snapshot, int want, double size = 1.0)
        {
            // Candidates are triangle centroids, not a bounds grid: a bounds grid puts most
            // footprints outside the walkable region and every one of them gets rejected, which
            // silently measures "0 buildings" while reporting a building count.
            var kept = new List<FPBuildingRect>();
            int step = System.Math.Max(1, mesh.TriangleCount / (want * 8));
            double half = size * 0.5;
            for (int t = 0; t < mesh.TriangleCount && kept.Count < want; t += step)
            {
                FPVector2 c = mesh.Triangles[t].centerXZ;
                double x = c.x.ToDouble(), z = c.y.ToDouble();
                var candidate = new FPBuildingRect(
                    FP64.FromDouble(x - half), FP64.FromDouble(z - half),
                    FP64.FromDouble(x + half), FP64.FromDouble(z + half), FP64.Zero);

                var trial = new List<FPBuildingRect>(kept) { candidate };
                try
                {
                    FPNavMeshRebaker.Rebake(snapshot, trial.ToArray());
                    kept.Add(candidate);
                }
                catch (Exception)
                {
                    // Rejected placement — the normal control-flow path.
                }
            }
            return kept.ToArray();
        }

        [Test]
        public void Stage0_A_CliffTablePerAsset()
        {
            // The carve headroom is `2^ceil(log2(minSize)) - minSize`, and minSize is
            // dominated by the snapshot's triangle count, which is fixed per asset. So whether a
            // map is one building away from the cliff or a thousand is essentially arbitrary — a
            // single asset cannot answer it.
            TestContext.Out.WriteLine("=== stage 0 (A): rented capacity vs carve growth, per asset ===");
            TestContext.Out.WriteLine(
                $"{"asset",-34}{"snapTri",9}{"minSize",9}{"capacity",10}{"headroom",10}{"grew",6}{"finalTri",10}");

            int cliffAssets = 0;
            foreach (var (name, mesh) in LoadAssets())
            {
                FPNavMeshRebakeSnapshot snapshot;
                try
                {
                    snapshot = FPNavMeshRebaker.CreateSnapshot(mesh);
                }
                catch (Exception e)
                {
                    TestContext.Out.WriteLine($"{name,-34} snapshot failed: {e.GetType().Name}");
                    continue;
                }

                FPBuildingRect[] buildings = PlaceBuildings(mesh, snapshot, 24);

                // Pooled context: NonPooling rents EXACTLY minSize, so headroom would always
                // read 0 and the power-of-two rounding the cliff hinges on would be invisible.
                var cliffCtx = new FPNavMeshRebakeContext(snapshot);
                cliffCtx.CommitSwap(FPNavMeshRebaker.Rebake(cliffCtx, buildings));

                FPConstrainedDelaunay.Diag.Reset();
                FPConstrainedDelaunay.Diag.Enabled = true;
                try
                {
                    cliffCtx.CommitSwap(FPNavMeshRebaker.Rebake(cliffCtx, buildings));
                }
                finally
                {
                    FPConstrainedDelaunay.Diag.Enabled = false;
                }

                int snapTri = snapshot.Cdt.TriCount;
                int h = buildings.Length * 4;
                int minSize = System.Math.Max(64, snapTri + h * 2 + 8);
                int capacity = FPConstrainedDelaunay.Diag.RentedTrisCapacity;
                int headroom = capacity - minSize;
                int grew = FPConstrainedDelaunay.Diag.TrisGrowthEvents;
                if (grew > 0)
                    cliffAssets++;

                TestContext.Out.WriteLine(
                    $"{name,-34}{snapTri,9}{minSize,9}{capacity,10}{headroom,10}{grew,6}" +
                    $"{FPConstrainedDelaunay.Diag.LastTriCount,10}   ({buildings.Length} bldg placed)");
            }

            TestContext.Out.WriteLine(
                $"\nVERDICT (B): {cliffAssets} asset(s) hit the Array.Resize cliff at 24 buildings. " +
                (cliffAssets > 0
                    ? "the cliff is REAL — entry threshold (B) is met."
                    : "the cliff did not fire on any asset at this building count."));
        }

        [Test]
        public void Stage0_B_ChannelLengthAndCarveCount()
        {
            // The rent size for removed/left/right follows the file's "generous rent + safety
            // valve" convention, which needs the real distribution rather than a guess. This also
            // supplies the denominator for "per carve" — an earlier ~2.26 KB figure was per
            // constraint EDGE, which turned out to be the wrong unit.
            TestContext.Out.WriteLine("=== stage 0 (B): carve count + channel length distribution ===");

            foreach (var (name, mesh) in LoadAssets())
            {
                FPNavMeshRebakeSnapshot snapshot;
                try { snapshot = FPNavMeshRebaker.CreateSnapshot(mesh); }
                catch (Exception) { continue; }

                FPBuildingRect[] buildings = PlaceBuildings(mesh, snapshot, 24);
                if (buildings.Length == 0)
                {
                    TestContext.Out.WriteLine($"{name,-34} no valid placement");
                    continue;
                }

                FPConstrainedDelaunay.Diag.Reset();
                FPConstrainedDelaunay.Diag.Enabled = true;
                try { FPNavMeshRebaker.Rebake(snapshot, buildings); }
                finally { FPConstrainedDelaunay.Diag.Enabled = false; }

                var lens = new List<int>(FPConstrainedDelaunay.Diag.ChannelLens);
                lens.Sort();
                int carves = FPConstrainedDelaunay.Diag.CarveCount;
                int edges = buildings.Length * 4;
                double median = lens.Count == 0 ? 0 : lens[lens.Count / 2];
                double p95 = lens.Count == 0 ? 0 : lens[(int)(lens.Count * 0.95)];

                TestContext.Out.WriteLine(
                    $"{name,-34} {buildings.Length,3} bldg / {edges,4} constraint edges → " +
                    $"{carves,5} carves ({carves / (double)edges:F2} per edge), " +
                    $"channel len median {median,4:F0} p95 {p95,5:F0} max {FPConstrainedDelaunay.Diag.ChannelLenMax,5}");
            }
        }

        [Test]
        public void Stage0_C_PressureAndBreakdown()
        {
            // Entry threshold (A): does a realistic burst actually collect? And the question the
            // channel-length measurement forced open — WHERE do the building-proportional bytes
            // actually come from? The obvious attribution is CarveChannel, but carving turns out
            // to be rare (0.5 per constraint edge on Field) and tiny (channel length 3-4), so that
            // attribution has to be measured rather than assumed.
            string root = RepoRoot();
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");

            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            var snapshot = FPNavMeshRebaker.CreateSnapshot(mesh);
            FPBuildingRect[] buildings = PlaceBuildings(mesh, snapshot, 24);
            var none = Array.Empty<FPBuildingRect>();

            TestContext.Out.WriteLine($"=== stage 0 (C): Field, {buildings.Length} buildings ===");

            // --- (A) pressure
            var ctx = new FPNavMeshRebakeContext(snapshot);
            for (int i = 0; i < 32; i++)
                ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings));

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            const int burst = 100;
            for (int i = 0; i < burst; i++)
                ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings));
            long perRebake = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / burst;
            int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;

            TestContext.Out.WriteLine($"allocation {perRebake} B per rebake, {perRebake * burst / 1024.0:F0} KB over {burst}");
            TestContext.Out.WriteLine($"collections: gen0={d0} gen1={d1} gen2={d2}");
            TestContext.Out.WriteLine(
                $"VERDICT (A): {(d0 > 0 ? "gen0 collections DID occur — pressure is real." : "no gen0 collection over the burst — pressure is NOT demonstrated.")}");

            // --- where the building-proportional bytes actually live
            var emptyCtx = new FPNavMeshRebakeContext(snapshot);
            for (int i = 0; i < 32; i++)
                emptyCtx.CommitSwap(FPNavMeshRebaker.Rebake(emptyCtx, none));

            long Measure(Action a, int iterations = 8)
            {
                for (int i = 0; i < 8; i++) a();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iterations; i++) a();
                return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
            }

            long rebake0 = Measure(() => emptyCtx.CommitSwap(FPNavMeshRebaker.Rebake(emptyCtx, none)));
            long rebakeN = Measure(() => ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings)));

            // The CDT stage in isolation, fed the same holes the rebaker would feed it.
            var holePool = new FPNavMeshRebakeBufferPool();
            var noXs = Array.Empty<long>();
            var noCons = Array.Empty<int>();
            long cdt0 = Measure(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, noXs, noXs, 0, noCons, 0, out _, true, null, holePool));

            TestContext.Out.WriteLine(
                $"\nRebake  0 bldg {rebake0,8} B   |  {buildings.Length} bldg {rebakeN,8} B   |  delta {rebakeN - rebake0,8} B");
            TestContext.Out.WriteLine($"CDT     0 hole {cdt0,8} B  (pooled, isolated)");

            // --- carve counters, so the carve share can be bounded
            FPConstrainedDelaunay.Diag.Reset();
            FPConstrainedDelaunay.Diag.Enabled = true;
            try { ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings)); }
            finally { FPConstrainedDelaunay.Diag.Enabled = false; }

            int carves = FPConstrainedDelaunay.Diag.CarveCount;
            var lens = new List<int>(FPConstrainedDelaunay.Diag.ChannelLens);
            TestContext.Out.WriteLine(
                $"carves this rebake: {carves}, total channel length {FPConstrainedDelaunay.Diag.ChannelLenSum}");

            // --- per-collection attribution by replaying the same construction pattern (CC-4)
            long Replay(Func<int, long> one)
            {
                for (int r = 0; r < 4; r++) foreach (int k in lens) GC.KeepAlive(one(k));
                long before = GC.GetAllocatedBytesForCurrentThread();
                foreach (int k in lens) GC.KeepAlive(one(k));
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            long removedBytes = Replay(k => { var l = new List<int>(); for (int i = 0; i < k; i++) l.Add(i); return l.Count; });
            long removedSetBytes = Replay(k => { var h = new HashSet<int>(); for (int i = 0; i < k; i++) h.Add(i); return h.Count; });
            long polylineBytes = Replay(k => { var a = new List<int>(); var b2 = new List<int>(); for (int i = 0; i < k; i++) { a.Add(i); b2.Add(i); } return a.Count + b2.Count; });
            long boundaryBytes = Replay(k => { var d = new Dictionary<(int, int), (int, bool)>(); for (int i = 0; i < k + 2; i++) d[(i, i + 1)] = (i, false); return d.Count; });
            long triplesBytes = Replay(k => { var l = new List<(int, int, int)>(); for (int i = 0; i < k; i++) l.Add((i, i, i)); return l.Count; });
            long localBytes = Replay(k => { var d = new Dictionary<(int, int), (int, int)>(); for (int i = 0; i < k * 3; i++) d[(i, i + 1)] = (i, i); return d.Count; });
            long newTrisBytes = Replay(k => { var l = new List<int>(); for (int i = 0; i < k; i++) l.Add(i); return l.Count; });

            long carveTotal = removedBytes + removedSetBytes + polylineBytes + boundaryBytes + triplesBytes + localBytes + newTrisBytes;
            long delta = rebakeN - rebake0;
            void Row(string label, long v) => TestContext.Out.WriteLine(
                $"  {label,-28}{v,8} B  {(carveTotal == 0 ? 0 : v * 100.0 / carveTotal),5:F1}% of carve  " +
                $"{(delta <= 0 ? 0 : v * 100.0 / delta),5:F1}% of delta");

            TestContext.Out.WriteLine($"\nper-carve collection attribution (replay over {lens.Count} carves):");
            Row("removed (List)", removedBytes);
            Row("removedSet (HashSet)", removedSetBytes);
            Row("left+right (List x2)", polylineBytes);
            Row("boundary (Dictionary)", boundaryBytes);
            Row("triples (List)", triplesBytes);
            Row("local (Dictionary)", localBytes);
            Row("newTris (List)", newTrisBytes);
            Row("CARVE TOTAL", carveTotal);
            TestContext.Out.WriteLine(
                $"  building-proportional delta {delta} B — carve collections explain " +
                $"{(delta <= 0 ? 0 : carveTotal * 100.0 / delta):F1}% of it. " +
                (carveTotal * 2 < delta
                    ? "*** Attributing this to CarveChannel is WRONG — most of the delta is elsewhere. ***"
                    : "attribution holds."));
        }

        [Test]
        public void Stage0_E_PushToCliff()
        {
            // (B) needs a configuration that actually trips Array.Resize. Stage01 is the
            // interesting asset: capacity 512 against minSize 420 leaves only 92 slots of carve
            // headroom, and the per-asset cliff table already showed finalTri (424) exceeding
            // minSize (420) —
            // i.e. the `h*2+8` formula is already short, just absorbed by the power-of-two round.
            TestContext.Out.WriteLine("=== stage 0 (E): push building count until the cliff fires ===");

            foreach (var (name, mesh) in LoadAssets())
            {
                FPNavMeshRebakeSnapshot snapshot;
                try { snapshot = FPNavMeshRebaker.CreateSnapshot(mesh); }
                catch (Exception) { continue; }

                foreach (int want in new[] { 24, 48, 96, 192 })
                {
                    FPBuildingRect[] buildings = PlaceBuildings(mesh, snapshot, want, size: 0.5);
                    if (buildings.Length == 0)
                        continue;

                    var ctx = new FPNavMeshRebakeContext(snapshot);
                    ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings));

                    FPConstrainedDelaunay.Diag.Reset();
                    FPConstrainedDelaunay.Diag.Enabled = true;
                    try { ctx.CommitSwap(FPNavMeshRebaker.Rebake(ctx, buildings)); }
                    finally { FPConstrainedDelaunay.Diag.Enabled = false; }

                    TestContext.Out.WriteLine(
                        $"{name,-34} want {want,4} → placed {buildings.Length,4}, " +
                        $"capacity {FPConstrainedDelaunay.Diag.RentedTrisCapacity,7}, " +
                        $"finalTri {FPConstrainedDelaunay.Diag.LastTriCount,7}, " +
                        $"carves {FPConstrainedDelaunay.Diag.CarveCount,5}, " +
                        $"resize {FPConstrainedDelaunay.Diag.TrisGrowthEvents,3}");

                    if (buildings.Length < want)
                        break;   // placement saturated — larger requests will not add more
                }
            }
        }

        [Test]
        public void Stage0_D_TriStructSize()
        {
            // Never hand-count a struct stride: doing that under-computed FPNavMeshTriangle by
            // 8 bytes once and mis-reported the residual by 174 KB.
            const int n = 4096;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var probe = new FPConstrainedDelaunay.Cdt.Tri[n];
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(probe);

            double stride = (bytes - 24) / (double)n;   // minus object header + length
            TestContext.Out.WriteLine($"=== stage 0 (D): sizeof(Cdt.Tri) ≈ {stride:F1} B (array of {n} = {bytes} B) ===");
            TestContext.Out.WriteLine(
                $"one Array.Resize at capacity C costs C * 2 * {stride:F0} B — e.g. C=65536 → " +
                $"{65536L * 2 * (long)System.Math.Round(stride) / 1024.0 / 1024.0:F1} MB");
        }
    }
}
