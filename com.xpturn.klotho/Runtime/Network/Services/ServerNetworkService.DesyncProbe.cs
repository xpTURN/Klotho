using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// SD server side of the online desync probe. The server only ever RESPONDS: it serves the
    /// breakdown a client asks for out of its own hash-history ring, and it receives the client's
    /// diagnosis for logging. It never probes anyone and never acts on what a client reports.
    ///
    /// <para>Two separate budgets, and the separation is the point: probe serving and verdict receiving
    /// are triggered by the same desync as the FullState request, so any window they shared would let
    /// one of them silently swallow the others.</para>
    /// </summary>
    public partial class ServerNetworkService : IDesyncProbeNetwork
    {
        private readonly Dictionary<int, ProbePolicy.PeerServeState> _probeServeState =
            new Dictionary<int, ProbePolicy.PeerServeState>();
        private readonly Dictionary<int, ProbePolicy.PeerServeState> _verdictReportState =
            new Dictionary<int, ProbePolicy.PeerServeState>();

        public event Action<int, DesyncProbeRequestMessage> OnDesyncProbeRequested;
#pragma warning disable CS0067 // interface-mandated (IDesyncProbeNetwork); the server only sends responses, never receives them — see doc
        public event Action<int, DesyncProbeResponseMessage> OnDesyncProbeResponse;
#pragma warning restore CS0067
        public event Action<int, DesyncVerdictReportMessage> OnDesyncVerdictReported;

        /// <summary>No-op: the server is authoritative — it has nobody to ask.</summary>
        public void SendDesyncProbeRequest(int targetPeerId, DesyncProbeRequestMessage msg) { }

        /// <summary>No-op: a verdict is a client's report TO the server.</summary>
        public void SendDesyncVerdict(DesyncVerdictReportMessage msg) { }

        /// <summary>False — the server never originates a probe, so it has no target to resolve.</summary>
        public bool TryResolveProbePeer(int playerId, out int peerId)
        {
            peerId = -1;
            return false;
        }

        public void SendDesyncProbeResponse(int targetPeerId, DesyncProbeResponseMessage msg)
        {
            if (msg == null) return;
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(targetPeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        // Budget first, engine second: a throttled request must not reach the ring lookup or the
        // payload build it was trying to buy. Mirrors HandleFullStateRequest's cooldown→event order.
        private void HandleDesyncProbeRequest(int peerId, DesyncProbeRequestMessage msg)
        {
            long now = _nowProvider();
            if (!ProbePolicy.TryConsumeServe(_probeServeState, peerId, now, out int throttled))
                return;   // counter-only drop

            if (throttled > 0)
                _logger?.KWarning($"[Desync][Probe] serve: peerId={peerId}, level={msg.Level}, throttledSinceLastServe={throttled}");

            OnDesyncProbeRequested?.Invoke(peerId, msg);
        }

        // Untrusted input, so the budget is here for a different reason than amplification: a client
        // that could report at will would be writing straight into the server's log and disk.
        private void HandleDesyncVerdictReport(int peerId, DesyncVerdictReportMessage msg)
        {
            long now = _nowProvider();
            if (!ProbePolicy.TryConsumeServe(_verdictReportState, peerId, now, out int throttled))
                return;

            if (throttled > 0)
                _logger?.KWarning($"[Desync][Verdict] report: peerId={peerId}, throttledSinceLastServe={throttled}");

            // Validation, name mapping and logging happen in the engine, which owns the registry and
            // the participant list. The service stays what it is here: the rate-limited edge.
            OnDesyncVerdictReported?.Invoke(peerId, msg);
        }
    }
}
