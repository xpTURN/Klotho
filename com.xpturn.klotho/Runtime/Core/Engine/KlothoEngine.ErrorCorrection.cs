using System.Collections.Generic;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        private ErrorCorrectionSettings _ecSettings = ErrorCorrectionSettings.Default;

        private readonly Dictionary<int, FPVector3> _posDeltas = new();
        private readonly Dictionary<int, FP64> _yawDeltas = new();
        private readonly HashSet<int> _teleportedEntities = new();

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        // Latched: a misconfigured game rolls back continuously, and one line is the whole message.
        private bool _warnedNoCorrectionTargets;
        // Consecutive rollbacks whose target filter matched nothing. A single observation is not evidence —
        // see the warning site.
        private int _zeroTargetRollbacks;

        // How many consecutive empty-filter rollbacks it takes before the wiring is called wrong. Sized
        // against the observed startup transient, which clears in one or two (see the warning site).
        private const int NoCorrectionTargetWarnThreshold = 8;

#endif

        // A resync's corrections, held until the frame's update has run its ClearErrorDeltas.
        //
        // The FullState handlers run from the transport poll, and the driver polls BEFORE it updates the
        // session (KlothoSessionDriver: PollEvents then s.Update) while ClearErrorDeltas is the first
        // thing the update does. Publishing straight into _posDeltas would therefore be erased in the same
        // frame, before any view read it. So the resync parks its deltas here and
        // PublishPendingResyncDeltas moves them across immediately after that clear.
        private readonly Dictionary<int, FPVector3> _pendingResyncPos = new();
        private readonly Dictionary<int, FP64> _pendingResyncYaw = new();
        private readonly HashSet<int> _pendingResyncTeleported = new();

        private readonly Dictionary<int, FPVector3> _preRollbackPos = new();
        private readonly Dictionary<int, FP64> _preRollbackYaw = new();
        private readonly Dictionary<int, int> _preRollbackTeleportTick = new();

        // -- IKlothoEngine implementation --

        public ErrorCorrectionSettings ErrorCorrectionSettings
        {
            get => _ecSettings;
            set => _ecSettings = value;
        }

        public (float x, float y, float z) GetPositionDelta(int entityIndex)
        {
            if (_posDeltas.TryGetValue(entityIndex, out var e))
                return (e.x.ToFloat(), e.y.ToFloat(), e.z.ToFloat());
            return (0f, 0f, 0f);
        }

        public float GetYawDelta(int entityIndex)
        {
            if (_yawDeltas.TryGetValue(entityIndex, out var e))
                return e.ToFloat();
            return 0f;
        }

        public bool HasEntityTeleported(int entityIndex)
        {
            return _teleportedEntities.Contains(entityIndex);
        }

        // -- Internal logic --

        /// <summary>
        /// The shortest signed yaw difference, wrapped to (-pi, pi]. Radians — <c>FP64.DeltaAngle</c> is
        /// the same idea in degrees, and <c>TransformComponent.Rotation</c> is radians.
        ///
        /// A plain subtraction reads a 90-degree turn across the +/-pi seam as 270, because the two
        /// operands sit on opposite ends of the wrap. That number then exceeds the view's rotation
        /// snap bound and the correction is thrown away instead of smoothed — the opposite of what
        /// error correction is for. Observed live: every other correction in the session measured
        /// 28.8 degrees while the one crossing the seam measured exactly 270.000.
        ///
        /// Repeat rather than a loop, so an unnormalised yaw (nothing constrains the component's range)
        /// costs the same as a normalised one.
        /// </summary>
        private static FP64 WrapYaw(FP64 radians)
        {
            FP64 wrapped = FP64.Repeat(radians, FP64.TwoPi);
            return wrapped > FP64.Pi ? wrapped - FP64.TwoPi : wrapped;
        }

        /// <summary>
        /// Yaw at <paramref name="alpha"/> between two ticks, taking the short way round.
        ///
        /// This has to agree with what the view actually drew, and both adapters interpolate yaw with
        /// LerpAngle. A straight <c>FP64.Lerp</c> sweeps the long way across the seam, so the pose this
        /// reconstructs would be one the screen never showed — and the delta measured from it describes
        /// no jump the player saw.
        /// </summary>
        private static FP64 LerpYaw(FP64 from, FP64 to, FP64 alpha)
            => from + WrapYaw(to - from) * alpha;

        private void ClearErrorDeltas()
        {
            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();
            _preRollbackPos.Clear();
            _preRollbackYaw.Clear();
            _preRollbackTeleportTick.Clear();
        }

        /// <summary>
        /// Everything the error-correction pipeline holds, including the parked resync deltas that
        /// <see cref="ClearErrorDeltas"/> deliberately does NOT touch.
        ///
        /// For the paths where the pipeline's history stops describing anything: a late join or a
        /// reconnect restores a world this peer never simulated, and <c>Stop()</c> ends the session. The
        /// parked maps are the reason this exists separately — they survive the per-frame clear by design
        /// (they are produced before it and consumed after it), so without an explicit reset they outlive
        /// the world they were measured against and get published, keyed by entity index, onto whatever
        /// occupies those indices next.
        /// </summary>
        private void ResetErrorCorrectionState()
        {
            ClearErrorDeltas();
            _pendingResyncPos.Clear();
            _pendingResyncYaw.Clear();
            _pendingResyncTeleported.Clear();
        }

        /// <summary>
        /// Records the rendered pose of every correction target so <see cref="ComputeErrorDeltas"/> can
        /// diff it against the post-rollback state.
        ///
        /// <paramref name="rollbackImminent"/> comes from the caller because every mode expresses
        /// "a rollback runs later in this frame" differently — the SD client by a verified batch waiting
        /// in <c>_pendingVerifiedQueue</c>, P2P by the deferred <c>_hasPendingRollback</c> flag. This
        /// used to read the SD queue directly, which silently disabled the whole pipeline in every other
        /// mode: P2P renders every view on the CSP path, the one path that applies these deltas, so
        /// EnableErrorCorrection was a knob that did nothing there.
        /// </summary>
        private void CapturePreRollbackTransforms(bool rollbackImminent)
        {
            // Skip computation in modes with no consumer for the result. Replay and the SD server have no
            // view render path at all; a spectator has views but every one of them is on the snapshot
            // path, which draws the authoritative state and deliberately discards these deltas. Capturing
            // for a spectator produced deltas that were parked by the resync path and then never
            // published or cleared, because the spectator update has no publish call.
            if (_isReplayMode || IsServer || _isSpectatorMode) return;
            if (!_simConfig.EnableErrorCorrection) return;
            if (!rollbackImminent) return;

            if (_simulation is not ECS.EcsSimulation ecsSim) return;
            var frame = ecsSim.Frame;
            var alpha = FP64.FromFloat(RenderClock.PredictedAlpha);

            // A capture is a fresh snapshot, never a merge onto whatever was here. Two captures can run in
            // one frame — the resync path captures from the FullState handler and the mode path captures
            // again after the tick loop — and without this the second one only overwrites the entities its
            // filter still matches, leaving the poses of anything that disappeared in between to be diffed
            // against a world they no longer belong to. Placed after the guards on purpose: a
            // call with rollbackImminent=false must not wipe a real capture taken earlier in the frame.
            _preRollbackPos.Clear();
            _preRollbackYaw.Clear();
            _preRollbackTeleportTick.Clear();

            var filter = frame.Filter<ECS.TransformComponent, ECS.ErrorCorrectionTargetComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var t = ref frame.GetReadOnly<ECS.TransformComponent>(entity);
                _preRollbackPos[entity.Index] = FPVector3.Lerp(t.PreviousPosition, t.Position, alpha);
                _preRollbackYaw[entity.Index] = LerpYaw(t.PreviousRotation, t.Rotation, alpha);
                _preRollbackTeleportTick[entity.Index] = t.TeleportTick;
            }

            // DEBUG is included so `dotnet test` covers this — the repository's convention for dev-only
            // guards (see the 0.9.1 Filter watch). The neighbouring [EC][DIAG] block below predates that
            // and stays Unity-only; widening it is not this change's business.
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            // Getting here means error correction is on and a rollback is happening, so if the filter
            // matched nothing then no entity carries ErrorCorrectionTargetComponent and no correction will
            // ever be computed. That is the misconfiguration itself, not an inference from it.
            //
            // The view used to be the one guessing: ErrorVisualState warns when its rollback delta stays
            // zero, which cannot distinguish "nobody wired the component" from "nothing needed correcting".
            // Steady input predicts perfectly — SimpleInputPredictor repeats the last continuous command —
            // so a delta-free stretch is normal, and that view-side check is additionally gated behind the
            // very flag it tells you to check. Here all three facts are local: the flag (checked above),
            // the rollback (the caller's argument), and whether any target exists (this filter).
            //
            // ClearErrorDeltas empties these dictionaries every frame, so Count is this frame's match
            // count and not an accumulation.
            //
            // ⚠️ One empty filter is NOT evidence, which the first version of this guard got wrong. The
            // frame read here is the PRE-rollback one, and an SD client predicts with UsePrediction=false:
            // it runs tick 0 as EmptyCommand and only learns about SpawnCharacterCommand from the very
            // rollback being captured here. So the first rollback of an SD session legitimately sees a
            // world with no characters in it, let alone correction targets — and a latched warning fired
            // there is wrong forever. Observed on both Brawler SD clients at tick 12, sixty-five log lines
            // before the first [EC][DIAG] proved the component was in fact present.
            //
            // Consecutive count instead: a real misconfiguration stays empty for every rollback there will
            // ever be, so it still gets reported; the startup transient clears in one or two.
            if (_preRollbackPos.Count > 0)
            {
                _zeroTargetRollbacks = 0;
            }
            else if (++_zeroTargetRollbacks >= NoCorrectionTargetWarnThreshold && !_warnedNoCorrectionTargets)
            {
                _warnedNoCorrectionTargets = true;
                _logger?.KWarning(
                    $"[EC] EnableErrorCorrection is on and the last {_zeroTargetRollbacks} rollbacks (through " +
                    $"tick {CurrentTick}) found no entity carrying ErrorCorrectionTargetComponent — no " +
                    $"correction will ever be computed. Add it to the entities whose views should smooth " +
                    $"rollback corrections.");
            }
