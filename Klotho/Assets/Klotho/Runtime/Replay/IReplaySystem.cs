using System;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Replay
{
    /// <summary>
    /// Replay system state
    /// </summary>
    public enum ReplayState
    {
        /// <summary>Not recording or playing</summary>
        Idle,
        /// <summary>Recording gameplay</summary>
        Recording,
        /// <summary>Playing back replay</summary>
        Playing,
        /// <summary>Playback paused</summary>
        Paused,
        /// <summary>Replay finished</summary>
        Finished
    }

    /// <summary>
    /// Replay playback speed
    /// </summary>
    public enum ReplaySpeed
    {
        /// <summary>0.25x speed</summary>
        Quarter = 25,
        /// <summary>0.5x speed</summary>
        Half = 50,
        /// <summary>1x speed (default)</summary>
        Normal = 100,
        /// <summary>2x speed</summary>
        Double = 200,
        /// <summary>4x speed</summary>
        Quadruple = 400
    }

    /// <summary>
    /// Replay metadata
    /// </summary>
    public interface IReplayMetadata
    {
        /// <summary>Replay format version</summary>
        int Version { get; }

        /// <summary>Game session ID</summary>
        string SessionId { get; }

        /// <summary>Recording timestamp (UTC ticks)</summary>
        long RecordedAt { get; }

        /// <summary>Total duration (milliseconds)</summary>
        long DurationMs { get; }

        /// <summary>Total number of ticks</summary>
        int TotalTicks { get; }

        /// <summary>Number of players</summary>
        int PlayerCount { get; }

        /// <summary>Tick interval (milliseconds)</summary>
        int TickIntervalMs { get; }

        /// <summary>Random seed used by the game</summary>
        int RandomSeed { get; }

        /// <summary>Game-specific custom metadata. Sample serializes it in the desired format and injects it.</summary>
        byte[] GameCustomData { get; }

        /// <summary>Full EcsSimulation state snapshot at the start of recording. Restored via RestoreFromFullState during playback.</summary>
        byte[] InitialStateSnapshot { get; }

        /// <summary>Restores SimulationConfig from metadata (full 13-field restore for V2 and above; defaults for V1).</summary>
        SimulationConfig ToSimulationConfig();
    }

    /// <summary>
    /// Replay data interface
    /// </summary>
    public interface IReplayData
    {
        /// <summary>Replay metadata</summary>
        IReplayMetadata Metadata { get; }

        /// <summary>Look up commands for a specific tick</summary>
        IReadOnlyList<ICommand> GetCommandsForTick(int tick);

        /// <summary>Serialize the replay to a byte array</summary>
        byte[] Serialize();

        /// <summary>Deserialize a replay from a byte array</summary>
        void Deserialize(byte[] data);
    }

    /// <summary>
    /// Replay recorder interface
    /// </summary>
    public interface IReplayRecorder
    {
        /// <summary>Current recording state</summary>
        ReplayState State { get; }

        /// <summary>Current recording tick</summary>
        int CurrentTick { get; }

        /// <summary>Start recording — stores the entire SimulationConfig as metadata (for restoration during playback)</summary>
        void StartRecording(int playerCount, ISimulationConfig simConfig, int randomSeed);

        /// <summary>Record commands for a tick</summary>
        void RecordTick(int tick, List<ICommand> commands);

        /// <summary>Stop recording and return the replay data</summary>
        IReplayData StopRecording(int totalTicks);

        /// <summary>Event raised when recording starts</summary>
        event Action OnRecordingStarted;

        /// <summary>Event raised when recording stops</summary>
        event Action<IReplayData> OnRecordingStopped;
    }

    /// <summary>
    /// Replay player interface
    /// </summary>
    public interface IReplayPlayer
    {
        /// <summary>Current playback state</summary>
        ReplayState State { get; }

        /// <summary>Current playback tick</summary>
        int CurrentTick { get; }

        /// <summary>Total number of ticks in the replay</summary>
        int TotalTicks { get; }

        /// <summary>Current playback speed</summary>
        ReplaySpeed Speed { get; set; }

        /// <summary>Progress (0.0 ~ 1.0)</summary>
        float Progress { get; }

        /// <summary>Load replay data</summary>
        void Load(IReplayData replayData, ILogger logger);

        /// <summary>Start playback</summary>
        void Play();

        /// <summary>Pause playback</summary>
        void Pause();

        /// <summary>Resume playback</summary>
        void Resume();

        /// <summary>Stop playback</summary>
        void Stop();

        /// <summary>Seek to a specific tick</summary>
        void SeekToTick(int tick);

        /// <summary>Seek by progress (0.0 ~ 1.0)</summary>
        void SeekToProgress(float progress);

        /// <summary>Retrieve commands for the current tick and advance to the next</summary>
        IReadOnlyList<ICommand> GetCurrentTickCommands();

        /// <summary>Playback update (called every frame)</summary>
        void Update(float deltaTime);

        /// <summary>Event raised when a tick is played</summary>
        event Action<int, IReadOnlyList<ICommand>> OnTickPlayed;

        /// <summary>Event raised when playback finishes</summary>
        event Action OnPlaybackFinished;

        /// <summary>Event raised when seeking completes</summary>
        event Action<int> OnSeekCompleted;

        /// <summary>Current accumulator (ms) used to compute interpolation alpha</summary>
        float Accumulator { get; }
    }

    /// <summary>
    /// Unified replay system interface
    /// </summary>
    public interface IReplaySystem : IReplayRecorder, IReplayPlayer
    {
        /// <summary>Whether recording is currently in progress</summary>
        bool IsRecording { get; }

        /// <summary>Whether playback is currently in progress</summary>
        bool IsPlaying { get; }

        /// <summary>Save the replay to a file. If dumpJson=true, also writes a .json debug dump to the same path.</summary>
        void SaveToFile(string filePath, bool dumpJson = false);

        /// <summary>Load a replay from a file</summary>
        void LoadFromFile(string filePath);

        /// <summary>Returns the current replay data (when recording or when loaded)</summary>
        IReplayData CurrentReplayData { get; }

        /// <summary>Sets the game-specific custom metadata of the recording replay. Call after StartRecording. Included in ReplayMetadata.GameCustomData when saving to file.</summary>
        void SetGameCustomData(byte[] data);

        /// <summary>Sets the initial state snapshot of the recording replay. Call after StartRecording. The snapshot is persisted to file; the hash is forwarded via the OnInitialStateSnapshotSet event for the engine's broadcast cache.</summary>
        void SetInitialStateSnapshot(byte[] snapshot, long hash);

        /// <summary>Raised when SetInitialStateSnapshot is called. The engine subscribes to it to apply (snapshot, hash) to _cachedFullState*.</summary>
        event Action<byte[], long> OnInitialStateSnapshotSet;
    }
}

