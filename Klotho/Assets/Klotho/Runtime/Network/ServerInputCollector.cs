using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server-side input collection, validation, and Hard Tolerance management (section 3.6.1).
    /// Filters inputs received from clients by peerId-PlayerId validation, tick validity, and the Hard Tolerance deadline,
    /// and substitutes EmptyCommand for inputs that have not arrived by tick execution time.
    /// </summary>
    public class ServerInputCollector
    {
        // tick -> (playerId -> command)
        private readonly Dictionary<int, Dictionary<int, ICommand>> _inputs
            = new Dictionary<int, Dictionary<int, ICommand>>();

        // tick -> deadline (UTC ms)
        private readonly Dictionary<int, long> _deadlines
            = new Dictionary<int, long>();

        private readonly List<ICommand> _resultCache = new List<ICommand>();
        private readonly List<int> _cleanupCache = new List<int>();

        // peerId -> playerId (externally owned, only the reference is held)
        private Dictionary<int, int> _peerToPlayer;

        private readonly SortedSet<int> _activePlayerIds = new SortedSet<int>();

        private int _lastExecutedTick = -1;
        private int _hardToleranceMs;
        private ILogger _logger;

        // Time provider (injectable for tests)
        private Func<long> _nowMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>
        /// Raised when a player's input has not arrived by tick execution time and is substituted with EmptyCommand.
        /// Parameter: playerId
        /// </summary>
        public event Action<int> OnPlayerInputTimeout;

        public int LastExecutedTick => _lastExecutedTick;

        /// <summary>
        /// Configures Hard Tolerance and the peerId-PlayerId mapping.
        /// </summary>
        /// <param name="hardToleranceMs">Input reception deadline (ms). If 0, deadline checks are skipped.</param>
        /// <param name="peerToPlayer">peerId → playerId mapping (reference to an externally owned Dictionary)</param>
        public void Configure(int hardToleranceMs, Dictionary<int, int> peerToPlayer)
        {
            _hardToleranceMs = hardToleranceMs;
            _peerToPlayer = peerToPlayer;
        }

        public void SetLogger(ILogger logger) => _logger = logger;

        /// <summary>
        /// Replaces the time provider (for tests).
        /// </summary>
        public void SetTimeProvider(Func<long> nowMs)
        {
            _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        }

        public void AddPlayer(int playerId) => _activePlayerIds.Add(playerId);

        public void RemovePlayer(int playerId) => _activePlayerIds.Remove(playerId);

        public int ActivePlayerCount => _activePlayerIds.Count;

        /// <summary>
        /// Opens the input collection window for the tick. The deadline is tickStartTimeMs + HardToleranceMs.
        /// </summary>
        public void BeginTick(int tick, long tickStartTimeMs)
        {
            if (_hardToleranceMs > 0)
                _deadlines[tick] = tickStartTimeMs + _hardToleranceMs;
        }

        /// <summary>
        /// Validates and stores a client input on receipt.
        /// </summary>
        /// <param name="peerId">Peer ID that sent the input</param>
        /// <param name="tick">Input tick</param>
        /// <param name="playerId">PlayerId from the message</param>
        /// <param name="command">Deserialized command</param>
        /// <returns>Whether the input was accepted</returns>
        public bool TryAcceptInput(int peerId, int tick, int playerId, ICommand command)
        {
            // 1. peerId-PlayerId mismatch → spoofed or unregistered
            if (_peerToPlayer == null
                || !_peerToPlayer.TryGetValue(peerId, out int expectedPlayerId)
                || expectedPlayerId != playerId)
            {
                _logger?.ZLogWarning($"[InputCollector] Rejected (peerId mismatch): peerId={peerId}, playerId={playerId}, cmd={command.GetType().Name}");
                return false;
            }

            // 2. Tick already executed
            if (tick <= _lastExecutedTick)
            {
                _logger?.ZLogWarning($"[InputCollector] Rejected (past tick): tick={tick}, lastExec={_lastExecutedTick}, playerId={playerId}, cmd={command.GetType().Name}");
                return false;
            }

            // 3. Hard Tolerance exceeded
            if (_deadlines.TryGetValue(tick, out long deadline))
            {
                if (_nowMs() > deadline)
                {
                    _logger?.ZLogWarning($"[InputCollector] Rejected (tolerance exceeded): tick={tick}, playerId={playerId}, cmd={command.GetType().Name}");
                    return false;
                }
            }

            // 4. Store
            if (!_inputs.TryGetValue(tick, out var tickInputs))
            {
                tickInputs = DictionaryPoolHelper.GetIntDictionary<ICommand>();
                _inputs[tick] = tickInputs;
            }

            bool overwrite = tickInputs.ContainsKey(playerId);
            tickInputs[playerId] = command;
            _logger?.ZLogDebug($"[Server][DIAG] Accept: tick={tick}, lastExec={_lastExecutedTick}, pid={playerId}, cmd={command.GetType().Name}, overwrite={overwrite}, slotCount={tickInputs.Count}");
            return true;
        }

        /// <summary>
        /// Returns whether the specified player's input has already arrived.
        /// </summary>
        public bool HasInput(int tick, int playerId)
        {
            return _inputs.TryGetValue(tick, out var tickInputs)
                && tickInputs.ContainsKey(playerId);
        }

        /// <summary>
        /// Called at tick execution time. Substitutes EmptyCommand for missing inputs and returns the command list.
        /// The returned list is an internal cache and will be mutated on the next call.
        /// </summary>
        public List<ICommand> CollectTickInputs(int tick)
        {
            _resultCache.Clear();

            _inputs.TryGetValue(tick, out var tickInputs);

            _logger?.ZLogDebug($"[Server][DIAG] Collect: tick={tick}, slotExists={(tickInputs != null)}, slotCount={(tickInputs?.Count ?? 0)}, activeCount={_activePlayerIds.Count}");

            foreach (int playerId in _activePlayerIds)
            {
                if (tickInputs != null && tickInputs.TryGetValue(playerId, out var cmd))
                {
                    _resultCache.Add(cmd);
                    _logger?.ZLogDebug($"[Server][DIAG] Collect.Hit: tick={tick}, pid={playerId}, cmd={cmd.GetType().Name}");
                }
                else
                {
                    var empty = CommandPool.Get<EmptyCommand>();
                    empty.PlayerId = playerId;
                    empty.Tick = tick;
                    _resultCache.Add(empty);
                    OnPlayerInputTimeout?.Invoke(playerId);
                    _logger?.ZLogDebug($"[Server][DIAG] Collect.Miss: tick={tick}, pid={playerId}, slotExists={(tickInputs != null)}, slotPids=[{(tickInputs != null ? string.Join(",", tickInputs.Keys) : "")}]");
                }
            }

            _lastExecutedTick = tick;

            // Clean up data for the executed tick
            _deadlines.Remove(tick);
            if (tickInputs != null)
            {
                tickInputs.Clear();
                DictionaryPoolHelper.ReturnIntDictionary(tickInputs);
                _inputs.Remove(tick);
            }

            return _resultCache;
        }

        /// <summary>
        /// Cleans up stale inputs and deadline data older than lastExecutedTick.
        /// Late-arriving inputs are already rejected in TryAcceptInput, so this is called
        /// when future-tick data has accumulated without going through CollectTickInputs.
        /// </summary>
        public void CleanupBefore(int tick)
        {
            _cleanupCache.Clear();
            foreach (var kvp in _inputs)
            {
                if (kvp.Key < tick)
                    _cleanupCache.Add(kvp.Key);
            }
            for (int i = 0; i < _cleanupCache.Count; i++)
            {
                int t = _cleanupCache[i];
                if (_inputs.TryGetValue(t, out var dict))
                {
                    foreach (var cmd in dict.Values)
                        CommandPool.Return(cmd);
                    dict.Clear();
                    DictionaryPoolHelper.ReturnIntDictionary(dict);
                }
                _inputs.Remove(t);
            }

            _cleanupCache.Clear();
            foreach (var kvp in _deadlines)
            {
                if (kvp.Key < tick)
                    _cleanupCache.Add(kvp.Key);
            }
            for (int i = 0; i < _cleanupCache.Count; i++)
                _deadlines.Remove(_cleanupCache[i]);
        }

        public void Reset()
        {
            foreach (var dict in _inputs.Values)
            {
                foreach (var cmd in dict.Values)
                    CommandPool.Return(cmd);
                dict.Clear();
                DictionaryPoolHelper.ReturnIntDictionary(dict);
            }
            _inputs.Clear();
            _deadlines.Clear();
            _activePlayerIds.Clear();
            _lastExecutedTick = -1;
        }
    }
}
