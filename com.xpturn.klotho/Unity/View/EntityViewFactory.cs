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
    ///
    /// Subclass contract:
    /// - Override ResolvePrefab to map entity components to prefab assets.
    /// - Override ShouldRender to filter which entities become Views.
    /// - The 5-flag BindBehaviour / ViewFlags matrix and the Pool-aware Create/Destroy paths
    ///   are implemented as virtual defaults below — override only for special needs.
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

        // ── Game-specific overrides (abstract) ─────────────────────────────────────

        /// <summary>
        /// Returns the prefab to spawn for this entity, or null to skip.
        /// Typical implementation: branch on component type (Character / Item / etc.)
        /// and return the corresponding SerializeField prefab.
        /// </summary>
        protected abstract GameObject ResolvePrefab(Frame frame, EntityRef entity);

        /// <summary>
        /// Whether this factory accepts this entity as a View target (spawn-time decision).
        /// Returning false causes EVU.Reconcile to skip view creation for this entity entirely
        /// (no Pool.Rent, no Instantiate). Called per-entity each Reconcile pass — keep cheap.
        /// Typical implementation:
        ///   frame.Has&lt;CharacterComponent&gt;(entity) || frame.Has&lt;ItemComponent&gt;(entity).
        /// </summary>
        protected abstract bool ShouldRender(Frame frame, EntityRef entity);

        // ── Framework default decisions (virtual — game can override if needed) ───

        /// <summary>
        /// Determines whether this entity should be rendered as a View and which BindBehaviour to use.
        /// Default implementation: delegates the "should it have a View" decision to ShouldRender,
        /// then resolves BindBehaviour from the 5-flag matrix (Mode / IsServer / IsReplayMode /
        /// IsSpectatorMode / OwnerId == LocalPlayerId).
        /// </summary>
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

            // Server-authoritative entity (no Owner) — Verified on SD-Client/Spectator, NonVerified otherwise.
            behaviour = IsPredictedRender(ownerId: 0, hasOwner: false)
                ? BindBehaviour.NonVerified
                : BindBehaviour.Verified;
            return true;
        }

        /// <summary>
        /// The bits <see cref="GetViewFlags"/> is authoritative for. Everything outside this mask is
        /// left to whatever the prefab authored — see <see cref="ComposeViewFlags"/>.
        ///
        /// Only <see cref="ViewFlags.EnableSnapshotInterpolation"/> qualifies by default, because it is
        /// the one flag decided from runtime facts the prefab cannot know (network mode and whether the
        /// entity is locally owned). The rest describe how a particular view behaves and belong to the
        /// asset. Override to widen this if a factory decides more per entity — and it must be widened,
        /// not worked around: a bit this mask does not claim is taken from the prefab, so returning it
        /// from <c>GetViewFlags</c> alone would have it silently dropped.
        /// </summary>
        public virtual ViewFlags FactoryOwnedFlags => ViewFlags.EnableSnapshotInterpolation;

        /// <summary>
        /// Merges the prefab-authored flags with the factory's decision: the factory wins inside
        /// <see cref="FactoryOwnedFlags"/>, the prefab keeps everything else.
        ///
        /// A plain assignment would clobber the prefab's bits — the factory can only express two of the
        /// sixteen combinations, so overwriting destroys unrelated authoring. A
        /// plain OR is not the fix either: it would let a prefab that ticked
        /// <c>EnableSnapshotInterpolation</c> force the verified path onto a locally-owned entity and
        /// render the local player several ticks late. Masking is what keeps both halves honest.
        /// </summary>
        public ViewFlags ComposeViewFlags(ViewFlags prefabFlags, ViewFlags factoryFlags)
        {
            ViewFlags owned = FactoryOwnedFlags;
            return (prefabFlags & ~owned) | (factoryFlags & owned);
        }

        /// <summary>
        /// Computes per-entity ViewFlags (e.g. Snapshot Interpolation ON/OFF).
        /// Default: EnableSnapshotInterpolation on the Verified-path side, None on Predicted.
        /// Only the bits in <see cref="FactoryOwnedFlags"/> are read from the result.
        /// </summary>
        public virtual ViewFlags GetViewFlags(Frame frame, EntityRef entity)
        {
            bool hasOwner = frame.Has<OwnerComponent>(entity);
            int  ownerId  = hasOwner ? frame.GetReadOnly<OwnerComponent>(entity).OwnerId : -1;

            return IsPredictedRender(ownerId, hasOwner)
                ? ViewFlags.None
                : ViewFlags.EnableSnapshotInterpolation;
        }

        // ── Helpers (protected — reusable in custom overrides) ────────────────────

        /// <summary>
        /// True iff this entity should render as Predicted (local responsiveness).
        /// Replay (regardless of mode) OR non-Verified-path (P2P / SD-Server) → always Predicted.
        /// SD-Client (Verified path, not replay) → local player owned = Predicted, others = Verified.
        ///
        /// <b>A spectator owns nothing.</b> It has no LocalPlayerId, so the engine's `?? 0` fallback
        /// reports 0 — and 0 is a valid player id that a P2P host holds. Without the IsSpectatorMode
        /// guard a spectator watching a P2P session matched its own fallback against the host's entity and
        /// rendered that one predicted: running ahead of every other entity and snapping on rollback.
        /// `PlayerViewRegistry.IsActuallyLocal` already disambiguated the same collision the same way; this
        /// is that guard, in the one place both the render path and the binding are decided.
        ///
        /// <paramref name="hasOwner"/> exists so the no-owner case shares this expression instead of
        /// duplicating it. A sentinel ownerId would have been the alternative and is deliberately avoided:
        /// -1 already means "no local player" (`ServerNetworkService.LocalPlayerId`, `KlothoSession`), so
        /// reusing it for "no owner" overloads one value with two meanings.
        /// </summary>
        protected bool IsPredictedRender(int ownerId, bool hasOwner)
            => !UseVerifiedPath()
            || Engine.IsReplayMode
            || (hasOwner && !Engine.IsSpectatorMode && ownerId == Engine.LocalPlayerId);

        /// <summary>Owner-carrying overload — kept so existing subclass calls compile unchanged.</summary>
        protected bool IsPredictedRender(int ownerId) => IsPredictedRender(ownerId, hasOwner: true);

        /// <summary>True iff this peer uses the Verified path (SD-Client or Spectator).</summary>
        protected bool UseVerifiedPath()
        {
            bool isSDClient = (Engine.SimulationConfig.Mode == NetworkMode.ServerDriven) && !Engine.IsServer;
            return isSDClient || Engine.IsSpectatorMode;
        }

        // ── Spawn / Destroy default (virtual) ─────────────────────────────────────

        /// <summary>
        /// Instantiates a prefab via Pool when available, falling back to Object.Instantiate.
        /// Returning null causes EVU to discard the spawn.
        /// </summary>
        public virtual async UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
        {
            var prefab = ResolvePrefab(frame, entity);
            if (prefab == null) return null;

            // Spawn at the entity's pose. Otherwise a pooled view reappears wearing its previous
            // occupant's transform — Return keeps the local pose under the pool root, so re-renting
            // restores the world one — and a fresh Instantiate lands on the prefab's authored position.
            // The first ApplyTransform normally corrects that within the same frame, but it never runs
            // for a view whose flags skip the position line (DisableUpdate / DisablePositionUpdate), and
            // it runs a frame late for a pool that completes asynchronously. The interpolation child is
            // already cleared by InternalActivate; this is the root's half of the same fix.
            bool hasPose = TryGetSpawnPose(frame, entity, out Vector3 spawnPos, out Quaternion spawnRot);

            if (Pool != null)
                return await Pool.Rent(prefab,
                                       hasPose ? spawnPos : (Vector3?)null,
                                       hasPose ? spawnRot : (Quaternion?)null);

            var go = hasPose
                ? Object.Instantiate(prefab, spawnPos, spawnRot)
                : Object.Instantiate(prefab);
            var view = go.GetComponent<EntityView>();
            if (view == null)
            {
                Object.Destroy(go);
                return null;
            }
            return view;
        }

        /// <summary>
        /// The entity's pose for <see cref="CreateAsync"/>, or false when it carries no transform.
        ///
        /// Split out because <c>CreateAsync</c> is <c>async</c> and C# forbids by-reference locals
        /// there — reading the component through <c>ref readonly</c> has to happen in a regular method,
        /// and the alternative is copying the whole struct to read two fields.
        ///
        /// <c>protected</c> because an override that returns a view WITHOUT calling base — adopting a
        /// scene-placed object is the usual reason — skips the pose write above and has no other way to
        /// ask the same question. Returns false for an entity with no transform; leave the pose alone
        /// then.
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
            pos = new Vector3(t.Position.x.ToFloat(), t.Position.y.ToFloat(), t.Position.z.ToFloat());
            rot = Quaternion.Euler(0f, t.Rotation.ToFloat() * Mathf.Rad2Deg, 0f);
            return true;
        }

        /// <summary>
        /// Returns the view to the Pool when available, falling back to Object.Destroy.
        /// </summary>
        public virtual void Destroy(EntityView view)
        {
            if (view == null) return;
            if (Pool != null) Pool.Return(view);
            else Object.Destroy(view.gameObject);
        }
    }
}
