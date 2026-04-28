using System;
using Microsoft.Extensions.Logging;
using ZLogger;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Replay
{
    /// <summary>
    /// Replay recorder implementation
    /// Records all commands during gameplay and stores them for later playback
    /// </summary>
    public class ReplayRecorder : IReplayRecorder
    {
        private ILogger _logger;
        private ReplayData _replayData;
        private ReplayState _state = ReplayState.Idle;
        private int _currentTick;
        private readonly ICommandFactory _commandFactory;

        public ReplayState State => _state;
        public int CurrentTick => _currentTick;

        public event Action OnRecordingStarted;
        public event Action<IReplayData> OnRecordingStopped;

        public ReplayRecorder() : this(new CommandFactory(), null)
        {
        }

        public ReplayRecorder(ICommandFactory commandFactory, ILogger logger)
        {
            _commandFactory = commandFactory;
            _logger = logger;
        }

        public void StartRecording(int playerCount, ISimulationConfig simConfig, int randomSeed)
        {
            if (_state == ReplayState.Recording)
            {
                _logger?.ZLogWarning($"[ReplayRecorder] Already recording");
                return;
            }

            _replayData = new ReplayData(_commandFactory);
            _replayData.Initialize(playerCount, simConfig, randomSeed);

            _currentTick = 0;
            _state = ReplayState.Recording;

            _logger?.ZLogInformation($"[ReplayRecorder] Recording started - players: {playerCount}, tick interval: {simConfig.TickIntervalMs}ms, seed: {randomSeed}");

            OnRecordingStarted?.Invoke();
        }

        public void RecordTick(int tick, List<ICommand> commands)
        {
            if (_state != ReplayState.Recording)
            {
                return;
            }

            _replayData.RecordCommands(tick, commands, _commandFactory);

            _currentTick = tick;
        }

        public IReplayData StopRecording(int totalTicks)
        {
            if (_state != ReplayState.Recording)
            {
                _logger?.ZLogWarning($"[ReplayRecorder] Not recording");
                return null;
            }

            _replayData.FinalizeRecording(totalTicks);
            _state = ReplayState.Idle;
            
            var result = _replayData;
            
            _logger?.ZLogInformation($"[ReplayRecorder] Recording stopped - total ticks: {result.Metadata.TotalTicks}, duration: {result.Metadata.DurationMs}ms");
            
            OnRecordingStopped?.Invoke(result);
            
            return result;
        }

        /// <summary>
        /// Returns the current replay data (while recording)
        /// </summary>
        public IReplayData GetCurrentReplayData()
        {
            return _replayData;
        }

        /// <summary>
        /// Sets game-specific custom metadata on the recording replay.
        /// Must be called after StartRecording for it to be persisted.
        /// </summary>
        public void SetGameCustomData(byte[] data)
        {
            if (_replayData == null) return;
            _replayData.SetGameCustomData(data);
        }

        /// <summary>
        /// Sets the initial state snapshot on the recording replay.
        /// Must be called after StartRecording for it to be persisted.
        /// </summary>
        public void SetInitialStateSnapshot(byte[] data)
        {
            if (_replayData == null) return;
            _replayData.SetInitialStateSnapshot(data);
        }

        /// <summary>
        /// Cancels the recording and discards the data
        /// </summary>
        public void CancelRecording()
        {
            if (_state != ReplayState.Recording)
                return;

            _replayData?.Clear();
            _replayData = null;
            _state = ReplayState.Idle;
            _currentTick = 0;
            
            _logger?.ZLogInformation($"[ReplayRecorder] Recording cancelled");
        }
    }
}
