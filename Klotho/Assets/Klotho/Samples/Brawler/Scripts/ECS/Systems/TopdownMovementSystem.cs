using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    /// <summary>
    /// Every tick: apply gravity -> integrate position -> clamp on landing -> change direction.
    /// XZ velocity is set by PlatformerCommandSystem(ICommandSystem) when receiving MoveInputCommand.
    /// </summary>
    public class TopdownMovementSystem : ISystem
    {
        static readonly FP64 MsToSeconds = FP64.FromDouble(0.001);

        readonly EventSystem _events;

        public TopdownMovementSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var asset = frame.AssetRegistry.Get<MovementPhysicsAsset>(1500);
            var dt = FP64.FromInt(frame.DeltaTimeMs) * MsToSeconds;

            var filter = frame.Filter<TransformComponent, PhysicsBodyComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (character.IsDead)
                    continue;

                ref var transform = ref frame.Get<TransformComponent>(entity);
                ref var physics = ref frame.Get<PhysicsBodyComponent>(entity);
                ref var body = ref physics.RigidBody;

                var oldPos = transform.Position;

                // 1. Gravity / slope projection
                if (character.IsJumping || !character.IsGrounded)
                {
                    // Jumping or airborne: apply gravity
                    body.velocity.y = body.velocity.y - asset.GravityAccel * dt;
                }
                else if (character.GroundNormal.y < FP64.One)
                {
                    // Slope: project horizontal velocity along slope direction (preserve speed)
                    FP64 hSqr = body.velocity.x * body.velocity.x + body.velocity.z * body.velocity.z;
                    if (hSqr > asset.MinMoveSqr)
                    {
                        FPVector3 hVel = new FPVector3(body.velocity.x, FP64.Zero, body.velocity.z);
                        FPVector3 n = character.GroundNormal;
                        FPVector3 projected = hVel - n * FPVector3.Dot(hVel, n);
                        FP64 projSqr = projected.sqrMagnitude;
                        if (projSqr > FP64.Epsilon)
                        {
                            FP64 scale = FP64.Sqrt(hSqr / projSqr);
                            body.velocity = projected * scale;
                        }
                    }
                }

                // 2. Rotate toward XZ movement direction (preserve rotation while knockback or action lock is active)
                if (!frame.Has<KnockbackComponent>(entity) && character.ActionLockTicks <= 0)
                {
                    FP64 vx = body.velocity.x;
                    FP64 vz = body.velocity.z;
                    if (vx * vx + vz * vz > asset.MinMoveSqr)
                        transform.Rotation = FP64.Atan2(vx, vz);
                }
            }
        }
    }
}
