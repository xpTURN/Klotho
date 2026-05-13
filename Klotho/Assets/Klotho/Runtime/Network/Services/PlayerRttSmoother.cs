using System;

namespace xpTURN.Klotho.Network
{
    // Short-window RTT smoother for the push-decision path.
    // 5-sample sliding median: rejects single spikes; ~5s window at PING_INTERVAL_MS=1000.
    // GC-free — fixed-size int[] + stackalloc on read.
    internal class PlayerRttSmoother
    {
        public const int BUFFER_SIZE = 5;
        public const int MIN_SAMPLES = 3;

        private readonly int[] _samples = new int[BUFFER_SIZE];
        private int _idx;
        private int _count;

        public int SampleCount => _count;

        public void OnSample(int rttMs)
        {
            _samples[_idx] = rttMs;
            _idx = (_idx + 1) % BUFFER_SIZE;
            if (_count < BUFFER_SIZE) _count++;
        }

        public bool TryGetSmoothedRtt(out int rttMs)
        {
            if (_count < MIN_SAMPLES) { rttMs = 0; return false; }
            Span<int> tmp = stackalloc int[BUFFER_SIZE];
            for (int i = 0; i < _count; i++) tmp[i] = _samples[i];
            // Insertion sort — N<=5; faster than Array.Sort for tiny N + no allocation.
            for (int i = 1; i < _count; i++)
            {
                int x = tmp[i];
                int j = i - 1;
                while (j >= 0 && tmp[j] > x) { tmp[j + 1] = tmp[j]; j--; }
                tmp[j + 1] = x;
            }
            rttMs = tmp[_count / 2];
            return true;
        }
    }
}
