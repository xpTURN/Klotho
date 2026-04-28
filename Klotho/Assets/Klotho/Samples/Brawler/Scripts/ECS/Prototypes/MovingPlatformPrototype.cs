using xpTURN.Klotho.ECS;

namespace Brawler
{
    public struct MovingPlatformPrototype : IEntityPrototype
    {
        public const int Id = 200;
        public void Apply(Frame frame, EntityRef entity)
        {
            frame.Add(entity, new TransformComponent());
            frame.Add(entity, new PhysicsBodyComponent());
            frame.Add(entity, new PlatformComponent());
        }
    }
}
