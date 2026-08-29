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

        /// <summary>
        /// Transform the interpolation result is applied to. If null, the root transform is the
        /// interpolation target. Any descendant works: the result is written through the world-space
        /// setters, so neither a direct-child relationship nor unit scale is assumed. The root keeps the
        /// tick-accurate transform meanwhile, which is the point of the split — collision and raycasts
        /// read a deterministic position while only the mesh moves smoothly. Note that the two are
        /// therefore apart by the render delay, which on the snapshot path is the SD lead plus
        /// InterpolationDelayTicks — target the mesh, not the root, when picking what the player sees.
        /// </summary>
        [SerializeField] protected Transform     _interpolationTarget;

        // The interpolation child's authored local pose, captured in EnsureInitialized. Only observable
        // on views whose position line never runs (DisableUpdate / DisablePositionUpdate) — every other
        // view has its child written in world space each frame, so authoring there does not survive the
        // first ApplyTransform either way (IMP105 C-9).
        private Vector3    _authoredTargetLocalPosition = Vector3.zero;
        private Quaternion _authoredTargetLocalRotation = Quaternion.identity;

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

        // PlayerViewRegistry unbind key — written by EVU at spawn time via SetCachedOwner.
        // _hasCachedOwner is an explicit flag because OwnerId valid range is not guaranteed
        // to be non-negative (avoids sentinel-collision with valid id 0 / negative bot ids).
        private int  _cachedOwnerId;
        private bool _hasCachedOwner;

        internal bool TryGetCachedOwner(out int ownerId)
        {
            ownerId = _cachedOwnerId;
            return _hasCachedOwner;
        }

        internal void SetCachedOwner(int ownerId)
        {
            _cachedOwnerId  = ownerId;
            _hasCachedOwner = true;
        }

        internal void ClearCachedOwner()
        {
            _cachedOwnerId  = 0;
            _hasCachedOwner = false;
        }

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

            // Remember what the prefab (or the scene, for an adopted view) authored on the interpolation
            // child, before anything has written to it. ResetInterpolationTarget restores THIS instead of
            // zero: zero is right for pool residue and wrong for authoring, and the two are the same
            // field. Captured once per instance — a pooled re-rent skips EnsureInitialized, which is
            // exactly what we want, since by then the value has been overwritten (IMP105 C-9).
            if (_interpolationTarget != null)
            {
                _authoredTargetLocalPosition = _interpolationTarget.localPosition;
                _authoredTargetLocalRotation = _interpolationTarget.localRotation;
            }

            OnInitialize();
        }

        /// <summary>
        /// Called by EVU at spawn and on every re-rent. Clears any accumulated ErrorVisualState residue on pool reuse, then calls OnActivate.
        /// </summary>
        internal void InternalActivate(FrameRef frame)
        {
            _errorVisual.Reset();
            ResetInterpolationTarget();
            OnActivate(frame);
        }

        /// <summary>
        /// Puts the interpolation child back on the root.
        ///
        /// The child carries the interpolation + error-visual offset, which is per-life state in exactly
        /// the way <see cref="ErrorVisualState"/> is — and nothing else clears it, because the pool only
        /// toggles <c>SetActive</c> and local transforms survive that. A re-rented view would otherwise
        /// come back wearing its previous occupant's offset until the first ApplyTransform, which is a
        /// visible frame when the spawn lands after LateUpdate, and is permanent when
        /// <see cref="ViewFlags.DisablePositionUpdate"/> keeps the position line from ever running.
        /// Teleport resets for the same reason, so both paths share this.
        ///
        /// Restores the AUTHORED local pose, not zero. The same flags that make the residue permanent
        /// make the authoring permanent too — that is the only case where either is observable — so
        /// zeroing here destroyed a prefab-authored child offset for exactly the views that could never
        /// get it back (IMP105 C-9).
        /// </summary>
        private void ResetInterpolationTarget()
        {
            if (_interpolationTarget == null) return;
            _interpolationTarget.localPosition = _authoredTargetLocalPosition;
            _interpolationTarget.localRotation = _authoredTargetLocalRotation;
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
            //
            // CSP path only. The flag fires on the frame the verified batch carrying the teleport is
            // applied, and on the snapshot path the rendered basis is still the verified window behind
            // that tick — so snapping there would throw the view forward by the entire render delay
            // (SD lead + InterpolationDelayTicks) and drop it back on the very next frame. The snapshot
            // path wants the discontinuity where its own timeline reaches the teleport instead, which is
            // the interpolation boundary, and VerifiedFrameInterpolator puts it there by refusing to lerp
            // across a TeleportTick change. Letting this branch run for it would add two pops around the
            // one jump that is actually correct.
            bool snapshotInterpolated = (_viewFlags & ViewFlags.EnableSnapshotInterpolation) != 0;
            if (param.Teleported && !snapshotInterpolated)
            {
                if (!skipPosition) transform.position = param.UninterpolatedPosition;
                transform.rotation = param.UninterpolatedRotation;
                ResetInterpolationTarget();
                return;
            }

            if (_interpolationTarget != null)
            {
                // Root is tick-accurate. Used as the reference for collision/raycasts.
                if (!skipPosition) transform.position = param.UninterpolatedPosition;
                transform.rotation = param.UninterpolatedRotation;

                // Apply interpolated render position and error visual on the child, through the WORLD
                // setters. Every value here is world-space by construction (New* comes from the frame,
                // ErrorVisual* from the engine's rollback delta), so reaching the child through the local
                // setters means converting into the child's PARENT space first. Writing world values
                // directly does that conversion correctly for any descendant depth and any parent scale,
                // which is why it is preferred over converting by hand: the hand conversion has to assume
                // the target is a direct child of an unscaled root, and nothing here enforces that.
                if (!skipPosition)
                    _interpolationTarget.position = param.NewPosition + param.ErrorVisualVector;
                _interpolationTarget.rotation = param.ErrorVisualQuaternion * param.NewRotation;
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
            OnUpdateView();
        }

        internal virtual void InternalLateUpdateView()
        {
            if ((_viewFlags & ViewFlags.DisableUpdate) != 0) return;
            if (Engine == null || !EntityRef.IsValid) return;

            // Frame may be null immediately after Late Join, FullState restore, or ring warmup — guard against it.
            var predictedRef = Engine.PredictedFrame;
            var prevRef      = Engine.PredictedPreviousFrame;

            var predicted = predictedRef.Frame;
            if (predicted == null) return;

            // Whether the entity is alive on the LIVE Predicted frame. IsAlive comes FIRST and the order is
            // load-bearing: Has forwards Index alone, so once the slot is recycled it answers about the
            // new occupant and this view would render a stranger's transform. That is the live-frame
            // mirror of what VerifiedFrameInterpolator guards on past frames — same defect, opposite
            // direction (there a new view reads the old entity; here a stale view reads the new one).
            //
            // This used to be an early return for BOTH paths. It still is for CSP, whose whole render
            // basis is this frame — but the snapshot path renders the Verified timeline, where the entity
            // can be perfectly alive while prediction has already destroyed it. Gating that path here
            // froze such a view for the entire SD lead (IMP103 D-2). Demoting the check from a gate to a
            // fallback condition is NOT the same as dropping it: the snapshot path still may not read
            // this frame's transform without it.
            bool predictedAlive = predicted.Entities.IsAlive(EntityRef)
                               && predicted.Has<TransformComponent>(EntityRef);

            bool snapshotPath = (_viewFlags & ViewFlags.EnableSnapshotInterpolation) != 0;

            Vector3    newPos, currPos;
            Quaternion newRot, currRot;
            bool       teleported;

            if (snapshotPath)
            {
                // Interpolate between two adjacent Verified frames. One query answers pose, occupancy and
                // teleport-in-window; see VerifiedFrameInterpolator.SnapshotPose.
                var pose = VerifiedFrameInterpolator.GetSnapshotPose(EntityRef, Engine);
                if (pose.Occupied)
                {
                    newPos = pose.Position;
                    newRot = pose.Rotation;

                    // Uninterpolated* is the render window's alpha=0 endpoint, not the live Predicted pose.
                    // For a verified-rendered entity the live pose is a prediction — the one thing this
                    // path exists to avoid — and it is the root, documented as the reference for
                    // collision/raycasts, that used to receive it (IMP103 D-2).
                    currPos = pose.BasePosition;
                    currRot = pose.BaseRotation;

                    // The snapshot path's teleport is the interpolator's, not TeleportTick-vs-CurrentTick:
                    // its timeline reaches the teleport when the window does (IMP103 V-5).
                    teleported = pose.Teleported;
                }
                else if (TryGetLatestVerifiedPose(out Vector3 vPos, out Quaternion vRot))
                {
                    // The window holds nothing for this entity, but the newest Verified frame does. That is
                    // the spawn gap: the view exists as soon as the latest Verified frame has the entity,
                    // while the render clock is still InterpolationDelayTicks behind — delay-1 ticks of it
                    // (IMP103, view-lifetime window).
                    //
                    // Using the live Predicted pose here is what produced a BACKWARD jump: predicted sits
                    // lead+delay ticks ahead of the render clock, so the moment the window reached the birth
                    // tick the view snapped back by lead+delay-2 ticks of motion. The newest Verified pose
                    // is authoritative and far closer to the render clock, which shrinks that jump to zero
                    // at delay<=2 and one tick at delay=3. It does not reach zero in general: this value is
                    // pose(lastVerified) and it advances each tick, so it is not the spawn pose after the
                    // first warmup frame. Holding the spawn pose instead would zero it at every delay and
                    // is filed separately — it needs per-view state.
                    //
                    // This branch is not only the spawn gap. Late join, FullState restore and render-clock
                    // drift catchup all produce "window misses, newest Verified has it", and the source
                    // changes there too. The direction holds in all of them — pose(lastVerified) is always
                    // closer to the render clock than the predicted pose — but the hold can be longer.
                    newPos = currPos = vPos;
                    newRot = currRot = vRot;
                    teleported = false;
                }
                else if (predictedAlive)
                {
                    // True ring warmup — no Verified frame at all (session start, late join before the ring
                    // fills, FullState restore). The live pose is the only source there is, and this is the
                    // one place the snapshot path still reads the Predicted frame for a value.
                    ref readonly var live = ref predicted.GetReadOnly<TransformComponent>(EntityRef);
                    newPos = currPos = ToVector3(live.Position);
                    newRot = currRot = Quaternion.Euler(0f, live.Rotation.ToFloat() * Mathf.Rad2Deg, 0f);
                    teleported = false;
                }
                else
                {
                    // No data on any timeline. Writing nothing is right here — and it is the only place
                    // that is true, because skipping the write does not hide the object: a pooled view
                    // still carries the previous occupant's transform.
                    return;
                }
            }
            else
            {
                if (!predictedAlive) return;

                ref readonly var curr = ref predicted.GetReadOnly<TransformComponent>(EntityRef);

                // ── CSP lerp (PredictedPrevious ↔ Predicted) — uses RenderClock.PredictedAlpha that updates per frame ──
                float alpha = Engine.RenderClock.PredictedAlpha;
                currPos = ToVector3(curr.Position);
                currRot = Quaternion.Euler(0f, curr.Rotation.ToFloat() * Mathf.Rad2Deg, 0f);

                newPos = currPos;
                newRot = currRot;

                // Default interpolation path. Lerps between PredictedPrevious and Predicted by alpha.
                //
                // IsAlive before Has, for the reason spelled out at the live-frame gate above: Has
                // forwards Index alone, so a recycled slot answers about whoever held it. The entity
                // allocator's free list is LIFO with immediate reuse, so a slot released inside a tick is
                // the FIRST one the next spawn takes — and the snapshot for that tick was captured on
                // entry, while the old occupant was still alive with a transform. Without this the new
                // view lerps out of a stranger's position for one whole tick (IMP103 V-8).
                var prev = prevRef.Frame;
                if (prev != null
                    && prev.Entities.IsAlive(EntityRef)
                    && prev.Has<TransformComponent>(EntityRef))
                {
                    ref readonly var prevT = ref prev.GetReadOnly<TransformComponent>(EntityRef);
                    Vector3 prevPos = ToVector3(prevT.Position);
                    float   prevYaw = prevT.Rotation.ToFloat() * Mathf.Rad2Deg;
                    float   currYaw = curr.Rotation.ToFloat() * Mathf.Rad2Deg;

                    newPos = Vector3.Lerp(prevPos, currPos, alpha);
                    newRot = Quaternion.Euler(0f, Mathf.LerpAngle(prevYaw, currYaw, alpha), 0f);
                }

                // TeleportTick rides on TransformComponent and the game stamps it with its own tick, so
                // comparing it against the tick this render interval sits on needs no engine support and no
                // error correction. Reading only the live frame keeps this independent of the prev endpoint
                // (IMP103 V-7). Reading it HERE rather than once for both paths is forced, not an
                // optimization: the snapshot path may have no live transform to take a `ref readonly` to.
                //
                // The `> 0` term is not defensive: TeleportTick defaults to 0, so without it every entity
                // that has never teleported answers true at CurrentTick == 1 and the whole CSP fleet takes
                // the teleport branch for as long as the tick cursor sits there (IMP105 C-1). The engine
                // guards the same ambiguity the same way (KlothoEngine.ErrorCorrection.cs). Losing the
                // ability to detect a teleport stamped during tick 0 costs nothing: both lerp endpoints
                // are then post-teleport, so there is no discontinuity to skip.
                teleported = curr.TeleportTick > 0 && curr.TeleportTick == Engine.CurrentTick - 1;
            }

            // HasEntityTeleported is an independent second source, because it does not mean what the render
            // needs. Its set is filled by ComputeErrorDeltas alone, which runs only when error correction is
            // on AND a rollback happened this frame — and even then it reports "re-simulation introduced a
            // teleport", not "the game teleported this tick". An ordinary respawn is neither, so the CSP
            // lerp used to walk across it for the whole tick interval in every configuration (IMP103 V-7).
            // It is ORed onto the CSP term only; the snapshot path uses the interpolator's own check (V-5).
            //
            // The whole error-visual pipeline sits behind EnableErrorCorrection, which is what the Godot
            // adapter has always done ("non-EC games pay nothing"). The flag is off by default, so
            // without this every game ran the delta lookups and the five-stage smoother for a feature it
            // never asked for. Three separate places depend on it — the teleport OR below, the skip flags
            // that feed the parameter, and the accumulation after ApplyTransform — because they sit on
            // either side of the transform write and cannot be one block (IMP103 D-1).
            //
            // Note the flag is read fresh each frame but the accumulator is only reset on bind
            // (InternalActivate). Flipping it from false to true mid-match would resume the smoother from
            // whatever it held when the gate closed; nothing in the engine does that, and the interface
            // exposes only a getter.
            bool errorCorrection = Engine.SimulationConfig.EnableErrorCorrection;

            if (errorCorrection && !snapshotPath)
                teleported |= Engine.HasEntityTeleported(EntityRef.Index);

            // EnableSnapshotInterpolation skips _errorVisual — the verified-frame interpolation already renders
            // the authoritative state, so applying rollback-delta-based offset would double-correct and jitter.
            bool skipPosError = !errorCorrection
                             || (_viewFlags & ViewFlags.DisablePositionUpdate) != 0
                             || snapshotPath;
            bool skipYawError = !errorCorrection || snapshotPath;

            // Refresh the error visual once per frame. The engine exposes only the delta caused by rollback
            // and excludes forward motion, so accumulation/decay/interpolation is performed only once at LateUpdate
            // to avoid redundant accumulation even when multiple ticks run in the same frame.
            //
            // Tick-then-read: the delta was produced in this frame's engine Update, so the smoothed value
            // that hides it has to be on this frame's transform. Reading first applied the offset one
            // frame late — the correction jump showed in full and the next frame jumped back — and a
            // teleport frame applied the previous offset once more before Reset cleared it (IMP103 V-9).
            //
            // Gated on the snapshot path, not on skipPosError: a DisablePositionUpdate-only view skips the
            // position offset but still applies the yaw one, so its accumulation has to keep running.
            // Only the snapshot path discards both, and it does so by design — verified-frame
            // interpolation already renders the authoritative state. Accumulating for it was work with no
            // consumer (IMP103 D-3). Safe because a view only changes path through EVU's bind, which calls
            // InternalActivate → _errorVisual.Reset() a few statements later; a game subclass writing
            // _viewFlags directly would bypass that.
            if (errorCorrection && !snapshotPath)
            {
                var (dx, dy, dz) = Engine.GetPositionDelta(EntityRef.Index);
                Vector3 rollbackDelta    = new Vector3(dx, dy, dz);
                float   rollbackYawDelta = Engine.GetYawDelta(EntityRef.Index);

                _errorVisual.Tick(
                    rollbackDelta, rollbackYawDelta, Time.deltaTime,
                    teleported, Engine.Logger, EntityRef.Index);
            }

            // ── Populate UpdatePositionParameter ──
            var param = new UpdatePositionParameter
            {
                NewPosition             = newPos,
                NewRotation             = newRot,
                UninterpolatedPosition  = currPos,
                UninterpolatedRotation  = currRot,
                ErrorVisualVector       = skipPosError ? Vector3.zero : _errorVisual.SmoothedPosError,
                ErrorVisualQuaternion   = skipYawError ? Quaternion.identity
                                                       : Quaternion.Euler(0f, _errorVisual.SmoothedYawError * Mathf.Rad2Deg, 0f),
                Teleported              = teleported,
            };

            ApplyTransform(ref param);

            OnLateUpdateView();
        }

        /// <summary>
        /// The newest Verified frame's pose for this entity, when the interpolation window has none.
        ///
        /// Reads <see cref="Core.IKlothoEngine.VerifiedFrame"/>, which does a ring lookup on every access —
        /// so this is called only from the fallback branch and never on a frame the window can serve.
        ///
        /// IsAlive comes first and Has after it, the same order the interpolator's occupancy check uses:
        /// Has forwards Index alone, so a recycled slot answers about its new occupant, and GetReadOnly
        /// on an entity without the component throws rather than returning anything (IMP103).
        /// </summary>
        private bool TryGetLatestVerifiedPose(out Vector3 position, out Quaternion rotation)
        {
            var verified = Engine.VerifiedFrame.Frame;
            if (verified != null
                && verified.Entities.IsAlive(EntityRef)
                && verified.Has<TransformComponent>(EntityRef))
            {
                ref readonly var t = ref verified.GetReadOnly<TransformComponent>(EntityRef);
                position = ToVector3(t.Position);
                rotation = Quaternion.Euler(0f, t.Rotation.ToFloat() * Mathf.Rad2Deg, 0f);
                return true;
            }

            position = default;
            rotation = default;
            return false;
        }

        private static Vector3 ToVector3(in xpTURN.Klotho.Deterministic.Math.FPVector3 v)
            => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
    }
}
