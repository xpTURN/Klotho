using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;

namespace xpTURN.Klotho.ECS
{
    public class RandomStressSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<RandomStateComponent>();
            while (filter.Next(out var entity))
            {
                ref var state = ref frame.Get<RandomStateComponent>(entity);

                // Deterministic per entity per tick
                var rng = DeterministicRandom.FromSeed(
                    (ulong)(frame.Tick * 31 + entity.Index),
                    0xDA7A_5EED_0000UL);

                state.Value = rng.NextFixed();
                state.Direction = rng.NextDirection3D();

                // Conditional branching based on random (control flow determinism)
                int branch = rng.NextInt(0, 4);
                state.BranchResult = branch switch
                {
                    0 => rng.NextInt(0, 100),
                    1 => rng.NextInt(100, 200),
                    2 => rng.NextInt(200, 300),
                    _ => rng.NextInt(300, 400),
                };
            }
        }
    }
}
