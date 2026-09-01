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

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // Diagnostic — throttled break-cause log for chain advance stall.
        private long _lastChainBreakLogMs;
        // Bitmask of which active-player slots were missing at the last logged break, and how many breaks
        // have been swallowed since. The mask is what makes the throttle work on the common case: the
        // frontier normally advances one tick per tick, so keying the throttle on the tick alone never
        // suppressed anything.
        private ulong _lastChainBreakMissingMask;
        private int _chainBreakSuppressed;
        private int _chainBreakSuppressedFromTick = -1;
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
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
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

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        private void LogChainAdvanceBreak(int tick)
        {
            // Which active-player slots are missing a command at the stalled tick, and is any of them a
            // player we have NOT been told disconnected. If every missing player is a confirmed-
            // disconnected peer (_disconnectedPlayerIds — populated on guests via the player state
            // notification), this stall is the expected "host proactive-fill is ~1 tick behind" condition
            // during a disconnect window and belongs at Debug. Any non-disconnected missing player = a real
            // stall → Warning.
            //
            // Computed before the throttle decision and without allocating: the mask is the throttle's key,
            // and the string is only built if we are actually going to log.
            // The mask only has to be a throttle key, so capping it at 64 slots is harmless — it can merge
            // two different causes into one key past that point and print one line late. The severity
            // decision is not capped: every player is examined for anyMissingNotDisconnected.
            ulong missingMask = 0;
            bool anyMissingNotDisconnected = false;
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int pid = _activePlayerIds[pi];
                if (_inputBuffer.HasCommandForTick(tick, pid)) continue;
                if (pi < 64) missingMask |= 1UL << pi;
                if (!_disconnectedPlayerIds.Contains(pid))
                    anyMissingNotDisconnected = true;
            }

            // Throttle: at most one line per second WHILE THE CAUSE IS UNCHANGED, plus an immediate line
            // whenever the missing set changes. The old form also required `tick == _lastLoggedTick`, which
            // made it vacuous for the dominant case — an input arriving one tick late advances the stalled
            // tick every tick, so the guard never held and every tick produced a WARN. A 2s
            // dynamic-delay warmup at 25ms therefore printed ~80 lines.
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool causeChanged = missingMask != _lastChainBreakMissingMask;
            if (!causeChanged && nowMs - _lastChainBreakLogMs < 1000)
            {
                if (_chainBreakSuppressed++ == 0) _chainBreakSuppressedFromTick = tick;
                return;
            }
            _lastChainBreakLogMs = nowMs;
            _lastChainBreakMissingMask = missingMask;

            // Asks the buffer again rather than reading the mask, so the line stays exact past 64 players.
            // Only reached on the logging path, which the throttle now makes rare.
            var sb = new System.Text.StringBuilder();
            sb.Append("present=[");
            for (int pi = 0; pi < _activePlayerIds.Count; pi++)
            {
                int pid = _activePlayerIds[pi];
                if (pi > 0) sb.Append(',');
                sb.Append(pid).Append(_inputBuffer.HasCommandForTick(tick, pid) ? '✓' : '✗');
            }
            sb.Append(']');

            // Never drop the burst size — that is the number the reader needs to tell "one late packet" from
            // "the chain has been broken for two seconds".
            if (_chainBreakSuppressed > 0)
            {
                sb.Append(" (+").Append(_chainBreakSuppressed).Append(" suppressed since tick=")
                  .Append(_chainBreakSuppressedFromTick).Append(')');
                _chainBreakSuppressed = 0;
                _chainBreakSuppressedFromTick = -1;
            }

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
