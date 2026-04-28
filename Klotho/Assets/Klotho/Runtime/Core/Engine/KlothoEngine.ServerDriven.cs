using System;
using System.Collections.Generic;

using ZLogger;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Server-mode-only cache (cast on Initialize).
        private ServerNetworkService _serverNetwork;

        /// <summary>
        /// Server-mode tick loop (section 3.3).
        /// Fixed-interval tick execution based on accumulator.
        /// Input collection → simulation → hash calculation → verified broadcast → snapshot save → cleanup.
        /// </summary>
        private const int MAX_TICKS_PER_UPDATE = 3;

        private void UpdateServerTick(float deltaTime)
        {
            // Cache ServerNetworkService on first call.
            if (_serverNetwork == null)
                _serverNetwork = _serverDrivenNetwork as ServerNetworkService;

            if (_consumePendingDeltaTime)
            {
                _consumePendingDeltaTime = false;
                _logger?.ZLogDebug($"[KlothoEngine] Pending deltaTime consumed: {deltaTime * 1000f:F1}ms dropped at tick={CurrentTick}");
            }
            else
            {
                _accumulator += deltaTime * 1000f;
            }

            // Clamp accumulator upper bound (prevents burst from GC pauses, etc.).
            float maxAccumulator = _simConfig.TickIntervalMs * MAX_TICKS_PER_UPDATE;
            if (_accumulator > maxAccumulator)
            {
                float dropped = _accumulator - maxAccumulator;
                _accumulator = maxAccumulator;
                _logger?.ZLogWarning($"[KlothoEngine][SD] ServerTick: Accumulator clamped: {dropped:F1}ms dropped ({dropped / _simConfig.TickIntervalMs:F1} ticks skipped)");
            }

            while (_accumulator >= _simConfig.TickIntervalMs)
            {
                _accumulator -= _simConfig.TickIntervalMs;

                ExecuteServerTick();
            }
        }

        /// <summary>
        /// Executes a single server tick (section 3.3 ExecuteServerTick).
        /// </summary>
        private void ExecuteServerTick()
        {
            var inputCollector = _serverNetwork.InputCollector;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 1. Collect inputs (apply Hard Tolerance).
            inputCollector.BeginTick(CurrentTick, now);
            var commands = inputCollector.CollectTickInputs(CurrentTick);

            // Also store commands in InputBuffer (for compatibility with CleanupOldData, HasCommand, etc.).
            for (int i = 0; i < commands.Count; i++)
                _inputBuffer.AddCommand(commands[i]);

            // 2. Record replay (optional).
            if (_replaySystem.IsRecording)
                _replaySystem.RecordTick(CurrentTick, commands);

            // 3. Run simulation.
            _logger?.ZLogDebug($"[KlothoEngine][SD] ServerTick: CurrentTick={CurrentTick}, frame.Tick before={_simulation.CurrentTick}, cmds={commands.Count}");
            _eventCollector.BeginTick(CurrentTick);
            commands.Sort(s_commandComparer);
            _simulation.Tick(commands);
            _logger?.ZLogDebug($"[KlothoEngine][SD] ServerTick: frame.Tick after={_simulation.CurrentTick}");

            // Collect events (no-op for NullEventCollector).
            _eventBuffer.ClearTick(CurrentTick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(CurrentTick, _eventCollector.Collected[ei]);

            // 4. Calculate state hash.
            long stateHash = _simulation.GetStateHash();
            _logger?.ZLogDebug($"[KlothoEngine][SD] Hash: tick={CurrentTick + 1}, hash=0x{stateHash:X16}");

            // 5. Broadcast verified message — send _frame.Tick(=CurrentTick+1) to match the client hash.
            //    Command Tick fields match CurrentTick so they are correctly looked up in the client InputBuffer.
            _serverNetwork.BroadcastVerifiedState(CurrentTick + 1, commands, stateHash);

            // 6. Save snapshot — not needed in SD server mode (Late Join/Reconnect FullState
            //    serializes the current simulation state on the spot, so old snapshots are not used).
            //    SaveSnapshot is only required in rollback mode (CSP).
            if (_simConfig.MaxRollbackTicks > 0)
                SaveSnapshot(CurrentTick);

            // Verified chain: server ticks are always verified.
            _lastVerifiedTick = CurrentTick;
            OnFrameVerified?.Invoke(CurrentTick);

            CurrentTick++;

            int executedTick = CurrentTick - 1;
            OnTickExecuted?.Invoke(executedTick);
            _viewCallbacks?.OnTickExecuted(executedTick);
            OnTickExecutedWithState?.Invoke(executedTick, FrameState.Verified);

            // Server does not need event dispatch (NullEventCollector).
            // DispatchTickEvents(executedTick, FrameState.Verified);

            // 7. Post-processing: CleanupOldData (server-mode-specific cleanup criteria).
            CleanupServerData();
        }

        /// <summary>
        /// Server-mode-only data cleanup.
        /// Cleans up only past data based on retentionFloor (= CurrentTick - retentionTicks).
        /// </summary>
        /// <remarks>
        /// cleanupTick must always be a past value. Including a future tick (e.g., a peer's LastAckedTick)
        /// as a cleanup boundary causes CleanupBefore to delete recently accepted near-future inputs,
        /// resulting in them being replaced with EmptyCommands at CollectTickInputs time.
        /// Cleanup of _inputs/_inputBuffer must use only retentionFloor as the sole criterion.
        /// </remarks>
        private void CleanupServerData()
        {
            int retentionTicks = _simConfig.ServerSnapshotRetentionTicks;
            if (retentionTicks <= 0)
                retentionTicks = (1000 / Math.Max(1, _simConfig.TickIntervalMs)) * 10; // TickRate x 10

            // retentionFloor: base tick going as far back as the retention window.
            // cleanupTick: pushed further into the past by the safety margin (CLEANUP_MARGIN_TICKS) → always a past value.
            // NOTE: do not include future ticks (e.g., LastAckedTick) in the cleanup boundary — doing so deletes near-future inputs.
            int retentionFloor = CurrentTick - retentionTicks;
            int cleanupTick = retentionFloor - CLEANUP_MARGIN_TICKS;

            if (cleanupTick > 0)
            {
                _inputBuffer.ClearBefore(cleanupTick);
                _networkService?.ClearOldData(cleanupTick);
            }

            // Flush the transport send queue.
            _networkService?.FlushSendQueue();
        }
    }
}
