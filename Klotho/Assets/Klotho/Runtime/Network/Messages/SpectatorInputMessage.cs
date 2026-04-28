using System;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SpectatorInput)]
    public partial class SpectatorInputMessage : NetworkMessageBase
    {
        [KlothoOrder] public int StartTick;
        [KlothoOrder] public int TickCount;
        [KlothoOrder] public byte[] InputData;
        [NonSerialized] public int InputDataLength;

        public override int GetSerializedSize()
            => base.GetSerializedSize() + 4 + 4 + 4 + InputDataLength;

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(StartTick);
            writer.WriteInt32(TickCount);
            writer.WriteInt32(InputDataLength);
            if (InputDataLength > 0)
                writer.WriteRawBytes(InputData.AsSpan(0, InputDataLength));
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            StartTick = reader.ReadInt32();
            TickCount = reader.ReadInt32();
            InputDataLength = reader.ReadInt32();
            InputData = InputDataLength > 0 ? reader.ReadRawBytes(InputDataLength).ToArray() : null;
        }
    }
}
