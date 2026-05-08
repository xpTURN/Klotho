using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Base class for the view corresponding to an entity. EVU manages the full lifecycle of creation, destruction, and tick updates.
    /// InternalUpdateView interpolates the frame transform, runs the ErrorVisualState pipeline,
    /// and applies the result to the actual transform via ApplyTransform.
    /// </summary>
    public abstract class EntityView : MonoBehaviour
    {
        [SerializeField] protected BindBehaviour _bindBehaviour = BindBehaviour.Verified;
        [SerializeField] protected ViewFlags     _viewFlags     = ViewFlags.None;

        /// <summary>Child transform to which interpolation results are applied. If null, the root transform is the interpolation target.</summary>
        [SerializeField] protected Transform     _interpolationTarget;

        /// <summary>View-side error visual pipeline. Holds per-view tuning parameters and state together.</summary>
        [SerializeField] protected ErrorVisualState _errorVisual = ErrorVisualState.Default;

        /// <summary>
        /// Set once by EVU at spawn time. Subclasses created through non-standard paths (directly via Registry) may also inject this manually.
        /// </summary>
        public EntityRef EntityRef { get; set; }

        /// <summary>
        /// Set once by EVU at spawn time. A public setter is provided for compatibility with non-standard creation paths.
        /// </summary>
        public IKlothoEngine Engine { get; set; }

        public BindBehaviour BindBehaviour => _bindBehaviour;
        public ViewFlags     ViewFlags     => _viewFlags;

        internal void SetBindBehaviour(BindBehaviour value) => _bindBehaviour = value;
        internal void SetViewFlags(ViewFlags value)         => _viewFlags = value;

        /// <summary>
        /// Returns true if this view's cached owner identity matches the given OwnerId.
        /// Override required for any view bound to an entity that has OwnerComponent — without override,
        /// the default returns false to make missing overrides fail loudly (EVU will Rebind every Reconcile,
        /// surfacing as continuous churn in profiler / [ViewLife][Rebind] logs). This prevents silent
        /// regression of the entity-slot reuse with owner swap bug when new Owner-bearing entity types are added.
        ///
        /// EVU's helper short-circuits with `return true` for entities lacking OwnerComponent, so this
        /// virtual method is only invoked for Owner-bearing entities — Owner-agnostic view types do not
        /// need to override.
        /// </summary>
        public virtual bool OwnerMatches(int ownerId) => false;

        // Array of child EntityViewComponents. Collected once in Awake; lifecycle callbacks are forwarded to each component.
        private EntityViewComponent[] _components;

        // Flag that prevents OnInitialize from being called more than once on pool reuse.
        // The value is preserved on Pool Return, so EVU.EnsureInitialized becomes a no-op on re-rent.
        private bool _hasInitialized;

        protected virtual void Awake()
        {
            _components = GetComponentsInChildren<EntityViewComponent>(includeInactive: false);
            for (int i = 0; i < _components.Length; i++)
                _components[i].BindTo(this);
        }

        /// <summary>
        /// Called by EVU at spawn time. Runs OnInitialize only once. No-op on pool reuse.
        /// </summary>
        internal void EnsureInitialized()
        {
            if (_hasInitialized) return;
            _hasInitialized = true;
            OnInitialize();
        }

        /// <summary>
        /// Called by EVU at spawn and on every re-rent. Clears any accumulated ErrorVisualState residue on pool reuse, then calls OnActivate.
        /// </summary>
        internal void InternalActivate(FrameRef frame)
        {
            _errorVisual.Reset();
            OnActivate(frame);
        }

        // ── Lifecycle callbacks ──
        // OnInitialize → OnActivate → OnUpdateView → (LateUpdate) → OnDeactivate

        /// <summary>Called once at first creation. Not called on pool reuse.</summary>
        public virtual void OnInitialize()
        {
            if (_components == null) return;
            for (int i = 0; i < _components.Length; i++) _components[i].OnInitialize();
        }

        /// <summary>Called at creation and on every pool reuse. <paramref name="frame"/> is the frame at which the spawn was decided.</summary>
        public virtual void OnActivate(FrameRef frame)
        {
            if (_components == null) return;
            for (int i = 0; i < _components.Length; i++) _components[i].OnActivate(frame);
        }

        /// <summary>Called every tick (EVU.OnTickExecuted).</summary>
        public virtual void OnUpdateView()
        {
            if (_components == null) return;
            for (int i = 0; i < _components.Length; i++) _components[i].OnUpdateView();
        }

        /// <summary>Called every frame (EVU.LateUpdate).</summary>
        public virtual void OnLateUpdateView()
        {
            if (_components == null) return;
            for (int i = 0; i < _components.Length; i++) _components[i].OnLateUpdateView();
        }

        /// <summary>Called just before destruction or pool return.</summary>
        public virtual void OnDeactivate()
        {
            if (_components == null) return;
            for (int i = 0; i < _components.Length; i++) _components[i].OnDeactivate();
        }

        /// <summary>
        /// Applies the error visual pipeline result to the transform.
        /// When _interpolationTarget is set, the root keeps the tick-accurate Uninterpolated value
        /// while interpolation and error visual are handled by the child transform. This preserves determinism
        /// for root-based collision/raycasts while allowing only the child mesh/VFX to move smoothly.
        /// </summary>
        protected virtual void ApplyTransform(ref UpdatePositionParameter param)
        {
            bool skipPosition = (_viewFlags & ViewFlags.DisablePositionUpdate) != 0;

            // Teleport intent → bypass interpolation / error visual and snap directly.
            if (param.Teleported)
            {
                if (!skipPosition) transform.position = param.UninterpolatedPosition;
                transform.rotation = param.UninterpolatedRotation;
                if (_interpolationTarget != null)
                {
                    // Reset child offset to origin (clear residual error visual / interpolation offset).
                    _interpolationTarget.localPosition = Vector3.zero;
                    _interpolationTarget.localRotation = Quaternion.identity;
                }
                return;
            }

            if (_interpolationTarget != null)
            {
                // Root is tick-accurate. Used as the reference for collision/raycasts.
                if (!skipPosition) transform.position = param.UninterpolatedPosition;
                transform.rotation = param.UninterpolatedRotation;

                // Apply interpolated render position and error visual on the child.
                if (!skipPosition)
                    _interpolationTarget.localPosition = (param.NewPosition + param.ErrorVisualVector) - transform.position;
                _interpolationTarget.localRotation = Quaternion.Inverse(transform.rotation)
                                                   * (param.ErrorVisualQuaternion * param.NewRotation);
            }
            else
            {
                // If no _interpolationTarget, interpolate the root transform directly.
                if (!skipPosition) transform.position = param.NewPosition + param.ErrorVisualVector;
                transform.rotation = param.ErrorVisualQuaternion * param.NewRotation;
            }
        }

        // ── Internal entry points called by EVU ──
        // Executed in order: frame transform interpolation → ErrorVisualState pipeline → ApplyTransform → OnUpdateView.

        internal virtual void InternalUpdateView()
        {
            if ((_viewFlags & ViewFlags.DisableUpdate) != 0) return;
            if (Engine == null || !EntityRef.IsValid) return;

            // Frame may be null immediately after Late Join, FullState restore, or ring warmup — guard against it.
            var predictedRef = Engine.PredictedFrame;
            var prevRef      = Engine.PredictedPreviousFrame;

            var predicted = predictedRef.Frame;
            if (predicted == null) return;

            // The entity may have been destroyed during rollback, so check for TransformComponent ownership first.
            if (!predicted.Has<TransformComponent>(EntityRef)) return;

            ref readonly var curr = ref predicted.GetReadOnly<TransformComponent>(EntityRef);

            // ── CSP lerp (PredictedPrevious ↔ Predicted) ──
            float alpha = Engine.RenderClock.PredictedAlpha;
            Vector3    currPos = ToVector3(curr.Position);
            Quaternion currRot = Quaternion.Euler(0f, (float)curr.Rotation.ToFloat() * Mathf.Rad2Deg, 0f);

            Vector3    newPos = currPos;
            Quaternion newRot = currRot;

            if ((_viewFlags & ViewFlags.EnableSnapshotInterpolation) != 0)
            {
                // Interpolate between two adjacent Verified frames. When no frame is available, the current Predicted value is used as a fallback.
                newPos = VerifiedFrameInterpolator.InterpolatePosition(EntityRef, Engine, currPos);
                newRot = VerifiedFrameInterpolator.InterpolateRotation(EntityRef, Engine, currRot);
            }
            else
            {
                // Default interpolation path. Lerps between PredictedPrevious and Predicted by alpha.
                var prev = prevRef.Frame;
                if (prev != null && prev.Has<TransformComponent>(EntityRef))
                {
                    ref readonly var prevT = ref prev.GetReadOnly<TransformComponent>(EntityRef);
                    Vector3 prevPos = ToVector3(prevT.Position);
                    float   prevYaw = (float)prevT.Rotation.ToFloat() * Mathf.Rad2Deg;
                    float   currYaw = (float)curr.Rotation.ToFloat() * Mathf.Rad2Deg;

                    newPos = Vector3.Lerp(prevPos, currPos, alpha);
                    newRot = Quaternion.Euler(0f, Mathf.LerpAngle(prevYaw, currYaw, alpha), 0f);
                }
            }

            // Here we only read the smoothed offset; ErrorVisualState.Tick is performed in InternalLateUpdateView.
            // If Tick is called per-tick, it accumulates redundantly with the same deltaTime,
            // and forward motion is mistaken for rollback, causing accumulated error to be emitted on rotation/direction changes and making the motion stutter.
            bool teleported = Engine.HasEntityTeleported(EntityRef.Index);

            // ── Populate UpdatePositionParameter ──
            var param = new UpdatePositionParameter
            {
                NewPosition             = newPos,
                NewRotation             = newRot,
                UninterpolatedPosition  = currPos,
                UninterpolatedRotation  = currRot,
                ErrorVisualVector       = (_viewFlags & ViewFlags.DisablePositionUpdate) != 0
                                            ? Vector3.zero : _errorVisual.SmoothedPosError,
                ErrorVisualQuaternion   = Quaternion.Euler(0f, _errorVisual.SmoothedYawError * Mathf.Rad2Deg, 0f),
                DeltaTime               = Time.deltaTime,
                Teleported              = teleported,
            };

            ApplyTransform(ref param);
            OnUpdateView();
        }

        internal virtual void InternalLateUpdateView()
        {
            if ((_viewFlags & ViewFlags.DisableUpdate) != 0) return;
            if (Engine != null && EntityRef.IsValid)
            {
                // Refresh the error visual once per frame. The engine exposes only the delta caused by rollback
                // and excludes forward motion, so accumulation/decay/interpolation is performed only once at LateUpdate
                // to avoid redundant accumulation even when multiple ticks run in the same frame.
                var (dx, dy, dz) = Engine.GetPositionDelta(EntityRef.Index);
                Vector3 rollbackDelta   = new Vector3(dx, dy, dz);
                float   rollbackYawDelta = Engine.GetYawDelta(EntityRef.Index);
                bool    teleported       = Engine.HasEntityTeleported(EntityRef.Index);

                _errorVisual.Tick(
                    rollbackDelta, rollbackYawDelta, Time.deltaTime,
                    teleported, Engine.Logger, EntityRef.Index);
            }
            OnLateUpdateView();
        }

        private static Vector3 ToVector3(in xpTURN.Klotho.Deterministic.Math.FPVector3 v)
            => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
    }
}