#endif
        }

        /// <summary>
        /// Records the rendered pose before a FullState replaces it, so the view can smooth the jump.
        ///
        /// Whatever is in the delta maps at this point belongs to the previous frame and has already been
        /// read (the view reads in LateUpdate, the poll that lands here is the next frame's), and the
        /// frame's own update is about to clear it — so it is not saved.
        ///
        /// ⚠️ Two earlier versions of this got the surrounding frame wrong, in opposite directions. The
        /// first cleared the maps after measuring, on the theory that the handler runs ahead of everything
        /// — it does, but so does the clear. The second skipped whenever deltas were present, which in SD
        /// is nearly every tick, so it never fired at all (proved by a live SD run, 2026-08-28).
        /// </summary>
        private void CaptureResyncTransforms()
        {
            CapturePreRollbackTransforms(rollbackImminent: true);
        }

        /// <summary>
        /// Computes the resync's corrections and parks them for this frame's update to publish.
        ///
        /// Measured before promoting this from diagnostics: two live SD resyncs produced position jumps of
        /// 4.6 cm and 5.7 cm, both inside <c>[PosMinCorrection, ErrorVisualState.PosTeleportDistance)</c> —
        /// the band error correction exists for — with the second equal to the median of its own session's
        /// non-zero corrections. The original assumption, that a resync is "usually past the teleport
        /// threshold so snapping is the design", did not hold. And it cannot: the FullState restores the
        /// authoritative pose, so the jump is the prediction error at that instant, not a function of
        /// whatever caused the resync. Injecting a real component divergence (Scale x7) instead of a bare
        /// hash salt moved the number by 1 cm.
        ///
        /// Replaces rather than merges: the resync swapped the whole world, so a correction still pending
        /// for the old one describes something that no longer exists.
        /// </summary>
        private void ComputeResyncErrorDeltas(ApplyReason reason, int tick, FullStateApplyResult result)
        {
            if (_preRollbackPos.Count == 0) return;   // capture was gated off (EC disabled / server / replay)

            if (result == FullStateApplyResult.Skipped)
            {
                // Nothing was applied, so the captured poses describe no jump. Leave the published deltas
                // alone — the retreat guard rejecting a state must not cost the frame its smoothing.
                _preRollbackPos.Clear();
                _preRollbackYaw.Clear();
                _preRollbackTeleportTick.Clear();
                return;
            }

            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();
            ComputeErrorDeltas();

            MoveInto(_posDeltas, _pendingResyncPos);
            MoveInto(_yawDeltas, _pendingResyncYaw);
            _pendingResyncTeleported.Clear();
            foreach (int idx in _teleportedEntities) _pendingResyncTeleported.Add(idx);
            _teleportedEntities.Clear();

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            float peak = 0f;
            foreach (var kvp in _pendingResyncPos)
            {
                float mag = kvp.Value.magnitude.ToFloat();
                if (mag > peak) peak = mag;
                _logger?.KDebug($"[EC][RESYNC] entity={kvp.Key} posDelta={mag:F5}m");
            }
            _logger?.KInformation(
                $"[EC][RESYNC] reason={reason} tick={tick} captured={_preRollbackPos.Count} " +
                $"corrected={_pendingResyncPos.Count} teleported={_pendingResyncTeleported.Count} peak={peak:F5}m");
#endif
        }

        /// <summary>
        /// Hands a resync's parked corrections to the view. Called immediately after each per-frame
        /// <see cref="ClearErrorDeltas"/>, which is the only reason the parking exists.
        /// </summary>
        private void PublishPendingResyncDeltas()
        {
            if (_pendingResyncPos.Count == 0
                && _pendingResyncYaw.Count == 0
                && _pendingResyncTeleported.Count == 0) return;

            MoveInto(_pendingResyncPos, _posDeltas);
            MoveInto(_pendingResyncYaw, _yawDeltas);
            foreach (int idx in _pendingResyncTeleported) _teleportedEntities.Add(idx);
            _pendingResyncTeleported.Clear();
        }

        private static void MoveInto<TValue>(Dictionary<int, TValue> from, Dictionary<int, TValue> to)
        {
            to.Clear();
            foreach (var kvp in from) to[kvp.Key] = kvp.Value;
            from.Clear();
        }

        private void ComputeErrorDeltas()
        {
            // Same guard as CapturePreRollbackTransforms. Missing either side causes NullRef or incorrect deltas.
            if (_isReplayMode || IsServer) return;
            if (!_simConfig.EnableErrorCorrection) return;
            if (_preRollbackPos.Count == 0) return;
            if (_simulation is not ECS.EcsSimulation ecsSim) return;

            var frame = ecsSim.Frame;
            var alpha = FP64.FromFloat(RenderClock.PredictedAlpha);

            FP64 rotMin = FP64.FromFloat(_ecSettings.RotMinCorrectionDeg * 0.017453292f);

            var filter = frame.Filter<ECS.TransformComponent, ECS.ErrorCorrectionTargetComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var t = ref frame.GetReadOnly<ECS.TransformComponent>(entity);
                int idx = entity.Index;

                if (_preRollbackTeleportTick.TryGetValue(idx, out var preTeleport)
                    && t.TeleportTick != preTeleport && t.TeleportTick > 0)
                {
                    _teleportedEntities.Add(idx);
                    continue;
                }

                if (_preRollbackPos.TryGetValue(idx, out var oldPos))
                {
                    var newPos = FPVector3.Lerp(t.PreviousPosition, t.Position, alpha);
                    var delta = oldPos - newPos;
                    var deltaMag = delta.magnitude.ToFloat();
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.KDebug($"[EC][DIAG] entity={idx} posDelta={deltaMag:F5}m");
#endif
                    if (deltaMag >= _ecSettings.PosMinCorrection)
                    {
                        // Accumulate, do not replace. A frame can carry two discontinuities — a FullState
                        // resync published before the tick loop, then a rollback after it — and each delta
                        // measures its own jump between the same two alphas, so the motion BETWEEN them is
                        // in neither. Assigning kept only the last one, which threw away the resync
                        // correction exactly when a verified batch followed it, i.e. the normal case.
                        // The teleport set was already additive; this makes the value agree
                        // with the flag.
                        _posDeltas[idx] = _posDeltas.TryGetValue(idx, out var prevDelta)
                            ? prevDelta + delta
                            : delta;
                    }
                }

                if (_preRollbackYaw.TryGetValue(idx, out var oldYaw))
                {
                    var newYaw = LerpYaw(t.PreviousRotation, t.Rotation, alpha);
                    var yawDelta = WrapYaw(oldYaw - newYaw);
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.KDebug($"[EC][DIAG] entity={idx} yawDelta={FP64.Abs(yawDelta).ToFloat() * 57.29578f:F3}deg");
#endif
                    if (FP64.Abs(yawDelta) >= rotMin)
                    {
                        _yawDeltas[idx] = _yawDeltas.TryGetValue(idx, out var prevYawDelta)
                            ? prevYawDelta + yawDelta
                            : yawDelta;
                    }
                }
            }

            // Consume the baseline. This is what makes _preRollback* a strict single-use buffer — filled by
            // a capture, emptied by the compute that reads it, empty at every other moment.
            //
            // Load-bearing, not tidiness: the compute call is UNCONDITIONAL at both mode sites while the
            // capture beside it is gated on "a rollback runs this frame". So a frame where a resync filled
            // the maps but no rollback followed used to reach this method with the resync's baseline still
            // in place, and diff it against the post-tick state — publishing pre_resync - post_tick, which
            // folds a tick of legitimate motion into the correction the view then smooths away. Clearing at
            // the capture cannot cover it, because that path returns at the rollbackImminent guard before
            // reaching its own clear.
            _preRollbackPos.Clear();
            _preRollbackYaw.Clear();
            _preRollbackTeleportTick.Clear();
        }
    }
}
