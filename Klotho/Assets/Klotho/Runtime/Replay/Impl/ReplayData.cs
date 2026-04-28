using System;
using System.Collections.Generic;
using System.IO;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Replay
{
    /// <summary>
    /// Replay metadata implementation
    /// </summary>
    [Serializable]
    public class ReplayMetadata : IReplayMetadata
    {
        public const int CURRENT_VERSION = 1;

        public int Version { get; set; } = CURRENT_VERSION;
        public string SessionId { get; set; }
        public long RecordedAt { get; set; }
        public long DurationMs { get; set; }
        public int TotalTicks { get; set; }
        public int PlayerCount { get; set; }
        public int RandomSeed { get; set; }

        // --- Full SimulationConfig ---

        public int TickIntervalMs { get; set; }
        public int InputDelayTicks { get; set; }
        public int MaxRollbackTicks { get; set; }
        public int SyncCheckInterval { get; set; }
        public bool UsePrediction { get; set; }
        public int MaxEntities { get; set; }
        public int Mode { get; set; }
        public int HardToleranceMs { get; set; }
        public int InputResendIntervalMs { get; set; }
        public int MaxUnackedInputs { get; set; }
        public int ServerSnapshotRetentionTicks { get; set; }
        public int EventDispatchWarnMs { get; set; }
        public int TickDriftWarnMultiplier { get; set; }

        // --- Game-specific custom data ---

        public byte[] GameCustomData { get; set; }

        // --- Initial state snapshot ---

        public byte[] InitialStateSnapshot { get; set; }

        public ReplayMetadata()
        {
            SessionId = Guid.NewGuid().ToString("N");
            RecordedAt = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Populates metadata fields from ISimulationConfig.
        /// </summary>
        public void CopySimulationConfigFrom(Core.ISimulationConfig config)
        {
            TickIntervalMs = config.TickIntervalMs;
            InputDelayTicks = config.InputDelayTicks;
            MaxRollbackTicks = config.MaxRollbackTicks;
            SyncCheckInterval = config.SyncCheckInterval;
            UsePrediction = config.UsePrediction;
            MaxEntities = config.MaxEntities;
            Mode = (int)config.Mode;
            HardToleranceMs = config.HardToleranceMs;
            InputResendIntervalMs = config.InputResendIntervalMs;
            MaxUnackedInputs = config.MaxUnackedInputs;
            ServerSnapshotRetentionTicks = config.ServerSnapshotRetentionTicks;
            EventDispatchWarnMs = config.EventDispatchWarnMs;
            TickDriftWarnMultiplier = config.TickDriftWarnMultiplier;
        }

        /// <summary>
        /// Restores a SimulationConfig from the metadata.
        /// </summary>
        public Core.SimulationConfig ToSimulationConfig()
        {
            return new Core.SimulationConfig
            {
                TickIntervalMs = TickIntervalMs,
                InputDelayTicks = InputDelayTicks,
                MaxRollbackTicks = MaxRollbackTicks,
                SyncCheckInterval = SyncCheckInterval,
                UsePrediction = UsePrediction,
                MaxEntities = MaxEntities,
                Mode = (Core.NetworkMode)Mode,
                HardToleranceMs = HardToleranceMs,
                InputResendIntervalMs = InputResendIntervalMs,
                MaxUnackedInputs = MaxUnackedInputs,
                ServerSnapshotRetentionTicks = ServerSnapshotRetentionTicks,
                EventDispatchWarnMs = EventDispatchWarnMs,
                TickDriftWarnMultiplier = TickDriftWarnMultiplier,
            };
        }

        public int GetSerializedSize()
        {
            int sessionIdBytes = System.Text.Encoding.UTF8.GetByteCount(SessionId ?? string.Empty);
            // Version(4) + SessionId(4+UTF8) + RecordedAt(8) + DurationMs(8) + TotalTicks(4) + PlayerCount(4) + TickIntervalMs(4) + RandomSeed(4)
            int size = 4 + (4 + sessionIdBytes) + 8 + 8 + 4 + 4 + 4 + 4;
            // Additional SimulationConfig fields (12 fields × 4 bytes + 1 bool via int = 52 bytes)
            size += 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4;
            // GameCustomData length prefix (4) + data
            size += 4 + (GameCustomData?.Length ?? 0);
            // InitialStateSnapshot length prefix (4) + data
            size += 4 + (InitialStateSnapshot?.Length ?? 0);
            return size;
        }

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(Version);
            writer.WriteString(SessionId);
            writer.WriteInt64(RecordedAt);
            writer.WriteInt64(DurationMs);
            writer.WriteInt32(TotalTicks);
            writer.WriteInt32(PlayerCount);
            writer.WriteInt32(TickIntervalMs);
            writer.WriteInt32(RandomSeed);

            // SimulationConfig fields
            writer.WriteInt32(InputDelayTicks);
            writer.WriteInt32(MaxRollbackTicks);
            writer.WriteInt32(SyncCheckInterval);
            writer.WriteBool(UsePrediction);
            writer.WriteInt32(MaxEntities);
            writer.WriteInt32(Mode);
            writer.WriteInt32(HardToleranceMs);
            writer.WriteInt32(InputResendIntervalMs);
            writer.WriteInt32(MaxUnackedInputs);
            writer.WriteInt32(ServerSnapshotRetentionTicks);
            writer.WriteInt32(EventDispatchWarnMs);
            writer.WriteInt32(TickDriftWarnMultiplier);

            // Game-specific custom data (length prefix + data)
            int customLen = GameCustomData?.Length ?? 0;
            writer.WriteInt32(customLen);
            if (customLen > 0)
                writer.WriteRawBytes(GameCustomData);

            // Initial state snapshot (length prefix + data)
            int snapshotLen = InitialStateSnapshot?.Length ?? 0;
            writer.WriteInt32(snapshotLen);
            if (snapshotLen > 0)
                writer.WriteRawBytes(InitialStateSnapshot);
        }

        public void Deserialize(ref SpanReader reader)
        {
            Version = reader.ReadInt32();
            SessionId = reader.ReadString();
            RecordedAt = reader.ReadInt64();
            DurationMs = reader.ReadInt64();
            TotalTicks = reader.ReadInt32();
            PlayerCount = reader.ReadInt32();
            TickIntervalMs = reader.ReadInt32();
            RandomSeed = reader.ReadInt32();

            // SimulationConfig
            InputDelayTicks = reader.ReadInt32();
            MaxRollbackTicks = reader.ReadInt32();
            SyncCheckInterval = reader.ReadInt32();
            UsePrediction = reader.ReadBool();
            MaxEntities = reader.ReadInt32();
            Mode = reader.ReadInt32();
            HardToleranceMs = reader.ReadInt32();
            InputResendIntervalMs = reader.ReadInt32();
            MaxUnackedInputs = reader.ReadInt32();
            ServerSnapshotRetentionTicks = reader.ReadInt32();
            EventDispatchWarnMs = reader.ReadInt32();
            TickDriftWarnMultiplier = reader.ReadInt32();

            // Game-specific custom data
            int customLen = reader.ReadInt32();
            if (customLen > 0 && reader.Remaining >= customLen)
                GameCustomData = reader.ReadRawBytes(customLen).ToArray();

            // Initial state snapshot
            int snapshotLen = reader.ReadInt32();
            if (snapshotLen > 0 && reader.Remaining >= snapshotLen)
                InitialStateSnapshot = reader.ReadRawBytes(snapshotLen).ToArray();
        }
    }

    /// <summary>
    /// Replay data implementation
    /// Contains all recorded commands organized per tick
    /// </summary>
    public class ReplayData : IReplayData
    {
        // Magic number identifying the replay file
        private const uint MAGIC_NUMBER = 0x52504C59; // "RPLY"
        
        private readonly ReplayMetadata _metadata;
        private byte[] _buffer = new byte[128 * 1024];
        private int _bufferPosition;
        private readonly Dictionary<int, (int offset, int length)> _tickOffsets;
        private readonly ICommandFactory _commandFactory;

        // Cached empty list for ticks without commands
        private static readonly List<ICommand> EmptyCommandList = new List<ICommand>();

        public IReplayMetadata Metadata => _metadata;

        public ReplayData() : this(new CommandFactory())
        {
        }

        public ReplayData(ICommandFactory commandFactory)
        {
            _metadata = new ReplayMetadata();
            _tickOffsets = new Dictionary<int, (int, int)>();
            _commandFactory = commandFactory;
        }

        /// <summary>
        /// Sets game-specific custom metadata (V3). Can be injected at any point during recording.
        /// </summary>
        public void SetGameCustomData(byte[] data)
        {
            _metadata.GameCustomData = data;
        }

        /// <summary>
        /// Sets the initial state snapshot. Can be injected at any point during recording.
        /// On playback, restored via RestoreFromFullState instead of OnInitializeWorld.
        /// </summary>
        public void SetInitialStateSnapshot(byte[] data)
        {
            _metadata.InitialStateSnapshot = data;
        }

        /// <summary>
        /// Initializes replay data for recording — copies all SimulationConfig fields into metadata.
        /// </summary>
        public void Initialize(int playerCount, ISimulationConfig simConfig, int randomSeed)
        {
            _metadata.PlayerCount = playerCount;
            _metadata.CopySimulationConfigFrom(simConfig);  // Bulk copy of 13 fields (restored on playback via ToSimulationConfig())
            _metadata.RandomSeed = randomSeed;
            _metadata.RecordedAt = DateTime.UtcNow.Ticks;
            _tickOffsets.Clear();
            _bufferPosition = 0;
        }

        public void AddSerializedCommands(int tick, ReadOnlySpan<byte> data)
        {
            EnsureCapacity(_bufferPosition + data.Length);
            data.CopyTo(_buffer.AsSpan(_bufferPosition));
            _tickOffsets[tick] = (_bufferPosition, data.Length);
            _bufferPosition += data.Length;

            UpdateTotalTicks(tick);
        }

        public void RecordCommands(int tick, List<ICommand> commands, ICommandFactory factory)
        {
            int size = factory.GetSerializedCommandsSize(commands);
            EnsureCapacity(_bufferPosition + size);
            int written = factory.SerializeCommandsTo(_buffer.AsSpan(_bufferPosition));
            _tickOffsets[tick] = (_bufferPosition, written);
            _bufferPosition += written;

            UpdateTotalTicks(tick);
        }

        private void UpdateTotalTicks(int tick)
        {
            if (tick > _metadata.TotalTicks)
            {
                _metadata.TotalTicks = tick;
                _metadata.DurationMs = (long)tick * _metadata.TickIntervalMs;
            }
        }

        /// <summary>
        /// Finalizes the recording
        /// </summary>
        public void FinalizeRecording(int totalTicks)
        {
            _metadata.TotalTicks = totalTicks;
            _metadata.DurationMs = (long)_metadata.TotalTicks * _metadata.TickIntervalMs;
        }

        public IReadOnlyList<ICommand> GetCommandsForTick(int tick)
        {
            if (_tickOffsets.TryGetValue(tick, out var entry))
            {
                return _commandFactory.DeserializeCommands(
                    _buffer.AsSpan(entry.offset, entry.length));
            }
            return EmptyCommandList;
        }

        public byte[] Serialize()
        {
            // magic(4) + metadata + tickCount(4) + bufferSize(4) + buffer + offsets (tick*12)
            int totalSize = 4 + _metadata.GetSerializedSize() + 4 + 4 + _bufferPosition + (_tickOffsets.Count * 12);

            using (var buf = SerializationBuffer.Create(totalSize))
            {
                var writer = new SpanWriter(buf.Span);

                writer.WriteUInt32(MAGIC_NUMBER);
                _metadata.Serialize(ref writer);

                writer.WriteInt32(_tickOffsets.Count);
                writer.WriteInt32(_bufferPosition);
                writer.WriteRawBytes(_buffer.AsSpan(0, _bufferPosition));

                foreach (var kvp in _tickOffsets)
                {
                    writer.WriteInt32(kvp.Key);
                    writer.WriteInt32(kvp.Value.offset);
                    writer.WriteInt32(kvp.Value.length);
                }

                return buf.Span.Slice(0, writer.Position).ToArray();
            }
        }

        public void Deserialize(byte[] data)
        {
            if (data == null || data.Length < 4)
                throw new ArgumentException("Invalid replay data");

            _tickOffsets.Clear();
            _bufferPosition = 0;

            var reader = new SpanReader(data);

            uint magic = reader.ReadUInt32();
            if (magic != MAGIC_NUMBER)
                throw new InvalidDataException("Invalid replay file format");

            _metadata.Deserialize(ref reader);

            if (_metadata.Version > ReplayMetadata.CURRENT_VERSION)
                throw new InvalidDataException($"Unsupported replay version: {_metadata.Version}");

            int tickCount = reader.ReadInt32();
            int bufferSize = reader.ReadInt32();

            if (bufferSize > _buffer.Length)
                _buffer = new byte[bufferSize];
            reader.ReadRawBytes(bufferSize).CopyTo(_buffer);
            _bufferPosition = bufferSize;

            for (int i = 0; i < tickCount; i++)
            {
                int tick = reader.ReadInt32();
                int offset = reader.ReadInt32();
                int length = reader.ReadInt32();
                _tickOffsets[tick] = (offset, length);
            }
        }

        public void Clear()
        {
            _tickOffsets.Clear();
            _bufferPosition = 0;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length) return;
            int newSize = _buffer.Length;
            while (newSize < required) newSize *= 2;
            var newBuffer = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _bufferPosition);
            _buffer = newBuffer;
        }
    }
}

