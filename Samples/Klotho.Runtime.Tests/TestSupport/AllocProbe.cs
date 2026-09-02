using System;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Allocation measurement for the zero-alloc gates: the SMALLEST of <see cref="DefaultPasses"/>
    /// measured windows, each entered with the thread's allocation quantum drained.
    ///
    /// <para><b>Both halves are there for one defect, and it is in the INSTRUMENT rather than in
    /// anything measured.</b> <see cref="GC.GetAllocatedBytesForCurrentThread"/> is only exact at
    /// the granularity of the thread's allocation context: when a BACKGROUND GC runs while the
    /// thread holds an unused quantum remainder, that remainder is charged to the thread, so a
    /// provably non-allocating loop reports up to ~8 KB. Reduced to a loop of pure arithmetic it
    /// reproduces at 105 of 1,500 windows with concurrent GC on and 0 of 1,500 with it off — and
    /// 0 of 1,500 with the collect below, at 2,999 background GCs.</para>
    ///
    /// <para>Which is exactly what these gates see: a fixture passes alone every time and fails
    /// inside the full suite, at a different configuration and a different odd number each run
    /// (32, 3,616, 4,096, 4,168, 4,544, 4,936, 5,344, 5,856 B — all under one quantum, always one
    /// run of many). A suite of 2,450 tests keeps a large heap and background GCs running; a lone
    /// fixture does not.</para>
    ///
    /// <para><b>The collect</b> drains the remainder, so a background GC landing mid-window has
    /// nothing to charge — sufficient on its own for the gates that assert zero, since a window
    /// that allocates nothing never takes a new quantum. <b>The passes</b> cover the ones that
    /// do allocate and therefore re-arm the artifact by taking one: the charge is non-negative
    /// and lands in at most one pass, while a real allocation is in every pass, so the minimum
    /// is the true steady state. Neither loosens a ceiling — a per-call regression survives both,
    /// which is the point of measuring this way instead of raising the numbers.</para>
    ///
    /// <para>A window that divides by a large iteration count is immune on its own (one quantum
    /// spread over 20,000 iterations floors to zero), as is one that takes the median of several
    /// samples. Those measure without this.</para>
    /// </summary>
    internal static class AllocProbe
    {
        public const int DefaultPasses = 3;

        /// <summary>Total bytes allocated by the smallest of <paramref name="passes"/> runs of
        /// <paramref name="window"/> — the caller supplies the whole measured loop.
        /// <paramref name="setup"/>, when given, re-establishes the window's precondition before each
        /// pass and is not measured: a window that deliberately covers a one-time cost (the first call
        /// after a rebake, say) needs that cost re-armed, or the later passes measure something else
        /// and the minimum reports the wrong thing.</summary>
        public static long SmallestWindow(Action window, Action setup = null, int passes = DefaultPasses)
        {
            long best = long.MaxValue;
            for (int p = 0; p < passes; p++)
            {
                setup?.Invoke();
                GC.Collect();
                long before = GC.GetAllocatedBytesForCurrentThread();
                window();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes < best) best = bytes;
                // Nothing can beat zero, and the gates that assert it are the common case.
                if (best == 0) break;
            }
            return best;
        }

        /// <summary>Bytes per call: <see cref="SmallestWindow"/> over a window of
        /// <paramref name="iterations"/> calls, divided.</summary>
        public static long SmallestPerCall(Action call, int iterations, int passes = DefaultPasses)
            => SmallestWindow(() =>
            {
                for (int i = 0; i < iterations; i++) call();
            }, passes: passes) / iterations;
    }
}
