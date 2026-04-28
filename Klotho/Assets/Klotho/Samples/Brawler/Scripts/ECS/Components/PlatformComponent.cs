using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    [KlothoComponent(101)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PlatformComponent : IComponent
    {
        public bool IsMoving;
        public FPVector3 Waypoint0;
        public FPVector3 Waypoint1;
        public FPVector3 Waypoint2;
        public FPVector3 Waypoint3;
        public int WaypointIndex;    // Index of the current target waypoint being moved toward (0~3)
        public FP64 MoveSpeed;
        public FP64 MoveProgress;    // Progress of the current segment (0~1)
    }
}
