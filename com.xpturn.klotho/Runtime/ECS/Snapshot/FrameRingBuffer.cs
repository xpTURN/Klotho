using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    public class FrameRingBuffer
    {
        private readonly Frame[] _frames;
        private readonly bool[] _dirty;
        private readonly byte[][] _systemStateSlots;
        // Bytes actually written into each system-state slot. The slot buffer may be larger than the
        // live payload (it is grown but never shrunk), so callers reading the raw bytes must use this
        // length rather than the buffer length — otherwise a stale tail from a larger prior save leaks
        // into the read. RestoreSystemState is unaffected (it reads exactly what each participant needs).
        private readonly int[] _systemStateLengths;
        private readonly int _capacity;
        private int _latestSavedTick = -1;

        public int Capacity => _capacity;

        public FrameRingBuffer(int capacity, int maxEntities, IKLogger logger)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
            _frames = new Frame[capacity];
            _dirty = new bool[capacity];
            _systemStateSlots = new byte[capacity][];
            _systemStateLengths = new int[capacity];

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
            _systemStateLengths[idx] = writer.Position;
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
                _systemStateLengths[i] = 0;
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

        /// <summary>
        /// Returns the raw serialized system-state (snapshot participant) bytes retained for
        /// <paramref name="tick"/>, WITHOUT restoring them into the live participants. This is the
        /// read-only twin of <see cref="RestoreSystemState"/>: it lets a diagnostic capture read the T₀
        /// system snapshot off the ring while the live simulation stays untouched (the "server/host does
        /// not rewind to serve a past tick" invariant). Returns false when the tick is out of the
        /// retention window or holds no participant state.
        /// </summary>
        public bool TryGetSystemState(int tick, int currentTick, out ReadOnlySpan<byte> systemState)
        {
            if (!HasFrame(tick, currentTick))
            {
                systemState = default;
                return false;
            }
            int idx = tick % _capacity;
            var data = _systemStateSlots[idx];
            if (data == null)
            {
                systemState = default;
                return false;
            }
            systemState = new ReadOnlySpan<byte>(data, 0, _systemStateLengths[idx]);
            return true;
        }

        public int GetNearestAvailableTick(int targetTick, int currentTick)
        {
            // Scan bound from the ring's actual retention state (same source as HasFrame):
            // the oldest retained tick is _latestSavedTick - _capacity + 1. Deriving it from
            // currentTick was off by one (latest saved is currentTick - 1 at rollback time),
            // which excluded the oldest retained tick and killed deep-rollback clamping.
            int oldest = Math.Max(0, _latestSavedTick - _capacity + 1);
            for (int t = targetTick; t >= oldest; t--)
            {
                if (HasFrame(t, currentTick))
                    return t;
            }
            return -1;
        }

        public void GetSavedTicks(int currentTick, System.Collections.Generic.IList<int> output)
        {
            // Same retention-state bound as GetNearestAvailableTick — the
            // currentTick-derived bound dropped the oldest retained tick from the list.
            int oldest = Math.Max(0, _latestSavedTick - _capacity + 1);
            for (int t = oldest; t <= currentTick; t++)
            {
                if (HasFrame(t, currentTick))
                    output.Add(t);
            }
        }
    }
}
