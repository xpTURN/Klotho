// Godot view node for a single entity. The EntityViewUpdaterNode manages its lifecycle and
// drives InternalUpdateView (per tick) and InternalLateUpdateView (per frame: interpolate + apply transform).
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Godot
{
    public partial class EntityViewNode : Node3D
    {
        public EntityRef     EntityRef { get; set; }
        public IKlothoEngine Engine    { get; set; }

        public BindBehaviour BindBehaviour { get; private set; } = BindBehaviour.Verified;
        public ViewFlags     ViewFlags     { get; private set; } = ViewFlags.None;

        internal void SetBindBehaviour(BindBehaviour value) => BindBehaviour = value;
        internal void SetViewFlags(ViewFlags value)         => ViewFlags = value;

        // Cached OwnerComponent.OwnerId, written at spawn-register time. Used as the stable unregister
        // key because OwnerComponent may already be absent on the live frame at despawn (GodotPlayerViewRegistry).
        private int  _cachedOwner;
        private bool _hasCachedOwner;
        internal void SetCachedOwner(int ownerId) { _cachedOwner = ownerId; _hasCachedOwner = true; }
        internal bool TryGetCachedOwner(out int ownerId) { ownerId = _cachedOwner; return _hasCachedOwner; }
        internal void ClearCachedOwner() => _hasCachedOwner = false;

        // Override required for any entity carrying OwnerComponent; the default returns false so a
        // missing override surfaces as continuous rebind churn rather than a silent owner-swap bug.
        public virtual bool OwnerMatches(int ownerId) => false;

        private bool _hasInitialized;

        internal void EnsureInitialized()
        {
            if (_hasInitialized) return;
            _hasInitialized = true;
            OnInitialize();
        }

        // Per-view desync error-blending state. Config is preserved across pool reuse; accumulation is reset on activate.
        private ErrorVisualState _errorVisual = ErrorVisualState.Default;

        internal void InternalActivate(FrameRef frame)
        {
            _errorVisual.Reset();
            OnActivate(frame);
        }

        // ── Lifecycle callbacks (override in game-specific views) ──
        public virtual void OnInitialize() { }
        public virtual void OnActivate(FrameRef frame) { }
        public virtual void OnUpdateView() { }
        public virtual void OnLateUpdateView() { }
        public virtual void OnDeactivate() { }

        internal virtual void InternalUpdateView()
        {
            if ((ViewFlags & ViewFlags.DisableUpdate) != 0) return;
            if (Engine == null || !EntityRef.IsValid) return;
            OnUpdateView();
        }

        // Per-frame interpolation + transform apply.
        // dt is the frame delta (seconds), forwarded from EntityViewUpdaterNode._Process for the error-visual decay.
        internal virtual void InternalLateUpdateView(float dt = 0f)
        {
            if ((ViewFlags & ViewFlags.DisableUpdate) != 0) return;
            if (Engine == null || !EntityRef.IsValid) return;

            var predicted = Engine.PredictedFrame.Frame;
            if (predicted == null) return;

            // Whether the entity is alive on the LIVE Predicted frame. IsAlive first, and the order is
            // load-bearing: Has forwards Index alone, so once the slot is recycled it answers about the new
            // occupant and this view would render a stranger's transform. Live-frame mirror of what
            // VerifiedFrameInterpolator guards on past frames.
            //
            // This used to be an early return for BOTH paths. It still is for CSP, whose whole render basis
            // is this frame — but the snapshot path renders the Verified timeline, where the entity can be
            // perfectly alive while prediction has already destroyed it. Gating that path here froze such a
            // view for the entire SD lead (IMP103 D-2). Demoting the check from a gate to a fallback
            // condition is NOT the same as dropping it: the snapshot path still may not read this frame's
            // transform without it.
            bool predictedAlive = predicted.Entities.IsAlive(EntityRef)
                               && predicted.Has<TransformComponent>(EntityRef);

            bool snapshot = (ViewFlags & ViewFlags.EnableSnapshotInterpolation) != 0;

            Vector3    newPos;
            Quaternion newRot;

            // "The game teleported this entity on the tick this render interval sits on." Computed on the
            // CSP branch only and left false for snapshot, mirroring the Unity view which ORs it onto the
            // CSP term alone: the snapshot path gets its discontinuity from the interpolator's own refusal
            // to lerp across a TeleportTick change, and ApplyErrorVisual returns before reading this on
            // that path anyway (IMP105 C-7).
            bool teleported = false;

            if (snapshot)
            {
                // Smooth between two adjacent Verified frames. One query answers pose, occupancy and
                // teleport-in-window; see VerifiedFrameInterpolator.SnapshotPose.
                var pose = VerifiedFrameInterpolator.GetSnapshotPose(EntityRef, Engine);
                if (pose.Occupied)
                {
                    newPos = pose.Position;
                    newRot = pose.Rotation;
                }
                else if (TryGetLatestVerifiedPose(out Vector3 vPos, out Quaternion vRot))
                {
                    // The window holds nothing for this entity, but the newest Verified frame does — the
                    // spawn gap: the view exists as soon as the latest Verified frame has the entity while
                    // the render clock is still InterpolationDelayTicks behind (delay-1 ticks of it).
                    //
                    // Using the live Predicted pose here produced a BACKWARD jump: predicted sits lead+delay
                    // ticks ahead of the render clock, so the moment the window reached the birth tick the
                    // view snapped back by lead+delay-2 ticks of motion. The newest Verified pose is
                    // authoritative and far closer to the render clock, shrinking that jump to zero at
                    // delay<=2 and one tick at delay=3. Not zero in general: this is pose(lastVerified) and
                    // it advances each tick, so it is the spawn pose only on the first warmup frame.
                    //
                    // Not only the spawn gap: late join, FullState restore and render-clock drift catchup
                    // all produce "window misses, newest Verified has it", and the source changes there too.
                    newPos = vPos;
                    newRot = vRot;
                }
                else if (predictedAlive)
                {
                    // True ring warmup — no Verified frame at all (session start, late join before the ring
                    // fills, FullState restore). The live pose is the only source there is, and this is the
                    // one place the snapshot path still reads the Predicted frame for a value.
                    ref readonly var live = ref predicted.GetReadOnly<TransformComponent>(EntityRef);
                    newPos = live.Position.ToVector3();
                    newRot = Quaternion.FromEuler(new Vector3(0f, live.Rotation.ToFloat(), 0f));
                }
                else
                {
                    // No data on any timeline. Writing nothing is right here — and it is the only place
                    // that is true, because skipping the write does not hide the node: a pooled view still
                    // carries the previous occupant's transform.
                    return;
                }
            }
            else
            {
                if (!predictedAlive) return;

                ref readonly var curr = ref predicted.GetReadOnly<TransformComponent>(EntityRef);
                Vector3    currPos = curr.Position.ToVector3();
                Quaternion currRot = Quaternion.FromEuler(new Vector3(0f, curr.Rotation.ToFloat(), 0f));

                newPos = currPos;
                newRot = currRot;

                if (curr.TeleportTick > 0 && curr.TeleportTick == Engine.CurrentTick - 1)
                {
                    // Teleport on the tick this render interval sits on — hold the post-teleport pose instead
                    // of lerping through positions the entity never occupied. TeleportTick rides on
                    // TransformComponent and the game stamps it with its own tick, so this needs neither
                    // engine support nor EnableErrorCorrection: HasEntityTeleported only ever fires when error
                    // correction is on AND a rollback happened, and even then it means "re-simulation
                    // introduced a teleport", not "the game teleported this tick" (IMP103 V-7). newPos/newRot
                    // already hold the live pose, so skipping the lerp is all the interpolation needs.
                    //
                    // The flag carries the same verdict on to the error smoother. Without it the smoother
                    // saw HasEntityTeleported alone — a set filled by ComputeErrorDeltas, i.e. the OTHER
                    // meaning — so a game teleport did not reset the accumulator and the pre-teleport
                    // offset was added on top of the destination for the frames it took to decay
                    // (IMP105 C-7). Resetting is enough; the Unity adapter's second guard (bypassing the
                    // offset in the transform write) has nothing left to bypass once the smoother is 0.
                    //
                    // The `> 0` term keeps the default value out: TeleportTick defaults to 0, so without it
                    // every entity that has never teleported matches at CurrentTick == 1 (IMP105 C-1).
                    teleported = true;
                }
                else
                {
                    // Lerp between PredictedPrevious and Predicted by the per-frame render alpha.
                    //
                    // IsAlive before Has, same reason as the live-frame gate above: Has forwards Index alone,
                    // and the allocator's free list is LIFO with immediate reuse, so a slot released inside a
                    // tick is the first one the next spawn takes — while that tick's snapshot, captured on
                    // entry, still holds the old occupant alive. Without this the new view lerps out of a
                    // stranger's position for one whole tick (IMP103 V-8).
                    var prev = Engine.PredictedPreviousFrame.Frame;
                    if (prev != null
                        && prev.Entities.IsAlive(EntityRef)
                        && prev.Has<TransformComponent>(EntityRef))
                    {
                        ref readonly var prevT = ref prev.GetReadOnly<TransformComponent>(EntityRef);
                        float alpha = Engine.RenderClock.PredictedAlpha;
                        newPos = prevT.Position.ToVector3().Lerp(currPos, alpha);
                        newRot = Quaternion.FromEuler(new Vector3(
                            0f, Mathf.LerpAngle(prevT.Rotation.ToFloat(), curr.Rotation.ToFloat(), alpha), 0f));
                    }
                }
            }

            // Blend the rollback-induced offset on top of the predicted interpolation so a corrected entity
            // drifts smoothly instead of snapping. Gated on EnableErrorCorrection so non-EC games pay nothing.
            if (Engine.SimulationConfig.EnableErrorCorrection)
                ApplyErrorVisual(ref newPos, ref newRot, snapshot, dt, teleported);

            if ((ViewFlags & ViewFlags.DisablePositionUpdate) == 0)
                Position = newPos;
            Quaternion = newRot;

            OnLateUpdateView();
        }

        // The newest Verified frame's pose for this entity, when the interpolation window has none.
        // Engine.VerifiedFrame does a ring lookup on every access, so this is called only from the fallback
        // branch and never on a frame the window can serve. IsAlive first and Has after it, the same order
        // the interpolator's occupancy check uses: Has forwards Index alone so a recycled slot answers about
        // its new occupant, and GetReadOnly on an entity without the component throws (IMP103).
        private bool TryGetLatestVerifiedPose(out Vector3 position, out Quaternion rotation)
        {
            var verified = Engine.VerifiedFrame.Frame;
            if (verified != null
                && verified.Entities.IsAlive(EntityRef)
                && verified.Has<TransformComponent>(EntityRef))
            {
                ref readonly var t = ref verified.GetReadOnly<TransformComponent>(EntityRef);
                position = t.Position.ToVector3();
                rotation = Quaternion.FromEuler(new Vector3(0f, t.Rotation.ToFloat(), 0f));
                return true;
            }

            position = default;
            rotation = default;
            return false;
        }

        // Tick-then-read: advance the accumulation with this frame's rollback delta, then apply the smoothed
        // error to this frame's transform. The delta was produced in this frame's engine update, so reading
        // first applied the offset one frame late — the correction jump showed in full and the next frame
        // jumped back — and a teleport frame applied the previous offset once more before Reset cleared it
        // (IMP103 V-9). skipPosError follows DisablePositionUpdate; yaw error still applies under it.
        // The yaw error is radians, applied as a pre-multiplied quaternion (error * rotation).
        private void ApplyErrorVisual(ref Vector3 newPos, ref Quaternion newRot, bool snapshot, float dt,
                                     bool teleported)
        {
            // Nothing to do on the snapshot path: it applies neither offset, so the delta lookups and the
            // smoother would feed a value no one reads. Keyed on `snapshot` rather than skipPosError
            // because a DisablePositionUpdate-only view still applies the yaw offset (IMP103 D-3).
            if (snapshot) return;

            int idx = EntityRef.Index;
            var (dx, dy, dz) = Engine.GetPositionDelta(idx);
            // Two independent sources, ORed. `teleported` is the game's own stamp read off TransformComponent;
            // HasEntityTeleported means "re-simulation introduced a teleport" and only ever fires when error
            // correction is on AND a rollback happened. An ordinary respawn is the first and not the second.
            _errorVisual.Tick(new Vector3(dx, dy, dz), Engine.GetYawDelta(idx), dt,
                              teleported || Engine.HasEntityTeleported(idx));

            if ((ViewFlags & ViewFlags.DisablePositionUpdate) == 0)
                newPos += _errorVisual.SmoothedPosError;
            newRot = Quaternion.FromEuler(new Vector3(0f, _errorVisual.SmoothedYawError, 0f)) * newRot;
        }
    }
}
