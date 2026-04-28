using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// Periodically spawns items using DeterministicRandom and removes
    /// item entities whose RemainingTicks has reached 0.
    ///
    /// Determinism guarantees:
    ///   - The RNG is not stored on a system field; instead it is derived from frame.Tick on each call.
    ///   - DeterministicRandom.FromSeed(worldSeed, featureKey, tick) produces the same value on rollback.
    ///   - worldSeed is read from the per-frame GameSeedComponent singleton, so it is rollback-safe.
    /// </summary>
    public class ItemSpawnSystem : ISystem
    {
        // RNG feature key (arbitrary constant — used to separate the item-spawn stream)
        const ulong FeatureKey = 0x4954454D53504157UL; // "ITEMSPAW"

        readonly EventSystem _events;

        public ItemSpawnSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            TickItemLifetimes(ref frame);
            TrySpawnItem(ref frame);
        }

        // ────────────────────────────────────────────
        // Decrement item lifetime + remove on expiry
        // ────────────────────────────────────────────
        void TickItemLifetimes(ref Frame frame)
        {
            var filter = frame.Filter<ItemComponent, TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref var item = ref frame.Get<ItemComponent>(entity);
                item.RemainingTicks--;
                if (item.RemainingTicks <= 0)
                    frame.DestroyEntity(entity);
            }
        }

        // ────────────────────────────────────────────
        // Spawn an item when the period is reached
        // ────────────────────────────────────────────
        void TrySpawnItem(ref Frame frame)
        {
            var config = frame.AssetRegistry.Get<ItemConfigAsset>(1400);

            if (frame.Tick % config.SpawnIntervalTicks != 0) return;

            // Read the seed from the frame (skip if unset)
            var seedFilter = frame.Filter<GameSeedComponent>();
            if (!seedFilter.Next(out var seedEntity)) return;
            ref readonly var seedComp = ref frame.GetReadOnly<GameSeedComponent>(seedEntity);
            if (seedComp.WorldSeed == 0) return;

            // Check current item count
            int count = 0;
            var countFilter = frame.Filter<ItemComponent>();
            while (countFilter.Next(out _)) count++;
            if (count >= config.MaxItems) return;

            // Tick-based RNG — guarantees the same value across rollback re-execution
            var rng = DeterministicRandom.FromSeed(seedComp.WorldSeed, FeatureKey, (ulong)frame.Tick);

            int itemType = rng.NextIntInclusive(0, 2); // 0=Shield, 1=Boost, 2=Bomb

            FP64 px = rng.NextFixed(config.SpawnMinRange, config.SpawnMaxRange);
            if (rng.NextBool()) px = -px;
            FP64 pz = rng.NextFixed(config.SpawnMinRange, config.SpawnMaxRange);
            if (rng.NextBool()) pz = -pz;

            var entity = frame.CreateEntity(ItemPickupPrototype.Id);

            ref var item = ref frame.Get<ItemComponent>(entity);
            item.ItemType       = itemType;
            item.RemainingTicks = config.ItemLifetimeTicks;
            item.EntityId = entity.ToId();

            ref var transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(px, FP64.FromDouble(0.5), pz);
        }
    }
}
