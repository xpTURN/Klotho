using xpTURN.Klotho.ECS;

namespace Brawler
{
    public struct ItemPickupPrototype : IEntityPrototype
    {
        public const int Id = 300;
        public void Apply(Frame frame, EntityRef entity)
        {
            frame.Add(entity, new TransformComponent());
            frame.Add(entity, new ItemComponent());
        }
    }
}
