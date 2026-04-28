using TMPro;
using UnityEngine;
using UnityEngine.UI;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace Brawler
{
    public class GameMenu : MonoBehaviour
    {
        public enum ActionType
        {
            CreateRoom,
            JoinRoom,
            Ready,
            Playing,
            Reconnect,
        }

        [SerializeField] public Button _btnAction;
        [SerializeField] public Button _btnReplay;
        [SerializeField] public Button _btnSpectator;
        [SerializeField] TMP_Text _textAction;
        [SerializeField] TMP_InputField _ipAddress;
        [SerializeField] public Button _btnHost;
        [SerializeField] public Button _btnGuest;
        [SerializeField] TMP_Text _textMode;
        [SerializeField] TMP_Text _textState;
        [SerializeField] TMP_Text _textTick;
        [SerializeField] TMP_Text _textPlayers;
        [SerializeField] TMP_Text _textEntities;
        [SerializeField] TMP_Text _textReady;
        [SerializeField] TMP_Text _textPhase;

        public ActionType CurrentAction { get; private set; }
        public string IpAddress { get { return _ipAddress.text; } set { _ipAddress.text = value; } }
        public bool IsHost { get; set; }
        public KlothoState State { get; set; }
        public SessionPhase Phase { get; set; }
        public int Tick { get; set; }
        public int Players { get; set; }
        public int Entities { get; set; }
        public bool IsAllReady { get; set; }
        public string ReconnectStatus { get; set; }

        bool _prevIsHost;
        KlothoState _prevState;
        SessionPhase _prevPhase;
        int _prevTick;
        int _prevPlayers;
        int _prevEntities;
        bool _prevIsAllReady;
        string _prevReconnectStatus;

        public void SetActionType(ActionType type)
        {
            CurrentAction = type;

            switch(type)
            {
            case ActionType.CreateRoom:
                _textAction.SetText($"Create Room");
                break;
            case ActionType.JoinRoom:
                _textAction.SetText($"Join Room");
                break;
            case ActionType.Ready:
                _textAction.SetText($"Ready");
                break;
            case ActionType.Playing:
                _textAction.SetText($"Stop");
                break;
            case ActionType.Reconnect:
                _textAction.SetText($"Cancel");
                break;
            }
        }

        void Update()
        {
            if (_prevIsHost != IsHost)
            {
                _prevIsHost = IsHost;
                if (IsHost)
                    _textMode.SetText($"Mode: Host");
                else
                    _textMode.SetText($"Mode: Guest");
            }

            if (_prevState != State)
            {
                _prevState = State;
                _textState.SetText($"State: {State}");
            }

            // if (_prevTick != Tick)
            // {
            //     _prevTick = Tick;
            //     _textTick.SetText($"Tick: {Tick}");
            // }

            if (_prevPlayers != Players)
            {
                _prevPlayers = Players;
                _textPlayers.SetText($"Players: {Players}");
            }

            if (_prevEntities != Entities)
            {
                _prevEntities = Entities;
                _textEntities.SetText($"Entities: {Entities}");
            }

            if (_prevIsAllReady != IsAllReady)
            {
                _prevIsAllReady = IsAllReady;
                _textReady.SetText($"IsAllReady: {IsAllReady}");
            }

            if (_prevPhase != Phase || _prevReconnectStatus != ReconnectStatus)
            {
                _prevPhase = Phase;
                _prevReconnectStatus = ReconnectStatus;
                if (!string.IsNullOrEmpty(ReconnectStatus))
                    _textPhase.SetText($"Phase: {Phase} ({ReconnectStatus})");
                else
                    _textPhase.SetText($"Phase: {Phase}");
            }
        }
    }
}