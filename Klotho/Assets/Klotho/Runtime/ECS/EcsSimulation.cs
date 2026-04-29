using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// ISimulation implementation — owns Frame + SystemRunner.
    /// Can be injected via the ISimulation interface without modifying KlothoEngine code.
    /// </summary>
    public class EcsSimulation : ISimulation
    {
        private ILogger _logger;
        private Frame _frame;
        private readonly SystemRunner _systemRunner;
        private readonly FrameRingBuffer _ringBuffer;
        private readonly int _deltaTimeMs;
        private byte[] _hashBuffer = Array.Empty<byte>();
        private IDataAssetRegistryBuilder _registryBuilder;

        public int CurrentTick => _frame.Tick;

        public EcsSimulation(int maxEntities, int maxRollbackTicks = 10, int deltaTimeMs = 50, ILogger logger = null, IDataAssetRegistryBuilder registryBuilder = null, IDataAssetRegistry assetRegistry = null)
        {
            if (assetRegistry != null && registryBuilder != null)
                throw new ArgumentException("Cannot specify both assetRegistry and registryBuilder.");

            _logger = logger;
            if (assetRegistry != null)
            {
                _registryBuilder = null;
                _frame = new Frame(maxEntities, _logger, assetRegistry);
            }
            else
            {
                _registryBuilder = registryBuilder ?? new DataAssetRegistry();
                _frame = new Frame(maxEntities, _logger, _registryBuilder);
            }
            _systemRunner = new SystemRunner();
            _ringBuffer = new FrameRingBuffer(maxRollbackTicks, maxEntities, _logger);
            _deltaTimeMs = deltaTimeMs;
        }

        public void LockAssetRegistry()
        {
            if (_registryBuilder == null) return;
            _frame.SetRegistry(_registryBuilder.Build());
        }

        private readonly List<ISnapshotParticipant> _snapshotParticipants = new();

        /// <summary>
        /// Registers a system. Adds the desired system from outside to match the Phase.
        /// </summary>
        public void AddSystem(object system, SystemPhase phase)
        {
            _systemRunner.AddSystem(system, phase);
            if (system is ISnapshotParticipant sp)
                _snapshotParticipants.Add(sp);
        }

public void Initialize()
        {
            _frame.Clear();
            _frame.Tick = 0;
            _frame.DeltaTimeMs = _deltaTimeMs;
            _frame.OnEntityCreated   = entity => _systemRunner.OnEntityCreated(ref _frame, entity);
            _frame.OnEntityDestroyed = entity => _systemRunner.OnEntityDestroyed(ref _frame, entity);
            _systemRunner.Init(ref _frame);
        }

        public void Tick(List<ICommand> commands)
        {
            _frame.DeltaTimeMs = _deltaTimeMs;

            // Phase.PreUpdate: apply commands
            for (int i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i];
                _systemRunner.RunCommandSystems(ref _frame, cmd);

                if (cmd is Core.PlayerJoinCommand joinCmd)
                    OnPlayerJoined(joinCmd.JoinedPlayerId, _frame.Tick);
            }

            // Phase.Update → PostUpdate → LateUpdate
            _systemRunner.RunUpdateSystems(ref _frame);

            _frame.Tick++;
        }

        public void Rollback(int targetTick)
        {
            _ringBuffer.RestoreFrame(targetTick, _frame);
            if (_snapshotParticipants.Count > 0)
                _ringBuffer.RestoreSystemState(targetTick, _snapshotParticipants);
        }

        public long GetStateHash()
        {
            ulong frameHash = _frame.CalculateHash();
            ulong hash = frameHash;

            if (_snapshotParticipants.Count > 0)
            {
                int sysSize = 0;
                for (int i = 0; i < _snapshotParticipants.Count; i++)
                    sysSize += _snapshotParticipants[i].GetSnapshotSize();

                if (_hashBuffer.Length < sysSize)
                    _hashBuffer = new byte[sysSize];

                var writer = new SpanWriter(_hashBuffer);
                for (int i = 0; i < _snapshotParticipants.Count; i++)
                    _snapshotParticipants[i].SaveSnapshot(ref writer);

                hash = FPHash.HashBytes(hash, new ReadOnlySpan<byte>(_hashBuffer, 0, writer.Position));
            }

            return (long)hash;
        }

        public void Reset()
        {
            _frame.Clear();
            _frame.Tick = 0;
            _frame.DeltaTimeMs = _deltaTimeMs;
        }

        public void RestoreFromFullState(byte[] stateData)
        {
            if (_snapshotParticipants.Count == 0)
            {
                _frame.DeserializeFrom(stateData);
            }
            else
            {
                var reader = new SpanReader(stateData);
                int frameLen = reader.ReadInt32();
                byte[] frameData = reader.ReadRawBytes(frameLen).ToArray();
                _frame.DeserializeFrom(frameData);
                for (int i = 0; i < _snapshotParticipants.Count; i++)
                    _snapshotParticipants[i].RestoreSnapshot(ref reader);
            }
            _ringBuffer.Clear();
        }

        public byte[] SerializeFullState()
        {
            byte[] frameData = _frame.SerializeTo();
            if (_snapshotParticipants.Count == 0)
                return frameData;

            int sysSize = 0;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                sysSize += _snapshotParticipants[i].GetSnapshotSize();

            byte[] combined = new byte[4 + frameData.Length + sysSize];
            var writer = new SpanWriter(combined);
            writer.WriteInt32(frameData.Length);
            writer.WriteRawBytes(frameData);
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].SaveSnapshot(ref writer);

            return combined;
        }

        public (byte[] data, long hash) SerializeFullStateWithHash()
        {
            if (_snapshotParticipants.Count == 0)
            {
                var (d, h) = _frame.SerializeToWithHash();
                return (d, (long)h);
            }

            int sysSize = 0;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                sysSize += _snapshotParticipants[i].GetSnapshotSize();

            // Single buffer: [frameLengthPlaceholder(4)] [frameData] [participantData]
            int totalSize = 4 + _frame.EstimateSerializedSize() + sysSize;
            byte[] combined = new byte[totalSize];
            var writer = new SpanWriter(combined);

            // frameLength placeholder — patched after serialization
            writer.WriteInt32(0);
            int frameStart = writer.Position;

            // Direct Frame serialization + hash calculation
            ulong frameHash = _frame.SerializeWithHash(ref writer);
            int frameLength = writer.Position - frameStart;

            // Patch frameLength
            BinaryPrimitives.WriteInt32LittleEndian(
                new Span<byte>(combined, 0, 4), frameLength);

            // Serialize participants
            int participantStart = writer.Position;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].SaveSnapshot(ref writer);

            // Participant hash
            ulong hash = FPHash.HashBytes(frameHash,
                new ReadOnlySpan<byte>(combined, participantStart, writer.Position - participantStart));

            return (combined, (long)hash);
        }

        /// <summary>
        /// Saves the current tick's snapshot to the ring buffer.
        /// KlothoEngine calls this every tick.
        /// </summary>
        public void SaveSnapshot()
        {
            _ringBuffer.SaveFrame(_frame.Tick, _frame);
            if (_snapshotParticipants.Count > 0)
                _ringBuffer.SaveSystemState(_frame.Tick, _snapshotParticipants);
        }

        public bool HasSnapshot(int tick)
            => _ringBuffer.HasFrame(tick, _frame.Tick);

        /// <summary>
        /// Returns the frame reference for the specified tick from the ring buffer.
        /// </summary>
        public bool TryGetSnapshotFrame(int tick, out Frame frame)
            => _ringBuffer.TryGetFrame(tick, _frame.Tick, out frame);

        public int GetNearestSnapshotTick(int targetTick)
            => _ringBuffer.GetNearestAvailableTick(targetTick, _frame.Tick);

        public void GetSavedSnapshotTicks(System.Collections.Generic.IList<int> output)
            => _ringBuffer.GetSavedTicks(_frame.Tick, output);

        public void ClearSnapshots()
            => _ringBuffer.Clear();

        public void EmitSyncEvents()
        {
            _systemRunner.EmitSyncEvents(ref _frame);
        }

        public event Action<int> OnPlayerJoinedNotification;

        public void OnPlayerJoined(int playerId, int tick)
        {
            OnPlayerJoinedNotification?.Invoke(playerId);
        }

        public int RollbackCapacity => _ringBuffer.Capacity;

        /// <summary>
        /// Direct ECS Frame access (for testing/debugging)
        /// </summary>
        public Frame Frame => _frame;
    }
}
