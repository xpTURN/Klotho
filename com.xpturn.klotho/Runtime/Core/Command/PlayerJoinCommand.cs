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

        /// <summary>
        /// The joining player's verified data (entitlement bytes) as the AUTHORITY held it at the join
        /// tick, or null when nothing was issued.
        ///
        /// <para><b>Why it rides the command.</b> Join-time world seeding reads this per-player value
        /// through the engine, which sources it from the network service — and a replay session has none.
        /// The command is already in the recorded tick stream and is already re-executed on playback, so it
        /// is the one carrier that reaches every node AND every replay at the right tick. Safe to trust
        /// because only the host/server creates this command; a client-authored command would let a client
        /// state its own entitlement.</para>
        ///
        /// <para>⚠ <b>Always assign it at creation, null included.</b> Commands are pooled and the pool does
        /// not clear fields, so a rented instance can still hold the previous joiner's bytes.</para>
        /// </summary>
        public byte[] Entitlement;

        // Sorts before same-tick gameplay reliable commands (OrderKey 0): a join must establish the
        // player's participant slot and any join-time world setup before that tick's other commands run.
        // The negative base keeps joins ahead of gameplay; +JoinedPlayerId is a stable tiebreak for
        // simultaneous joins. OrderKey is sort-only (CommandOrdering.Compare), never serialized.
        private const int JoinOrderBase = -1_000_000;
        public int OrderKey => JoinOrderBase + JoinedPlayerId;

        // JoinedPlayerId(4) + entitlement length(4) + its bytes. Variable size is fine: commands are
        // length-prefixed on the wire and in the replay buffer, so only this number has to be right.
        public override int GetSerializedSize()
            => base.GetSerializedSize() + 4 + 4 + (Entitlement?.Length ?? 0);

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(JoinedPlayerId);
            int len = Entitlement?.Length ?? 0;
            writer.WriteInt32(len);
            if (len > 0)
                writer.WriteRawBytes(Entitlement);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            JoinedPlayerId = reader.ReadInt32();
            int len = reader.ReadInt32();
            // Assigned unconditionally — a pooled instance must not keep the previous join's bytes.
            Entitlement = len > 0 && reader.Remaining >= len ? reader.ReadRawBytes(len).ToArray() : null;
        }
    }
}
