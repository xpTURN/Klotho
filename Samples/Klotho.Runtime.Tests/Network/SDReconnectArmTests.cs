using System;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// SD fault-injection in-session reconnect.
    /// (1) ArmInSessionReconnectForFaultInjection drives the SD reconnect state machine
    ///     (mirror of the P2P guard ReconnectTests.ArmInSessionReconnectForFaultInjection_*).
    /// (2) HandleFullStateResponse restores Phase lost on a self-initiated disconnect,
    ///     and is a no-op on the natural NetworkFailure path (Phase already Playing).
    /// </summary>
    [TestFixture]
    public class SDReconnectArmTests
    {
        private TestTransport _transport;
        private CommandFactory _commandFactory;
        private ServerDrivenClientService _service;

        private const BindingFlags PrivFlags = BindingFlags.NonPublic | BindingFlags.Instance;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _transport = new TestTransport();
            _commandFactory = new CommandFactory();

            _service = new ServerDrivenClientService();
            _service.Initialize(_transport, _commandFactory, null);
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── Reflection helpers ──────────────────────────────────

        private string GetReconnectStateName()
        {
            var f = typeof(ServerDrivenClientService).GetField("_reconnectState", PrivFlags);
            return f.GetValue(_service).ToString();
        }

        private long GetReconnectStartTimeMs()
        {
            var f = typeof(ServerDrivenClientService).GetField("_reconnectStartTimeMs", PrivFlags);
            return (long)f.GetValue(_service);
        }

        private void SetReconnectState(string name)
        {
            var f = typeof(ServerDrivenClientService).GetField("_reconnectState", PrivFlags);
            f.SetValue(_service, Enum.Parse(f.FieldType, name));
        }

        private void SetPhaseField(SessionPhase phase)
        {
            var f = typeof(ServerDrivenClientService).GetField("_phase", PrivFlags);
            f.SetValue(_service, phase);
        }

        private void InvokeHandleFullStateResponse(int tick)
        {
            var msg = new FullStateResponseMessage
            {
                Tick = tick,
                StateHash = 0,
                StateData = Array.Empty<byte>(),
                StaticFingerprint = 0,
            };
            var m = typeof(ServerDrivenClientService).GetMethod("HandleFullStateResponse", PrivFlags);
            m.Invoke(_service, new object[] { msg });
        }

        // ── Step 1: arm ─────────────────────────────────────────

        [Test]
        public void SdArm_SeedsTimer_NoInstantTimeout()
        {
            bool reconnecting = false;
            _service.OnReconnecting += () => reconnecting = true;

            _service.ArmInSessionReconnectForFaultInjection();

            Assert.AreEqual("WaitingForTransport", GetReconnectStateName());
            Assert.AreNotEqual(0L, GetReconnectStartTimeMs(),
                "arm must seed _reconnectStartTimeMs, else UpdateReconnect instantly times out");
            Assert.IsTrue(reconnecting, "arm must raise OnReconnecting");

            // A single Update() must not collapse to Failed (no instant timeout).
            _service.Update();
            Assert.AreNotEqual("Failed", GetReconnectStateName(),
                "armed reconnect must not instantly time out");
        }

        [Test]
        public void SdArm_Update_TransportReconnects_SendsReconnectRequest()
        {
            _service.ArmInSessionReconnectForFaultInjection();

            // UpdateReconnect (WaitingForTransport) → transport Connect → OnConnected →
            // HandleConnected reconnect branch → SendReconnectRequest.
            _service.Update();

            Assert.AreEqual("SendingRequest", GetReconnectStateName(),
                "transport reconnect must advance the state machine to SendingRequest");
            Assert.Greater(_transport.SendCallCount, 0,
                "a ReconnectRequest must be sent after transport reconnects");
        }

        // ── Step 2: Phase restore on reconnect completion ──

        [Test]
        public void SdHandleFullStateResponse_RestoresPhaseToPlaying()
        {
            // Fault-injection self-disconnect left Phase=Disconnected; reconnect handshake done.
            SetPhaseField(SessionPhase.Disconnected);
            SetReconnectState("WaitingForFullState");

            int phaseChanges = 0;
            SessionPhase last = SessionPhase.None;
            _service.OnPhaseChanged += p => { phaseChanges++; last = p; };
            bool reconnected = false;
            _service.OnReconnected += () => reconnected = true;

            InvokeHandleFullStateResponse(tick: 100);

            Assert.AreEqual(SessionPhase.Playing, _service.Phase, "Phase must be restored to Playing");
            Assert.AreEqual(1, phaseChanges, "exactly one Disconnected→Playing transition");
            Assert.AreEqual(SessionPhase.Playing, last);
            Assert.IsTrue(reconnected, "OnReconnected must fire on reconnect completion");
            Assert.AreEqual("None", GetReconnectStateName());
        }

        [Test]
        public void SdHandleFullStateResponse_NaturalPath_NoPhaseFlap()
        {
            // Natural NetworkFailure reconnect never loses Phase==Playing → restore is a no-op.
            SetPhaseField(SessionPhase.Playing);
            SetReconnectState("WaitingForFullState");

            int phaseChanges = 0;
            _service.OnPhaseChanged += _ => phaseChanges++;

            InvokeHandleFullStateResponse(tick: 100);

            Assert.AreEqual(SessionPhase.Playing, _service.Phase);
            Assert.AreEqual(0, phaseChanges,
                "Phase setter change-guard must suppress OnPhaseChanged when already Playing (regression guard)");
        }
    }
}
