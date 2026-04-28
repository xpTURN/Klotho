using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
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

        // Version of entities being spawned. Since DestroyStale also clears pending keys,
        // async completion can detect version mismatches and naturally discard the result.
        private readonly Dictionary<int, int> _pendingVersion = new();

        private readonly HashSet<int> _presentEntityIndices = new();
        private readonly List<int>    _staleIndices         = new();
        private readonly List<int>    _pendingStaleKeys     = new();
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
            _pendingVersion.Clear();
            _presentEntityIndices.Clear();
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
            _presentEntityIndices.Clear();

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

                _presentEntityIndices.Add(entity.Index);
                TrySpawn(entity, frameRef, entityBehaviour);
            }
        }

        private void TrySpawn(EntityRef entity, FrameRef frame, BindBehaviour behaviour)
        {
            if (_viewsByEntityIndex.ContainsKey(entity.Index)) return;
            if (_pendingVersion.ContainsKey(entity.Index))    return;  // already in progress asynchronously

            int version = ++_versionCounter;
            _pendingVersion[entity.Index] = version;
            SpawnViewAsync(entity, frame, behaviour, version).Forget();
        }

        private async UniTaskVoid SpawnViewAsync(EntityRef entity, FrameRef frame, BindBehaviour behaviour, int version)
        {
            ViewFlags flags = _factory.GetViewFlags(frame.Frame, entity);
            EntityView view = await _factory.CreateAsync(frame.Frame, entity, behaviour, flags);

            // If the version changed by the time the async completes, treat it as stale and discard.
            if (!_pendingVersion.TryGetValue(entity.Index, out int stored) || stored != version)
            {
                if (view != null) _factory.Destroy(view);
                return;
            }
            _pendingVersion.Remove(entity.Index);

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
            foreach (var kvp in _pendingVersion)
                if (!_presentEntityIndices.Contains(kvp.Key))
                    _pendingStaleKeys.Add(kvp.Key);
            foreach (var key in _pendingStaleKeys)
                _pendingVersion.Remove(key);

            foreach (var kvp in _viewsByEntityIndex)
                if (!_presentEntityIndices.Contains(kvp.Key))
                    _staleIndices.Add(kvp.Key);

            foreach (var idx in _staleIndices)
            {
                var view = _viewsByEntityIndex[idx];
                view.OnDeactivate();
                if (_factory != null) _factory.Destroy(view);
                else if (view != null) Destroy(view.gameObject);
                _viewsByEntityIndex.Remove(idx);
            }
            _staleIndices.Clear();
        }
    }
}
