using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    public class ObstacleMovementSystem : ISystem
    {
        static readonly FP64 MsToSeconds = FP64.FromDouble(0.001);
        static readonly FP64 RideThreshold = FP64.FromDouble(0.15);

        readonly EventSystem _events;

        public ObstacleMovementSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var deltaSeconds = FP64.FromInt(frame.DeltaTimeMs) * MsToSeconds;

            var filter = frame.Filter<TransformComponent, PhysicsBodyComponent, PlatformComponent>();
            while (filter.Next(out var entity))
            {
                ref var platform  = ref frame.Get<PlatformComponent>(entity);
                ref var transform = ref frame.Get<TransformComponent>(entity);
                ref var physBody  = ref frame.Get<PhysicsBodyComponent>(entity);

                if (!platform.IsMoving)
                    continue;

                FPVector3 oldPos = transform.Position;

                // Advance MoveProgress (0→1, on segment completion cycle to the next waypoint)
                platform.MoveProgress += platform.MoveSpeed * deltaSeconds;

                if (platform.MoveProgress >= FP64.One)
                {
                    platform.MoveProgress -= FP64.One;
                    platform.WaypointIndex = (platform.WaypointIndex + 1) & 3; // cycle 0~3
                }

                FPVector3 from = platform.WaypointIndex switch
                {
                    0 => platform.Waypoint0,
                    1 => platform.Waypoint1,
                    2 => platform.Waypoint2,
                    _ => platform.Waypoint3,
                };
                int nextIdx = (platform.WaypointIndex + 1) & 3;
                FPVector3 to = nextIdx switch
                {
                    0 => platform.Waypoint0,
                    1 => platform.Waypoint1,
                    2 => platform.Waypoint2,
                    _ => platform.Waypoint3,
                };

                // Update XZ position (keep Y)
                transform.Position = FPVector3.Lerp(from, to, platform.MoveProgress);

                FPVector3 delta = transform.Position - oldPos;

                // Set kinematic velocity
                FPVector3 xzVel = (to - from) * platform.MoveSpeed;
                physBody.RigidBody.velocity = xzVel;

                // Apply deltaPosition to characters standing on the platform
                if (delta.x == FP64.Zero && delta.z == FP64.Zero)
                    continue;

                FPVector3 platHalf = physBody.Collider.box.halfExtents;
                FP64 platTop = transform.Position.y + platHalf.y;

                var charFilter = frame.Filter<TransformComponent, PhysicsBodyComponent, CharacterComponent>();
                while (charFilter.Next(out var charEntity))
                {
                    ref readonly var charComp = ref frame.GetReadOnly<CharacterComponent>(charEntity);
                    if (charComp.IsDead) continue;

                    ref var charTransform = ref frame.Get<TransformComponent>(charEntity);
                    ref readonly var charPhys = ref frame.GetReadOnly<PhysicsBodyComponent>(charEntity);

                    // Character bottom Y
                    FP64 charBottom = charTransform.Position.y;

                    // Whether the character's bottom is near the platform top
                    FP64 yDiff = charBottom - platTop;
                    if (yDiff < -RideThreshold || yDiff > RideThreshold)
                        continue;

                    // XZ AABB overlap check
                    FP64 charRadius = charPhys.Collider.type == ShapeType.Capsule
                        ? charPhys.Collider.capsule.radius
                        : charPhys.Collider.sphere.radius;

                    FP64 cx = charTransform.Position.x;
                    FP64 cz = charTransform.Position.z;
                    FP64 px = transform.Position.x;
                    FP64 pz = transform.Position.z;

                    if (cx + charRadius < px - platHalf.x || cx - charRadius > px + platHalf.x)
                        continue;
                    if (cz + charRadius < pz - platHalf.z || cz - charRadius > pz + platHalf.z)
                        continue;

                    // Riding — apply deltaPosition
                    charTransform.Position = new FPVector3(
                        charTransform.Position.x + delta.x,
                        charTransform.Position.y,
                        charTransform.Position.z + delta.z);
                }
            }
        }
    }
}
