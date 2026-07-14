using System;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Hand-packed wire layout of <see cref="DesyncProbeResponseMessage.Payload"/>.
    ///
    /// <para>Level 1 — <c>int count; count × (int tick, long totalHash)</c></para>
    /// <para>Level 2 — <c>int typeCount; typeCount × (int count, ulong hash); int participantCount;
    /// participantCount × (ulong hash)</c></para>
    ///
    /// <para>Every unpack re-derives the expected byte length from the leading counts and rejects the
    /// payload if it does not match exactly. A count can therefore never make the reader allocate or
    /// read past what the sender actually paid for in bytes.</para>
    ///
    /// <para>Unpacking allocates: it runs only after a desync has been detected, on the response path.
    /// Packing writes into a caller-sized array — also post-detection.</para>
    /// </summary>
    public static class DesyncProbePayload
    {
        private const int L1EntryBytes = 12;   // int tick + long total
        private const int L2TypeBytes = 12;    // int count + ulong hash
        private const int L2SystemBytes = 8;   // ulong hash

        // "Diagnostics unavailable" is read off the unpacked result (an empty tick / component array),
        // not from the raw blob: an empty L1 answer is 4 bytes and an empty L2 answer is 8, so raw
        // length alone cannot tell unavailable from malformed.

        public static byte[] PackL1(ReadOnlySpan<int> ticks, ReadOnlySpan<long> totals, int count)
        {
            var buf = new byte[4 + count * L1EntryBytes];
            var w = new SpanWriter(buf.AsSpan());
            w.WriteInt32(count);
            for (int i = 0; i < count; i++)
            {
                w.WriteInt32(ticks[i]);
                w.WriteInt64(totals[i]);
            }
            return buf;
        }

        /// <summary>
        /// False when the blob is malformed (truncated, padded, or a count that does not account for
        /// exactly the bytes present). A well-formed empty answer yields zero-length arrays and true.
        /// </summary>
        public static bool TryUnpackL1(byte[] payload, out int[] ticks, out long[] totals)
        {
            ticks = Array.Empty<int>();
            totals = Array.Empty<long>();
            if (payload == null || payload.Length == 0) return true;   // unavailable
            if (payload.Length < 4) return false;

            var r = new SpanReader(payload.AsSpan());
            int count = r.ReadInt32();
            if (count < 0 || payload.Length != 4 + (long)count * L1EntryBytes) return false;
            if (count == 0) return true;

            ticks = new int[count];
            totals = new long[count];
            for (int i = 0; i < count; i++)
            {
                ticks[i] = r.ReadInt32();
                totals[i] = r.ReadInt64();
            }
            return true;
        }

        public static byte[] PackL2(ReadOnlySpan<ulong> componentHashes, ReadOnlySpan<int> componentCounts,
            ReadOnlySpan<ulong> systemHashes)
        {
            int typeCount = componentHashes.Length;
            int participantCount = systemHashes.Length;

            var buf = new byte[4 + typeCount * L2TypeBytes + 4 + participantCount * L2SystemBytes];
            var w = new SpanWriter(buf.AsSpan());
            w.WriteInt32(typeCount);
            for (int i = 0; i < typeCount; i++)
            {
                w.WriteInt32(componentCounts[i]);
                w.WriteUInt64(componentHashes[i]);
            }
            w.WriteInt32(participantCount);
            for (int i = 0; i < participantCount; i++)
                w.WriteUInt64(systemHashes[i]);
            return buf;
        }

        /// <summary>
        /// False when the blob is malformed. A well-formed empty answer (typeCount 0, participantCount 0)
        /// yields zero-length arrays and true.
        /// </summary>
        public static bool TryUnpackL2(byte[] payload, out ulong[] componentHashes, out int[] componentCounts,
            out ulong[] systemHashes)
        {
            componentHashes = Array.Empty<ulong>();
            componentCounts = Array.Empty<int>();
            systemHashes = Array.Empty<ulong>();
            if (payload == null || payload.Length == 0) return true;   // unavailable
            if (payload.Length < 8) return false;

            var r = new SpanReader(payload.AsSpan());
            int typeCount = r.ReadInt32();
            // Bound typeCount before it is multiplied out: the payload must at least hold its own
            // component block plus the participant-count field.
            if (typeCount < 0 || payload.Length < 4 + (long)typeCount * L2TypeBytes + 4) return false;

            var hashes = new ulong[typeCount];
            var counts = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                counts[i] = r.ReadInt32();
                hashes[i] = r.ReadUInt64();
            }

            int participantCount = r.ReadInt32();
            if (participantCount < 0
                || payload.Length != 4 + (long)typeCount * L2TypeBytes + 4 + (long)participantCount * L2SystemBytes)
                return false;

            var sys = new ulong[participantCount];
            for (int i = 0; i < participantCount; i++)
                sys[i] = r.ReadUInt64();

            componentHashes = hashes;
            componentCounts = counts;
            systemHashes = sys;
            return true;
        }
    }
}
