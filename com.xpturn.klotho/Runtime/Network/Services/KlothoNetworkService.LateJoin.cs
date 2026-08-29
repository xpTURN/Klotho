using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // Late join
        private const int LATE_JOIN_HANDSHAKE_TIMEOUT_MS = 10000;
        private readonly Dictionary<int, LateJoinCatchupInfo> _lateJoinCatchups = new Dictionary<int, LateJoinCatchupInfo>();

        // Late join: guest state machine
        private enum LateJoinState { None, WaitingForAccept, WaitingForFullState, CatchingUp, Active }
        private LateJoinState _lateJoinState;

        // This guest's own joinTick (the tick the host schedules our PlayerJoinCommand at),
        // read from the host-computed accept field in SeedLateJoinPlayers. Flushed to the engine as a
        // cmd.Tick floor in SubscribeEngine — the joiner's spawn must not execute before the join
        // command that seeds its join-time state — and used by HandleCatchupComplete to size the
        // prefill. 0 = not a late-join guest.
        private int _lateJoinJoinTick;

        // ── Late Join (Guest) ──────────────────────

        /// <summary>
        /// Restores _players / RandomSeed from a LateJoinAcceptMessage received via KlothoConnection on the Late Join path.
        /// Must be called by KlothoSession.Create before engine.Initialize so that the _activePlayerIds copy loop
        /// inside Engine.Initialize is populated correctly. Mirrors ServerDrivenClientService.SeedLateJoinPlayers.
        /// _sessionMagic / LocalPlayerId / _sharedClock are already set by InitializeFromConnection.
        /// </summary>
        public void SeedLateJoinPlayers(LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            // Host-computed joinTick carried in the accept — single-sourced, so a host-side formula
            // change can never silently diverge from a guest-side re-derivation.
            _lateJoinJoinTick = msg.JoinTick;
            SeedPlayersFromCatchupPayload(msg.RandomSeed, msg.Roster, msg.RosterTickets);
        }

        /// <summary>
        /// Cold-start Reconnect counterpart of SeedLateJoinPlayers. The host echoes the existing
        /// PlayerId via ReconnectAcceptMessage.PlayerId rather than allocating a new one. _sessionMagic is
        /// restored from persisted credentials at InitializeFromConnection.
        /// </summary>
        public void SeedReconnectPlayers(ReconnectPayload payload)
        {
            var msg = payload.AcceptMessage;
            SeedPlayersFromCatchupPayload(msg.RandomSeed, msg.Roster, msg.RosterTickets);
        }

        /// <summary>
        /// Common seed helper shared by Late Join and cold-start Reconnect.
        /// Restores _players / RandomSeed, sets Phase = Playing, and switches to CatchingUp so subsequent
        /// catchup input batches flow into _inputBuffer. rosterTickets: per-player
        /// original tickets index-parallel to roster; when present (gate on) each entry is re-verified and
        /// its ticket-derived identity/entitlement adopted (mirrors RebuildPlayerList) — otherwise a no-op.
        /// </summary>
        private void SeedPlayersFromCatchupPayload(int randomSeed, List<RosterEntry> roster, List<string> rosterTickets)
        {
            RandomSeed = randomSeed;
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                // roster-init (mirrors RebuildPlayerList useNames:true): start from the host-relayed roster
                // name so the re-verify below can detect a host-forged roster (name != ticket) and adopt the
                // ticket value. IsReady hardcoded true on this catchup-seed path (preserves prior behavior).
                string account = e.Account.ToString();
                string displayName = e.DisplayName.ToString();
                byte[] entitlement = null;
                // Re-verify the propagated ticket and adopt its identity/entitlement. No-op when the
                // gate is off (no/empty tickets) → names stay at the host-relayed roster values (aligned with
                // warm reconnect). Bound-checked against a short RosterTickets list.
                if (rosterTickets != null)
                {
                    string ticket = i < rosterTickets.Count ? rosterTickets[i] : null;
                    ResolveRosterEntryIdentity(e.PlayerId, ticket, ref account, ref displayName, ref entitlement);
                }
                _players.Add(new PlayerInfo
                {
                    PlayerId = e.PlayerId,
                    Entitlement = entitlement,
                    IsReady = true,
                    ConnectionState = (PlayerConnectionState)e.ConnectionState,
                    DisplayName = displayName,
                    Account = account,
                });
                if (entitlement != null && entitlement.Length > 0)
                    _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via CatchupRebuild: playerId={e.PlayerId}, bytes={entitlement.Length}");
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            Phase = SessionPhase.Playing;
            SaveReconnectCredentialsIfApplicable();

            // KlothoConnection path bypasses the standard handshake message flow
            // (WaitingForAccept → WaitingForFullState → CatchingUp). The Accept + FullState are
            // pre-delivered via the payload, so jump straight to CatchingUp. Without this,
            // _lateJoinState stays at None and HandleCatchupComplete / HandleCatchupInputMessage
            // guards drop all subsequent input batches.
            _lateJoinState = LateJoinState.CatchingUp;
        }

        private void HandleCatchupComplete()
        {
            if (_lateJoinState != LateJoinState.CatchingUp)
                return;

            _lateJoinState = LateJoinState.Active;

            // Prefill (InputDelay + RecommendedExtraDelay) ticks worth of empty inputs.
            // RecommendedExtraDelay shifts the guest's first real cmd.Tick to currentTick + InputDelay + extraDelay,
            // so without extended prefill the local chain has a permanent gap at [currentTick + InputDelay, ...)
            // until the first real cmd arrives. Host side is unaffected because its roster activates this player
            // only at joinTick; the gap only manifests on the guest's own chain.
            int inputDelay = _engine.InputDelay;
            int extraDelay = _engine.RecommendedExtraDelay;
            int totalPrefill = inputDelay + extraDelay;
            int currentTick = _engine.CurrentTick;
            // The engine floors real cmd.Tick at joinTick (SetLateJoinCommandFloor), so when catchup
            // ends more than (InputDelay + extraDelay) ticks before joinTick, extend the prefill to
            // cover the widened local-chain gap [currentTick, joinTick) — otherwise the chain stalls
            // on the uncovered ticks between the prefill end and the floored first real cmd.
            if (_lateJoinJoinTick - currentTick > totalPrefill)
                totalPrefill = _lateJoinJoinTick - currentTick;
            for (int i = 0; i < totalPrefill; i++)
            {
                int tick = currentTick + i;
                _commandFactory.PopulateEmpty(_emptyCommandCache, LocalPlayerId, tick);
                // Service-internal cache pattern — relies on SendCommand serializing the instance to
                // bytes synchronously and not pool-returning it. Interface ownership contract exception
                // (ILockstepNetworkService.SendCommand XML doc).
                SendCommand(_emptyCommandCache);
            }

            _logger?.KInformation($"[KlothoNetworkService][LateJoin] Transition to active: tick={currentTick}, prefilled {totalPrefill} empty commands (InputDelay={inputDelay}, extraDelay={extraDelay}, joinTick={_lateJoinJoinTick})");
        }

        private void HandleCatchupInputMessage(SpectatorInputMessage msg)
        {
            // Accept input batches in BOTH CatchingUp and Active states. With ticksAdvanced=0
            // catchup (host CurrentTick already at _catchupLastConfirmedTick + 1), state
            // transitions to Active immediately, but the gap-tick input batches still need to
            // flow into _inputBuffer for chain advance to proceed past the LateJoin gap.
            // ConfirmCatchupTick is a no-op once catchup ended.
            if (_lateJoinState != LateJoinState.CatchingUp && _lateJoinState != LateJoinState.Active)
                return;

            var reader = new SpanReader(msg.InputData, 0, msg.InputDataLength);
            for (int tick = msg.StartTick; tick < msg.StartTick + msg.TickCount; tick++)
            {
                int commandCount = reader.ReadInt32();
                for (int i = 0; i < commandCount; i++)
                {
                    var cmd = _commandFactory.DeserializeCommandRaw(ref reader);
                    _engine.ReceiveConfirmedCommand(cmd);
                }
                _engine.ConfirmCatchupTick(tick);
            }
#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            _logger?.KTrace($"[KlothoNetworkService][CatchupInput] state={_lateJoinState}, ticks=[{msg.StartTick}..{msg.StartTick + msg.TickCount - 1}], dataLen={msg.InputDataLength}");
#endif
        }

        // DEAD in the current P2P flow: KlothoConnection bypass jumps _lateJoinState directly to CatchingUp
        // via SeedPlayersFromCatchupPayload (see line 64-69 of this file), so the WaitingForAccept guard
        // below is always taken. RecommendedExtraDelay application is handled by the pending buffer in
        // KlothoNetworkService.InitializeFromConnection + SubscribeEngine. Retained for future
        // non-Connection path scenarios.
        private void HandleLateJoinAccept(LateJoinAcceptMessage msg)
        {
            if (_lateJoinState != LateJoinState.WaitingForAccept)
                return;

            LocalPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            // P2P carries identity via a separate Notification, so the LateJoin handler ignores roster
            // names and IsReady stays false (preserves prior behavior). Pass the
            // propagated tickets so the late-joiner re-verifies existing players independently when the
            // gate is on (empty/no-op when off — prior behavior unchanged).
            RebuildPlayerList(msg.Roster, rosterTickets: msg.RosterTickets);
            RandomSeed = msg.RandomSeed;

            _lateJoinState = LateJoinState.WaitingForFullState;
            _engine.ExpectFullState();
            _logger?.KInformation($"[KlothoNetworkService][HandleLateJoinAccept] playerId={msg.PlayerId}, playerCount={msg.PlayerCount}, waiting for FullState");
        }

        /// <summary>
        /// Guest receiver for the host's LateJoinNotificationMessage broadcast. Adds the new player
        /// to _players + fires PlayerCount / AllPlayersReady / OnPlayerJoined surfaces so UI on
        /// existing peers reflects the same roster state as the host.
        /// </summary>
        private void HandleLateJoinNotification(LateJoinNotificationMessage msg)
        {
            // Host's _players is owned by CompleteLateJoinSync. Reject so a forged PlayerId from a
            // misbehaving guest cannot slip past the duplicate-PlayerId guard below.
            if (IsHost)
                return;

            // Duplicate notification (e.g. reliable retry path race) must not double-add.
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _logger?.KDebug($"[KlothoNetworkService][HandleLateJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                    return;
                }
            }

            // DisplayName mirrors the host's CompleteLateJoinSync construction (empty string in P2P)
            // so the same PlayerId reads identically across peers. Ping is unmeasurable on guests
            // and stays at default 0.
            // Use the authority-propagated identity instead of an empty name.
            // Re-verify the propagated original ticket and adopt its identity before
            // adding (no-op when the gate is off).
            string ljnDisplayName = msg.DisplayName ?? string.Empty;
            string ljnAccount = msg.Account ?? string.Empty;
            byte[] ljnEntitlement = null; // adopted from the re-verified ticket
            ReverifyAndAdoptIdentity(msg.PlayerId, msg.OriginalTicket, ref ljnAccount, ref ljnDisplayName, ref ljnEntitlement);
            var newPlayer = new PlayerInfo
            {
                PlayerId = msg.PlayerId,
                DisplayName = ljnDisplayName,
                Account = ljnAccount,
                Entitlement = ljnEntitlement,
                IsReady = true,
                ConnectionState = PlayerConnectionState.Connected,
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            if (ljnEntitlement != null && ljnEntitlement.Length > 0)
                _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via LateJoinNotification: playerId={msg.PlayerId}, bytes={ljnEntitlement.Length}");

            OnPlayerJoined?.Invoke(newPlayer);

            _logger?.KInformation($"[KlothoNetworkService][HandleLateJoinNotification] Late join player added: playerId={msg.PlayerId}, joinTick={msg.JoinTick}");
        }

        // ── Late Join (Host) ──────────────────────

        private void CompleteLateJoinSync(int peerId, PeerSyncState state)
        {
            // Identity validation gate — before slot reservation. P2P validation is synchronous.
            if (!TryValidateIdentityP2P(peerId, isLateJoin: true, out bool validatorRan, out string vAccount, out string vDisplayName, out byte[] vEntitlement))
                return;

            // PlayerId allocation goes through TryReservePlayerSlot — the Post-GameStart
            // path bumps _nextPlayerId and _assignedPlayerIdCount; on overflow it rejects + cleans up.
            if (!TryReservePlayerSlot(peerId, out int newPlayerId))
                return;

            state.Completed = true;

            state.GetBestSample(out int avgRtt, out long avgOffset);

            // Identity: validated value when a validator ran (claimed name ignored), else the client-claimed
            // name, else empty (P2P late-join uses no fabricated name — preserved). Account empty unless validated.
            var ljClaimedName = _peerClaimedDisplayNames.TryGetValue(peerId, out var __ljcn) ? __ljcn : string.Empty;
            var newPlayer = new PlayerInfo
            {
                PlayerId = newPlayerId,
                DisplayName = validatorRan ? (vDisplayName ?? string.Empty) : (ljClaimedName ?? string.Empty),
                Account = validatorRan ? (vAccount ?? string.Empty) : string.Empty,
                // Promote the host-validated original ticket. "" when gate off.
                OriginalTicket = _propagateOriginalTickets && _peerTickets.TryGetValue(peerId, out var ljCapturedTicket)
                    ? (ljCapturedTicket ?? string.Empty) : string.Empty,
                // Store the late-joining guest's lobby-signed entitlement (host-side).
                Entitlement = vEntitlement,
                Ping = avgRtt,
                IsReady = true
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            _peerToPlayer[peerId] = newPlayerId;
            if (newPlayer.Entitlement != null && newPlayer.Entitlement.Length > 0)
                _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via LateJoinHost: playerId={newPlayerId}, bytes={newPlayer.Entitlement.Length}");

            // 2. Target tick for PlayerJoinCommand
            int joinTick = _engine.CurrentTick + _sessionConfig.LateJoinDelayTicks;

            // Send SimulationConfig first so the guest's KlothoConnection can build
            // the Late Join result. Mirrors the SD path order.
            SendSimulationConfig(peerId);

            // 3. Send LateJoinAcceptMessage
            int lateJoinSeedExtraDelay = ComputeRecommendedExtraDelay(avgRtt, newPlayerId, peerId, "LateJoin");
            var accept = new LateJoinAcceptMessage
            {
                PlayerId = newPlayerId,
                CurrentTick = _engine.CurrentTick,
                Magic = _sessionMagic,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset,
                PlayerCount = _players.Count,
                RandomSeed = RandomSeed,
                MaxPlayers = _sessionConfig.MaxPlayers,
                MinPlayers = _sessionConfig.MinPlayers,
                MaxSpectators = _sessionConfig.MaxSpectators,
                AllowLateJoin = _sessionConfig.AllowLateJoin,
                LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                LateJoinDelaySafety = _sessionConfig.LateJoinDelaySafety,
                RttSanityMaxMs = _sessionConfig.RttSanityMaxMs,
                MinStallAbortTicks = _sessionConfig.MinStallAbortTicks,
                CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                AbortGraceMs = _sessionConfig.AbortGraceMs,
                EndGracePolicy = (int)_sessionConfig.EndGracePolicy,
                EndGraceMs = _sessionConfig.EndGraceMs,
                ClientShutdownGraceMs = _sessionConfig.ClientShutdownGraceMs,
                RecommendedExtraDelay = lateJoinSeedExtraDelay,
                JoinTick = joinTick,
            };
            for (int i = 0; i < _players.Count; i++)
            {
                accept.Roster.Add(RosterEntry.FromPlayer(
                    _players[i], _logger, (byte)_players[i].ConnectionState));
                // Index-parallel original tickets so the late-joiner re-verifies every
                // existing player (P2P full-state is host-authored). Only when gate on → empty list otherwise.
                if (_propagateOriginalTickets)
                    accept.RosterTickets.Add(_players[i].OriginalTicket ?? string.Empty);
            }
            using (var serialized = _messageSerializer.SerializePooled(accept))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // 4. Send FullState
            OnFullStateRequested?.Invoke(peerId, _engine.CurrentTick);

            // 5. Register the catchup target
            _lateJoinCatchups[peerId] = new LateJoinCatchupInfo
            {
                PeerId = peerId,
                PlayerId = newPlayerId,
                // The FullState is served at the host's CurrentTick, which is a START-of-tick snapshot
                // (state after CurrentTick-1, still needs CurrentTick's input to advance). The joiner
                // restores CurrentTick and re-executes that tick during catchup, so it needs that tick's
                // other-player inputs replayed — otherwise it runs the tick with empty input and its
                // verified chain diverges/stalls at the served tick. Backfill therefore starts AT the
                // served tick: LastSentTick = CurrentTick-1 (the send loop uses LastSentTick+1). This
                // matches the reconnect / desync-resync paths (KlothoNetworkService.Reconnect.cs,
                // KlothoNetworkService.FullStateResync.cs) which enforce the same contract.
                LastSentTick = _engine.CurrentTick - 1,
                JoinTick = joinTick,
            };

            // 6. Send the LateJoinNotification BEFORE the PlayerJoinCommand. The notification carries the
            // entitlement-bearing OriginalTicket → existing guests set PlayerInfo.Entitlement in
            // HandleLateJoinNotification; the PlayerJoinCommand triggers the deterministic OnPlayerJoinedWorld
            // callback at joinTick. Both go on the same ReliableOrdered channel, so send-order = delivery-order:
            // the command (and thus the callback that reads GetPlayerEntitlement) can only be delivered
            // AFTER the notification → the entitlement is guaranteed set before the callback runs on every peer.
            // Exclude the new joiner — they already received the full roster via LateJoinAcceptMessage.
            var notification = new LateJoinNotificationMessage
            {
                PlayerId = newPlayerId,
                JoinTick = joinTick,
                Account = newPlayer.Account ?? string.Empty,
                DisplayName = newPlayer.DisplayName ?? string.Empty,
                // The late-joiner's original ticket so existing peers re-verify it.
                OriginalTicket = newPlayer.OriginalTicket ?? string.Empty,
            };
            RelayMessage(notification, excludePeerId: peerId, DeliveryMethod.ReliableOrdered);

            // Spectators are tracked in _spectators (disjoint from _peerToPlayer), so RelayMessage
            // does not reach them. Send the same notification separately so spectator UI stays
            // consistent with players.
            if (_spectators.Count > 0)
            {
                using (var serialized = _messageSerializer.SerializePooled(notification))
                {
                    for (int i = 0; i < _spectators.Count; i++)
                    {
                        _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }
            }

            // Insert PlayerJoinCommand + broadcast — AFTER the notification (above), so the deterministic
            // OnPlayerJoinedWorld callback it triggers never precedes the entitlement that callback reads.
            OnLateJoinPlayerAdded?.Invoke(newPlayerId, joinTick);

            // 7. Notify existing peers
            _logger?.KInformation($"[KlothoNetworkService][CompleteLateJoinSync] Late join sync complete: peerId={peerId}, playerId={newPlayerId}, joinTick={joinTick}");
            OnPlayerJoined?.Invoke(newPlayer);
        }

        private void HandleLateJoin(int peerId)
        {
            if (_sessionConfig != null && !_sessionConfig.AllowLateJoin)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleLateJoin] Late join not allowed, peer {peerId} rejected");
                DisconnectWithReason(peerId, JoinFailReason.LateJoinDisabled.ToWireCode());
                return;
            }

            // HandleDataReceived dispatches here only when _gameStarted is true,
            // so the previous `Phase != Playing → StartHandshake` self-redirect is unreachable.
            // Kept as a defensive guard against future callers that bypass HandleDataReceived.
            if (!_gameStarted)
            {
                StartHandshake(peerId);
                return;
            }

            // Pending-aware capacity gate. Redundant with the gate in HandleDataReceived,
            // but kept as second-line defense for callers that may not pass through that dispatch.
            if (EffectivePlayerCount >= MaxPlayerCapacity)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleLateJoin] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                return;
            }

            _logger?.KInformation($"[KlothoNetworkService][HandleLateJoin] Late join handshake started: peerId={peerId}");
            var state = new PeerSyncState
            {
                PeerId = peerId,
                SyncPacketsSent = 0,
                RttSamples = new long[NUM_SYNC_PACKETS],
                ClockOffsetSamples = new long[NUM_SYNC_PACKETS],
                Attempt = 0,
                Completed = false,
                IsLateJoin = true
            };
            _peerSyncStates[peerId] = state;
            SendSyncRequest(peerId, state);
        }

        // Pending handshake count (LateJoin + general — both use _peerSyncStates).
        private int CountPendingHandshakes()
        {
            int count = 0;
            foreach (var kvp in _peerSyncStates)
            {
                if (!kvp.Value.Completed)
                    count++;
            }
            return count;
        }

        // Instance wrapper — config injection + path-tagged logging + structured metrics emission.
        // playerId = game-level player identifier (reported in metrics JSON for telemetry analysis).
        // peerId = connection-level identifier (kept in operational logs for connection debugging).
        // Pure computation lives in xpTURN.Klotho.Core.RecommendedExtraDelayCalculator (shared with SD path).
        private int ComputeRecommendedExtraDelay(int avgRtt, int playerId, int peerId, string pathTag)
        {
            var (extraDelay, fallback, rttTicks, raw, clamped) = RecommendedExtraDelayCalculator.Compute(
                avgRtt,
                _simConfig.TickIntervalMs,
                _sessionConfig.LateJoinDelaySafety,
                _sessionConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            if (fallback)
                _logger?.KWarning($"[KlothoNetworkService][{pathTag}] FallbackPath: avgRtt={avgRtt}ms invalid, peerId={peerId}, clamped={extraDelay}");
            else
                _logger?.KDebug($"[KlothoNetworkService][{pathTag}] RecommendedExtraDelay computed: peerId={peerId}, avgRtt={avgRtt}ms, clamped={extraDelay}");

            // Structured JSON-line metrics — single source of truth for verification scripts and
            // production telemetry. Bool literals lowercased for JSON validity. Path tag is controlled
            // ("LateJoin" / "Reconnect" / "Sync") so no escaping needed.
            string clampedStr = clamped ? "true" : "false";
            string fallbackStr = fallback ? "true" : "false";
            int safety = _sessionConfig.LateJoinDelaySafety;
            _logger?.KInformation($"[Metrics][{pathTag}] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"{pathTag}\",\"avgRtt\":{avgRtt},\"rttTicks\":{rttTicks},\"safety\":{safety},\"raw\":{raw},\"clamped\":{clampedStr},\"extraDelay\":{extraDelay},\"fallback\":{fallbackStr}}}");

            return extraDelay;
        }
    }
}
