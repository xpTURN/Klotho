using System;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Input;

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

        /// <summary>
        /// Records the verified tick to the replay AT PROMOTION TIME.
        /// Re-fetches GetCommandList(tick) at the record point — never reuse a commands reference captured
        /// before _simulation.Tick / event dispatch, which may clobber the shared list cache. Serializes
        /// immediately, so it is safe before the promotion event handlers run.
        /// P2P promotion points ONLY (ExecuteTick inline branch + TryAdvanceVerifiedChain below). NOT the
        /// SD-client path (ServerDrivenClient records entry.Commands — a pre-validated batch, different source).
        ///
        /// Audit — "promotion (P2P verified chain advance) == recording" is the contract. Every OTHER direct
        /// <c>_lastVerifiedTick =</c> assignment is a NON-recording state jump and intentionally does NOT
        /// call this helper (the tail-invariant pin, GetCommandsForTick(t).Count ≥ 1 over 0..TotalTicks,
        /// catches a promoted-but-unrecorded tick if that ever regresses). Audited non-recording sites:
        ///   • field init / resets to -1 — KlothoEngine.cs:207, :785, Replay.cs:44, Spectator.cs:36
        ///   • Rollback.cs:159 (rewind — the subsequent chain re-advance re-records via this helper)
        ///   • FullStateResync.cs:404 (corrective reset — truncates recording BEFORE this; buffer cleared)
        ///   • Replay.cs:234 (replay PLAYBACK — IsRecording is false)
        ///   • Spectator.cs:205/242/318/336/374/392 (spectator engine — IsRecording structurally false, never Start())
        ///   • ServerDriven.cs:119 · ServerDrivenClient.cs:673/981/1039 (SD model — records at its own path)
        /// </summary>
        private void RecordVerifiedTick(int tick)
        {
            if (_replaySystem.IsRecording)
                _replaySystem.RecordTick(tick, _inputBuffer.GetCommandList(tick));
        }

        private void TryAdvanceVerifiedChain()
        {
            int tick = _lastVerifiedTick + 1;
            while (tick < CurrentTick)
            {
                if (!_inputBuffer.HasAllCommands(tick, _activePlayerIds))
                {
                    OnChainAdvanceBreak?.Invoke();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    LogChainAdvanceBreak(tick);
#endif
                    break;
                }
                _lastVerifiedTick = tick;
                RecordVerifiedTick(tick);   // record at promotion (post-resim = corrected set), before event dispatch

                // Deferred sync-hash send: this check tick was executed speculatively and has now
                // reached verified with its predictions confirmed byte-equal (a mismatch would
                // have rolled back first) — the stashed hash is the verified hash.
                if (_deferredHashSendTicks.Remove(tick) && _localHashes.TryGetValue(tick, out var deferred))
                    _networkService?.SendSyncHash(tick, deferred.state, deferred.cmd);

                OnFrameVerified?.Invoke(tick);
                FireVerifiedInputBatch();

                // Dispatch synced events for the newly verified tick.
                // Regular events were already fired during the Predicted stage - do not refire them.
                // On the rollback path, the subsequent DiffRollbackEvents fires new-only events as Confirmed.
                // Batch helper short-circuits when this tick was already dispatched (rollback chain re-walk).
                DispatchSyncedEventsForTick(tick, _eventBuffer.GetEvents(tick));

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

            // Enumerate which playerIds have / are missing commands at the stalled tick. Track
            // whether any *non-disconnected* player is missing: if every missing player is a
            // confirmed-disconnected peer (_disconnectedPlayerIds — populated on guests via
            // the player state notification), this stall is the expected "host proactive-fill is
            // ~1 tick behind" condition during a disconnect window, so log it at Debug to avoid
            // per-tick WARN spam. Any non-disconnected missing player = a real stall → Warning.
            var sb = new System.Text.StringBuilder();
            sb.Append("present=[");
            bool first = true;
            bool anyMissingNotDisconnected = false;
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int pid = _activePlayerIds[pi];
                bool has = _inputBuffer.HasCommandForTick(tick, pid);
                if (!has && !_disconnectedPlayerIds.Contains(pid))
                    anyMissingNotDisconnected = true;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(pid).Append(has ? '✓' : '✗');
            }
            sb.Append(']');

            if (anyMissingNotDisconnected)
                _logger?.KWarning($"[KlothoEngine][ChainBreak] stuck at tick={tick} (_lastVerifiedTick={_lastVerifiedTick}, CurrentTick={CurrentTick}, activeIds.Count={_activePlayerIds.Count}, recommendedExtraDelay={RecommendedExtraDelay}) {sb}");
            else
                _logger?.KDebug($"[KlothoEngine][ChainBreak] stuck at tick={tick} (_lastVerifiedTick={_lastVerifiedTick}, CurrentTick={CurrentTick}, activeIds.Count={_activePlayerIds.Count}, recommendedExtraDelay={RecommendedExtraDelay}) {sb}"); // expected: only confirmed-disconnected peers are missing (reactive-fill lag)

            // One-shot buffer dump only for a real (unexpected) stall — skip the expected
            // disconnect-window case so the dump is still available for a later genuine stall.
            if (anyMissingNotDisconnected && !_chainBreakBufferDumped)
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
