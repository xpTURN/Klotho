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
        // Bumped to 5 for the tick-0 entitlements. Every change to the metadata layout bumps this. There is no version branch in the codec, so a
        // number reused across two layouts cannot be told apart and the older file is misparsed rather than
        // refused — which is why 2 is skipped: it was issued by an earlier build and files carrying it exist.
        public const int CURRENT_VERSION = 5;

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

        // --- Multi-stage ---

        public int StageId { get; set; }
        public byte[] MatchConfigData { get; set; }

        // --- Game-specific custom data ---

        public byte[] GameCustomData { get; set; }

        // --- Initial state snapshot ---

        public byte[] InitialStateSnapshot { get; set; }

        /// <summary>Hash of <see cref="InitialStateSnapshot"/>, as produced with the bytes.</summary>
        public long InitialStateHash { get; set; }

        /// <summary>Tick the snapshot was taken at. 0 for a tick-0 bootstrap; an SD client that received
        /// its initial FullState mid-match records that tick here.</summary>
        public int InitialStateTick { get; set; }

        // --- Reproduction anchors (recorded when the snapshot is set, so they describe the same instant) ---

        public long LayoutFingerprint { get; set; }
        public long StaticColliderFingerprint { get; set; }
        public long NavFingerprint { get; set; }
        public long GameFingerprint { get; set; }

        // --- Completion marker ---

        public ReplayEndReason EndReason { get; set; } = ReplayEndReason.Unspecified;

        /// <summary>
        /// The roster the tick-0 world was BUILT from, in creation order — not the match's current
        /// participants. Late joiners must never be appended: the participant entities are created by
        /// walking this list, so its order is state-hash input and only the order used at tick 0 reproduces
        /// that world.
        ///
        /// <para>Empty means "this recording did not build tick 0" — an SD client receives its initial state
        /// as a server FullState and has no evidence of the order the server built with. So a non-empty
        /// roster is exactly the signal "this replay can be reconstructed", with no separate flag.</para>
        /// </summary>
        public List<int> InitialRoster { get; set; } = new List<int>();

        /// <summary>
        /// Per-player verified data the game read while BUILDING tick 0 — the entitlement bytes, laid out
        /// index-parallel to <see cref="InitialRoster"/> as concatenated bytes plus per-entry lengths (the
        /// same shape the roster-propagation messages use).
        ///
        /// <para><b>Why the file has to carry it.</b> A game may seed tick-0 state from data only the
        /// authority verified — a loadout from an entitlement, say. The engine reads that through the
        /// network service, and a replay session has none, so on playback it decodes as "nothing was
        /// issued" and the rebuilt world silently differs. Entity and component COUNTS match; only values
        /// move, so the failure looks like a determinism bug rather than a missing input.</para>
        ///
        /// <para><b>Empty is a valid record</b>, not a missing one: a match with no issuer really has no
        /// entitlements. The format version, not this field, is what tells an old file from a new one.</para>
        /// </summary>
        public byte[] InitialEntitlementData { get; set; }

        /// <summary>Per-entry byte counts for <see cref="InitialEntitlementData"/>, index-parallel to
        /// <see cref="InitialRoster"/>. A count that disagrees with the roster is a corrupted file — the
        /// slices would hand each player someone else's data.</summary>
        public List<int> InitialEntitlementLengths { get; set; } = new List<int>();

        // --- Layout-determining config: RECORDED ONLY, never restored (see ToSimulationConfig) ---

        public List<int> PrunedComponentTypeIds { get; set; } = new List<int>();
        public List<int> ComponentMaxCountTypeIds { get; set; } = new List<int>();
        public List<int> ComponentMaxCountValues { get; set; } = new List<int>();

        IReadOnlyList<int> IReplayMetadata.InitialRoster => InitialRoster;
        byte[] IReplayMetadata.InitialEntitlementData => InitialEntitlementData;
        IReadOnlyList<int> IReplayMetadata.InitialEntitlementLengths => InitialEntitlementLengths;
        IReadOnlyList<int> IReplayMetadata.PrunedComponentTypeIds => PrunedComponentTypeIds;
        IReadOnlyList<int> IReplayMetadata.ComponentMaxCountTypeIds => ComponentMaxCountTypeIds;
        IReadOnlyList<int> IReplayMetadata.ComponentMaxCountValues => ComponentMaxCountValues;

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
            StageId = config.StageId;
            MatchConfigData = config.MatchConfigData;

            // Layout-determining inputs. Recorded so a verifier can be BOOTED with them; deliberately
            // not restored on playback (ToSimulationConfig) — the layout is frozen process-wide long
            // before a replay loads, so assigning them there would only look like a restore.
            // Sorted by typeId, unlike the wire codec: the wire's receiver rebuilds a dict so order is
            // irrelevant there, but this file is read for comparison and diffing.
            PrunedComponentTypeIds.Clear();
            if (config.PrunedComponentTypeIds != null)
            {
                foreach (var id in config.PrunedComponentTypeIds)
                    PrunedComponentTypeIds.Add(id);
                PrunedComponentTypeIds.Sort();
            }

            ComponentMaxCountTypeIds.Clear();
            ComponentMaxCountValues.Clear();
            if (config.ComponentMaxCountOverrides != null && config.ComponentMaxCountOverrides.Count > 0)
            {
                foreach (var kv in config.ComponentMaxCountOverrides)
                    ComponentMaxCountTypeIds.Add(kv.Key);
                ComponentMaxCountTypeIds.Sort();
                for (int i = 0; i < ComponentMaxCountTypeIds.Count; i++)
                    ComponentMaxCountValues.Add(config.ComponentMaxCountOverrides[ComponentMaxCountTypeIds[i]]);
            }
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
                StageId = StageId,
                MatchConfigData = MatchConfigData,
                // PrunedComponentTypeIds / ComponentMaxCount* are deliberately NOT restored. They are
                // process-global layout inputs, frozen before any replay is loaded, so assigning them
                // here would change nothing while signalling "restored" — the resulting run would
                // reproduce under a different layout and only the fingerprint mismatch would say so.
                // They are metadata for booting a verifier, not config for this process.
            };
        }

        public int GetSerializedSize()
        {
            int sessionIdBytes = System.Text.Encoding.UTF8.GetByteCount(SessionId ?? string.Empty);
            // Version(4) + SessionId(4+UTF8) + RecordedAt(8) + DurationMs(8) + TotalTicks(4) + PlayerCount(4) + TickIntervalMs(4) + RandomSeed(4)
            int size = 4 + (4 + sessionIdBytes) + 8 + 8 + 4 + 4 + 4 + 4;
            // Additional SimulationConfig fields: 11 int32 + 1 bool(1 byte). Counted as 12 x 4 —
            // 3 bytes over, which is harmless (SpanWriter only throws when the estimate is UNDER).
            size += 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4;
            // GameCustomData length prefix (4) + data
            size += 4 + (GameCustomData?.Length ?? 0);
            // InitialStateSnapshot length prefix (4) + data
            size += 4 + (InitialStateSnapshot?.Length ?? 0);
            // StageId(4) + MatchConfigData length prefix(4) + data
            size += 4 + 4 + (MatchConfigData?.Length ?? 0);
            // Anchors: 4 fingerprints (8 each) + InitialStateHash(8) + InitialStateTick(4) + EndReason(4)
            size += 8 * 5 + 4 + 4;
            // Initial roster: count(4) + ids
            size += 4 + 4 * InitialRoster.Count;
            size += 4 + 4 * InitialEntitlementLengths.Count;          // lengths
            size += 4 + (InitialEntitlementData?.Length ?? 0);        // concatenated bytes
            // Layout inputs: pruned count(4) + ids, maxCount count(4) + ids + values
            size += 4 + 4 * PrunedComponentTypeIds.Count;
            size += 4 + 8 * ComponentMaxCountTypeIds.Count;
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

            // Multi-stage
            writer.WriteInt32(StageId);
            int matchCfgLen = MatchConfigData?.Length ?? 0;
            writer.WriteInt32(matchCfgLen);
            if (matchCfgLen > 0)
                writer.WriteRawBytes(MatchConfigData);

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

            // Reproduction anchors + completion marker
            writer.WriteInt64(LayoutFingerprint);
            writer.WriteInt64(StaticColliderFingerprint);
            writer.WriteInt64(NavFingerprint);
            writer.WriteInt64(GameFingerprint);
            writer.WriteInt64(InitialStateHash);
            writer.WriteInt32(InitialStateTick);
            writer.WriteInt32((int)EndReason);

            // Roster the tick-0 world was built from (empty = built elsewhere, cannot be reconstructed)
            writer.WriteInt32(InitialRoster.Count);
            for (int i = 0; i < InitialRoster.Count; i++)
                writer.WriteInt32(InitialRoster[i]);

            // Roster-parallel per-player verified data: lengths first, then the concatenated bytes.
            writer.WriteInt32(InitialEntitlementLengths.Count);
            for (int i = 0; i < InitialEntitlementLengths.Count; i++)
                writer.WriteInt32(InitialEntitlementLengths[i]);
            int entLen = InitialEntitlementData?.Length ?? 0;
            writer.WriteInt32(entLen);
            if (entLen > 0)
                writer.WriteRawBytes(InitialEntitlementData);

            // Layout-determining config (recorded only)
            writer.WriteInt32(PrunedComponentTypeIds.Count);
            for (int i = 0; i < PrunedComponentTypeIds.Count; i++)
                writer.WriteInt32(PrunedComponentTypeIds[i]);

            writer.WriteInt32(ComponentMaxCountTypeIds.Count);
            for (int i = 0; i < ComponentMaxCountTypeIds.Count; i++)
                writer.WriteInt32(ComponentMaxCountTypeIds[i]);
            for (int i = 0; i < ComponentMaxCountTypeIds.Count; i++)
                writer.WriteInt32(ComponentMaxCountValues[i]);
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

            // Multi-stage
            StageId = reader.ReadInt32();
            int matchCfgLen = reader.ReadInt32();
            if (matchCfgLen > 0 && reader.Remaining >= matchCfgLen)
                MatchConfigData = reader.ReadRawBytes(matchCfgLen).ToArray();

            // Game-specific custom data
            int customLen = reader.ReadInt32();
            if (customLen > 0 && reader.Remaining >= customLen)
                GameCustomData = reader.ReadRawBytes(customLen).ToArray();

            // Initial state snapshot
            int snapshotLen = reader.ReadInt32();
            if (snapshotLen > 0 && reader.Remaining >= snapshotLen)
                InitialStateSnapshot = reader.ReadRawBytes(snapshotLen).ToArray();

            // Reproduction anchors + completion marker
            LayoutFingerprint = reader.ReadInt64();
            StaticColliderFingerprint = reader.ReadInt64();
            NavFingerprint = reader.ReadInt64();
            GameFingerprint = reader.ReadInt64();
            InitialStateHash = reader.ReadInt64();
            InitialStateTick = reader.ReadInt32();
            EndReason = (ReplayEndReason)reader.ReadInt32();

            // Roster the tick-0 world was built from
            InitialRoster.Clear();
            int rosterCount = reader.ReadInt32();
            for (int i = 0; i < rosterCount; i++)
                InitialRoster.Add(reader.ReadInt32());

            InitialEntitlementLengths.Clear();
            int entCount = reader.ReadInt32();
            for (int i = 0; i < entCount; i++)
                InitialEntitlementLengths.Add(reader.ReadInt32());
            int entBytes = reader.ReadInt32();
            InitialEntitlementData = entBytes > 0 && reader.Remaining >= entBytes
                ? reader.ReadRawBytes(entBytes).ToArray()
                : null;

            // Layout-determining config
            PrunedComponentTypeIds.Clear();
            int prunedCount = reader.ReadInt32();
            for (int i = 0; i < prunedCount; i++)
                PrunedComponentTypeIds.Add(reader.ReadInt32());

            ComponentMaxCountTypeIds.Clear();
            ComponentMaxCountValues.Clear();
            int maxCountCount = reader.ReadInt32();
            for (int i = 0; i < maxCountCount; i++)
                ComponentMaxCountTypeIds.Add(reader.ReadInt32());
            for (int i = 0; i < maxCountCount; i++)
                ComponentMaxCountValues.Add(reader.ReadInt32());
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
        /// Sets game-specific custom metadata. Can be injected at any point during recording.
        /// </summary>
        public void SetGameCustomData(byte[] data)
        {
            _metadata.GameCustomData = data;
        }

        /// <summary>
        /// Sets the initial state snapshot with the hash and the tick it was taken at. Can be injected at
        /// any point during recording. On playback, restored via RestoreFromFullState instead of
        /// OnInitializeWorld.
        /// </summary>
        public void SetInitialStateSnapshot(byte[] data, long hash, int tick)
        {
            _metadata.InitialStateSnapshot = data;
            _metadata.InitialStateHash = hash;
            _metadata.InitialStateTick = tick;
        }

        /// <summary>
        /// Records the roster the tick-0 world was built from, in creation order. Only a recording that
        /// actually built that world calls this — see <see cref="ReplayMetadata.InitialRoster"/>.
        /// </summary>
        public void SetInitialRoster(IReadOnlyList<int> roster)
        {
            _metadata.InitialRoster.Clear();
            if (roster == null) return;
            for (int i = 0; i < roster.Count; i++)
                _metadata.InitialRoster.Add(roster[i]);
        }

        /// <summary>
        /// Records the per-player verified data the tick-0 world was built from, in roster order. The
        /// caller passes one entry per <see cref="ReplayMetadata.InitialRoster"/> slot (null where a player
        /// had none); the concat + lengths layout is built here so exactly one place knows it.
        ///
        /// <para>Call it beside <c>SetInitialRoster</c> and from the same peer — the two are index-parallel
        /// and a file carrying one without the other cannot be sliced.</para>
        /// </summary>
        public void SetInitialEntitlements(IReadOnlyList<byte[]> perRosterEntry)
        {
            _metadata.InitialEntitlementLengths.Clear();
            _metadata.InitialEntitlementData = null;
            if (perRosterEntry == null || perRosterEntry.Count == 0) return;

            int total = 0;
            for (int i = 0; i < perRosterEntry.Count; i++)
            {
                int len = perRosterEntry[i]?.Length ?? 0;
                _metadata.InitialEntitlementLengths.Add(len);
                total += len;
            }
            if (total == 0) return;   // every player had none — lengths alone carry that, no payload needed

            var data = new byte[total];
            int offset = 0;
            for (int i = 0; i < perRosterEntry.Count; i++)
            {
                var entry = perRosterEntry[i];
                if (entry == null || entry.Length == 0) continue;
                Buffer.BlockCopy(entry, 0, data, offset, entry.Length);
                offset += entry.Length;
            }
            _metadata.InitialEntitlementData = data;
        }

        /// <summary>
        /// Sets the reproduction anchors — the four fingerprint terms describing the code and content this
        /// recording ran against. Written when the initial snapshot is set so both describe the same instant
        /// (the navigation term moves with runtime rebakes).
        /// </summary>
        public void SetReproductionAnchors(long layoutFingerprint, long staticColliderFingerprint, long navFingerprint, long gameFingerprint)
        {
            _metadata.LayoutFingerprint = layoutFingerprint;
            _metadata.StaticColliderFingerprint = staticColliderFingerprint;
            _metadata.NavFingerprint = navFingerprint;
            _metadata.GameFingerprint = gameFingerprint;
        }

        /// <summary>
        /// Initializes replay data for recording — copies all SimulationConfig fields into metadata.
        /// </summary>
        public void Initialize(int playerCount, ISimulationConfig simConfig, int randomSeed)
        {
            _metadata.PlayerCount = playerCount;
            _metadata.CopySimulationConfigFrom(simConfig);  // 15 fields restored on playback + 2 layout inputs recorded only
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

            // Rollback re-promotion re-records whole windows every rollback; most
            // re-records are byte-identical. Overwrite in place when the new serialization is the same
            // length as the existing entry — keeps the buffer from growing on each rollback (the dominant
            // term). A different length (e.g. empty-fill → real command) falls through to append; that
            // growth is bounded by the correction count. Old bytes of an appended re-record stay dead in
            // the buffer (see FinalizeRecording compaction, option (c), if that ever matters).
            if (_tickOffsets.TryGetValue(tick, out var existing) && existing.length == size)
            {
                int rewritten = factory.SerializeCommandsTo(_buffer.AsSpan(existing.offset, size));
                _tickOffsets[tick] = (existing.offset, rewritten);
                UpdateTotalTicks(tick);
                return;
            }

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
        public void FinalizeRecording(int totalTicks, ReplayEndReason endReason)
        {
            _metadata.TotalTicks = totalTicks;
            _metadata.DurationMs = (long)_metadata.TotalTicks * _metadata.TickIntervalMs;
            _metadata.EndReason = endReason;
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

            // != , not >: a lower version is a DIFFERENT layout, not an older-but-readable one —
            // this format has no version branch, so letting it through means silent misparsing.
            if (_metadata.Version != ReplayMetadata.CURRENT_VERSION)
                throw new InvalidDataException(
                    $"Unsupported replay version {_metadata.Version} (this build reads {ReplayMetadata.CURRENT_VERSION}). " +
                    "Replay files are not backward compatible — re-record this replay.");

            // PlayerCount and InitialRoster record the same thing twice. Empty roster is the one legal
            // disagreement ("this recording did not build tick 0"); any other is a corrupted file, and
            // letting it through means reconstructing a world with the wrong number of participants.
            // Checked after the version guard so an older file still gets the version message.
            if (_metadata.InitialRoster.Count != 0 && _metadata.InitialRoster.Count != _metadata.PlayerCount)
                throw new InvalidDataException(
                    $"Replay metadata is inconsistent: InitialRoster has {_metadata.InitialRoster.Count} entries but PlayerCount is {_metadata.PlayerCount}.");

            // The per-player data is sliced by roster index, so a count that disagrees would hand each
            // player someone else's bytes. Empty is legal (no issuer); anything else must line up.
            if (_metadata.InitialEntitlementLengths.Count != 0
                && _metadata.InitialEntitlementLengths.Count != _metadata.InitialRoster.Count)
                throw new InvalidDataException(
                    $"Replay metadata is inconsistent: {_metadata.InitialEntitlementLengths.Count} entitlement entries "
                    + $"but InitialRoster has {_metadata.InitialRoster.Count}.");

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

