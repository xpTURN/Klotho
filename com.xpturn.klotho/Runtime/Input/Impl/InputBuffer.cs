using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Input
{
    /// <summary>
    /// Storage outcome of <see cref="InputBuffer.AddCommandChecked"/>.
    /// Ownership contract: the buffer owns accepted instances (Stored/Replaced) and returns
    /// them to CommandPool only in cleanup (Clear/ClearBefore/ClearAfter); it never returns a
    /// rejected arrival — disposal of Dropped* instances (CommandPool.Return or GC) is the
    /// caller's responsibility, and AlreadyStored arrivals are buffer property that callers
    /// must NOT return.
    /// </summary>
    public enum CommandStoreResult
    {
        /// <summary>Accepted into an empty slot — the buffer takes ownership.</summary>
        Stored,
        /// <summary>Accepted, replacing a different stored instance (overwrite path). The buffer
        /// owns the new instance; the displaced one is left to GC (ownership unprovable).</summary>
        Replaced,
        /// <summary>The arriving instance IS the stored instance (ReferenceEquals — e.g. a
        /// double-dispatched arrival re-entering AddCommand). Buffer property: never Return.</summary>
        AlreadyStored,
        /// <summary>Dropped: a different instance already occupies (tick, playerId) and overwrite
        /// was not requested (keep-first).</summary>
        DroppedDuplicate,
        /// <summary>Dropped: (tick, playerId) is sealed by a range-fill placeholder.</summary>
        DroppedSealed,
        /// <summary>Dropped: the command was null.</summary>
        DroppedNull,
    }

    /// <summary>
    /// Input buffer implementation.
    /// Ownership rule: the buffer owns only the instances it accepted and returns
    /// them to CommandPool exclusively in cleanup (Clear/ClearBefore/ClearAfter). It never
    /// returns a rejected arrival — callers that can prove sole ownership handle it via the
    /// AddCommandChecked result; result-discarding paths (void AddCommand) leave rejects to GC.
    /// </summary>
    public class InputBuffer : IInputBuffer
    {
        // tick -> playerId -> command
        private readonly Dictionary<int, Dictionary<int, ICommand>> _commands
            = new Dictionary<int, Dictionary<int, ICommand>>();

        // tick -> system command list (ISystemCommand only)
        private readonly Dictionary<int, List<ICommand>> _systemCommands
            = new Dictionary<int, List<ICommand>>();

        // Per-player high-water sequence number for client-side reliable-command dedup
        // (see AddSystemCommand). Monotone per match; reset only on full Clear (match reset).
        private readonly Dictionary<int, int> _reliableSeqHighWater
            = new Dictionary<int, int>();

        private int _oldestTick = int.MaxValue;
        private int _newestTick = int.MinValue;

        // Cached lists for object pooling (GC prevention)
        private readonly List<ICommand> _commandListCache = new List<ICommand>();
        private readonly List<int> _ticksToRemoveCache = new List<int>();
        private readonly List<long> _sealKeysToRemoveCache = new List<long>();

        // (tick << 32) | (uint)playerId — entries sealed by range fill. AddCommand skips any
        // later real command arrival at a sealed (tick, playerId) to prevent InputBuffer ↔
        // simulation state divergence (the empty placeholder has already been consumed by chain
        // advance). Cleared by ClearBefore for collected ticks.
        private readonly HashSet<long> _sealedTickPlayer = new HashSet<long>();

        private IKLogger _logger;

        public void SetLogger(IKLogger logger) => _logger = logger;

#if DEBUG || DEVELOPMENT_BUILD
        private bool _resimulating;

        internal void SetResimulating(bool value) => _resimulating = value;
#endif

        public int Count
        {
            get
            {
                int count = 0;
                foreach (var tickCommands in _commands.Values)
                    count += tickCommands.Count;
                return count;
            }
        }

        public int OldestTick => _oldestTick == int.MaxValue ? 0 : _oldestTick;
        public int NewestTick => _newestTick == int.MinValue ? 0 : _newestTick;

        public void AddCommand(ICommand command) => AddCommandChecked(command, overwriteExisting: false);

        /// <summary>
        /// SD client verified-batch variant: the verified command must replace the stored
        /// predicted/issued local input even when content differs — e.g. the server substituted
        /// EmptyCommand for a late input; keeping the local real input would diverge the resim
        /// from the server's verified state. The displaced instance is intentionally NOT returned
        /// to the pool: its ownership cannot be proven (aliasing), and a GC'd leak is safer than
        /// a poisoned pool. Result-discarding wrapper — callers that manage
        /// instance ownership use AddCommandChecked.
        /// </summary>
        public void AddCommandOverwrite(ICommand command) => AddCommandChecked(command, overwriteExisting: true);

        /// <summary>
        /// Adds a command and reports the storage outcome — see <see cref="CommandStoreResult"/>
        /// for the ownership contract. The buffer never returns a rejected arrival to
        /// CommandPool; checked callers dispose Dropped* instances themselves when they can prove
        /// sole ownership, and must never Return an AlreadyStored arrival (buffer property).
        /// </summary>
        public CommandStoreResult AddCommandChecked(ICommand command, bool overwriteExisting = false)
        {
            if (command == null)
                return CommandStoreResult.DroppedNull;

#if DEBUG || DEVELOPMENT_BUILD
            if (_resimulating)
            {
                _logger?.KError($"[InputBuffer] AddCommand called during re-simulation: tick={command.Tick}, playerId={command.PlayerId}, type={command.GetType().Name}. Predicted commands must go into _tickCommandsCache/_pendingCommands only.");
                System.Diagnostics.Debug.Assert(false,
                    "InputBuffer.AddCommand must not be called during re-simulation. " +
                    "Predicted commands should only go into _tickCommandsCache/_pendingCommands.");
            }
#endif

            if (command is ISystemCommand)
            {
                AddSystemCommand(command);
                return CommandStoreResult.Stored;
            }

            int tick = command.Tick;
            int playerId = command.PlayerId;

            // Seal guard: if this (tick, playerId) was filled with an empty placeholder by the
            // range fill path and chain has already advanced past it, silently drop the late
            // real command to keep InputBuffer and simulation state consistent. No pool return
            // here: disposal moved to checked callers that own the arrival.
            long sealKey = ((long)tick << 32) | (uint)playerId;
            if (_sealedTickPlayer.Contains(sealKey))
            {
#if DEBUG || DEVELOPMENT_BUILD
                // The arrival must not be the stored placeholder itself — a caller returning
                // DroppedSealed rejects to the pool would hand back a buffer-owned instance.
                // ClearAfter removes commands but keeps seals, so an empty slot is legitimate;
                // compare only when an entry exists.
                if (_commands.TryGetValue(tick, out var sealedTickCommands)
                    && sealedTickCommands.TryGetValue(playerId, out var sealedStored))
                {
                    System.Diagnostics.Debug.Assert(!ReferenceEquals(sealedStored, command),
                        "DroppedSealed arrival is the buffer-owned placeholder — caller-side Return would poison the pool.");
                }
#endif
                return CommandStoreResult.DroppedSealed;
            }

            if (!_commands.TryGetValue(tick, out var tickCommands))
            {
                // Get from dictionary pool (GC prevention)
                tickCommands = DictionaryPoolHelper.GetIntDictionary<ICommand>();
                _commands[tick] = tickCommands;
            }

            if (tickCommands.TryGetValue(playerId, out var existing))
            {
                // Same arrival instance re-entering AddCommand (double-dispatch insurance — the
                // engine's Initialize re-entry guard removes the known vector).
                // Checked regardless of overwrite mode: the instance is buffer property in both,
                // and reporting it as a Dropped* would let a checked caller Return it — the
                // exact pool poisoning this closed.
                if (ReferenceEquals(existing, command))
                    return CommandStoreResult.AlreadyStored;

                if (overwriteExisting)
                {
                    // Verified overwrite: the displaced instance is left to GC (see
                    // AddCommandOverwrite — ownership unprovable).
                    tickCommands[playerId] = command;
                    UpdateBounds(tick);
                    return CommandStoreResult.Replaced;
                }

                // Keep-first on duplicate (tick, playerId): receive-path duplicates are reliable
                // resends with identical content, so keep-first and last-wins are semantically
                // equal — but the stored instance may be aliased (replay record, diff caches),
                // making last-wins an instance swap under live references. Dropped
                // without a buffer-side pool return; checked callers may Return it when they can
                // prove sole ownership of the arrival.
                return CommandStoreResult.DroppedDuplicate;
            }

            tickCommands[playerId] = command;

            UpdateBounds(tick);
            return CommandStoreResult.Stored;
        }

        // Mark (tick, playerId) as sealed by an empty range-fill placeholder. Any subsequent
        // AddCommand for the same key (e.g. delayed real packet) is silently dropped — see
        // AddCommand seal guard. Seals at ticks below cleanupTick are removed by ClearBefore
        // in lockstep with the buffer entries.
        public void SealEmpty(int tick, int playerId)
        {
            long sealKey = ((long)tick << 32) | (uint)playerId;
            _sealedTickPlayer.Add(sealKey);
        }

        // Remove a seal previously set by SealEmpty. The range-fill authority
        // uses this to restore its own empty placeholder after a rollback (ClearAfter) cleared
        // the command but kept the seal — without it the slot is a sealed-but-no-command hole
        // that no path can repopulate (AddCommand, even overwrite, is rejected by the seal guard),
        // freezing the chain. Pairs with SealEmpty; not for clearing seals on live real input.
        public void Unseal(int tick, int playerId)
        {
            long sealKey = ((long)tick << 32) | (uint)playerId;
            _sealedTickPlayer.Remove(sealKey);
        }

        // Returns true if (tick, playerId) was previously sealed via SealEmpty and not yet
        // cleared by ClearBefore. Used by the network layer to suppress relay of late real
        // packets that would overwrite the empty placeholder on receiving peers.
        public bool IsSealed(int tick, int playerId)
        {
            long sealKey = ((long)tick << 32) | (uint)playerId;
            return _sealedTickPlayer.Contains(sealKey);
        }

        private void AddSystemCommand(ICommand command)
        {
            // Client-side reliable dedup. AddSystemCommand is a dedup-free List.Add, so a reliable
            // command redelivered/reprocessed at the same commit tick would double-execute. The
            // per-player high-water sequence number (reliable-ordered delivery ⇒ gap-free monotone)
            // drops any seq <= high-water silently. Safe against rollback: server-driven reliable lands
            // at a verified tick (below the rollback floor, never wiped/re-added); peer-to-peer preserves
            // committed reliable across the rollback wipe (see ClearAfter) — neither path re-adds.
            if (command is IReliableCommand rel)
            {
                if (_reliableSeqHighWater.TryGetValue(command.PlayerId, out int hw)
                    && rel.SequenceNumber <= hw)
                    return;   // duplicate — ignore (arrival is caller-owned; not buffer property)
                _reliableSeqHighWater[command.PlayerId] = rel.SequenceNumber;
            }

            int tick = command.Tick;
            if (!_systemCommands.TryGetValue(tick, out var list))
            {
                list = new List<ICommand>();
                _systemCommands[tick] = list;
            }
            list.Add(command);

            UpdateBounds(tick);
        }

        private void UpdateBounds(int tick)
        {
            if (tick < _oldestTick)
                _oldestTick = tick;
            if (tick > _newestTick)
                _newestTick = tick;
        }

        public IEnumerable<ICommand> GetCommands(int tick)
        {
            if (_commands.TryGetValue(tick, out var tickCommands))
            {
                return tickCommands.Values;
            }
            return System.Array.Empty<ICommand>();
        }

        public ICommand GetCommand(int tick, int playerId)
        {
            if (_commands.TryGetValue(tick, out var tickCommands))
            {
                if (tickCommands.TryGetValue(playerId, out var command))
                    return command;
            }
            return null;
        }

        public bool HasCommandForTick(int tick)
        {
            return _commands.ContainsKey(tick) && _commands[tick].Count > 0;
        }

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        public void DumpTickRange(int fromTick, int toTick)
        {
            if (_logger == null)
                return;

            var sb = new System.Text.StringBuilder();
            for (int t = fromTick; t <= toTick; t++)
            {
                sb.Append("tick=").Append(t).Append(":[");
                if (_commands.TryGetValue(t, out var tickCommands) && tickCommands.Count > 0)
                {
                    bool first = true;
                    foreach (var kv in tickCommands)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("pid=").Append(kv.Key);
                    }
                }
                sb.Append("] ");
            }
            _logger.KWarning($"[InputBuffer][DumpTickRange] from={fromTick}, to={toTick}, oldest={OldestTick}, newest={NewestTick}, {sb}");
        }

        // Emits a warning for each tick in (verifiedTick, beforeTick) that still holds
        // player commands at the moment of cleanup. Surfaces host self-wipe during P2P
        // quorum stall — wiped commands past the verified horizon are unrecoverable.
        public void LogPendingWipe(int beforeTick, int verifiedTick, int currentTick, System.Action<int, int> onWipe = null)
        {
            if (_logger == null && onWipe == null)
                return;

            int from = System.Math.Max(0, verifiedTick + 1);
            for (int t = from; t < beforeTick; t++)
            {
                if (!_commands.TryGetValue(t, out var tickCommands) || tickCommands.Count == 0)
                    continue;

                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (var kv in tickCommands)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("pid=").Append(kv.Key);
                    onWipe?.Invoke(t, kv.Key);
                }
                _logger?.KWarning($"[InputBuffer][Cleanup] Pending Input WIPED: tick={t}, commands=[{sb}], _lastVerifiedTick={verifiedTick}, CurrentTick={currentTick}, lag={currentTick - verifiedTick}");
            }
        }
