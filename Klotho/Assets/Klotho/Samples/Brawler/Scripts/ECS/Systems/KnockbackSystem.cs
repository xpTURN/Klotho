using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Deterministic.Curve;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// Applies KnockbackComponent.Force to PhysicsBodyComponent.velocity.xz.
    /// Force decays over DurationTicks via the FPAnimationCurve damping,
    /// and the component is removed once the count reaches 0.
    /// </summary>
    public class KnockbackSystem : ISystem
    {
        // Damping curve: t=0 (start) → 1.0, t=1 (end) → 0.0 (linear)
        static readonly FPAnimationCurve DampingCurve = FPAnimationCurve.Linear();

        static readonly FP64 MsToSeconds = FP64.FromDouble(0.001);

        readonly EventSystem _events;

        public KnockbackSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var dt = FP64.FromInt(frame.DeltaTimeMs) * MsToSeconds;

            var filter = frame.Filter<PhysicsBodyComponent, KnockbackComponent>();
            while (filter.Next(out var entity))
            {
                ref var kb   = ref frame.Get<KnockbackComponent>(entity);
                ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);

                // Elapsed ratio (0=start, 1=end)
                FP64 elapsed = FP64.One
                    - FP64.FromInt(kb.DurationTicks) / FP64.FromInt(kb.InitialDurationTicks);
                FP64 multiplier = DampingCurve.Evaluate(elapsed);

                // Accumulate the damped Force into XZ velocity (FPVector2.y → world Z)
                phys.RigidBody.velocity.x = phys.RigidBody.velocity.x + kb.Force.x * multiplier * dt;
                phys.RigidBody.velocity.z = phys.RigidBody.velocity.z + kb.Force.y * multiplier * dt;

                kb.DurationTicks--;
                if (kb.DurationTicks <= 0)
                    frame.Remove<KnockbackComponent>(entity);
            }
        }
    }
}
