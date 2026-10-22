using System;
using NUnit.Framework;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Serialization.Tests
{
    /// <summary>
    /// The bytes of filled collection fields do not change with the codec's null handling.
    ///
    /// <para>The goldens were taken from the codecs the analyzer generated before null collections were
    /// handled (count 0 for null was already the encoding of an empty collection). Nothing on the wire says
    /// which codec wrote it — the ready fingerprint covers component layout, colliders, NavMesh and the game
    /// slot, not command or message codecs — so a client and a server built a version apart must agree byte for
    /// byte. The engine messages are generated in the Runtime assembly itself.</para>
    /// </summary>
    [TestFixture]
    public class CollectionWireFormatTests
    {
        private static string Hex(int size, Func<byte[], int> write)
        {
            var buffer = new byte[size];
            int written = write(buffer);
            Assert.AreEqual(size, written, "bytes written vs GetSerializedSize()");
            return Convert.ToHexString(buffer);
        }

        private static string Hex(NetworkMessageBase message) => Hex(message.GetSerializedSize(), b =>
        {
            var w = new SpanWriter(b);
            message.Serialize(ref w);
            return w.Position;
        });

        [Test]
        public void Command_FilledCollections()
        {
            var command = NullCollectionTests.FilledCommand();
            command.PlayerId = 1;
            command.Tick = 2;
            string hex = Hex(command.GetSerializedSize(), b => { var w = new SpanWriter(b); command.Serialize(ref w); return w.Position; });

            Assert.AreEqual(CommandGolden, hex);
        }

        [Test]
        public void DataAsset_FilledCollections()
        {
            var asset = NullCollectionTests.FilledAsset();
            string hex = Hex(asset.GetSerializedSize(), b => { var w = new SpanWriter(b); asset.Serialize(ref w); return w.Position; });

            Assert.AreEqual(DataAssetGolden, hex);
        }

        [Test]
        public void GameStartMessage_PlayerIds()
        {
            var message = new GameStartMessage { RandomSeed = 7, MaxPlayers = 4 };
            message.PlayerIds.AddRange(new[] { 1, 2, 3 });

            Assert.AreEqual(GameStartGolden, Hex(message));
        }

        [Test]
        public void SimulationConfigMessage_OverrideLists()
        {
            var message = new SimulationConfigMessage { TickIntervalMs = 50, MaxEntities = 256 };
            message.MaxCountOverrideTypeIds.AddRange(new[] { 10, 11 });
            message.MaxCountOverrideValues.AddRange(new[] { 64, 32 });
            message.PrunedComponentTypeIds.Add(99);

            Assert.AreEqual(SimulationConfigGolden, Hex(message));
        }

        [Test]
        public void SyncCompleteMessage_RosterLists()
        {
            var message = new SyncCompleteMessage { PlayerId = 2, Magic = 5 };
            message.Roster.Add(new RosterEntry { PlayerId = 1, ConnectionState = 1, ReadyState = 1 });
            message.RosterTickets.Add("tk-1");
            message.RosterTickets.Add("한");
            message.RosterEntitlementLengths.Add(3);

            Assert.AreEqual(SyncCompleteGolden, Hex(message));
        }

        private const string CommandGolden =
            "57240000010000000200000002000000010000006106000000ED959CEAB8800100000001000000780200000001000000"
            + "02000000010000000300000002000000010000000A000000020000001400000001000000010000000200000001000000"
            + "03000000040000000000000007000000";
        private const string DataAssetGolden =
            "5624000002000000010000006106000000ED959CEAB88001000000010000007802000000010000000200000001000000"
            + "0300000001000000000000000100000002000000010000000A0000000200000014000000010000000100000002000000"
            + "0100000003000000040000000000000007000000";
        private const string GameStartGolden =
            "0D0000000000000000030000000100000002000000030000000700000004000000000000000000000000000000000000"
            + "0000000000000000000000000000000000000000000000000000000000000000000000000000";
        private const string SimulationConfigGolden =
            "5A3200000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000000000000000000000000000000000020000000A0000000B000000020000004000000020"
            + "0000000100000063000000";
        private const string SyncCompleteGolden =
            "340500000000000000020000000000000000000000000000000000000000000000010000000100000001010000000000"
            + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            + "0000000000000000000000000000000000000000000000000000000200000004000000746B2D3103000000ED959C0000"
            + "00000100000003000000";
    }
}
