using Microsoft.Extensions.Logging;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.ECS.FSM
{
    public ref struct AIContext
    {
        public Frame Frame;
        public EntityRef Entity;
        public FPNavMeshQuery NavQuery;
        public ICommandSystem CommandSystem;
        public IPhysicsRayCaster RayCaster;
        public ILogger Logger;
    }
}
