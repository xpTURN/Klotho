using System.Collections.Generic;

using ZLogger;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        private ErrorCorrectionSettings _ecSettings = ErrorCorrectionSettings.Default;

        private readonly Dictionary<int, FPVector3> _posDeltas = new();
        private readonly Dictionary<int, FP64> _yawDeltas = new();
        private readonly HashSet<int> _teleportedEntities = new();

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

        private void ClearErrorDeltas()
        {
            _posDeltas.Clear();
            _yawDeltas.Clear();
            _teleportedEntities.Clear();
            _preRollbackPos.Clear();
            _preRollbackYaw.Clear();
            _preRollbackTeleportTick.Clear();
        }

        private void CapturePreRollbackTransforms()
        {
            // Skip computation in modes without a view render path (Replay/Spectator/SD-Server).
            if (_isReplayMode || _isSpectatorMode || IsServer) return;
            if (!_simConfig.EnableErrorCorrection) return;
            if (_pendingVerifiedQueue.Count == 0) return;

            if (_simulation is not ECS.EcsSimulation ecsSim) return;
            var frame = ecsSim.Frame;
            var alpha = FP64.FromFloat(RenderClock.PredictedAlpha);

            var filter = frame.Filter<ECS.TransformComponent, ECS.ErrorCorrectionTargetComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var t = ref frame.GetReadOnly<ECS.TransformComponent>(entity);
                _preRollbackPos[entity.Index] = FPVector3.Lerp(t.PreviousPosition, t.Position, alpha);
                _preRollbackYaw[entity.Index] = FP64.Lerp(t.PreviousRotation, t.Rotation, alpha);
                _preRollbackTeleportTick[entity.Index] = t.TeleportTick;
            }
        }

        private void ComputeErrorDeltas()
        {
            // Same guard as CapturePreRollbackTransforms. Missing either side causes NullRef or incorrect deltas.
            if (_isReplayMode || _isSpectatorMode || IsServer) return;
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
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.ZLogDebug($"[EC][DIAG] entity={idx} posDelta={deltaMag:F5}m");
#endif
                    if (deltaMag >= _ecSettings.PosMinCorrection)
                    {
                        _posDeltas[idx] = delta;
                    }
                }

                if (_preRollbackYaw.TryGetValue(idx, out var oldYaw))
                {
                    var newYaw = FP64.Lerp(t.PreviousRotation, t.Rotation, alpha);
                    var yawDelta = oldYaw - newYaw;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    _logger?.ZLogDebug($"[EC][DIAG] entity={idx} yawDelta={FP64.Abs(yawDelta).ToFloat() * 57.29578f:F3}deg");
#endif
                    if (FP64.Abs(yawDelta) >= rotMin)
                    {
                        _yawDeltas[idx] = yawDelta;
                    }
                }
            }
        }
    }
}
