using System;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server → client: verified tick state (ReliableOrdered).
    /// Confirmed input list + state hash.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.VerifiedState)]
    public partial class VerifiedStateMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public long StateHash;

        /// <summary>
        /// Byte array containing the entire serialized confirmed input set.
        /// Convert via CommandFactory.SerializeCommandsTo / DeserializeCommands.
        /// </summary>
        [KlothoOrder]
        public byte[] ConfirmedInputsData;

        [NonSerialized]
        public int ConfirmedInputsDataLength;

        [NonSerialized]
        internal int _confirmedInputsOffset;

        [NonSerialized]
        internal byte[] _sourceBuffer;

        public ReadOnlySpan<byte> ConfirmedInputsSpan
        {
            get
            {
                if (_sourceBuffer != null)
                    return _sourceBuffer.AsSpan(_confirmedInputsOffset, ConfirmedInputsDataLength);
                int len = ConfirmedInputsDataLength > 0 ? ConfirmedInputsDataLength : (ConfirmedInputsData?.Length ?? 0);
                return ConfirmedInputsData.AsSpan(0, len);
            }
        }

        public override int GetSerializedSize()
        {
            int dataLen = ConfirmedInputsDataLength > 0 ? ConfirmedInputsDataLength : (ConfirmedInputsData?.Length ?? 0);
            return 1 + 4 + 8 + 4 + dataLen;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(Tick);
            writer.WriteInt64(StateHash);
            var span = ConfirmedInputsSpan;
            writer.WriteInt32(span.Length);
            if (span.Length > 0)
                writer.WriteRawBytes(span);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            Tick = reader.ReadInt32();
            StateHash = reader.ReadInt64();
            int len = reader.ReadInt32();
            _sourceBuffer = reader.SourceBuffer;
            if (_sourceBuffer != null)
            {
                _confirmedInputsOffset = reader.Position;
                ConfirmedInputsDataLength = len;
                ConfirmedInputsData = null;
                reader.Skip(len);
            }
            else
            {
                ConfirmedInputsData = len > 0 ? reader.ReadRawBytes(len).ToArray() : null;
                ConfirmedInputsDataLength = len;
                _confirmedInputsOffset = 0;
            }
        }
    }
}
