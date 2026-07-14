using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Fixed-capacity numeric ring of per-tick <see cref="StateHashBreakdown"/> values. Replaces the
    /// former DEVELOPMENT_BUILD-only string-history queue: it is pre-allocated with flat backing arrays
    /// and <see cref="Record"/> only copies numbers, so accumulation on the steady path is allocation-free
    /// (formatting happens only at flush, a cold path after a desync). Flat arrays (not jagged) are used
    /// for locality and a single allocation per array.
    ///
    /// <para>Slot = <c>tick % capacity</c>; the tick label is stored per slot so a reader can tell live
    /// entries from stale ones after wraparound. Sizing (<paramref name="typeCount"/> /
    /// <paramref name="participantCount"/>) is fixed for a session — the registry is frozen and the
    /// snapshot-participant set is stable once systems are registered.</para>
    /// </summary>
    internal sealed class StateHashRing
    {
        private const int EmptySlot = int.MinValue;

        private readonly int _capacity;
        private readonly int _typeCount;
        private readonly int _participantCount;

        private readonly int[] _ticks;              // [capacity] tick label, EmptySlot when unused
        private readonly long[] _totals;            // [capacity]
        private readonly ulong[] _frameHashes;      // [capacity]
        private readonly ulong[] _componentHashes;  // flat [capacity * typeCount]
        private readonly int[] _componentCounts;    // flat [capacity * typeCount]
        private readonly ulong[] _systemHashes;     // flat [capacity * participantCount]

        public int Capacity => _capacity;
        public int TypeCount => _typeCount;
        public int ParticipantCount => _participantCount;

        public StateHashRing(int capacity, int typeCount, int participantCount)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
            _typeCount = typeCount;
            _participantCount = participantCount;

            _ticks = new int[capacity];
            for (int i = 0; i < capacity; i++) _ticks[i] = EmptySlot;
            _totals = new long[capacity];
            _frameHashes = new ulong[capacity];
            _componentHashes = new ulong[capacity * typeCount];
            _componentCounts = new int[capacity * typeCount];
            _systemHashes = new ulong[capacity * Math.Max(1, participantCount)];
        }

        /// <summary>
        /// Copies the breakdown into the slot for <paramref name="tick"/> (zero-GC).
        /// <para><b>Precondition</b>: <paramref name="b"/> is sized to at least this ring's
        /// <see cref="TypeCount"/> / <see cref="ParticipantCount"/>. Every caller runs
        /// <c>EnsureHashRing</c> first, which grows the breakdown to the current counts AND rebuilds the
        /// ring (fresh, all slots cleared) whenever either count changes — so the <c>Math.Min</c> bounds
        /// below always equal <c>_typeCount</c> / <c>_participantCount</c> and the whole slot region is
        /// overwritten (no stale tail). The <c>Min</c> is a safety clamp, not a partial-copy path: a
        /// caller that bypassed <c>EnsureHashRing</c> with an undersized breakdown would leave the slot
        /// tail holding the previous tick's values.</para>
        /// </summary>
        public void Record(int tick, StateHashBreakdown b)
        {
            int slot = tick % _capacity;
            if (slot < 0) slot += _capacity;

            _ticks[slot] = tick;
            _totals[slot] = b.Total;
            _frameHashes[slot] = b.FrameHash;

            int cCount = Math.Min(_typeCount, b.ComponentHashes.Length);
            Array.Copy(b.ComponentHashes, 0, _componentHashes, slot * _typeCount, cCount);
            Array.Copy(b.ComponentCounts, 0, _componentCounts, slot * _typeCount, cCount);

            int sCount = Math.Min(_participantCount, b.SystemHashes.Length);
            if (sCount > 0)
                Array.Copy(b.SystemHashes, 0, _systemHashes, slot * _participantCount, sCount);
        }

        /// <summary>Marks every slot empty (called when the recorded timeline is discarded).</summary>
        public void Clear()
        {
            for (int i = 0; i < _capacity; i++) _ticks[i] = EmptySlot;
        }

        /// <summary>True and fills the out params when the slot for <paramref name="tick"/> holds that tick.</summary>
        public bool TryGet(int tick, out long total, out ulong frameHash)
        {
            int slot = tick % _capacity;
            if (slot < 0) slot += _capacity;
            if (_ticks[slot] != tick)
            {
                total = 0;
                frameHash = 0;
                return false;
            }
            total = _totals[slot];
            frameHash = _frameHashes[slot];
            return true;
        }

        /// <summary>
        /// True and points the spans at the recorded breakdown for <paramref name="tick"/>. The spans
        /// alias the ring's backing arrays — a later Record for the same slot overwrites them, so a
        /// caller that needs the values past the current call must copy them out.
        /// </summary>
        public bool TryGetBreakdown(int tick, out ReadOnlySpan<ulong> componentHashes,
            out ReadOnlySpan<int> componentCounts, out ReadOnlySpan<ulong> systemHashes, out long total)
        {
            int slot = tick % _capacity;
            if (slot < 0) slot += _capacity;
            if (_ticks[slot] != tick)
            {
                componentHashes = default;
                componentCounts = default;
                systemHashes = default;
                total = 0;
                return false;
            }
            componentHashes = ComponentHashesAt(slot);
            componentCounts = ComponentCountsAt(slot);
            systemHashes = SystemHashesAt(slot);
            total = _totals[slot];
            return true;
        }

        /// <summary>Per-component-type hash/count for a recorded tick (index = RegisteredTypeIdsSorted position).</summary>
        public ReadOnlySpan<ulong> ComponentHashesAt(int slot) => new ReadOnlySpan<ulong>(_componentHashes, slot * _typeCount, _typeCount);
        public ReadOnlySpan<int> ComponentCountsAt(int slot) => new ReadOnlySpan<int>(_componentCounts, slot * _typeCount, _typeCount);
        public ReadOnlySpan<ulong> SystemHashesAt(int slot) => new ReadOnlySpan<ulong>(_systemHashes, slot * _participantCount, _participantCount);

        /// <summary>
        /// Enumerates recorded ticks in ascending tick order into <paramref name="slotOrder"/> (slot
        /// indices), returning the count. <paramref name="slotOrder"/> must be at least
        /// <see cref="Capacity"/> long. Used by the cold-path flush to print history oldest-first.
        /// </summary>
        public int GetOrderedSlots(int[] slotOrder)
        {
            int n = 0;
            for (int i = 0; i < _capacity; i++)
                if (_ticks[i] != EmptySlot) slotOrder[n++] = i;

            // Insertion sort by tick label — n is small (capacity, ~60) and this is a cold path.
            for (int i = 1; i < n; i++)
            {
                int s = slotOrder[i];
                int t = _ticks[s];
                int j = i - 1;
                while (j >= 0 && _ticks[slotOrder[j]] > t)
                {
                    slotOrder[j + 1] = slotOrder[j];
                    j--;
                }
                slotOrder[j + 1] = s;
            }
            return n;
        }

        public int TickAt(int slot) => _ticks[slot];
        public long TotalAt(int slot) => _totals[slot];
        public ulong FrameHashAt(int slot) => _frameHashes[slot];
    }
}
