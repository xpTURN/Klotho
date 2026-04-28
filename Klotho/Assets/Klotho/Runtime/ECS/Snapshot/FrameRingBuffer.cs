using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    public class FrameRingBuffer
    {
        private readonly Frame[] _frames;
        private readonly bool[] _dirty;
        private readonly byte[][] _systemStateSlots;
        private readonly int _capacity;
        private int _latestSavedTick = -1;

        public int Capacity => _capacity;

        public FrameRingBuffer(int capacity, int maxEntities, ILogger logger)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
            _frames = new Frame[capacity];
            _dirty = new bool[capacity];
            _systemStateSlots = new byte[capacity][];

            for (int i = 0; i < capacity; i++)
                _frames[i] = new Frame(maxEntities, logger);
        }

        public void SaveFrame(int tick, Frame source)
        {
            int idx = tick % _capacity;
            _frames[idx].CopyFrom(source);
            _dirty[idx] = true;
            _latestSavedTick = tick;
        }

        public void RestoreFrame(int tick, Frame target)
        {
            int idx = tick % _capacity;
            target.CopyFrom(_frames[idx]);
        }

        public void SaveSystemState(int tick, IReadOnlyList<ISnapshotParticipant> participants)
        {
            int idx = tick % _capacity;
            int totalSize = 0;
            for (int i = 0; i < participants.Count; i++)
                totalSize += participants[i].GetSnapshotSize();

            if (_systemStateSlots[idx] == null || _systemStateSlots[idx].Length < totalSize)
                _systemStateSlots[idx] = new byte[totalSize];

            var writer = new SpanWriter(_systemStateSlots[idx]);
            for (int i = 0; i < participants.Count; i++)
                participants[i].SaveSnapshot(ref writer);
        }

        public void RestoreSystemState(int tick, IReadOnlyList<ISnapshotParticipant> participants)
        {
            int idx = tick % _capacity;
            var data = _systemStateSlots[idx];
            if (data == null) return;

            var reader = new SpanReader(data);
            for (int i = 0; i < participants.Count; i++)
                participants[i].RestoreSnapshot(ref reader);
        }

        public void Clear()
        {
            for (int i = 0; i < _capacity; i++)
            {
                _frames[i].Clear();
                _dirty[i] = false;
                _systemStateSlots[i] = null;
            }
            _latestSavedTick = -1;
        }

        public bool HasFrame(int tick, int currentTick)
        {
            if (tick > currentTick || tick < 0 || _latestSavedTick < 0)
                return false;
            if (tick > _latestSavedTick)
                return false;
            if ((_latestSavedTick - tick) >= _capacity)
                return false;
            return _dirty[tick % _capacity];
        }

        /// <summary>
        /// Returns the ring slot frame for the specified tick. Returns false if out of range or not saved.
        /// IKlothoEngine.TryGetFrameAtTick delegates to this method.
        /// </summary>
        public bool TryGetFrame(int tick, int currentTick, out Frame frame)
        {
            if (!HasFrame(tick, currentTick))
            {
                frame = null;
                return false;
            }
            frame = _frames[tick % _capacity];
            return true;
        }

        public int GetNearestAvailableTick(int targetTick, int currentTick)
        {
            int oldest = Math.Max(0, currentTick - _capacity + 1);
            for (int t = targetTick; t >= oldest; t--)
            {
                if (HasFrame(t, currentTick))
                    return t;
            }
            return -1;
        }

        public void GetSavedTicks(int currentTick, System.Collections.Generic.IList<int> output)
        {
            int oldest = Math.Max(0, currentTick - _capacity + 1);
            for (int t = oldest; t <= currentTick; t++)
            {
                if (HasFrame(t, currentTick))
                    output.Add(t);
            }
        }
    }
}
