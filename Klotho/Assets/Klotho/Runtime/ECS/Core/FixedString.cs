using System;
using System.Text;

namespace xpTURN.Klotho.ECS
{
    internal static class FixedStringHelper
    {
        [ThreadStatic] private static Encoder _encoder;

        internal static Encoder GetEncoder()
        {
            var enc = _encoder;
            if (enc == null)
            {
                enc = Encoding.UTF8.GetEncoder();
                _encoder = enc;
            }
            else
            {
                enc.Reset();
            }
            return enc;
        }
    }

    public unsafe struct FixedString32 : IEquatable<FixedString32>
    {
        public fixed byte Bytes[30];
        public short Length;

        public static FixedString32 FromString(string value)
        {
            var fs = new FixedString32();
            if (string.IsNullOrEmpty(value)) return fs;

            var encoder = FixedStringHelper.GetEncoder();
            byte* ptr = fs.Bytes;
            fixed (char* chars = value)
            {
                encoder.Convert(chars, value.Length, ptr, 30,
                    flush: true, out _, out int bytesUsed, out _);
                fs.Length = (short)bytesUsed;
            }
            return fs;
        }

        public override string ToString()
        {
            if (Length <= 0) return string.Empty;
            fixed (byte* ptr = Bytes)
            {
                return Encoding.UTF8.GetString(ptr, Length);
            }
        }

        public bool Equals(FixedString32 other)
        {
            if (Length != other.Length) return false;
            fixed (byte* a = Bytes)
            {
                byte* b = other.Bytes;
                for (int i = 0; i < Length; i++)
                {
                    if (a[i] != b[i]) return false;
                }
            }
            return true;
        }

        public override bool Equals(object obj) => obj is FixedString32 other && Equals(other);

        public override int GetHashCode()
        {
            int hash = Length;
            fixed (byte* ptr = Bytes)
            {
                for (int i = 0; i < Length; i++)
                    hash = hash * 31 + ptr[i];
            }
            return hash;
        }

        public static bool operator ==(FixedString32 left, FixedString32 right) => left.Equals(right);
        public static bool operator !=(FixedString32 left, FixedString32 right) => !left.Equals(right);
    }

    public unsafe struct FixedString64 : IEquatable<FixedString64>
    {
        public fixed byte Bytes[62];
        public short Length;

        public static FixedString64 FromString(string value)
        {
            var fs = new FixedString64();
            if (string.IsNullOrEmpty(value)) return fs;

            var encoder = FixedStringHelper.GetEncoder();
            byte* ptr = fs.Bytes;
            fixed (char* chars = value)
            {
                encoder.Convert(chars, value.Length, ptr, 62,
                    flush: true, out _, out int bytesUsed, out _);
                fs.Length = (short)bytesUsed;
            }
            return fs;
        }

        public override string ToString()
        {
            if (Length <= 0) return string.Empty;
            fixed (byte* ptr = Bytes)
            {
                return Encoding.UTF8.GetString(ptr, Length);
            }
        }

        public bool Equals(FixedString64 other)
        {
            if (Length != other.Length) return false;
            fixed (byte* a = Bytes)
            {
                byte* b = other.Bytes;
                for (int i = 0; i < Length; i++)
                {
                    if (a[i] != b[i]) return false;
                }
            }
            return true;
        }

        public override bool Equals(object obj) => obj is FixedString64 other && Equals(other);

        public override int GetHashCode()
        {
            int hash = Length;
            fixed (byte* ptr = Bytes)
            {
                for (int i = 0; i < Length; i++)
                    hash = hash * 31 + ptr[i];
            }
            return hash;
        }

        public static bool operator ==(FixedString64 left, FixedString64 right) => left.Equals(right);
        public static bool operator !=(FixedString64 left, FixedString64 right) => !left.Equals(right);
    }
}
