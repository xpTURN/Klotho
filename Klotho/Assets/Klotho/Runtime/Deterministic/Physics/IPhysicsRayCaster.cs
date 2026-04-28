using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Raycast interface against static colliders.
    /// </summary>
    public interface IPhysicsRayCaster
    {
        bool RayCastStatic(FPRay3 ray, FP64 maxDistance,
            out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance);
    }
}
