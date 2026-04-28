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

            if (!_commands.TryGetValue(tick, out var tickCommands))
            {
                // Get from dictionary pool (GC prevention)
                tickCommands = DictionaryPoolHelper.GetIntDictionary<ICommand>();
                _commands[tick] = tickCommands;
            }

            tickCommands[playerId] = command;

            UpdateBounds(tick);
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
