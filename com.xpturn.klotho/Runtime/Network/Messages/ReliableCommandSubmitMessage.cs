using System;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client/guest → authority (server-driven server / P2P host) submit of a reliable command
    /// (<see cref="Core.IReliableCommand"/>), delivered reliably-ordered. Carries the serialized
    /// command with NO client-assigned tick — the authority assigns the execution tick on arrival.
    /// PlayerId is a mirror for cheap peer↔player validation (reject a spoofed peer) without
    /// deserializing; the authoritative PlayerId and the per-player sequence number live inside
    /// CommandData. CommandData byte handling mirrors <see cref="CommandMessage"/> (length-prefixed,
    /// zero-copy span from the receive buffer).
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReliableCommandSubmit)]
    public partial class ReliableCommandSubmitMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public byte[] CommandData;

        [NonSerialized]
        public int CommandDataLength;

        [NonSerialized]
        internal int _commandDataOffset;

        [NonSerialized]
        internal byte[] _sourceBuffer;

        public ReadOnlySpan<byte> CommandDataSpan
        {
            get
            {
                if (_sourceBuffer != null)
                    return _sourceBuffer.AsSpan(_commandDataOffset, CommandDataLength);
                int len = CommandDataLength > 0 ? CommandDataLength : (CommandData?.Length ?? 0);
                return CommandData.AsSpan(0, len);
            }
        }

        public override int GetSerializedSize()
        {
            // header(1) + PlayerId(4) + length prefix(4) + data
            return 1 + 4 + 4 + CommandDataSpan.Length;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(PlayerId);
            var span = CommandDataSpan;
            writer.WriteInt32(span.Length);
            if (span.Length > 0)
                writer.WriteRawBytes(span);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            PlayerId = reader.ReadInt32();
            int len = reader.ReadInt32();
            // One bound for both branches: without it the copying branch reads a negative length as no data.
            if (len < 0 || len > reader.Remaining)
                throw new System.IO.InvalidDataException($"ReliableCommandSubmit: length {len} does not fit the {reader.Remaining} remaining bytes");
            _sourceBuffer = reader.SourceBuffer;
            if (_sourceBuffer != null)
            {
                _commandDataOffset = reader.SourceOffset + reader.Position;
                CommandDataLength = len;
                CommandData = null;
                reader.Skip(len);
            }
            else
            {
                CommandData = len > 0 ? reader.ReadRawBytes(len).ToArray() : null;
                CommandDataLength = len;
                _commandDataOffset = 0;
            }
        }
    }
}
