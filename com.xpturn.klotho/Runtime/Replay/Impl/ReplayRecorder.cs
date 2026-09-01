using System;
using xpTURN.Klotho.Logging;
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
        private IKLogger _logger;
        private ReplayData _replayData;
        // Snapshot + anchors set before StartRecording (SD client: the initial FullState can land during
        // Countdown); flushed on start. The hash and tick travel WITH the bytes — keeping only the bytes
        // here would drop both on exactly that path, and no P2P test would ever see it.
        private byte[] _pendingInitialSnapshot;
        private long _pendingInitialHash;
        private int _pendingInitialTick;
        private bool _hasPendingAnchors;
        private long _pendingLayoutFp, _pendingColliderFp, _pendingNavFp, _pendingGameFp;
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

        public ReplayRecorder(ICommandFactory commandFactory, IKLogger logger)
        {
            _commandFactory = commandFactory;
            _logger = logger;
        }

        public void StartRecording(int playerCount, ISimulationConfig simConfig, int randomSeed)
        {
            if (_state == ReplayState.Recording)
            {
                _logger?.KWarning($"[ReplayRecorder] Already recording");
                return;
            }

            _replayData = new ReplayData(_commandFactory);
            _replayData.Initialize(playerCount, simConfig, randomSeed);

            // Flush a snapshot that arrived before recording started (SD client: initial FullState
            // may be applied during Countdown, ahead of StartRecording).
            if (_pendingInitialSnapshot != null)
            {
                _replayData.SetInitialStateSnapshot(_pendingInitialSnapshot, _pendingInitialHash, _pendingInitialTick);
                _pendingInitialSnapshot = null;
                _pendingInitialHash = 0;
                _pendingInitialTick = 0;
            }
            if (_hasPendingAnchors)
            {
                _replayData.SetReproductionAnchors(_pendingLayoutFp, _pendingColliderFp, _pendingNavFp, _pendingGameFp);
                _hasPendingAnchors = false;
            }

            _currentTick = 0;
            _state = ReplayState.Recording;

            _logger?.KInformation($"[ReplayRecorder] Recording started - players: {playerCount}, tick interval: {simConfig.TickIntervalMs}ms, seed: {randomSeed}");

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

        public IReplayData StopRecording(int totalTicks, ReplayEndReason reason = ReplayEndReason.Normal)
        {
            if (_state != ReplayState.Recording)
            {
                _logger?.KWarning($"[ReplayRecorder] Not recording");
                return null;
            }

            _replayData.FinalizeRecording(totalTicks, reason);
            _state = ReplayState.Idle;
            
            var result = _replayData;
            
            _logger?.KInformation($"[ReplayRecorder] Recording stopped - total ticks: {result.Metadata.TotalTicks}, duration: {result.Metadata.DurationMs}ms, endReason: {reason}");
            
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
        /// Sets the initial state snapshot, its hash and the tick it was taken at on the recording replay.
        /// Arriving before StartRecording is retained rather than dropped.
        /// </summary>
        public void SetInitialStateSnapshot(byte[] data, long hash, int tick)
        {
            if (_replayData == null)
            {
                // Retain until StartRecording instead of dropping silently.
                _pendingInitialSnapshot = data;
                _pendingInitialHash = hash;
                _pendingInitialTick = tick;
                return;
            }
            _replayData.SetInitialStateSnapshot(data, hash, tick);
        }

        /// <summary>
        /// Records the roster the tick-0 world was built from. No pending buffer: the engine calls this
        /// after StartRecording (unlike the initial snapshot, which an SD client can deliver earlier), so a
        /// null _replayData here means recording is off and there is nothing to record onto.
        /// </summary>
        public void SetInitialRoster(IReadOnlyList<int> roster)
        {
            _replayData?.SetInitialRoster(roster);
        }

        /// <summary>
        /// Records the per-player verified data tick 0 was built from. Same no-pending rule as the roster:
        /// the engine calls this after StartRecording, so a null _replayData means recording is off.
        /// </summary>
        public void SetInitialEntitlements(IReadOnlyList<byte[]> perRosterEntry)
        {
            _replayData?.SetInitialEntitlements(perRosterEntry);
        }

        /// <summary>
        /// Sets the reproduction anchors on the recording replay. Same retain-until-start rule as the
        /// snapshot, and for the same reason: the two are set together.
        /// </summary>
        public void SetReproductionAnchors(long layoutFingerprint, long staticColliderFingerprint, long navFingerprint, long gameFingerprint)
        {
            if (_replayData == null)
            {
                _hasPendingAnchors = true;
                _pendingLayoutFp = layoutFingerprint;
                _pendingColliderFp = staticColliderFingerprint;
                _pendingNavFp = navFingerprint;
                _pendingGameFp = gameFingerprint;
                return;
            }
            _replayData.SetReproductionAnchors(layoutFingerprint, staticColliderFingerprint, navFingerprint, gameFingerprint);
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
            
            _logger?.KInformation($"[ReplayRecorder] Recording cancelled");
        }
    }
}
