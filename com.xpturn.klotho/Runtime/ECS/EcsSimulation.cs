using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// ISimulation implementation — owns Frame + SystemRunner.
    /// Can be injected via the ISimulation interface without modifying KlothoEngine code.
    /// </summary>
    public class EcsSimulation : ISimulation
    {
        private IKLogger _logger;
        private Frame _frame;
        private readonly SystemRunner _systemRunner;
        private readonly FrameRingBuffer _ringBuffer;
        private readonly int _deltaTimeMs;
        private byte[] _hashBuffer = Array.Empty<byte>();
        private IDataAssetRegistryBuilder _registryBuilder;

        // Desync-diagnostic hash history. _breakdown is the reusable layered-hash scratch that
        // GetStateHash fills when history is enabled; _hashHistory is the numeric ring, built lazily once
        // the participant set is stable. All inert (no cost / no allocation) when _hashHistoryCapacity==0.
        private readonly StateHashBreakdown _breakdown = new StateHashBreakdown();
        private int _hashHistoryCapacity;
        private StateHashRing _hashHistory;
        private int[] _flushSlotOrder = Array.Empty<int>();

        public int CurrentTick => _frame.Tick;

        // Reused payload for GetActiveMatchEnd — callers (resync backstop) do not retain it.
        private readonly MatchEndStatePayload _matchEndPayload = new MatchEndStatePayload();

        /// <inheritdoc />
        public bool IsMatchEndedState
        {
            // TryGetSingleton (NOT GetSingleton): the singleton is absent in spectator/pre-init and on
            // an SD client before its first FullState — absence means "not ended", never throw.
            get => _frame.TryGetSingleton<MatchEndStateComponent>(out var e)
                   && _frame.GetReadOnly<MatchEndStateComponent>(e).Ended;
        }

        /// <inheritdoc />
        public xpTURN.Klotho.Core.IMatchEndEvent GetActiveMatchEnd()
        {
            if (!_frame.TryGetSingleton<MatchEndStateComponent>(out var e))
                return null;
            ref readonly var c = ref _frame.GetReadOnly<MatchEndStateComponent>(e);
            if (!c.Ended)
                return null;
            _matchEndPayload.WinnerPlayerId = c.WinnerPlayerId;
            return _matchEndPayload;
        }

        // Engine-agnostic IMatchEndEvent backing the backstop (EcsSimulation cannot build a game event
        // type). Carries the winner from MatchEndStateComponent; Reason is a sentinel ("resync") because
        // the original game reason is not persisted in state — the game subtype is not
        // preserved on the backstop path, which is sufficient for the one-way termination notification.
        private sealed class MatchEndStatePayload : xpTURN.Klotho.Core.IMatchEndEvent
        {
            private static readonly FixedString32 s_resyncReason = FixedString32.FromString("resync");
            public int WinnerPlayerId;
            int xpTURN.Klotho.Core.IMatchEndEvent.WinnerPlayerId => WinnerPlayerId;
            FixedString32 xpTURN.Klotho.Core.IMatchEndEvent.Reason => s_resyncReason;
        }

        /// <summary>
        /// Scenario C: when &gt;= 0, XORs a salt into the hash of the named execution tick
        /// (evaluated at _frame.Tick == value+1, the post-execution hash point) so an armed SD client's
        /// resim mismatches the server's verified hash, driving a desync-resync FullState request. -1
        /// disables. Set by the engine only on the targeted client, never the server. Hash-only and
        /// tick-gated, so a FullState restore past the tick auto-recovers.
        ///
        /// <para>Field is compiled UNCONDITIONALLY (not under <c>#if KLOTHO_FAULT_INJECTION</c>) so the
        /// EcsSimulation serialization layout is identical between the Editor and a player build whose
        /// Scripting Define Symbols differ — otherwise the player build fails with "script class layout
        /// is incompatible between the editor and the player". The salt is still applied only under the
        /// define (see GetStateHash) and the field is only set by the engine under the define, so it is
        /// inert (default -1) in non-fault-injection builds.</para>
        /// </summary>
        public int ForceDesyncHashTick = -1;

        /// <summary>
        /// Positive-control state corruption (armed by the engine from FaultInjection.RegisterStateCorruption).
        /// When <c>_frame.Tick == StateCorruptionTick</c> at the end of <see cref="Tick"/>, the mutator
        /// mutates the live post-execution Frame — before the caller's SaveSnapshot/GetStateHash, so the
        /// corruption is both hashed (→ divergence detected) and snapshotted. It re-fires on EVERY
        /// (re-)execution of the tick (verified/rollback resim re-runs Tick()), mirroring the hash salt's
        /// monotonic tick gate, so the divergence is not silently washed out; a FullState restore
        /// past the tick stops re-execution and recovery is clean.
        ///
        /// <para>Compiled unconditionally (like <see cref="ForceDesyncHashTick"/>) for editor/player
        /// serialization-layout parity; the mutator is only invoked under <c>#if KLOTHO_FAULT_INJECTION</c>
        /// and only set by the engine under that define, so it is inert (null / -1) otherwise.</para>
        /// </summary>
        public int StateCorruptionTick = -1;
        public System.Action<Frame> StateCorruptionMutator;

        public EcsSimulation(int maxEntities, int maxRollbackTicks = 10, int deltaTimeMs = 50, IKLogger logger = null, IDataAssetRegistryBuilder registryBuilder = null, IDataAssetRegistry assetRegistry = null, IReadOnlyDictionary<int, int> maxCountOverrides = null, IReadOnlyCollection<int> prunedComponentTypeIds = null)
        {
            if (assetRegistry != null && registryBuilder != null)
                throw new ArgumentException("Cannot specify both assetRegistry and registryBuilder.");

            // Freeze the component layout with the config maxCount overrides + reservation-pruning denylist
            // before any Frame is built (the Frame ctor's no-arg EnsureLayoutComputed is then idempotent).
            // Single seam for the process-global layout inputs.
            ComponentStorageRegistry.EnsureLayoutComputed(maxEntities, maxCountOverrides, prunedComponentTypeIds);

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

        /// <summary>
        /// Returns the first registered system instance assignable to <typeparamref name="T"/>,
        /// or <c>null</c> if none. Useful when a callback boundary needs to expose a system's
        /// secondary interface (e.g. <c>IFPPhysicsWorldProvider</c> implemented by
        /// <c>PhysicsSystem</c>) without storing a process-wide static reference.
        /// </summary>
        public T GetSystem<T>() where T : class => _systemRunner.Find<T>();

        /// <summary>
        /// Returns true and assigns <paramref name="system"/> if a matching system is registered.
        /// </summary>
        public bool TryGetSystem<T>(out T system) where T : class
        {
            system = _systemRunner.Find<T>();
            return system != null;
        }

        /// <summary>
        /// Appends every registered system instance assignable to <typeparamref name="T"/>
        /// into <paramref name="buffer"/>. Returns the appended count.
        /// </summary>
        public int GetSystems<T>(List<T> buffer) where T : class
            => _systemRunner.FindAll(buffer);

        /// <summary>
        /// Enables the per-system perf monitor (opt-in diagnostic; forwards to SystemRunner).
        /// Determinism-neutral, off by default. Armed by KlothoEngine from the config gate.
        /// </summary>
        public void EnableSystemPerfMonitor(int warmupExecutions = 0)
            => _systemRunner.EnablePerfMonitor(warmupExecutions);

        /// <summary>
        /// Per-system perf profile as text, or null when the monitor is off (caller guards on null).
        /// Only-allocating path; called once at engine Stop.
        /// </summary>
        public string AppendSystemPerfLog() => _systemRunner.PerfMonitor?.ToText();

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

#if KLOTHO_FAULT_INJECTION
            // Positive-control state corruption: mutate the post-execution state of the armed tick on every
            // (re-)execution (before Tick++ so _frame.Tick is the executed tick; before the caller's
            // SaveSnapshot/GetStateHash so the corruption is hashed + snapshotted). See StateCorruptionTick.
            if (StateCorruptionMutator != null && _frame.Tick == StateCorruptionTick)
                StateCorruptionMutator(_frame);
#endif

            _frame.Tick++;
        }

        public void Rollback(int targetTick)
        {
            _ringBuffer.RestoreFrame(targetTick, _frame);
            if (_snapshotParticipants.Count > 0)
                _ringBuffer.RestoreSystemState(targetTick, _snapshotParticipants);
        }

        /// <summary>
        /// Copies the live frame + participating system state into a caller-owned side buffer,
        /// outside the rollback ring (the ring stores pre-tick snapshots only). SyncTest uses
        /// this to keep the post-forward state so a failed check can restore the forward branch
        /// instead of leaving the diverged resim branch live.
        /// </summary>
        public void CaptureStateTo(Frame targetFrame, ref byte[] systemStateBuffer)
        {
            targetFrame.CopyFrom(_frame);
            if (_snapshotParticipants.Count == 0) return;

            int totalSize = 0;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                totalSize += _snapshotParticipants[i].GetSnapshotSize();

            if (systemStateBuffer == null || systemStateBuffer.Length < totalSize)
                systemStateBuffer = new byte[totalSize];

            var writer = new SpanWriter(systemStateBuffer);
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].SaveSnapshot(ref writer);
        }

        /// <summary>
        /// Restores the live frame + participating system state from a side buffer previously
        /// filled by <see cref="CaptureStateTo"/>.
        /// </summary>
        public void RestoreStateFrom(Frame sourceFrame, byte[] systemStateBuffer)
        {
            _frame.CopyFrom(sourceFrame);
            if (_snapshotParticipants.Count == 0 || systemStateBuffer == null) return;

            var reader = new SpanReader(systemStateBuffer);
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].RestoreSnapshot(ref reader);
        }

        public long GetStateHash() => GetStateHash(_hashHistoryCapacity > 0 ? _breakdown : null);

        // Layered state hash. When <paramref name="breakdown"/> is non-null, records the frame-level
        // hash, the per-component-type hashes/counts (via Frame.CalculateHash) and the per-participant
        // system hashes as a byproduct. Snapshot participants are now hashed one at a time from a
        // fresh FNV seed and folded individually (replacing the single concatenated HashBytes), matching
        // SerializeFullStateWithHash so GetStateHash() stays equal to the FullState hash verified at
        // resync restore. The fault-injection salt applies to Total only, leaving every layer hash intact
        // (so the negative control reports "all layers match, Total differs").
        public long GetStateHash(StateHashBreakdown breakdown)
        {
            ulong total = _frame.CalculateHash(breakdown);

            int pc = _snapshotParticipants.Count;
            breakdown?.EnsureSystemCapacity(pc);

            for (int i = 0; i < pc; i++)
            {
                int size = _snapshotParticipants[i].GetSnapshotSize();
                if (_hashBuffer.Length < size)
                    _hashBuffer = new byte[size];

                var writer = new SpanWriter(_hashBuffer);
                _snapshotParticipants[i].SaveSnapshot(ref writer);

                ulong sysHash = FPHash.HashBytes(FPHash.FNV_OFFSET,
                    new ReadOnlySpan<byte>(_hashBuffer, 0, writer.Position));
                total = FPHash.Hash(total, sysHash);
                if (breakdown != null)
                    breakdown.SystemHashes[i] = sysHash;
            }

#if KLOTHO_FAULT_INJECTION
            // Scenario C: salt the hash of one execution tick on the armed client so its
            // resim diverges from the server's verified hash (determinism-failure path → FullStateRequest).
            // The verified compare hashes after Tick() increments, so the post-execution hash of tick N
            // is taken at _frame.Tick == N+1. Hash-only (no state mutation) + monotonic tick gate → a
            // FullState restore past N stops the salt and recovery is clean.
            if (ForceDesyncHashTick >= 0 && _frame.Tick == ForceDesyncHashTick + 1)
                total ^= 0x5DE59C00DEADF00DUL;
#endif

            if (breakdown != null)
                breakdown.Total = (long)total;

            return (long)total;
        }

        public void Reset()
        {
            _frame.Clear();
            _frame.Tick = 0;
            _frame.DeltaTimeMs = _deltaTimeMs;
        }

        // Diagnostic — delegates to Frame for per-component hash dump.
        public void LogComponentHashes(IKLogger logger, string label, KLogLevel logLevel = KLogLevel.Debug)
            => _frame.LogComponentHashes(logger, label, logLevel);

        // Diagnostic — one-line fingerprint of the registered static geometry (count + per-collider
        // fields). Static colliders are intentionally not part of the state hash or snapshot, so this
        // log is the only way a server/client log diff surfaces a static mismatch at setup time.
        public void LogStaticFingerprint(IKLogger logger, string label, KLogLevel logLevel = KLogLevel.Information)
        {
            if (logger == null || !logger.IsEnabled(logLevel)) return;
            var svc = GetSystem<IStaticColliderService>();
            if (svc == null) return;
            svc.GetStaticColliders(out _, out int count);
            long fp = svc.GetStaticFingerprint();
            logger.Log(logLevel, $"[Physics][StaticGeometry] {label}: count={count} fp=0x{fp:X16}", null);
        }

        // ── diagnostic hash history ─────────────────────────────────────

        /// <summary>True when the per-tick hash-history ring is active.</summary>
        public bool HashHistoryEnabled => _hashHistoryCapacity > 0;

        /// <summary>
        /// Sets the hash-history retention window (ticks); 0 disables. Called by the engine from
        /// <c>ISimulationConfig.DiagnosticHistoryTicks</c>. The ring is built lazily on first record, when
        /// the registered-type and participant counts are stable.
        /// </summary>
        public void SetHashHistoryCapacity(int capacityTicks)
        {
            _hashHistoryCapacity = capacityTicks > 0 ? capacityTicks : 0;
            if (_hashHistoryCapacity == 0)
                _hashHistory = null;
        }

        // Builds/resizes the ring to the current type + participant counts (one-time on the steady path)
        // and sizes the reusable breakdown so GetStateHash fills it allocation-free.
        private void EnsureHashRing()
        {
            int typeCount = ComponentStorageRegistry.RegisteredTypeIdsSorted.Length;
            int pc = _snapshotParticipants.Count;
            if (_hashHistory == null || _hashHistory.TypeCount != typeCount || _hashHistory.ParticipantCount != pc)
            {
                _hashHistory = new StateHashRing(_hashHistoryCapacity, typeCount, pc);
                if (_flushSlotOrder.Length < _hashHistoryCapacity)
                    _flushSlotOrder = new int[_hashHistoryCapacity];
            }
            _breakdown.EnsureComponentCapacity(typeCount);
            _breakdown.EnsureSystemCapacity(pc);
        }

        /// <summary>
        /// Records the current breakdown for <paramref name="tick"/> into the ring (copy only). The caller
        /// must have just computed <see cref="GetStateHash()"/> for this state — used at the SD paths and
        /// P2P check ticks that already hash, so there is no extra hash cost.
        /// </summary>
        public void RecordHashHistory(int tick)
        {
            if (_hashHistoryCapacity == 0) return;
            EnsureHashRing();
            _hashHistory.Record(tick, _breakdown);
        }

        /// <summary>
        /// Reads the recorded total/frame hash for <paramref name="tick"/> from the diagnostic history
        /// ring. False when history is disabled, the tick was never recorded, or its slot has been
        /// overwritten by a later tick (wraparound — the slot's tick label no longer matches). Read-only:
        /// this is the shape the L1 probe responder serves from, and it lets tests pin ring
        /// coherence after a rollback re-record without reaching into the internal ring.
        /// </summary>
        public bool TryGetHashHistory(int tick, out long total, out ulong frameHash)
        {
            total = 0;
            frameHash = 0;
            return _hashHistory != null && _hashHistory.TryGet(tick, out total, out frameHash);
        }

        /// <summary>
        /// Computes the layered hash for the current state and records it for <paramref name="tick"/>. Used
        /// at P2P non-check / rollback-resim ticks where the hash is not otherwise computed — the new
        /// per-tick cost the config buys. Re-recording during rollback resim keeps the ring on the
        /// corrected timeline so L1 does not mis-attribute the divergence to a misprediction point.
        /// </summary>
        public void ComputeAndRecordHashHistory(int tick)
        {
            if (_hashHistoryCapacity == 0) return;
            EnsureHashRing();
            GetStateHash(_breakdown);
            _hashHistory.Record(tick, _breakdown);
        }

        // Last tick a history dump was emitted for. Replaces the old "clear the ring after dumping"
        // de-duplication — see FlushHashHistory.
        private int _lastFlushedDumpTick = int.MinValue;

        /// <summary>
        /// Dumps the accumulated hash history to the log, oldest first. No-op when history is disabled or
        /// empty, or when this tick has already been dumped. Cold path (post-desync) — string formatting
        /// is allowed here (the zero-GC boundary is "after detection"). Each line carries the total, the
        /// frame hash, every component-type hash/count and every snapshot-participant hash.
        ///
        /// <para><b>The ring is NOT cleared.</b> It used to be, purely to keep a re-firing desync from
        /// re-dumping the same lines — but the ring is also the only thing the online probe can
        /// serve a REMOTE peer from, and the answer is asked for an RTT after the flush. In P2P both
        /// peers desync on the same tick and both flush, so clearing here wiped exactly the data the
        /// other peer was about to ask for, on both sides at once (SD never showed this: the server does
        /// not desync, so it never flushed). The ring is fixed-size and overwrites itself by tick label,
        /// so keeping it costs nothing — only the LOG needed de-duplication, and a dumped-tick marker
        /// does that without destroying the data.</para>
        /// </summary>
        public void FlushHashHistory(IKLogger logger, int dumpTick)
        {
            if (logger == null || _hashHistory == null || !logger.IsEnabled(KLogLevel.Information)) return;
            if (dumpTick == _lastFlushedDumpTick) return;   // same episode re-firing — dump once

            int n = _hashHistory.GetOrderedSlots(_flushSlotOrder);
            if (n == 0) return;

            _lastFlushedDumpTick = dumpTick;

            logger.KInformation($"[Desync][Diag][History] dumpTick={dumpTick} depth={n}");
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            var sb = new System.Text.StringBuilder(256);
            for (int i = 0; i < n; i++)
            {
                int slot = _flushSlotOrder[i];
                var comp = _hashHistory.ComponentHashesAt(slot);
                var counts = _hashHistory.ComponentCountsAt(slot);
                var sys = _hashHistory.SystemHashesAt(slot);

                sb.Clear();
                sb.Append("[Desync][Diag][HashLine] tick=").Append(_hashHistory.TickAt(slot))
                  .Append(" total=0x").Append(_hashHistory.TotalAt(slot).ToString("X16"))
                  .Append(" frame=0x").Append(_hashHistory.FrameHashAt(slot).ToString("X16"));
                for (int t = 0; t < typeIds.Length && t < comp.Length; t++)
                {
                    string name = ComponentStorageRegistry.GetType(typeIds[t])?.Name ?? "?";
                    sb.Append(' ').Append(name).Append('=').Append(counts[t]).Append("/0x").Append(comp[t].ToString("X16"));
                }
                for (int p = 0; p < sys.Length; p++)
                    sb.Append(" sys[").Append(p).Append(']').Append(_snapshotParticipants[p].GetType().Name).Append("=0x").Append(sys[p].ToString("X16"));

                // Raw Log (not the interpolated-handler extension) so the pre-built line passes through.
                logger.Log(KLogLevel.Information, sb.ToString(), null);
            }
        }

        // ── online probe access to the history ring ─────────────────────
        //
        // FlushHashHistory dumps the ring to the log and clears it; the probe instead needs the numbers
        // themselves, and needs them to OUTLIVE the flush and the rollback re-sim that follow a
        // detection. These accessors are the only way out of the ring for a caller outside this class.

        /// <summary>Live capacity of the history ring (0 when history is disabled).</summary>
        public int HashHistoryCapacity => _hashHistory?.Capacity ?? 0;

        /// <summary>Component-type slots per recorded tick — the length of a breakdown's component arrays.</summary>
        public int HashHistoryTypeCount => _hashHistory?.TypeCount ?? 0;

        /// <summary>Snapshot-participant slots per recorded tick — the length of a breakdown's system array.</summary>
        public int HashHistoryParticipantCount => _hashHistory?.ParticipantCount ?? 0;

        /// <summary>
        /// Fills <paramref name="ticksOut"/> with every tick currently recorded, oldest first, and
        /// returns the count. <paramref name="ticksOut"/> must be at least
        /// <see cref="HashHistoryCapacity"/> long.
        /// </summary>
        public int GetHashHistoryTicks(int[] ticksOut)
        {
            if (_hashHistory == null || ticksOut == null) return 0;
            if (_flushSlotOrder.Length < _hashHistory.Capacity)
                _flushSlotOrder = new int[_hashHistory.Capacity];

            int n = _hashHistory.GetOrderedSlots(_flushSlotOrder);
            if (ticksOut.Length < n) return 0;
            for (int i = 0; i < n; i++)
                ticksOut[i] = _hashHistory.TickAt(_flushSlotOrder[i]);
            return n;
        }

        /// <summary>
        /// Points the spans at the recorded breakdown for <paramref name="tick"/>. False when history is
        /// disabled, the tick was never recorded, or its slot has since been overwritten.
        /// <para>The spans alias the ring's storage: a subsequent record for the same slot changes them.
        /// Read or copy them before returning to the tick loop — this is what the probe's synchronous
        /// capture (before the rollback re-sim overwrites the slots) and the responder's serve path
        /// (which packs straight into the wire payload) both do.</para>
        /// </summary>
        public bool TryGetHashHistoryBreakdown(int tick, out ReadOnlySpan<ulong> componentHashes,
            out ReadOnlySpan<int> componentCounts, out ReadOnlySpan<ulong> systemHashes, out long total)
        {
            if (_hashHistory != null)
                return _hashHistory.TryGetBreakdown(tick, out componentHashes, out componentCounts, out systemHashes, out total);

            componentHashes = default;
            componentCounts = default;
            systemHashes = default;
            total = 0;
            return false;
        }

        /// <summary>
        /// Registry typeId for a positional index into a breakdown's component arrays, or -1 when out of
        /// range. The wire carries typeIds, not positions: a position is only meaningful against the
        /// exact registry that produced it.
        /// </summary>
        public static int ComponentTypeIdAt(int index)
        {
            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            return (uint)index < (uint)typeIds.Length ? typeIds[index] : -1;
        }

        /// <summary>Type name for a registry typeId, or null when it is not registered on this build.</summary>
        public static string ComponentTypeName(int typeId) => ComponentStorageRegistry.GetType(typeId)?.Name;

        /// <summary>Number of registered snapshot participants — the valid range of a System-layer index.</summary>
        public int SnapshotParticipantCount => _snapshotParticipants.Count;

        /// <summary>Name of the i-th snapshot participant, or null when out of range.</summary>
        public string SnapshotParticipantName(int index)
            => (uint)index < (uint)_snapshotParticipants.Count ? _snapshotParticipants[index].GetType().Name : null;

        /// <summary>
        /// Logs the current state's layered hash — per-component-type AND per-snapshot-participant
        /// (the former blind spot) — so a desync log surfaces the diverging layer without waiting for
        /// the offline dump. Cold path. Computes a fresh breakdown, independent of whether history is
        /// enabled (used at SD detection, where the live sim is exactly at the diverged tick).
        /// </summary>
        public void LogStateBreakdown(IKLogger logger, string label, KLogLevel logLevel = KLogLevel.Information)
        {
            if (logger == null || !logger.IsEnabled(logLevel)) return;

            GetStateHash(_breakdown);   // fills + sizes _breakdown

            var typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            logger.KLog(logLevel, $"[Desync][Diag][{label}] tick={_frame.Tick} total=0x{_breakdown.Total:X16} frame=0x{_breakdown.FrameHash:X16} components={typeIds.Length} systems={_snapshotParticipants.Count}");
            for (int i = 0; i < typeIds.Length; i++)
            {
                string name = ComponentStorageRegistry.GetType(typeIds[i])?.Name ?? "?";
                logger.KLog(logLevel, $"[Desync][Diag][{label}] layer=Component type={name}({typeIds[i]}) count={_breakdown.ComponentCounts[i]} hash=0x{_breakdown.ComponentHashes[i]:X16}");
            }
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                logger.KLog(logLevel, $"[Desync][Diag][{label}] layer=System participant={i}({_snapshotParticipants[i].GetType().Name}) hash=0x{_breakdown.SystemHashes[i]:X16}");
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

            // Serialize participants — fold each one individually from a fresh FNV seed, matching
            // GetStateHash's per-participant fold so the two hashes stay equal.
            ulong total = frameHash;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
            {
                int pStart = writer.Position;
                _snapshotParticipants[i].SaveSnapshot(ref writer);
                ulong sysHash = FPHash.HashBytes(FPHash.FNV_OFFSET,
                    new ReadOnlySpan<byte>(combined, pStart, writer.Position - pStart));
                total = FPHash.Hash(total, sysHash);
            }

            return (combined, (long)total);
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

        /// <summary>
        /// Reads the retained snapshot-participant (system) bytes for <paramref name="tick"/> off the ring
        /// without disturbing the live participants. Pairs with <see cref="TryGetSnapshotFrame"/>
        /// so a diagnostic capture can reconstruct the full T₀ state (frame + system) after the live
        /// simulation has already advanced past T₀. Returns false when the tick is unavailable or the
        /// simulation has no snapshot participants.
        /// </summary>
        public bool TryGetSnapshotSystemState(int tick, out ReadOnlySpan<byte> systemState)
            => _ringBuffer.TryGetSystemState(tick, _frame.Tick, out systemState);

        public int GetNearestRollbackTick(int targetTick) => GetNearestSnapshotTick(targetTick);

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
