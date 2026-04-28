using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.State;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Replay system
        private ReplaySystem _replaySystem;
        private bool _isReplayMode;

        /// <summary>
        /// Replay system instance.
        /// </summary>
        public IReplaySystem ReplaySystem => _replaySystem;

        /// <summary>
        /// Whether the engine is currently in replay playback mode.
        /// </summary>
        public bool IsReplayMode => _isReplayMode;

        #region Replay Methods

        /// <summary>
        /// Starts replay playback.
        /// </summary>
        public void StartReplay(IReplayData replayData)
        {
            if (replayData == null)
            {
                _logger?.ZLogError($"[KlothoEngine][Replay] Cannot start replay: null replay data");
                return;
            }

            _isReplayMode = true;
            _randomSeed = replayData.Metadata.RandomSeed;

            // Reset state
            CurrentTick = 0;
            _lastVerifiedTick = -1;
            _accumulator = 0;
            _inputBuffer.Clear();

            // Initialize simulation with the replay seed
            _simulation.Initialize();

            // Restore initial state snapshot - via RestoreFromFullState instead of OnInitializeWorld
            var snapshot = replayData.Metadata.InitialStateSnapshot;
            if (snapshot == null || snapshot.Length == 0)
                throw new InvalidDataException(
                    "[Replay] InitialStateSnapshot missing - corrupted file or snapshot was not injected during recording");
            _simulation.RestoreFromFullState(snapshot);

            // Save initial snapshot
            SaveSnapshot(0);

            // Load replay
            _replaySystem.Load(replayData, _logger);
            _replaySystem.OnTickPlayed += HandleReplayTick;
            _replaySystem.OnPlaybackFinished += HandleReplayFinished;

            State = KlothoState.Running;
            _replaySystem.Play();

            // Semantic symmetry with the normal start path - game code guards live-only behavior with IsReplayMode
            _viewCallbacks?.OnGameStart(this);
            OnGameStart?.Invoke();

            _logger?.ZLogInformation($"[KlothoEngine][Replay] started: {replayData.Metadata.TotalTicks} ticks, {replayData.Metadata.DurationMs}ms");
        }

        /// <summary>
        /// Starts replay from a file.
        /// </summary>
        public void StartReplayFromFile(string filePath)
        {
            _replaySystem.LoadFromFile(filePath);
            var replayData = _replaySystem.CurrentReplayData;

            if (replayData != null)
            {
                StartReplay(replayData);
            }
        }

        /// <summary>
        /// Stops replay playback.
        /// </summary>
        public void StopReplay()
        {
            if (!_isReplayMode)
                return;

            _replaySystem.Stop();
            _replaySystem.OnTickPlayed -= HandleReplayTick;
            _replaySystem.OnPlaybackFinished -= HandleReplayFinished;

            _isReplayMode = false;
            State = KlothoState.Finished;

            _logger?.ZLogInformation($"[KlothoEngine][Replay] stopped");
        }

        /// <summary>
        /// Pauses replay playback.
        /// </summary>
        public void PauseReplay()
        {
            if (_isReplayMode)
            {
                _replaySystem.Pause();
                State = KlothoState.Paused;
            }
        }

        /// <summary>
        /// Resumes replay playback.
        /// </summary>
        public void ResumeReplay()
        {
            if (_isReplayMode && State == KlothoState.Paused)
            {
                _replaySystem.Resume();
                State = KlothoState.Running;
            }
        }

        /// <summary>
        /// Sets the replay playback speed.
        /// </summary>
        public void SetReplaySpeed(ReplaySpeed speed)
        {
            _replaySystem.Speed = speed;
        }

        /// <summary>
        /// Seeks to a specific tick in the replay.
        /// </summary>
        public void SeekReplay(int tick)
        {
            if (!_isReplayMode)
                return;

            // Find the snapshot closest to the target tick
            int startTick = 0;
            if (_simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSim)
            {
                int nearestTick = ecsSim.GetNearestSnapshotTick(tick);
                if (nearestTick >= 0)
                    startTick = nearestTick;
            }
            else if (_snapshotManager is RingSnapshotManager ringMgr)
            {
                var nearest = ringMgr.GetNearestSnapshot(tick);
                if (nearest != null)
                    startTick = nearest.Tick;
            }

            _simulation.Rollback(startTick);

            // Re-simulate from the nearest snapshot up to the target tick
            CurrentTick = startTick;
            var replayData = _replaySystem.CurrentReplayData;

            while (CurrentTick < tick && CurrentTick <= replayData.Metadata.TotalTicks)
            {
                var commands = replayData.GetCommandsForTick(CurrentTick);
                _tickCommandsCache.Clear();
                for (int i = 0; i < commands.Count; i++)
                    _tickCommandsCache.Add(commands[i]);
                _simulation.Tick(_tickCommandsCache);

                SaveSnapshot(CurrentTick);

                CurrentTick++;
            }

            _replaySystem.SeekToTick(tick);

            _logger?.ZLogInformation($"[KlothoEngine][Replay] seek: tick={tick}");
        }

        /// <summary>
        /// Saves the current replay to a file.
        /// </summary>
        public void SaveReplayToFile(string filePath, bool dumpJson = false)
        {
            _replaySystem.SaveToFile(filePath, dumpJson);
        }

        /// <summary>
        /// Gets the current replay data.
        /// </summary>
        public IReplayData GetCurrentReplayData()
        {
            return _replaySystem.CurrentReplayData;
        }

        /// <summary>
        /// Gets the random seed used for this game.
        /// </summary>
        public int GetRandomSeed()
        {
            return _randomSeed;
        }

        private void HandleReplayTick(int tick, System.Collections.Generic.IReadOnlyList<ICommand> commands)
        {
            // Save snapshot for seeking - per-tick save
            SaveSnapshot(tick);

            // Run the simulation with replay commands and collect events
            _tickCommandsCache.Clear();
            for (int i = 0; i < commands.Count; i++)
                _tickCommandsCache.Add(commands[i]);
            _eventCollector.BeginTick(tick);
            _simulation.Tick(_tickCommandsCache);

            // Store the collected events
            _eventBuffer.ClearTick(tick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(tick, _eventCollector.Collected[ei]);

            _lastVerifiedTick = tick;
            CurrentTick = tick + 1;
            OnTickExecuted?.Invoke(tick);
            _viewCallbacks?.OnTickExecuted(tick);
            OnTickExecutedWithState?.Invoke(tick, FrameState.Verified);
            OnFrameVerified?.Invoke(tick);

            // Dispatch all events as confirmed (replay = all verified)
            DispatchTickEvents(tick, FrameState.Verified);
        }

        private void HandleReplayFinished()
        {
            State = KlothoState.Finished;
            _isReplayMode = false;

            _replaySystem.OnTickPlayed -= HandleReplayTick;
            _replaySystem.OnPlaybackFinished -= HandleReplayFinished;

            _logger?.ZLogInformation($"[KlothoEngine][Replay] playback finished");
        }

        #endregion
    }
}
