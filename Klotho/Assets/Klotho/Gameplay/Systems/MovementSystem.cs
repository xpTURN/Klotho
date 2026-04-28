using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Systems
{
    public class MovementSystem : ISystem
    {
        static readonly FP64 ArrivalThreshold = FP64.FromDouble(0.1);

        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<TransformComponent, VelocityComponent, MovementComponent>();
            while (filter.Next(out var entity))
            {
                ref var movement = ref frame.Get<MovementComponent>(entity);
                if (!movement.IsMoving)
                    continue;

                ref var transform = ref frame.Get<TransformComponent>(entity);
                ref var velocity = ref frame.Get<VelocityComponent>(entity);

                FPVector3 direction = movement.TargetPosition - transform.Position;
                FP64 distance = direction.magnitude;

                if (distance < ArrivalThreshold)
                {
                    transform.Position = movement.TargetPosition;
                    movement.IsMoving = false;
                    velocity.Velocity = FPVector3.Zero;
                    continue;
                }

                FPVector3 normalizedDir = direction.normalized;
                FP64 deltaSeconds = FP64.FromInt(frame.DeltaTimeMs) / FP64.FromInt(1000);
                FP64 moveDistance = movement.MoveSpeed * deltaSeconds;

                if (moveDistance > distance)
                    moveDistance = distance;

                velocity.Velocity = normalizedDir * movement.MoveSpeed;
                transform.Position = transform.Position + normalizedDir * moveDistance;
            }
        }
    }
}
