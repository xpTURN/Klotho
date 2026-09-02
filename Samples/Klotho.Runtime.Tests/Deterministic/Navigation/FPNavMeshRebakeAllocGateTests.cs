using System;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Core.Tests;

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
    /// The byte gate measures FOUR configurations, and the labels matter because they were once
    /// wrong: Field with no buildings (the mesh-independent residual), Field with four separated
    /// footprints (the real asset, 22,321 triangles), the synthetic grid with four clustered
    /// ones (interacting carve channels, ~7x the cost of the same count separated — a path Field
    /// cannot host at all), and the same Field placements under
    /// <see cref="FPBoundaryPlacementPolicy.ClipOverlap"/>. It requires the Field asset and FAILS
    /// without it; the fallback it used to have is what let it report synthetic numbers under a
    /// message that said "on Field".
    ///
    /// <para><b>Why a policy configuration exists at all.</b> Every gate here ran the DEFAULT rules
    /// and nothing else, so the clip stage — which only the `ClipOverlap` policy enters — was
    /// outside all of them. That was fine while no game used the policy; the Brawler sample now
    /// ships it. The clipper's own note says a pooled rewrite is follow-up work, and the fourth
    /// configuration is what turns the size of that debt from an argument into a number that moves
    /// when someone pays it down.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeAllocGateTests
    {
        private const int Warmup = 32;

        private static string RepoRoot()
        {
            // The com.xpturn.klotho directory, the marker every other fixture in this suite walks
            // up for. A marker that does not exist does not fail here: the walk runs off the top of
            // the filesystem and returns null. This asked for "Klotho.Tests.sln", which is in no
            // repo, so both byte gates threw on the first Release run to reach them — DEBUG
            // self-ignores, so local runs never saw it. Hence LoadField below refusing to fall back.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
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
            Assert.NotNull(root, "could not locate the repo root (com.xpturn.klotho) from "
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

        private static void RebakeAndInstall(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings,
            FPBuildingPlacementRules rules = default)
        {
            FPNavMesh mesh = FPNavMeshRebaker.Rebake(ctx, buildings, null, rules);
            ctx.CommitSwap(mesh);
        }

        private static long MeasurePerRebake(FPNavMeshRebakeContext ctx, FPBuildingRect[] buildings,
            FPBuildingPlacementRules rules = default)
            => SmallestPass(() => RebakeAndInstall(ctx, buildings, rules), iterations: 8);

        /// <summary>
        /// Allocation per call, measured the way every zero-alloc gate in this suite measures —
        /// smallest of several quantum-drained passes. The instrument defect this exists for, and
        /// the evidence, are on <see cref="AllocProbe"/>; these gates are where it was diagnosed.
        /// </summary>
        private static long SmallestPass(Action call, int iterations)
            => AllocProbe.SmallestPerCall(call, iterations);

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

            // Fourth configuration: the SAME buildings on the SAME asset, only the boundary policy
            // differs. That is the whole design — the delta is the clip stage and nothing else.
            //
            // History, because the number moved twice and the ceiling had to move with it. This
            // configuration was added measuring 255,272 B: the clip stage allocated per CALL and per
            // RING rather than per building, and Field's boundary decomposes into 1,679 rings (one
            // outer of 995 edges plus 1,678 holes averaging 8). Every call built an int[15,317]
            // ring-of-edge map (~83 KB) and then a List<int>[1,679] with a List<int> for each ring
            // (~148 KB) to group a handful of transitions. Moving the decomposition onto the
            // snapshot removed the first; replacing the per-ring lists with one sorted run walk
            // removed the second. Neither changed an answer.
            //
            // What is left is per-placement and small, so the ceiling is tight on purpose: it is no
            // longer a debt being tracked, it is the working set. If it climbs back into the tens of
            // KB something re-introduced a per-ring container.
            var clipRules = new FPBuildingPlacementRules(
                allowBuildingTouch: false, FPBoundaryPlacementPolicy.ClipOverlap);
            var clipCtx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            for (int i = 0; i < Warmup; i++)
                RebakeAndInstall(clipCtx, buildings, clipRules);
            long clipBytes = MeasurePerRebake(clipCtx, buildings, clipRules);

            TestContext.Out.WriteLine(
                $"ClipOverlap on the same Field placements: {clipBytes} B "
                + $"({clipBytes - withBuildings:+#;-#;0} B vs the default policy) — Field has "
                + "1,679 boundary rings and nothing in the clip stage scales with that any more");

            Assert.Greater(clipBytes, withBuildings,
                "the clip stage allocates per call, so ClipOverlap cannot be cheaper than the default "
                + "on an asset with 1,679 boundary rings. If these are equal the policy did not take "
                + "effect and this configuration is measuring the default path twice");

            Assert.Less(clipBytes, 24L * 1024,
                "byte gate: measured 9,104 B for 4 separated buildings on Field under ClipOverlap, "
                + "against 5,360 B for the same placements under the default — a 3,744 B delta on an "
                + "asset with 1,679 boundary rings. It was 255,272 B before the ring decomposition "
                + "moved to the snapshot and the per-ring lists became one sorted run walk, and "
                + "9,504 B before Result stopped materialising five arrays (the preview needed that; "
                + "the rebake got 400 B of it for free). Nothing here scales with the ring count any "
                + "more, so a climb back into the tens of KB means a per-ring container came back. "
                + "The rebake still allocates its clip buffers per call on purpose — pooling them is "
                + "the preview's contract, and a once-per-placement caller must bring its own");
#endif
        }

        /// <summary>
        /// The per-frame ghost preview's allocation, for every policy and both API forms.
        ///
        /// <para><b>Why this exists before any pooling work.</b> The changes that reduce this
        /// allocation are, several of them, invisible to every correctness test in the suite: a
        /// capacity hint and a skipped all-zero array change no output at all, so nothing else can
        /// tell whether they landed or later regressed. This gate is the only regression line for
        /// them, which is why it is written first and why its numbers are recorded rather than
        /// bounded loosely.</para>
        ///
        /// <para><b>`Reject` and `Touch` allocate nothing, and that is a contract.</b> It is what
        /// <see cref="FPBuildingPreviewScratch"/> buys, and it holds however many buildings are
        /// down. `ClipOverlap` breaks it because answering means building the clip rings.</para>
        ///
        /// <para><b>Both forms, because they measure the same bytes.</b> The snapshot form has no
        /// accepted set and so no transition cache, yet it allocates byte for byte what the context
        /// form does — the cache saves WORK, not memory. That equality is itself the snapshot form's
        /// non-vacuity check: the context form's is `CachedTransitionStart[n] > 0`, which the
        /// snapshot form has no way to assert.</para>
        /// </summary>
        [Test]
        public void ByteGate_GhostPreviewAllocationPerPolicyAndForm()
        {
#if DEBUG
            Assert.Ignore(
                "Release-only for the same reason as the rebake byte gate: DEBUG allocations swamp "
                + "the signal. Run: dotnet test -c Release --filter FullyQualifiedName~ByteGate");
#else
            FPNavMesh baseMesh = LoadField();
            Assert.AreEqual(22321, baseMesh.TriangleCount,
                "the numbers below were measured on this exact asset");

            // Measured 360 / 528 / 1,056 / 1,944 B at N = 1 / 4 / 16 / 32 after stage 3 of the
            // preview-allocation work. The trail, at N = 32:
            //   85,624 → 41,512 (stage 1: CSR order table, sized transition list, no Y channel)
            //          → 10,464 (stage 2: the nine clip buffers pooled)
            //          →  1,944 (stage 3: Result hands out the working lists instead of five arrays)
            // What is left is the CSR pair (808), `uf` (160) and the pairing scratch (~976) — that
            // is stage 4, whose target is zero. The decomposition is in that plan's §2.
            //
            // Ceilings are ~1.05x, not budgets. Allocation is deterministic for a given build after
            // warmup, so a number that moves means something changed.
            var clipCeiling = new[] { 400L, 600L, 1200L, 2200L };
            var counts = new[] { 1, 4, 16, 32 };
            TestContext.Out.WriteLine("ghost preview allocation per call:");

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
                FPNavMeshRebakeSnapshot shared = FPNavMeshRebaker.CreateSnapshot(baseMesh);

                for (int k = 0; k < counts.Length; k++)
                {
                    int n = counts[k];
                    FPBuildingRect[] existing = FieldPlacementSpots.Take(spots, n);

                    var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
                    FPNavMeshRebaker.Rebake(ctx, existing, null, rules);
                    // A scratch PER configuration, mirroring the fresh context above. The snapshot
                    // form sizes its transition list from what the last call through this scratch
                    // resolved, so a scratch carried across configurations arrives holding another
                    // N's count and over-reserves — which is correct behaviour and a wrong
                    // measurement. (Found by the equality assertion below on its first run.)
                    var scratch = new FPBuildingPreviewScratch();

                    // The preview has to be answering the question. A refusal short-circuits before
                    // the boundary tests and would measure the separation loop instead.
                    Assert.IsTrue(
                        FPNavMeshRebaker.TryValidateOne(ctx, ghost, out FPBuildingRejectionInfo why, rules),
                        $"the ghost was refused under {policy} at N = {n} ({why.Reason}) — this "
                        + "configuration is measuring a rejection path, not the real work");
                    // And the clipping rows have to be clipping, or the expensive label sits on the
                    // cheap path. Only the context form can be asked; see the summary.
                    if (clipping)
                        Assert.Greater(ctx.Accepted.CachedTransitionStart[n], 0,
                            $"no existing building crosses a wall at N = {n}, so this row is "
                            + "measuring the identity carve rather than the clip stage");

                    long contextBytes = MeasurePerPreview(
                        () => FPNavMeshRebaker.TryValidateOne(ctx, ghost, out _, rules));
                    long snapshotBytes = MeasurePerPreview(
                        () => FPNavMeshRebaker.TryValidateOne(
                            shared, existing, n, ghost, out _, scratch, rules));

                    // Written as it goes, not collected: a failing assertion below has to arrive
                    // with the rows that led to it, or the message names a number with no row.
                    TestContext.Out.WriteLine(
                        $"  {policy,-12} N={n,-3} context {contextBytes,7} B   snapshot {snapshotBytes,7} B");

                    Assert.AreEqual(contextBytes, snapshotBytes,
                        $"the two forms allocate the same bytes under {policy} at N = {n} — the "
                        + "transition cache saves work, not memory. A difference means one form "
                        + "gained (or lost) an allocation the other did not, and this gate's single "
                        + "baseline table stops describing both");

                    Assert.AreEqual(0, contextBytes,
                        $"the {policy} preview at N = {n} allocated {contextBytes} B — it must "
                        + "allocate NOTHING. The strict policies walk the base mesh's edges once and "
                        + "touch nothing already built; ClipOverlap builds clip rings but does it in "
                        + "buffers it reuses (FPClipScratch). It was "
                        + $"{new[] { 5104, 12696, 41384, 85624 }[k]} B under ClipOverlap before the "
                        + "buffers were pooled. A per-frame API has no room for a "
                        + "steady-state allocation, so this is zero and not a ceiling");
                }
            }

#endif
        }

        /// <summary>
        /// Steady-state allocation of one preview call. The warmup matters for the same reason it
        /// does in the perf harness: tiered JIT and one-shot caches make the first calls allocate
        /// differently from the hundredth, and a per-frame API's cost is the steady state.
        /// </summary>
        private static long MeasurePerPreview(System.Action preview)
        {
            for (int i = 0; i < Warmup; i++)
                preview();
            return SmallestPass(preview, iterations: 20);
        }

        // ─────────────────────────────────────────────── the per-tick driver

        /// <summary>The table the driver's quiet tick is measured against. Deliberately allocation
        /// free itself, so what the gate sees is the driver's.</summary>
        private sealed class FixedSource : IFPNavMeshPlacementSource
        {
            private readonly FPNavMeshTimedPlacement[] _entries;
            public FixedSource(FPNavMeshTimedPlacement[] entries) { _entries = entries; }

            public int Capacity => 40;

            public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
            {
                for (int i = 0; i < _entries.Length; i++)
                    buffer[i] = _entries[i];
                eligible = _entries.Length;
                return _entries.Length;
            }

            public void DestroyDue(ref Frame frame, int tick) { }
        }

        private sealed class NoopInstaller : IFPNavMeshInstaller
        {
            public void Install(ref Frame frame, FPNavMesh mesh) { }
            public void Reseed(ref Frame frame) { }
        }

        [Test]
        public void ByteGate_TheDriversQuietTickAllocatesNothing()
        {
            // The driver runs EVERY tick, which is a different exposure from the rebake gates above:
            // a rebake is an event, a survey is not. The quiet tick — nothing due, the right mesh
            // already installed — is one walk of the game's table plus an integer compare, and it has
            // to cost zero bytes or the seam is a per-tick GC source on every consumer.
            //
            // Anything that shows up here is almost certainly a closure, a boxed enumerator, or a
            // params array that looked free at the call site.
            var entries = new FPNavMeshTimedPlacement[8];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = new FPNavMeshTimedPlacement
                {
                    Sequence = i + 1,
                    Placement = new FPBuildingPlacement(
                        0, FP64.FromInt(i * 2 - 8), FP64.FromInt(4), FP64.Zero),
                    // Live from before the measured ticks and never removed, so every measured tick
                    // is quiet: no boundary, no correction, no task.
                    EffectiveTick = 0,
                    RemovalEffectiveTick = int.MaxValue,
                };

            var driver = new FPNavMeshRebakeDriver(
                new FixedSource(entries), new NoopInstaller(), slotCount: 2);
            var frame = new Frame(64, null);

            // Warm: the first tick installs, which allocates a mesh by design. Measure after that.
            frame.Tick = 100;
            driver.Update(ref frame);
            frame.Tick = 101;
            driver.Update(ref frame);

            int tick = 102;
            long perTick = SmallestPass(() =>
            {
                frame.Tick = tick++;
                driver.Update(ref frame);
            }, iterations: 64);

            Assert.AreEqual(0, perTick,
                $"the driver allocated {perTick} B on a quiet tick. It runs every tick on every peer, "
                + "so this is a steady-state GC source rather than an event cost — find the closure "
                + "or the boxed enumerator instead of raising the ceiling");
        }
    }
}
