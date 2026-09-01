using System;
using System.Collections.Generic;

using xpTURN.Klotho.Logging; // IKLogger

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Lobby issue coordinator — the event-deferred state machine that sits between the dev lobby's IssueRequest
    /// handler and the two-phase reservation. Extracted from Program.cs so it is unit-testable (inject clock +
    /// send callback + nonce/config policy). Single-threaded: all entry points run on the lobby poll thread
    /// (HandleIssue / HandleReserveAck / SweepTimeouts / HandleClientDisconnect), so no locking here — the
    /// underlying <see cref="DevLobbyCore"/> owns its own lock.
    /// <para>
    /// Flow: an IssueRequest calls TryAssign. Reusing a committed room responds immediately (step-1a);
    /// reserving a fresh room sends a ReservePush and defers the IssueResponse until the dedi's ReserveAck
    /// (step-2); joining a room still awaiting its first ack attaches as a waiter on that pending (step-1b).
    /// commit → mint + respond to all participants; nak/timeout → rollback + IssueFull to all.
    /// </para>
    /// </summary>
    public sealed class DevLobbyReserveCoordinator
    {
        // Per-client participant of a pending reservation (originator + step-1b waiters share one push).
        private sealed class Participant
        {
            public int ClientRequestId;
            public int ClientPeerId;
            public string Nonce;
            public LobbyTicket Ticket; // material minted (signed) only on commit
        }

        private sealed class Pending
        {
            public int PushRequestId;
            public string ServerId;
            public int RoomId;
            public int ServerPeerId;
            public string Host;
            public int Port;
            public long Deadline;
            public readonly List<Participant> Participants = new List<Participant>();
        }

        private readonly DevLobbyCore _core;
        private readonly Func<long> _nowMs;
        private readonly Func<string> _newNonce;
        private readonly Action<int, byte[]> _send;                       // (peerId, wire) → transport.Send
        // (RENDEZVOUS matchId, roomId) → match config. Never pass the instance id here: the dev policy reads the
        // id's trailing ASCII digit to pick a stage, and an instance id ends in a hex token char, so a leak flips
        // the stage for half of all matches — silently. Both call sites are covered by a recording-policy test
        // (StagePolicy_ReceivesRendezvousMatchId_*); ADD ONE ALONGSIDE ANY NEW PUSH PATH.
        private readonly Func<MatchConfigRequest, (int stageId, byte[] payload)> _configPolicy;
        private readonly long _ticketValidityMs;
        private readonly long _ackTimeoutMs;
        private readonly IKLogger _logger;

        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>(); // by push requestId
        private int _pushRequestSeq;

        /// <summary>
        /// What the lobby knows about a match when it builds that match's config payload.
        ///
        /// <para><b>Stage selection reads <see cref="MatchId"/>, never <see cref="InstanceId"/>.</b> The dev
        /// policy derives the stage from the id's trailing ASCII digit, and an instance id ends in a hex token
        /// digit — reading the wrong one silently flips the stage for half of all matches.</para>
        ///
        /// <para><b>Why the instance id and the capacity are here at all.</b> They are inputs a replay verifier
        /// needs and cannot obtain: the instance id is the only key that joins an uploaded replay to the
        /// lobby's own record of that match, and the capacity is tick-0 state (bot ids are numbered past it).
        /// Both must ride the config payload, because that is what reaches every peer and lands in every
        /// peer's replay file.</para>
        /// </summary>
        public readonly struct MatchConfigRequest
        {
            /// <summary>Rendezvous key. The stage comes from THIS.</summary>
            public readonly string MatchId;
            public readonly int RoomId;
            /// <summary>This match instance's key (<c>{matchId}#{token}</c>) — new on every re-reservation.</summary>
            public readonly string InstanceId;
            /// <summary>Assigned server's MaxPlayersPerRoom. <c>0</c> = unknown; consumers fall back locally.</summary>
            public readonly int Capacity;

            public MatchConfigRequest(string matchId, int roomId, string instanceId, int capacity)
            { MatchId = matchId; RoomId = roomId; InstanceId = instanceId; Capacity = capacity; }
        }

        public DevLobbyReserveCoordinator(DevLobbyCore core, Func<long> nowMs, Func<string> newNonce,
                                          Action<int, byte[]> send,
                                          Func<MatchConfigRequest, (int, byte[])> configPolicy,
                                          long ticketValidityMs, long ackTimeoutMs, IKLogger logger = null)
        {
            _core = core;
            _nowMs = nowMs;
            _newNonce = newNonce;
            _send = send;
            _configPolicy = configPolicy ?? (req => (1 + (req.RoomId % 2), (byte[])null));
            _ticketValidityMs = ticketValidityMs;
            _ackTimeoutMs = ackTimeoutMs;
            _logger = logger;
        }

        /// <summary>Handles an IssueRequest. Either responds immediately (Full / committed-room reuse) or
        /// defers (fresh reserve → ReservePush, or join a pending room → waiter).</summary>
        public void HandleIssue(int clientRequestId, int clientPeerId, string authToken, string displayName, string matchId)
        {
            long t = _nowMs();
            long expiresAt = t + _ticketValidityMs;
            string nonce = _newNonce();
            var assign = _core.TryAssign(matchId, nonce, expiresAt);

            if (!assign.Ok)
            {
                SendFull(clientRequestId, clientPeerId);
                _logger?.KInformation($"[DevLobby] issue FULL account={authToken} match={matchId}");
                return;
            }

            var participant = new Participant
            {
                ClientRequestId = clientRequestId,
                ClientPeerId = clientPeerId,
                Nonce = nonce,
                Ticket = new LobbyTicket(authToken, displayName, matchId, t, expiresAt, nonce),
            };

            // step-1a — committed room reuse: config already confirmed on the dedi → respond now.
            if (!assign.AckPending)
            {
                SendIssueOk(participant, assign.Host, assign.Port, assign.RoomId);
                _logger?.KInformation($"[DevLobby] issue account={authToken} match={matchId} → {assign.Host}:{assign.Port} room={assign.RoomId} (committed)");
                return;
            }

            // step-2 — fresh reserve: push the room's match config and await ack before responding.
            if (assign.FreshReserve)
            {
                // The stage policy reads the RENDEZVOUS matchId, never the instance id: it selects the stage from
                // the id's trailing ASCII digit, and an instance id ends in a hex token digit — reading the wrong
                // one silently flips the stage for half of all matches. Both ids are handed over (the policy
                // stamps the instance id INTO the payload, which is how a replay gets its match identity) and
                // MatchConfigRequest's doc comment is where that separation is pinned.
                (int stageId, byte[] payload) = _configPolicy(
                    new MatchConfigRequest(matchId, assign.RoomId, assign.InstanceId, assign.Capacity));
                int pushId = ++_pushRequestSeq;
                var pending = new Pending
                {
                    PushRequestId = pushId,
                    ServerId = assign.ServerId,
                    RoomId = assign.RoomId,
                    ServerPeerId = assign.ServerPeerId,
                    Host = assign.Host,
                    Port = assign.Port,
                    Deadline = t + _ackTimeoutMs,
                };
                pending.Participants.Add(participant);
                _pending[pushId] = pending;
                _send(assign.ServerPeerId, LobbyWire.EncodeReservePush(pushId, assign.RoomId, assign.InstanceId, stageId, payload, expiresAt));
                _logger?.KInformation($"[DevLobby] reserve push id={pushId} match={matchId} instance={assign.InstanceId} → room={assign.RoomId} stage={stageId} (awaiting ack)");
                return;
            }

            // step-1b — joined a room still awaiting its first ack: attach as a waiter on that pending.
            var host = FindPendingByRoom(assign.ServerId, assign.RoomId);
            if (host != null)
            {
                host.Participants.Add(participant);
                _logger?.KInformation($"[DevLobby] issue match={matchId} → room={assign.RoomId} joined pending id={host.PushRequestId} as waiter");
                return;
            }

            // Defensive: AckPending but no pending entry (inconsistency) → roll this reservation back, Full.
            _core.ReleaseReservation(nonce);
            SendFull(clientRequestId, clientPeerId);
            _logger?.KWarning($"[DevLobby] issue match={matchId} room={assign.RoomId} AckPending with no pending — rolled back");
        }

        /// <summary>Handles a ReserveAck from the dedi. ok → commit + mint/respond to all participants;
        /// nak → rollback + Full to all. <paramref name="senderPeerId"/> is the transport peer the ack
        /// arrived on; it must match the dedi the matching ReservePush was sent to, else the ack is ignored
        /// (any lobby-connected peer can forge a ReserveAck otherwise — sequential push ids are guessable,
        /// so an unverified ack lets a client commit/rollback another client's pending reservation).</summary>
        public void HandleReserveAck(int senderPeerId, int pushRequestId, bool ok, byte nakReason)
        {
            if (!_pending.TryGetValue(pushRequestId, out var p)) return; // stale / already resolved
            if (p.ServerPeerId != senderPeerId) return; // not from the dedi we pushed to — ignore, leave pending intact
            _pending.Remove(pushRequestId);

            if (ok)
            {
                _core.CommitReservation(p.ServerId, p.RoomId);
                foreach (var part in p.Participants)
                    SendIssueOk(part, p.Host, p.Port, p.RoomId);
                _logger?.KInformation($"[DevLobby] reserve ack OK id={pushRequestId} room={p.RoomId} → {p.Participants.Count} issued");
            }
            else
            {
                RollbackAndFail(p);
                _logger?.KWarning($"[DevLobby] reserve ack NAK id={pushRequestId} room={p.RoomId} reason={nakReason} → {p.Participants.Count} full");
            }
        }

        /// <summary>Rolls back pendings whose ack deadline elapsed (dead server / lost ack). Call from the
        /// lobby loop next to <c>DevLobbyCore.Sweep</c>.</summary>
        public void SweepTimeouts(long now)
        {
            List<int> expired = null;
            foreach (var kv in _pending)
                if (now > kv.Value.Deadline) (expired ??= new List<int>()).Add(kv.Key);
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                var p = _pending[expired[i]];
                _pending.Remove(expired[i]);
                RollbackAndFail(p);
                _logger?.KWarning($"[DevLobby] reserve timeout id={p.PushRequestId} room={p.RoomId} → {p.Participants.Count} full");
            }
        }

        /// <summary>A client peer dropped before its deferred response. Removes it from any pending and rolls
        /// back its reservation; an emptied pending is discarded (its outstanding push self-heals via the dedi's
        /// reservation TTL). Call from the lobby's OnPeerDisconnected.</summary>
        public void HandleClientDisconnect(int clientPeerId)
        {
            List<int> emptied = null;
            foreach (var kv in _pending)
            {
                var parts = kv.Value.Participants;
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    if (parts[i].ClientPeerId != clientPeerId) continue;
                    _core.ReleaseReservation(parts[i].Nonce);
                    parts.RemoveAt(i);
                }
                if (parts.Count == 0) (emptied ??= new List<int>()).Add(kv.Key);
            }
            if (emptied != null)
                for (int i = 0; i < emptied.Count; i++) _pending.Remove(emptied[i]);
        }

        /// <summary>On a dedi reconnect (ServerRegister re-received), re-push its active reservations so the
        /// dedi rebuilds its match-config table. Fire-and-forget (no pending entry, not ack-gated): the
        /// client already holds its ticket, so the dedi's ReserveAck lands on no pending and is ignored.</summary>
        public void RePushReservations(string serverId, int serverPeerId)
        {
            foreach (var (roomId, matchId, instanceId, expiresAt, capacity) in _core.ActiveRoomReservations(serverId))
            {
                // Same request the original push built — including the instance id, so the re-pushed payload
                // carries the SAME identity the dedi already materialized (see the id note below).
                (int stageId, byte[] payload) = _configPolicy(
                    new MatchConfigRequest(matchId, roomId, instanceId, capacity));
                // Re-push the SAME instance id: if a client already joined, the dedi's entry is materialized and a
                // different id would trip its match-conflict check (ReserveNakMatchConflict) against ourselves.
                _send(serverPeerId, LobbyWire.EncodeReservePush(++_pushRequestSeq, roomId, instanceId, stageId, payload, expiresAt));
                _logger?.KInformation($"[DevLobby] reserve re-push room={roomId} match={matchId} instance={instanceId} stage={stageId} (server reconnect)");
            }
        }

        private Pending FindPendingByRoom(string serverId, int roomId)
        {
            foreach (var kv in _pending)
                if (kv.Value.RoomId == roomId && string.Equals(kv.Value.ServerId, serverId, StringComparison.Ordinal))
                    return kv.Value;
            return null;
        }

        private void RollbackAndFail(Pending p)
        {
            foreach (var part in p.Participants)
            {
                _core.ReleaseReservation(part.Nonce);
                SendFull(part.ClientRequestId, part.ClientPeerId);
            }
        }

        private void SendIssueOk(Participant part, string host, int port, int roomId)
        {
            string wire = _core.Issue(part.Ticket); // sign only now (commit) — a rolled-back reserve never mints
            _send(part.ClientPeerId, LobbyWire.EncodeIssueResponse(part.ClientRequestId, LobbyWire.IssueOk,
                wire, host, port, roomId, LobbyWire.ModeSd));
        }

        private void SendFull(int clientRequestId, int clientPeerId)
            => _send(clientPeerId, LobbyWire.EncodeIssueResponse(clientRequestId, LobbyWire.IssueFull,
                string.Empty, string.Empty, 0, -1, LobbyWire.ModeSd));
    }
}
