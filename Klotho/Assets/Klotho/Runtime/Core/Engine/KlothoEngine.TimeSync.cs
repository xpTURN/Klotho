using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Time sync
        private TimeSyncService _timeSync;
        private bool _timeSyncEnabled;
        private readonly Dictionary<int, (int remoteTick, int receivedAtTick)> _remoteTicks =
            new Dictionary<int, (int remoteTick, int receivedAtTick)>();

        #region TimeSync

        private void HandleFrameAdvantage(int playerId, int senderTick)
        {
            _remoteTicks[playerId] = (senderTick, CurrentTick);
        }

        private float CalculateLocalAdvantage()
        {
            if (_remoteTicks.Count == 0) return 0;

            int staleThreshold = _simConfig.MaxRollbackTicks;
            Span<int> valid = stackalloc int[_remoteTicks.Count];

            int validCount = 0;
            foreach (var kvp in _remoteTicks)
            {
                var (remoteTick, receivedAtTick) = kvp.Value;
                if (CurrentTick - receivedAtTick > staleThreshold)
                    continue;
                // Insertion sort (player count is small).
                int i = validCount;
                while (i > 0 && valid[i - 1] > remoteTick)
                {
                    valid[i] = valid[i - 1];
                    i--;
                }
                valid[i] = remoteTick;
                validCount++;
            }

            if (validCount == 0) return 0;
            int medianTick = valid[validCount / 2];
            return CurrentTick - medianTick;
        }

        public void EnableTimeSync()
        {
            _timeSyncEnabled = true;
            _timeSync.Reset();
            _remoteTicks.Clear();
        }

        public void DisableTimeSync()
        {
            _timeSyncEnabled = false;
        }

        public bool IsTimeSyncEnabled => _timeSyncEnabled;
        public ITimeSyncService TimeSyncService => _timeSync;

        #endregion
    }
}
