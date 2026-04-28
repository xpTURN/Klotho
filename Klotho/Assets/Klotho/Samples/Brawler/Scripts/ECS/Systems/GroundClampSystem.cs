using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;

namespace Brawler
{
    public class GroundClampSystem : ISystem
    {
        private readonly IPhysicsRayCaster _rayCaster;

        public GroundClampSystem(IPhysicsRayCaster rayCaster)
        {
            _rayCaster = rayCaster;
        }

        public void Update(ref Frame frame)
        {
            var asset = frame.AssetRegistry.Get<MovementPhysicsAsset>(1500);

            var filter = frame.Filter<TransformComponent, PhysicsBodyComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref var character = ref frame.Get<CharacterComponent>(entity);
                if (character.IsDead)
                    continue;

                ref var transform = ref frame.Get<TransformComponent>(entity);
                ref var physics = ref frame.Get<PhysicsBodyComponent>(entity);

                if (physics.Collider.type != ShapeType.Capsule)
                    continue;

                FP64 halfH = physics.Collider.capsule.halfHeight;
                FP64 r = physics.Collider.capsule.radius;

                // Skip clamp while jumping upward
                if (character.IsJumping && physics.RigidBody.velocity.y > FP64.Zero)
                {
                    character.IsGrounded = false;
                    continue;
                }

                FPVector3 capsuleCenter = transform.Position + physics.ColliderOffset;
                FPVector3 rayOrigin = capsuleCenter - FPVector3.Up * (halfH + r) + FPVector3.Up * asset.SkinOffset;
                FPRay3 downRay = new FPRay3(rayOrigin, -FPVector3.Up);

                if (!_rayCaster.RayCastStatic(downRay, asset.MaxFallProbe, out var groundPt, out var groundNormal, out _))
                {
                    character.IsGrounded = false;
                    character.GroundNormal = FPVector3.Up;
                    continue;
                }

                FP64 groundY = groundPt.y + halfH + r - physics.ColliderOffset.y;

                if (transform.Position.y < groundY)
                {
                    // Below ground → clamp
                    transform.Position.y = groundY;
                    physics.RigidBody.velocity.y = FP64.Zero;
                    character.IsGrounded = true;
                    character.IsJumping = false;
                    character.GroundNormal = groundNormal;
                }
                else
                {
                    FP64 surfaceDist = transform.Position.y - groundY;

                    if (character.IsGrounded && surfaceDist < asset.GroundSnapDepth)
                    {
                        // Close above ground (slightly floating after slope projection) → snap
                        transform.Position.y = groundY;
                        physics.RigidBody.velocity.y = FP64.Zero;
                        character.IsGrounded = true;
                        character.IsJumping = false;
                        character.GroundNormal = groundNormal;
                    }
                    else
                    {
                        character.IsGrounded = surfaceDist < asset.GroundEnterDepth;
                        if (character.IsGrounded)
                            physics.RigidBody.velocity.y = FP64.Zero;

                        character.IsJumping = character.IsJumping && !character.IsGrounded;
                        character.GroundNormal = character.IsGrounded ? groundNormal : FPVector3.Up;
                    }
                }
            }
        }
    }
}
