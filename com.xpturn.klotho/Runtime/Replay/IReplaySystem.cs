using System;
using xpTURN.Klotho.Logging;
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

        /// <summary>Multi-stage stage identifier. Also recoverable via <see cref="ToSimulationConfig"/>.</summary>
        int StageId { get; }

        /// <summary>Multi-stage match config payload. Also recoverable via <see cref="ToSimulationConfig"/>.</summary>
        byte[] MatchConfigData { get; }

        /// <summary>Game-specific custom metadata. Sample serializes it in the desired format and injects it.</summary>
        byte[] GameCustomData { get; }

        /// <summary>Full EcsSimulation state snapshot the recording starts from. Restored via
        /// RestoreFromFullState during playback. Taken at <see cref="InitialStateTick"/> — that is tick 0 for a
        /// normal bootstrap, but an SD client receiving its initial FullState mid-match records that tick.</summary>
        byte[] InitialStateSnapshot { get; }

        /// <summary>Hash of <see cref="InitialStateSnapshot"/>, produced with the bytes.</summary>
        long InitialStateHash { get; }

        /// <summary>Tick <see cref="InitialStateSnapshot"/> was taken at.</summary>
        int InitialStateTick { get; }

        /// <summary>Component-registry layout fingerprint of the recording process. 0 = not provided.</summary>
        long LayoutFingerprint { get; }

        /// <summary>Static-collider fingerprint at the snapshot instant. 0 = no source registered.</summary>
        long StaticColliderFingerprint { get; }

        /// <summary>Navigation fingerprint at the snapshot instant. 0 = no mesh (the source normalizes its
        /// own 0 to 1, so 0 here really means "no navigation").</summary>
        long NavFingerprint { get; }

        /// <summary>The game's own fingerprint slot at the snapshot instant. 0 = no source registered.</summary>
        long GameFingerprint { get; }

        /// <summary>How the recording ended. A replay shorter than the match is honest when this is not
        /// <see cref="ReplayEndReason.Normal"/> — see Docs/Replay.md §4.</summary>
        ReplayEndReason EndReason { get; }

        /// <summary>The roster the tick-0 world was BUILT from, in creation order. Participant entities are
        /// created by walking it, so its order is state-hash input — only that order reproduces that world.
        /// Late joiners are NOT in it: this is the tick-0 roster, not the match's current one.
        /// <para><b>Empty means the recording did not build tick 0</b> (an SD client receives its initial
        /// state from the server and has no evidence of the order the server used). A non-empty roster is
        /// therefore exactly the signal "this replay can be reconstructed" — there is no separate flag.</para></summary>
        IReadOnlyList<int> InitialRoster { get; }

        /// <summary>Per-player verified data the tick-0 world was built from — concatenated bytes, sliced by
        /// <see cref="InitialEntitlementLengths"/> and index-parallel to <see cref="InitialRoster"/>.
        /// <para><b>Empty is a valid record</b> (a match with no issuer has none), not a missing one — the
        /// format version distinguishes an old file, never this field.</para></summary>
        byte[] InitialEntitlementData { get; }

        /// <summary>Per-entry byte counts for <see cref="InitialEntitlementData"/>. Index-parallel to
        /// <see cref="InitialRoster"/>; a disagreeing count is a corrupted file.</summary>
        IReadOnlyList<int> InitialEntitlementLengths { get; }

        /// <summary>Reservation-pruning denylist the recording ran with, sorted. RECORDED ONLY: the layout is
        /// frozen process-wide before a replay loads, so <see cref="ToSimulationConfig"/> does not restore it.
        /// A verifier reads this to BOOT with the same layout.</summary>
        IReadOnlyList<int> PrunedComponentTypeIds { get; }

        /// <summary>Per-component maxCount override type ids, sorted; parallel to
        /// <see cref="ComponentMaxCountValues"/>. Recorded only, same reason as
        /// <see cref="PrunedComponentTypeIds"/>.</summary>
        IReadOnlyList<int> ComponentMaxCountTypeIds { get; }

        /// <summary>Per-component maxCount override values, parallel to
        /// <see cref="ComponentMaxCountTypeIds"/>. Recorded only.</summary>
        IReadOnlyList<int> ComponentMaxCountValues { get; }

        /// <summary>Restores the replayable SimulationConfig fields from the metadata. The layout-determining
        /// inputs above are deliberately NOT restored.</summary>
        SimulationConfig ToSimulationConfig();
    }

    /// <summary>
    /// How a recording ended. <see cref="Unspecified"/> is 0 on purpose: if Normal were 0 a metadata that
    /// nobody ever stamped would be indistinguishable from one that ended cleanly, and a termination path
    /// added later without a reason would go unnoticed.
    /// </summary>
    public enum ReplayEndReason
    {
        /// <summary>Never stamped — a bug in whatever ended the recording.</summary>
        Unspecified = 0,
        /// <summary>Ordinary end of recording. A dropped unconfirmed tail is still Normal (Docs/Replay.md §4).</summary>
        Normal = 1,
        /// <summary>Cut at a host corrective reset — everything up to the cut is faithful.</summary>
        CorrectiveReset = 2,
        /// <summary>Cut at a full-state resync — everything up to the cut is faithful.</summary>
        ResyncRequest = 3,
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

        /// <summary>Stop recording and return the replay data. <paramref name="reason"/> is stamped into the
        /// metadata so a reader can tell an honest short replay from a suspicious one; the default is correct
        /// for every ordinary end-of-match, and only a state-jump truncation passes something else.</summary>
        IReplayData StopRecording(int totalTicks, ReplayEndReason reason = ReplayEndReason.Normal);

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
        void Load(IReplayData replayData, IKLogger logger);

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

        /// <summary>
        /// Loads a replay file from disk. Throws <see cref="ReplayLoadException"/> on failure
        /// (file-not-found, file-read I/O, malformed payload). On success the loaded data is
        /// accessible via <see cref="CurrentReplayData"/>; on failure <see cref="CurrentReplayData"/>
        /// is left unchanged (previous value retained — see implementation note on atomic commit).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="ReplayLoadException">Any replay-load failure.</exception>
        void LoadFromFile(string filePath);

        /// <summary>Returns the current replay data (when recording or when loaded)</summary>
        IReplayData CurrentReplayData { get; }

        /// <summary>Sets the game-specific custom metadata of the recording replay. Call after StartRecording. Included in ReplayMetadata.GameCustomData when saving to file.</summary>
        void SetGameCustomData(byte[] data);

        /// <summary>Sets the initial state snapshot of the recording replay along with its hash and the tick it
        /// was taken at. The snapshot, hash and tick are persisted to file; the (snapshot, hash) pair is also
        /// forwarded via the OnInitialStateSnapshotSet event for the engine's broadcast cache.</summary>
        void SetInitialStateSnapshot(byte[] snapshot, long hash, int tick);

        /// <summary>Records the roster the tick-0 world was built from. Call only from a peer that actually
        /// built it — see <see cref="IReplayMetadata.InitialRoster"/> for why an empty roster is meaningful.</summary>
        void SetInitialRoster(IReadOnlyList<int> roster);

        /// <summary>Records the per-player verified data tick 0 was built from, one entry per roster slot
        /// (null where a player had none). Call beside <see cref="SetInitialRoster"/> from the same peer —
        /// the two are index-parallel.</summary>
        void SetInitialEntitlements(IReadOnlyList<byte[]> perRosterEntry);

        /// <summary>Sets the reproduction anchors of the recording replay. Call at the same instant the initial
        /// snapshot is set — the navigation term moves with runtime rebakes, so anchors captured at any other
        /// moment would describe a different world than the snapshot does.</summary>
        void SetReproductionAnchors(long layoutFingerprint, long staticColliderFingerprint, long navFingerprint, long gameFingerprint);

        /// <summary>Raised when SetInitialStateSnapshot is called. The engine subscribes to it to apply (snapshot, hash) to _cachedFullState*.</summary>
        event Action<byte[], long> OnInitialStateSnapshotSet;
    }
}

