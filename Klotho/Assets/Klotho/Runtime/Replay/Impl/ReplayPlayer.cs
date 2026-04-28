using System;
using Microsoft.Extensions.Logging;
using ZLogger;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Replay
{
    /// <summary>
    /// Replay player implementation
    /// Plays back recorded replay data
    /// </summary>
    public class ReplayPlayer : IReplayPlayer
    {
        private ILogger _logger;
        private IReplayData _replayData;
        private ReplayState _state = ReplayState.Idle;
        private int _currentTick;
        private ReplaySpeed _speed = ReplaySpeed.Normal;
        private float _accumulator;
        
        // Cached empty list
        private static readonly List<ICommand> EmptyCommandList = new List<ICommand>();

        public ReplayState State => _state;
        public int CurrentTick => _currentTick;
        public int TotalTicks => _replayData?.Metadata.TotalTicks ?? 0;
        public float Accumulator => _accumulator;
        
        public ReplaySpeed Speed
        {
            get => _speed;
            set => _speed = value;
        }

        public float Progress
        {
            get
            {
                if (TotalTicks <= 0) return 0f;
                return (float)_currentTick / TotalTicks;
            }
        }

        public event Action<int, IReadOnlyList<ICommand>> OnTickPlayed;
        public event Action OnPlaybackFinished;
        public event Action<int> OnSeekCompleted;

        public void Load(IReplayData replayData, ILogger logger)
        {
            _logger = logger;
            if (replayData == null)
            {
                _logger?.ZLogError($"[ReplayPlayer] Cannot load null replay data");
                return;
            }

            _replayData = replayData;
            _currentTick = 0;
            _accumulator = 0;
            _state = ReplayState.Idle;
            
            _logger?.ZLogInformation($"[ReplayPlayer] Replay loaded - ticks: {replayData.Metadata.TotalTicks}, duration: {replayData.Metadata.DurationMs}ms");
        }

        public void Play()
        {
            if (_replayData == null)
            {
                _logger?.ZLogError($"[ReplayPlayer] No replay data loaded");
                return;
            }

            if (_state == ReplayState.Finished)
            {
                // Restart from the beginning
                _currentTick = 0;
                _accumulator = 0;
            }

            _state = ReplayState.Playing;
            
            _logger?.ZLogInformation($"[ReplayPlayer] Playback started");
        }

        public void Pause()
        {
            if (_state == ReplayState.Playing)
            {
                _state = ReplayState.Paused;
                _logger?.ZLogInformation($"[ReplayPlayer] Playback paused");
            }
        }

        public void Resume()
        {
            if (_state == ReplayState.Paused)
            {
                _state = ReplayState.Playing;
                _logger?.ZLogInformation($"[ReplayPlayer] Playback resumed");
            }
        }

        public void Stop()
        {
            _state = ReplayState.Idle;
            _currentTick = 0;
            _accumulator = 0;
            
            _logger?.ZLogInformation($"[ReplayPlayer] Playback stopped");
        }

        public void SeekToTick(int tick)
        {
            if (_replayData == null)
                return;

            tick = System.Math.Max(0, System.Math.Min(tick, TotalTicks));
            _currentTick = tick;
            _accumulator = 0;
            
            _logger?.ZLogInformation($"[ReplayPlayer] Seek: tick={tick}");
            
            OnSeekCompleted?.Invoke(tick);
        }

        public void SeekToProgress(float progress)
        {
            progress = System.Math.Max(0f, System.Math.Min(1f, progress));
            int tick = (int)(TotalTicks * progress);
            SeekToTick(tick);
        }

        public IReadOnlyList<ICommand> GetCurrentTickCommands()
        {
            if (_replayData == null || _currentTick > TotalTicks)
            {
                return EmptyCommandList;
            }

            return _replayData.GetCommandsForTick(_currentTick);
        }

        public void Update(float deltaTime)
        {
            if (_state != ReplayState.Playing || _replayData == null)
                return;

            // Apply speed multiplier
            float speedMultiplier = (int)_speed / 100f;
            float tickIntervalMs = _replayData.Metadata.TickIntervalMs;
            
            _accumulator += deltaTime * 1000f * speedMultiplier;

            // Process ticks
            while (_accumulator >= tickIntervalMs && _currentTick <= TotalTicks)
            {
                _accumulator -= tickIntervalMs;
                
                // Fetch and play back commands for the current tick
                var commands = _replayData.GetCommandsForTick(_currentTick);
                OnTickPlayed?.Invoke(_currentTick, commands);
                
                _currentTick++;
                
                // Check for completion
                if (_currentTick > TotalTicks)
                {
                    _state = ReplayState.Finished;
                    _accumulator = 0;
                    
                    _logger?.ZLogInformation($"[ReplayPlayer] Playback finished");
                    OnPlaybackFinished?.Invoke();
                    break;
                }
            }
        }

        /// <summary>
        /// Steps forward by one tick (for frame-by-frame playback)
        /// </summary>
        public void StepForward()
        {
            if (_replayData == null || _currentTick > TotalTicks)
                return;

            var commands = _replayData.GetCommandsForTick(_currentTick);
            OnTickPlayed?.Invoke(_currentTick, commands);
            
            _currentTick++;
            
            if (_currentTick > TotalTicks)
            {
                _state = ReplayState.Finished;
                OnPlaybackFinished?.Invoke();
            }
        }

        /// <summary>
        /// Steps backward by one tick.
        /// Note: a simulation rollback is required for correct state restoration.
        /// </summary>
        public void StepBackward()
        {
            if (_replayData == null || _currentTick <= 0)
                return;

            _currentTick--;
            OnSeekCompleted?.Invoke(_currentTick);
        }

        /// <summary>
        /// Returns the replay metadata
        /// </summary>
        public IReplayMetadata GetMetadata()
        {
            return _replayData?.Metadata;
        }

        /// <summary>
        /// Indicates whether replay data has been loaded
        /// </summary>
        public bool HasReplayData => _replayData != null;
    }
}