#endif

        public bool HasCommandForTick(int tick, int playerId)
        {
            if (_commands.TryGetValue(tick, out var tickCommands))
            {
                return tickCommands.ContainsKey(playerId);
            }
            return false;
        }

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
        public (int lo, int hi) GetBufferedTickRange(int playerId)
        {
            int lo = int.MaxValue;
            int hi = int.MinValue;
            foreach (var kv in _commands)
            {
                if (kv.Value.ContainsKey(playerId))
                {
                    if (kv.Key < lo) lo = kv.Key;
                    if (kv.Key > hi) hi = kv.Key;
                }
            }
            return hi == int.MinValue ? (-1, -1) : (lo, hi);
        }
#endif

        /// <summary>
        /// Checks whether commands from all active players have arrived. Membership check, not a
        /// count: leftover future-tick commands from departed players must not satisfy the quorum.
        /// GC-free — iterates the caller-owned id list against ContainsKey.
        /// </summary>
        public bool HasAllCommands(int tick, List<int> playerIds)
        {
            if (!_commands.TryGetValue(tick, out var tickCommands))
                return false;

            for (int i = 0; i < playerIds.Count; i++)
            {
                if (!tickCommands.ContainsKey(playerIds[i]))
                    return false;
            }
            return true;
        }

        public void ClearBefore(int tick)
        {
            // Use cached list (GC prevention)
            _ticksToRemoveCache.Clear();

            foreach (var t in _commands.Keys)
            {
                if (t < tick)
                    _ticksToRemoveCache.Add(t);
            }

            for (int i = 0; i < _ticksToRemoveCache.Count; i++)
            {
                int t = _ticksToRemoveCache[i];
                // Return dictionary to pool
                if (_commands.TryGetValue(t, out var dict))
                {
                    // if (_logger != null && dict.Count > 0)
                    //     _logger.KDebug($"[InputBuffer] ClearBefore({tick}): discarding tick={t}, playerCommands={dict.Count}");
                    foreach (var cmd in dict.Values)
                        CommandPool.Return(cmd);
                    DictionaryPoolHelper.ReturnIntDictionary(dict);
                }
                _commands.Remove(t);
            }

            // System commands
            _ticksToRemoveCache.Clear();
            foreach (var t in _systemCommands.Keys)
            {
                if (t < tick)
                    _ticksToRemoveCache.Add(t);
            }
            for (int i = 0; i < _ticksToRemoveCache.Count; i++)
            {
                int t = _ticksToRemoveCache[i];
                if (_systemCommands.TryGetValue(t, out var list))
                {
                    // if (_logger != null && list.Count > 0)
                    //     _logger.KDebug($"[InputBuffer] ClearBefore({tick}): discarding tick={t}, systemCommands={list.Count}");
                    for (int j = 0; j < list.Count; j++)
                        CommandPool.Return(list[j]);
                }
                _systemCommands.Remove(t);
            }

            // Discard seals at ticks below cleanup horizon in lockstep with buffer.
            if (_sealedTickPlayer.Count > 0)
            {
                _sealKeysToRemoveCache.Clear();
                foreach (var key in _sealedTickPlayer)
                    if ((int)(key >> 32) < tick)
                        _sealKeysToRemoveCache.Add(key);
                for (int i = 0; i < _sealKeysToRemoveCache.Count; i++)
                    _sealedTickPlayer.Remove(_sealKeysToRemoveCache[i]);
            }

            // Recalculate bounds
            RecalculateBounds();
        }

        public void ClearAfter(int tick)
        {
            // Use cached list (GC prevention)
            _ticksToRemoveCache.Clear();

            foreach (var t in _commands.Keys)
            {
                if (t > tick)
                    _ticksToRemoveCache.Add(t);
            }

            for (int i = 0; i < _ticksToRemoveCache.Count; i++)
            {
                int t = _ticksToRemoveCache[i];
                // Return dictionary to pool
                if (_commands.TryGetValue(t, out var dict))
                {
                    foreach (var cmd in dict.Values)
                        CommandPool.Return(cmd);
                    DictionaryPoolHelper.ReturnIntDictionary(dict);
                }
                _commands.Remove(t);
            }

            // System commands — PRESERVE committed reliable commands across the rollback wipe.
            // A reliable command is placed by the authority at an absolute tick; rolling back earlier
            // ticks doesn't move it, and peer-to-peer has no re-delivery path (reliable-ordered,
            // single-shot), so wiping it here would lose it permanently. Keep IReliableCommand entries;
            // return only non-reliable system commands (e.g. PlayerJoin) to the pool. (Server-driven:
            // reliable lands at the verified floor — below any ClearAfter tick — so this skip is a no-op
            // there. ClearBefore still cleans reliable normally; preservation is ClearAfter-only.)
            _ticksToRemoveCache.Clear();
            foreach (var t in _systemCommands.Keys)
            {
                if (t > tick)
                    _ticksToRemoveCache.Add(t);
            }
            for (int i = 0; i < _ticksToRemoveCache.Count; i++)
            {
                int t = _ticksToRemoveCache[i];
                if (!_systemCommands.TryGetValue(t, out var list))
                    continue;

                bool hasReliable = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j] is IReliableCommand)
                        hasReliable = true;       // keep — committed at an absolute tick
                    else
                        CommandPool.Return(list[j]);
                }

                if (hasReliable)
                    list.RemoveAll(c => !(c is IReliableCommand));   // compact: drop returned non-reliable
                else
                    _systemCommands.Remove(t);
            }

            RecalculateBounds();
        }

        public void Clear()
        {
            // Return all commands and dictionaries to pool
            foreach (var dict in _commands.Values)
            {
                foreach (var cmd in dict.Values)
                    CommandPool.Return(cmd);
                DictionaryPoolHelper.ReturnIntDictionary(dict);
            }
            _commands.Clear();

            // System commands
            foreach (var list in _systemCommands.Values)
            {
                for (int i = 0; i < list.Count; i++)
                    CommandPool.Return(list[i]);
            }
            _systemCommands.Clear();

            _sealedTickPlayer.Clear();
            _reliableSeqHighWater.Clear();   // match reset — fresh reliable seq sequence

            _oldestTick = int.MaxValue;
            _newestTick = int.MinValue;
        }

        private void RecalculateBounds()
        {
            _oldestTick = int.MaxValue;
            _newestTick = int.MinValue;

            foreach (var tick in _commands.Keys)
            {
                if (tick < _oldestTick)
                    _oldestTick = tick;
                if (tick > _newestTick)
                    _newestTick = tick;
            }
            foreach (var tick in _systemCommands.Keys)
            {
                if (tick < _oldestTick)
                    _oldestTick = tick;
                if (tick > _newestTick)
                    _newestTick = tick;
            }
        }

        /// <summary>
        /// Fills a cached list with commands for a specific tick (GC-Free)
        /// Note: the contents of the returned list may change on the next call
        /// </summary>
        public List<ICommand> GetCommandList(int tick)
        {
            _commandListCache.Clear();
            // Player inputs
            if (_commands.TryGetValue(tick, out var tickCommands))
            {
                foreach (var cmd in tickCommands.Values)
                    _commandListCache.Add(cmd);
                _commandListCache.Sort(s_playerCommandComparer);
            }
            // System commands (deterministic order by OrderKey)
            if (_systemCommands.TryGetValue(tick, out var sysCmds))
            {
                sysCmds.Sort(CompareSystemCommands);
                for (int i = 0; i < sysCmds.Count; i++)
                    _commandListCache.Add(sysCmds[i]);
            }
            return _commandListCache;
        }

        private static readonly PlayerCommandComparer s_playerCommandComparer = new();

        private sealed class PlayerCommandComparer : IComparer<ICommand>
        {
            public int Compare(ICommand a, ICommand b)
                => a.PlayerId.CompareTo(b.PlayerId);
        }

        // Single source of truth for system/reliable ordering (shared with KlothoEngine's
        // server-sim/replay comparer) — total-order key chain, see Core.CommandOrdering.
        private static int CompareSystemCommands(ICommand a, ICommand b)
            => Core.CommandOrdering.Compare(a, b);
    }

    /// <summary>
    /// Simple input predictor implementation (repeat last input)
    /// </summary>
    public class SimpleInputPredictor : IInputPredictor
    {
        private int _correctPredictions;
        private int _totalPredictions;

        // CommandFactory for cloning commands during prediction
        private Core.ICommandFactory _commandFactory = new Core.CommandFactory();

        public void SetCommandFactory(Core.ICommandFactory commandFactory)
        {
            if (commandFactory != null)
                _commandFactory = commandFactory;
        }

        public float Accuracy => _totalPredictions > 0
            ? (float)_correctPredictions / _totalPredictions
            : 1.0f;

        public ICommand PredictInput(int playerId, int tick, List<ICommand> previousCommands)
        {
            // Find the most recent command with IsContinuousInput=true and clone it.
            // One-shot commands (skills, spawns, etc.) are not prediction targets and are skipped.
            ICommand lastCommand = null;

            for (int i = 0; i < previousCommands.Count; i++)
            {
                var cmd = previousCommands[i];
                if (cmd.PlayerId == playerId && cmd is Core.CommandBase cb && cb.IsContinuousInput)
                {
                    if (lastCommand == null || cmd.Tick > lastCommand.Tick)
                        lastCommand = cmd;
                }
            }

            if (lastCommand != null)
            {
                // Clone via Span and update tick (GC-free)
                int size = lastCommand.GetSerializedSize();
                Span<byte> buf = size <= 1024
                    ? stackalloc byte[size]
                    : new byte[size];
                var writer = new SpanWriter(buf);
                lastCommand.Serialize(ref writer);
                var predicted = _commandFactory.CreateCommand(lastCommand.CommandTypeId);
                var reader = new SpanReader(buf.Slice(0, writer.Position));
                predicted.Deserialize(ref reader);

                // Update tick (if CommandBase)
                if (predicted is Core.CommandBase cmdBase)
                {
                    cmdBase.Tick = tick;
                    // Repeat only the held/continuous portion; clear one-shot intent (attack/skill/jump
                    // edge) so prediction does not re-fire it every predicted tick. No-op by default.
                    cmdBase.ClearOneShotForPrediction();
                }

                return predicted;
            }

            // No continuous input command found; return EmptyCommand
            var empty = Core.CommandPool.Get<Core.EmptyCommand>();
            empty.PlayerId = playerId;
            empty.Tick = tick;
            return empty;
        }

        public void UpdateAccuracy(bool wasCorrect)
        {
            _totalPredictions++;
            if (wasCorrect)
                _correctPredictions++;
        }
    }
}
