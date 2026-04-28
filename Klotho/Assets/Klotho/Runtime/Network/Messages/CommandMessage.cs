using System;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Network message that transmits a player command for a specific tick.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.Command)]
    public partial class CommandMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public int SenderTick;

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
            return 1 + 4 + 4 + 4 + 4 + CommandDataSpan.Length; // header(1) + Tick(4) + PlayerId(4) + SenderTick(4) + length prefix(4) + data
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(Tick);
            writer.WriteInt32(PlayerId);
            writer.WriteInt32(SenderTick);
            var span = CommandDataSpan;
            writer.WriteInt32(span.Length);
            if (span.Length > 0)
                writer.WriteRawBytes(span);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            Tick = reader.ReadInt32();
            PlayerId = reader.ReadInt32();
            SenderTick = reader.ReadInt32();
            int len = reader.ReadInt32();
            _sourceBuffer = reader.SourceBuffer;
            if (_sourceBuffer != null)
            {
                _commandDataOffset = reader.Position;
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
