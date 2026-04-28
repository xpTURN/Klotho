using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;

namespace xpTURN.Klotho.ECS
{
    public class EntityLifecycleSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            // Destroy entities that exceeded lifetime
            var filter = frame.Filter<EntityLifecycleTagComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var tag = ref frame.GetReadOnly<EntityLifecycleTagComponent>(entity);
                if (frame.Tick - tag.SpawnedAtTick >= tag.LifetimeTicks)
                {
                    frame.DestroyEntity(entity);
                }
            }

            // Spawn new entities based on deterministic random
            var rng = DeterministicRandom.FromSeed(
                (ulong)(frame.Tick * 7919),
                0xE17F_EC7C_1E00UL);

            int spawnCount = rng.NextInt(0, 3); // 0-2 entities per tick
            for (int i = 0; i < spawnCount; i++)
            {
                var entity = frame.CreateEntity();

                frame.Add(entity, new TransformComponent
                {
                    Position = new FPVector3(
                        rng.NextFixed(FP64.FromInt(-10), FP64.FromInt(10)),
                        FP64.Zero,
                        rng.NextFixed(FP64.FromInt(-10), FP64.FromInt(10))),
                    Scale = FPVector3.One
                });

                frame.Add(entity, new ArithmeticResultComponent());
                frame.Add(entity, new RandomStateComponent());
                frame.Add(entity, new EntityLifecycleTagComponent
                {
                    SpawnedAtTick = frame.Tick,
                    LifetimeTicks = rng.NextInt(10, 100)
                });
            }
        }
    }
}
