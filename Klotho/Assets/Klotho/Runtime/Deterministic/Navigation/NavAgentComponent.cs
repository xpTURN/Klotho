#pragma warning disable KLSG_ECS004
using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    public enum FPNavAgentStatus
    {
        Idle,
        PathPending,
        Moving,
        Arrived,
        PathFailed,
    }

    [KlothoComponent(11)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe partial struct NavAgentComponent : IComponent
    {
        public const int MAX_CORRIDOR = 128;

        // ── Settings ──
        public FP64 Speed;
        public FP64 Acceleration;
        public FP64 AngularSpeed;
        public FP64 Radius;
        public FP64 StoppingDistance;
        public FP64 PathRepathCooldown;

        // ── Runtime state ──
        public FPVector3 Position;
        public FPVector2 Velocity;
        public FPVector2 DesiredVelocity;
        public FP64 CurrentSpeed;

        // ── Path (corridor) ──
        public fixed int Corridor[MAX_CORRIDOR];
        public int CorridorLength;
        public FPVector3 PathTarget;
        public int PathId;
        public bool PathIsValid;

        // ── Destination / triangle ──
        public FPVector3 Destination;
        public bool HasNavDestination;
        public bool HasPath;
        public int CurrentTriangleIndex;

        // ── Internal counters ──
        public int LastRepathTick;
        public int PathRequestId;
        public int OffCorridorTicks;
        public byte Status; // FPNavAgentStatus

        public static void Init(ref NavAgentComponent nav, FPVector3 startPosition)
        {
            nav.Speed = FP64.FromInt(5);
            nav.Acceleration = FP64.FromInt(10);
            nav.AngularSpeed = FP64.FromInt(360);
            nav.Radius = FP64.Half;
            nav.StoppingDistance = FP64.FromDouble(0.1);
            nav.PathRepathCooldown = FP64.FromInt(10);

            nav.Position = startPosition;
            nav.Velocity = FPVector2.Zero;
            nav.DesiredVelocity = FPVector2.Zero;
            nav.CurrentSpeed = FP64.Zero;

            nav.CorridorLength = 0;
            nav.PathTarget = FPVector3.Zero;
            nav.PathId = 0;
            nav.PathIsValid = false;

            nav.Destination = FPVector3.Zero;
            nav.HasNavDestination = false;
            nav.HasPath = false;
            nav.CurrentTriangleIndex = -1;

            nav.LastRepathTick = 0;
            nav.PathRequestId = 0;
            nav.OffCorridorTicks = 0;
            nav.Status = (byte)FPNavAgentStatus.Idle;
        }

        public static void SetDestination(ref NavAgentComponent nav, FPVector3 dest)
        {
            nav.Destination = dest;
            nav.HasNavDestination = true;
            nav.HasPath = false;
            nav.CorridorLength = 0;
            nav.PathIsValid = false;
            nav.Status = (byte)FPNavAgentStatus.PathPending;
            nav.PathRequestId++;
            nav.OffCorridorTicks = 0;
        }

        public static void Stop(ref NavAgentComponent nav)
        {
            nav.HasNavDestination = false;
            nav.HasPath = false;
            nav.CorridorLength = 0;
            nav.PathIsValid = false;
            nav.Velocity = FPVector2.Zero;
            nav.DesiredVelocity = FPVector2.Zero;
            nav.CurrentSpeed = FP64.Zero;
            nav.Status = (byte)FPNavAgentStatus.Idle;
            nav.OffCorridorTicks = 0;
        }
    }
}
