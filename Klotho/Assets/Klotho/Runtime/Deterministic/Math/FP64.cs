using System;
using xpTURN.Klotho.Core;
namespace xpTURN.Klotho.Deterministic.Math
{
    /// <summary>
    /// 64-bit fixed-point number implementation
    /// 32.32 format (upper 32 bits: integer part, lower 32 bits: fractional part)
    /// </summary>
    [Primitive]
    [Serializable]
    public partial struct FP64 : IEquatable<FP64>, IComparable<FP64>
    {
        public const int FRACTIONAL_BITS = 32;
        public const long ONE = 1L << FRACTIONAL_BITS;
        public const long HALF = ONE >> 1;

        public static readonly FP64 Zero = new FP64(0);
        public static readonly FP64 One = new FP64(ONE);
        public static readonly FP64 Half = new FP64(HALF);
        public static readonly FP64 MinValue = new FP64(long.MinValue);
        public static readonly FP64 MaxValue = new FP64(long.MaxValue);
        public static readonly FP64 Pi = FromDouble(3.14159265358979323846);
        public static readonly FP64 TwoPi = FromDouble(6.28318530717958647692);
        public static readonly FP64 HalfPi = FromDouble(1.57079632679489661923);
        public static readonly FP64 Deg2Rad = FromDouble(0.01745329251994329577);
        public static readonly FP64 Rad2Deg = FromDouble(57.29577951308232087680);
        public static readonly FP64 Epsilon = new FP64(1);

        private long _rawValue;

        public long RawValue => _rawValue;

        internal FP64(long rawValue)
        {
            _rawValue = rawValue;
        }

        public static FP64 FromRaw(long rawValue)
        {
            return new FP64(rawValue);
        }

        public static FP64 FromInt(int value)
        {
            return new FP64((long)value << FRACTIONAL_BITS);
        }

        public static FP64 FromFloat(float value)
        {
            return new FP64((long)(value * ONE));
        }

        public static FP64 FromDouble(double value)
        {
            return new FP64((long)(value * ONE));
        }

        public float ToFloat()
        {
            return (float)_rawValue / ONE;
        }

        public double ToDouble()
        {
            return (double)_rawValue / ONE;
        }

        public int ToInt()
        {
            return (int)(_rawValue >> FRACTIONAL_BITS);
        }

        // operator overloads
        public static FP64 operator +(FP64 a, FP64 b)
        {
            return new FP64(a._rawValue + b._rawValue);
        }

        public static FP64 operator -(FP64 a, FP64 b)
        {
            return new FP64(a._rawValue - b._rawValue);
        }

        public static FP64 operator -(FP64 a)
        {
            return new FP64(-a._rawValue);
        }

        public static FP64 operator *(FP64 a, FP64 b)
        {
            return SafeMultiply(a, b);
        }

        public static FP64 operator /(FP64 a, FP64 b)
        {
            return SafeDivide(a, b);
        }

        public static FP64 operator %(FP64 a, FP64 b)
        {
            if (b._rawValue == 0)
                return Zero;
            return new FP64(a._rawValue % b._rawValue);
        }

        public static bool operator ==(FP64 a, FP64 b)
        {
            return a._rawValue == b._rawValue;
        }

        public static bool operator !=(FP64 a, FP64 b)
        {
            return a._rawValue != b._rawValue;
        }

        public static bool operator <(FP64 a, FP64 b)
        {
            return a._rawValue < b._rawValue;
        }

        public static bool operator >(FP64 a, FP64 b)
        {
            return a._rawValue > b._rawValue;
        }

        public static bool operator <=(FP64 a, FP64 b)
        {
            return a._rawValue <= b._rawValue;
        }

        public static bool operator >=(FP64 a, FP64 b)
        {
            return a._rawValue >= b._rawValue;
        }

        // implicit conversion
        public static implicit operator FP64(int value)
        {
            return FromInt(value);
        }

        // IEquatable, IComparable implementation
        public bool Equals(FP64 other)
        {
            return _rawValue == other._rawValue;
        }

        public override bool Equals(object obj)
        {
            return obj is FP64 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _rawValue.GetHashCode();
        }

        public int CompareTo(FP64 other)
        {
            return _rawValue.CompareTo(other._rawValue);
        }

        public override string ToString()
        {
            return ToDouble().ToString("F4");
        }

        public string ToString(string format)
        {
            return ToDouble().ToString(format);
        }
    }

    public static class FP64Extensions
    {
        public static FP64 ToFP64(this float @this)
        {
            return new FP64((long)(@this * FP64.ONE));
        }

        public static FP64 ToFP64(this double @this)
        {
            return new FP64((long)(@this * FP64.ONE));
        }

        public static FP64 FromRaw(this ref FP64 @this, long rawValue)
        {
            @this = new FP64(rawValue); 
            return @this;
        }

        public static FP64 FromInt(this ref FP64 @this, int value)
        {
            @this = new FP64((long)value << FP64.FRACTIONAL_BITS);
            return @this;
        }

        public static FP64 FromFloat(this ref FP64 @this, float value)
        {
            @this = new FP64((long)(value * FP64.ONE));
            return @this;
        }

        public static FP64 FromDouble(this ref FP64 @this, double value)
        {
            @this = new FP64((long)(value * FP64.ONE));
            return @this;
        }
    }
}
