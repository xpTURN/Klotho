using System;
using UnityEngine;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using Cysharp.Threading.Tasks;

namespace Brawler
{
    public class BrawlerViewSync : MonoBehaviour
    {
        private PlatformView[] _movingPlatforms;
        
        [SerializeField] private GameHUD _gameHUD;
        [SerializeField] private ResultScreen _resultScreen;

        [field: Header("Camera")]
        [SerializeField] private BrawlerCameraController _cameraController;

        [field: Header("VFX")]
        [SerializeField] private GameObject _trapVfxPrefab;
        [SerializeField] private GameObject _bombVfxPrefab;

        public event Action OnLocalCharacterSpawned;
        public event Action OnLocalCharacterDespawned;

        private IKlothoEngine _engine;
        private EcsSimulation _simulation;
        private EntityViewUpdater _evu;

        public void Initialize(IKlothoEngine engine, EcsSimulation simulation, EntityViewUpdater evu)
        {
            _engine = engine;
            _simulation = simulation;
            _evu = evu;

            // Inactive must be included: the factory deactivates an adopted platform in Destroy,
            // so from the second session on this would find nothing and the platform never returns.
            _movingPlatforms = FindObjectsByType<PlatformView>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            // FindObjectsByType promises no order, and the factory hands instances out from the tail
            // against entities walked in dense index order — so WHICH scene object adopts WHICH platform
            // entity could differ between runs and between peers. Nothing desyncs (this is view-only),
            // but the moment platforms carry their own authoring — a mesh, a scale, a VFX — the visual
            // and the collision geometry stop agreeing. Sorting by name fixes the pairing to something
            // the scene author controls; descending, because the factory pops from the tail, so the
            // first entity gets the first name (IMP105 C-12).
            //
            // Note the scan is process-wide: an additively loaded scene that also holds PlatformViews
            // contributes here too. That is intended for the stage view scene, which is exactly how the
            // platforms arrive, and there is no cheap way to tell the two cases apart from here.
            System.Array.Sort(_movingPlatforms, (a, b) => string.CompareOrdinal(b.name, a.name));

            // Hand the scene-placed platforms to the factory before the first Reconcile. The factory is
            // a ScriptableObject and cannot hold scene references itself, so this runs every session —
            // a reloaded scene leaves the previous array pointing at destroyed objects.
            if (_evu != null && _evu.Factory is BrawlerEntityViewFactory factory)
                factory.BindPlacedPlatforms(_movingPlatforms);

            engine.OnSyncedEvent  += OnSyncedEvent;

            if (_evu != null && _evu.PlayerViews != null)
            {
                _evu.PlayerViews.OnViewRegistered        += HandleViewRegistered;
                _evu.PlayerViews.OnLocalViewRegistered   += HandleLocalViewRegistered;
                _evu.PlayerViews.OnLocalViewUnregistered += HandleLocalViewUnregistered;
            }

            _gameHUD?.Initialize(engine);
            _resultScreen?.Initialize(engine);
        }

        public void Cleanup()
        {
            if (_engine != null)
                _engine.OnSyncedEvent -= OnSyncedEvent;

            if (_evu != null && _evu.PlayerViews != null)
            {
                _evu.PlayerViews.OnViewRegistered        -= HandleViewRegistered;
                _evu.PlayerViews.OnLocalViewRegistered   -= HandleLocalViewRegistered;
                _evu.PlayerViews.OnLocalViewUnregistered -= HandleLocalViewUnregistered;
            }

            _engine = null;
            _simulation = null;
            _evu = null;
        }

        private void HandleViewRegistered(int playerId, EntityView view)
        {
            if (view is CharacterView ch)
                _gameHUD?.RegisterCharacterView(playerId, ch);
        }

        private void HandleLocalViewRegistered(EntityView view)
        {
            if (view is CharacterView ch)
            {
                _cameraController?.SetFollowTarget(ch.transform);
                OnLocalCharacterSpawned?.Invoke();
            }
        }

        private void HandleLocalViewUnregistered(EntityView view)
        {
            _cameraController?.ClearFollowTarget();
            OnLocalCharacterDespawned?.Invoke();
        }

        // ── Synced event: fires exactly once at the verified point ──
        // Events that tolerate delay (like Trap/Bomb) are promoted to Synced so duplicate dispatch is naturally blocked.

        private void OnSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is TrapTriggeredEvent trap)
                OnTrapTriggered(trap);
            else if (evt is ItemPickedUpEvent pickup)
                OnItemPickedUp(pickup);
        }

        private void OnTrapTriggered(TrapTriggeredEvent evt)
        {
            var pos = new Vector3(evt.TrapPosition.x.ToFloat(), 0f, evt.TrapPosition.y.ToFloat());
            SpawnVfx(_trapVfxPrefab, pos).Forget();
        }

        private void OnItemPickedUp(ItemPickedUpEvent evt)
        {
            if (evt.ItemType == 2) // Bomb
            {
                var pos = new Vector3(evt.ItemPosition.x.ToFloat(), 0f, evt.ItemPosition.y.ToFloat());
                SpawnVfx(_bombVfxPrefab, pos).Forget();
            }
        }

        private static async UniTaskVoid SpawnVfx(GameObject prefab, Vector3 position)
        {
            if (prefab == null) return;
            var results = await InstantiateAsync(prefab, position, Quaternion.identity).ToUniTask();
            var ps = results[0].GetComponent<ParticleSystem>();
            if (ps != null && !ps.main.loop)
                Destroy(results[0], ps.main.duration + ps.main.startLifetime.constantMax);
            else
                Destroy(results[0], 3f);
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }
}
