using System;
using System.Buffers;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // ── Spectator management ──────────────────────────────────

        private void HandleSpectatorJoin(int peerId, SpectatorJoinMessage msg)
        {
            if (!IsHost) return;

            var spectator = new SpectatorInfo { SpectatorId = _nextSpectatorId--, PeerId = peerId };

            int snapshotTick = _engine?.GetNearestSnapshotTickWithinBuffer() ?? -1;

            if (snapshotTick >= 0)
            {
                // Validate that the input range can be serialized
                if (!_engine.TrySerializeVerifiedInputRange(snapshotTick + 1, _engine.LastVerifiedTick, out byte[] verifyData, out _))
                {
                    _logger?.ZLogWarning($"[KlothoNetworkService][HandleSpectatorJoin] Cannot serialize input range for spectator peer {peerId}");
                    return;
                }
                if (verifyData != null)
                    ArrayPool<byte>.Shared.Return(verifyData);
            }

            _spectators.Add(spectator);

            var accept = new SpectatorAcceptMessage
            {
                SpectatorId = spectator.SpectatorId,
                RandomSeed = RandomSeed,
                LastVerifiedTick = snapshotTick,
                CurrentTick = _engine?.CurrentTick ?? 0,
            };
            if (_engine?.SimulationConfig != null)
                accept.CopySimulationConfigFrom(_engine.SimulationConfig);
            if (_engine?.SessionConfig != null)
                accept.CopySessionConfigFrom(_engine.SessionConfig);
            for (int i = 0; i < _players.Count; i++)
                accept.PlayerIds.Add(_players[i].PlayerId);

            using (var serialized = _messageSerializer.SerializePooled(accept))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            // If a spectator join caused Syncing, return to Synchronized
            if (Phase == SessionPhase.Syncing || Phase == SessionPhase.Synchronized)
            {
                Phase = SessionPhase.Synchronized;
            }

            // Game running: send GameStartMessage to spectators with LastSentTick == -1
            if (Phase == SessionPhase.Playing || Phase == SessionPhase.Countdown)
            {
                var startMsg = new GameStartMessage
                {
                    StartTime = _gameStartTime,
                    RandomSeed = RandomSeed,
                    MaxPlayers = _players.Count,
                    AllowLateJoin = _sessionConfig.AllowLateJoin,
                    ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                    ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                    LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                    ResyncMaxRetries = _sessionConfig.ResyncMaxRetries,
                    DesyncThresholdForResync = _sessionConfig.DesyncThresholdForResync,
                    CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                    CatchupMaxTicksPerFrame = _sessionConfig.CatchupMaxTicksPerFrame,
                };
                for (int i = 0; i < _players.Count; i++)
                    startMsg.PlayerIds.Add(_players[i].PlayerId);

                using (var serialized = _messageSerializer.SerializePooled(startMsg))
                {
                    for (int i = 0; i < _spectators.Count; i++)
                    {
                        if (_spectators[i].LastSentTick == -1)
                            _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }

        private void HandleSpectatorLeave(int peerId)
        {
            for (int i = _spectators.Count - 1; i >= 0; i--)
            {
                if (_spectators[i].PeerId == peerId)
                {
                    _spectators.RemoveAt(i);
                    break;
                }
            }
        }

        private void HandleFrameVerifiedForCatchup(int verifiedTick)
        {
            if (!IsHost || _lateJoinCatchups.Count == 0)
                return;

            _completedLateJoinCatchups.Clear();

            foreach (var kvp in _lateJoinCatchups)
            {
                var info = kvp.Value;
                int fromTick = info.LastSentTick + 1;
                int toTick = verifiedTick;

                if (fromTick > toTick)
                    continue;

                if (!_engine.TrySerializeVerifiedInputRange(fromTick, toTick, out byte[] rangeData, out int len))
                {
                    // Host's _inputBuffer cleanup (CleanupOldData) may have advanced OldestTick
                    // past fromTick → permanent serialize failure → guest input gap unrecoverable
                    // via this path. Log warning so the failure is visible; full FullState-resync
                    // fallback is deferred.
                    _failedSerializeCount.TryGetValue(info.PeerId, out int failCount);
                    failCount++;
                    _failedSerializeCount[info.PeerId] = failCount;
                    if (failCount == 1 || failCount % 60 == 0)
                    {
                        _logger?.ZLogWarning($"[KlothoNetworkService][LateJoinCatchup] Serialize failed: peer={info.PeerId}, fromTick={fromTick}, toTick={toTick}, failCount={failCount} — possible OldestTick race; guest may need FullState resync");
                    }
                    continue;
                }

                // Reset failure counter on success.
                if (_failedSerializeCount.ContainsKey(info.PeerId))
                    _failedSerializeCount.Remove(info.PeerId);

                var msg = _spectatorInputMessageCache;
                msg.StartTick = fromTick;
                msg.TickCount = toTick - fromTick + 1;
                msg.InputData = rangeData;
                msg.InputDataLength = len;

                using (var serialized = _messageSerializer.SerializePooled(msg))
                {
                    _transport.Send(info.PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }

                System.Buffers.ArrayPool<byte>.Shared.Return(rangeData);
                info.LastSentTick = toTick;

                // Catchup completes when host has delivered input up to JoinTick (the tick
                // guest's first command targets). Beyond this point, guest's own command stream
                // + normal P2P broadcast suffices.
                if (info.LastSentTick >= info.JoinTick)
                    _completedLateJoinCatchups.Add(info.PeerId);
            }

            // Remove completed catchups outside the foreach to avoid mutation during iteration.
            for (int i = 0; i < _completedLateJoinCatchups.Count; i++)
            {
                int peerId = _completedLateJoinCatchups[i];
                _lateJoinCatchups.Remove(peerId);
                _logger?.ZLogInformation($"[KlothoNetworkService][LateJoinCatchup] Catchup complete: peerId={peerId}, verifiedTick={verifiedTick}");
            }
            _completedLateJoinCatchups.Clear();
        }

        // Track repeated serialize failures per LateJoin peer to surface OldestTick races.
        // Cleared on success.
        private readonly System.Collections.Generic.Dictionary<int, int> _failedSerializeCount = new();

        // Reusable buffer for marking catchups completed during HandleFrameVerifiedForCatchup.
        private readonly System.Collections.Generic.List<int> _completedLateJoinCatchups = new();

        private void HandleVerifiedInputBatchReady(int startTick, int tickCount, byte[] data, int dataLength)
        {
            HandleFrameVerifiedForCatchup(startTick + tickCount - 1);

            if (_spectators.Count == 0) return;

            int batchEnd = startTick + tickCount - 1;
            //_logger?.ZLogInformation($"[KlothoNetworkService][BatchReady] startTick={startTick}, tickCount={tickCount}, batchEnd={batchEnd}, spectators={_spectators.Count}");

            var msg = _spectatorInputMessageCache;
            msg.StartTick = startTick;
            msg.TickCount = tickCount;
            msg.InputData = data;
            msg.InputDataLength = dataLength;

            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                for (int i = 0; i < _spectators.Count; i++)
                {
                    var spectator = _spectators[i];
                    if (spectator.LastSentTick >= batchEnd)
                    {
                        _logger?.ZLogInformation($"[KlothoNetworkService][BatchReady] Spectator {spectator.PeerId} skipped: LastSentTick={spectator.LastSentTick} >= batchEnd={batchEnd}");
                        continue;
                    }
                    if (spectator.LastSentTick + 1 == startTick)
                    {
                        //_logger?.ZLogInformation($"[KlothoNetworkService][BatchReady] SEND direct to {spectator.PeerId}: ticks {startTick}-{batchEnd}, serializedLen={serialized.Length}");
                        _transport.Send(spectator.PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                        spectator.LastSentTick = batchEnd;
                    }
                    else
                    {
                        _logger?.ZLogInformation($"[KlothoNetworkService][BatchReady] Spectator {spectator.PeerId} gap detected: LastSentTick={spectator.LastSentTick}, startTick={startTick} → catchup");
                        // Gap detected after FullState — send the missing range via catchup
                        SendSpectatorCatchupInputs(spectator);
                    }
                }
            }
        }

        private void SendSpectatorCatchupInputs(SpectatorInfo spectator)
        {
            if (_engine == null) return;
            int fromTick = spectator.LastSentTick + 1;
            int toTick = _engine.LastVerifiedTick;
            if (fromTick > toTick)
            {
                _logger?.ZLogInformation($"[KlothoNetworkService][Catchup] Skipped: fromTick={fromTick} > toTick={toTick}");
                return;
            }

            if (!_engine.TrySerializeVerifiedInputRange(fromTick, toTick, out byte[] inputData, out int dataLen))
            {
                _logger?.ZLogWarning($"[KlothoNetworkService][Catchup] Serialization failed: fromTick={fromTick}, toTick={toTick}");
                return;
            }

            var msg = _spectatorInputMessageCache;
            msg.StartTick = fromTick;
            msg.TickCount = toTick - fromTick + 1;
            msg.InputData = inputData;
            msg.InputDataLength = dataLen;

            _logger?.ZLogInformation($"[KlothoNetworkService][Catchup] Sent: {spectator.PeerId}, ticks {fromTick}-{toTick}, bytes={dataLen}");
            using (var serialized = _messageSerializer.SerializePooled(msg))
                _transport.Send(spectator.PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            spectator.LastSentTick = toTick;
            ArrayPool<byte>.Shared.Return(inputData);
        }
    }
}
