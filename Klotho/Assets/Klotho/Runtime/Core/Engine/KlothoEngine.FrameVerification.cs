using System;

using xpTURN.Klotho.Input;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
using ZLogger;
#endif

#if KLOTHO_FAULT_INJECTION
using xpTURN.Klotho.Diagnostics;
#endif

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        #region Frame Verification

        public int LastVerifiedTick => _lastVerifiedTick;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // Diagnostic — throttled break-cause log for chain advance stall.
        private long _lastChainBreakLogMs;
        private int _lastChainBreakLoggedTick = -1;
        // Single-shot buffer dump on first chain-break to bound log volume.
        private bool _chainBreakBufferDumped;
#endif

        public bool IsFrameVerified(int tick)
        {
            return tick >= 0 && tick <= _lastVerifiedTick;
        }

        public FrameState GetFrameState(int tick)
        {
            return tick <= _lastVerifiedTick ? FrameState.Verified : FrameState.Predicted;
        }

        private void TryAdvanceVerifiedChain()
        {
            int tick = _lastVerifiedTick + 1;
            while (tick < CurrentTick)
            {
                if (!_inputBuffer.HasAllCommands(tick, _activePlayerIds.Count))
                {
                    OnChainAdvanceBreak?.Invoke();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    LogChainAdvanceBreak(tick);
#endif
                    break;
                }
                _lastVerifiedTick = tick;
                OnFrameVerified?.Invoke(tick);
                FireVerifiedInputBatch();

                // Dispatch synced events for the newly verified tick.
                // Regular events were already fired during the Predicted stage - do not refire them.
                // On the rollback path, the subsequent DiffRollbackEvents fires new-only events as Confirmed.
                var verifiedEvents = _eventBuffer.GetEvents(tick);
                for (int ei = 0; ei < verifiedEvents.Count; ei++)
                {
                    var evt = verifiedEvents[ei];
                    if (evt.Mode == EventMode.Synced)
                        OnSyncedEvent?.Invoke(tick, evt);
                }

                tick++;
            }
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void LogChainAdvanceBreak(int tick)
        {
            // Throttle: log per-tick at most once per 1s, and skip duplicates of the same stalled tick.
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (tick == _lastChainBreakLoggedTick && nowMs - _lastChainBreakLogMs < 1000)
                return;
            _lastChainBreakLogMs = nowMs;
            _lastChainBreakLoggedTick = tick;

            // Enumerate which playerIds have / are missing commands at the stalled tick.
            var sb = new System.Text.StringBuilder();
            sb.Append("present=[");
            bool first = true;
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int pid = _activePlayerIds[pi];
                bool has = _inputBuffer.HasCommandForTick(tick, pid);
                if (!first) sb.Append(',');
                first = false;
                sb.Append(pid).Append(has ? "✓" : "✗");
            }
            sb.Append(']');

            _logger?.ZLogWarning($"[KlothoEngine][ChainBreak] stuck at tick={tick} (_lastVerifiedTick={_lastVerifiedTick}, CurrentTick={CurrentTick}, activeIds.Count={_activePlayerIds.Count}, recommendedExtraDelay={_recommendedExtraDelay}) {sb}");

            if (!_chainBreakBufferDumped)
            {
                _chainBreakBufferDumped = true;
                _inputBuffer.DumpTickRange(tick - 3, tick + 3);
            }

#if KLOTHO_FAULT_INJECTION
            RttSpikeMetricsCollector.OnChainBreak();
#endif
        }
#endif

        #endregion
    }
}
