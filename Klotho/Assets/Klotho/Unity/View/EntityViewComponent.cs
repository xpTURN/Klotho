using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Base component attached to an EntityView. EntityView collects these from its children and injects the parent via BindTo.
    ///
    /// Lifecycle call order:
    ///   1. OnInitialize()    — once at first creation
    ///   2. OnActivate(frame) — just before the first OnUpdateView (every time on pool reuse)
    ///   3. OnUpdateView()    — every tick
    ///   4. OnLateUpdateView()— every frame (Unity LateUpdate)
    ///   5. OnDeactivate()    — just before removal/pool return
    /// </summary>
    public abstract class EntityViewComponent : MonoBehaviour
    {
        public EntityView ParentView { get; private set; }

        protected EntityRef       EntityRef => ParentView.EntityRef;
        protected IKlothoEngine Engine    => ParentView.Engine;

        // Convenience properties for commonly used frame references.
        public FrameRef VerifiedFrame               => Engine.VerifiedFrame;
        public FrameRef PredictedFrame              => Engine.PredictedFrame;
        public FrameRef PredictedPreviousFrame      => Engine.PredictedPreviousFrame;
        public FrameRef PreviousUpdatePredictedFrame => Engine.PreviousUpdatePredictedFrame;

        public virtual void OnInitialize() { }
        public virtual void OnActivate(FrameRef frame) { }
        public virtual void OnUpdateView() { }
        public virtual void OnLateUpdateView() { }
        public virtual void OnDeactivate() { }

        internal void BindTo(EntityView parent) => ParentView = parent;
    }

    /// <summary>
    /// Variant that accepts an external context type.
    /// TContext is used to inject an object shared across multiple components, such as runtime state or a game-side controller.
    /// </summary>
    public abstract class EntityViewComponent<TContext> : EntityViewComponent where TContext : class
    {
        public TContext Context { get; private set; }

        internal void SetContext(TContext context) => Context = context;
    }
}
