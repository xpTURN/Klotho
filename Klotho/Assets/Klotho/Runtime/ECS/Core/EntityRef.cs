using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Lightweight entity reference (8 bytes, stack-allocated).
    /// Index is the slot index in the ComponentStorage array.
    /// Version prevents dangling references when a slot is reused (generation index).
    /// </summary>
    [Primitive]
    public readonly struct EntityRef : IEquatable<EntityRef>
    {
        public readonly int Index;
        public readonly int Version;

        public EntityRef(int index, int version)
        {
            Index = index;
            Version = version;
        }

        public bool IsValid => Index >= 0 && Version > 0;

        public long ToId() => ((long)Index << 32) | (uint)Version;

        public static EntityRef FromId(long id) =>
            new EntityRef((int)(id >> 32), (int)(uint)id);

        public static readonly EntityRef None = new EntityRef(-1, 0);

        public bool Equals(EntityRef other)
        {
            return Index == other.Index && Version == other.Version;
        }

        public override bool Equals(object obj)
        {
            return obj is EntityRef other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Index * 397) ^ Version;
        }

        public static bool operator ==(EntityRef left, EntityRef right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(EntityRef left, EntityRef right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"Entity({Index}:{Version})";
        }
    }
}
