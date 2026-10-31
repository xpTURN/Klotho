using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server-side input collection and validation.
    /// Filters inputs received from clients by peerId-PlayerId validation and tick validity
    /// (the effective deadline is the tick's execution moment — later arrivals are past-tick
    /// rejected and the client self-corrects via lead escalation), and substitutes EmptyCommand
    /// for inputs that have not arrived by tick execution time.
    /// </summary>
    public class ServerInputCollector
    {
        // tick -> (playerId -> command)
        private readonly Dictionary<int, Dictionary<int, ICommand>> _inputs
            = new Dictionary<int, Dictionary<int, ICommand>>();

        private readonly List<ICommand> _resultCache = new List<ICommand>();
        private readonly List<int> _cleanupCache = new List<int>();

        // peerId -> playerId (externally owned, only the reference is held)
        private Dictionary<int, int> _peerToPlayer;

        private readonly SortedSet<int> _activePlayerIds = new SortedSet<int>();

        private int _lastExecutedTick = -1;
        private IKLogger _logger;

        // First scheduled tick — kept in sync with KlothoEngine.Start() setting CurrentTick = 0.
        // Engine reference avoided here to prevent dependency cycle.
        private const int FIRST_SCHEDULED_TICK = 0;

        // Bootstrap window flag (SD-server only). When true, defensively redirects past-tick inputs
        // arriving before any tick has executed (_lastExecutedTick == -1) to FIRST_SCHEDULED_TICK
        // instead of rejecting. Toggled by ServerNetworkService.
        private bool _bootstrapPending;

        // Rejection / acceptance counters since last GetAndResetStats (monitoring).
        private int _acceptedCount;
        private int _rejectedPastTickCount;
        private int _rejectedPeerMismatchCount;

        // Reliable channel: per-player FIFO of pending reliable commands awaiting authoritative tick
        // placement, plus a per-player high-water sequence number for dedup. Reliably-ordered delivery
        // is gap-free monotone, so a single high-water value suffices (seq <= high-water ⇒ duplicate).
        private readonly Dictionary<int, Queue<ICommand>> _reliableInbox
            = new Dictionary<int, Queue<ICommand>>();
        private readonly Dictionary<int, int> _reliableSeqHighWater
            = new Dictionary<int, int>();

        // Server-authored system commands (e.g. the late-join PlayerJoinCommand) awaiting their
        // scheduled tick. A separate lane from _inputs: the per-(tick, playerId) slot would collide
        // with the player's own input, and CollectTickInputs only visits _activePlayerIds — the
        // server itself has no player slot to carry a command under.
        private readonly Dictionary<int, List<ICommand>> _systemCommands
            = new Dictionary<int, List<ICommand>>();

        // Per-player reliable placement floor. Reliable commands are placed at their arrival tick
        // (authoritative placement), so a late-joiner's spawn arriving before its joinTick would
        // commit before the join-time seed. Holding the player's inbox until the floor tick keeps
        // spawn >= joinTick server-side (the SD counterpart of the P2P joiner-side cmd.Tick floor).
        private readonly Dictionary<int, int> _reliablePlacementFloor
            = new Dictionary<int, int>();

        /// <summary>
        /// Raised when a player's input has not arrived by tick execution time and is substituted with EmptyCommand.
        /// Parameter: playerId
        /// </summary>
        public event Action<int> OnPlayerInputTimeout;

        /// <summary>
        /// Raised when a transport-level command rejection occurs (peer mismatch, past tick).
        /// Consumers (ServerNetworkService) unicast a CommandRejectedMessage hint to the originating peer.
        /// Parameters: peerId, tick, commandTypeId, reason
        /// </summary>
        public event Action<int, int, int, RejectionReason> OnCommandRejected;

        public int LastExecutedTick => _lastExecutedTick;

        /// <summary>
        /// Configures the peerId-PlayerId mapping.
        /// </summary>
        /// <param name="peerToPlayer">peerId → playerId mapping (reference to an externally owned Dictionary)</param>
        public void Configure(Dictionary<int, int> peerToPlayer)
        {
            _peerToPlayer = peerToPlayer;
        }

        public void SetLogger(IKLogger logger) => _logger = logger;

        /// <summary>
        /// Toggles the bootstrap-pending window. Owned by ServerNetworkService;
        /// set true at Phase = Playing, cleared via CompleteBootstrap on ack-complete or timeout.
        /// </summary>
        public void SetBootstrapPending(bool pending) => _bootstrapPending = pending;

        public void AddPlayer(int playerId) => _activePlayerIds.Add(playerId);

        // Permanent leave (both call sites) — also drop the player's reliable state: the drain loop
        // only visits active players, so a held inbox would otherwise linger until Reset, and a stale
        // floor/high-water would misapply if the id were ever reused.
        public void RemovePlayer(int playerId)
        {
            _activePlayerIds.Remove(playerId);
            _reliablePlacementFloor.Remove(playerId);
            _reliableSeqHighWater.Remove(playerId);
            if (_reliableInbox.TryGetValue(playerId, out var queue))
            {
                while (queue.Count > 0)
                    CommandPool.Return(queue.Dequeue());
                _reliableInbox.Remove(playerId);
            }
            // Also purge any pending system-lane command scheduled for this player (the late-join
            // PlayerJoinCommand). CollectTickInputs drains the system lane UNCONDITIONALLY — it does not
            // gate on _activePlayerIds — so a leftover join for a player that permanently left before its
            // joinTick would still create a participant slot at joinTick (an orphan / ghost participant,
            // never cleaned up since the leave already ran before the slot existed).
            PurgePendingSystemCommands(playerId);
        }

        // Drops pending _systemCommands scheduled for a departed player (currently the late-join
        // PlayerJoinCommand, keyed by JoinedPlayerId), returning them to the pool and clearing any tick
        // whose list empties. Called only on permanent leave (rare), so the linear scan is fine.
        private void PurgePendingSystemCommands(int playerId)
        {
            _cleanupCache.Clear();
            foreach (var kvp in _systemCommands)
            {
                var list = kvp.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is PlayerJoinCommand pjc && pjc.JoinedPlayerId == playerId)
                    {
                        CommandPool.Return(list[i]);
                        list.RemoveAt(i);
                    }
                }
                if (list.Count == 0)
                    _cleanupCache.Add(kvp.Key);
            }
            for (int i = 0; i < _cleanupCache.Count; i++)
                _systemCommands.Remove(_cleanupCache[i]);
        }

        /// <summary>
        /// Schedules a server-authored system command (e.g. the late-join PlayerJoinCommand) for a
        /// future tick. Drained by CollectTickInputs at exactly that tick, so the command enters the
        /// same list that is executed, replay-recorded, and broadcast in one server tick pass.
        /// </summary>
        public void AddSystemCommand(int tick, ICommand command)
        {
            if (!_systemCommands.TryGetValue(tick, out var list))
            {
                list = new List<ICommand>();
                _systemCommands[tick] = list;
            }
            list.Add(command);
            _logger?.KDebug($"[InputCollector][System] Scheduled: tick={tick}, cmd={command.GetType().Name}");
        }

        /// <summary>
        /// Holds the player's reliable inbox until the given tick (late-join: joinTick) so the
        /// join-time seed always precedes the player's first reliable command (e.g. spawn).
        /// </summary>
        public void SetReliablePlacementFloor(int playerId, int floorTick)
        {
            _reliablePlacementFloor[playerId] = floorTick;
            _logger?.KDebug($"[InputCollector][Reliable] Placement floor set: playerId={playerId}, floorTick={floorTick}");
        }

        public int ActivePlayerCount => _activePlayerIds.Count;

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
                _rejectedPeerMismatchCount++;
                _logger?.KWarning($"[InputCollector] Rejected (peerId mismatch): peerId={peerId}, playerId={playerId}, cmd={command.GetType().Name}");
                _logger?.KDebug($"[InputCollector][Reject] tick={tick} peerId={peerId} playerId={playerId} reason=peer_mismatch arrivalDelayMs=-1 cmdTypeId={command.CommandTypeId}");
                OnCommandRejected?.Invoke(peerId, tick, command.CommandTypeId, RejectionReason.PeerMismatch);
                return false;
            }

            // 2. Tick already executed
            if (tick <= _lastExecutedTick)
            {
                if (_bootstrapPending && _lastExecutedTick == -1)
                {
                    // Bootstrap window, no tick executed yet — defensively redirect to the first scheduled tick.
                    _logger?.KDebug($"[InputCollector] Bootstrap redirect: tick={tick} -> {FIRST_SCHEDULED_TICK}, playerId={playerId}, cmd={command.GetType().Name}");
                    tick = FIRST_SCHEDULED_TICK;
                }
                else
                {
                    _rejectedPastTickCount++;
                    _logger?.KWarning($"[InputCollector] Rejected (past tick): tick={tick}, lastExec={_lastExecutedTick}, playerId={playerId}, cmd={command.GetType().Name}");
                    _logger?.KDebug($"[InputCollector][Reject] tick={tick} peerId={peerId} playerId={playerId} reason=past_tick arrivalDelayMs=-1 cmdTypeId={command.CommandTypeId}");
                    OnCommandRejected?.Invoke(peerId, tick, command.CommandTypeId, RejectionReason.PastTick);
                    return false;
                }
            }

            // 3. Store
            if (!_inputs.TryGetValue(tick, out var tickInputs))
            {
                tickInputs = DictionaryPoolHelper.GetIntDictionary<ICommand>();
                _inputs[tick] = tickInputs;
            }

            bool overwrite = tickInputs.TryGetValue(playerId, out var existing);

            // Empty-no-overwrite-real backstop. A client's own auto-inject empty that
            // collided with a real cmd is already suppressed client-side (send gating); this is the
            // server-side invariant guarding any future sender / regression. Never let an arriving
            // EmptyCommand replace a stored non-Empty (real) cmd — last-write-wins would eat the button.
            // real-over-empty / real-over-real / empty-over-empty keep the existing last-write behavior.
            // The skipped empty is left to GC, matching how a displaced cmd is handled on overwrite.
            if (overwrite && command is EmptyCommand && !(existing is EmptyCommand))
            {
                _logger?.KDebug($"[Server][DIAG] Empty-no-overwrite-real: tick={tick}, pid={playerId}, kept={existing.GetType().Name}, droppedEmpty");
                return true;
            }

            // The slot is authoritative: stamp the peer-validated playerId and the (possibly redirected)
            // tick over the payload's untrusted values. The server executes the collected command as is,
            // but an SD client files the broadcast command under its own (Tick, PlayerId) — a payload that
            // disagrees with its slot would steer another player's character or desync every client.
            if (command is CommandBase cb)
            {
                cb.PlayerId = playerId;
                cb.Tick = tick;
            }

            tickInputs[playerId] = command;
            if (!overwrite)
                _acceptedCount++;
            _logger?.KDebug($"[Server][DIAG] Accept: tick={tick}, lastExec={_lastExecutedTick}, pid={playerId}, cmd={command.GetType().Name}, overwrite={overwrite}, slotCount={tickInputs.Count}");
            return true;
        }

        /// <summary>
        /// Accept a reliable command submit into the per-player inbox for authoritative tick placement
        /// (there is no client-assigned tick, so no past-tick check). Peer↔player validation is kept
        /// (reject a spoofed/unregistered peer). Dedup by per-player high-water sequence — a duplicate
        /// (seq &lt;= high-water, e.g. a reconnect resend) is silently ignored. Accepted instances are
        /// owned by the inbox until drained by CollectTickInputs; rejected/duplicate arrivals return
        /// false so the caller disposes them.
        /// </summary>
        public bool TryAcceptReliable(int peerId, int playerId, int sequenceNumber, ICommand command)
        {
            // PeerMismatch — spoofed or unregistered peer (security; mirrors TryAcceptInput).
            if (_peerToPlayer == null
                || !_peerToPlayer.TryGetValue(peerId, out int expectedPlayerId)
                || expectedPlayerId != playerId)
            {
                _rejectedPeerMismatchCount++;
                _logger?.KWarning($"[InputCollector][Reliable] Rejected (peerId mismatch): peerId={peerId}, playerId={playerId}, cmd={command.GetType().Name}");
                OnCommandRejected?.Invoke(peerId, -1, command.CommandTypeId, RejectionReason.PeerMismatch);
                return false;
            }

            // Dedup — ReliableOrdered is gap-free monotone, so seq <= high-water is a duplicate resend.
            if (_reliableSeqHighWater.TryGetValue(playerId, out int hw) && sequenceNumber <= hw)
            {
                _logger?.KDebug($"[InputCollector][Reliable] Dup ignored: playerId={playerId}, seq={sequenceNumber}, highWater={hw}");
                return false;
            }
            _reliableSeqHighWater[playerId] = sequenceNumber;

            if (!_reliableInbox.TryGetValue(playerId, out var queue))
            {
                queue = new Queue<ICommand>();
                _reliableInbox[playerId] = queue;
            }
            queue.Enqueue(command);
            _logger?.KDebug($"[InputCollector][Reliable] Enqueued: playerId={playerId}, seq={sequenceNumber}, cmd={command.GetType().Name}, pending={queue.Count}");
            return true;
        }

        /// <summary>
        /// Snapshot and reset rejection/acceptance counters for phase-tagged monitoring.
        /// </summary>
        public void GetAndResetStats(out int accepted, out int rejectedPastTick, out int rejectedPeerMismatch)
        {
            accepted = _acceptedCount;
            rejectedPastTick = _rejectedPastTickCount;
            rejectedPeerMismatch = _rejectedPeerMismatchCount;

            _acceptedCount = 0;
            _rejectedPastTickCount = 0;
            _rejectedPeerMismatchCount = 0;
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

            _logger?.KDebug($"[Server][DIAG] Collect: tick={tick}, slotExists={(tickInputs != null)}, slotCount={(tickInputs?.Count ?? 0)}, activeCount={_activePlayerIds.Count}");

            foreach (int playerId in _activePlayerIds)
            {
                if (tickInputs != null && tickInputs.TryGetValue(playerId, out var cmd))
                {
                    _resultCache.Add(cmd);
                    _logger?.KDebug($"[Server][DIAG] Collect.Hit: tick={tick}, pid={playerId}, cmd={cmd.GetType().Name}");
                }
                else
                {
                    var empty = CommandPool.Get<EmptyCommand>();
                    empty.PlayerId = playerId;
                    empty.Tick = tick;
                    _resultCache.Add(empty);
                    OnPlayerInputTimeout?.Invoke(playerId);
                    _logger?.KDebug($"[Server][DIAG] Collect.Miss: tick={tick}, pid={playerId}, slotExists={(tickInputs != null)}, slotPids=[{(tickInputs != null ? string.Join(",", tickInputs.Keys) : "")}]");
                    _logger?.KDebug($"[InputCollector][EmptySubst] execTick={tick} playerId={playerId}");
                }
            }

            // Drain pending reliable commands onto this (executed) tick — authoritative placement.
            // Stamp command.PlayerId from the peer-validated inbox key (the client-supplied payload
            // PlayerId is untrusted/zero and must not be relied on) and command.Tick = tick so the
            // client buffers them at the commit tick and replay/broadcast key them correctly. Drain
            // order is irrelevant: the engine re-sorts the returned batch with the deterministic
            // ordering key chain (CommandOrdering) before sim/record.
            foreach (int playerId in _activePlayerIds)
            {
                // Hold this player's inbox until its placement floor (late-join: joinTick) — the
                // join-time seed must precede the player's first reliable command (e.g. spawn).
                // Held commands drain naturally on the floor tick.
                if (_reliablePlacementFloor.TryGetValue(playerId, out int floorTick) && tick < floorTick)
                {
                    if (_reliableInbox.TryGetValue(playerId, out var held) && held.Count > 0)
                        _logger?.KDebug($"[InputCollector][Reliable] Held by placement floor: tick={tick}, pid={playerId}, floorTick={floorTick}, pending={held.Count}");
                    continue;
                }
                if (!_reliableInbox.TryGetValue(playerId, out var queue))
                    continue;
                while (queue.Count > 0)
                {
                    var rel = queue.Dequeue();
                    if (rel is CommandBase cb)
                    {
                        cb.PlayerId = playerId;
                        cb.Tick = tick;
                    }
                    _resultCache.Add(rel);
                    _logger?.KDebug($"[InputCollector][Reliable] Placed: tick={tick}, pid={playerId}, cmd={rel.GetType().Name}");
                }
            }

            // Drain server-authored system commands scheduled for this tick (late-join
            // PlayerJoinCommand). Tick-internal ordering is the engine's deterministic sort —
            // PlayerJoin's negative OrderKey leads the tick.
            if (_systemCommands.TryGetValue(tick, out var sysList))
            {
                for (int i = 0; i < sysList.Count; i++)
                {
                    _resultCache.Add(sysList[i]);
                    _logger?.KDebug($"[InputCollector][System] Placed: tick={tick}, cmd={sysList[i].GetType().Name}");
                }
                _systemCommands.Remove(tick);
            }

            _lastExecutedTick = tick;

            // Clean up data for the executed tick
            if (tickInputs != null)
            {
                tickInputs.Clear();
                DictionaryPoolHelper.ReturnIntDictionary(tickInputs);
                _inputs.Remove(tick);
            }

            return _resultCache;
        }

        /// <summary>
        /// Cleans up stale inputs older than lastExecutedTick.
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

            // Reliable inbox: return any undrained pending commands to the pool, then clear inbox
            // and per-player seq high-water (match reset — a fresh authority/seq sequence).
            foreach (var queue in _reliableInbox.Values)
                while (queue.Count > 0)
                    CommandPool.Return(queue.Dequeue());
            _reliableInbox.Clear();
            _reliableSeqHighWater.Clear();
            _reliablePlacementFloor.Clear();

            // System lane: return undrained server-authored commands (ticks never executed).
            foreach (var list in _systemCommands.Values)
                for (int i = 0; i < list.Count; i++)
                    CommandPool.Return(list[i]);
            _systemCommands.Clear();

            _activePlayerIds.Clear();
            _lastExecutedTick = -1;
            _bootstrapPending = false;

            _acceptedCount = 0;
            _rejectedPastTickCount = 0;
            _rejectedPeerMismatchCount = 0;
        }
    }
}
