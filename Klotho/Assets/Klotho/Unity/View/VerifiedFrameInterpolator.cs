using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Interpolates position/rotation between two adjacent Verified frames.
    /// Used as the interpolation source for entities with ViewFlags.EnableSnapshotInterpolation enabled.
    ///
    /// Four branches:
    ///   (a) Both frames valid → Lerp(ta, tb, alpha)
    ///   (b) Only baseTick valid     → ta value (corresponds to alpha=0)
    ///   (c) Only baseTick+1 valid   → tb value (corresponds to alpha=1)
    ///   (d) Neither available or entity missing → use fallback value
    ///
    /// (d) The fallback is a transient ring warmup situation, so the error visual accumulator is not reset.
    /// </summary>
    public static class VerifiedFrameInterpolator
    {
        public static Vector3 InterpolatePosition(EntityRef entity, IKlothoEngine engine, Vector3 fallbackPos)
        {
            int baseTick = engine.RenderClock.VerifiedBaseTick;
            bool hasA = engine.TryGetFrameAtTick(baseTick,     out var a);
            bool hasB = engine.TryGetFrameAtTick(baseTick + 1, out var b);

            // If the entity does not exist in the frame, treat that slot as invalid.
            if (hasA && !a.Has<TransformComponent>(entity)) hasA = false;
            if (hasB && !b.Has<TransformComponent>(entity)) hasB = false;

            if (!hasA && !hasB) return fallbackPos;                                                // (d)
            if ( hasA && !hasB) return ToVector3(a.GetReadOnly<TransformComponent>(entity).Position); // (b)
            if (!hasA &&  hasB) return ToVector3(b.GetReadOnly<TransformComponent>(entity).Position); // (c)

            ref readonly var ta = ref a.GetReadOnly<TransformComponent>(entity);
            ref readonly var tb = ref b.GetReadOnly<TransformComponent>(entity);
            return Vector3.Lerp(
                ToVector3(ta.Position),
                ToVector3(tb.Position),
                engine.RenderClock.VerifiedAlpha);   // (a)
        }

        public static Quaternion InterpolateRotation(EntityRef entity, IKlothoEngine engine, Quaternion fallbackRot)
        {
            int baseTick = engine.RenderClock.VerifiedBaseTick;
            bool hasA = engine.TryGetFrameAtTick(baseTick,     out var a);
            bool hasB = engine.TryGetFrameAtTick(baseTick + 1, out var b);

            if (hasA && !a.Has<TransformComponent>(entity)) hasA = false;
            if (hasB && !b.Has<TransformComponent>(entity)) hasB = false;

            if (!hasA && !hasB) return fallbackRot;                                                 // (d)
            if ( hasA && !hasB) return ToYawRotation(a.GetReadOnly<TransformComponent>(entity).Rotation); // (b)
            if (!hasA &&  hasB) return ToYawRotation(b.GetReadOnly<TransformComponent>(entity).Rotation); // (c)

            ref readonly var ta = ref a.GetReadOnly<TransformComponent>(entity);
            ref readonly var tb = ref b.GetReadOnly<TransformComponent>(entity);
            float yawA = ta.Rotation.ToFloat() * Mathf.Rad2Deg;
            float yawB = tb.Rotation.ToFloat() * Mathf.Rad2Deg;
            float yaw  = Mathf.LerpAngle(yawA, yawB, engine.RenderClock.VerifiedAlpha);             // (a)
            return Quaternion.Euler(0f, yaw, 0f);
        }

        private static Vector3 ToVector3(in FPVector3 v)
            => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());

        private static Quaternion ToYawRotation(FP64 yawRad)
            => Quaternion.Euler(0f, yawRad.ToFloat() * Mathf.Rad2Deg, 0f);
    }
}
