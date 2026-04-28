using System;
using System.Collections.Generic;
using UnityEngine;

using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using Cysharp.Threading.Tasks;

namespace Brawler
{
    public class BrawlerViewSync : MonoBehaviour
    {
        [SerializeField] private PlatformView[] _movingPlatforms;
        [SerializeField] private GameHUD _gameHUD;
        [SerializeField] private ResultScreen _resultScreen;

        [Header("Camera")]
        [SerializeField] private BrawlerCameraController _cameraController;

        [Header("VFX")]
        [SerializeField] private GameObject _trapVfxPrefab;
        [SerializeField] private GameObject _bombVfxPrefab;

        public event Action OnLocalCharacterSpawned;
        public event Action OnLocalCharacterDespawned;

        private KlothoEngine _engine;
        private EcsSimulation _simulation;
        private int _maxPlayers;
        private ILogger _logger;

        private bool _platformsAssigned;

        // After EVU creates CharacterView, holds the self-register result from OnActivate.
        // Referenced by camera follow and GameHUD when finding the local player's view.
        private readonly Dictionary<int, CharacterView> _characterViews = new();

        public void Initialize(KlothoEngine engine, EcsSimulation simulation, int maxPlayers, ILogger logger = null)
        {
            _engine = engine;
            _simulation = simulation;
            _maxPlayers = maxPlayers;
            _logger = logger;

            _platformsAssigned = false;

            engine.OnTickExecuted += OnTickExecuted;
            engine.OnSyncedEvent  += OnSyncedEvent;

            _gameHUD?.Initialize(engine);
            _resultScreen?.Initialize(engine);
        }

        public void Cleanup()
        {
            if (_engine != null)
            {
                _engine.OnTickExecuted -= OnTickExecuted;
                _engine.OnSyncedEvent  -= OnSyncedEvent;
            }

            _characterViews.Clear();
            _platformsAssigned = false;
            _engine = null;
            _simulation = null;
        }

        private void OnTickExecuted(int tick)
        {
            TryAssignPlatformEntities();
        }

        private void TryAssignPlatformEntities()
        {
            if (_platformsAssigned) return;
            if (_movingPlatforms == null || _movingPlatforms.Length == 0) return;
            if (_simulation == null) return;

            var frame = _simulation.Frame;

            int idx    = 0;
            var filter = frame.Filter<PlatformComponent, TransformComponent>();
            while (filter.Next(out var entity) && idx < _movingPlatforms.Length)
            {
                _movingPlatforms[idx]?.Initialize(_simulation, _engine);
                _movingPlatforms[idx]?.Assign(entity);
                idx++;
            }

            if (idx >= _movingPlatforms.Length)
                _platformsAssigned = true;
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

        // ── EVU self-register entry point (called from CharacterView.OnActivate) ──
        // Inherits Registry's OnLocalCharacterSpawned wiring — guarantees no regression in camera / GameHUD / BrawlerGameController.

        /// <summary>Called when CharacterView is activated by EVU — wires camera follow / HUD.</summary>
        public void RegisterCharacter(int playerId, CharacterView view)
        {
            if (view == null) return;
            if (_characterViews.TryGetValue(playerId, out var existing) && existing == view) return;

            _characterViews[playerId] = view;
            _gameHUD?.RegisterCharacterView(playerId, view);

            if (_engine != null && playerId == _engine.LocalPlayerId)
            {
                _cameraController?.SetFollowTarget(view.transform);
                OnLocalCharacterSpawned?.Invoke();
            }
        }

        /// <summary>Called when CharacterView is deactivated — releases camera follow / keeps HUD slot (e.g., shows stock 0).</summary>
        public void UnregisterCharacter(int playerId)
        {
            if (!_characterViews.Remove(playerId)) return;

            if (_engine != null && playerId == _engine.LocalPlayerId)
            {
                _cameraController?.ClearFollowTarget();
                OnLocalCharacterDespawned?.Invoke();
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }
}
