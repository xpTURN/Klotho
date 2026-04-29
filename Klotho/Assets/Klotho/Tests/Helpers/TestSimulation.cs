#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;

namespace xpTURN.Klotho.Helper.Tests
{
    internal class TestSimulation : ISimulation
    {
        private int _playerCount;
        public int CurrentTick { get; private set; }
        public long StateHash { get; set; } = 12345L;
        public int TickCallCount { get; private set; }

        /// <summary>
        /// When true, computes an input-based hash that differs per command type and player.
        /// When false (default), returns a fixed StateHash.
        /// </summary>
        public bool UseDeterministicHash { get; set; }

        // Input-based hash state (deterministic accumulation)
        private long _deterministicState;

        // Rollback support: per-tick state snapshots
        private readonly Dictionary<int, long> _stateSnapshots = new Dictionary<int, long>();

        public void Initialize() { CurrentTick = 0; _deterministicState = 0; }

        public void Tick(List<ICommand> commands)
        {
            // Save state before Tick for rollback restoration
            if (UseDeterministicHash)
                _stateSnapshots[CurrentTick] = _deterministicState;

            CurrentTick++;
            TickCallCount++;

            if (UseDeterministicHash)
            {
                // Accumulate deterministic hash from inputs (command order, type, and player; CurrentTick not used)
                for (int i = 0; i < commands.Count; i++)
                {
                    long cmdHash = (long)commands[i].CommandTypeId * 31 + commands[i].PlayerId * 97;
                    _deterministicState = _deterministicState * 6364136223846793005L + cmdHash + 1442695040888963407L;
                }
            }

            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i] is PlayerJoinCommand joinCmd)
                {
                    _playerCount++;
                    OnPlayerJoined(joinCmd.JoinedPlayerId, CurrentTick);
                }
            }
        }

        public void Rollback(int targetTick)
        {
            CurrentTick = targetTick;
            if (UseDeterministicHash)
            {
                if (_stateSnapshots.TryGetValue(targetTick, out long state))
                    _deterministicState = state;
                else
                    _deterministicState = 0; // Fallback to initial state (no snapshot exists for tick -1, etc.)
            }
        }

        public long GetStateHash() => UseDeterministicHash ? _deterministicState : StateHash;

        /// <summary>
        /// Saves the current state as a snapshot (for rollback restoration).
        /// Separate from the engine's SaveSnapshot; must be called explicitly in tests.
        /// Ideally called before Tick so state is saved automatically.
        /// Convenience mode: auto-saved inside Tick.
        /// </summary>
        public void SaveStateSnapshot()
        {
            if (UseDeterministicHash)
                _stateSnapshots[CurrentTick] = _deterministicState;
        }

        public void Reset()
        {
            CurrentTick = 0;
            TickCallCount = 0;
            _deterministicState = 0;
            _stateSnapshots.Clear();
        }

        public void RestoreFromFullState(byte[] stateData)
        {
            if (stateData != null && stateData.Length >= 8 && UseDeterministicHash)
                _deterministicState = BitConverter.ToInt64(stateData, 0);
        }

        public byte[] SerializeFullState()
        {
            if (UseDeterministicHash)
                return BitConverter.GetBytes(_deterministicState);
            return BitConverter.GetBytes(StateHash);
        }

        public (byte[] data, long hash) SerializeFullStateWithHash()
        {
            long hash = GetStateHash();
            return (SerializeFullState(), hash);
        }

        public void EmitSyncEvents() { }

        public event Action<int> OnPlayerJoinedNotification;

        public void OnPlayerJoined(int playerId, int tick)
        {
            OnPlayerJoinedNotification?.Invoke(playerId);
        }

        public void SetPlayerCount(int count) { _playerCount = count; }
    }
}
