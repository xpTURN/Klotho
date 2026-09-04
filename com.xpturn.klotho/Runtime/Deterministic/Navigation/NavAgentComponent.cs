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

        /// <summary>
        /// The agent has a valid corridor but cannot enter the next triangle in it, because that
        /// triangle's area is excluded by this agent's walk mask. It has stopped where it touched.
        ///
        /// <para>Distinct from <see cref="PathFailed"/> on purpose: that one means "no route
        /// exists", this one means "a route exists and you are not allowed through it". A game that
        /// plans permissively and walks restrictively (the reason per-agent masks exist) sees only
        /// this one, and the two call for opposite responses — repath vs. change what the agent
        /// believes, or attack what is in the way.</para>
        ///
        /// <para><b>Without this value the stall is invisible.</b> An agent pressed against a
        /// forbidden edge keeps <c>Moving</c> and a valid path, and the off-corridor repath never
        /// fires because it is still standing inside its corridor's current triangle — so nothing
        /// in its state distinguishes it from an agent that is walking. Clearing the block is the
        /// game's call: widen the mask through <see cref="NavAgentComponent.SetAreaMask"/>, or give
        /// it somewhere else to go.</para>
        ///
        /// <para><b>It is terminal, with one engine-side exception.</b> While it is set, both
        /// ProcessSteering and ProcessMovement return on <c>Status != Moving</c>, so no engine path
        /// writes the status again — the game's call above is the way out. The exception is the one
        /// event that can make the block untrue by itself: a navmesh swap. <c>ReseedAgents</c> hands
        /// a Blocked agent that still has a destination back to the planner, so a demolished
        /// building releases the units it had stopped.</para>
        /// </summary>
        Blocked,
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

        // ── Area masks (per-agent, OVERRIDES — 0 means "use FPNavAgentSystem.DEFAULT_AREA_MASK") ──
        //
        // Two, not one, and they are asymmetric on purpose: the plan mask decides what the agent
        // can ROUTE through, the walk mask what it can ENTER. Setting the plan mask permissively
        // and the walk mask restrictively is what produces "the path is drawn straight through the
        // building, and the unit walks into it and stops" — the agent plans as if it did not know
        // the building was there. That is the combination the two fields exist for; a single mask
        // can only make the route and the entry agree.
        //
        // A ZERO field is not a mask. Zero as a mask would be total paralysis (both enforcement
        // points ask "do we share a bit?", and nothing shares a bit with 0), and zero is what a
        // `default(NavAgentComponent)` carries — storage growth, deserialisation, and the gap
        // between `frame.Add(default)` and `Init` all produce it. So zero is read as "no override",
        // which makes an untouched agent behave exactly as it did before these fields existed.
        // Assign through SetAreaMask rather than directly; the setter is what drops the corridor
        // that the OLD mask planned.
        public int PlanAreaMaskOverride;
        public int WalkAreaMaskOverride;

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
            nav.PlanAreaMaskOverride = 0;   // 0 = no override, i.e. DEFAULT_AREA_MASK
            nav.WalkAreaMaskOverride = 0;
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

        /// <summary>
        /// Gives the agent new area masks and drops what the old ones planned.
        ///
        /// <para>Pass 0 for either mask to mean "no override" — that agent then uses
        /// <c>FPNavAgentSystem.DEFAULT_AREA_MASK</c> for that half, which is the behaviour it had
        /// before these fields existed.</para>
        ///
        /// <para><b>Why this is not just two assignments.</b> The corridor the agent is holding was
        /// planned under the OLD masks. Changing a mask moves no geometry, so nothing else
        /// invalidates it: no rebake, no swap, no reseed. The agent would keep following a corridor
        /// whose triangles its new walk mask refuses, get stopped at the first forbidden edge, and
        /// stay there — the off-corridor repath cannot rescue it, because an agent standing still
        /// inside its corridor's current triangle is "on the corridor" and resets that counter. So
        /// the corridor has to go here.</para>
        ///
        /// <para>The <c>LastRepathTick</c> write is the part that is easy to miss: without it the
        /// next plan waits out <c>PathRepathCooldown</c>, which is the same delay a game hits when
        /// it tries to fix this with <c>SetDestination</c> (that method deliberately does not touch
        /// the cooldown). And setting <c>Status</c> here is what releases an agent parked in
        /// <see cref="FPNavAgentStatus.Blocked"/> — widening a mask would otherwise leave it stopped
        /// forever, recreating the exact symptom the Blocked state exists to end.</para>
        ///
        /// <para>That status write is conditional on the agent having somewhere to go, and the
        /// reason is the spawn order: <c>Init</c> then <c>SetAreaMask</c>, before any destination.
        /// <c>PathPending</c> on a destination-less agent is a status nothing can advance — the
        /// planner returns on <c>!HasNavDestination</c> — so it would sit there, reported as
        /// planning, until the first <c>SetDestination</c>. Such an agent gets
        /// <see cref="FPNavAgentStatus.Idle"/>, which is what <c>Init</c> sets and what it is. An
        /// agent cannot be Blocked or Arrived without a destination (both presuppose a corridor
        /// planned from one, and <c>Stop</c> clears the pair), so nothing that needs releasing ever
        /// takes this branch.</para>
        /// </summary>
        public static void SetAreaMask(ref NavAgentComponent nav, int planMask, int walkMask)
        {
            nav.PlanAreaMaskOverride = planMask;
            nav.WalkAreaMaskOverride = walkMask;

            nav.CorridorLength = 0;
            nav.PathIsValid = false;
            nav.HasPath = false;
            nav.Status = nav.HasNavDestination
                ? (byte)FPNavAgentStatus.PathPending
                : (byte)FPNavAgentStatus.Idle;
            nav.PathRequestId++;
            nav.OffCorridorTicks = 0;
            nav.LastRepathTick = 0;
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
