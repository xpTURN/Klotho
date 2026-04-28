using System;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Curve
{
    /// <summary>
    /// Keyframe of a fixed-point animation curve. Contains time, value, and tangents.
    /// </summary>
    [Serializable]
    public struct FPKeyframe : IComparable<FPKeyframe>
    {
        public FP64 time;
        public FP64 value;
        public FP64 inTangent;
        public FP64 outTangent;

        public FPKeyframe(FP64 time, FP64 value)
        {
            this.time = time;
            this.value = value;
            this.inTangent = FP64.Zero;
            this.outTangent = FP64.Zero;
        }

        public FPKeyframe(FP64 time, FP64 value, FP64 inTangent, FP64 outTangent)
        {
            this.time = time;
            this.value = value;
            this.inTangent = inTangent;
            this.outTangent = outTangent;
        }

        public int CompareTo(FPKeyframe other)
        {
            return time.CompareTo(other.time);
        }
    }

    public enum FPWrapMode : byte
    {
        Clamp = 0,
        Loop = 1,
        PingPong = 2,
    }

    /// <summary>
    /// Fixed-point animation curve. Performs Hermite interpolation between keyframes.
    /// </summary>
    [Serializable]
    public sealed partial class FPAnimationCurve
    {
        private FPKeyframe[] _keyframes;
        private FPWrapMode _preWrapMode;
        private FPWrapMode _postWrapMode;
        private int _length;

        public int Length => _length;
        public FPWrapMode PreWrapMode { get => _preWrapMode; set => _preWrapMode = value; }
        public FPWrapMode PostWrapMode { get => _postWrapMode; set => _postWrapMode = value; }
        public FPKeyframe this[int index] => _keyframes[index];

        public FP64 StartTime => _length > 0 ? _keyframes[0].time : FP64.Zero;
        public FP64 EndTime => _length > 0 ? _keyframes[_length - 1].time : FP64.Zero;
        public FP64 Duration => EndTime - StartTime;

        public FPAnimationCurve()
        {    
        }

        public FPAnimationCurve(FPKeyframe[] keyframes)
            : this(keyframes, FPWrapMode.Clamp, FPWrapMode.Clamp)
        {
        }

        public FPAnimationCurve(FPKeyframe[] keyframes, FPWrapMode preWrapMode, FPWrapMode postWrapMode)
        {
            Assign(keyframes, preWrapMode, postWrapMode);
        }

        public void Assign(FPKeyframe[] keyframes, FPWrapMode preWrapMode, FPWrapMode postWrapMode)
        {
            _keyframes = (FPKeyframe[])keyframes.Clone();
            Array.Sort(_keyframes);
            _length = _keyframes.Length;
            _preWrapMode = preWrapMode;
            _postWrapMode = postWrapMode;
        }        

        public FP64 Evaluate(FP64 time)
        {
            if (_length == 0)
                return FP64.Zero;
            if (_length == 1)
                return _keyframes[0].value;

            // Wrap time if out of range
            if (time < _keyframes[0].time)
                time = WrapTime(time, _keyframes[0].time, _keyframes[_length - 1].time, _preWrapMode);
            else if (time > _keyframes[_length - 1].time)
                time = WrapTime(time, _keyframes[0].time, _keyframes[_length - 1].time, _postWrapMode);

            // Clamp after wrapping (safety handling)
            if (time <= _keyframes[0].time)
                return _keyframes[0].value;
            if (time >= _keyframes[_length - 1].time)
                return _keyframes[_length - 1].value;

            // Binary search for segment
            int i = FindSegmentIndex(time);
            var k0 = _keyframes[i];
            var k1 = _keyframes[i + 1];

            // Normalized t within the segment
            FP64 dt = k1.time - k0.time;
            if (dt == FP64.Zero)
                return k0.value;

            FP64 t = (time - k0.time) / dt;

            // Constant (step) mode
            if (k0.outTangent == FP64.MaxValue || k1.inTangent == FP64.MaxValue)
                return k0.value;

            // Cubic Hermite interpolation (Horner form)
            FP64 m0 = k0.outTangent * dt;
            FP64 m1 = k1.inTangent * dt;
            FP64 dp = k1.value - k0.value;

            FP64 a = m0 + m1 - dp - dp;
            FP64 b = dp + dp + dp - m0 - m0 - m1;

            return k0.value + t * (m0 + t * (b + t * a));
        }

        private int FindSegmentIndex(FP64 time)
        {
            int lo = 0;
            int hi = _length - 1;

            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keyframes[mid].time <= time)
                    lo = mid;
                else
                    hi = mid;
            }

            return lo;
        }

        private static FP64 WrapTime(FP64 time, FP64 startTime, FP64 endTime, FPWrapMode wrapMode)
        {
            FP64 duration = endTime - startTime;
            if (duration == FP64.Zero)
                return startTime;

            switch (wrapMode)
            {
                case FPWrapMode.Loop:
                {
                    FP64 t = (time - startTime) % duration;
                    if (t < FP64.Zero)
                        t = t + duration;
                    return startTime + t;
                }
                case FPWrapMode.PingPong:
                {
                    FP64 dur2 = duration + duration;
                    FP64 t = (time - startTime) % dur2;
                    if (t < FP64.Zero)
                        t = t + dur2;
                    if (t > duration)
                        t = dur2 - t;
                    return startTime + t;
                }
                default: // Clamp
                    return FP64.Clamp(time, startTime, endTime);
            }
        }

        // Factory methods

        public static FPAnimationCurve Linear()
        {
            var keys = new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.One, FP64.One),
                new FPKeyframe(FP64.One, FP64.One, FP64.One, FP64.One),
            };
            return new FPAnimationCurve(keys);
        }

        public static FPAnimationCurve EaseInOut()
        {
            var keys = new[]
            {
                new FPKeyframe(FP64.Zero, FP64.Zero, FP64.Zero, FP64.Zero),
                new FPKeyframe(FP64.One, FP64.One, FP64.Zero, FP64.Zero),
            };
            return new FPAnimationCurve(keys);
        }

        public static FPAnimationCurve Constant(FP64 value)
        {
            var keys = new[]
            {
                new FPKeyframe(FP64.Zero, value),
                new FPKeyframe(FP64.One, value),
            };
            return new FPAnimationCurve(keys);
        }

        // Serialization

        // preWrap(1) + postWrap(1) + length(4) + keyframes(32 bytes each)
        public int GetSerializedSize() => 1 + 1 + 4 + _length * 32;

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteByte((byte)_preWrapMode);
            writer.WriteByte((byte)_postWrapMode);
            writer.WriteInt32(_length);
            for (int i = 0; i < _length; i++)
            {
                writer.WriteInt64(_keyframes[i].time.RawValue);
                writer.WriteInt64(_keyframes[i].value.RawValue);
                writer.WriteInt64(_keyframes[i].inTangent.RawValue);
                writer.WriteInt64(_keyframes[i].outTangent.RawValue);
            }
        }

        public static FPAnimationCurve Deserialize(ref SpanReader reader)
        {
            var preWrap = (FPWrapMode)reader.ReadByte();
            var postWrap = (FPWrapMode)reader.ReadByte();
            int count = reader.ReadInt32();
            var keys = new FPKeyframe[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = new FPKeyframe(
                    FP64.FromRaw(reader.ReadInt64()),
                    FP64.FromRaw(reader.ReadInt64()),
                    FP64.FromRaw(reader.ReadInt64()),
                    FP64.FromRaw(reader.ReadInt64())
                );
            }

            return new FPAnimationCurve(keys, preWrap, postWrap);
        }
    }
}
