using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Action command (skill, attack, etc.). Contains ActionId, target entity, and position.
    /// </summary>
    [KlothoSerializable(2)]
    public partial class ActionCommand : CommandBase
    {
        [KlothoOrder]
        public int ActionId { get; set; }

        [KlothoOrder]
        public int TargetEntityId { get; set; }

        [KlothoOrder]
        public FPVector3 Position { get; set; }

        public ActionCommand() : base() { }

        public ActionCommand(int playerId, int tick, int actionId, int targetEntityId = -1)
            : base(playerId, tick)
        {
            ActionId = actionId;
            TargetEntityId = targetEntityId;
        }
    }
}
