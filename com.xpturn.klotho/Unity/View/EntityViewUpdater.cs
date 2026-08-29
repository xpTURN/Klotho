using System.Collections.Generic;
using xpTURN.Klotho.Logging;
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

        // Pool supplied through Initialize. It cannot share the field above: that one is a concrete
        // DefaultEntityViewPool because Unity does not serialize interface references, so an injected
        // IEntityViewPool has nowhere else to live. Injection wins when both are present (IMP104 W-2).
        private IEntityViewPool _injectedPool;

        // Active views, keyed by EntityRef.ToId() — (Index, Version) packed into a long.
        //
        // The key carries the Version so that a dying view and the new occupant of the same entity
        // slot coexist as ordinary entries. That is what makes the despawn grace in DestroyStale
        // possible: the render clock sits InterpolationDelayTicks behind the Verified frame, so a
        // snapshot-bound view has to outlive its entity's disappearance from that frame by exactly
        // that much or the tail of its motion is never drawn (IMP103, view-despawn grace).
        private readonly Dictionary<long, EntityView> _viewsByEntity = new();

        // Spawn-sequence counter per entity. SpawnViewAsync compares against this on completion to
        // discard stale results. Keyed by ToId() as well — the Version is in the key, so a second
        // in-flight spawn for the same slot no longer cancels the first one's completion. That
        // cancellation is exactly what would keep the grace view from ever appearing.
        private readonly Dictionary<long, int> _pendingSpawnCounter = new();

        // Index → EntityRef.Version snapshot of present entities, populated by CollectPresent.
        // Read as a POSITIVE-ONLY fast path in DestroyStale — see IsCarriedByVerified for why the
        // negative direction is not trustworthy.
        private readonly Dictionary<int, int> _presentEntityVersions = new();

        // Verified tick at which a snapshot-bound view was first found absent from the Verified
        // frame. The view then lives until the render clock reaches that tick. No key = not dying.
        private readonly Dictionary<long, int> _despawnVerifiedTick = new();

        // One warning per updater for views destroyed behind our back — see IsGone.
        private bool _warnedExternallyDestroyedView;

        // One warning per updater for a factory that refused a spawn — see SpawnViewAsync.
        private bool _warnedFactoryRefusedSpawn;

        private readonly List<long>           _staleKeys             = new();
        private readonly List<long>           _pendingStaleKeys      = new();
        private int _versionCounter;

        // Reusable buffer for collecting live entities during Reconcile (GC-free).
        private EntityRef[] _entityScratch;

        // playerId → EntityView registry. Built each Initialize against engine.SessionConfig.MaxPlayers.
        // EVU is the sole Register / Unregister site (game subscribes to events).
        private PlayerViewRegistry<EntityView> _playerViews;

        protected IKlothoEngine Engine { get; private set; }

        public EntityViewFactory Factory => _factory;

        public PlayerViewRegistry<EntityView> PlayerViews => _playerViews;

        /// <summary>
        /// Bootstrap order:
        ///   1. Create KlothoEngine
        ///   2. engine.Initialize
        ///   3. engine.Start / StartSpectator / StartReplay (IsReplayMode/IsSpectatorMode finalized)
        ///   4. evu.Initialize(engine) — this method
        ///   5. On the first OnTickExecuted firing, Reconcile runs and Factory lookups occur.
        ///
        /// <paramref name="factory"/> and <paramref name="pool"/> let code supply what the Inspector
        /// normally holds — a DI container, a factory built with <c>ScriptableObject.CreateInstance</c>,
        /// or a test harness. This mirrors the Godot adapter, whose Initialize has taken them from the
        /// start.
        ///
        /// <b>Re-entering reclaims the previous session.</b> Initialize begins with
        /// <see cref="Cleanup"/>, so calling it a second time returns the still-active views through
        /// the factory and empties the bookkeeping before the new engine is attached. Calling
        /// <see cref="Cleanup"/> yourself at session end is still the documented shape — this only
        /// keeps the omission from turning into ghost views.
        ///
        /// <b>Passing null means "keep whatever is already set", not "fall back to the serialized
        /// field".</b> EVU is a scene-reuse target and Initialize runs once per session, so a fallback
        /// would silently revert an injected factory the next time someone calls the argument-less
        /// overload.
        ///
        /// <b>One EVU per factory asset.</b> <see cref="EntityViewFactory.Attach"/> stores Engine and
        /// Pool on the ScriptableObject itself, so two updaters sharing one asset is not supported —
        /// the last Attach wins.
        /// </summary>
        public void Initialize(IKlothoEngine engine, EntityViewFactory factory = null, IEntityViewPool pool = null)
        {
            // Re-entry reclaims the previous session. Unsubscribing was not enough: every other piece
            // of per-session state survived, and the active-view dictionary above all — those views
            // went on drawing a stopped engine's last frame, unreachable through PlayerViews (which
            // IS replaced, below), and their keys collided with the new session's. A fresh
            // EntityManager hands out (0, 1) again, so TrySpawn found the old entry, OwnersMatch said
            // yes for any entity without an OwnerComponent, and the new spawn was deduped away
            // silently (IMP105 C-15). The Godot adapter's Initialize has always started this way.
            //
            // The warning goes BEFORE the call because Cleanup nulls Engine — and therefore the
            // logger — on its way out. It stays quiet on a first Initialize: no views, no warning.
            // Cleanup itself is idempotent, so a game that does call it first pays nothing.
            //
            // This has to stay above the factory/pool injection: Cleanup reclaims through the factory
            // that is attached NOW, and a factory handed in here has not been attached yet — its Pool
            // is null, so the old views would be destroyed outright instead of returned to the pool.
            if (_viewsByEntity.Count > 0)
                Engine?.Logger?.KWarning($"[ViewLife] Initialize called while {_viewsByEntity.Count} view(s) are still active — reclaiming them. Call Cleanup() at session end.");
            Cleanup();

            Engine = engine;
            Engine.OnTickExecuted += OnTickExecuted;

            // Injection before the check below: a caller that hands in a factory here must not be
            // warned about the empty Inspector slot.
            if (factory != null) _factory      = factory;
            if (pool    != null) _injectedPool = pool;

            // A missing Factory is silent otherwise: CollectPresent returns early, nothing spawns, and
            // no log is emitted — the game just gets an empty scene. The Pool is genuinely optional, but
            // this is not, and the null-conditional below reads the same for both (IMP104 W-5).
            if (_factory == null)
                Engine?.Logger?.KWarning($"[ViewLife] EntityViewUpdater has no Factory assigned — no views will be created. Assign one in the Inspector.");

            // `??` and `?.` do NOT use Unity's null overload, so a destroyed or Missing serialized
            // reference passes straight through them — the warning above fires and then the very same
            // object is handed to Attach and used for the rest of the session, while every later
            // `_factory != null` (which DOES use the overload) answers the opposite. Ask the same
            // question the same way in all three places (IMP105 C-10).
            //
            // _injectedPool is code-supplied and interface-typed, so a plain null test is the right one
            // for it; the fallback below is the serialized reference and gets Unity's.
            IEntityViewPool effectivePool = _injectedPool;
            if (effectivePool == null && _pool != null) effectivePool = _pool;

            if (_factory != null) _factory.Attach(engine, effectivePool);

            // PlayerViewRegistry — capacity sized from server-authoritative SessionConfig.
            // A fresh instance per Initialize forces ViewSync to re-subscribe each session
            // (see InitializeViewSync invariant: EVU.Initialize ⇔ ViewSync.Initialize atomic).
            _playerViews = new PlayerViewRegistry<EntityView>(engine, engine.SessionConfig.MaxPlayers);
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

            // Return active views to pool / Destroy. Views inside their despawn grace are ordinary
            // entries here, so they are reclaimed by the same loop.
            foreach (var view in _viewsByEntity.Values)
            {
                // Nothing to unbind here: the registry is cleared in bulk below, not per view.
                if (IsGone(view)) { WarnExternallyDestroyedViewOnce(); continue; }
                view.OnDeactivate();
                if (_factory != null) _factory.Destroy(view);
                else Destroy(view.gameObject);
            }
            _viewsByEntity.Clear();
            _pendingSpawnCounter.Clear();
            _presentEntityVersions.Clear();
            _despawnVerifiedTick.Clear();
            _staleKeys.Clear();
            _pendingStaleKeys.Clear();

            // Bulk-clear and null the player registry. Null assignment surfaces stale ViewSync
            // references as NullRef on the next call — safer than silently allowing leaked
            // subscriptions to fire on a re-created instance.
            _playerViews?.Clear();
            _playerViews = null;
        }

        protected virtual void OnDestroy()
        {
            if (Engine != null)
                Engine.OnTickExecuted -= OnTickExecuted;
        }

        private void OnTickExecuted(int tick)
        {
            Reconcile();

            foreach (var kvp in _viewsByEntity)
            {
                // A view inside its despawn grace gets no tick callback: the entity it draws is
                // already gone from the Verified frame, so OnUpdateView would be a callback about
                // something the game can no longer look up. Interpolation still runs in LateUpdate —
                // that is what the grace exists for (IMP103, view-despawn grace D-4).
                if (_despawnVerifiedTick.ContainsKey(kvp.Key)) continue;
                if (IsGone(kvp.Value)) continue;   // reclaimed by the scan in the next Reconcile
                kvp.Value.InternalUpdateView();
            }
        }

        protected virtual void LateUpdate()
        {
            foreach (var view in _viewsByEntity.Values)
            {
                if (IsGone(view)) continue;   // reclaimed by the scan in the next Reconcile
                view.InternalLateUpdateView();
            }
        }

        private void Reconcile()
        {
            _presentEntityVersions.Clear();

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
                TrySpawn(entity, frameRef, entityBehaviour);
            }
        }

        // Returns true if either (a) entity has no OwnerComponent (Owner-agnostic),
        // or (b) view reports its cached Owner matches the current frame's Owner.
        // EVU lives in xpTURN.Klotho.Runtime.Unity asmdef which has no reference to concrete view
        // assemblies (e.g. Brawler.View) — identity comparison is delegated to a virtual method on EntityView.
        /// <summary>
        /// Whether this entry's view was destroyed behind the updater's back — a reloaded scene
        /// invalidating an adopted scene view, or a game destroying one by hand.
        ///
        /// `== null` is Unity's overload, not a C# null test: a destroyed object's managed half is
        /// still there, which is also why the destroyed entry never announced itself. Calling a
        /// virtual method on it is legal, so nothing throws until the body reaches Unity API —
        /// LateUpdate does, through ApplyTransform, on every frame (IMP105 C-16). The Godot adapter
        /// answers the same question with GodotObject.IsInstanceValid.
        /// </summary>
        private static bool IsGone(EntityView view) => view == null;

        // Latched: the state persists for as long as the entry does, so an unlatched warning would
        // be one per frame per view. Not silent — the destruction is a game-side bug and the guards
        // below make it survivable, not visible.
        private void WarnExternallyDestroyedViewOnce()
        {
            if (_warnedExternallyDestroyedView) return;
            _warnedExternallyDestroyedView = true;
            Engine?.Logger?.KWarning($"[ViewLife] A view was destroyed outside the EntityViewUpdater. Reclaiming its entry. Destroy views through Cleanup() or let the entity die instead.");
        }

        private static bool OwnersMatch(EntityView view, EntityRef entity, Frame frame)
        {
            if (!frame.Has<OwnerComponent>(entity)) return true;

            int currentOwner = frame.GetReadOnly<OwnerComponent>(entity).OwnerId;
            return view.OwnerMatches(currentOwner);
        }

        private void TrySpawn(EntityRef entity, FrameRef frame, BindBehaviour behaviour)
        {
            long key = entity.ToId();

            // An active view for this exact (Index, Version) — only the Owner can still differ.
            // A Version mismatch cannot reach this branch any more: it is a different key, and the
            // two views are meant to coexist while the older one plays out its despawn grace.
            if (_viewsByEntity.TryGetValue(key, out var existing))
            {
                // Above OwnersMatch, and deliberately NOT a return: CollectPresent (our caller) runs
                // before DestroyStale, so returning here would leave the entity view-less until the
                // scan caught up a tick later. Falling through hands the slot over in this same tick.
                if (IsGone(existing))
                {
                    WarnExternallyDestroyedViewOnce();
                    TryUnregisterPlayerView(existing);
                    _viewsByEntity.Remove(key);
                    _despawnVerifiedTick.Remove(key);
                }
                else if (OwnersMatch(existing, entity, frame.Frame)) return;  // truly same entity, dedup
                else
                {

                    Engine?.Logger?.KDebug($"[ViewLife][Rebind] entity={entity.Index}, version={entity.Version}, viewType={existing.GetType().Name}, viewIID={existing.GetInstanceID()}");
                    existing.OnDeactivate();
                    TryUnregisterPlayerView(existing);
                    if (_factory != null) _factory.Destroy(existing);
                    _viewsByEntity.Remove(key);
                    _despawnVerifiedTick.Remove(key);
                }
            }

            // The same async spawn is already in flight for this exact entity.
            if (_pendingSpawnCounter.ContainsKey(key)) return;

            int spawnCounter = ++_versionCounter;
            _pendingSpawnCounter[key] = spawnCounter;
            SpawnViewAsync(entity, frame, behaviour, spawnCounter).Forget();
        }

        private async UniTaskVoid SpawnViewAsync(EntityRef entity, FrameRef frame, BindBehaviour behaviour, int spawnCounter)
        {
            long key = entity.ToId();

            ViewFlags flags = _factory.GetViewFlags(frame.Frame, entity);
            EntityView view = await _factory.CreateAsync(frame.Frame, entity, behaviour, flags);

            // Discard if the dispatch was invalidated (stale clear / teardown) by the time the async
            // completes. The Version no longer needs a separate check — it is part of the key.
            if (!_pendingSpawnCounter.TryGetValue(key, out int storedCounter)
                || storedCounter != spawnCounter)
            {
                if (view != null) _factory.Destroy(view);
                return;
            }
            _pendingSpawnCounter.Remove(key);

            // Factory refused the spawn. Saying nothing here is the same silent failure W-5 removed for
            // the unassigned factory: the entity stays collectable, so the next Reconcile dispatches the
            // same spawn again — forever, with no line to say why that entity has no visual. Brawler hit
            // exactly this loop (IMP105 C-11), and the fix there was game-side; this is the engine-side
            // half. Latched like the others: the state that produces it does not change tick to tick, so
            // an unlatched warning would be one per entity per tick (IMP105 C-19).
            if (view == null)
            {
                if (!_warnedFactoryRefusedSpawn)
                {
                    _warnedFactoryRefusedSpawn = true;
                    Engine?.Logger?.KWarning($"[ViewLife] Factory returned no view for entity={entity.Index} (version={entity.Version}) — it will have no visual and the spawn is retried every tick. Refuse it in TryGetBindBehaviour instead, or give ResolvePrefab a branch for it.");
                }
                return;
            }

            view.EntityRef = entity;
            view.Engine    = Engine;
            view.SetBindBehaviour(behaviour);
            // BindBehaviour is a plain overwrite because the factory's answer covers the whole enum —
            // nothing is lost. ViewFlags is a bitfield the factory only partly decides, so it merges
            // instead: assigning would drop the prefab's authoring (IMP103 V-3).
            view.SetViewFlags(_factory.ComposeViewFlags(view.ViewFlags, flags));
            _viewsByEntity[key] = view;

            // On pool reuse, OnInitialize is skipped but OnActivate is called every time.
            view.EnsureInitialized();
            view.InternalActivate(frame);

            TryRegisterPlayerView(entity, view, frame);
        }

        /// <summary>
        /// Destroys views whose entity is gone — immediately on the CSP path, after a render-clock
        /// grace on the snapshot path.
        ///
        /// The two paths differ because their render source does. A CSP view draws the Predicted
        /// frame, so its lifetime criterion and its render position are the same tick and immediate
        /// destruction is correct. A snapshot view draws the window the render clock points at, and
        /// that clock converges to <c>LastVerifiedTick - InterpolationDelayTicks</c> — so at the
        /// moment the Verified frame stops carrying the entity, the render is still `delay` ticks
        /// short of it. Destroying there discards the last `delay` ticks of the entity's motion, and
        /// it does so silently: the frames are in the ring, nothing reports a miss (IMP103).
        /// </summary>
        private void DestroyStale()
        {
            var verified  = Engine.VerifiedFrame;
            var predicted = Engine.PredictedFrame;

            // Invalidate Pending for entities that are gone from BOTH timelines — the in-flight
            // result is then discarded on completion. The presence snapshot cannot answer this: it
            // holds one Version per Index and the Predicted pass overwrites the Verified one, so a
            // pending (Index, Version) whose slot was reused would look stale while its entity is
            // still alive. Ask the frames with the reconstructed handle instead; Entities.IsAlive is
            // version-aware, which is the whole reason coexistence is safe here.
            _pendingStaleKeys.Clear();
            foreach (var kvp in _pendingSpawnCounter)
                if (!IsCollectableInEitherFrame(kvp.Key, verified.Frame, predicted.Frame))
                    _pendingStaleKeys.Add(kvp.Key);
            foreach (var key in _pendingStaleKeys)
                _pendingSpawnCounter.Remove(key);

            // One RenderClock read for the whole pass — the getter assembles a struct per access.
            int renderBaseTick = Engine.RenderClock.VerifiedBaseTick;

            _staleKeys.Clear();
            foreach (var kvp in _viewsByEntity)
            {
                var view = kvp.Value;

                // Destroyed from outside: stale by definition, and marking it here is what keeps the
                // other five loops' exposure down to a single tick instead of forever (IMP105 C-16).
                if (IsGone(view)) { _staleKeys.Add(kvp.Key); continue; }

                if (view.BindBehaviour == BindBehaviour.Verified)
                {
                    if (IsCarriedByVerified(view, verified.Frame))
                    {
                        // Present again (a rollback can resurrect it) — cancel any pending grace.
                        _despawnVerifiedTick.Remove(kvp.Key);
                        continue;
                    }

                    if (!_despawnVerifiedTick.TryGetValue(kvp.Key, out int despawnTick))
                    {
                        // First tick the Verified frame does not carry it. The entity's last live
                        // tick is <= verified.Tick - 1, so taking verified.Tick is the safe side by
                        // at most one tick. A frameless engine (-1) cannot start a grace.
                        if (verified.Frame == null) { _staleKeys.Add(kvp.Key); continue; }
                        despawnTick = verified.Tick;
                        _despawnVerifiedTick[kvp.Key] = despawnTick;

                        // The registry is NOT unbound here, deliberately. It maps a player to the view on
                        // screen, and a view inside its grace is still on screen — still interpolating,
                        // still the thing a camera should follow. Handing the slot over at this point cost
                        // more than it bought: the entry that comes back from a rollback (the Verified
                        // frame carries it again, above) has no way to re-register — Register is called
                        // from exactly one place, the end of SpawnViewAsync, which TrySpawn skips for a
                        // view that is still alive. So the player stayed unmapped for the rest of the
                        // match, and the game only ever heard the Unregistered half of the pair
                        // (IMP105 C-2). Nothing is lost by waiting: Unregister is instance-guarded, so a
                        // respawn that claims the slot first wins and this view's eventual unbind at the
                        // stale-destroy below is skipped.
                    }

                    if (renderBaseTick < despawnTick) continue;  // still inside the grace window
                    _staleKeys.Add(kvp.Key);
                    continue;
                }

                // CSP view — lifetime and render agree, so absence from the Predicted collection is
                // enough. Owner changes still mean "a different entity is here now".
                if (!_presentEntityVersions.TryGetValue(view.EntityRef.Index, out int presentVersion)
                    || presentVersion != view.EntityRef.Version
                    || (predicted.Frame != null && !OwnersMatch(view, view.EntityRef, predicted.Frame)))
                {
                    _staleKeys.Add(kvp.Key);
                }
            }

            foreach (var key in _staleKeys)
            {
                var view = _viewsByEntity[key];

                // Above the log line, not just above OnDeactivate: the interpolated arguments include
                // GetInstanceID(), which throws on a destroyed view whenever Debug logging is live.
                // The bookkeeping still runs — TryUnregisterPlayerView reads the cached owner, a
                // managed field, so a destroyed view can and must still be unbound (IMP105 C-16).
                if (IsGone(view))
                {
                    WarnExternallyDestroyedViewOnce();
                    TryUnregisterPlayerView(view);
                    _viewsByEntity.Remove(key);
                    _despawnVerifiedTick.Remove(key);
                    continue;
                }

                Engine?.Logger?.KDebug($"[ViewLife][StaleDestroy] entity={view.EntityRef.Index}, viewType={view.GetType().Name}, viewVersion={view.EntityRef.Version}, grace={_despawnVerifiedTick.ContainsKey(key)}, viewIID={view.GetInstanceID()}");
                view.OnDeactivate();
                TryUnregisterPlayerView(view);
                if (_factory != null) _factory.Destroy(view);
                else if (view != null) Destroy(view.gameObject);
                _viewsByEntity.Remove(key);
                _despawnVerifiedTick.Remove(key);
            }
            _staleKeys.Clear();
        }

        /// <summary>
        /// Whether the Verified frame still carries this view's entity.
        ///
        /// The presence snapshot is a positive-only fast path. It holds one Version per Index and the
        /// Predicted pass overwrites the Verified one, so a Version match proves the entity was
        /// collected this tick — and since Predicted runs ahead of Verified, alive there means alive
        /// here too — while a mismatch proves nothing at all. On a mismatch, ask the frame directly
        /// with the view's own handle. Keeping the fast path is what holds this method at one
        /// dictionary lookup in the steady state instead of a virtual factory call per view per tick.
        /// </summary>
        private bool IsCarriedByVerified(EntityView view, Frame verified)
        {
            var entity = view.EntityRef;
            if (_presentEntityVersions.TryGetValue(entity.Index, out int version) && version == entity.Version)
                return true;

            if (verified == null || !verified.Entities.IsAlive(entity)) return false;

            // Equivalent to what CollectPresent would have decided — aliveness alone is not the
            // criterion, the factory's ShouldRender / Owner answer is. TransformComponent is
            // deliberately NOT required: presence never required it, and the interpolator judges
            // that for itself.
            return _factory != null
                && _factory.TryGetBindBehaviour(verified, entity, out var behaviour)
                && behaviour == BindBehaviour.Verified;
        }

        // Whether either timeline would still collect this pending spawn's entity. Aliveness alone is
        // not the question — the presence snapshot this replaces was built through the factory, so an
        // entity the factory has stopped wanting must still invalidate the dispatch or a view appears
        // for something that should never have had one.
        //
        // The handle is reconstructed from the ToId() key rather than carried alongside the counter:
        // no extra storage, and EntityRef.FromId is ToId's inverse, so the bit layout stays in one place
        // (this used to inline the two shifts, which put a second copy of that knowledge here — IMP105 N-1).
        private bool IsCollectableInEitherFrame(long key, Frame verified, Frame predicted)
        {
            var entity = EntityRef.FromId(key);
            return IsCollectable(entity, verified) || IsCollectable(entity, predicted);
        }

        private bool IsCollectable(EntityRef entity, Frame frame)
            => frame != null
            && frame.Entities.IsAlive(entity)
            && _factory != null
            && _factory.TryGetBindBehaviour(frame, entity, out _);

        // Spawn-side hook (called once at the end of SpawnViewAsync, after InternalActivate).
        // Reads OwnerComponent from the spawn-decision frame and writes the cached owner on the view
        // so any of the three unbind sites can produce a stable unregister key.
        private void TryRegisterPlayerView(EntityRef entity, EntityView view, FrameRef frame)
        {
            if (_playerViews == null) return;
            var f = frame.Frame;
            if (f == null || !f.Has<OwnerComponent>(entity)) return;
            int ownerId = f.GetReadOnly<OwnerComponent>(entity).OwnerId;
            view.SetCachedOwner(ownerId);
            _playerViews.Register(ownerId, view);
        }

        // Unbind-side hook (called from TrySpawn Rebind / DestroyStale).
        // OwnerComponent may already be absent on the live frame at despawn time —
        // the view's cached owner is the stable unregister key.
        private void TryUnregisterPlayerView(EntityView view)
        {
            if (_playerViews == null) return;
            if (!view.TryGetCachedOwner(out int ownerId)) return;
            _playerViews.Unregister(ownerId, view);
            view.ClearCachedOwner();
        }
    }
}
