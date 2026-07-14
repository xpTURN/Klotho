using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// P2P transport for the online desync probe. This class is the edge: it routes probe
    /// messages by topology and enforces the serve budget. Everything past the edge — which peers have
    /// a probe in flight, the captured history, the diff, the verdict — belongs to the engine.
    ///
    /// <para>Both P2P roles serve, not just the host. When the HOST detects the desync, the GUEST is
    /// the responder, so a guest that never rate-limited would be an amplification target for a
    /// malicious host. One peerId-keyed budget covers both roles.</para>
    /// </summary>
    public partial class KlothoNetworkService : IDesyncProbeNetwork
    {
        // Probe-serve budget. Deliberately NOT _lastResyncResponseTime: a desync fires a FullState
        // request and a probe request together, and sharing a window would let the recovery request
        // consume it and drop the probe with no trace.
        private readonly Dictionary<int, ProbePolicy.PeerServeState> _probeServeState =
            new Dictionary<int, ProbePolicy.PeerServeState>();

        public event Action<int, DesyncProbeRequestMessage> OnDesyncProbeRequested;
        public event Action<int, DesyncProbeResponseMessage> OnDesyncProbeResponse;

#pragma warning disable CS0067 // interface-mandated (IDesyncProbeNetwork); never raised in this P2P role — see doc
        /// <summary>Never raised in P2P — a verdict is a report to an authority, and P2P has no trusted one.</summary>
        public event Action<int, DesyncVerdictReportMessage> OnDesyncVerdictReported;
#pragma warning restore CS0067

        /// <summary>
        /// Requester → responder. A guest broadcasts (the star topology delivers only to the host, the
        /// sole peer whose hashes it ever compares against); a host unicasts to the guest it disagrees with.
        /// </summary>
        public void SendDesyncProbeRequest(int targetPeerId, DesyncProbeRequestMessage msg)
        {
            if (msg == null) return;

            if (!IsHost)
            {
                BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
                return;
            }
            SendToPeer(targetPeerId, msg);
        }

        /// <summary>
        /// Responder → requester, always to the peerId the request ARRIVED on. The request's
        /// RequesterPlayerId is never consulted here: it is unauthenticated, and honouring it would let
        /// a forged value aim this response — and the bandwidth behind it — at a third peer.
        /// </summary>
        public void SendDesyncProbeResponse(int targetPeerId, DesyncProbeResponseMessage msg)
        {
            if (msg == null) return;

            if (!IsHost)
            {
                BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);   // star: reaches the host only
                return;
            }
            SendToPeer(targetPeerId, msg);
        }

        /// <summary>No-op: peers are symmetric and mutually untrusted here, and there is no authority log to report into.</summary>
        public void SendDesyncVerdict(DesyncVerdictReportMessage msg) { }

        /// <summary>
        /// A guest only ever disagrees with the host, so it answers peer 0 without a lookup. The host
        /// resolves the player it disagrees with through its own peer↔player binding, which is
        /// authoritative — no wire field is trusted for routing.
        /// </summary>
        public bool TryResolveProbePeer(int playerId, out int peerId)
        {
            if (!IsHost)
            {
                peerId = 0;   // star topology — the host
                return true;
            }

            foreach (var kv in _peerToPlayer)
            {
                if (kv.Value == playerId)
                {
                    peerId = kv.Key;
                    return true;
                }
            }
            peerId = -1;
            return false;
        }

        private void SendToPeer(int peerId, NetworkMessageBase msg)
        {
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        // Serve budget is checked HERE, before the engine sees the request: a throttled request must
        // not cost a ring lookup or a payload build. Passing it raises the event and the engine answers
        // out of its history ring — it never rewinds to reconstruct a tick it no longer has.
        private void HandleDesyncProbeRequest(int peerId, DesyncProbeRequestMessage msg)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!ProbePolicy.TryConsumeServe(_probeServeState, peerId, now, out int throttled))
                return;   // counter-only: see ProbePolicy.PeerServeState

            if (throttled > 0)
                _logger?.KWarning($"[Desync][Probe] serve: peer={peerId}, level={msg.Level}, throttledSinceLastServe={throttled}");

            OnDesyncProbeRequested?.Invoke(peerId, msg);
        }

        private void HandleDesyncProbeResponse(int peerId, DesyncProbeResponseMessage msg)
        {
            OnDesyncProbeResponse?.Invoke(peerId, msg);
        }
    }
}
