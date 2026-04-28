using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Move command. A continuous input command that specifies a target position.
    /// </summary>
    [KlothoSerializable(1)]
    public partial class MoveCommand : CommandBase
    {
        public override bool IsContinuousInput => true;

        [KlothoOrder]
        public FPVector3 Target { get; set; }

        public MoveCommand() : base() { }

        public MoveCommand(int playerId, int tick, FPVector3 target) : base(playerId, tick)
        {
            Target = target;
        }
    }
}
