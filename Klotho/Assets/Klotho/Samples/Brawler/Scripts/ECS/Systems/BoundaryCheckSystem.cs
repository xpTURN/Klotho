using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// Each tick, checks whether the character's XZ position is outside the stage boundary (FPBounds2).
    /// On out-of-bounds detection:
    ///   1. StockCount decrement by 1, KnockbackPower reset
    ///   2. IsDead = true, RespawnTimer set
    ///   3. Enqueue CharacterKilledEvent
    /// </summary>
    public class BoundaryCheckSystem : ISystem
    {
        readonly EventSystem _events;

        public BoundaryCheckSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);

            FP64 half = rules.StageBoundsSize / FP64.FromInt(2);
            var stageBounds = new FPBounds2(
                center: FPVector2.Zero,
                size: new FPVector2(rules.StageBoundsSize, rules.StageBoundsSize)
            );

            var filter = frame.Filter<CharacterComponent, TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref var character = ref frame.Get<CharacterComponent>(entity);
                if (character.IsDead) continue;

                ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
                FPVector2 xzPos = new FPVector2(transform.Position.x, transform.Position.z);

                if (stageBounds.Contains(xzPos) && transform.Position.y > rules.FallDeathY) continue;

                // Out of boundary → death handling
                character.StockCount--;
                character.KnockbackPower = 0;
                character.IsDead = true;
                character.RespawnTimer = character.StockCount > 0 ? rules.RespawnTicks : 0;

                // Stop physics updates
                if (frame.Has<PhysicsBodyComponent>(entity))
                {
                    ref var physics = ref frame.Get<PhysicsBodyComponent>(entity);
                    physics.RigidBody.velocity = FPVector3.Zero;
                    physics.RigidBody.isStatic = true;
                }

                var killEvt = EventPool.Get<CharacterKilledEvent>();
                killEvt.Character = entity;
                killEvt.PlayerId = character.PlayerId;
                killEvt.StockRemaining = character.StockCount;
                _events.Enqueue(killEvt);
            }
        }
    }
}
