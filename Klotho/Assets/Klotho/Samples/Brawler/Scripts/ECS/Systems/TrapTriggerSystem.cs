using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Core;

namespace Brawler
{
    /// <summary>
    /// Subscribes to the PhysicsSystem.OnStaticTriggerEnter callback and
    /// applies knockback to characters that enter an isTrigger StaticCollider (trap).
    /// </summary>
    public class TrapTriggerSystem : ISystem
    {
        private readonly PhysicsSystem _physicsSystem;
        private readonly EventSystem _events;

        private const int TrapBasePower = 8;
        private static readonly FP64 TrapImpulse = FP64.FromInt(8);

        private (EntityRef entity, int staticColliderId)[] _enterBuffer
            = new (EntityRef, int)[32];
        private int _enterCount;

        public TrapTriggerSystem(PhysicsSystem physicsSystem, EventSystem events)
        {
            _physicsSystem = physicsSystem;
            _events = events;
            _physicsSystem.OnStaticTriggerEnter += HandleStaticTriggerEnter;
        }

        private void HandleStaticTriggerEnter(EntityRef entity, int staticColliderId)
        {
            if (_enterCount < _enterBuffer.Length)
                _enterBuffer[_enterCount++] = (entity, staticColliderId);
        }

        public void Update(ref Frame frame)
        {
            if (_enterCount == 0) return;

            _physicsSystem.GetStaticColliders(out var colliders, out var count);

            for (int i = 0; i < _enterCount; i++)
            {
                var (entity, staticColliderId) = _enterBuffer[i];

                int scIdx = FindStaticCollider(colliders, count, staticColliderId);
                if (scIdx < 0) continue;
                if (!colliders[scIdx].isTrigger) continue;

                if (!frame.Has<CharacterComponent>(entity)) continue;
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (character.IsDead) continue;

                ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
                FPVector2 charXZ = new FPVector2(transform.Position.x, transform.Position.z);

                FPVector3 trapPos = GetColliderPosition(ref colliders[scIdx].collider);
                FPVector2 trapXZ = new FPVector2(trapPos.x, trapPos.z);

                FPVector2 dir = charXZ - trapXZ;
                dir = dir.sqrMagnitude > FP64.Zero ? dir.normalized : FPVector2.Up;

                if (CombatHelper.IsShielded(ref frame, entity)) continue;

                CombatHelper.ApplyKnockback(ref frame, entity, dir, TrapBasePower);

                // Immediate impulse: set velocity directly to provide a perceptible knockback
                ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);
                phys.RigidBody.velocity.x = dir.x * TrapImpulse;
                phys.RigidBody.velocity.z = dir.y * TrapImpulse;

                var evt = EventPool.Get<TrapTriggeredEvent>();
                evt.Character = entity;
                evt.TrapPosition = trapXZ;
                _events.Enqueue(evt);
            }

            _enterCount = 0;
        }

        private static int FindStaticCollider(FPStaticCollider[] colliders, int count, int id)
        {
            for (int c = 0; c < count; c++)
            {
                if (colliders[c].id == id)
                    return c;
            }
            return -1;
        }

        private static FPVector3 GetColliderPosition(ref FPCollider col)
        {
            return col.type switch
            {
                ShapeType.Box     => col.box.position,
                ShapeType.Sphere  => col.sphere.position,
                ShapeType.Capsule => col.capsule.position,
                _                 => default
            };
        }
    }
}
