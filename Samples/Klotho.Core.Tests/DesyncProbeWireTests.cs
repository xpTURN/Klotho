using Xunit;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// The probe payload is hand-packed, so the code generator does not check it for us: an offset
    /// mistake would show up as a plausible-looking but wrong diagnosis, not as a crash. These pin the
    /// layout round trip and, just as importantly, the rejection of blobs whose declared counts do not
    /// match the bytes actually present (an attacker's cheapest way in on this path).
    /// </summary>
    public sealed class DesyncProbeWireTests
    {
        [Fact]
        public void L1_RoundTrips()
        {
            var ticks = new[] { 100, 101, 102 };
            var totals = new[] { 0x1111L, unchecked((long)0xFFFF_FFFF_FFFF_FFFF), 0L };

            byte[] blob = DesyncProbePayload.PackL1(ticks, totals, ticks.Length);

            Assert.True(DesyncProbePayload.TryUnpackL1(blob, out int[] outTicks, out long[] outTotals));
            Assert.Equal(ticks, outTicks);
            Assert.Equal(totals, outTotals);
        }

        [Fact] // the "I have no history for that" answer — the requester must degrade, not wait out its timeout
        public void L1_EmptyAnswer_UnpacksToNothing()
        {
            byte[] blob = DesyncProbePayload.PackL1(new int[0], new long[0], 0);

            Assert.True(DesyncProbePayload.TryUnpackL1(blob, out int[] ticks, out long[] totals));
            Assert.Empty(ticks);
            Assert.Empty(totals);
        }

        [Fact]
        public void L1_CountNotMatchingByteLength_IsRejected()
        {
            byte[] blob = DesyncProbePayload.PackL1(new[] { 5, 6 }, new[] { 1L, 2L }, 2);

            byte[] truncated = new byte[blob.Length - 1];
            System.Array.Copy(blob, truncated, truncated.Length);
            Assert.False(DesyncProbePayload.TryUnpackL1(truncated, out _, out _));

            byte[] padded = new byte[blob.Length + 1];
            System.Array.Copy(blob, padded, blob.Length);
            Assert.False(DesyncProbePayload.TryUnpackL1(padded, out _, out _));
        }

        [Fact] // a huge count with no bytes behind it must not make the reader allocate for it
        public void L1_OversizedCount_IsRejected()
        {
            var blob = new byte[4];
            blob[0] = 0xFF; blob[1] = 0xFF; blob[2] = 0xFF; blob[3] = 0x7F;   // int.MaxValue entries, 0 bytes of them

            Assert.False(DesyncProbePayload.TryUnpackL1(blob, out _, out _));
        }

        [Fact] // a count that overflows int32 in the length check (4 + count*12) must not slip through
        public void L1_OverflowWrappedCount_IsRejected()
        {
            // count*12 wraps 32-bit to 8 (int.MaxValue at :53 wraps negative and is caught for the wrong
            // reason; this positive wrap is the real hole). Unguarded "4 + count*12 == 12" would pass and
            // allocate int[count] (~1.4GB) then long[count] (>2GB, throws). Fixed by widening to long.
            int count = 357913942;
            var blob = new byte[12];
            blob[0] = (byte)count;
            blob[1] = (byte)(count >> 8);
            blob[2] = (byte)(count >> 16);
            blob[3] = (byte)(count >> 24);

            Assert.False(DesyncProbePayload.TryUnpackL1(blob, out _, out _));
        }

        [Fact]
        public void L2_RoundTrips()
        {
            var componentHashes = new ulong[] { 0xAAAA, 0xBBBB, 0 };
            var componentCounts = new[] { 3, 0, 17 };
            var systemHashes = new ulong[] { 0xCCCC, 0xDDDD };

            byte[] blob = DesyncProbePayload.PackL2(componentHashes, componentCounts, systemHashes);

            Assert.True(DesyncProbePayload.TryUnpackL2(blob, out ulong[] outHashes, out int[] outCounts, out ulong[] outSystems));
            Assert.Equal(componentHashes, outHashes);
            Assert.Equal(componentCounts, outCounts);
            Assert.Equal(systemHashes, outSystems);
        }

        [Fact] // an unavailable breakdown is an empty one, and it must survive the round trip as such
        public void L2_EmptyAnswer_UnpacksToNothing()
        {
            byte[] blob = DesyncProbePayload.PackL2(new ulong[0], new int[0], new ulong[0]);

            Assert.True(DesyncProbePayload.TryUnpackL2(blob, out ulong[] hashes, out _, out ulong[] systems));
            Assert.Empty(hashes);
            Assert.Empty(systems);
        }

        [Fact]
        public void L2_CountNotMatchingByteLength_IsRejected()
        {
            byte[] blob = DesyncProbePayload.PackL2(new ulong[] { 1, 2 }, new[] { 1, 2 }, new ulong[] { 3 });

            byte[] truncated = new byte[blob.Length - 4];
            System.Array.Copy(blob, truncated, truncated.Length);
            Assert.False(DesyncProbePayload.TryUnpackL2(truncated, out _, out _, out _));

            byte[] padded = new byte[blob.Length + 4];
            System.Array.Copy(blob, padded, blob.Length);
            Assert.False(DesyncProbePayload.TryUnpackL2(padded, out _, out _, out _));
        }

        [Fact] // a type count that would index past the blob must be refused before any allocation
        public void L2_OversizedTypeCount_IsRejected()
        {
            var blob = new byte[8];
            blob[0] = 0xFF; blob[1] = 0xFF; blob[2] = 0xFF; blob[3] = 0x7F;

            Assert.False(DesyncProbePayload.TryUnpackL2(blob, out _, out _, out _));
        }

        [Fact] // participantCount that overflows int32 in the length check (… + participantCount*8) must not slip through
        public void L2_OverflowWrappedParticipantCount_IsRejected()
        {
            // typeCount=0, then participantCount*8 wraps 32-bit to 0 — the typeCount guard at :106 already
            // uses (long), but this second field was unguarded, so "4 + 0 + 4 + 0 == 8" would pass and
            // allocate ulong[participantCount] (~4.3GB, throws). Fixed by widening to long.
            int participantCount = 536870912;   // 2^29
            var blob = new byte[8];              // [typeCount=0][participantCount]
            blob[4] = (byte)participantCount;
            blob[5] = (byte)(participantCount >> 8);
            blob[6] = (byte)(participantCount >> 16);
            blob[7] = (byte)(participantCount >> 24);

            Assert.False(DesyncProbePayload.TryUnpackL2(blob, out _, out _, out _));
        }

        [Fact] // the three probe messages must survive the generated serializer, ids and all
        public void ProbeMessages_RoundTripThroughSerializer()
        {
            var serializer = new MessageSerializer();

            var request = new DesyncProbeRequestMessage
            {
                CorrelationId = 7,
                LevelEnum = DesyncProbeLevel.Breakdown,
                FromTick = 120,
                ToTick = 120,
                RequesterPlayerId = 3,
            };
            var decodedRequest = (DesyncProbeRequestMessage)RoundTrip(serializer, request);
            Assert.Equal(7, decodedRequest.CorrelationId);
            Assert.Equal(DesyncProbeLevel.Breakdown, decodedRequest.LevelEnum);
            Assert.Equal(120, decodedRequest.FromTick);
            Assert.Equal(120, decodedRequest.ToTick);
            Assert.Equal(3, decodedRequest.RequesterPlayerId);

            var response = new DesyncProbeResponseMessage
            {
                CorrelationId = 7,
                LevelEnum = DesyncProbeLevel.TickHashes,
                BaseTick = 100,
                CmdHashAtTick = 0x1234_5678_9ABC_DEF0,
                Payload = DesyncProbePayload.PackL1(new[] { 100, 101 }, new[] { 5L, 6L }, 2),
            };
            var decodedResponse = (DesyncProbeResponseMessage)RoundTrip(serializer, response);
            Assert.Equal(7, decodedResponse.CorrelationId);
            Assert.Equal(100, decodedResponse.BaseTick);
            Assert.Equal(0x1234_5678_9ABC_DEF0, decodedResponse.CmdHashAtTick);
            Assert.True(DesyncProbePayload.TryUnpackL1(decodedResponse.Payload, out int[] ticks, out long[] totals));
            Assert.Equal(new[] { 100, 101 }, ticks);
            Assert.Equal(new[] { 5L, 6L }, totals);

            var verdict = new DesyncVerdictReportMessage
            {
                DivergedTick = 42,
                Class = 1,
                Layer = 2,
                TypeIdOrParticipantIdx = 5,
                LocalHash = -1,
                RemoteHash = 99,
            };
            var decodedVerdict = (DesyncVerdictReportMessage)RoundTrip(serializer, verdict);
            Assert.Equal(42, decodedVerdict.DivergedTick);
            Assert.Equal(1, decodedVerdict.Class);
            Assert.Equal(2, decodedVerdict.Layer);
            Assert.Equal(5, decodedVerdict.TypeIdOrParticipantIdx);
            Assert.Equal(-1, decodedVerdict.LocalHash);
            Assert.Equal(99, decodedVerdict.RemoteHash);
        }

        private static INetworkMessage RoundTrip(MessageSerializer serializer, NetworkMessageBase msg)
        {
            byte[] bytes = serializer.Serialize(msg);
            return serializer.Deserialize(bytes, bytes.Length);
        }
    }
}
