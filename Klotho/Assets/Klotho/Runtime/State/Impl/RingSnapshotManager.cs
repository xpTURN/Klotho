using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.State
{
    /// <summary>
    /// Ring buffer snapshot manager.
    /// Fixed-size array, per-tick storage, O(1) insertion/lookup, zero GC.
    /// </summary>
    public class RingSnapshotManager : IStateSnapshotManager
    {
        private readonly IStateSnapshot[] _ring;
        private readonly int _capacity;
        private int _head;   // Index of the oldest slot
        private int _tail;   // Index of the next write slot
        private int _count;

        // SavedTicks enumeration (GC-free via cached list)
        private readonly List<int> _savedTicksCache = new List<int>();

        public RingSnapshotManager(int maxRollbackTicks)
        {
            _capacity = maxRollbackTicks + 2; // MAX_PREDICTION + 2
            _ring = new IStateSnapshot[_capacity];
        }

        /// <summary>
        /// MaxSnapshots is determined in the constructor.
        /// The setter is kept for interface compatibility but has no effect.
        /// </summary>
        public int MaxSnapshots
        {
            get => _capacity;
            set { /* no-op: ring buffer capacity is fixed at creation */ }
        }

        public IEnumerable<int> SavedTicks
        {
            get
            {
                _savedTicksCache.Clear();
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _capacity;
                    _savedTicksCache.Add(_ring[idx].Tick);
                }
                return _savedTicksCache;
            }
        }

        public void SaveSnapshot(int tick, IStateSnapshot snapshot)
        {
            // Overwrite existing tick (re-save same tick during rollback)
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _capacity;
                if (_ring[idx].Tick == tick)
                {
                    ReturnSnapshotBuffer(_ring[idx]);
                    _ring[idx] = snapshot;
                    return;
                }
            }

            // Ring is full -> remove the oldest entry
            if (_count == _capacity)
            {
                ReturnSnapshotBuffer(_ring[_head]);
                _ring[_head] = null;
                _head = (_head + 1) % _capacity;
            }
            else
            {
                _count++;
            }

            _ring[_tail] = snapshot;
            _tail = (_tail + 1) % _capacity;
        }

        public IStateSnapshot GetSnapshot(int tick)
        {
            // Per-tick storage -> direct index calculation
            if (_count == 0) return null;

            int newestTick = _ring[(_tail - 1 + _capacity) % _capacity].Tick;
            int oldestTick = _ring[_head].Tick;

            // Range check
            if (tick < oldestTick || tick > newestTick)
                return null;

            // Direct index: offset from the oldest tick
            int offset = tick - oldestTick;
            if (offset >= 0 && offset < _count)
            {
                int idx = (_head + offset) % _capacity;
                if (_ring[idx] != null && _ring[idx].Tick == tick)
                    return _ring[idx];
            }

            // Fallback: linear scan (empty slots may exist after ClearSnapshotsAfter)
            for (int i = 0; i < _count; i++)
            {
                int idx = (_tail - 1 - i + _capacity) % _capacity;
                if (_ring[idx].Tick == tick)
                    return _ring[idx];
            }

            return null;
        }

        public bool HasSnapshot(int tick)
        {
            return GetSnapshot(tick) != null;
        }

        /// <summary>
        /// Find the nearest snapshot at or before the specified tick.
        /// Since storage is per-tick, GetSnapshot(tick) can be used for direct lookup.
        /// </summary>
        public IStateSnapshot GetNearestSnapshot(int tick)
        {
            var exact = GetSnapshot(tick);
            if (exact != null) return exact;

            // Fallback: scan in reverse for ticks at or before the target tick
            for (int i = 0; i < _count; i++)
            {
                int idx = (_tail - 1 - i + _capacity) % _capacity;
                if (_ring[idx].Tick <= tick)
                    return _ring[idx];
            }

            return null;
        }

        public void GetSavedTicks(IList<int> output)
        {
            output.Clear();
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _capacity;
                output.Add(_ring[idx].Tick);
            }
        }

        public void ClearSnapshotsAfter(int tick)
        {
            // Remove from the tail in reverse - O(k)
            while (_count > 0)
            {
                int lastIdx = (_tail - 1 + _capacity) % _capacity;
                if (_ring[lastIdx].Tick <= tick)
                    break;
                ReturnSnapshotBuffer(_ring[lastIdx]);
                _ring[lastIdx] = null;
                _tail = lastIdx;
                _count--;
            }
        }

        public void ClearAll()
        {
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _capacity;
                ReturnSnapshotBuffer(_ring[idx]);
                _ring[idx] = null;
            }
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        /// <summary>
        /// Return the snapshot buffer to the pool
        /// </summary>
        private static void ReturnSnapshotBuffer(IStateSnapshot snapshot)
        {
            if (snapshot is StateSnapshot ss)
            {
                var data = ss.GetData();
                if (data != null)
                {
                    // Clear _data first to prevent double-return in SetData
                    ss.ClearDataWithoutReturn();
                    StreamPool.ReturnBuffer(data);
                }
            }
        }
    }
}
