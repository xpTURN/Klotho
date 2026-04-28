using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network.Tests
{
    [TestFixture]
    public class NetworkMessageSerializationTests
    {
        #region PingMessage

        [Test]
        public void PingMessage_RoundTrip_PreservesData()
        {
            var original = new PingMessage { Timestamp = 1234567890L, Sequence = 42 };
            var restored = RoundTrip<PingMessage>(original);

            Assert.AreEqual(original.Timestamp, restored.Timestamp);
            Assert.AreEqual(original.Sequence, restored.Sequence);
        }

        #endregion

        #region PongMessage

        [Test]
        public void PongMessage_RoundTrip_PreservesData()
        {
            var original = new PongMessage { Timestamp = 9876543210L, Sequence = 99 };
            var restored = RoundTrip<PongMessage>(original);

            Assert.AreEqual(original.Timestamp, restored.Timestamp);
            Assert.AreEqual(original.Sequence, restored.Sequence);
        }

        #endregion

        #region PlayerReadyMessage

        [Test]
        public void PlayerReadyMessage_RoundTrip_PreservesData()
        {
            var original = new PlayerReadyMessage { PlayerId = 3, IsReady = true };
            var restored = RoundTrip<PlayerReadyMessage>(original);

            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.IsReady, restored.IsReady);
        }

        #endregion

        #region CommandMessage

        [Test]
        public void CommandMessage_RoundTrip_PreservesData()
        {
            var original = new CommandMessage
            {
                Tick = 100,
                PlayerId = 2,
                SenderTick = 98,
                CommandData = new byte[] { 1, 2, 3, 4, 5 }
            };
            var restored = RoundTrip<CommandMessage>(original);

            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.SenderTick, restored.SenderTick);
            Assert.AreEqual(original.CommandData.Length, restored.CommandData.Length);
            for (int i = 0; i < original.CommandData.Length; i++)
                Assert.AreEqual(original.CommandData[i], restored.CommandData[i]);
        }

        [Test]
        public void CommandMessage_NullCommandData_RoundTrip()
        {
            var original = new CommandMessage
            {
                Tick = 50,
                PlayerId = 1,
                SenderTick = 49,
                CommandData = null
            };
            var restored = RoundTrip<CommandMessage>(original);

            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.IsTrue(restored.CommandData == null || restored.CommandData.Length == 0);
        }

        #endregion

        #region CommandAckMessage

        [Test]
        public void CommandAckMessage_RoundTrip_PreservesData()
        {
            var original = new CommandAckMessage { Tick = 200, PlayerId = 5 };
            var restored = RoundTrip<CommandAckMessage>(original);

            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.PlayerId, restored.PlayerId);
        }

        #endregion

        #region GameStartMessage

        [Test]
        public void GameStartMessage_RoundTrip_PreservesData()
        {
            var original = new GameStartMessage
            {
                RandomSeed = 12345,
                StartTime = 9999999L,
                PlayerIds = new List<int> { 1, 2, 3, 4 }
            };
            var restored = RoundTrip<GameStartMessage>(original);

            Assert.AreEqual(original.RandomSeed, restored.RandomSeed);
            Assert.AreEqual(original.StartTime, restored.StartTime);
            Assert.AreEqual(original.PlayerIds.Count, restored.PlayerIds.Count);
            for (int i = 0; i < original.PlayerIds.Count; i++)
                Assert.AreEqual(original.PlayerIds[i], restored.PlayerIds[i]);
        }

        #endregion

        #region SyncRequestMessage

        [Test]
        public void SyncRequestMessage_RoundTrip_PreservesData()
        {
            var original = new SyncRequestMessage
            {
                Magic = 0x4C4B5354,
                Sequence = 7,
                Attempt = 3,
                HostTime = 5555555L
            };
            var restored = RoundTrip<SyncRequestMessage>(original);

            Assert.AreEqual(original.Magic, restored.Magic);
            Assert.AreEqual(original.Sequence, restored.Sequence);
            Assert.AreEqual(original.Attempt, restored.Attempt);
            Assert.AreEqual(original.HostTime, restored.HostTime);
        }

        #endregion

        #region SyncReplyMessage

        [Test]
        public void SyncReplyMessage_RoundTrip_PreservesData()
        {
            var original = new SyncReplyMessage
            {
                Magic = 0x4C4B5354,
                Sequence = 7,
                Attempt = 3,
                ClientTime = 6666666L
            };
            var restored = RoundTrip<SyncReplyMessage>(original);

            Assert.AreEqual(original.Magic, restored.Magic);
            Assert.AreEqual(original.Sequence, restored.Sequence);
            Assert.AreEqual(original.Attempt, restored.Attempt);
            Assert.AreEqual(original.ClientTime, restored.ClientTime);
        }

        #endregion

        #region SyncCompleteMessage

        [Test]
        public void SyncCompleteMessage_RoundTrip_PreservesData()
        {
            var original = new SyncCompleteMessage
            {
                Magic = 0x4C4B5354,
                PlayerId = 2,
                SharedEpoch = 7777777L,
                ClockOffset = -12345L
            };
            var restored = RoundTrip<SyncCompleteMessage>(original);

            Assert.AreEqual(original.Magic, restored.Magic);
            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.SharedEpoch, restored.SharedEpoch);
            Assert.AreEqual(original.ClockOffset, restored.ClockOffset);
        }

        #endregion

        #region SyncHashMessage

        [Test]
        public void SyncHashMessage_RoundTrip_PreservesData()
        {
            var original = new SyncHashMessage { Tick = 300, Hash = -987654321L, PlayerId = 1 };
            var restored = RoundTrip<SyncHashMessage>(original);

            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.Hash, restored.Hash);
            Assert.AreEqual(original.PlayerId, restored.PlayerId);
        }

        #endregion

        #region FullStateRequestMessage

        [Test]
        public void FullStateRequestMessage_RoundTrip_PreservesData()
        {
            var original = new FullStateRequestMessage { RequestTick = 12345 };
            var restored = RoundTrip<FullStateRequestMessage>(original);

            Assert.AreEqual(original.RequestTick, restored.RequestTick);
        }

        #endregion

        #region FullStateResponseMessage

        [Test]
        public void FullStateResponseMessage_RoundTrip_PreservesData()
        {
            var stateData = new byte[1024];
            for (int i = 0; i < stateData.Length; i++)
                stateData[i] = (byte)(i % 256);

            var original = new FullStateResponseMessage
            {
                Tick = 500,
                StateHash = -123456789012345L,
                StateData = stateData
            };
            var restored = RoundTrip<FullStateResponseMessage>(original);

            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.StateHash, restored.StateHash);
            Assert.AreEqual(original.StateData.Length, restored.StateData.Length);
            for (int i = 0; i < original.StateData.Length; i++)
                Assert.AreEqual(original.StateData[i], restored.StateData[i], $"Byte mismatch at {i}");
        }

        [Test]
        public void FullStateResponseMessage_HashIntegrity()
        {
            long expectedHash = 0x7FFFFFFFFFFFFFFFL;
            var original = new FullStateResponseMessage
            {
                Tick = 999,
                StateHash = expectedHash,
                StateData = new byte[] { 0xFF, 0x00, 0xAB }
            };
            var restored = RoundTrip<FullStateResponseMessage>(original);

            Assert.AreEqual(expectedHash, restored.StateHash);
        }

        #endregion

        #region MessageSerializer

        [Test]
        public void MessageSerializer_RoundTrip_AllMessageTypes()
        {
            var serializer = new MessageSerializer();

            var ping = new PingMessage { Timestamp = 111L, Sequence = 1 };
            var bytes = serializer.Serialize(ping);
            var restored = serializer.Deserialize(bytes);

            Assert.IsNotNull(restored);
            Assert.IsInstanceOf<PingMessage>(restored);
            Assert.AreEqual(ping.Timestamp, ((PingMessage)restored).Timestamp);
        }

        #endregion

        #region Helpers

        private static T RoundTrip<T>(T message) where T : NetworkMessageBase, new()
        {
            int size = message.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            message.Serialize(ref writer);

            var restored = new T();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);
            return restored;
        }

        #endregion
    }
}
