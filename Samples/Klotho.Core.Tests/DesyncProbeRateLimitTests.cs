using System.Collections.Generic;
using Xunit;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// The serve budgets on the SD server's probe edge. Two failures are being fenced off here, and both
    /// of them are silent — they lose the diagnosis without losing anything a player would notice, which
    /// is exactly why they need tests rather than a bug report.
    ///
    /// <para>1. A single desync fires a FullStateRequest, a ProbeRequest and (a moment later) a verdict.
    /// If any two of them shared a rate-limit window, whichever arrived first would take it and the rest
    /// would be dropped with no log line.</para>
    ///
    /// <para>2. One probe episode is TWO serves to the same peer — L1, then L2 for the tick L1 pointed at,
    /// about one RTT later. A one-per-window cooldown drops the L2 of every honest episode, and the P2P
    /// diagnosis never completes.</para>
    /// </summary>
    public sealed class DesyncProbeRateLimitTests
    {
        [Fact] // #1: the three paths a desync triggers must not share a window
        public void FullStateProbeAndVerdict_HaveIndependentBudgets()
        {
            var (svc, tx, ser, _) = NewServer();

            int fullStates = 0, probes = 0, verdicts = 0;
            svc.OnFullStateRequested += (_, _) => fullStates++;
            svc.OnDesyncProbeRequested += (_, _) => probes++;
            svc.OnDesyncVerdictReported += (_, _) => verdicts++;

            // One desync, all three messages inside the same cooldown window.
            Feed(tx, ser, 7, new FullStateRequestMessage { RequestTick = 100 });
            Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = 1, Level = 1, FromTick = 90, ToTick = 100 });
            Feed(tx, ser, 7, new DesyncVerdictReportMessage { DivergedTick = 95, Class = 1, Layer = 0, TypeIdOrParticipantIdx = -1 });

            Assert.Equal(1, fullStates);
            Assert.Equal(1, probes);
            Assert.Equal(1, verdicts);
        }

        [Fact] // #2: L2 arrives ~1 RTT after L1, deep inside the window — it must still be served
        public void ProbeServe_AllowsTheSecondServeOfAnEpisode()
        {
            long now = 1_000_000;
            var (svc, tx, ser, _) = NewServer(() => now);

            var levels = new List<byte>();
            svc.OnDesyncProbeRequested += (_, msg) => levels.Add(msg.Level);

            Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = 1, Level = 1, FromTick = 90, ToTick = 100 });
            now += 60;   // one RTT later, far inside the 2s window
            Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = 2, Level = 2, FromTick = 95, ToTick = 95 });

            Assert.Equal(new byte[] { 1, 2 }, levels);
        }

        [Fact] // an honest episode is 2 serves; a third inside the window is a flood
        public void ProbeServe_ThrottlesBeyondTheEpisodeBurst()
        {
            long now = 1_000_000;
            var (svc, tx, ser, _) = NewServer(() => now);

            int served = 0;
            svc.OnDesyncProbeRequested += (_, _) => served++;

            for (int i = 0; i < 50; i++)
            {
                now += 10;
                Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = i, Level = 1, FromTick = 90, ToTick = 100 });
            }

            Assert.Equal(2, served);
        }

        [Fact] // the window is per-peer: one peer flooding must not silence another peer's diagnosis
        public void ProbeServe_BudgetIsPerPeer()
        {
            long now = 1_000_000;
            var (svc, tx, ser, _) = NewServer(() => now);

            var servedPeers = new List<int>();
            svc.OnDesyncProbeRequested += (peerId, _) => servedPeers.Add(peerId);

            for (int i = 0; i < 20; i++)
                Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = i, Level = 1, FromTick = 90, ToTick = 100 });

            Feed(tx, ser, 8, new DesyncProbeRequestMessage { CorrelationId = 99, Level = 1, FromTick = 90, ToTick = 100 });

            Assert.Equal(new[] { 7, 7, 8 }, servedPeers);
        }

        [Fact] // a fresh window reopens the budget
        public void ProbeServe_BudgetRefillsAfterTheWindow()
        {
            long now = 1_000_000;
            var (svc, tx, ser, _) = NewServer(() => now);

            int served = 0;
            svc.OnDesyncProbeRequested += (_, _) => served++;

            for (int i = 0; i < 5; i++)
                Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = i, Level = 1, FromTick = 90, ToTick = 100 });
            Assert.Equal(2, served);

            now += 2000;   // window elapsed
            Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = 100, Level = 1, FromTick = 90, ToTick = 100 });

            Assert.Equal(3, served);
        }

        [Fact] // a verdict is a single message per episode — its own budget stays at one per window
        public void VerdictReceive_ThrottlesRepeats()
        {
            long now = 1_000_000;
            var (svc, tx, ser, _) = NewServer(() => now);

            int received = 0;
            svc.OnDesyncVerdictReported += (_, _) => received++;

            for (int i = 0; i < 20; i++)
            {
                now += 10;
                Feed(tx, ser, 7, new DesyncVerdictReportMessage { DivergedTick = 95 + i, Class = 1, Layer = 0, TypeIdOrParticipantIdx = -1 });
            }

            Assert.True(received <= 2, $"verdict flood must be throttled, but {received} got through");
            Assert.True(received >= 1, "the first verdict must always be received");
        }

        [Fact] // peerIds are recycled: a stale window would drop the next peer's FIRST probe
        public void Disconnect_ClearsProbeAndVerdictBudgets()
        {
            long now = 1_000_000;
            var (svc, tx, ser, _) = NewServer(() => now);

            int probes = 0, verdicts = 0;
            svc.OnDesyncProbeRequested += (_, _) => probes++;
            svc.OnDesyncVerdictReported += (_, _) => verdicts++;

            for (int i = 0; i < 5; i++)
                Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = i, Level = 1, FromTick = 90, ToTick = 100 });
            Feed(tx, ser, 7, new DesyncVerdictReportMessage { DivergedTick = 95, Class = 1, Layer = 0, TypeIdOrParticipantIdx = -1 });
            Feed(tx, ser, 7, new DesyncVerdictReportMessage { DivergedTick = 96, Class = 1, Layer = 0, TypeIdOrParticipantIdx = -1 });
            int probesBefore = probes, verdictsBefore = verdicts;

            tx.RaiseDisconnect(7);

            // Same peerId, new peer, still inside the old window — it must start with a full budget.
            Feed(tx, ser, 7, new DesyncProbeRequestMessage { CorrelationId = 100, Level = 1, FromTick = 90, ToTick = 100 });
            Feed(tx, ser, 7, new DesyncVerdictReportMessage { DivergedTick = 200, Class = 1, Layer = 0, TypeIdOrParticipantIdx = -1 });

            Assert.Equal(probesBefore + 1, probes);
            Assert.Equal(verdictsBefore + 1, verdicts);
        }

        // ── harness ───────────────────────────────────────────────────

        private static (ServerNetworkService svc, FakeTransport tx, MessageSerializer ser, System.Func<long> now)
            NewServer(System.Func<long> nowProvider = null)
        {
            var tx = new FakeTransport();
            var svc = new ServerNetworkService();
            svc.Initialize(tx, null, null);
            if (nowProvider != null) svc.SetNowProviderForTest(nowProvider);
            svc.CreateRoom("test", 4);
            return (svc, tx, new MessageSerializer(), nowProvider);
        }

        private static void Feed(FakeTransport tx, MessageSerializer ser, int peerId, NetworkMessageBase msg)
        {
            byte[] bytes = ser.Serialize(msg);
            tx.RaiseData(peerId, bytes, bytes.Length);
        }
    }
}
