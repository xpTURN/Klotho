// Single scene orchestrator for entity views.
// Subscribes to Engine.OnTickExecuted: Reconcile (spawn/destroy views matching the present entities)
// then InternalUpdateView per view. Per-frame interpolation (InternalLateUpdateView) runs from this
// node's own _Process — the adapter is a Godot.NET.Sdk project, so its Node lifecycle is dispatched.
// ProcessViews() is also exposed for explicit/headless drive. A high ProcessPriority makes it run after
// the session driver's _Process, so interpolation reads the frame the driver just advanced.
// Spawn pooling is opt-in (see IGodotEntityViewPool); the async/pending spawn paths are not implemented.
using System.Collections.Generic;
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Godot
{
    public partial class EntityViewUpdaterNode : Node
    {
        private EntityViewFactory _factory;
        private IKlothoEngine     _engine;

        // Keyed by EntityRef.ToId() — (Index, Version) packed — so a dying view and the new occupant
        // of the same entity slot coexist. That is what makes the despawn grace in DestroyStale
        // possible; see the Unity EntityViewUpdater for the full reasoning (IMP103).
        private readonly Dictionary<long, EntityViewNode> _viewsByEntity = new();
        private readonly Dictionary<int, int>             _presentEntityVersions = new();
        // Verified tick at which a snapshot-bound view was first found absent. No key = not dying.
        private readonly Dictionary<long, int>            _despawnVerifiedTick = new();
        private readonly List<long>                      _staleKeys = new();
        private EntityRef[] _entityScratch;

        // One warning per updater for views freed behind our back — see IsGone.
        private bool _warnedExternallyDestroyedView;

        // One warning per updater for a factory that refused a spawn — see TrySpawn.
        private bool _warnedFactoryRefusedSpawn;

        public EntityViewFactory Factory => _factory;

        // playerId -> view registry for Owner-bearing views, auto-populated on spawn/despawn.
        private GodotPlayerViewRegistry<EntityViewNode> _playerViews;
        public GodotPlayerViewRegistry<EntityViewNode> PlayerViews => _playerViews;

        public void Initialize(IKlothoEngine engine, EntityViewFactory factory, IGodotEntityViewPool pool = null)
        {
            Cleanup();
            _engine  = engine;
            _factory = factory;
            _factory.Attach(engine, pool);
            _playerViews = new GodotPlayerViewRegistry<EntityViewNode>(engine, engine.SessionConfig.MaxPlayers);
            _engine.OnTickExecuted += OnTickExecuted;
            // Run interpolation after the session driver's _Process (lower priority runs first).
            ProcessPriority = 1000;
        }

        // Per-frame interpolation, self-driven (the adapter's Node lifecycle is dispatched). No-op until
        // Initialize populates views. Equivalent to the explicit ProcessViews() call kept below.
        public override void _Process(double delta) => ProcessViews(delta);

        public void Cleanup()
        {
            if (_engine != null) _engine.OnTickExecuted -= OnTickExecuted;
            foreach (var view in _viewsByEntity.Values)
            {
                // Nothing to unbind here: the registry is cleared in bulk below, not per view.
                if (IsGone(view)) { WarnExternallyDestroyedViewOnce(); continue; }
                view.OnDeactivate();
                _factory?.Destroy(view);
            }
            _viewsByEntity.Clear();
            _presentEntityVersions.Clear();
            _despawnVerifiedTick.Clear();
            _staleKeys.Clear();
            _playerViews?.Clear();
            _playerViews = null;
            _engine = null;
        }

        private void OnTickExecuted(int tick)
        {
            Reconcile();
            foreach (var kvp in _viewsByEntity)
            {
                // A view inside its despawn grace gets no tick callback — its entity is already gone
                // from the Verified frame. Interpolation keeps running in ProcessViews (IMP103 D-4).
                if (_despawnVerifiedTick.ContainsKey(kvp.Key)) continue;
                if (IsGone(kvp.Value)) continue;   // reclaimed by the scan in the next Reconcile
                kvp.Value.InternalUpdateView();
            }
        }

        // Per-frame interpolation pass. Call once per frame after session.Update.
        // delta is the frame time (seconds), forwarded to each view for the error-visual decay.
        public void ProcessViews(double delta = 0)
        {
            foreach (var view in _viewsByEntity.Values)
            {
                if (IsGone(view)) continue;   // reclaimed by the scan in the next Reconcile
                view.InternalLateUpdateView((float)delta);
            }
        }

        private void Reconcile()
        {
            if (_factory == null) return;

            _presentEntityVersions.Clear();

            var verified  = _engine.VerifiedFrame;
            var predicted = _engine.PredictedFrame;

            if (verified.Frame  != null) CollectPresent(verified,  BindBehaviour.Verified);
            if (predicted.Frame != null) CollectPresent(predicted, BindBehaviour.NonVerified);

            if (verified.Frame == null && predicted.Frame == null) return;

            DestroyStale();
        }

        private void CollectPresent(FrameRef frameRef, BindBehaviour matchBehaviour)
        {
            var frame = frameRef.Frame;
            int maxEntities = frame.MaxEntities;
            if (_entityScratch == null || _entityScratch.Length < maxEntities)
                _entityScratch = new EntityRef[maxEntities];

            int count = frame.GetAllLiveEntities(_entityScratch);
            for (int i = 0; i < count; i++)
            {
                var entity = _entityScratch[i];

                if (!_factory.TryGetBindBehaviour(frame, entity, out var behaviour)) continue;
                if (behaviour != matchBehaviour) continue;

                _presentEntityVersions[entity.Index] = entity.Version;
                TrySpawn(entity, frameRef, behaviour);
            }
        }

        private void TrySpawn(EntityRef entity, FrameRef frame, BindBehaviour behaviour)
        {
            long key = entity.ToId();

            // The key carries the Version, so only the Owner can still differ here — a Version
            // mismatch is a different key and the two views are meant to coexist.
            if (_viewsByEntity.TryGetValue(key, out var existing))
            {
                // Above OwnersMatch, and deliberately NOT a return: CollectPresent (our caller) runs
                // before DestroyStale, so returning would leave the entity view-less until the scan
                // caught up a tick later. Falling through hands the slot over in this same tick.
                if (IsGone(existing))
                {
                    WarnExternallyDestroyedViewOnce();
                    TryUnregisterPlayerView(existing);   // unbind: the view was freed from outside
                    _viewsByEntity.Remove(key);
                    _despawnVerifiedTick.Remove(key);
                }
                else if (OwnersMatch(existing, entity, frame.Frame)) return; // same entity — keep the view
                else
                {
                    existing.OnDeactivate();
                    TryUnregisterPlayerView(existing);   // unbind: the slot was reused
                    _factory.Destroy(existing);
                    _viewsByEntity.Remove(key);
                    _despawnVerifiedTick.Remove(key);
                }
            }

            ViewFlags flags = _factory.GetViewFlags(frame.Frame, entity);
            var view = _factory.Create(frame.Frame, entity, behaviour, flags);

            // Factory refused the spawn. Silence here means the entity stays collectable and the next
            // Reconcile asks again — forever, with no line to say why it has no visual. Latched: the
            // state that produces it does not change tick to tick (IMP105 C-19).
            if (view == null)
            {
                if (!_warnedFactoryRefusedSpawn)
                {
                    _warnedFactoryRefusedSpawn = true;
                    _engine?.Logger?.KWarning($"[ViewLife] Factory returned no view for entity={entity.Index} (version={entity.Version}) — it will have no visual and the spawn is retried every tick. Refuse it in TryGetBindBehaviour instead, or give ResolvePrefab a branch for it.");
                }
                return;
            }

            view.EntityRef = entity;
            view.Engine    = _engine;
            view.SetBindBehaviour(behaviour);
            view.SetViewFlags(flags);

            AddChild(view);
            _viewsByEntity[key] = view;

            view.EnsureInitialized();
            view.InternalActivate(frame);
            TryRegisterPlayerView(entity, view, frame);
        }

        // Spawn-side: read OwnerComponent from the spawn-decision frame, cache it on the view (the stable
        // unregister key), and register. Owner-agnostic views (no OwnerComponent) are not registered.
        private void TryRegisterPlayerView(EntityRef entity, EntityViewNode view, FrameRef frame)
        {
            if (_playerViews == null) return;
            var f = frame.Frame;
            if (f == null || !f.Has<OwnerComponent>(entity)) return;
            int ownerId = f.GetReadOnly<OwnerComponent>(entity).OwnerId;
            view.SetCachedOwner(ownerId);
            _playerViews.Register(ownerId, view);
        }

        // Unbind-side: OwnerComponent may already be absent on the live frame at despawn, so the cached
        // owner is the unregister key. Called from rebind and DestroyStale.
        private void TryUnregisterPlayerView(EntityViewNode view)
        {
            if (_playerViews == null) return;
            if (!view.TryGetCachedOwner(out int ownerId)) return;
            _playerViews.Unregister(ownerId, view);
            view.ClearCachedOwner();
        }

        // Whether this entry's view was freed behind the updater's back — a reloaded scene, or a game
        // freeing one by hand. A freed Node's C# wrapper is NOT null, so `== null` answers the wrong
        // question here; IsInstanceValid answers the right one. Note a QueueFree'd node is still valid
        // and still safe to call into, which is why IsQueuedForDeletion has no business in this test.
        // Unity's copy calls the same predicate IsGone and asks `view == null` (IMP105 C-16).
        private static bool IsGone(EntityViewNode view) => !GodotObject.IsInstanceValid(view);

        // Latched: the state lasts as long as the entry, so an unlatched warning would be one per frame.
        private void WarnExternallyDestroyedViewOnce()
        {
            if (_warnedExternallyDestroyedView) return;
            _warnedExternallyDestroyedView = true;
            _engine?.Logger?.KWarning($"[ViewLife] A view was freed outside the EntityViewUpdaterNode. Reclaiming its entry. Free views through Cleanup() or let the entity die instead.");
        }

        private static bool OwnersMatch(EntityViewNode view, EntityRef entity, Frame frame)
        {
            if (!frame.Has<OwnerComponent>(entity)) return true;
            int currentOwner = frame.GetReadOnly<OwnerComponent>(entity).OwnerId;
            return view.OwnerMatches(currentOwner);
        }

        // Destroys views whose entity is gone — immediately on the CSP path, after a render-clock
        // grace on the snapshot path. A CSP view draws the Predicted frame so its lifetime criterion
        // and its render position are the same tick; a snapshot view draws the window the render clock
        // points at, which converges to LastVerifiedTick - InterpolationDelayTicks, so it has to
        // outlive its entity's disappearance from the Verified frame by that much (IMP103).
        //
        // The Version comparison below is new here — this adapter used to test presence by Index
        // alone, which meant a reused slot never produced a stale verdict and the whole burden sat on
        // TrySpawn's rebind. Unity has always compared the Version; the two are now symmetric.
        private void DestroyStale()
        {
            var verified = _engine.VerifiedFrame;
            int renderBaseTick = _engine.RenderClock.VerifiedBaseTick;

            _staleKeys.Clear();
            foreach (var kvp in _viewsByEntity)
            {
                var view = kvp.Value;

                // Freed from outside: stale by definition, and marking it here is what keeps the other
                // loops' exposure down to a single tick instead of forever (IMP105 C-16).
                if (IsGone(view)) { _staleKeys.Add(kvp.Key); continue; }

                if (view.BindBehaviour == BindBehaviour.Verified)
                {
                    if (IsCarriedByVerified(view, verified.Frame))
                    {
                        _despawnVerifiedTick.Remove(kvp.Key);   // resurrected by a rollback
                        continue;
                    }

                    if (!_despawnVerifiedTick.TryGetValue(kvp.Key, out int despawnTick))
                    {
                        if (verified.Frame == null) { _staleKeys.Add(kvp.Key); continue; }
                        despawnTick = verified.Tick;
                        _despawnVerifiedTick[kvp.Key] = despawnTick;
                        // No unbind here (there used to be one). The registry maps a player to the view on
                        // screen, and a view inside its grace is still on screen. Unbinding at grace entry
                        // left a resurrected view unmapped for the rest of the match, because Register has
                        // exactly one call site (end of TrySpawn) which a still-alive view never reaches
                        // (IMP105 C-2). Waiting is safe: Unregister is instance-guarded, so a respawn that
                        // claims the slot first wins over this view's unbind at the stale destroy below.
                    }

                    if (renderBaseTick < despawnTick) continue;  // still inside the grace window
                    _staleKeys.Add(kvp.Key);
                    continue;
                }

                if (!_presentEntityVersions.TryGetValue(view.EntityRef.Index, out int presentVersion)
                    || presentVersion != view.EntityRef.Version)
                {
                    _staleKeys.Add(kvp.Key);
                }
            }

            foreach (var key in _staleKeys)
            {
                var view = _viewsByEntity[key];

                // The bookkeeping still runs — TryUnregisterPlayerView reads the cached owner, a
                // managed field, so a freed view can and must still be unbound (IMP105 C-16).
                if (IsGone(view))
                {
                    WarnExternallyDestroyedViewOnce();
                    TryUnregisterPlayerView(view);   // unbind: the view was freed from outside
                    _viewsByEntity.Remove(key);
                    _despawnVerifiedTick.Remove(key);
                    continue;
                }

                view.OnDeactivate();
                TryUnregisterPlayerView(view);   // unbind: the view is going away
                _factory.Destroy(view);
                _viewsByEntity.Remove(key);
                _despawnVerifiedTick.Remove(key);
            }
        }

        // Positive-only fast path over the presence snapshot: it holds one Version per Index and the
        // Predicted pass overwrites the Verified one, so a match proves the entity was collected this
        // tick while a mismatch proves nothing. On a mismatch ask the frame with the view's own
        // handle — Entities.IsAlive is version-aware, which is what makes coexistence safe.
        private bool IsCarriedByVerified(EntityViewNode view, Frame verified)
        {
            var entity = view.EntityRef;
            if (_presentEntityVersions.TryGetValue(entity.Index, out int version) && version == entity.Version)
                return true;

            if (verified == null || !verified.Entities.IsAlive(entity)) return false;

            return _factory != null
                && _factory.TryGetBindBehaviour(verified, entity, out var behaviour)
                && behaviour == BindBehaviour.Verified;
        }
    }
}
