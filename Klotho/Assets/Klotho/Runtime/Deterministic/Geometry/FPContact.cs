using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point collision contact point. Contains contact position, normal, penetration depth, and the related entity pair.
    /// </summary>
    [Serializable]
    public struct FPContact : IEquatable<FPContact>
    {
        public FPVector3 point;
        public FPVector3 normal;
        public FP64 depth;
        public int entityA;
        public int entityB;
        public bool isSpeculative;

        public FPContact(FPVector3 point, FPVector3 normal, FP64 depth, int entityA, int entityB)
        {
            this.point = point;
            this.normal = normal;
            this.depth = depth;
            this.entityA = entityA;
            this.entityB = entityB;
            this.isSpeculative = false;
        }

        public FPContact Flipped()
        {
            return new FPContact(point, -normal, depth, entityB, entityA) { isSpeculative = isSpeculative };
        }

        public bool Equals(FPContact other)
        {
            return point == other.point
                && normal == other.normal
                && depth == other.depth
                && entityA == other.entityA
                && entityB == other.entityB
                && isSpeculative == other.isSpeculative;
        }

        public override bool Equals(object obj) => obj is FPContact other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = point.GetHashCode();
                hash = hash * 397 ^ normal.GetHashCode();
                hash = hash * 397 ^ depth.GetHashCode();
                hash = hash * 397 ^ entityA;
                hash = hash * 397 ^ entityB;
                hash = hash * 397 ^ isSpeculative.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(FPContact a, FPContact b) => a.Equals(b);
        public static bool operator !=(FPContact a, FPContact b) => !a.Equals(b);

        public override string ToString()
        {
            return $"FPContact(point:{point}, normal:{normal}, depth:{depth}, A:{entityA}, B:{entityB}, spec:{isSpeculative})";
        }
    }
}
