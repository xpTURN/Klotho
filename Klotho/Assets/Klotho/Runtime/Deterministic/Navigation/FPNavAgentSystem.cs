using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Per-tick agent update system.
    /// Handles path requests, steering, movement, and NavMesh constraints.
    /// </summary>
    public class FPNavAgentSystem
    {
        private readonly FPNavMesh _navMesh;
        private readonly FPNavMeshQuery _query;
        private readonly FPNavMeshPathfinder _pathfinder;
        private readonly FPNavMeshFunnel _funnel;
        private readonly ILogger _logger;

        private FPNavAvoidance _avoidance;

        private const int VISITED_BUFFER_SIZE = 48;
        private readonly int[] _visitedBuffer = new int[VISITED_BUFFER_SIZE];
        private readonly int[] _corridorBuffer = new int[NavAgentComponent.MAX_CORRIDOR];

        /// <summary>
        /// Distance threshold for waypoint arrival detection.
        /// </summary>
        public FP64 WaypointThreshold;

        /// <summary>
        /// Y difference threshold between floors. Triangles differing more than this are considered different floors.
        /// </summary>
        public FP64 MultiFloorYThreshold = FP64.FromDouble(2.0);

        /// <summary>
        /// Consecutive off-corridor tick threshold. Triggers repath when exceeded continuously.
        /// </summary>
        public int OffCorridorRepathThreshold = 10;

        /// <summary>
        /// Default areaMask value (all areas allowed).
        /// </summary>
        public const int DEFAULT_AREA_MASK = ~0;

        public FPNavAgentSystem(FPNavMesh navMesh, FPNavMeshQuery query,
            FPNavMeshPathfinder pathfinder, FPNavMeshFunnel funnel, ILogger logger)
        {
            _navMesh = navMesh;
            _query = query;
            _pathfinder = pathfinder;
            _funnel = funnel;
            _logger = logger;

            WaypointThreshold = FP64.FromDouble(0.3);
            _avoidance = null;
        }

        /// <summary>
        /// Sets the ORCA avoidance system. Pass null to disable avoidance.
        /// </summary>
        public void SetAvoidance(FPNavAvoidance avoidance)
        {
            _avoidance = avoidance;
        }

        /// <summary>
        /// Constrains a position to the NavMesh (uses MoveAlongSurface internally).
        /// </summary>
        public FPVector3 ConstrainToNavMesh(FPVector3 newPos, FPVector3 oldPos, int currentTri)
        {
            var (resultPos, _) = _query.MoveAlongSurface(oldPos, newPos, currentTri, MultiFloorYThreshold);
            return resultPos;
        }

        /// <summary>
        /// Updates all agents by one tick based on NavAgentComponent data.
        /// </summary>
        public unsafe void Update(ref Frame frame, EntityRef[] entities, int entityCount, int currentTick, FP64 dt)
        {
            for (int i = 0; i < entityCount; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                ProcessPathRequest(ref nav, currentTick);
                ProcessSteering(ref nav);
            }

            if (_avoidance != null)
            {
                for (int i = 0; i < entityCount; i++)
                {
                    ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                    if (nav.Status == (byte)FPNavAgentStatus.Moving)
                    {
                        nav.DesiredVelocity = _avoidance.ComputeNewVelocity(
                            i, ref frame, entities, entityCount, dt);
                    }
                }
            }

            for (int i = 0; i < entityCount; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                ProcessMovement(ref nav, dt);
            }
        }

        private unsafe void ProcessPathRequest(ref NavAgentComponent nav, int currentTick)
        {
            if (!nav.HasNavDestination || nav.HasPath)
                return;

            if (nav.Status != (byte)FPNavAgentStatus.PathPending)
                return;

            {
                FP64 distToTarget = FPVector2.Distance(nav.Position.ToXZ(), nav.Destination.ToXZ());
                FP64 yDistToTarget = FP64.Abs(nav.Position.y - nav.Destination.y);
                if (distToTarget < WaypointThreshold && yDistToTarget < MultiFloorYThreshold)
                {
                    nav.Status = (byte)FPNavAgentStatus.Arrived;
                    nav.Velocity = FPVector2.Zero;
                    nav.DesiredVelocity = FPVector2.Zero;
                    return;
                }
            }

            FP64 ticksSinceLast = FP64.FromInt(currentTick - nav.LastRepathTick);
            if (ticksSinceLast < nav.PathRepathCooldown && nav.LastRepathTick > 0)
                return;

            nav.LastRepathTick = currentTick;

            bool found = _pathfinder.FindPath(nav.Position, nav.Destination, DEFAULT_AREA_MASK,
                out int[] corridor, out int corridorLength);
            if (found)
            {
                fixed (int* dst = nav.Corridor)
                {
                    NavCorridorHelper.SetCorridor(dst, ref nav.CorridorLength,
                        NavAgentComponent.MAX_CORRIDOR, corridor, corridorLength);
                }
                nav.PathTarget = nav.Destination;
                nav.PathId = nav.PathRequestId;
                nav.PathIsValid = true;
                nav.HasPath = true;
                nav.Status = (byte)FPNavAgentStatus.Moving;
            }
            else
            {
                nav.Status = (byte)FPNavAgentStatus.PathFailed;
            }
        }

        private unsafe void ProcessSteering(ref NavAgentComponent nav)
        {
            if (nav.Status != (byte)FPNavAgentStatus.Moving)
                return;

            if (!nav.PathIsValid || nav.CorridorLength <= 0)
            {
                nav.Status = (byte)FPNavAgentStatus.Arrived;
                nav.Velocity = FPVector2.Zero;
                nav.DesiredVelocity = FPVector2.Zero;
                return;
            }

            fixed (int* src = nav.Corridor)
            {
                for (int k = 0; k < nav.CorridorLength; k++)
                    _corridorBuffer[k] = src[k];
            }

            int cornerCount = _funnel.FindCorners(_corridorBuffer, nav.CorridorLength,
                nav.Position, nav.PathTarget, 4);
            FPVector3[] corners = _funnel.Corners;

            if (cornerCount == 0)
            {
                if (nav.CorridorLength > 1)
                {
                    nav.HasPath = false;
                    nav.Status = (byte)FPNavAgentStatus.PathPending;
                    return;
                }
                nav.Status = (byte)FPNavAgentStatus.Arrived;
                nav.Velocity = FPVector2.Zero;
                nav.DesiredVelocity = FPVector2.Zero;
                return;
            }

            FPVector3 nextCorner = corners[0];
            FPVector2 posXZ = nav.Position.ToXZ();
            FPVector2 cornerXZ = nextCorner.ToXZ();

            FPVector2 direction = (cornerXZ - posXZ).normalized;
            nav.DesiredVelocity = direction * nav.Speed;

            FPVector2 targetXZ = nav.PathTarget.ToXZ();
            FP64 distToTarget = FPVector2.Distance(posXZ, targetXZ);

            if (nav.Acceleration > FP64.Zero)
            {
                FP64 brakingRadius = nav.Speed * nav.Speed / (nav.Acceleration * FP64.FromInt(2));
                if (distToTarget < brakingRadius)
                {
                    nav.DesiredVelocity = nav.DesiredVelocity * distToTarget / brakingRadius;
                }
            }

            if (nav.StoppingDistance > FP64.Zero)
            {
                FP64 yDist = FP64.Abs(nav.Position.y - nav.PathTarget.y);
                if (yDist < MultiFloorYThreshold)
                {
                    if (distToTarget < nav.StoppingDistance)
                    {
                        nav.DesiredVelocity = nav.DesiredVelocity * distToTarget / nav.StoppingDistance;
                    }
                }
            }
        }

        private unsafe void ProcessMovement(ref NavAgentComponent nav, FP64 dt)
        {
            if (nav.Status != (byte)FPNavAgentStatus.Moving)
            {
                nav.Velocity = FPVector2.Zero;
                nav.CurrentSpeed = FP64.Zero;
                return;
            }

            FPVector2 diff = nav.DesiredVelocity - nav.Velocity;
            FP64 maxAccelStep = nav.Acceleration * dt;
            FP64 diffSqrMag = diff.sqrMagnitude;

            if (diffSqrMag > maxAccelStep * maxAccelStep)
            {
                diff = diff.normalized * maxAccelStep;
            }

            nav.Velocity = nav.Velocity + diff;

            FP64 velSqrMag = nav.Velocity.sqrMagnitude;
            if (velSqrMag > nav.Speed * nav.Speed)
            {
                nav.Velocity = nav.Velocity.normalized * nav.Speed;
            }

            nav.CurrentSpeed = nav.Velocity.magnitude;

            FPVector3 displacement = new FPVector3(
                nav.Velocity.x * dt,
                FP64.Zero,
                nav.Velocity.y * dt);
            FPVector3 newPos = nav.Position + displacement;

            var (resultPos, resultTri) = _query.MoveAlongSurfaceWithVisited(
                nav.Position, newPos, nav.CurrentTriangleIndex, MultiFloorYThreshold,
                _visitedBuffer, out int visitedCount);

            nav.CurrentTriangleIndex = resultTri;
            nav.Position = resultPos;

            if (nav.PathIsValid && nav.CorridorLength > 0)
            {
                int advanceIdx = -1;
                fixed (int* p = nav.Corridor)
                {
                    for (int i = 0; i < nav.CorridorLength; i++)
                    {
                        if (p[i] == resultTri)
                        {
                            advanceIdx = i;
                            break;
                        }
                    }

                    if (advanceIdx > 0)
                    {
                        int newLen = nav.CorridorLength - advanceIdx;
                        for (int i = 0; i < newLen; i++)
                            p[i] = p[i + advanceIdx];
                        nav.CorridorLength = newLen;
                        nav.OffCorridorTicks = 0;

                        if (newLen == 1)
                        {
                            FP64 currentSpeed = nav.Velocity.magnitude;
                            if (currentSpeed > FP64.Zero)
                            {
                                FPVector2 desiredDir = nav.DesiredVelocity.normalized;
                                if (desiredDir.sqrMagnitude > FP64.Zero)
                                {
                                    nav.Velocity = desiredDir * currentSpeed;
                                }
                            }
                        }
                    }
                    else if (advanceIdx == 0)
                    {
                        nav.OffCorridorTicks = 0;
                    }
                    else
                    {
                        nav.OffCorridorTicks++;
                        if (nav.OffCorridorTicks >= OffCorridorRepathThreshold)
                        {
                            nav.HasPath = false;
                            nav.Status = (byte)FPNavAgentStatus.PathPending;
                            nav.OffCorridorTicks = 0;
                            return;
                        }
                    }
                }
            }

            {
                FP64 distToTarget = FPVector2.Distance(
                    nav.Position.ToXZ(), nav.PathTarget.ToXZ());
                FP64 yDistToTarget = FP64.Abs(nav.Position.y - nav.PathTarget.y);
                if (distToTarget < WaypointThreshold && yDistToTarget < MultiFloorYThreshold)
                {
                    nav.Status = (byte)FPNavAgentStatus.Arrived;
                    nav.Velocity = FPVector2.Zero;
                    nav.DesiredVelocity = FPVector2.Zero;
                }
            }
        }
    }
}
