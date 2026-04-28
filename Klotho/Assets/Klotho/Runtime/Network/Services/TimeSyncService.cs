namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Frame-advantage based clock sync.
    /// </summary>
    public class TimeSyncService : ITimeSyncService
    {
        // ── Constants ──
        public const int FRAME_WINDOW_SIZE = 40;
        public const int MIN_UNIQUE_FRAMES = 10;
        public const int MIN_FRAME_ADVANTAGE = 3;
        public const int MAX_FRAME_ADVANTAGE = 9;
        private const int IDLE_INPUT_WINDOW = 10;

        // ── Frame advantage history (ring buffer) ──
        private readonly float[] _localAdvantage = new float[FRAME_WINDOW_SIZE];
        private readonly float[] _remoteAdvantage = new float[FRAME_WINDOW_SIZE];
        private int _head;
        private int _count;

        // ── Idle input detection ──
        private readonly bool[] _recentInputIdle = new bool[IDLE_INPUT_WINDOW];
        private int _inputHead;
        private int _inputCount;

        public float LocalAdvantageMean { get; private set; }
        public float RemoteAdvantageMean { get; private set; }

        public void AdvanceFrame(float localAdvantage, float remoteAdvantage)
        {
            _localAdvantage[_head] = localAdvantage;
            _remoteAdvantage[_head] = remoteAdvantage;
            _head = (_head + 1) % FRAME_WINDOW_SIZE;
            if (_count < FRAME_WINDOW_SIZE)
                _count++;

            LocalAdvantageMean = CalculateMean(_localAdvantage, _count);
            RemoteAdvantageMean = CalculateMean(_remoteAdvantage, _count);
        }

        public void RecordInput(bool isIdle)
        {
            _recentInputIdle[_inputHead] = isIdle;
            _inputHead = (_inputHead + 1) % IDLE_INPUT_WINDOW;
            if (_inputCount < IDLE_INPUT_WINDOW)
                _inputCount++;
        }

        public int RecommendWaitFrames(bool requireIdleInput)
        {
            if (_count < MIN_UNIQUE_FRAMES)
                return 0;

            float advantage = LocalAdvantageMean;
            float radvantage = RemoteAdvantageMean;

            // local is behind or equal → no wait needed
            if (advantage >= radvantage)
                return 0;

            // wait for half of the difference
            float sleep = (radvantage - advantage) / 2.0f;

            // below minimum threshold → ignore
            if (sleep < MIN_FRAME_ADVANTAGE)
                return 0;

            // active input → skip wait (preserve responsiveness)
            if (requireIdleInput && !IsInputIdle())
                return 0;

            // apply maximum cap
            int waitFrames = (int)sleep;
            if (waitFrames > MAX_FRAME_ADVANTAGE)
                waitFrames = MAX_FRAME_ADVANTAGE;

            return waitFrames;
        }

        public void Reset()
        {
            _head = 0;
            _count = 0;
            _inputHead = 0;
            _inputCount = 0;
            LocalAdvantageMean = 0;
            RemoteAdvantageMean = 0;

            for (int i = 0; i < FRAME_WINDOW_SIZE; i++)
            {
                _localAdvantage[i] = 0;
                _remoteAdvantage[i] = 0;
            }
            for (int i = 0; i < IDLE_INPUT_WINDOW; i++)
                _recentInputIdle[i] = true;
        }

        private bool IsInputIdle()
        {
            if (_inputCount < IDLE_INPUT_WINDOW)
                return true;

            for (int i = 0; i < IDLE_INPUT_WINDOW; i++)
            {
                if (!_recentInputIdle[i])
                    return false;
            }
            return true;
        }

        private static float CalculateMean(float[] buffer, int count)
        {
            if (count == 0) return 0;
            float sum = 0;
            for (int i = 0; i < count; i++)
                sum += buffer[i];
            return sum / count;
        }
    }
}
