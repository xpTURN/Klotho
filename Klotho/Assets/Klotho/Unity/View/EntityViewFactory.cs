using Cysharp.Threading.Tasks;
using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Abstract class responsible for view creation and BindBehaviour/ViewFlags determination.
    ///
    /// Initialization notes:
    /// - Do not query Engine information such as LocalPlayerId or IsServer in constructors/Awake/OnEnable. The engine may not be initialized yet.
    /// - Engine queries are only permitted inside TryGetBindBehaviour / GetViewFlags / CreateAsync. These methods are only called from the EVU.Reconcile path, which guarantees the engine is ready.
    /// </summary>
    public abstract class EntityViewFactory : ScriptableObject
    {
        /// <summary>Injected by EVU at Initialize time.</summary>
        public IKlothoEngine Engine { get; private set; }

        /// <summary>
        /// View pool placed in the scene. Injected by EVU and used by subclass CreateAsync implementations.
        /// If null, the subclass calls Object.Instantiate directly without using a pool.
        /// </summary>
        public IEntityViewPool Pool { get; private set; }

        internal void Attach(IKlothoEngine engine, IEntityViewPool pool)
        {
            Engine = engine;
            Pool   = pool;
        }

        /// <summary>
        /// Determines whether this entity should be rendered as a View and which BindBehaviour to use.
        /// Returning false skips view creation entirely (e.g. for UI-less entities).
        /// </summary>
        public abstract bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour);

        /// <summary>Computes per-entity ViewFlags (e.g. Snapshot Interpolation ON/OFF).</summary>
        public abstract ViewFlags GetViewFlags(Frame frame, EntityRef entity);

        /// <summary>
        /// Instantiates a prefab. Pool-based implementations should override this in a subclass.
        /// Returning null causes EVU to discard the spawn.
        /// </summary>
        public abstract UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags);

        /// <summary>Destroys or returns the view to the pool. Default implementation calls Destroy.</summary>
        public virtual void Destroy(EntityView view)
        {
            if (view != null) Object.Destroy(view.gameObject);
        }
    }
}
