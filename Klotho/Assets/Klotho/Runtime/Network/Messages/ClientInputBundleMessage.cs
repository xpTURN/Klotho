using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client → server: resend bundle of unacked inputs (Unreliable).
    /// A single packet contains all unacked inputs for ticks N~M, allowing the server to recover missed inputs in one shot.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ClientInputBundle)]
    public partial class ClientInputBundleMessage : NetworkMessageBase
    {
        public int PlayerId;
        public int Count;

        // Entry array (allocated at a fixed size for reuse; only the first Count items are valid)
        public BundleEntry[] Entries = new BundleEntry[32];

        public struct BundleEntry
        {
            public int Tick;
            public byte[] CommandData;
            public int CommandDataLength;
        }

        public override int GetSerializedSize()
        {
            // type(1) + playerId(4) + count(4) + entries
            int size = 1 + 4 + 4;
            for (int i = 0; i < Count; i++)
            {
                size += 4 + 4 + Entries[i].CommandDataLength; // tick + dataLen + data
            }
            return size;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(PlayerId);
            writer.WriteInt32(Count);
            for (int i = 0; i < Count; i++)
            {
                writer.WriteInt32(Entries[i].Tick);
                writer.WriteInt32(Entries[i].CommandDataLength);
                if (Entries[i].CommandDataLength > 0)
                    writer.WriteRawBytes(Entries[i].CommandData.AsSpan(0, Entries[i].CommandDataLength));
            }
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            PlayerId = reader.ReadInt32();
            Count = reader.ReadInt32();
            if (Entries.Length < Count)
                Entries = new BundleEntry[Count];
            for (int i = 0; i < Count; i++)
            {
                Entries[i].Tick = reader.ReadInt32();
                int len = reader.ReadInt32();
                Entries[i].CommandDataLength = len;
                Entries[i].CommandData = len > 0 ? reader.ReadRawBytes(len).ToArray() : null;
            }
        }

        public void EnsureCapacity(int count)
        {
            if (Entries.Length < count)
                Entries = new BundleEntry[count];
        }
    }
}
