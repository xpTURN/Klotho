using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    [KlothoComponent(1)]
    [KlothoCoreComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct TransformComponent : IComponent
    {
        public FPVector3 Position;
        public FP64 Rotation;
        public FPVector3 Scale;

        public FPVector3 PreviousPosition;
        public FP64 PreviousRotation;
        
        // True once PreviousPosition / PreviousRotation have been initialized (either by the
        // engine auto-init hook in Frame.Add<TransformComponent>, the per-tick SavePrev pass,
        // or by the game explicitly via Frame.RefreshPreviousTransform / inline literal).
        // Default false — the engine auto-init hook sets it to true at first Add.
        public bool PreviousInitialized;

        public int TeleportTick;
    }
}
