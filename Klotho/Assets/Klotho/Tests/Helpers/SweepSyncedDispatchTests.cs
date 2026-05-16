using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Integration test layered on the sweep harness — a representative stress cell
    /// (60s disconnect × QuorumMissDropTicks=20) raises Synced events on both peers' simulations
    /// at three timings (pre-disconnect, during stall, post-reconnect) and asserts dispatch
    /// invariants under the full disconnect → stall → reconnect → resim cycle.
    ///
    /// Invariants:
    ///   - No double-fire of any Synced event on either peer (regression guard for the
    ///     post-rollback / resim single-fire fix).
    ///   - Host and Guest agree on the verified-phase raises (pre-disconnect, post-reconnect):
    ///     each peer dispatches exactly once.
    ///   - evt.Tick stamping invariant — every dispatched event's Tick equals the engine
    ///     dispatch tick.
    ///   - The during-stall raise is dispatched once on Host (chain auto-fills via presumed-drop
    ///     once QuorumMissDropTicks elapses) and zero times on Guest (resync state-jump skips the
    ///     intermediate ticks). Any deviation indicates a regression in dispatch semantics.
    ///   - SweepPassCriteria main + auxiliary signals stay clean (WIPED 0, hash unchanged, etc.).
    /// </summary>
    [TestFixture]
    internal class SweepSyncedDispatchTests
    {
        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_700;
            public override EventMode Mode => EventMode.Synced;
        }

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void WireEventRaiserFromEngine(TestPeer peer)
        {
            var collector = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(peer.Engine);
            peer.Simulation.EventRaiser = collector;
        }

        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SweepSyncedDispatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [Test]
        public void Sweep_StressCell_SyncedDispatch_NoDoubleFire_HostGuestParity()
        {
            var cell = new SweepMatrixConfigGenerator.Cell(60f, 20, 0);
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = cell.QuorumMissDropTicks,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(2);
                var guest = harness.AddGuest();
                harness.StartPlaying();
                WireEventRaiserFromEngine(harness.Host);
                WireEventRaiserFromEngine(guest);

                const int baselineTick = 50;
                const int preDisconnectRaiseTick = baselineTick + 5;   // 55
                const int duringStallRaiseTick = baselineTick + 50;    // 100 — host's chain
                                                                       // auto-advances via
                                                                       // presumed-drop (after
                                                                       // QuorumMissDropTicks=20).

                bool armPostReconnect = false;
                bool hostRaisedP3 = false;
                bool guestRaisedP3 = false;
                int hostP3Tick = -1;
                int guestP3Tick = -1;

                var hostCounts = new Dictionary<int, int>();
                var guestCounts = new Dictionary<int, int>();

                harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
                {
                    if (!(evt is TestSyncedEvent t)) return;
                    Assert.AreEqual(tick, t.Tick,
                        $"Host: evt.Tick {t.Tick} must equal dispatch tick {tick} (payload={t.Payload}).");
                    hostCounts.TryGetValue(t.Payload, out int c);
                    hostCounts[t.Payload] = c + 1;
                };
                guest.Engine.OnSyncedEvent += (tick, evt) =>
                {
                    if (!(evt is TestSyncedEvent t)) return;
                    Assert.AreEqual(tick, t.Tick,
                        $"Guest: evt.Tick {t.Tick} must equal dispatch tick {tick} (payload={t.Payload}).");
                    guestCounts.TryGetValue(t.Payload, out int c);
                    guestCounts[t.Payload] = c + 1;
                };

                // OnAfterTickRaise fires inside _simulation.Tick(), which the engine calls right
                // after _eventCollector.BeginTick(CurrentTick). Reading peer.Engine.CurrentTick
                // returns the executing tick T. TestSim's own CurrentTick can diverge from
                // engine.CurrentTick after FullStateResync (state transfer jumps engine.CurrentTick
                // without re-running sim.Tick for the skipped range), so engine.CurrentTick is the
                // authoritative tick for raise gating. Post-reconnect raise uses an arm-flag
                // instead of a precomputed tick to decouple from per-peer verified-chain timing.
                harness.Host.Simulation.OnAfterTickRaise = (simTick, raiser) =>
                {
                    if (raiser == null) return;
                    int engineTick = harness.Host.Engine.CurrentTick;
                    if (engineTick == preDisconnectRaiseTick)
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
                    else if (engineTick == duringStallRaiseTick)
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 2 });
                    else if (armPostReconnect && !hostRaisedP3)
                    {
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 3 });
                        hostRaisedP3 = true;
                        hostP3Tick = engineTick;
                    }
                };
                guest.Simulation.OnAfterTickRaise = (simTick, raiser) =>
                {
                    if (raiser == null) return;
                    int engineTick = guest.Engine.CurrentTick;
                    if (engineTick == preDisconnectRaiseTick)
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
                    else if (engineTick == duringStallRaiseTick)
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 2 });
                    else if (armPostReconnect && !guestRaisedP3)
                    {
                        raiser.RaiseEvent(new TestSyncedEvent { Payload = 3 });
                        guestRaisedP3 = true;
                        guestP3Tick = engineTick;
                    }
                };

                harness.AdvanceAllToTick(baselineTick);
                harness.AdvanceAllToTick(preDisconnectRaiseTick + 5);

                using var criteria = new SweepPassCriteria(harness);
                criteria.CaptureBaseline();

                int stallPlayerId = guest.LocalPlayerId;
                int disconnectTicks = (int)(cell.DisconnectDurationSec * 1000f / simConfig.TickIntervalMs);
                int stalledEndTick = harness.Host.CurrentTick + disconnectTicks;

                harness.DisconnectPeer(guest);
                harness.AdvanceWithStalledPeer(stalledEndTick, stallPlayerId);

                harness.ReconnectPeer(guest);
                // ReconnectPeer re-Initializes the engine, which creates a fresh _eventCollector;
                // re-wire the simulation's EventRaiser to the live collector so post-reconnect
                // raises route into the new engine's event buffer.
                WireEventRaiserFromEngine(guest);
                harness.PumpMessages(20);

                // Settle phase — let the reconnect resync handshake plus the input-gap recovery
                // converge before arming the post-reconnect raise so dispatch timing on the guest
                // is deterministic.
                harness.AdvanceAllToTick(harness.Host.CurrentTick + 100);

                armPostReconnect = true;
                harness.AdvanceAllToTick(harness.Host.CurrentTick + 100);
                armPostReconnect = false;

                int recoverEndTick = harness.Host.CurrentTick;
                criteria.AssertAll(recoverEndTick);

                // Verified-phase raises — exactly one dispatch per peer.
                AssertCount(hostCounts, payload: 1, expected: 1, peer: "Host", label: "pre-disconnect");
                AssertCount(hostCounts, payload: 3, expected: 1, peer: "Host", label: "post-reconnect");
                AssertCount(guestCounts, payload: 1, expected: 1, peer: "Guest", label: "pre-disconnect");
                AssertCount(guestCounts, payload: 3, expected: 1, peer: "Guest", label: "post-reconnect");

                // During-stall raise — Host advances through the tick under presumed-drop auto-fill
                // (QuorumMissDropTicks=20 < stall duration), dispatching exactly once. Guest's
                // resync state-jump skips the tick entirely, so it never dispatches.
                AssertCount(hostCounts, payload: 2, expected: 1, peer: "Host", label: "during-stall");
                AssertCount(guestCounts, payload: 2, expected: 0, peer: "Guest", label: "during-stall");

                TestContext.WriteLine(
                    $"{cell} OK | host=[1:{GetCount(hostCounts, 1)}, 2:{GetCount(hostCounts, 2)}, 3:{GetCount(hostCounts, 3)}]@P3={hostP3Tick} " +
                    $"guest=[1:{GetCount(guestCounts, 1)}, 2:{GetCount(guestCounts, 2)}, 3:{GetCount(guestCounts, 3)}]@P3={guestP3Tick} " +
                    $"hostTick={harness.Host.CurrentTick} hostVerified={harness.Host.Engine.LastVerifiedTick} guestVerified={guest.Engine.LastVerifiedTick}");
            }
            finally
            {
                harness.Reset();
            }
        }

        private static int GetCount(Dictionary<int, int> map, int payload)
            => map.TryGetValue(payload, out int c) ? c : 0;

        private static void AssertCount(Dictionary<int, int> map, int payload, int expected, string peer, string label)
        {
            int actual = GetCount(map, payload);
            Assert.AreEqual(expected, actual,
                $"{peer} {label} Synced (payload={payload}) must dispatch {expected} time(s) — got {actual}.");
        }
    }
}
