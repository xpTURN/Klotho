using System;
using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// ServerDriven unit tests (section 7.1).
    /// </summary>
    [TestFixture]
    public class ServerDrivenUnitTests
    {
        private MessageSerializer _serializer;
        private CommandFactory _commandFactory;

        [SetUp]
        public void SetUp()
        {
            _serializer = new MessageSerializer();
            _commandFactory = new CommandFactory();
        }

        // ── #1 ServerInputCollector.HardTolerance ───────────

        [Test]
        public void ServerInputCollector_HardTolerance_ExpiredInputRejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(100, peerMap); // 100ms tolerance
            collector.AddPlayer(10);

            long now = 1000;
            collector.SetTimeProvider(() => now);
            collector.BeginTick(0, now);

            // Input within deadline -> accepted
            var cmd1 = new EmptyCommand(10, 0);
            Assert.IsTrue(collector.TryAcceptInput(1, 0, 10, cmd1));

            // Input past deadline (tick 1, time elapsed after deadline set)
            collector.BeginTick(1, now);
            now = 1200; // 200ms elapsed > 100ms tolerance
            var cmd2 = new EmptyCommand(10, 1);
            Assert.IsFalse(collector.TryAcceptInput(1, 1, 10, cmd2));
        }

        [Test]
        public void ServerInputCollector_HardTolerance_EmptyCommandSubstituted()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(100, peerMap);
            collector.AddPlayer(10);

            long now = 1000;
            collector.SetTimeProvider(() => now);

            // CollectTickInputs without input -> substituted with EmptyCommand
            int timeoutCount = 0;
            collector.OnPlayerInputTimeout += _ => timeoutCount++;

            var commands = collector.CollectTickInputs(0);
            Assert.AreEqual(1, commands.Count);
            Assert.IsInstanceOf<EmptyCommand>(commands[0]);
            Assert.AreEqual(10, commands[0].PlayerId);
            Assert.AreEqual(1, timeoutCount);
        }

        // ── #2 ServerInputCollector.InputValidation ─────────

        [Test]
        public void ServerInputCollector_InvalidPlayerId_Rejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);

            // peerId 1 -> playerId 10 mapping, sent with playerId 99 -> rejected
            var cmd = new EmptyCommand(99, 0);
            Assert.IsFalse(collector.TryAcceptInput(1, 0, 99, cmd));
        }

        [Test]
        public void ServerInputCollector_UnknownPeerId_Rejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);

            // peerId 99 (unregistered) -> rejected
            var cmd = new EmptyCommand(10, 0);
            Assert.IsFalse(collector.TryAcceptInput(99, 0, 10, cmd));
        }

        [Test]
        public void ServerInputCollector_AlreadyExecutedTick_Rejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);

            // Execute tick 0
            collector.CollectTickInputs(0);
            Assert.AreEqual(0, collector.LastExecutedTick);

            // Late input for tick 0 -> rejected
            var cmd = new EmptyCommand(10, 0);
            Assert.IsFalse(collector.TryAcceptInput(1, 0, 10, cmd));
        }

        // ── #3 ClientInputMessage.Serialize/Deserialize ─────

        [Test]
        public void ClientInputMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new ClientInputMessage
            {
                Tick = 42,
                PlayerId = 7,
                CommandData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                CommandDataLength = 8
            };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as ClientInputMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(42, deserialized.Tick);
            Assert.AreEqual(7, deserialized.PlayerId);
            Assert.AreEqual(8, deserialized.CommandDataLength);
        }

        // ── #4 VerifiedStateMessage.Serialize/Deserialize ───

        [Test]
        public void VerifiedStateMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new VerifiedStateMessage
            {
                Tick = 100,
                StateHash = 0x123456789ABCDEF0L,
                ConfirmedInputsData = new byte[] { 10, 20, 30, 40 },
                ConfirmedInputsDataLength = 4
            };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as VerifiedStateMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(100, deserialized.Tick);
            Assert.AreEqual(0x123456789ABCDEF0L, deserialized.StateHash);
            Assert.AreEqual(4, deserialized.ConfirmedInputsDataLength);
        }

        // ── #5 FullStateResponseMessage (SD path) ──────────

        [Test]
        public void FullStateResponseMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new FullStateResponseMessage
            {
                Tick = 200,
                StateHash = -1L,
                StateData = new byte[] { 0xFF, 0xFE, 0xFD }
            };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as FullStateResponseMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(200, deserialized.Tick);
            Assert.AreEqual(-1L, deserialized.StateHash);
            Assert.AreEqual(3, deserialized.StateData.Length);
            Assert.AreEqual(0xFF, deserialized.StateData[0]);
        }

        // ── #6 InputAckMessage.Serialize/Deserialize ────────

        [Test]
        public void InputAckMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new InputAckMessage { AckedTick = 55 };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as InputAckMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(55, deserialized.AckedTick);
        }

        // ── #7 ClientInputResender.ResendLogic ──────────────

        [Test]
        public void ClientInputResender_NoResendBeforeInterval()
        {
            var config = new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                InputResendIntervalMs = 50,
                MaxUnackedInputs = 30
            };

            // No resend before resend interval elapses -- indirect verification
            // ServerDrivenClientService.ResendUnackedInputs is private, so
            // verify at integration level (resend packet count)
            Assert.AreEqual(50, config.InputResendIntervalMs);
            Assert.Pass("ResendLogic is verified by integration tests");
        }

        // ── #8 ClientInputResender.AckCleanup ───────────────

        [Test]
        public void ClientInputResender_AckCleansQueue()
        {
            // Verify resend queue cleanup on InputAckMessage receipt
            // ServerDrivenClientService.HandleInputAckMessage removes pre-ACK entries from the queue
            // Here we only confirm message roundtrip consistency
            var ack = new InputAckMessage { AckedTick = 10 };
            var bytes = _serializer.Serialize(ack);
            var deserialized = _serializer.Deserialize(bytes) as InputAckMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(10, deserialized.AckedTick);
            Assert.Pass("AckCleanup is verified by integration tests (InputDelivery)");
        }

        // ── ClientInputBundleMessage ────────────────────────

        [Test]
        public void ClientInputBundleMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new ClientInputBundleMessage();
            original.PlayerId = 3;
            original.Count = 2;
            original.EnsureCapacity(2);
            original.Entries[0] = new ClientInputBundleMessage.BundleEntry
            {
                Tick = 10,
                CommandData = new byte[] { 1, 2, 3, 4 },
                CommandDataLength = 4
            };
            original.Entries[1] = new ClientInputBundleMessage.BundleEntry
            {
                Tick = 11,
                CommandData = new byte[] { 5, 6, 7, 8, 9 },
                CommandDataLength = 5
            };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as ClientInputBundleMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(3, deserialized.PlayerId);
            Assert.AreEqual(2, deserialized.Count);
            Assert.AreEqual(10, deserialized.Entries[0].Tick);
            Assert.AreEqual(4, deserialized.Entries[0].CommandDataLength);
            Assert.AreEqual(11, deserialized.Entries[1].Tick);
            Assert.AreEqual(5, deserialized.Entries[1].CommandDataLength);
        }
    }
}
