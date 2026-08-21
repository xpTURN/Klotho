using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// The clip stage's working buffers, reused across calls, for the per-frame preview path.
    ///
    /// <para><b>Why the preview needs its own and cannot borrow the rebake's.</b>
    /// <see cref="FPNavMeshRebakeBufferPool"/> says it out loud — a second consumer must bring its
    /// own pool, because work buffers are one slot per role with no return call and two consumers
    /// overwrite each other partially. And it is not just any pool: it is not
    /// <c>context.Pool</c> either, because that one is marked in use from the moment a rebake task
    /// starts until it finishes or is discarded, and a sliced rebake holds that across several
    /// frames — exactly the frames a placement UI is previewing in.</para>
    ///
    /// <para><b>Every buffer is handed out in its correct initial state, by construction.</b> That
    /// is the whole reason the rent methods exist instead of exposing the fields. Three of these
    /// have an initial state the algorithm depends on, and each fails differently if it is skipped:
    /// <list type="bullet">
    /// <item><c>EmittedRoot</c> is read before it is written, so a stale <c>true</c> makes a group's
    /// ring silently not be emitted.</item>
    /// <item><c>Identity</c> is only written for transition-free buildings and is read in full by
    /// the caller, so a stale <c>true</c> runs the flush-semantics validation on a building that
    /// actually clipped — which refuses a placement that is fine, with no exception and no failing
    /// test.</item>
    /// <item><c>Starts</c> is born holding a single <c>0</c>. Lose it and every ring's range is off
    /// by one.</item>
    /// </list>
    /// None of the three throws, and a preview is peer-local so a divergence check cannot see them
    /// either. The only net is the clipper tests' cold-vs-warm output comparison.</para>
    ///
    /// <para><b>Not reentrant.</b> One previewer per scratch, on the thread that rebakes — the same
    /// rule the preview API already documents. Two previews sharing one scratch corrupt it.</para>
    /// </summary>
    internal sealed class FPClipScratch
    {
        private List<FPNavMeshClipper.Transition> _transitions;
        private int[] _transitionCountOf;
        private List<long> _clipXs;
        private List<long> _clipZs;
        private List<FP64> _clipYs;
        private List<int> _starts;
        private List<int> _roots;
        private bool[] _identity;
        private bool[] _emittedRoot;
        private int[] _sortOrder;
        private int[] _sortOrderStart;
        private int[] _uf;
        private int[] _pairOrder;
        private bool[] _breakBefore;
        private List<int> _interval;

        /// <summary>
        /// The EXISTING buildings' transition count from the last clip through this scratch, used to
        /// size the next one's transition list. The context form reads that count from its accepted
        /// set; the snapshot form has none, so the previous call is the estimate — a good one,
        /// because between two previews the ghost moves and the accepted set does not.
        ///
        /// <para>Deliberately the existing count and not the total: the total includes the ghost, and
        /// adding the ghost headroom on top of it made the two API forms reserve different amounts
        /// for the same placement (caught by the allocation gate's cross-form equality).</para>
        ///
        /// <para>Capacity only — it cannot change an answer, and being wrong costs one growth.</para>
        /// </summary>
        internal int TransitionHint;

        internal List<FPNavMeshClipper.Transition> RentTransitions(int minCapacity)
        {
            if (_transitions == null) _transitions = new List<FPNavMeshClipper.Transition>(minCapacity);
            else _transitions.Clear();
            if (_transitions.Capacity < minCapacity) _transitions.Capacity = minCapacity;
            return _transitions;
        }

        internal List<long> RentXs() => Rented(ref _clipXs, 64);
        internal List<long> RentZs() => Rented(ref _clipZs, 64);
        internal List<int> RentRoots() => Rented(ref _roots, 8);

        internal List<FP64> RentYs()
        {
            if (_clipYs == null) _clipYs = new List<FP64>(64);
            else _clipYs.Clear();
            return _clipYs;
        }

        /// <summary>The ring-range list, handed back holding its leading <c>0</c> — see the type's
        /// summary for what losing it does.</summary>
        internal List<int> RentStarts()
        {
            List<int> list = Rented(ref _starts, 8);
            list.Add(0);
            return list;
        }

        internal int[] RentTransitionCountOf(int count) => Cleared(ref _transitionCountOf, count);

        /// <summary>Zeroed, because the walk reads these before it writes them (or the caller reads
        /// what the walk did not write) — see the type's summary.</summary>
        internal bool[] RentIdentity(int count) => Cleared(ref _identity, count);
        internal bool[] RentEmittedRoot(int count) => Cleared(ref _emittedRoot, count);

        /// <summary>
        /// Buffers whose used prefix is written before it is read, so they are handed back
        /// UNCLEARED — the tail beyond the count holds the last call's values and no read reaches
        /// it. Each one's guarantee, because "it happens to work" is how the cleared ones got their
        /// own note:
        /// <list type="bullet">
        /// <item><c>SortOrder</c> and <c>PairOrder</c>: filled by <c>order[i] = i</c> over the whole
        /// count.</item>
        /// <item><c>Uf</c>: filled by <c>uf[i] = i</c> over the whole count.</item>
        /// <item><c>SortOrderStart</c>: <c>[1..count]</c> is a running prefix sum, and <c>[0]</c> is
        /// assigned explicitly at the call site.</item>
        /// <item><c>BreakBefore</c>: <c>Array.Clear(0, runLength)</c> at the top of every run, and
        /// reads are inside that run.</item>
        /// <item><c>Interval</c>: <c>Clear()</c>ed before every fill.</item>
        /// </list>
        ///
        /// <para><b>Two of those guarantees are belt-and-braces, and it is worth saying which.</b>
        /// Removing <c>orderStart[0] = 0</c> or the leading <c>interval.Clear()</c> breaks no test —
        /// verified by mutation. Nothing ever writes <c>orderStart[0]</c> to anything but zero, and
        /// every run leaves <c>interval</c> cleared behind it, so both hold without the guard. They
        /// stay because the invariant that makes them redundant is NON-LOCAL (a write elsewhere, a
        /// clear at the bottom of a loop), which is precisely the kind a later edit removes without
        /// noticing. The other four are load-bearing: dropping the fill in <c>SortOrder</c>,
        /// <c>PairOrder</c> or <c>Uf</c>, or the per-run clear of <c>BreakBefore</c>, each turns the
        /// suite red.</para>
        /// </summary>
        internal int[] RentSortOrder(int count) => Grown(ref _sortOrder, count);
        internal int[] RentSortOrderStart(int count) => Grown(ref _sortOrderStart, count);
        internal int[] RentUf(int count) => Grown(ref _uf, count);
        internal int[] RentPairOrder(int count) => Grown(ref _pairOrder, count);
        internal bool[] RentBreakBefore(int count) => GrownBool(ref _breakBefore, count);
        internal List<int> RentInterval() => Rented(ref _interval, 8);

        private static int[] Grown(ref int[] slot, int count)
        {
            if (slot == null || slot.Length < count) slot = new int[count];
            return slot;
        }

        private static bool[] GrownBool(ref bool[] slot, int count)
        {
            if (slot == null || slot.Length < count) slot = new bool[count];
            return slot;
        }

        private static List<T> Rented<T>(ref List<T> slot, int initialCapacity)
        {
            if (slot == null) slot = new List<T>(initialCapacity);
            else slot.Clear();
            return slot;
        }

        private static bool[] Cleared(ref bool[] slot, int count)
        {
            if (slot == null || slot.Length < count) slot = new bool[count];
            else Array.Clear(slot, 0, count);
            return slot;
        }

        private static int[] Cleared(ref int[] slot, int count)
        {
            if (slot == null || slot.Length < count) slot = new int[count];
            else Array.Clear(slot, 0, count);
            return slot;
        }
    }
}
