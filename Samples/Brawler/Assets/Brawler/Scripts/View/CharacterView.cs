using UnityEngine;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// Syncs character ECS state → Unity Transform. EVU manages spawn and lifecycle.
    /// Transform interpolation + <see cref="EntityView._errorVisual"/> offset are handled by the base path
    /// (frame-time ApplyTransform in InternalLateUpdateView). This class only caches per-tick character
    /// state and toggles Renderer/Shield/Boost visual feedback via <see cref="OnUpdateView"/>.
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

        // Rollback view re-bind diagnostic counters.
        private int _activateCount;
        private int _deactivateCount;

        public override void OnActivate(FrameRef frame)
        {
            base.OnActivate(frame);
            _wasDead = false;

            // Resolve playerId via OwnerComponent (overrides prefab default _playerId).
            // The game-facing _playerId is kept in sync with EVU's cached owner — both read
            // the same OwnerComponent from the spawn-decision frame, so they always agree.
            var f = frame.Frame;
            if (f != null && f.Has<OwnerComponent>(EntityRef))
                _playerId = f.GetReadOnly<OwnerComponent>(EntityRef).OwnerId;

            _activateCount++;
            Engine?.Logger?.KDebug($"[ViewLife][Activate] playerId={_playerId}, entity={EntityRef.Index}, viewIID={UnityObjectId.Of(this)}, activateCount={_activateCount}");
        }

        public override void OnDeactivate()
        {
            base.OnDeactivate();
            _deactivateCount++;
            Engine?.Logger?.KDebug($"[ViewLife][Deactivate] playerId={_playerId}, entity={EntityRef.Index}, viewIID={UnityObjectId.Of(this)}, deactivateCount={_deactivateCount}");
        }

        public override void OnUpdateView()
        {
            base.OnUpdateView();

            var frame = Engine.PredictedFrame.Frame;
            if (frame == null || !frame.Has<CharacterComponent>(EntityRef)) return;

            ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(EntityRef);

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

            if (frame.Has<SkillCooldownComponent>(EntityRef))
            {
                ref readonly var cd = ref frame.GetReadOnly<SkillCooldownComponent>(EntityRef);
                if (_shieldEffect != null) _shieldEffect.SetActive(cd.ShieldTicks > 0);
                if (_boostEffect  != null) _boostEffect.SetActive(cd.BoostTicks > 0);
            }
        }
    }
}
