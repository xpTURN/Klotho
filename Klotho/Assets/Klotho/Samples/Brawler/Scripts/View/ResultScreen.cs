using UnityEngine;
using TMPro;

using xpTURN.Klotho.Core;

namespace Brawler
{
    /// <summary>
    /// Game over result screen.
    /// When GameOverEvent is received from Engine.OnSyncedEvent, activates the panel and displays the result.
    /// </summary>
    public class ResultScreen : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject _panel;
        [SerializeField] private TMP_Text   _resultLabel;
        [SerializeField] private TMP_Text   _reasonLabel;

        private KlothoEngine _engine;

        private void Start()
        {
            if (_panel != null)
                _panel.SetActive(false);
        }

        public void Initialize(KlothoEngine engine)
        {
            if (_engine != null)
                _engine.OnSyncedEvent -= OnSyncedEvent;

            _engine = engine;

            if (_engine != null)
                _engine.OnSyncedEvent += OnSyncedEvent;
        }

        private void OnDestroy()
        {
            if (_engine != null)
                _engine.OnSyncedEvent -= OnSyncedEvent;
        }

        private void OnSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is not GameOverEvent over) return;

            if (_panel != null)
                _panel.SetActive(true);

            int localPlayerId = _engine.LocalPlayerId;
            bool isWinner     = over.WinnerPlayerId == localPlayerId;

            if (_resultLabel != null)
                _resultLabel.text = isWinner ? "YOU WIN!" : "YOU LOSE";

            if (_reasonLabel != null)
            {
                string reason = over.Reason.ToString();
                if (reason == "timeout")
                    _reasonLabel.text = "TIME UP";
                else if (over.WinnerPlayerId < 0)
                    _reasonLabel.text = "DRAW";
                else
                    _reasonLabel.text = $"Player {over.WinnerPlayerId + 1} wins";
            }
        }
    }
}
