using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using ZLogger;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Single scene orchestrator. Runs Reconcile and View updates in the IKlothoEngine.OnTickExecuted hook.
    ///
    /// Lifecycle order:
    ///   1. engine.Tick completes → 2. OnTickExecuted fires → 3. EVU.Reconcile + InternalUpdateView
    ///   → 4. event dispatch → 5. Unity LateUpdate → 6. EVU.InternalLateUpdateView
    /// </summary>
    public class EntityViewUpdater : MonoBehaviour
    {
        /// <summary>
        /// Factory responsible for BindBehaviour / ViewFlags determination and prefab instantiation.
        /// </summary>
        [SerializeField] private EntityViewFactory _factory;

        /// <summary>
        /// Optional view pool. Injected into the Factory during Initialize.
        /// If null, the Factory calls Object.Instantiate directly.
        /// </summary>
        [SerializeField] private DefaultEntityViewPool _pool;

        // Active views. Keyed by EntityRef.Index and mutually exclusive with the pending collection.
        private readonly Dictionary<int, EntityView> _viewsByEntityIndex = new();

        // Spawn-sequence counter per Index. SpawnViewAsync compares against this on completion to discard stale results.
        private readonly Dictionary<int, int> _pendingSpawnCounter = new();

        // EntityRef.Version per Index for in-flight spawns. Used to detect entity-slot reuse across rollback cycles.
        // Invariant: _pendingSpawnCounter and _pendingEntityVersion always share the same key set.
        private readonly Dictionary<int, int> _pendingEntityVersion = new();

        // Index → EntityRef.Version snapshot of present entities, populated by CollectPresent and consumed by DestroyStale.
        private readonly Dictionary<int, int> _presentEntityVersions = new();

        // Index → OwnerComponent.OwnerId snapshot for entities that have OwnerComponent.
        // Missing key = entity has no OwnerComponent → Owner-agnostic, treat as match in DestroyStale.
        private readonly Dictionary<int, int> _presentEntityOwners   = new();
        private readonly List<int>            _staleIndices          = new();
        private readonly List<int>            _pendingStaleKeys      = new();
        private int _versionCounter;

        // Reusable buffer for collecting live entities during Reconcile (GC-free).
        private EntityRef[] _entityScratch;

        protected IKlothoEngine Engine { get; private set; }

        public EntityViewFactory Factory => _factory;

        /// <summary>
        /// Bootstrap order:
        ///   1. Create KlothoEngine
        ///   2. engine.Initialize
        ///   3. engine.Start / StartSpectator / StartReplay (IsReplayMode/IsSpectatorMode finalized)
        ///   4. evu.Initialize(engine) — this method
        ///   5. On the first OnTickExecuted firing, Reconcile runs and Factory lookups occur.
        /// </summary>
        public void Initialize(IKlothoEngine engine)
        {
            // To handle the case where scene objects are reused between sessions, unsubscribe from the previous session first.
            if (Engine != null)
                Engine.OnTickExecuted -= OnTickExecuted;

            Engine = engine;
            Engine.OnTickExecuted += OnTickExecuted;
            _factory?.Attach(engine, _pool);
        }

        /// <summary>
        /// Called at session end. Unsubscribes engine subscription and cleans up active views.
        /// The GameObject itself is not destroyed since it is a scene reuse target.
        /// </summary>
        public void Cleanup()
        {
            if (Engine != null)
            {
                Engine.OnTickExecuted -= OnTickExecuted;
                Engine = null;
            }

            // Return active views to pool / Destroy
            foreach (var view in _viewsByEntityIndex.Values)
            {
                view.OnDeactivate();
                if (_factory != null) _factory.Destroy(view);
                else if (view != null) Destroy(view.gameObject);
            }
            _viewsByEntityIndex.Clear();
            _pendingSpawnCounter.Clear();
            _pendingEntityVersion.Clear();
            _presentEntityVersions.Clear();
            _presentEntityOwners.Clear();
            _staleIndices.Clear();
            _pendingStaleKeys.Clear();
        }

        protected virtual void OnDestroy()
        {
            if (Engine != null)
                Engine.OnTickExecuted -= OnTickExecuted;
        }

        private void OnTickExecuted(int tick)
        {
            Reconcile();

            foreach (var view in _viewsByEntityIndex.Values)
                view.InternalUpdateView();
        }

        protected virtual void LateUpdate()
        {
            foreach (var view in _viewsByEntityIndex.Values)
                view.InternalLateUpdateView();
        }

        private void Reconcile()
        {
            _presentEntityVersions.Clear();
            _presentEntityOwners.Clear();

            // Frame may be null immediately after ring warmup / FullState restore / Late Join, so guard against it.
            var verified  = Engine.VerifiedFrame;
            var predicted = Engine.PredictedFrame;

            if (verified.Frame  != null) CollectPresent(verified,  BindBehaviour.Verified);
            if (predicted.Frame != null) CollectPresent(predicted, BindBehaviour.NonVerified);

            // If both are null, skip Reconcile — also skip DestroyStale to preserve stale views.
            if (verified.Frame == null && predicted.Frame == null) return;

            DestroyStale();
        }

        /// <summary>
        /// Collects only entities matching the given BindBehaviour as determined by the Factory.
        /// Skips collection if no Factory is assigned.
        /// </summary>
        private void CollectPresent(FrameRef frameRef, BindBehaviour matchBehaviour)
        {
            if (_factory == null) return;

            var frame = frameRef.Frame;
            int maxEntities = frame.MaxEntities;
            if (_entityScratch == null || _entityScratch.Length < maxEntities)
                _entityScratch = new EntityRef[maxEntities];

            int count = frame.GetAllLiveEntities(_entityScratch);
            for (int i = 0; i < count; i++)
            {
                var entity = _entityScratch[i];

                if (!_factory.TryGetBindBehaviour(frame, entity, out var entityBehaviour)) continue;
                if (entityBehaviour != matchBehaviour) continue;

                _presentEntityVersions[entity.Index] = entity.Version;
                if (frame.Has<OwnerComponent>(entity))
                    _presentEntityOwners[entity.Index] = frame.GetReadOnly<OwnerComponent>(entity).OwnerId;
                TrySpawn(entity, frameRef, entityBehaviour);
            }
        }

        // Returns true if either (a) entity has no OwnerComponent (Owner-agnostic),
        // or (b) view reports its cached Owner matches the current frame's Owner.
        // EVU lives in xpTURN.Klotho.Runtime.Unity asmdef which has no reference to concrete view
        // assemblies (e.g. Brawler.View) — identity comparison is delegated to a virtual method on EntityView.
        private static bool OwnersMatch(EntityView view, EntityRef entity, Frame frame)
        {
            if (!frame.Has<OwnerComponent>(entity)) return true;

            int currentOwner = frame.GetReadOnly<OwnerComponent>(entity).OwnerId;
            return view.OwnerMatches(currentOwner);
        }

        private void TrySpawn(EntityRef entity, FrameRef frame, BindBehaviour behaviour)
        {
            // Active view exists for this Index — compare EntityRef.Version + OwnerId for hybrid dedup.
            if (_viewsByEntityIndex.TryGetValue(entity.Index, out var existing))
            {
                bool versionMatch = existing.EntityRef.Version == entity.Version;
                bool ownerMatch   = OwnersMatch(existing, entity, frame.Frame);

                if (versionMatch && ownerMatch) return;  // truly same entity, dedup

                Engine?.Logger?.ZLogDebug($"[ViewLife][Rebind] entity={entity.Index}, viewType={existing.GetType().Name}, oldVersion={existing.EntityRef.Version}, newVersion={entity.Version}, versionMatch={versionMatch}, ownerMatch={ownerMatch}, viewIID={existing.GetInstanceID()}");
                existing.OnDeactivate();
                if (_factory != null) _factory.Destroy(existing);
                _viewsByEntityIndex.Remove(entity.Index);
            }

            // Pending async spawn exists — compare EntityRef.Version. On mismatch, invalidate so the in-flight result is discarded on completion.
            if (_pendingEntityVersion.TryGetValue(entity.Index, out var pendingVer))
            {
                if (pendingVer == entity.Version) return;  // same async spawn in flight

                _pendingSpawnCounter.Remove(entity.Index);
                _pendingEntityVersion.Remove(entity.Index);
            }

            int spawnCounter = ++_versionCounter;
            _pendingSpawnCounter[entity.Index]  = spawnCounter;
            _pendingEntityVersion[entity.Index] = entity.Version;
            SpawnViewAsync(entity, frame, behaviour, spawnCounter).Forget();
        }

        private async UniTaskVoid SpawnViewAsync(EntityRef entity, FrameRef frame, BindBehaviour behaviour, int spawnCounter)
        {
            ViewFlags flags = _factory.GetViewFlags(frame.Frame, entity);
            EntityView view = await _factory.CreateAsync(frame.Frame, entity, behaviour, flags);

            // Discard if the dispatch was invalidated (Version mismatch / stale clear) by the time the async completes.
            if (!_pendingSpawnCounter.TryGetValue(entity.Index, out int storedCounter)
                || storedCounter != spawnCounter
                || !_pendingEntityVersion.TryGetValue(entity.Index, out int storedEntityVersion)
                || storedEntityVersion != entity.Version)
            {
                if (view != null) _factory.Destroy(view);
                return;
            }
            _pendingSpawnCounter.Remove(entity.Index);
            _pendingEntityVersion.Remove(entity.Index);

            if (view == null) return;  // Factory refused spawn — clean up stale and exit.

            view.EntityRef = entity;
            view.Engine    = Engine;
            view.SetBindBehaviour(behaviour);
            view.SetViewFlags(flags);
            _viewsByEntityIndex[entity.Index] = view;

            // On pool reuse, OnInitialize is skipped but OnActivate is called every time.
            view.EnsureInitialized();
            view.InternalActivate(frame);
        }

        private void DestroyStale()
        {
            // Invalidate Pending too — on async completion, results are discarded due to version mismatch.
            _pendingStaleKeys.Clear();
            foreach (var kvp in _pendingSpawnCounter)
                if (!_presentEntityVersions.ContainsKey(kvp.Key))
                    _pendingStaleKeys.Add(kvp.Key);
            foreach (var key in _pendingStaleKeys)
            {
                _pendingSpawnCounter.Remove(key);
                _pendingEntityVersion.Remove(key);
            }

            foreach (var kvp in _viewsByEntityIndex)
            {
                if (!_presentEntityVersions.TryGetValue(kvp.Key, out int presentVersion)
                    || presentVersion != kvp.Value.EntityRef.Version)
                {
                    _staleIndices.Add(kvp.Key);
                    continue;
                }

                // Owner mismatch detection: when entity has OwnerComponent but the view's cached owner differs.
                // Helper not used here because _presentEntityOwners only contains entries for entities with OwnerComponent.
                if (_presentEntityOwners.TryGetValue(kvp.Key, out int presentOwner)
                    && !kvp.Value.OwnerMatches(presentOwner))
                {
                    _staleIndices.Add(kvp.Key);
                }
            }

            foreach (var idx in _staleIndices)
            {
                var view = _viewsByEntityIndex[idx];
                Engine?.Logger?.ZLogDebug($"[ViewLife][StaleDestroy] entity={idx}, viewType={view.GetType().Name}, viewVersion={view.EntityRef.Version}, viewIID={view.GetInstanceID()}");
                view.OnDeactivate();
                if (_factory != null) _factory.Destroy(view);
                else if (view != null) Destroy(view.gameObject);
                _viewsByEntityIndex.Remove(idx);
            }
            _staleIndices.Clear();
        }
    }
}
