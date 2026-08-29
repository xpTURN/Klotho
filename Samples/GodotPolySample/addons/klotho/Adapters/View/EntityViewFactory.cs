// Abstract view factory: resolves a PackedScene per entity and decides BindBehaviour / ViewFlags.
// Game-specific factories override ResolvePrefab + ShouldRender; the decision matrix and instantiate
// paths are virtual defaults.
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Godot
{
    public abstract class EntityViewFactory
    {
        // Injected by the EntityViewUpdaterNode at Initialize time.
        public IKlothoEngine Engine { get; private set; }

        // Optional view pool. When null, Create/Destroy instantiate/free directly (default).
        public IGodotEntityViewPool Pool { get; private set; }

        internal void Attach(IKlothoEngine engine, IGodotEntityViewPool pool = null)
        {
            Engine = engine;
            Pool   = pool;
        }

        // ── Game-specific overrides ──
        protected abstract PackedScene ResolvePrefab(Frame frame, EntityRef entity);
        protected abstract bool ShouldRender(Frame frame, EntityRef entity);

        // ── Framework default decisions ──
        public virtual bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
        {
            if (!ShouldRender(frame, entity))
            {
                behaviour = BindBehaviour.Verified;
                return false;
            }

            if (frame.Has<OwnerComponent>(entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                behaviour = IsPredictedRender(owner.OwnerId)
                    ? BindBehaviour.NonVerified
                    : BindBehaviour.Verified;
                return true;
            }

            behaviour = IsPredictedRender(ownerId: 0, hasOwner: false)
                ? BindBehaviour.NonVerified
                : BindBehaviour.Verified;
            return true;
        }

        public virtual ViewFlags GetViewFlags(Frame frame, EntityRef entity)
        {
            bool hasOwner = frame.Has<OwnerComponent>(entity);
            int  ownerId  = hasOwner ? frame.GetReadOnly<OwnerComponent>(entity).OwnerId : -1;

            return IsPredictedRender(ownerId, hasOwner)
                ? ViewFlags.None
                : ViewFlags.EnableSnapshotInterpolation;
        }

        // True iff this entity should render as Predicted (local responsiveness). Replay or non-Verified
        // path (P2P / SD-Server) -> always Predicted. SD-Client -> local player owned = Predicted.
        //
        // A spectator owns nothing. It has no LocalPlayerId, so the engine's `?? 0` fallback reports 0 --
        // and 0 is a valid player id that a P2P host holds. Without the IsSpectatorMode guard a spectator
        // watching a P2P session matched its own fallback against the host's entity and rendered that one
        // predicted: running ahead of every other entity and snapping on rollback. PlayerViewRegistry's
        // IsActuallyLocal already disambiguated the same collision the same way (IMP103).
        //
        // hasOwner exists so the no-owner case shares this expression instead of duplicating it. A sentinel
        // ownerId was the alternative and is deliberately avoided: -1 already means "no local player"
        // (ServerNetworkService.LocalPlayerId, KlothoSession), so reusing it overloads one value.
        protected bool IsPredictedRender(int ownerId, bool hasOwner)
            => !UseVerifiedPath()
            || Engine.IsReplayMode
            || (hasOwner && !Engine.IsSpectatorMode && ownerId == Engine.LocalPlayerId);

        // Owner-carrying overload -- kept so existing subclass calls compile unchanged.
        protected bool IsPredictedRender(int ownerId) => IsPredictedRender(ownerId, hasOwner: true);

        protected bool UseVerifiedPath()
        {
            bool isSDClient = (Engine.SimulationConfig.Mode == NetworkMode.ServerDriven) && !Engine.IsServer;
            return isSDClient || Engine.IsSpectatorMode;
        }

        // ── Instantiate / Destroy default ──
        // Instantiates the resolved PackedScene (root must be an EntityViewNode). Returns null to skip.
        // The caller (EntityViewUpdaterNode) attaches the node to the scene tree.
        public virtual EntityViewNode Create(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
        {
            var prefab = ResolvePrefab(frame, entity);
            if (prefab == null) return null;

            var view = Pool != null ? Pool.Rent(prefab) : prefab.Instantiate<EntityViewNode>();
            if (view == null) return null;

            // Spawn at the entity's pose. A pooled node keeps its local Transform across Return — the pool
            // only detaches it — so a re-rented view otherwise reappears wearing its previous occupant's
            // pose, and a fresh Instantiate lands on the scene's authored one. The first ProcessViews
            // normally corrects that within the same frame (the session driver runs at a lower
            // ProcessPriority than the updater), but it never runs for flags that skip the position line
            // (DisableUpdate / DisablePositionUpdate) and it writes nothing on the snapshot branch that
            // finds the entity on no timeline at all — late join and ring warmup reach that (IMP105 C-17).
            //
            // Written as the LOCAL transform, and on the node while it is still detached: the steady-state
            // write does the same (Position = newPos with a world-space value), so the updater node being
            // at identity is an assumption the adapter already makes. GlobalPosition would only be valid
            // after the caller's AddChild, which is the wrong side of the seam.
            if (TryGetSpawnPose(frame, entity, out Vector3 spawnPos, out Quaternion spawnRot))
            {
                view.Position   = spawnPos;
                view.Quaternion = spawnRot;
            }

            return view;
        }

        /// <summary>
        /// The entity's pose for <see cref="Create"/>. protected because an override that returns a node
        /// WITHOUT calling base — adopting a scene-placed object is the usual reason — skips the pose
        /// write above and has no other way to ask the same question. Returns false for an entity with no
        /// transform; leave the pose alone then. Unity's factory carries the same helper under the same
        /// name (IMP105 C-13).
        /// </summary>
        protected static bool TryGetSpawnPose(Frame frame, EntityRef entity, out Vector3 pos, out Quaternion rot)
        {
            if (!frame.Has<TransformComponent>(entity))
            {
                pos = default;
                rot = default;
                return false;
            }

            ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);
            pos = t.Position.ToVector3();
            rot = Quaternion.FromEuler(new Vector3(0f, t.Rotation.ToFloat(), 0f));
            return true;
        }

        public virtual void Destroy(EntityViewNode view)
        {
            if (view == null) return;
            if (Pool != null) Pool.Return(view);
            else              view.QueueFree();
        }
    }
}
