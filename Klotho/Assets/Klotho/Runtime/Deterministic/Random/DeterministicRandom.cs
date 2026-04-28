using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Random
{
    /// <summary>
    /// Deterministic random number generator implementation (Xorshift128+ algorithm)
    /// </summary>
    [Serializable]
    public struct DeterministicRandom
    {
        private ulong _state0;
        private ulong _state1;
        private int _seed;

        public int Seed => _seed;

        public DeterministicRandom(int seed)
        {
            _state0 = 0;
            _state1 = 0;
            _seed = 0;
            SetSeed(seed);
        }

        public void SetSeed(int seed)
        {
            _seed = seed;

            // Generate the initial state with SplitMix64
            ulong z = (ulong)seed;

            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            _state0 = z ^ (z >> 31);

            z = _state0 + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            _state1 = z ^ (z >> 31);
        }

        /// <summary>
        /// Creates a derived stream from a world seed, feature key, and optional index
        /// </summary>
        public static DeterministicRandom FromSeed(ulong worldSeed, ulong featureKey, ulong index = 0)
        {
            ulong seed = Mix64(worldSeed, featureKey);
            seed = Mix64(seed, index);

            var rng = new DeterministicRandom();
            rng._seed = 0;

            // SplitMix64 expansion
            rng._state0 = SplitMix64(ref seed);
            rng._state1 = SplitMix64(ref seed);

            // Prevent the all-zero state
            if (rng._state0 == 0UL && rng._state1 == 0UL)
                rng._state1 = 0x9E3779B97F4A7C15UL;

            return rng;
        }

        private static ulong Mix64(ulong a, ulong b)
        {
            ulong x = a ^ (b + 0x9E3779B97F4A7C15UL);
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        private static ulong SplitMix64(ref ulong state)
        {
            ulong z = (state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public (ulong state0, ulong state1) GetFullState()
        {
            return (_state0, _state1);
        }

        public void SetFullState(ulong state0, ulong state1)
        {
            _state0 = state0;
            _state1 = state1;
        }

        private ulong NextUInt64()
        {
            // Xorshift128+
            ulong s1 = _state0;
            ulong s0 = _state1;
            ulong result = s0 + s1;

            _state0 = s0;
            s1 ^= s1 << 23;
            _state1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);

            return result;
        }

        public int NextInt()
        {
            return (int)(NextUInt64() & 0x7FFFFFFF);
        }

        public int NextInt(int min, int max)
        {
            if (min >= max)
                return min;

            ulong range = (ulong)(max - min);
            ulong threshold = (~range + 1) % range;
            ulong r;
            do { r = NextUInt64(); } while (r < threshold);
            return min + (int)(r % range);
        }

        /// <summary>
        /// Fixed-point in the range [0, 1)
        /// </summary>
        public FP64 NextFixed()
        {
            return FP64.FromRaw((long)(NextUInt64() & 0xFFFFFFFF));
        }

        /// <summary>
        /// Fixed-point in the range [min, max)
        /// </summary>
        public FP64 NextFixed(FP64 min, FP64 max)
        {
            FP64 t = NextFixed();
            return FP64.LerpUnclamped(min, max, t);
        }

        /// <summary>
        /// Fixed-point in the range [0, 1] (inclusive upper bound)
        /// </summary>
        public FP64 NextFixedInclusive()
        {
            ulong r = NextUInt64() % ((ulong)FP64.ONE + 1);
            return FP64.FromRaw((long)r);
        }

        /// <summary>
        /// Fixed-point in the range [min, max] (inclusive upper bound)
        /// </summary>
        public FP64 NextFixedInclusive(FP64 min, FP64 max)
        {
            FP64 t = NextFixedInclusive();
            return FP64.LerpUnclamped(min, max, t);
        }

        /// <summary>
        /// Integer in the range [min, max] (inclusive upper bound)
        /// </summary>
        public int NextIntInclusive(int min, int max)
        {
            if (min >= max)
                return min;

            ulong range = (ulong)(max - min) + 1;
            ulong threshold = (~range + 1) % range;
            ulong r;
            do { r = NextUInt64(); } while (r < threshold);
            return min + (int)(r % range);
        }

        /// <summary>
        /// Random point inside the unit circle
        /// </summary>
        public FPVector2 NextInsideUnitCircle()
        {
            // Rejection sampling
            while (true)
            {
                FP64 x = FP64.FromRaw((long)(NextUInt64() % (2 * FP64.ONE)) - FP64.ONE);
                FP64 y = FP64.FromRaw((long)(NextUInt64() % (2 * FP64.ONE)) - FP64.ONE);

                FP64 sqrMag = x * x + y * y;
                if (sqrMag <= FP64.One)
                {
                    return new FPVector2(x, y);
                }
            }
        }

        /// <summary>
        /// Random point inside the unit sphere
        /// </summary>
        public FPVector3 NextInsideUnitSphere()
        {
            // Rejection sampling
            while (true)
            {
                FP64 x = FP64.FromRaw((long)(NextUInt64() % (2 * FP64.ONE)) - FP64.ONE);
                FP64 y = FP64.FromRaw((long)(NextUInt64() % (2 * FP64.ONE)) - FP64.ONE);
                FP64 z = FP64.FromRaw((long)(NextUInt64() % (2 * FP64.ONE)) - FP64.ONE);

                FP64 sqrMag = x * x + y * y + z * z;
                if (sqrMag <= FP64.One)
                {
                    return new FPVector3(x, y, z);
                }
            }
        }

        /// <summary>
        /// 2D random direction vector
        /// </summary>
        public FPVector2 NextDirection2D()
        {
            FP64 angle = NextFixed() * FP64.TwoPi;
            return new FPVector2(FP64.Cos(angle), FP64.Sin(angle));
        }

        /// <summary>
        /// 3D random direction vector - uniform distribution
        /// </summary>
        public FPVector3 NextDirection3D()
        {
            // Generate a uniformly distributed direction vector
            // theta: [0, 2π) azimuth
            FP64 theta = NextFixed() * FP64.TwoPi;

            // z: uniform on [-1, 1] (cos(phi) must be uniform for a uniform spherical distribution)
            FP64 z = NextFixed() * FP64.FromInt(2) - FP64.One;

            // sinPhi = sqrt(1 - z²)
            FP64 sinPhi = FP64.Sqrt(FP64.One - z * z);

            return new FPVector3(
                sinPhi * FP64.Cos(theta),
                sinPhi * FP64.Sin(theta),
                z
            );
        }

        /// <summary>
        /// Uniformly random rotation (Shoemake's method)
        /// </summary>
        public FPQuaternion NextRotation()
        {
            FP64 u0 = NextFixed();
            FP64 u1 = NextFixed();
            FP64 u2 = NextFixed();

            FP64 twoPi = FP64.TwoPi;
            FP64 sqrt1 = FP64.Sqrt(FP64.One - u0);
            FP64 sqrtU = FP64.Sqrt(u0);
            FP64 theta1 = twoPi * u1;
            FP64 theta2 = twoPi * u2;

            return new FPQuaternion(
                sqrt1 * FP64.Sin(theta1),
                sqrt1 * FP64.Cos(theta1),
                sqrtU * FP64.Sin(theta2),
                sqrtU * FP64.Cos(theta2)
            );
        }

        public bool NextBool()
        {
            return (NextUInt64() & 1) == 1;
        }

        public bool NextChance(int percent)
        {
            return NextInt(0, 100) < percent;
        }

        /// <summary>
        /// Weighted random index selection
        /// </summary>
        public int NextWeighted(int[] weights)
        {
            if (weights == null || weights.Length == 0)
                return -1;

            int total = 0;
            foreach (int w in weights)
                total += w;

            if (total <= 0)
                return NextInt(0, weights.Length);

            int roll = NextInt(0, total);
            int cumulative = 0;

            for (int i = 0; i < weights.Length; i++)
            {
                cumulative += weights[i];
                if (roll < cumulative)
                    return i;
            }

            return weights.Length - 1;
        }

        /// <summary>
        /// Fisher-Yates shuffle
        /// </summary>
        public void Shuffle<T>(T[] array)
        {
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = NextInt(0, i + 1);
                (array[i], array[j]) = (array[j], array[i]);
            }
        }
    }
}
