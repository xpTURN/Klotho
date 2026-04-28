using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// System command for a player joining.
    /// </summary>
    [KlothoSerializable(10)]
    public partial class PlayerJoinCommand : CommandBase, ISystemCommand
    {
        public int JoinedPlayerId;
        public int OrderKey => JoinedPlayerId;

        public override int GetSerializedSize() => base.GetSerializedSize() + 4;

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(JoinedPlayerId);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            JoinedPlayerId = reader.ReadInt32();
        }
    }
}
