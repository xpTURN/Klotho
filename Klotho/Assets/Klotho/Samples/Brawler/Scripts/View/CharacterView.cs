using UnityEngine;
using ZLogger;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// Syncs character ECS state → Unity Transform. EVU manages spawn and lifecycle.
    /// Performs transform interpolation + <see cref="EntityView._errorVisual"/> offset application +
    /// Renderer/Shield/Boost visual feedback in its own <see cref="LateUpdate"/>. The base <see cref="EntityView.ApplyTransform"/> sets
    /// Visuals.localPosition with tick-time alpha, which conflicts with LateUpdate, hence no-op override.
    /// </summary>
    public class CharacterView : EntityView
    {
        [SerializeField] private int _playerId;
        [SerializeField] private Renderer[] _renderer;
        [SerializeField] private GameObject _shieldEffect;
        [SerializeField] private GameObject _boostEffect;

        public int PlayerId => _playerId;

        public override bool OwnerMatches(int ownerId) => _playerId == ownerId;

        // Latest ECS state cache read by external code (GameHUD, etc.)
        public int KnockbackPower { get; private set; }
        public int StockCount     { get; private set; }
        public bool IsDead        { get; private set; }

        // For ECS query from Editor Inspector
        internal EntityRef CachedEntity => EntityRef;

        private bool _wasDead;
        private BrawlerViewSync _viewSync;

        // Rollback view re-bind diagnostic counters.
        private int _activateCount;
        private int _deactivateCount;

        public override void OnActivate(FrameRef frame)
        {
            base.OnActivate(frame);
            _wasDead = false;

            // Resolve playerId via OwnerComponent (overrides prefab default _playerId).
            var f = frame.Frame;
            if (f != null && f.Has<OwnerComponent>(EntityRef))
                _playerId = f.GetReadOnly<OwnerComponent>(EntityRef).OwnerId;

            _activateCount++;
            Engine?.Logger?.ZLogDebug($"[ViewLife][Activate] playerId={_playerId}, entity={EntityRef.Index}, viewIID={GetInstanceID()}, activateCount={_activateCount}");

            // Wire camera follow / GameHUD.
            if (_viewSync == null)
                _viewSync = FindFirstObjectByType<BrawlerViewSync>();
            _viewSync?.RegisterCharacter(_playerId, this);
        }

        public override void OnDeactivate()
        {
            base.OnDeactivate();
            _deactivateCount++;
            Engine?.Logger?.ZLogDebug($"[ViewLife][Deactivate] playerId={_playerId}, entity={EntityRef.Index}, viewIID={GetInstanceID()}, deactivateCount={_deactivateCount}");
            _viewSync?.UnregisterCharacter(_playerId, this);
        }

        /// <summary>
        /// Skips the entire base transform / InterpolationTarget logic — <see cref="LateUpdate"/> handles it solely.
        /// The base <see cref="EntityView.ApplyTransform"/> sets Visuals.localPosition with tick-time alpha, which
        /// conflicts with LateUpdate's root overwrite → causes jitter. The Tick of <see cref="EntityView._errorVisual"/> continues
        /// to be called inside the base <see cref="EntityView.InternalUpdateView"/>, so the smoothed offset remains valid.
        /// </summary>
        protected override void ApplyTransform(ref UpdatePositionParameter param) { }

        private void LateUpdate()
        {
            if (Engine == null || !EntityRef.IsValid) return;

            var frame = Engine.PredictedFrame.Frame;
            if (frame == null) return;
            if (!frame.Has<CharacterComponent>(EntityRef)) return;

            SyncFromEntity(ref frame, EntityRef);
        }

        private void SyncFromEntity(ref Frame frame, EntityRef entity)
        {
            ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);

            KnockbackPower = c.KnockbackPower;
            StockCount     = c.StockCount;
            IsDead         = c.IsDead;

            if (c.IsDead)
            {
                _wasDead = true;
                if (_renderer != null)
                    foreach (var r in _renderer) if (r != null && r.enabled) r.enabled = false;
                return;
            }

            if (_renderer != null)
                foreach (var r in _renderer) if (r != null && !r.enabled) r.enabled = true;

            if (_wasDead)
            {
                _wasDead = false;
                _errorVisual.Reset();
            }

            ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);
            Vector3    currPos = new Vector3(t.Position.x.ToFloat(), t.Position.y.ToFloat(), t.Position.z.ToFloat());
            Quaternion currRot = Quaternion.Euler(0f, t.Rotation.ToFloat() * Mathf.Rad2Deg, 0f);

            if ((_viewFlags & ViewFlags.EnableSnapshotInterpolation) != 0)
            {
                // Remote character (SD-Client / Spectator) — VerifiedFrame(n) ↔ VerifiedFrame(n+1) snapshot interpolation.
                // Renders the already-authoritative verified state, so _errorVisual offset (based on rollback delta) is not applied —
                // applying it would duplicate the same rollback correction, introducing unnecessary jitter into yaw/position.
                transform.position = VerifiedFrameInterpolator.InterpolatePosition(EntityRef, Engine, currPos);
                transform.rotation = VerifiedFrameInterpolator.InterpolateRotation(EntityRef, Engine, currRot);
            }
            else
            {
                // Local character — CSP lerp (PredictedPrevious ↔ Predicted) + _errorVisual offset (rollback visual correction).
                float alpha = Engine.RenderClock.PredictedAlpha;
                float px = Mathf.Lerp(t.PreviousPosition.x.ToFloat(), t.Position.x.ToFloat(), alpha);
                float py = Mathf.Lerp(t.PreviousPosition.y.ToFloat(), t.Position.y.ToFloat(), alpha);
                float pz = Mathf.Lerp(t.PreviousPosition.z.ToFloat(), t.Position.z.ToFloat(), alpha);
                float prevYawDeg = t.PreviousRotation.ToFloat() * Mathf.Rad2Deg;
                float currYawDeg = t.Rotation.ToFloat() * Mathf.Rad2Deg;
                Vector3 renderPos = new Vector3(px, py, pz);
                float yawDeg = Mathf.LerpAngle(prevYawDeg, currYawDeg, alpha);

                transform.position = renderPos + _errorVisual.SmoothedPosError;
                transform.rotation = Quaternion.Euler(0f, yawDeg + _errorVisual.SmoothedYawError * Mathf.Rad2Deg, 0f);
            }

            if (frame.Has<SkillCooldownComponent>(entity))
            {
                ref readonly var cd = ref frame.GetReadOnly<SkillCooldownComponent>(entity);
                if (_shieldEffect != null) _shieldEffect.SetActive(cd.ShieldTicks > 0);
                if (_boostEffect  != null) _boostEffect.SetActive(cd.BoostTicks > 0);
            }
        }
    }
}
