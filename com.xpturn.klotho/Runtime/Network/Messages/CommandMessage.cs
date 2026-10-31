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

        // Sender's measured frame-advantage at send time
        // (round(CalculateLocalAdvantage)). The receiver feeds -SenderAdvantage into the
        // remote channel (true GGPO exchange) instead of synthesizing a mirror of its own.
        [KlothoOrder]
        public int SenderAdvantage;

        // bit0 = IsProxyTiming: this command was sent on behalf of another player (host proxy
        // fill / catchup), so its SenderTick/SenderAdvantage carry the SENDING machine's timing,
        // not the slot owner's — receivers must skip the timing vote.
        [KlothoOrder]
        public byte TimingFlags;

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
            // header(1) + Tick(4) + PlayerId(4) + SenderTick(4) + SenderAdvantage(4)
            // + TimingFlags(1) + length prefix(4) + data
            return 1 + 4 + 4 + 4 + 4 + 1 + 4 + CommandDataSpan.Length;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(Tick);
            writer.WriteInt32(PlayerId);
            writer.WriteInt32(SenderTick);
            writer.WriteInt32(SenderAdvantage);
            writer.WriteByte(TimingFlags);
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
            SenderAdvantage = reader.ReadInt32();
            TimingFlags = reader.ReadByte();
            int len = reader.ReadInt32();
            // One bound for both branches: without it the copying branch reads a negative length as no data.
            if (len < 0 || len > reader.Remaining)
                throw new System.IO.InvalidDataException($"Command: length {len} does not fit the {reader.Remaining} remaining bytes");
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
