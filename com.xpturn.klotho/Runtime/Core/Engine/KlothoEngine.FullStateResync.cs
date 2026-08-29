using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Full state resync. internal so the resync-policy invariant
        // (ResyncPolicy.RESYNC_RESPONSE_COOLDOWN_MS < RESYNC_TIMEOUT_MS) can be pinned by a test:
        // a serve cooldown at or above this retry interval would drop every legitimate retry.
        internal const int RESYNC_TIMEOUT_MS = 5000;

        private enum ResyncState { None, Requested, Applying }
        private ResyncState _resyncState = ResyncState.None;
        private float _resyncElapsedMs;
        private int _resyncRetryCount;
        private bool _expectingFullState;

        // True while the SD Client is waiting for the server's initial FullState broadcast at session start.
        // Used for branching at the entry of HandleServerDrivenFullStateReceived and for blocking tick progression in the parent Update.
        // Mutually exclusive with _expectingFullState (Late Join).
        private bool _expectingInitialFullState;

        // Full state cache (host side)
        private byte[] _cachedFullState;
        private long _cachedFullStateHash;
        private int _cachedFullStateTick = -1;
        private bool _staticMismatchLogged;

        // Counts FullStateResponse messages dropped by the silent-ignore guard in
        // HandleFullStateReceived (received outside Requested / expectingFullState state).
        // Exposed to the network-service telemetry emitter.
        private int _unexpectedFullStateDropCount;
        internal int UnexpectedFullStateDropCount => _unexpectedFullStateDropCount;

        // Local tick-0 state hash, for the bootstrap-FullState comparison in HandleFullStateReceived.
        // 0 = unavailable (see where it is written), and the comparison is then skipped.
        private long _bootstrapStateHash;

        // Counts hash-mismatch observations in ApplyFullState. Quantifies the residual
        // divergence frequency — operational input for corrective-reset priority.
        private int _resyncHashMismatchCount;
        internal int ResyncHashMismatchCount => _resyncHashMismatchCount;

        // Per-peer desync escalation state. Keyed by remotePlayerId; the post-apply
        // self-state-mismatch bridge uses SelfDesyncPeerKey. A match with one peer no longer wipes
        // another peer's accumulation, so 3+-player partial desyncs reach the resync threshold.
        //  - _desyncCountByPeer:          consecutive desync count per peer (the escalation counter).
        //  - _lastCountedDesyncTickByPeer: per-peer dedup — LAST (most-recent-arrival) counted tick;
        //    one count per (peer, tick).
        //  - _lastMismatchedTickByPeer:    per-peer MAX mismatched tick — the forward-progress gate
        //    for clearing a peer's counter on a recovery match. NOT mergeable with the dedup field:
        //    LAST vs MAX diverge under non-consecutive out-of-order arrival.
        private readonly Dictionary<int, int> _desyncCountByPeer = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _lastCountedDesyncTickByPeer = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _lastMismatchedTickByPeer = new Dictionary<int, int>();
        private const int SelfDesyncPeerKey = -1;

        // Peak per-peer desync count reached during the match. Quantifies divergence
        // pressure: low peak = sporadic, high peak ≥ DesyncThresholdForResync = repeated
        // escalation. Resets when the desync streak ends.
        private int _consecutiveDesyncPeak;
        internal int ConsecutiveDesyncPeak => _consecutiveDesyncPeak;

        // Total RequestFullStateResync invocations across the match. Separate from
        // _resyncRetryCount which resets after a completed resync; this counter never resets
        // so the operator sees the lifetime resync-request burden.
        private int _resyncRequestTotalCount;
        internal int ResyncRequestTotalCount => _resyncRequestTotalCount;

        // Desync detections that occur after at least one successful resync — i.e. the
        // resync recovered to a clean state and the match diverged again. A high value
        // signals non-deterministic serialization or a persistent state bug.
        private int _postResyncDesyncCount;
        internal int PostResyncDesyncCount => _postResyncDesyncCount;

        private bool _hasCompletedResync;

        // Corrective Reset
        private long _lastCorrectiveResetMs;
        // Tick (frame) of the most recent corrective-reset broadcast. A guest failure report whose
        // divergence tick predates this is stale (in-flight, sent before the guest could apply the
        // reset) and must not abort an exhausted budget — only a report at/after the reset tick
        // means the reset itself failed to converge.
        private int _lastCorrectiveResetTick = -1;

        // Recovery ladder rungs 3-4: host-side corrective-reset attempt budget,
        // fed by guest ResyncFailureReport messages. Attempts decay back to zero after a quiet
        // period (max(CorrectiveResetCooldownMs x 2, ResyncMaxRetries x RESYNC_TIMEOUT_MS +
        // CorrectiveResetCooldownMs) without reports — must exceed the worst-case RetryExhausted
        // cadence) so isolated episodes hours apart do not accumulate toward an abort.
        private int _correctiveResetAttempts;
        private long _lastResyncFailureReportMs;

        /// <summary>
        /// Event handler for ReplaySystem.OnInitialStateSnapshotSet.
        /// When the game code calls SetInitialStateSnapshot(data, hash), fills _cachedFullState* based on tick 0.
        /// The SD Server later reuses this cache in BroadcastFullState at the tail of HandleGameStart.
        /// On P2P / SD Client the cache is not broadcast, but it is always set for consistency and future reuse.
        /// </summary>
        private void HandleInitialStateSnapshotSet(byte[] snapshot, long hash)
        {
            _cachedFullState = snapshot;
            _cachedFullStateHash = hash;
            _cachedFullStateTick = 0;
        }

        #region Full State Resync

        /// <summary>
        /// Returns true when a corrective reset was actually broadcast — cooldown-suppressed
        /// or guarded calls return false so they do not consume a rung-3 attempt.
        /// </summary>
        private bool TryCorrectiveReset(int divergenceTick)
        {
            if (!_networkService.IsHost) return false;
            if (State.IsEnded()) return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastCorrectiveResetMs < _simConfig.CorrectiveResetCooldownMs)
            {
                _logger?.KWarning($"[KlothoEngine][CorrectiveReset] cooldown active, skip (elapsed={now - _lastCorrectiveResetMs}ms)");
                return false;
            }
            _lastCorrectiveResetMs = now;
            _lastCorrectiveResetTick = CurrentTick;

            if (_cachedFullStateTick != CurrentTick)
            {
                var (data, hash) = _simulation.SerializeFullStateWithHash();
                _cachedFullState = data;
                _cachedFullStateHash = hash;
                _cachedFullStateTick = CurrentTick;
            }

            _logger?.KWarning($"[KlothoEngine][CorrectiveReset] broadcast: tick={CurrentTick}, divergenceTick={divergenceTick}");
            _networkService.BroadcastFullState(CurrentTick, _cachedFullState, _cachedFullStateHash, FullStateKind.CorrectiveReset);

            // Host self-apply — mirrors HandleFullStateReceived post-processing (host does not receive
            // its own broadcast). CorrectiveReset allows retreat, so the result is never Skipped.
            // Capture the pre-reset verified tick BEFORE ApplyFullState (ResetTransient below overwrites
            // _lastVerifiedTick to CurrentTick-1, which would include unrecorded predicted ticks). F47:
            // the reset's state jump has no replay record, so truncate here too — the host originates
            // every corrective reset and must not leave its own replay running past the jump (M1).
            int preResetTotalTicks = ComputeReplayTotalTicks();
            ApplyFullState(CurrentTick, _cachedFullState, _cachedFullStateHash, ApplyReason.CorrectiveReset);
            TruncateReplayForStateJump(ApplyReason.CorrectiveReset, preResetTotalTicks, CurrentTick);
            ResetTransientSyncStateAfterFullState(CurrentTick);
            _lastMatchedSyncTick = CurrentTick;
            _prevMatchedSyncTick = 0;
            ClearPerPeerDesyncState();
            _resyncRetryCount = 0;
            return true;
        }

        private void HandleHashMismatchForCorrectiveReset(int tick, long localHash, long remoteHash)
        {
            TryCorrectiveReset(tick);
        }

        /// <summary>
        /// Host-side handler for guest ResyncFailureReport messages (recovery ladder rungs 3-4).
        /// Spends one corrective-reset attempt per report; when the budget
        /// (CorrectiveResetMaxAttempts) is exhausted, either broadcasts MatchAbort and aborts
        /// locally (AutoAbortOnRecoveryExhausted) or logs an error and defers to the game layer.
        /// Cooldown-suppressed resets do not consume attempts. When the budget is exhausted, a
        /// report whose divergence tick predates the latest reset is stale (in-flight before the
        /// guest applied that reset) and is absorbed rather than aborting — only a report at/after
        /// the reset tick proves the reset failed and warrants the abort. The
        /// decay quiet-period is derived from the worst-case report cadence;
        /// see _correctiveResetAttempts.
        /// </summary>
        private void HandleResyncFailureReported(int playerId, int tick)
        {
            if (!_networkService.IsHost) return;
            if (State.IsEnded()) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Quiet period must exceed the WORST-CASE report cadence — RetryExhausted reports
            // can only arrive every ResyncMaxRetries × RESYNC_TIMEOUT_MS; a shorter window
            // decays the budget before every increment and rung 4 becomes unreachable.
            // Defaults: max(10s, 3×5s + 5s) = 20s.
            long decayQuietMs = Math.Max(
                _simConfig.CorrectiveResetCooldownMs * 2L,
                (long)_simConfig.ResyncMaxRetries * RESYNC_TIMEOUT_MS + _simConfig.CorrectiveResetCooldownMs);
            if (_lastResyncFailureReportMs > 0 && now - _lastResyncFailureReportMs > decayQuietMs)
            {
                _logger?.KInformation($"[KlothoEngine][RecoveryLadder] attempt budget decayed after quiet period ({now - _lastResyncFailureReportMs}ms), attempts {_correctiveResetAttempts} -> 0");
                _correctiveResetAttempts = 0;
            }
            _lastResyncFailureReportMs = now;

            if (_correctiveResetAttempts >= _simConfig.CorrectiveResetMaxAttempts)
            {
                // The budget is spent — but a report whose divergence tick PREDATES the latest
                // corrective reset is stale: it was in flight before the guest could apply that
                // reset, so it is answering pre-reset state and must not abort a reset that has not
                // yet had a chance to converge. A report at/after the reset tick
                // means the guest applied the reset and still diverges — the reset failed, so abort.
                // (Time-based cooldown was the wrong signal: under zero latency a fresh post-reset
                // report arrives within the cooldown yet legitimately warrants an abort.)
                // Gating only the exhaustion path (not the climb below) leaves the budget-build and
                // the host self-detection reset cadence untouched.
                if (_lastCorrectiveResetTick >= 0 && tick < _lastCorrectiveResetTick)
                {
                    _logger?.KInformation($"[KlothoEngine][RecoveryLadder] stale exhausted report absorbed (reportTick={tick} < lastResetTick={_lastCorrectiveResetTick}): playerId={playerId}");
                    return;
                }
                if (_simConfig.AutoAbortOnRecoveryExhausted)
                {
                    _logger?.KError($"[KlothoEngine][RecoveryLadder] exhausted ({_correctiveResetAttempts}/{_simConfig.CorrectiveResetMaxAttempts}), aborting match: playerId={playerId}, tick={tick}");
                    _networkService.BroadcastMatchAbort((byte)AbortReason.StateDivergence);
                    AbortMatch(AbortReason.StateDivergence);
                }
                else
                {
                    _logger?.KError($"[KlothoEngine][RecoveryLadder] exhausted ({_correctiveResetAttempts}/{_simConfig.CorrectiveResetMaxAttempts}) but AutoAbortOnRecoveryExhausted=false — game layer must decide: playerId={playerId}, tick={tick}");
                }
                return;
            }

            _logger?.KWarning($"[KlothoEngine][RecoveryLadder] resync failure reported: playerId={playerId}, tick={tick}, attempts={_correctiveResetAttempts}/{_simConfig.CorrectiveResetMaxAttempts}");
            if (TryCorrectiveReset(tick))
                _correctiveResetAttempts++;
        }

        /// <summary>
        /// Guest-side handler for the host's MatchAbort broadcast (rung 4).
        /// </summary>
        private void HandleMatchAbortReceived(int reason)
        {
            _logger?.KError($"[KlothoEngine][RecoveryLadder] MatchAbort received from host: reason={(AbortReason)reason}");
            AbortMatch((AbortReason)reason);
        }

        private void RequestFullStateResync()
        {
            if (_resyncState != ResyncState.None)
                return;

            if (_networkService.IsHost)
                return;

            _resyncRetryCount++;
            _resyncRequestTotalCount++;

            if (_resyncRetryCount > _simConfig.ResyncMaxRetries)
            {
                _logger?.KError($"[KlothoEngine][FullStateResync] failed: max retry count exceeded");
                OnResyncFailed?.Invoke();
                // Rung-3 escalation: local retries are exhausted — hand the episode
                // to the host's recovery ladder. Reset the retry counter so the desync path can
                // attempt fresh local resyncs if the host's corrective reset restores flow.
                _networkService.SendResyncFailureReport(CurrentTick, ResyncFailureReason.RetryExhausted, 0, 0);
                _resyncRetryCount = 0;
                return;
            }

            _resyncState = ResyncState.Requested;
            _resyncElapsedMs = 0;

            _logger?.KWarning($"[KlothoEngine][FullStateResync] requested (attempt {_resyncRetryCount}/{_simConfig.ResyncMaxRetries})");
            _networkService.SendFullStateRequest(CurrentTick);
        }

        private void CheckResyncTimeout(float deltaTime)
        {
            _resyncElapsedMs += deltaTime * 1000f;
            if (_resyncElapsedMs >= RESYNC_TIMEOUT_MS)
            {
                _logger?.KWarning($"[KlothoEngine][FullStateResync] timed out, retrying...");
                _resyncState = ResyncState.None;
                RequestFullStateResync();
            }
        }

        public void ExpectFullState()
        {
            _expectingFullState = true;
        }

        public void CancelExpectFullState()
        {
            _expectingFullState = false;
        }

        /// <summary>
        /// SD client only: arms the routing flag so the next FullState arrival is treated as the
        /// initial-state broadcast (HandleInitialFullStateReceived path) rather than a determinism resync.
        /// Called from the SD client transport layer when it learns the game is starting — works for both
        /// countdown-enabled (covered by HandleCountdownStarted) and countdown-skip configurations.
        /// </summary>
        public void MarkExpectingInitialFullState()
        {
            _expectingInitialFullState = true;
        }

        /// <summary>
        /// Outcome of <see cref="ApplyFullState"/>. Skipped means the retreat guard rejected the
        /// state and NOTHING was applied — callers must skip all post-processing (clearing input
        /// history for a state that was never applied stalls the chain).
        /// <para><b>DerivativeRebuildFailed</b> — the state was applied AND its hash matched, but the
        /// game's <see cref="ISimulationCallbacks.OnFullStateApplied"/> hook threw while rebuilding a
        /// peer-local derivative (navmesh, …). Distinct from HashMismatch on purpose: the restored
        /// state is trustworthy, so this must NOT take the SD unrecoverable/abort branch — what is
        /// wrong is this peer's local derivative, which usually means its assets or config disagree
        /// with the authority's. Re-requesting the same full state re-runs the same deterministic
        /// rebuild and fails identically, so there is no automatic recovery to trigger; the value
        /// exists so a caller can refuse to report this peer as healthy and so the KError beside it
        /// is not the only trace.</para>
        /// </summary>
        private enum FullStateApplyResult { Applied, Skipped, HashMismatch, DerivativeRebuildFailed }

        /// <summary>
        /// Common full-state restoration for P2P/SD. The caller performs mode-specific post-processing.
        /// </summary>
        private FullStateApplyResult ApplyFullState(int tick, byte[] stateData, long stateHash, ApplyReason reason)
        {
            // Retreat guard — CorrectiveReset/LateJoin/InitialFullState allow retreat; others do not.
            bool allowRetreat = reason == ApplyReason.CorrectiveReset
                               || reason == ApplyReason.LateJoin
                               || reason == ApplyReason.InitialFullState;
            if (!allowRetreat && _lastVerifiedTick >= tick)
            {
                _logger?.KWarning($"[KlothoEngine][ApplyFullState] skip retreat: _lastVerifiedTick={_lastVerifiedTick} >= tick={tick}, reason={reason}");
                return FullStateApplyResult.Skipped;
            }

            // 1. Replace local state
            _simulation.RestoreFromFullState(stateData);

            // 1.5. Hash verification
            long localHash = _simulation.GetStateHash();
            bool hashMatched = localHash == stateHash;
            _logger?.KInformation($"[KlothoEngine][FullStateResync] hash check: tick={tick} local=0x{localHash:X16} remote=0x{stateHash:X16} match={hashMatched}");
            if (!hashMatched)
            {
                _resyncHashMismatchCount++;
                _logger?.KError($"[KlothoEngine][FullStateResync] hash mismatch: local=0x{localHash:X16}, remote=0x{stateHash:X16}. Deserialization may be non-deterministic - resync state unreliable");
                // The registered component set is itself state-hash input, so a registry difference
                // makes every hash differ regardless of world contents. Compare this line with the
                // other peer's before reading the per-component breakdown below — if they differ,
                // the components are a symptom and the builds are the cause.
                //
                // Information, not Error: this is context for the failure just reported, not a
                // second failure. Logging it at Error double-counts one problem and — as Unity's
                // test framework showed — fails any test that deliberately provokes a mismatch and
                // expects exactly one error. Same level as the component/static diagnostics below.
                _logger?.KInformation(
                    $"[KlothoEngine][Registry] local: types={xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutTypeCount} " +
                    $"fp=0x{xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutFingerprint:X16} " +
                    $"— compare with the peer's [Registry] line; a mismatch means different builds (e.g. Unity Editor vs player build)");

                // Diagnostic — per-component hash to identify which component(s) diverged.
                if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                {
                    ecsSimDiag.LogComponentHashes(_logger, "ClientApplyMismatch");
                    ecsSimDiag.LogStaticFingerprint(_logger, "ClientApplyMismatch");
                }

                OnHashMismatch?.Invoke(tick, localHash, stateHash);
                // External notification only. Escalation accounting happens in the caller via
                // RegisterDesyncForEscalation — calling it here would be suppressed by the
                // in-progress guard (_resyncState is still Applying at this point).
                OnDesyncDetected?.Invoke(localHash, stateHash);
            }

            // 2. Reset snapshot manager
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
                ecsSim.ClearSnapshots();

            // 3. Tick synchronization
            CurrentTick = tick;
            
            // Keep the service's piggyback tick in sync — _localTick is otherwise only updated
            // inside tick execution, so every send before the first post-restore tick (LateJoin/
            // Reconnect prefill included) would carry a stale (or zero) SenderTick and blow up the
            // receiver's advantage by hundreds of ticks — max throttle right in the catchup
            // window (found via Sweep RelaySealDropCount).
            _networkService?.SetLocalTick(CurrentTick);

            // 4. Save new snapshot
            SaveSnapshot(CurrentTick);

            // 5. Clear event buffer
            // _eventBuffer.ClearAll() already returns this engine's buffered events to EventPool
            // slot-by-slot. EventPool is process-global (static, shared across engines/threads),
            // so an engine-scoped EventPool.ClearAll() here would wipe other
            // live engines' _outstanding tracking + pooled stock. Do NOT ClearAll the pool here.
            _eventBuffer.ClearAll();

            // Watermark cascade: ClearAll discards all buffered Synced events, so previously-
            // dispatched ticks no longer have buffered evidence. Lower watermark below tick so
            // future Synced events at tick or later can dispatch.
            if (_syncedDispatchHighWaterMark >= tick)
                _syncedDispatchHighWaterMark = tick - 1;

            // 6. Reset accumulator
            _accumulator = 0.0f;

            // Clamp _lastBatchedTick on corrective reset to prevent FireVerifiedInputBatch under-fire.
            if (reason == ApplyReason.CorrectiveReset)
                _lastBatchedTick = Math.Min(_lastBatchedTick, tick - 1);

            // fire-forward backstop: if the restored timeline is a match-ended state
            // but OnMatchEnded never fired, fire it once. Covers a match-end Synced event lost from the
            // event ring (ring-wipe) or skipped by a forward-jump ClearAll — both recover via
            // this ApplyFullState path. The _matchEndedDispatched guard keeps exactly-once (never un-fire);
            // hashMatched-only so an untrusted (HashMismatch) state never fires it. The payload is the
            // simulation's engine-agnostic IMatchEndEvent (winner/reason; game subtype not preserved here).
            if (hashMatched && !_matchEndedDispatched && _simulation.IsMatchEndedState)
            {
                var endEvt = _simulation.GetActiveMatchEnd();
                if (endEvt != null)
                {
                    _matchEndedDispatched = true;
                    OnMatchEnded?.Invoke(tick, endEvt);
                }
            }

            // Peer-local derivative rebuild hook: the restored frame is live from
            // this point (even on HashMismatch the local sim runs on it), so out-of-hash
            // derivatives (navmesh) must be rebuilt to match it before the next tick.
            //
            // The hook runs on the HashMismatch branch too, deliberately: the local sim keeps
            // ticking on the restored state either way, so its derivatives have to track it. That
            // also means an UNTRUSTED building set reaches the rebaker, which is why the rebaker's
            // re-validation is kept (it is provably redundant on the trusted path — every building
            // cleared a trial rebake of the whole set at placement time and the checks are all
            // per-building or pairwise-monotone — but that proof does not cover a mismatched state).
            // If a check that looks at the building set AS A WHOLE is ever added, that monotonicity
            // argument breaks and this path throws for the first time.
            //
            // Caught here rather than left to the callers because the engine owes them a return
            // value and cannot undo the apply. A throw escaping this method skips not just this
            // return but every caller's post-apply bookkeeping — replay truncation, sync-state
            // reset, and the SD client's StartCatchingUp among them — leaving a live restored state
            // with half-open books. Catch is deliberately unbounded: the contract this defends is
            // "the implementation does not throw", and a violation of it arrives as an arbitrary
            // type, so narrowing to the exceptions today's callee happens to raise would reopen
            // the hole. The KError below carries the full exception so the breadth costs diagnosis
            // nothing, and the game seam is expected to handle its own domain errors regardless.
            bool derivativeRebuildFailed = false;

            // The navmesh half of that rebuild, done by the engine. The restored BuildingComponents
            // are frame state but the mesh is still whatever this peer had, so it has to be made to
            // match — and it has to happen HERE rather than on the next tick, because the receive
            // path compares this peer's nav fingerprint against the sender's post-rebake one
            // immediately after the apply. A peer one tick stale reports a false mismatch, and that
            // report is one-shot: the false one consumes the report a real divergence in the same
            // window would have needed.
            //
            // ⚠ Its own block, deliberately NOT inside the `_simulationCallbacks != null` guard
            // below. The test harnesses initialize without callbacks (three of them, and roughly
            // fifty call sites), so anything placed behind that guard cannot be reached by a test at
            // all — a net that is always green. That mistake has been made in this codebase before,
            // in the same seam, and it is why this reads as duplicated structure rather than being
            // folded into the block below.
            //
            // Runs on the HashMismatch branch too, for the same reason the game hook does: the local
            // simulation keeps ticking on the restored state either way, so its derivatives have to
            // track it. That also means an UNTRUSTED building set reaches the rebaker, which is why
            // the rebaker's re-validation is kept.
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsNav && _navRebakeDriver != null)
            {
                try
                {
                    var navFrame = ecsNav.Frame;
                    _navRebakeDriver.CorrectNow(ref navFrame);
                }
                catch (Exception ex)
                {
                    derivativeRebuildFailed = true;
                    _logger?.KError(
                        $"[KlothoEngine][FullStateResync] the navmesh correction threw at tick={tick} " +
                        $"reason={reason} hashMatched={hashMatched} — the state stays applied but this " +
                        $"peer's navmesh no longer matches it, so the next fingerprint comparison will " +
                        $"report a divergence this peer caused. {ex}");
                }
            }

            if (_simulationCallbacks != null && _simulation is xpTURN.Klotho.ECS.EcsSimulation ecsApplied)
            {
                try
                {
                    _simulationCallbacks.OnFullStateApplied(this, ecsApplied.Frame);
                }
                catch (Exception ex)
                {
                    derivativeRebuildFailed = true;
                    _logger?.KError(
                        $"[KlothoEngine][FullStateResync] OnFullStateApplied threw at tick={tick} reason={reason} " +
                        $"hashMatched={hashMatched} — the state stays applied but this peer's out-of-hash " +
                        $"derivatives (navmesh) no longer match it. Implementations must not throw; the game seam " +
                        $"should handle its own domain errors. {ex}");
                }
            }

            if (!hashMatched)
                return FullStateApplyResult.HashMismatch;   // the worse signal wins and stays honest
            return derivativeRebuildFailed
                ? FullStateApplyResult.DerivativeRebuildFailed
                : FullStateApplyResult.Applied;
        }

        /// <summary>
        /// Shared transient-state reset after a full state has actually been applied
        /// (HandleFullStateReceived post-processing and the corrective-reset self-apply mirror).
        /// </summary>
        private void ResetTransientSyncStateAfterFullState(int tick)
        {
            _inputBuffer.Clear();
            _pendingCommands.Clear();
            // State-jump watermark, NOT a promotion — does not call RecordVerifiedTick.
            // For corrective reset / resync, recording has already been truncated (StopRecording) BEFORE
            // this runs, so IsRecording is false here; the cleared buffer is never recorded.
            _lastVerifiedTick = tick - 1;
            _lastMismatchedSyncTick = -1; // new baseline — stale mismatch records no longer apply
            _hasPendingRollback = false;
            _pendingRollbackTick = -1;

            // Drop deferred-hash-send entries staged before the jump. After a
            // FullState apply (esp. a backward jump), pre-jump check-tick entries are orphans; the
            // chain re-advance would otherwise re-cross them and re-send (duplicate) the recomputed
            // hash, or leak entries below the new verified floor. Re-arm is driven by ExecuteTick
            // recompute, so clearing loses no legitimate send.
            _deferredHashSendTicks.Clear();

            // Drop the network layer's stale sync hashes >= tick (local + remote)
            // so the post-apply recompute is not compared against a pre-reset remote hash across the
            // reset boundary (false mismatch). P2P-only — this method is never reached on the SD path.
            _networkService?.InvalidateSyncHashes(tick);
        }

        /// <summary>
        /// Truncates the replay recording at a mid-match state jump (corrective reset / resync) so the
        /// recording never holds ticks past the last verified one — the jump has no replay record and
        /// replaying past it diverges (F47). Shared by every state-jump entry point that records on the
        /// P2P line: the host's <see cref="TryCorrectiveReset"/> self-apply and the guest's
        /// <see cref="HandleFullStateReceived"/>. Session-start applies (LateJoin / InitialFullState) are
        /// NOT truncated — recording boundaries, not mid-match jumps; reconnect recovery flows as
        /// ResyncRequest and IS truncated (also a state jump).
        /// <para><paramref name="preResetTotalTicks"/> MUST be captured BEFORE ApplyFullState —
        /// <see cref="ResetTransientSyncStateAfterFullState"/> overwrites _lastVerifiedTick to tick-1,
        /// which would include unrecorded predicted ticks. A second call after the first truncation is a
        /// no-op (IsRecording already false), so repeated attempts never double-finalize.</para>
        /// </summary>
        private void TruncateReplayForStateJump(ApplyReason reason, int preResetTotalTicks, int resetTick)
        {
            if (!_replaySystem.IsRecording) return;
            if (reason != ApplyReason.CorrectiveReset && reason != ApplyReason.ResyncRequest) return;
            int truncateAt = preResetTotalTicks < 0 ? 0 : preResetTotalTicks;
            _replaySystem.StopRecording(truncateAt);
            _logger?.KWarning($"[KlothoEngine][Replay] truncated at {reason}: resetTick={resetTick}, totalTicks={truncateAt}");
        }

        /// <summary>
        /// Sender-side static environment fingerprint for outbound FullState messages:
        /// static collider geometry folded with the peer-local navigation fingerprint
        /// (the navmesh, including runtime rebakes, is outside the state hash).
        /// Returns 0 when neither source is present (wire sentinel: "not provided").
        /// </summary>
        public long GetLocalStaticFingerprint()
        {
            return ComputeLocalStaticFingerprint();
        }

        /// <summary>
        /// Static colliders XOR navigation fingerprint. Both sources are per-peer static and
        /// excluded from the state hash; folding keeps the existing single wire field
        /// (FullStateResponseMessage.StaticFingerprint) — no wire change. 0 stays reserved as
        /// the "not provided" sentinel.
        /// </summary>
        private long ComputeLocalStaticFingerprint()
        {
            if (_simulation is not xpTURN.Klotho.ECS.EcsSimulation)
                return 0;

            // Component-registry layout is folded in first. It is always present, so peers now
            // always have something comparable — and a registry difference is worth surfacing
            // HERE, at join, rather than letting it appear later as an inexplicable state-hash
            // divergence (the registered type set is state-hash input; see LayoutFingerprint).
            //
            // Split into (layout ^ environment) so the Ready path can carry the two halves in
            // separate wire fields and react to them differently (a layout difference is fatal
            // from tick 0; an environment difference is not part of the state hash). This value
            // stays bit-identical to the pre-split fold: the environment half is raw and the
            // 0 -> 1 sentinel normalization still happens here and only here.
            long fp = xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutFingerprint
                    ^ ComputeLocalEnvironmentFingerprintRaw();

            if (fp == 0)
                fp = 1; // preserve the 0 = "not provided" wire sentinel
            return fp;
        }

        /// <summary>
        /// Environment half only — static colliders XOR navigation XOR the game's own slot, with the
        /// component registry deliberately EXCLUDED so the two Ready-path fields stay orthogonal
        /// (a layout difference must not also move this value). Raw: no 0 -> 1 normalization, so a
        /// peer with no environment source at all reports 0 and receivers skip it, exactly as the
        /// combined path treats "not provided".
        /// </summary>
        private long ComputeLocalEnvironmentFingerprintRaw()
        {
            if (_simulation is not xpTURN.Klotho.ECS.EcsSimulation ecs)
                return 0;

            long fp = 0;
            var svc = ecs.GetSystem<xpTURN.Klotho.Deterministic.Physics.IStaticColliderService>();
            if (svc != null)
                fp ^= svc.GetStaticFingerprint();

            var nav = ecs.GetSystem<xpTURN.Klotho.Deterministic.Navigation.INavFingerprintSource>();
            long navFp = nav != null ? nav.GetNavFingerprint() : 0;
            fp ^= navFp;

            // The game's own slot. The two sources above are things the engine can see for
            // itself; this is for what it cannot — a shape catalog, a tuning table, an asset id
            // map: data that must be identical across builds and is deliberately outside the
            // state hash. Without it a game with such data has nowhere to put it but inside one
            // of the engine's terms, which then reports its divergence as a navmesh problem.
            var game = ecs.GetSystem<xpTURN.Klotho.ECS.IGameFingerprintSource>();
            if (game != null)
                fp ^= game.GetGameFingerprint();

            return fp;
        }

        /// <summary>
        /// Receiver-side check: compares the server-sent static environment fingerprint (static
        /// colliders + navmesh) against the local one and logs on mismatch. Independent of the
        /// state-hash check — neither source is part of the state hash, so a divergence would
        /// otherwise surface only several ticks later as a dynamic-body/agent desync. A wire
        /// value of 0 means "not provided" and is skipped. A navmesh mismatch at join time can
        /// also mean the joining peer has not yet re-applied runtime rebakes (buildings) from
        /// the received state — the game seam must rebake after FullState apply.
        /// </summary>
        public void CheckStaticGeometryFingerprint(long serverStaticFingerprint)
        {
            if (serverStaticFingerprint == 0) return;

            long localFp = ComputeLocalStaticFingerprint();
            if (localFp == 0) return; // nothing registered locally to compare

            if (localFp == serverStaticFingerprint)
            {
                _staticMismatchLogged = false; // matched — re-arm so a later divergence logs again
                return;
            }

            if (_staticMismatchLogged) return; // already reported this divergence — suppress per-resync spam
            _staticMismatchLogged = true;
            // Break the fold down so the reader can tell WHICH source diverged by comparing this
            // line with the peer's, instead of only knowing that the XOR differs.
            var colliderSvc = (_simulation as xpTURN.Klotho.ECS.EcsSimulation)
                ?.GetSystem<xpTURN.Klotho.Deterministic.Physics.IStaticColliderService>();
            var navSrc = (_simulation as xpTURN.Klotho.ECS.EcsSimulation)
                ?.GetSystem<xpTURN.Klotho.Deterministic.Navigation.INavFingerprintSource>();
            var gameSrc = (_simulation as xpTURN.Klotho.ECS.EcsSimulation)
                ?.GetSystem<xpTURN.Klotho.ECS.IGameFingerprintSource>();
            _logger?.KError(
                $"[KlothoEngine] Static environment mismatch: server=0x{serverStaticFingerprint:X16} local=0x{localFp:X16} " +
                $"— local sources: registry(types={xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutTypeCount})=0x{xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutFingerprint:X16} " +
                $"colliders=0x{(colliderSvc?.GetStaticFingerprint() ?? 0):X16} nav=0x{(navSrc?.GetNavFingerprint() ?? 0):X16} " +
                $"game=0x{(gameSrc?.GetGameFingerprint() ?? 0):X16}. " +
                $"Compare each against the peer's: a registry difference means different builds (e.g. Unity Editor vs player build); " +
                $"a game difference is whatever that game folded in (shape catalog, tuning tables); " +
                $"otherwise check RegisterSystems static load / post-FullState rebake");
        }

        private void HandleFullStateReceived(int tick, byte[] stateData, long stateHash, FullStateKind kind)
        {
            bool isResync = _resyncState == ResyncState.Requested;
            bool isCorrectiveReset = kind == FullStateKind.CorrectiveReset;

            // The host broadcasts the tick-0 state to everyone at bootstrap. A peer that was present at game
            // start already produced the identical snapshot locally, so dropping this copy is correct — but
            // it is not an anomaly, and counting it as one made unexpectedFullStateDrop useless: it fired
            // exactly once per guest per session, every session. Late joiners are the peers that need the
            // payload, and they arrive here with _expectingFullState set.
            //
            // The drop is also the one moment we can compare tick-0 state across peers for free, so a hash
            // difference is escalated instead of ignored: it means the two sides disagreed before the first
            // tick, which no later desync report can attribute. _bootstrapStateHash is taken in
            // HandleGameStart for every session and cleared by Stop(), so 0 here means the local tick-0
            // state was never established (this peer never reached HandleGameStart) and there is nothing
            // to compare against.
            if (kind == FullStateKind.InitialState && !isResync && !_expectingFullState)
            {
                if (_bootstrapStateHash != 0 && _bootstrapStateHash != stateHash)
                {
                    _unexpectedFullStateDropCount++;
                    _logger?.KError(
                        $"[KlothoEngine][FullStateResync] Bootstrap FullState disagrees with the local tick-0 state: " +
                        $"remote=0x{stateHash:X16} local=0x{_bootstrapStateHash:X16} — the peers diverged before tick 0 " +
                        $"(check RegisterSystems static load / OnInitializeWorld determinism)");
                    return;
                }
                _logger?.KDebug($"[KlothoEngine][FullStateResync] Bootstrap FullState ignored — already held locally (tick={tick}, hash=0x{stateHash:X16})");
                return;
            }

            if (!isResync && !_expectingFullState && !isCorrectiveReset)
            {
                _unexpectedFullStateDropCount++;
                _logger?.KWarning($"[KlothoEngine][FullStateResync] FullStateResponse received but not in Requested state, ignoring (drops={_unexpectedFullStateDropCount})");
                return;
            }

            if (isResync)
                _resyncState = ResyncState.Applying;
            _expectingFullState = false;

            // Common restoration
            ApplyReason reason = isCorrectiveReset
                ? ApplyReason.CorrectiveReset
                : (isResync ? ApplyReason.ResyncRequest : ApplyReason.LateJoin);
            // Capture the pre-reset verified tick BEFORE ApplyFullState — the post-apply
            // ResetTransientSyncStateAfterFullState overwrites _lastVerifiedTick to tick-1 (which would
            // include unrecorded predicted ticks). Same clamp value as ComputeReplayTotalTicks.
            int preResetTotalTicks = ComputeReplayTotalTicks();
            CaptureResyncTransforms();
            var applyResult = ApplyFullState(tick, stateData, stateHash, reason);
            ComputeResyncErrorDeltas(reason, tick, applyResult);

            if (applyResult == FullStateApplyResult.Skipped)
            {
                // Retreat guard rejected the state — NOTHING was applied. Skip all post-processing
                // (clearing input history here would stall the chain with the state unapplied,
                // and re-arm the retry path: the host replies with its CurrentTick,
                // so a timeout retry (~5s ≈ 200 ticks later) overtakes _lastVerifiedTick and
                // passes the guard.
                if (isResync)
                    _resyncState = ResyncState.Requested;
                _logger?.KWarning($"[KlothoEngine][FullStateResync] apply skipped (retreat guard) at tick={tick}; awaiting timeout retry");
                return;
            }

            // DerivativeRebuildFailed counts as matched here because it IS matched — the restored
            // state and its hash are sound, only this peer's out-of-hash derivative rebuild failed.
            // So the state bookkeeping below is correct as-is and must run. What must NOT happen is
            // treating the peer as fully healthy, which is why the value is distinct and logged.
            bool hashMatched = applyResult != FullStateApplyResult.HashMismatch;
            bool derivativeRebuildFailed = applyResult == FullStateApplyResult.DerivativeRebuildFailed;

            // Truncate the replay at a corrective reset / resync (F47): the reset's state jump has no
            // replay record, so replaying past it diverges. Applied OR HashMismatch (both applied state;
            // Skipped already returned). Shared seam — see TruncateReplayForStateJump (reason gate,
            // pre-reset capture, Stop() double-finalize guard).
            TruncateReplayForStateJump(reason, preResetTotalTicks, tick);

            if (!hashMatched && !_networkService.IsHost)
            {
                // Rung-3 feedback: a post-apply mismatch means the recovery state
                // itself diverges — local retries cannot fix that, so the host decides whether
                // to spend another corrective reset or abort.
                _networkService.SendResyncFailureReport(tick, ResyncFailureReason.ApplyMismatch, _simulation.GetStateHash(), stateHash);
            }

            // P2P-only post-processing (new state is the baseline regardless of hash).
            // The matched-sync baseline only advances when the hash agrees so a mismatched
            // tick does not become a known-good rollback target for HandleNetworkDesync.
            ResetTransientSyncStateAfterFullState(tick);

            if (hashMatched)
            {
                _lastMatchedSyncTick = tick;
                _prevMatchedSyncTick = 0;
                ClearPerPeerDesyncState();
                _resyncRetryCount = 0;
            }
            // else: preserve _lastMatchedSyncTick / per-peer desync counts / _resyncRetryCount
            // so the mid-match desync path keeps accumulating toward escalation.

            // Resync path: state restoration + event firing
            if (isResync)
            {
                _resyncState = ResyncState.None;
                if (hashMatched)
                {
                    _hasCompletedResync = true;
                    // Still "complete" even when the derivative rebuild failed: the state is
                    // restored and sound, and suppressing the event would strand a game layer
                    // that waits on it. The qualifier keeps the log from reading clean — the
                    // KError from ApplyFullState carries the cause.
                    if (derivativeRebuildFailed)
                        _logger?.KWarning($"[KlothoEngine][FullStateResync] complete: tick={tick} (WITH a failed peer-local derivative rebuild — see the preceding KError)");
                    else
                        _logger?.KWarning($"[KlothoEngine][FullStateResync] complete: tick={tick}");
                    OnResyncCompleted?.Invoke(tick);
                }
                else
                {
                    // Bridge into the desync escalation pipeline: the resync state
                    // is None again here, so the in-progress guard does not suppress it. No rung-1
                    // rollback — a mismatch right after applying a full state is a divergence in
                    // the state representation itself; rolling back to the anchor cannot help.
                    _logger?.KWarning($"[KlothoEngine][FullStateResync] applied with hash mismatch at tick={tick}; OnResyncCompleted suppressed, mid-match desync path will re-attempt");
                    RegisterDesyncForEscalation(tick, SelfDesyncPeerKey);
                }
            }

            // Corrective reset path: emit OnMatchReset on successful state restoration.
            if (isCorrectiveReset && hashMatched)
            {
                _logger?.KWarning($"[KlothoEngine][CorrectiveReset] state restored at tick={tick}, firing OnMatchReset");
                OnMatchReset?.Invoke(ResetReason.StateDivergence);
            }
        }

        private void HandleFullStateRequested(int peerId, int requestTick)
        {
            if (!_networkService.IsHost) return;

            // Reuse the serialized result for the same tick (caching)
            if (_cachedFullStateTick != CurrentTick)
            {
                var (data, hash) = _simulation.SerializeFullStateWithHash();
                _cachedFullState = data;
                _cachedFullStateHash = hash;
                _cachedFullStateTick = CurrentTick;
            }

            _networkService.SendFullStateResponse(peerId, CurrentTick, _cachedFullState, _cachedFullStateHash);

            // requestTick is logged, never acted on: the state is always served from CurrentTick
            // (provider invariant — the requester's catchup is computed from the served tick, not
            // the asked-for one). Its meaning varies by caller, so it is not a "divergence tick":
            // an SD desync sends the diverged tick, a retry sends the verified frontier, a P2P
            // guest sends its own CurrentTick. servedTick - requestTick shows how far behind the
            // requester was.
            _logger?.KWarning($"[KlothoEngine][FullStateResync] FullState sent: peer={peerId}, servedTick={CurrentTick}, requestTick={requestTick}, size={_cachedFullState.Length}");
        }

        #endregion

        /// <summary>
        /// Event-based promotion of the last-matched sync anchor (replaces the old grace-window
        /// machinery): a matched comparison promotes the anchor monotonically and clears that
        /// peer's desync counter; a tick that ever mismatched is vetoed so a host seeing both a
        /// match (peer A) and a mismatch (peer B) for the same tick never anchors on it. Anchor
        /// integrity is order-independent: the demotion for a poisoned anchor lives in
        /// HandleNetworkDesync. Absence of comparisons (dropped hashes) skips promotion.
        /// </summary>
        private void HandleSyncHashCompared(int tick, int remotePlayerId, bool matched)
        {
            if (!matched)
            {
                if (tick > _lastMismatchedSyncTick)
                    _lastMismatchedSyncTick = tick; // global veto only — demotion is in HandleNetworkDesync
                return;
            }

            // Forward-confirmed match with this peer: clear ITS counter only (per-peer).
            // Gate on the peer's own MAX mismatched tick — NOT the global veto — so another peer's
            // divergence cannot block this peer's recovery from clearing its accumulation.
            if (!_lastMismatchedTickByPeer.TryGetValue(remotePlayerId, out int peerLastMismatch) || tick > peerLastMismatch)
                _desyncCountByPeer.Remove(remotePlayerId);

            if (tick <= _lastMismatchedSyncTick)
                return;

            if (tick > _lastMatchedSyncTick)
            {
                _prevMatchedSyncTick = _lastMatchedSyncTick; // 1-step history for order-independent demotion
                _lastMatchedSyncTick = tick;
            }
        }

        private enum DesyncRegistration { Suppressed, Counted, Escalated }

        // Clears all per-peer desync escalation state. Called when a full state is applied clean
        // (resync / corrective reset / late join) — the prior accumulation described the
        // now-discarded pre-apply divergence.
        private void ClearPerPeerDesyncState()
        {
            _desyncCountByPeer.Clear();
            _lastCountedDesyncTickByPeer.Clear();
            _lastMismatchedTickByPeer.Clear();
        }

        /// <summary>
        /// Counts a desync occurrence per (peer, sync tick) and escalates to a full-state resync
        /// when ANY peer reaches the threshold. Shared by the network mismatch path
        /// (HandleNetworkDesync — which additionally attempts a rung-1 rollback) and the post-apply
        /// hash-mismatch bridge (which uses SelfDesyncPeerKey and must not roll back).
        /// Per-peer dedup: the same peer reporting one diverged
        /// tick counts once, so a single tick cannot skip rung 1 straight to resync.
        /// </summary>
        private DesyncRegistration RegisterDesyncForEscalation(int tick, int remotePlayerId)
        {
            // Suppress while a resync is already in flight.
            if (_resyncState != ResyncState.None)
                return DesyncRegistration.Suppressed;

            // Per-peer dedup — LAST counted tick (exact-tick suppression).
            if (_lastCountedDesyncTickByPeer.TryGetValue(remotePlayerId, out int lastCounted) && tick == lastCounted)
                return DesyncRegistration.Suppressed;
            _lastCountedDesyncTickByPeer[remotePlayerId] = tick;

            // Per-peer MAX mismatched tick — the forward-progress gate used by HandleSyncHashCompared.
            if (!_lastMismatchedTickByPeer.TryGetValue(remotePlayerId, out int lastMismatched) || tick > lastMismatched)
                _lastMismatchedTickByPeer[remotePlayerId] = tick;

            int peerCount = (_desyncCountByPeer.TryGetValue(remotePlayerId, out int c) ? c : 0) + 1;
            _desyncCountByPeer[remotePlayerId] = peerCount;
            if (peerCount > _consecutiveDesyncPeak)
                _consecutiveDesyncPeak = peerCount;
            if (_hasCompletedResync)
                _postResyncDesyncCount++;
            _logger?.KWarning($"[KlothoEngine][FullStateResync] Desync peer={remotePlayerId} count={peerCount}/{_simConfig.DesyncThresholdForResync}, lastMatchedSyncTick={_lastMatchedSyncTick}, currentTick={CurrentTick}");

            if (peerCount >= _simConfig.DesyncThresholdForResync)
            {
                // Resync is whole-engine recovery — clear every peer so a sibling peer's residual
                // accumulation does not re-trip the threshold immediately after the resync applies.
                ClearPerPeerDesyncState();
                RequestFullStateResync();
                return DesyncRegistration.Escalated;
            }
            return DesyncRegistration.Counted;
        }

        private void HandleNetworkDesync(int playerId, int tick, long localHash, long remoteHash)
        {
            _logger?.KError($"[KlothoEngine][FullStateResync] Desync detected at tick {tick}! Player {playerId}: local={localHash}, remote={remoteHash}");
            OnDesyncDetected?.Invoke(localHash, remoteHash);

            // Capture the ring HERE, first, while it still describes the divergence. The rung-1
            // rollback below re-sims over those same slots, and the probe's answer arrives an RTT later,
            // long after that — so the diff runs against this capture, not against the live ring. Gated
            // inside (one episode per peer, budget of 3) so the re-fires that follow a desync up the
            // ladder do not each allocate a ring-sized copy.
            TryBeginDesyncProbe(playerId, tick, localHash, remoteHash);

            // Dump the diagnostic hash history BEFORE any recovery trigger. The ring holds the
            // layered breakdown around the diverged tick; a rung-1 rollback below re-sims and overwrites
            // those slots, so flushing first is the only way P2P surfaces the diverging
            // component/system layer from a local log. Dumps once per diverged tick and leaves
            // the ring intact — the peer on the other side of this desync is about to probe it.
            _diagHistorySim?.FlushHashHistory(_logger, tick);

            // A mismatch at/before the current anchor means the anchor was a tick some
            // peer flags as diverged — promoted before this mismatch arrived (order dependency).
            // Demote to the prior matched tick UNCONDITIONALLY (before the escalation guard) so anchor
            // integrity is a local invariant, independent of Counted/Suppressed/Escalated bookkeeping.
            // Co-located with the rollback-target read below → demotion precedes it regardless of
            // OnDesyncDetected vs OnSyncHashCompared firing order.
            if (_lastMatchedSyncTick > 0 && _lastMatchedSyncTick >= tick)
                _lastMatchedSyncTick = _prevMatchedSyncTick;

            if (RegisterDesyncForEscalation(tick, playerId) != DesyncRegistration.Counted)
                return; // suppressed (in-flight resync / per-peer duplicate) or already escalated

            int rollbackTarget = _lastMatchedSyncTick > 0 ? _lastMatchedSyncTick : tick;

            // No known-good tick strictly before the divergence (1-step history exhausted on
            // consecutive/out-of-order mismatches, or a future anchor). A rollback to >= tick is
            // futile (re-sims the divergence) or silently ignored. Skip rung-1 and let the
            // per-peer counter escalate to resync — the divergence is recorded, just not rolled.
            if (rollbackTarget >= tick)
            {
                _logger?.KTrace($"[KlothoEngine][FullStateResync] rung-1 skipped: no known-good tick < {tick} (anchor={_lastMatchedSyncTick}, prev={_prevMatchedSyncTick}) — deferring to per-peer escalation");
                return;
            }

            _logger?.KWarning($"[KlothoEngine][FullStateResync] Desync recovery: rolling back to lastMatchedSyncTick={_lastMatchedSyncTick} (desync tick={tick})");
            RequestRollback(rollbackTarget);
        }
    }
}
