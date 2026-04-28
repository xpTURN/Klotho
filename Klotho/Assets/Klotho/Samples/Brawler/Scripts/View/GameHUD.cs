using UnityEngine;
using TMPro;

using xpTURN.Klotho.Core;

namespace Brawler
{
    /// <summary>
    /// In-game HUD: shows time limit, per-player stock count and knockback values.
    ///
    /// Wiring:
    ///   - _characterViews : assign CharacterView array sized by player count (Inspector)
    ///   - _timerLabel      : remaining time text (TMP_Text)
    ///   - _playerPanels    : assign PlayerHUDPanel array sized by player count (Inspector)
    ///
    /// The timer is updated via Engine.OnSyncedEvent(RoundTimerEvent).
    /// Stock and knockback are updated by reading the CharacterView cache on each OnTickExecuted.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        [System.Serializable]
        public struct PlayerHUDPanel
        {
            public TMP_Text StockLabel;
            public TMP_Text KnockbackLabel;
        }

        [Header("References")]
        [SerializeField] private TMP_Text         _timerLabel;
        [SerializeField] private PlayerHUDPanel[] _playerPanels;

        private CharacterView[] _characterViews;

        private KlothoEngine _engine;

        public void Initialize(KlothoEngine engine)
        {
            if (_engine != null)
            {
                _engine.OnTickExecuted -= OnTickExecuted;
                _engine.OnSyncedEvent  -= OnSyncedEvent;
            }

            _engine = engine;

            if (_engine != null)
            {
                _engine.OnTickExecuted += OnTickExecuted;
                _engine.OnSyncedEvent  += OnSyncedEvent;
            }
        }

        public void RegisterCharacterView(int playerId, CharacterView view)
        {
            if (_characterViews == null || (uint)playerId >= (uint)_characterViews.Length)
            {
                int size = playerId + 1;
                var arr = new CharacterView[size];
                if (_characterViews != null)
                    System.Array.Copy(_characterViews, arr, _characterViews.Length);
                _characterViews = arr;
            }
            _characterViews[playerId] = view;
        }

        private void OnDestroy()
        {
            if (_engine == null) return;
            _engine.OnTickExecuted -= OnTickExecuted;
            _engine.OnSyncedEvent  -= OnSyncedEvent;
        }

        // ────────────────────────────────────────────
        // Each tick: update stock & knockback
        // ────────────────────────────────────────────
        private void OnTickExecuted(int tick)
        {
            if (_characterViews == null || _playerPanels == null) return;

            int count = Mathf.Min(_characterViews.Length, _playerPanels.Length);
            for (int i = 0; i < count; i++)
            {
                var view  = _characterViews[i];
                var panel = _playerPanels[i];

                if (view == null) continue;

                if (panel.StockLabel != null)
                    panel.StockLabel.SetText($"Stock: {view.StockCount}");

                if (panel.KnockbackLabel != null)
                    panel.KnockbackLabel.SetText($"{view.KnockbackPower}%");
            }
        }

        // ────────────────────────────────────────────
        // Synced event: update timer
        // ────────────────────────────────────────────
        private void OnSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is RoundTimerEvent timer && _timerLabel != null)
            {
                int m = timer.RemainingSeconds / 60;
                int s = timer.RemainingSeconds % 60;
                _timerLabel.SetText($"{m:D2}:{s:D2}");
            }
        }
    }
}
