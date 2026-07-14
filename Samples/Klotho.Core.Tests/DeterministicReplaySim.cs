using System;
using System.Collections.Generic;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Deterministic simulation used to validate replay fidelity.
    ///
    /// Ports the input-accumulation hash formula from Unity's <c>TestSimulation</c> verbatim (so hash
    /// semantics match across both suites), and adds a **per-tick post-tick hash capture dictionary** for
    /// judging replay fidelity.
    ///
    /// Capture rules:
    ///  - key = <see cref="CurrentTick"/> on entry to <see cref="Tick"/> (engine tick T); value = the hash after commands are accumulated.
    ///  - last-write-wins — both the original run and re-simulation call <see cref="Tick"/> again, so the corrected value naturally remains.
    ///  - the capture dictionary (<c>_tickHashCapture</c>) is **separate** from the rollback snapshots (<c>_stateSnapshots</c>)
    ///    and is **not cleared** by <see cref="RestoreFromFullState"/> or <see cref="Rollback"/>
    ///    (needed to compare against pre-reset hashes after a corrective reset). Only <see cref="Initialize"/> resets it.
    /// </summary>
    internal sealed class DeterministicReplaySim : ISimulation
    {
        public int CurrentTick { get; private set; }
        public int TickCallCount { get; private set; }

        // Input-based deterministic hash state (identical accumulation to Unity TestSimulation).
        private long _deterministicState;

        // Rollback support: pre-tick state snapshots. Cleared on restore/reset (timeline invariant).
        private readonly Dictionary<int, long> _stateSnapshots = new Dictionary<int, long>();

        // Verification capture: post-tick hash per tick. NOT cleared on restore/rollback.
        private readonly Dictionary<int, long> _tickHashCapture = new Dictionary<int, long>();

        /// <summary>Per-tick post-tick hash (for record/replay comparison, last-write-wins).</summary>
        public IReadOnlyDictionary<int, long> TickHashCapture => _tickHashCapture;

        public void Initialize()
        {
            CurrentTick = 0;
            _deterministicState = 0;
            _stateSnapshots.Clear();
            _tickHashCapture.Clear();
        }

        public void Tick(List<ICommand> commands)
        {
            int t = CurrentTick;                        // engine tick being executed
            _stateSnapshots[t] = _deterministicState;   // pre-tick snapshot (rollback)

            CurrentTick++;
            TickCallCount++;

            // Accumulate deterministic hash from inputs (command order, type, player; tick not used).
            for (int i = 0; i < commands.Count; i++)
            {
                long cmdHash = (long)commands[i].CommandTypeId * 31 + commands[i].PlayerId * 97;
                _deterministicState = _deterministicState * 6364136223846793005L + cmdHash + 1442695040888963407L;
            }

            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i] is PlayerJoinCommand joinCmd)
                    OnPlayerJoined(joinCmd.JoinedPlayerId, CurrentTick);
            }

            // Verification capture: post-tick hash keyed by the executed tick (last-write-wins).
            _tickHashCapture[t] = _deterministicState;
        }

        public int GetNearestRollbackTick(int targetTick)
        {
            if (targetTick < 0) return -1;
            int best = -1;
            foreach (var kvp in _stateSnapshots)
                if (kvp.Key <= targetTick && kvp.Key > best) best = kvp.Key;
            return best;
        }

        public void Rollback(int targetTick)
        {
            CurrentTick = targetTick;
            _deterministicState = _stateSnapshots.TryGetValue(targetTick, out long s) ? s : 0;
            // _tickHashCapture intentionally untouched.
        }

        public void SaveSnapshot() => _stateSnapshots[CurrentTick] = _deterministicState;

        public long GetStateHash() => _deterministicState;

        public void Reset()
        {
            CurrentTick = 0;
            TickCallCount = 0;
            _deterministicState = 0;
            _stateSnapshots.Clear();
            // _tickHashCapture kept — only Initialize clears it.
        }

        public void RestoreFromFullState(byte[] stateData)
        {
            if (stateData != null && stateData.Length >= 8)
                _deterministicState = BitConverter.ToInt64(stateData, 0);
            // Timeline invariant: drop rollback history (mirrors EcsSimulation ring clear on restore).
            _stateSnapshots.Clear();
            // _tickHashCapture intentionally NOT cleared (needed to compare against pre-reset hashes after a corrective reset).
        }

        public byte[] SerializeFullState() => BitConverter.GetBytes(_deterministicState);

        public (byte[] data, long hash) SerializeFullStateWithHash()
            => (SerializeFullState(), _deterministicState);

        public void EmitSyncEvents() { }

        public event Action<int> OnPlayerJoinedNotification;

        public void OnPlayerJoined(int playerId, int tick) => OnPlayerJoinedNotification?.Invoke(playerId);
    }
}
