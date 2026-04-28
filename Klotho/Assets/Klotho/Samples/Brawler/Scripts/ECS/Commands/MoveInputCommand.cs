using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(100)]
    public partial class MoveInputCommand : CommandBase
    {
        public override bool IsContinuousInput => true;

        [KlothoOrder(0)] public FP64 HorizontalAxis;  // -1 ~ 1 (X axis on XZ plane)
        [KlothoOrder(1)] public FP64 VerticalAxis;    // -1 ~ 1 (Z axis on XZ plane)
        [KlothoOrder(2)] public bool JumpPressed;
        [KlothoOrder(3)] public bool JumpHeld;
    }
}
