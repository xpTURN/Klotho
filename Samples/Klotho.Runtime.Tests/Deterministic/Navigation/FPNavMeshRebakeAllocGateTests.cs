using System;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The two gates that make "zero allocation" a maintained invariant rather
    /// than a number someone measured once. They watch DIFFERENT things and neither replaces
    /// the other:
    ///
    ///   The COUNTER gate — how many arrays the POOL allocated. Immune to the DEBUG-only
    ///     diagnostic allocations (VerifyConformingContract's HashSets, the CDT SelfCheck) that
    ///     make a byte measurement meaningless outside Release, so it runs in the normal suite.
    ///     Blind to a raw `new int[...]` written anywhere in the rebake path.
    ///   The BYTE gate — anything newly allocated, wherever it came from. That is exactly what
    ///     the counter cannot see, and exactly what "someone adds a new()" would look like. It
    ///     can only be measured in Release, so it self-ignores in DEBUG.
    ///
    /// Deliberately NOT placed in FPNavMeshRebakerPerfTests: [Explicit] there is a CLASS
    /// attribute, so a gate living in that fixture would never run on its own — which is how the
    /// pre-existing 400 KB assertion there sat failing at 974 KB unnoticed.
    ///
    /// Both gates measure STEADY STATE. The pool is slot-holding: the first rebakes, and any
    /// rebake that grows a buffer, legitimately allocate. Asserting zero without warming first
    /// would flag normal behaviour — buildings accumulate within a match, so growth is expected
    /// and is not a regression.
    ///
    /// The byte gate measures THREE configurations, and the labels matter because they were once
    /// wrong: Field with no buildings (the mesh-independent residual), Field with four separated
    /// footprints (the real asset, 22,321 triangles), and the synthetic grid with four clustered
    /// ones (interacting carve channels, ~7x the cost of the same count separated — a path Field
    /// cannot host at all). It requires the Field asset and FAILS without it; the fallback it used
    /// to have is what let it report synthetic numbers under a message that said "on Field".
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeAllocGateTests
    {
        private const int Warmup = 32;

        private static string RepoRoot()
        {
            // Klotho.Tests.sln. There is no Klotho.sln, and naming one here does not fail —
            // the walk just runs off the top of the filesystem and returns null, after which the
            // byte gate silently measured a synthetic mesh while its own assertion message said
            // "on Field". Hence LoadField below refusing to fall back.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Klotho.Tests.sln")))
                dir = dir.Parent;
            return dir?.FullName;
        }

        /// <summary>
        /// The real Field asset — 22,321 triangles, the size the byte gate exists to measure.
        /// <para>Fails rather than falling back to <see cref="BuildBase"/>. A fallback here is
        /// indistinguishable in the output from a real run, which is exactly how this gate spent
        /// its life measuring a 1,152-triangle synthetic grid.</para>
        /// </summary>
        private static FPNavMesh LoadField()
        {
            string root = RepoRoot();
            Assert.NotNull(root, "could not locate the repo root (Klotho.Tests.sln) from "
                + AppContext.BaseDirectory);
            string path = Path.Combine(root, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            Assert.IsTrue(File.Exists(path), $"the byte gate measures the real Field asset and it is missing: {path}");
            return FPNavMeshSerializer.Deserialize(path);
        }

        /// <summary>
        /// Buildings placed on FIELD. Not interchangeable with <see cref="Buildings"/>: that one's
        /// row sits at x -8..2, z -4, which is inside the synthetic grid and outside Field's
        /// walkable region — pointing the gate at Field with it throws "building (expanded) lies
        /// outside the walkable region".
        /// <para>Nor is it a matter of moving the row. Field has no straight ten-unit corridor:
        /// a sweep of 2,107 positions across the whole asset placed that layout nowhere, while
        /// single buildings were accepted at 29 of 325 sampled spots. So these are four SEPARATE
        /// footprints, taken from the centroids of Field's largest triangles (its open ground)
        /// and each verified to be accepted on its own.</para>
        /// <para>If Field is ever re-baked these may stop being walkable, and the gate will fail
        /// loudly with the rejection above rather than quietly measure something else.</para>
        /// </summary>
        private static FPBuildingRect[] FieldBuildings()
        {
            var at = new (int x, int z)[] { (14, -2), (46, -62), (46, 68), (-15, -23) };
            var result = new FPBuildingRect[at.Length];
            for (int i = 0; i < at.Length; i++)
            {
                FP64 x = FP64.FromInt(at[i].x), z = FP64.FromInt(at[i].z);
                result[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
            }
            return result;
        }

        /// <summary>
        /// A base mesh big enough to be representative but cheap enough for the normal suite —
        /// the real Field asset is used by the Release byte gate, not here.
        /// </summary>
        private static FPNavMesh BuildBase(int half = 12)
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

        private static FPBuildingRect[] Buildings(int count)
        {
            var result = new FPBuildingRect[count];
            for (int i = 0; i < count; i++)
            {
                // A row of separated 1x1 footprints; expansion by the bake radius keeps them
                // apart, which the rebaker requires.
                FP64 x = FP64.FromInt(-8 + i * 3);
                FP64 z = FP64.FromInt(-4);
                result[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
            }
            return result;
        }

        private static void RebakeAndInstall(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings)
        {
            FPNavMesh mesh = FPNavMeshRebaker.Rebake(ctx, buildings);
            ctx.CommitSwap(mesh);
        }

        private static long MeasurePerRebake(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings)
        {
            const int iterations = 8;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
                RebakeAndInstall(ctx, buildings);
            return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }

        [Test]
        public void CounterGate_SteadyStateRebakeAllocatesNoArrays()
        {
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            FPBuildingRect[] buildings = Buildings(4);

            for (int i = 0; i < Warmup; i++)
                RebakeAndInstall(ctx, buildings);

            ctx.Pool.ResetAllocatedArrayCount();
            for (int i = 0; i < 8; i++)
                RebakeAndInstall(ctx, buildings);

            Assert.AreEqual(0, ctx.Pool.AllocatedArrayCount,
                "counter gate: after warmup, a rebake repeated at the same size must not allocate a single "
                + "array through the pool — every buffer is a reused slot and the outputs come "
                + "back through the retirement chain");
        }

        [Test]
        public void CounterGate_GrowthIsNotARegression()
        {
            // The counterpart to the gate above, and the reason it says "same size": a rebake
            // that needs more room legitimately allocates. Pinning this keeps someone from
            // "fixing" a growth allocation by defeating the growth policy.
            FPNavMesh baseMesh = BuildBase();
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));

            for (int i = 0; i < Warmup; i++)
                RebakeAndInstall(ctx, Buildings(1));

            ctx.Pool.ResetAllocatedArrayCount();
            RebakeAndInstall(ctx, Buildings(5));

            Assert.Greater(ctx.Pool.AllocatedArrayCount, 0,
                "a rebake that needs more hole capacity than any before it must grow the pool — "
                + "if this ever reads zero the gate above is measuring nothing");
        }

        [Test]
        public void ByteGate_SteadyStateRebakeAllocatesEssentiallyNothing()
        {
#if DEBUG
            Assert.Ignore(
                "the byte gate is Release-only: DEBUG runs VerifyConformingContract's HashSets and the "
                + "CDT SelfCheck, which allocate hundreds of KB per rebake and swamp the signal. "
                + "Run: dotnet test -c Release --filter FullyQualifiedName~ByteGate");
#else
            FPNavMesh baseMesh = LoadField();
            Assert.AreEqual(22321, baseMesh.TriangleCount,
                "the gate's ceilings were measured on this exact asset — a re-baked Field needs "
                + "them re-derived, not silently re-used");

            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            FPBuildingRect[] buildings = FieldBuildings();

            for (int i = 0; i < Warmup; i++)
                RebakeAndInstall(ctx, buildings);

            long withBuildings = MeasurePerRebake(ctx, buildings);

            // Second configuration, no buildings: isolates the building-INDEPENDENT residual,
            // which is what the residual inventory covers (the Cdt instance, the FPNavMesh object —
            // the result, unpoolable by construction — and a few small helpers).
            var emptyCtx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            for (int i = 0; i < Warmup; i++)
                RebakeAndInstall(emptyCtx, null);
            long noBuildings = MeasurePerRebake(emptyCtx, null);

            // Third configuration, on the SYNTHETIC grid: four footprints in a row three apart.
            // Kept because Field cannot host that layout at all (see FieldBuildings), and it is
            // not a lesser case — interacting carve channels allocate an order of magnitude more
            // than the same count of separated ones. Dropping it when the gate moved to Field
            // would have quietly retired the only coverage of the expensive path.
            var clusteredCtx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(BuildBase()));
            FPBuildingRect[] clustered = Buildings(4);
            for (int i = 0; i < Warmup; i++)
                RebakeAndInstall(clusteredCtx, clustered);
            long clusteredBytes = MeasurePerRebake(clusteredCtx, clustered);

            TestContext.Out.WriteLine(
                $"steady-state rebake allocation: {noBuildings} B at 0 buildings, "
                + $"{withBuildings} B at {buildings.Length} separated buildings on Field "
                + $"(+{(withBuildings - noBuildings) / (double)(buildings.Length * 4):F0} B per constraint edge); "
                + $"{clusteredBytes} B at {clustered.Length} clustered buildings on the synthetic grid");

            // Measured 2,280 B — and identically on the synthetic grid, which is the point: the
            // residual is the inventoried set (the Cdt instance, the FPNavMesh result which is
            // unpoolable by construction, a few small helpers) and none of it scales with the mesh.
            Assert.Less(noBuildings, 4L * 1024,
                "the building-independent residual is the inventoried set and nothing else — a new array "
                + "anywhere in the path shows up here even though the counter gate cannot see it. "
                + "Measured 2,280 B; the ceiling is not a budget to spend");

            // Building-proportional bound. The gap between the two numbers is not the residual: it is the
            // per-carve collections inside the CDT's channel carve/re-triangulate
            // (FPConstrainedDelaunay.CarveChannel's removed/left/right, the triples list, the
            // boundary map, and WireChannel's local edge map), which this plan did not inventory
            // — the byte gate found them on its first run, which is the argument for having it.
            Assert.Less(withBuildings, 8L * 1024,
                "byte gate: measured 4,536 B at 4 separated buildings on Field — if this grows, either "
                + "the CDT channel collections grew or something new was added; both need a decision, "
                + "not a raised ceiling");

            // The clustered ceiling is the one the old 40 KB number actually described: it was
            // measured on this synthetic configuration all along while the message said "on Field".
            // A separate measurement pass acted on the cheap half: the HashSet and the
            // new-triangle list are gone, taking 38.6 KB down to the 31.8 KB seen here. The rest is
            // the two dictionaries (~66% of the carve allocation); removing those needs a
            // merge-join rewrite and a golden, and that work's entry threshold did not clearly
            // justify it. Bounded rather than silently absorbed.
            Assert.Less(clusteredBytes, 40L * 1024,
                "byte gate: measured 31.8 KB at 4 clustered buildings on the synthetic grid — this is "
                + "the interacting-carve path, and it costs ~7x what the same count of separated "
                + "footprints does");
#endif
        }
    }
}
