using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Input
{
    /// <summary>
    /// Input buffer implementation
    /// </summary>
    public class InputBuffer : IInputBuffer
    {
        // tick -> playerId -> command
        private readonly Dictionary<int, Dictionary<int, ICommand>> _commands
            = new Dictionary<int, Dictionary<int, ICommand>>();

        // tick -> system command list (ISystemCommand only)
        private readonly Dictionary<int, List<ICommand>> _systemCommands
            = new Dictionary<int, List<ICommand>>();

        private int _oldestTick = int.MaxValue;
        private int _newestTick = int.MinValue;

        // Cached lists for object pooling (GC prevention)
        private readonly List<ICommand> _commandListCache = new List<ICommand>();
        private readonly List<int> _ticksToRemoveCache = new List<int>();

        // (tick << 32) | (uint)playerId — entries sealed by range fill. AddCommand skips any
        // later real command arrival at a sealed (tick, playerId) to prevent InputBuffer ↔
        // simulation state divergence (the empty placeholder has already been consumed by chain
        // advance). Cleared by ClearBefore for collected ticks.
        private readonly HashSet<long> _sealedTickPlayer = new HashSet<long>();

        private ILogger _logger;

        public void SetLogger(ILogger logger) => _logger = logger;

#if DEBUG
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

        public void AddCommand(ICommand command)
        {
            if (command == null)
                return;

#if DEBUG
            System.Diagnostics.Debug.Assert(!_resimulating,
                "InputBuffer.AddCommand must not be called during re-simulation. " +
                "Predicted commands should only go into _tickCommandsCache/_pendingCommands.");
#endif

            if (command is ISystemCommand)
            {
                AddSystemCommand(command);
                return;
            }

            int tick = command.Tick;
            int playerId = command.PlayerId;

            // Seal guard: if this (tick, playerId) was filled with an empty placeholder by the
            // range fill path and chain has already advanced past it, silently drop the late
            // real command to keep InputBuffer and simulation state consistent.
            long sealKey = ((long)tick << 32) | (uint)playerId;
            if (_sealedTickPlayer.Contains(sealKey))
            {
                CommandPool.Return(command);
                return;
            }

            if (!_commands.TryGetValue(tick, out var tickCommands))
            {
                // Get from dictionary pool (GC prevention)
                tickCommands = DictionaryPoolHelper.GetIntDictionary<ICommand>();
                _commands[tick] = tickCommands;
            }

            tickCommands[playerId] = command;

            UpdateBounds(tick);
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

#if DEVELOPMENT_BUILD || UNITY_EDITOR
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
            _logger.ZLogWarning($"[InputBuffer][DumpTickRange] from={fromTick}, to={toTick}, oldest={OldestTick}, newest={NewestTick}, {sb}");
        }

        // Emits a warning for each tick in (verifiedTick, beforeTick) that still holds
        // player commands at the moment of cleanup. Surfaces host self-wipe during P2P
        // quorum stall — wiped commands past the verified horizon are unrecoverable.
        public void LogPendingWipe(int beforeTick, int verifiedTick, int currentTick)
        {
            if (_logger == null)
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
                }
                _logger.ZLogWarning($"[InputBuffer][Cleanup] Pending Input WIPED: tick={t}, commands=[{sb}], _lastVerifiedTick={verifiedTick}, CurrentTick={currentTick}, lag={currentTick - verifiedTick}");
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

        /// <summary>
        /// Checks whether commands from all players have arrived
        /// </summary>
        public bool HasAllCommands(int tick, int playerCount)
        {
            if (!_commands.TryGetValue(tick, out var tickCommands))
                return false;

            return tickCommands.Count >= playerCount;
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
                    //     _logger.ZLogDebug($"[InputBuffer] ClearBefore({tick}): discarding tick={t}, playerCommands={dict.Count}");
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
                    //     _logger.ZLogDebug($"[InputBuffer] ClearBefore({tick}): discarding tick={t}, systemCommands={list.Count}");
                    for (int j = 0; j < list.Count; j++)
                        CommandPool.Return(list[j]);
                }
                _systemCommands.Remove(t);
            }

            // Discard seals at ticks below cleanup horizon in lockstep with buffer.
            if (_sealedTickPlayer.Count > 0)
            {
                _sealedTickPlayer.RemoveWhere(key => (int)(key >> 32) < tick);
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

            // System commands
            _ticksToRemoveCache.Clear();
            foreach (var t in _systemCommands.Keys)
            {
                if (t > tick)
                    _ticksToRemoveCache.Add(t);
            }
            for (int i = 0; i < _ticksToRemoveCache.Count; i++)
            {
                int t = _ticksToRemoveCache[i];
                if (_systemCommands.TryGetValue(t, out var list))
                {
                    for (int j = 0; j < list.Count; j++)
                        CommandPool.Return(list[j]);
                }
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

        private static int CompareSystemCommands(ICommand a, ICommand b)
        {
            if (a is ISystemCommand sa && b is ISystemCommand sb)
                return sa.OrderKey.CompareTo(sb.OrderKey);
            return a.CommandTypeId.CompareTo(b.CommandTypeId);
        }
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
                }

                return predicted;
            }

            // No continuous input command found; return EmptyCommand
            var empty = Core.CommandPool.Get<Core.EmptyCommand>();
            empty.PlayerId = playerId;
            empty.Tick = tick;
            return empty;
        }

        public void UpdateAccuracy(ICommand predicted, ICommand actual)
        {
            _totalPredictions++;

            // Simple comparison: considered accurate if same type
            if (predicted.CommandTypeId == actual.CommandTypeId)
            {
                _correctPredictions++;
            }
        }
    }
}
