using UnityEngine;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// Syncs moving platform ECS state → Unity Transform.
    /// External code such as BrawlerGameController specifies the entity to track via Assign(EntityRef).
    /// </summary>
    public class PlatformView : EntityView
    {
        private EntityRef _entity = EntityRef.None;
        private EcsSimulation _simulation;
        private IKlothoEngine _engine;

        public EntityRef Entity => _entity;
        public bool IsAssigned => _entity.IsValid;

        public void Initialize(EcsSimulation simulation, IKlothoEngine engine)
        {
            _simulation = simulation;
            _engine = engine;
        }

        /// <summary>
        /// Specifies the platform entity that this view will track.
        /// </summary>
        public void Assign(EntityRef entity)
        {
            _entity = entity;
        }

        private void LateUpdate()
        {
            if (!_entity.IsValid) return;
            if (_simulation == null) return;

            var frame = _simulation.Frame;

            if (!frame.Has<TransformComponent>(_entity)) return;

            ref readonly var t = ref frame.GetReadOnly<TransformComponent>(_entity);
            float alpha = _engine.RenderClock.PredictedAlpha;

            transform.position = new Vector3(
                Mathf.Lerp(t.PreviousPosition.x.ToFloat(), t.Position.x.ToFloat(), alpha),
                Mathf.Lerp(t.PreviousPosition.y.ToFloat(), t.Position.y.ToFloat(), alpha),
                Mathf.Lerp(t.PreviousPosition.z.ToFloat(), t.Position.z.ToFloat(), alpha));
        }
    }
}
