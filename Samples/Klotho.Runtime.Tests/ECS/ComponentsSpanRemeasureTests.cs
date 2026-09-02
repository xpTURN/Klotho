using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.ECS.Tests
{
    // Probe components for the ComponentsSpan re-materialisation measurement. typeIds in the 9600
    // block — 9000-9101, 9200-9240, 9300-9304, 9400-9402, 9500-9504 and 9999 are taken by other
    // fixtures. No MaxCount on purpose: unspecified (0) means SlotCapacity == maxEntities, which is
    // what lets a single component hold all 3200 instances (ComponentStorageRegistry.ResolveSlotCapacity).
    // All three are the same 16 B shape — mixing sizes would add a cache/bandwidth term and make the
    // result harder to compare against the reporter's number, not easier.

    [KlothoComponent(9600)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SpanProbeAComponent : IComponent
    {
        public int Value;
        public int Pad0;
        public int Pad1;
        public int Pad2;
    }

    [KlothoComponent(9601)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SpanProbeBComponent : IComponent
    {
        public int Value;
        public int Pad0;
        public int Pad1;
        public int Pad2;
    }

    [KlothoComponent(9602)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct SpanProbeCComponent : IComponent
    {
        public int Value;
        public int Pad0;
        public int Pad1;
        public int Pad2;
    }

    /// <summary>
    /// Re-measurement of the <c>ComponentsSpan</c> re-materialisation cost that Klotho#7 reported as
    /// 847.7 µs vs 41.4 µs at 3200 entities over a three-component pass. That report came from the
    /// Editor's eval path; the public answer promised we would re-run it ourselves and post the number.
    ///
    /// <para>Excluded from the normal suite: run explicitly, in Release. DEBUG keeps
    /// <c>FilterMutationGuard.Check/Record</c> alive (they are
    /// <c>[Conditional("DEBUG"), Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]</c>),
    /// which is one of the two candidate explanations this probe exists to separate:
    ///   dotnet test -c Release --filter FullyQualifiedName~ComponentsSpanRemeasureTests</para>
    ///
    /// <para>Six measurements — three access shapes × two iteration sources. The N→H1 step removes
    /// <c>GetTypeId</c> + the pruning guard + the storage ctor; the H1→H2 step removes the span
    /// re-materialisation itself. The published answer recommended hoisting <c>GetStorage&lt;T&gt;()</c>,
    /// which is only the N→H1 step — so the H1→H2 gap is what decides whether that advice was the
    /// whole fix. The Filter/bare split isolates the mutation guard, because the original probe's shape
    /// is not recorded anywhere on our side.</para>
    ///
    /// <para>Measurement discipline follows FPNavMeshRebakerPerfTests: warmup 32 (tiered JIT promotes
    /// at ~30 calls; fewer warmups measure tier-0 code), min and median over 9 samples. Every pass
    /// accumulates into <see cref="_sink"/> and the total is asserted — H2's inner loop is pure
    /// indexing and a Release JIT is free to delete it otherwise, which would read as "H2 is free".</para>
    /// </summary>
    [TestFixture]
    [Explicit("perf measurement — run in Release with an explicit filter")]
    public class ComponentsSpanRemeasureTests
    {
        private const int MaxEntities = 3200;
        private const int Warmup      = 32;
        private const int Iterations  = 9;

        // Every entity carries all three components, so the Filter match count is the full 3200 —
        // the same shape the reporter measured. Reported alongside the numbers.
        private const int ExpectedMatches = MaxEntities;

        // Σ over i in [0, 3200) of (i + 2i + 3i).
        private const long ExpectedSum = 6L * MaxEntities * (MaxEntities - 1) / 2;

        private IKLogger _logger;
        private Frame _frame;
        private EntityRef[] _entities;

        // Sink: an instance field the pass writes at the end, so the loop cannot be optimised away.
        private long _sink;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Warning);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SpanProbe");
        }

        // Built here rather than once per fixture-run: the registry layout is process-global and
        // other fixtures build Frames at 64/256. TestAssemblySetup keeps AllowLayoutRecompute on, so
        // constructing at 3200 recomputes instead of throwing — but ComponentRegistryFingerprintTests
        // flips that flag off and back for one test, so this fixture assumes no residual state.
        [SetUp]
        public void SetUp()
        {
            _frame    = new Frame(MaxEntities, _logger);
            _entities = new EntityRef[MaxEntities];

            for (int i = 0; i < MaxEntities; i++)
            {
                var e = _frame.CreateEntity();
                _frame.Add(e, new SpanProbeAComponent { Value = i });
                _frame.Add(e, new SpanProbeBComponent { Value = i * 2 });
                _frame.Add(e, new SpanProbeCComponent { Value = i * 3 });
                _entities[i] = e;
            }
        }

        [Test]
        public void ThreeComponentPass_AccessLadder()
        {
            int matches = CountFilterMatches();
            Assert.AreEqual(ExpectedMatches, matches, "filter match count");

            TestContext.Out.WriteLine(
                $"3-component read pass · {MaxEntities} entities · {matches} matches · 16 B x 3 · "
                + $"warmup {Warmup} · {Iterations} samples · per-pass totals");
            TestContext.Out.WriteLine(new string('-', 96));

            var nFilter  = Measure(PassFilterNaive);
            var h1Filter = Measure(PassFilterHoistedStorage);
            var h2Filter = Measure(PassFilterHoistedSpans);

            var nBare    = Measure(PassBareNaive);
            var h1Bare   = Measure(PassBareHoistedStorage);
            var h2Bare   = Measure(PassBareHoistedSpans);

            Report("Filter  N  frame.Get<T> x3", nFilter);
            Report("Filter  H1 hoisted GetStorage<T>", h1Filter, Ratio(nFilter, h1Filter));
            Report("Filter  H2 hoisted spans", h2Filter, Ratio(nFilter, h2Filter));
            Report("bare    N  frame.Get<T> x3", nBare);
            Report("bare    H1 hoisted GetStorage<T>", h1Bare, Ratio(nBare, h1Bare));
            Report("bare    H2 hoisted spans", h2Bare, Ratio(nBare, h2Bare));

            TestContext.Out.WriteLine(new string('-', 96));
            TestContext.Out.WriteLine(
                $"H1->H2 gap (the part hoisting GetStorage<T> does NOT remove): "
                + $"Filter {h1Filter.medianUs - h2Filter.medianUs,8:F2} us   "
                + $"bare {h1Bare.medianUs - h2Bare.medianUs,8:F2} us");
            TestContext.Out.WriteLine(
                $"Filter-vs-bare at H2 (iteration source only): "
                + $"{h2Filter.medianUs - h2Bare.medianUs,8:F2} us");
        }

        // ── passes ───────────────────────────────────────────────────────────
        // Each returns nothing and writes the accumulated total to _sink. The spans in the H2 forms
        // are hoisted inside the delegate: Span<T> is a ref struct and cannot be captured by one.

        private void PassFilterNaive()
        {
            long sum = 0;
            var filter = _frame.Filter<SpanProbeAComponent, SpanProbeBComponent, SpanProbeCComponent>();
            while (filter.Next(out var e))
            {
                sum += _frame.Get<SpanProbeAComponent>(e).Value
                     + _frame.Get<SpanProbeBComponent>(e).Value
                     + _frame.Get<SpanProbeCComponent>(e).Value;
            }
            _sink = sum;
        }

        private void PassFilterHoistedStorage()
        {
            long sum = 0;
            var sa = _frame.GetStorage<SpanProbeAComponent>();
            var sb = _frame.GetStorage<SpanProbeBComponent>();
            var sc = _frame.GetStorage<SpanProbeCComponent>();

            var filter = _frame.Filter<SpanProbeAComponent, SpanProbeBComponent, SpanProbeCComponent>();
            while (filter.Next(out var e))
            {
                int i = e.Index;
                sum += sa.Get(i).Value + sb.Get(i).Value + sc.Get(i).Value;
            }
            _sink = sum;
        }

        private void PassFilterHoistedSpans()
        {
            long sum = 0;
            var sa = _frame.GetStorage<SpanProbeAComponent>();
            var sb = _frame.GetStorage<SpanProbeBComponent>();
            var sc = _frame.GetStorage<SpanProbeCComponent>();

            var ca = sa.ComponentsSpan; var xa = sa.SparseSpan;
            var cb = sb.ComponentsSpan; var xb = sb.SparseSpan;
            var cc = sc.ComponentsSpan; var xc = sc.SparseSpan;

            var filter = _frame.Filter<SpanProbeAComponent, SpanProbeBComponent, SpanProbeCComponent>();
            while (filter.Next(out var e))
            {
                int i = e.Index;
                sum += ca[xa[i]].Value + cb[xb[i]].Value + cc[xc[i]].Value;
            }
            _sink = sum;
        }

        private void PassBareNaive()
        {
            long sum = 0;
            var entities = _entities;
            for (int k = 0; k < entities.Length; k++)
            {
                var e = entities[k];
                sum += _frame.Get<SpanProbeAComponent>(e).Value
                     + _frame.Get<SpanProbeBComponent>(e).Value
                     + _frame.Get<SpanProbeCComponent>(e).Value;
            }
            _sink = sum;
        }

        private void PassBareHoistedStorage()
        {
            long sum = 0;
            var entities = _entities;
            var sa = _frame.GetStorage<SpanProbeAComponent>();
            var sb = _frame.GetStorage<SpanProbeBComponent>();
            var sc = _frame.GetStorage<SpanProbeCComponent>();

            for (int k = 0; k < entities.Length; k++)
            {
                int i = entities[k].Index;
                sum += sa.Get(i).Value + sb.Get(i).Value + sc.Get(i).Value;
            }
            _sink = sum;
        }

        private void PassBareHoistedSpans()
        {
            long sum = 0;
            var entities = _entities;
            var sa = _frame.GetStorage<SpanProbeAComponent>();
            var sb = _frame.GetStorage<SpanProbeBComponent>();
            var sc = _frame.GetStorage<SpanProbeCComponent>();

            var ca = sa.ComponentsSpan; var xa = sa.SparseSpan;
            var cb = sb.ComponentsSpan; var xb = sb.SparseSpan;
            var cc = sc.ComponentsSpan; var xc = sc.SparseSpan;

            for (int k = 0; k < entities.Length; k++)
            {
                int i = entities[k].Index;
                sum += ca[xa[i]].Value + cb[xb[i]].Value + cc[xc[i]].Value;
            }
            _sink = sum;
        }

        private int CountFilterMatches()
        {
            int n = 0;
            var filter = _frame.Filter<SpanProbeAComponent, SpanProbeBComponent, SpanProbeCComponent>();
            while (filter.Next(out _)) n++;
            return n;
        }

        // ── harness ──────────────────────────────────────────────────────────

        // Same discipline as FPNavMeshRebakerPerfTests.Measure, reported in µs per pass so the number
        // is on the reporter's axis (847.7 / 41.4 µs at 3200 entities) rather than per access.
        private (double minUs, double medianUs) Measure(Action pass)
        {
            for (int i = 0; i < Warmup; i++)
                pass();

            var samples = new List<double>(Iterations);
            var sw = new Stopwatch();
            for (int i = 0; i < Iterations; i++)
            {
                sw.Restart();
                pass();
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds * 1000.0);
            }

            // Consume the sink: without this the pass is dead code in Release and H2 would read as free.
            Assert.AreEqual(ExpectedSum, _sink, "pass did not read the components");

            samples.Sort();
            return (samples[0], samples[samples.Count / 2]);
        }

        private static string Ratio((double minUs, double medianUs) baseline,
                                    (double minUs, double medianUs) m)
            => m.medianUs > 0 ? $"{baseline.medianUs / m.medianUs,6:F1}x vs N" : "";

        private static void Report(string label, (double minUs, double medianUs) m, string extra = "")
            => TestContext.Out.WriteLine(
                $"{label,-40} min {m.minUs,9:F2} us   median {m.medianUs,9:F2} us   {extra}");
    }
}
