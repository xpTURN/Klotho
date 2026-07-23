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

        /// <summary>Number of FullState serialization calls — lets tests detect cache re-serialization.</summary>
        public int SerializeCallCount { get; private set; }

        /// <summary>
        /// Optional hook invoked at the end of each Tick(commands) call. The engine sets up its
        /// internal event collector before Tick; tests can wire it into <see cref="EventRaiser"/>
        /// via reflection and raise events here for dispatch through the engine's event pipeline.
        /// </summary>
        public ISimulationEventRaiser EventRaiser { get; set; }
        public Action<int, ISimulationEventRaiser> OnAfterTickRaise { get; set; }

        /// <summary>
        /// When true, computes an input-based hash that differs per command type and player.
        /// When false (default), returns a fixed StateHash.
        /// </summary>
        public bool UseDeterministicHash { get; set; }

        // Input-based hash state (deterministic accumulation)
        private long _deterministicState;

        // Rollback support: per-tick state snapshots
        private readonly Dictionary<int, long> _stateSnapshots = new Dictionary<int, long>();

        // Controllable match-end state mirroring EcsSimulation's MatchEndStateComponent.
        // Round-trips through serialize/restore and rolls back per-tick so tests can drive the backstop
        // and the Pause-grace gate release.
        public bool MatchEnded;
        public int MatchWinnerId = -1;
        private readonly Dictionary<int, (bool ended, int winner)> _matchEndSnapshots
            = new Dictionary<int, (bool, int)>();
        private readonly TestMatchEndPayload _matchEndPayload = new TestMatchEndPayload();

        public bool IsMatchEndedState => MatchEnded;

        public IMatchEndEvent GetActiveMatchEnd()
        {
            if (!MatchEnded) return null;
            _matchEndPayload.Winner = MatchWinnerId;
            return _matchEndPayload;
        }

        private sealed class TestMatchEndPayload : IMatchEndEvent
        {
            public int Winner;
            public int WinnerPlayerId => Winner;
            public ECS.FixedString32 Reason => default;
        }

        public void Initialize() { CurrentTick = 0; _deterministicState = 0; }

        public void Tick(List<ICommand> commands)
        {
            // Save state before Tick for rollback restoration
            if (UseDeterministicHash)
                _stateSnapshots[CurrentTick] = _deterministicState;
            _matchEndSnapshots[CurrentTick] = (MatchEnded, MatchWinnerId);

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

            OnAfterTickRaise?.Invoke(CurrentTick, EventRaiser);
        }

        public int GetNearestRollbackTick(int targetTick)
        {
            if (targetTick < 0)
                return -1;
            // Stateless mode: Rollback only rewinds CurrentTick — any tick is restorable.
            if (!UseDeterministicHash)
                return targetTick;
            int best = -1;
            foreach (var kvp in _stateSnapshots)
            {
                if (kvp.Key <= targetTick && kvp.Key > best)
                    best = kvp.Key;
            }
            return best;
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
            // Restore match-end state at the rollback target (pre-end tick → not ended again).
            if (_matchEndSnapshots.TryGetValue(targetTick, out var me))
                (MatchEnded, MatchWinnerId) = me;
            else
                (MatchEnded, MatchWinnerId) = (false, -1);
        }

        public long GetStateHash() => UseDeterministicHash ? _deterministicState : StateHash;

        /// <summary>
        /// Number of engine-triggered <see cref="SaveSnapshot"/> calls — distinguishes the
        /// engine save trigger from the Tick-internal auto-save in tests.
        /// </summary>
        public int EngineSaveSnapshotCallCount { get; private set; }

        /// <summary>
        /// ISimulation per-tick save trigger — the engine calls this every tick.
        /// Stateless mode saves nothing (Rollback rewinds CurrentTick only, so any tick is restorable).
        /// </summary>
        public void SaveSnapshot()
        {
            EngineSaveSnapshotCallCount++;
            SaveStateSnapshot();
        }

        /// <summary>
        /// Saves the current state as a snapshot (for rollback restoration).
        /// Also auto-saved inside Tick so tests driving Tick directly keep their history;
        /// the engine-triggered SaveSnapshot writes the same key/value — idempotent.
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
            MatchEnded = false;
            MatchWinnerId = -1;
            _matchEndSnapshots.Clear();
        }

        public void RestoreFromFullState(byte[] stateData)
        {
            if (stateData != null && stateData.Length >= 8 && UseDeterministicHash)
                _deterministicState = BitConverter.ToInt64(stateData, 0);
            // Match-end state round-trips (mirrors MatchEndStateComponent in full state),
            // so a resync into an ended timeline restores Ended=true → drives the fire-forward backstop.
            // Restore is authoritative: an 8-byte (non-ended) snapshot clears any stale flag.
            bool footerPresent = stateData != null && stateData.Length >= 13 && stateData[8] != 0;
            MatchEnded = footerPresent;
            MatchWinnerId = footerPresent ? BitConverter.ToInt32(stateData, 9) : -1;
            // Timeline invariant: drop pre-restore history so stale ticks are not
            // exposed via GetNearestRollbackTick (mirrors EcsSimulation's ring clear on restore).
            _stateSnapshots.Clear();
            _matchEndSnapshots.Clear();
        }

        public byte[] SerializeFullState()
        {
            SerializeCallCount++;
            long stateWord = UseDeterministicHash ? _deterministicState : StateHash;
            // Default snapshot stays 8 bytes (state hash only). The match-end footer ([8] ended,
            // [9..13) winner) is appended ONLY when ended, so it round-trips into a resync without
            // changing the baseline snapshot size; RestoreFromFullState reads it when present.
            if (!MatchEnded)
                return BitConverter.GetBytes(stateWord);
            var buf = new byte[13];
            BitConverter.GetBytes(stateWord).CopyTo(buf, 0);
            buf[8] = 1;
            BitConverter.GetBytes(MatchWinnerId).CopyTo(buf, 9);
            return buf;
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
