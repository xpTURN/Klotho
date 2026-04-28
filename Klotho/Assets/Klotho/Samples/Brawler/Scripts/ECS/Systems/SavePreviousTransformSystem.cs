using xpTURN.Klotho.ECS;

namespace Brawler
{
    public class SavePreviousTransformSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref var t = ref frame.Get<TransformComponent>(entity);
                t.PreviousPosition = t.Position;
                t.PreviousRotation = t.Rotation;
            }
        }
    }
}
