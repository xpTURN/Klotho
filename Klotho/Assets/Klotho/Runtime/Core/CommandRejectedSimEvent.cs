using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Game-layer command rejection event — Synced (dispatched only on Verified ticks).
    /// Emitted from inside ECS systems when a command fails an application-level check
    /// (e.g. spawn duplicate). Mode=Synced ensures it fires only on the server's verified path,
    /// while client predicted paths emit-then-cancel automatically on rollback.
    /// Consumed server-side and forwarded to the originating peer as a CommandRejectedMessage hint.
    /// </summary>
    [KlothoSerializable(4)]
    public partial class CommandRejectedSimEvent : SimulationEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public int CommandTypeId;

        [KlothoOrder]
        public byte Reason;

        public RejectionReason ReasonEnum
        {
            get => (RejectionReason)Reason;
            set => Reason = (byte)value;
        }
    }
}
