using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;

namespace Brawler
{
    public struct KnightPrototype : IEntityPrototype
    {
        public const int Id = 103;
        public void Apply(Frame frame, EntityRef entity)
        {
            var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1103);
            frame.Add(entity, new TransformComponent());
            var rb = FPRigidBody.CreateDynamic(stats.Mass);
            rb.friction = stats.Friction;
            frame.Add(entity, new PhysicsBodyComponent
            {
                RigidBody = rb,
                Collider = FPCollider.FromCapsule(new FPCapsuleShape(stats.ColliderHalfHeight, stats.ColliderRadius, FPVector3.Zero)),
                ColliderOffset = new FPVector3(FP64.Zero, stats.ColliderOffsetY, FP64.Zero),
            });
            frame.Add(entity, new CharacterComponent { CharacterClass = 3 });
            frame.Add(entity, new SkillCooldownComponent());
            frame.Add(entity, new OwnerComponent());
        }
    }
}
