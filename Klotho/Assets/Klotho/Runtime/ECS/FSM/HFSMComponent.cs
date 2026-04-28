using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS.FSM
{
    [KlothoComponent(200)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe partial struct HFSMComponent : IComponent
    {
        public const int MaxDepth = 8;
        public const int MaxPendingEvents = 4;

        public int RootId;

        public fixed int ActiveStateIds[MaxDepth];
        public int ActiveDepth;

        public fixed int PendingEventIds[MaxPendingEvents];
        public int PendingEventCount;

        public int StateElapsedTicks;
    }
}
