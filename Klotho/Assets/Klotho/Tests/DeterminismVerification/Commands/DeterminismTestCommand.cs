using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.DeterminismVerification
{
    [KlothoSerializable(200)]
    public partial class DeterminismTestCommand : CommandBase
    {
        public override bool IsContinuousInput => true;

        [KlothoOrder]
        public FPVector3 MoveDirection { get; set; }

        [KlothoOrder]
        public int ActionId { get; set; }

        public DeterminismTestCommand() : base() { }

        public DeterminismTestCommand(int playerId, int tick, FPVector3 moveDir, int actionId)
            : base(playerId, tick)
        {
            MoveDirection = moveDir;
            ActionId = actionId;
        }
    }
}
