using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;


namespace xpTURN.Klotho.Deterministic.Random.Tests
{
    /// <summary>
    /// DeterministicRandom deterministic random number generator test
    /// </summary>
    [TestFixture]
    public class DeterministicRandomTests
    {
        #region Seed-based Determinism

        [Test]
        public void SameSeed_ProducesSameSequence()
        {
            var random1 = new DeterministicRandom();
            var random2 = new DeterministicRandom();

            random1.SetSeed(12345);
            random2.SetSeed(12345);

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(random1.NextInt(), random2.NextInt(),
                    $"With the same seed, value {i} should be identical");
            }
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            var random1 = new DeterministicRandom();
            var random2 = new DeterministicRandom();

            random1.SetSeed(12345);
            random2.SetSeed(54321);

            bool allSame = true;
            for (int i = 0; i < 10; i++)
            {
                if (random1.NextInt() != random2.NextInt())
                {
                    allSame = false;
                    break;
                }
            }

            Assert.IsFalse(allSame, "Different seeds should produce different sequences");
        }

        [Test]
        public void ResetSeed_RestartsSequence()
        {
            var random = new DeterministicRandom();
            random.SetSeed(42);

            int first1 = random.NextInt();
            int second1 = random.NextInt();
            int third1 = random.NextInt();

            random.SetSeed(42);

            int first2 = random.NextInt();
            int second2 = random.NextInt();
            int third2 = random.NextInt();

            Assert.AreEqual(first1, first2);
            Assert.AreEqual(second1, second2);
            Assert.AreEqual(third1, third2);
        }

        #endregion

        #region Range Tests

        [Test]
        public void Next_ReturnsNonNegative()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);

            for (int i = 0; i < 1000; i++)
            {
                int value = random.NextInt();
                Assert.GreaterOrEqual(value, 0, "Next() should not return a negative value");
            }
        }

        [Test]
        public void NextMax_ReturnsInRange()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);
            int max = 100;

            for (int i = 0; i < 1000; i++)
            {
                int value = random.NextInt(0, max);
                Assert.GreaterOrEqual(value, 0, "Value should be >= 0");
                Assert.Less(value, max, $"Value should be < {max}");
            }
        }

        [Test]
        public void NextMinMax_ReturnsInRange()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);
            int min = 50;
            int max = 100;

            for (int i = 0; i < 1000; i++)
            {
                int value = random.NextInt(min, max);
                Assert.GreaterOrEqual(value, min, $"Value should be >= {min}");
                Assert.Less(value, max, $"Value should be < {max}");
            }
        }

        [Test]
        public void NextFP64_ReturnsInZeroToOne()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);

            for (int i = 0; i < 1000; i++)
            {
                FP64 value = random.NextFixed();
                Assert.GreaterOrEqual(value.ToFloat(), 0.0f, "Value should be >= 0");
                Assert.Less(value.ToFloat(), 1.0f, "Value should be < 1");
            }
        }

        [Test]
        public void NextFP64MinMax_ReturnsInRange()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);
            FP64 min = FP64.FromFloat(-5.0f);
            FP64 max = FP64.FromFloat(5.0f);

            for (int i = 0; i < 1000; i++)
            {
                FP64 value = random.NextFixed(min, max);
                Assert.GreaterOrEqual(value.ToFloat(), -5.0f, "Value should be >= -5");
                Assert.LessOrEqual(value.ToFloat(), 5.0f, "Value should be <= 5");
            }
        }

        [Test]
        public void NextFixedInclusive_ReturnsInZeroToOneInclusive()
        {
            var random = new DeterministicRandom(42);

            for (int i = 0; i < 10000; i++)
            {
                FP64 value = random.NextFixedInclusive();
                Assert.GreaterOrEqual(value.RawValue, 0L, "Value should be >= 0");
                Assert.LessOrEqual(value.RawValue, FP64.ONE, "Value should be <= 1.0");
            }
        }

        [Test]
        public void NextFixedInclusive_MinMax_ReturnsInRange()
        {
            var random = new DeterministicRandom(42);
            FP64 min = FP64.FromFloat(-5.0f);
            FP64 max = FP64.FromFloat(5.0f);

            for (int i = 0; i < 1000; i++)
            {
                FP64 value = random.NextFixedInclusive(min, max);
                Assert.GreaterOrEqual(value.ToFloat(), -5.0f);
                Assert.LessOrEqual(value.ToFloat(), 5.0f);
            }
        }

        [Test]
        public void NextIntInclusive_ReturnsInInclusiveRange()
        {
            var random = new DeterministicRandom(42);
            bool hitMin = false;
            bool hitMax = false;

            for (int i = 0; i < 10000; i++)
            {
                int value = random.NextIntInclusive(1, 6);
                Assert.GreaterOrEqual(value, 1);
                Assert.LessOrEqual(value, 6);
                if (value == 1) hitMin = true;
                if (value == 6) hitMax = true;
            }

            Assert.IsTrue(hitMin, "Minimum value should be reachable");
            Assert.IsTrue(hitMax, "Maximum value should be reachable");
        }

        [Test]
        public void NextIntInclusive_Deterministic()
        {
            var a = new DeterministicRandom(123);
            var b = new DeterministicRandom(123);

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(a.NextIntInclusive(0, 10), b.NextIntInclusive(0, 10));
            }
        }

        [Test]
        public void NextIntInclusive_SameMinMax_ReturnsThatValue()
        {
            var random = new DeterministicRandom(42);
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(5, random.NextIntInclusive(5, 5));
            }
        }

        #endregion

        #region Distribution Tests

        [Test]
        public void Next_HasReasonableDistribution()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);

            int[] buckets = new int[10];
            int max = 1000;

            for (int i = 0; i < 10000; i++)
            {
                int value = random.NextInt(0, max);
                int bucket = value / 100;
                if (bucket < 10)
                    buckets[bucket]++;
            }

            // Each bucket should be approximately 1000 (uniform distribution)
            foreach (int count in buckets)
            {
                Assert.Greater(count, 500, "Each bucket should be at least 500");
                Assert.Less(count, 1500, "Each bucket should be at most 1500");
            }
        }

        [Test]
        public void NextBool_HasFairDistribution()
        {
            var random = new DeterministicRandom();
            random.SetSeed(42);

            int trueCount = 0;
            int falseCount = 0;
            int sampleSize = 10000;

            for (int i = 0; i < sampleSize; i++)
            {
                if (random.NextBool())
                    trueCount++;
                else
                    falseCount++;
            }

            // 50% +/- 5% range (4500 ~ 5500)
            Assert.Greater(trueCount, sampleSize * 0.45, $"True ratio is too low: {trueCount}/{sampleSize}");
            Assert.Less(trueCount, sampleSize * 0.55, $"True ratio is too high: {trueCount}/{sampleSize}");
            Assert.Greater(falseCount, sampleSize * 0.45, $"False ratio is too low: {falseCount}/{sampleSize}");
            Assert.Less(falseCount, sampleSize * 0.55, $"False ratio is too high: {falseCount}/{sampleSize}");
        }

        [Test]
        public void NextChance_HasCorrectProbability()
        {
            var random = new DeterministicRandom();
            random.SetSeed(123);

            int[] testPercents = { 10, 25, 50, 75, 90 };
            int sampleSize = 10000;

            foreach (int percent in testPercents)
            {
                random.SetSeed(123 + percent); // Use a different seed for each test
                int successCount = 0;

                for (int i = 0; i < sampleSize; i++)
                {
                    if (random.NextChance(percent))
                        successCount++;
                }

                float actualPercent = (float)successCount / sampleSize * 100;
                float tolerance = 5.0f; // +/-5% tolerance

                Assert.Greater(actualPercent, percent - tolerance,
                    $"NextChance({percent}%) actual probability is too low: {actualPercent:F1}%");
                Assert.Less(actualPercent, percent + tolerance,
                    $"NextChance({percent}%) actual probability is too high: {actualPercent:F1}%");
            }
        }

        [Test]
        public void NextWeighted_RespectsWeights()
        {
            var random = new DeterministicRandom();
            random.SetSeed(999);

            int[] weights = { 1, 2, 3, 4 }; // Sum 10, ratio 10%, 20%, 30%, 40%
            int[] counts = new int[4];
            int sampleSize = 10000;

            for (int i = 0; i < sampleSize; i++)
            {
                int index = random.NextWeighted(weights);
                counts[index]++;
            }

            // Verify expected ratios (+/-5% tolerance)
            float[] expectedPercents = { 10f, 20f, 30f, 40f };
            for (int i = 0; i < counts.Length; i++)
            {
                float actualPercent = (float)counts[i] / sampleSize * 100;
                Assert.Greater(actualPercent, expectedPercents[i] - 5f,
                    $"Weight index {i}: actual {actualPercent:F1}%, expected {expectedPercents[i]}%");
                Assert.Less(actualPercent, expectedPercents[i] + 5f,
                    $"Weight index {i}: actual {actualPercent:F1}%, expected {expectedPercents[i]}%");
            }
        }

        [Test]
        public void NextInsideUnitCircle_AllPointsInsideCircle()
        {
            var random = new DeterministicRandom();
            random.SetSeed(777);

            for (int i = 0; i < 1000; i++)
            {
                var point = (FPVector2)random.NextInsideUnitCircle();
                FP64 sqrMag = point.sqrMagnitude;
                
                Assert.LessOrEqual(sqrMag.ToFloat(), 1.0f + 0.001f,
                    $"Point ({point.x.ToFloat()}, {point.y.ToFloat()}) is outside the unit circle");
            }
        }

        [Test]
        public void NextInsideUnitCircle_HasUniformDistribution()
        {
            var random = new DeterministicRandom();
            random.SetSeed(888);

            // Distribution test across 4 quadrants
            int[] quadrants = new int[4]; // Q1(++), Q2(-+), Q3(--), Q4(+-)
            int sampleSize = 4000;

            for (int i = 0; i < sampleSize; i++)
            {
                var point = (FPVector2)random.NextInsideUnitCircle();
                float x = point.x.ToFloat();
                float y = point.y.ToFloat();

                if (x >= 0 && y >= 0) quadrants[0]++;
                else if (x < 0 && y >= 0) quadrants[1]++;
                else if (x < 0 && y < 0) quadrants[2]++;
                else quadrants[3]++;
            }

            // Each quadrant is approximately 25% (+/-8% tolerance)
            int expected = sampleSize / 4;
            foreach (int count in quadrants)
            {
                Assert.Greater(count, expected * 0.67, $"Quadrant distribution is uneven: {count}/{expected}");
                Assert.Less(count, expected * 1.33, $"Quadrant distribution is uneven: {count}/{expected}");
            }
        }

        [Test]
        public void NextInsideUnitSphere_AllPointsInsideSphere()
        {
            var random = new DeterministicRandom();
            random.SetSeed(555);

            for (int i = 0; i < 1000; i++)
            {
                var point = (FPVector3)random.NextInsideUnitSphere();
                FP64 sqrMag = point.sqrMagnitude;
                
                Assert.LessOrEqual(sqrMag.ToFloat(), 1.0f + 0.001f,
                    $"Point ({point.x.ToFloat()}, {point.y.ToFloat()}, {point.z.ToFloat()}) is outside the unit sphere");
            }
        }

        [Test]
        public void NextInsideUnitSphere_HasUniformDistribution()
        {
            var random = new DeterministicRandom();
            random.SetSeed(666);

            // Distribution test across 8 octants
            int[] octants = new int[8];
            int sampleSize = 8000;

            for (int i = 0; i < sampleSize; i++)
            {
                var point = (FPVector3)random.NextInsideUnitSphere();
                float x = point.x.ToFloat();
                float y = point.y.ToFloat();
                float z = point.z.ToFloat();

                int octant = (x >= 0 ? 0 : 1) + (y >= 0 ? 0 : 2) + (z >= 0 ? 0 : 4);
                octants[octant]++;
            }

            // Each octant is approximately 12.5% (+/-6% tolerance)
            int expected = sampleSize / 8;
            foreach (int count in octants)
            {
                Assert.Greater(count, expected * 0.52, $"Octant distribution is uneven: {count}/{expected}");
                Assert.Less(count, expected * 1.48, $"Octant distribution is uneven: {count}/{expected}");
            }
        }

        [Test]
        public void NextDirection2D_AllVectorsAreNormalized()
        {
            var random = new DeterministicRandom();
            random.SetSeed(111);

            for (int i = 0; i < 1000; i++)
            {
                var dir = (FPVector2)random.NextDirection2D();
                float mag = dir.magnitude.ToFloat();
                
                Assert.AreEqual(1.0f, mag, 0.01f,
                    $"Direction vector magnitude is not 1: {mag}");
            }
        }

        [Test]
        public void NextDirection2D_HasUniformAngularDistribution()
        {
            var random = new DeterministicRandom();
            random.SetSeed(222);

            // Distribution test split into 8 directional sectors
            int[] sectors = new int[8]; // Each 45 degrees
            int sampleSize = 8000;

            for (int i = 0; i < sampleSize; i++)
            {
                var dir = (FPVector2)random.NextDirection2D();
                float angle = UnityEngine.Mathf.Atan2(dir.y.ToFloat(), dir.x.ToFloat());
                if (angle < 0) angle += UnityEngine.Mathf.PI * 2;
                
                int sector = (int)(angle / (UnityEngine.Mathf.PI / 4)) % 8;
                sectors[sector]++;
            }

            // Each sector is approximately 12.5% (+/-5% tolerance)
            int expected = sampleSize / 8;
            foreach (int count in sectors)
            {
                Assert.Greater(count, expected * 0.6, $"Angular distribution is uneven: {count}/{expected}");
                Assert.Less(count, expected * 1.4, $"Angular distribution is uneven: {count}/{expected}");
            }
        }

        [Test]
        public void NextDirection3D_AllVectorsAreNormalized()
        {
            var random = new DeterministicRandom();
            random.SetSeed(333);

            for (int i = 0; i < 1000; i++)
            {
                var dir = (FPVector3)random.NextDirection3D();
                float mag = dir.magnitude.ToFloat();
                
                Assert.AreEqual(1.0f, mag, 0.02f,
                    $"Direction vector magnitude is not 1: {mag}");
            }
        }

        [Test]
        public void NextDirection3D_HasUniformHemisphereDistributionZ()
        {
            var random = new DeterministicRandom();
            random.SetSeed(444);

            // Upper/lower hemisphere distribution test
            int upperCount = 0;
            int lowerCount = 0;
            int sampleSize = 10000;

            for (int i = 0; i < sampleSize; i++)
            {
                var dir = (FPVector3)random.NextDirection3D();
                if (dir.z.ToFloat() >= 0)
                    upperCount++;
                else
                    lowerCount++;
            }

            // 50% +/- 5% range
            Assert.Greater(upperCount, sampleSize * 0.45, $"Upper hemisphere distribution is too low: {upperCount}/{sampleSize}");
            Assert.Less(upperCount, sampleSize * 0.55, $"Upper hemisphere distribution is too high: {upperCount}/{sampleSize}");
        }

        [Test]
        public void NextDirection3D_HasUniformHemisphereDistributionY()
        {
            var random = new DeterministicRandom();
            random.SetSeed(444);

            // Upper/lower hemisphere distribution test
            int upperCount = 0;
            int lowerCount = 0;
            int sampleSize = 10000;

            for (int i = 0; i < sampleSize; i++)
            {
                var dir = (FPVector3)random.NextDirection3D();
                if (dir.y.ToFloat() >= 0)
                    upperCount++;
                else
                    lowerCount++;
            }

            // 50% +/- 5% range
            Assert.Greater(upperCount, sampleSize * 0.45, $"Upper hemisphere distribution is too low: {upperCount}/{sampleSize}");
            Assert.Less(upperCount, sampleSize * 0.55, $"Upper hemisphere distribution is too high: {upperCount}/{sampleSize}");
        }

                [Test]
        public void NextDirection3D_HasUniformHemisphereDistributionX()
        {
            var random = new DeterministicRandom();
            random.SetSeed(444);

            // Upper/lower hemisphere distribution test
            int upperCount = 0;
            int lowerCount = 0;
            int sampleSize = 10000;

            for (int i = 0; i < sampleSize; i++)
            {
                var dir = (FPVector3)random.NextDirection3D();
                if (dir.x.ToFloat() >= 0)
                    upperCount++;
                else
                    lowerCount++;
            }

            // 50% +/- 5% range
            Assert.Greater(upperCount, sampleSize * 0.45, $"Upper hemisphere distribution is too low: {upperCount}/{sampleSize}");
            Assert.Less(upperCount, sampleSize * 0.55, $"Upper hemisphere distribution is too high: {upperCount}/{sampleSize}");
        }

        [Test]
        public void Shuffle_MaintainsAllElements()
        {
            var random = new DeterministicRandom();
            random.SetSeed(12345);

            int[] original = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            int[] array = (int[])original.Clone();

            random.Shuffle(array);

            // All elements should be preserved
            System.Array.Sort(array);
            CollectionAssert.AreEqual(original, array, "Elements lost or duplicated after shuffle");
        }

        [Test]
        public void Shuffle_ProducesDifferentOrdersWithDifferentSeeds()
        {
            int[] array1 = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            int[] array2 = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            var random1 = new DeterministicRandom();
            var random2 = new DeterministicRandom();

            random1.SetSeed(111);
            random2.SetSeed(222);

            random1.Shuffle(array1);
            random2.Shuffle(array2);

            // Should produce different orderings (very low probability of being the same)
            bool allSame = true;
            for (int i = 0; i < array1.Length; i++)
            {
                if (array1[i] != array2[i])
                {
                    allSame = false;
                    break;
                }
            }
            Assert.IsFalse(allSame, "Different seeds should produce different shuffle results");
        }

        [Test]
        public void Shuffle_HasUniformPositionDistribution()
        {
            var random = new DeterministicRandom();
            
            int arraySize = 5;
            int sampleSize = 5000;
            int[,] positionCounts = new int[arraySize, arraySize]; // [element][position]

            for (int trial = 0; trial < sampleSize; trial++)
            {
                random.SetSeed(trial);
                int[] array = { 0, 1, 2, 3, 4 };
                random.Shuffle(array);

                for (int pos = 0; pos < arraySize; pos++)
                {
                    positionCounts[array[pos], pos]++;
                }
            }

            // The frequency of each element appearing at each position is approximately 20% (sampleSize/5)
            int expected = sampleSize / arraySize;
            for (int elem = 0; elem < arraySize; elem++)
            {
                for (int pos = 0; pos < arraySize; pos++)
                {
                    int count = positionCounts[elem, pos];
                    Assert.Greater(count, expected * 0.7,
                        $"Frequency of element {elem} at position {pos} is too low: {count}/{expected}");
                    Assert.Less(count, expected * 1.3,
                        $"Frequency of element {elem} at position {pos} is too high: {count}/{expected}");
                }
            }
        }

        [Test]
        public void ChiSquare_UniformDistributionTest()
        {
            var random = new DeterministicRandom();
            random.SetSeed(54321);

            int bucketCount = 10;
            int sampleSize = 10000;
            int[] observed = new int[bucketCount];
            float expected = (float)sampleSize / bucketCount;

            for (int i = 0; i < sampleSize; i++)
            {
                int value = random.NextInt(0, bucketCount);
                observed[value]++;
            }

            // Calculate chi-square statistic
            float chiSquare = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                float diff = observed[i] - expected;
                chiSquare += (diff * diff) / expected;
            }

            // df=9, critical value alpha=0.05: ~16.92
            // critical value alpha=0.01: ~21.67
            Assert.Less(chiSquare, 21.67f,
                $"Chi-square test failed: chi^2 = {chiSquare:F2} (critical value 21.67, df=9, alpha=0.01)");
        }

        [Test]
        public void NextFixed_HasUniformDistribution()
        {
            var random = new DeterministicRandom();
            random.SetSeed(99999);

            int bucketCount = 10;
            int sampleSize = 10000;
            int[] buckets = new int[bucketCount];

            for (int i = 0; i < sampleSize; i++)
            {
                float value = ((FP64)random.NextFixed()).ToFloat();
                int bucket = (int)(value * bucketCount);
                if (bucket >= bucketCount) bucket = bucketCount - 1;
                if (bucket < 0) bucket = 0;
                buckets[bucket]++;
            }

            // Each bucket is approximately 1000 (+/-30% tolerance)
            int expected = sampleSize / bucketCount;
            for (int i = 0; i < bucketCount; i++)
            {
                Assert.Greater(buckets[i], expected * 0.7,
                    $"Bucket {i} distribution is too low: {buckets[i]}/{expected}");
                Assert.Less(buckets[i], expected * 1.3,
                    $"Bucket {i} distribution is too high: {buckets[i]}/{expected}");
            }
        }

        [Test]
        public void NextRotation_IsUnitQuaternion()
        {
            var random = new DeterministicRandom(42);

            for (int i = 0; i < 100; i++)
            {
                var q = random.NextRotation();
                float mag = q.magnitude.ToFloat();
                Assert.AreEqual(1f, mag, 0.01f, $"Rotation {i} should be a unit quaternion");
            }
        }

        [Test]
        public void NextRotation_Deterministic()
        {
            var a = new DeterministicRandom(123);
            var b = new DeterministicRandom(123);

            for (int i = 0; i < 100; i++)
            {
                var qa = a.NextRotation();
                var qb = b.NextRotation();
                Assert.AreEqual(qa.x.RawValue, qb.x.RawValue);
                Assert.AreEqual(qa.y.RawValue, qb.y.RawValue);
                Assert.AreEqual(qa.z.RawValue, qb.z.RawValue);
                Assert.AreEqual(qa.w.RawValue, qb.w.RawValue);
            }
        }

        [Test]
        public void NextRotation_ProducesVariedResults()
        {
            var random = new DeterministicRandom(42);
            var first = random.NextRotation();
            bool varied = false;

            for (int i = 0; i < 100; i++)
            {
                var q = random.NextRotation();
                if (q.x.RawValue != first.x.RawValue || q.y.RawValue != first.y.RawValue)
                {
                    varied = true;
                    break;
                }
            }

            Assert.IsTrue(varied, "NextRotation should produce varied results");
        }

        #endregion

        #region Special Cases

        [Test]
        public void NextMax_One_ReturnsZero()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);

            for (int i = 0; i < 100; i++)
            {
                int value = random.NextInt(0, 1);
                Assert.AreEqual(0, value, "Next(1) should always return 0");
            }
        }

        [Test]
        public void NextMinMax_SameValues_ReturnsMin()
        {
            var random = new DeterministicRandom();
            random.SetSeed(0);

            for (int i = 0; i < 100; i++)
            {
                int value = random.NextInt(5, 5);
                Assert.AreEqual(5, value, "Next(5, 5) should always return 5");
            }
        }

        #endregion

        #region State Save/Restore

        [Test]
        public void GetState_AllowsReproduction()
        {
            var random = new DeterministicRandom();
            random.SetSeed(12345);

            // Call several times
            random.NextInt();
            random.NextInt();
            random.NextInt();

            // Save state
            (ulong state0, ulong state1) state = random.GetFullState();

            // Additional calls
            int val1 = random.NextInt();
            int val2 = random.NextInt();

            // Restore state
            random.SetFullState(state.state0, state.state1);

            // Should produce the same values
            Assert.AreEqual(val1, random.NextInt());
            Assert.AreEqual(val2, random.NextInt());
        }

        #endregion

        #region Multi-Instance Independence

        [Test]
        public void MultipleInstances_AreIndependent()
        {
            var random1 = new DeterministicRandom();
            var random2 = new DeterministicRandom();

            random1.SetSeed(100);
            random2.SetSeed(200);

            int r1_1 = random1.NextInt();
            int r2_1 = random2.NextInt();

            // Calls on random2 should not affect random1
            random2.NextInt();
            random2.NextInt();
            random2.NextInt();

            random1.SetSeed(100);
            int r1_2 = random1.NextInt();

            Assert.AreEqual(r1_1, r1_2, "Calls on another instance should not affect this instance");
        }

        #endregion

        #region Struct Value Copy

        [Test]
        public void StructCopy_DoesNotAffectOriginal()
        {
            var original = new DeterministicRandom();
            original.SetSeed(42);

            // Advance the original a few steps
            original.NextInt();
            original.NextInt();

            // Copy via value assignment
            var copy = original;

            // Advance the copy
            copy.NextInt();
            copy.NextInt();
            copy.NextInt();

            // The original state should not change
            var originalState = original.GetFullState();
            int originalNext = original.NextInt();

            // Verify by reproducing the same state
            var verify = new DeterministicRandom();
            verify.SetSeed(42);
            verify.NextInt();
            verify.NextInt();
            var verifyState = verify.GetFullState();

            Assert.AreEqual(verifyState.state0, originalState.state0, "Original state0 should not be affected by the copy");
            Assert.AreEqual(verifyState.state1, originalState.state1, "Original state1 should not be affected by the copy");
            Assert.AreEqual(verify.NextInt(), originalNext, "Original sequence should not be affected by the copy");
        }

        #endregion

        #region FromSeed Derived Stream

        [Test]
        public void FromSeed_SameInput_ProducesSameSequence()
        {
            var rng1 = DeterministicRandom.FromSeed(12345, 100, 0);
            var rng2 = DeterministicRandom.FromSeed(12345, 100, 0);

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(rng1.NextInt(), rng2.NextInt(),
                    $"The same FromSeed input should produce the same sequence at index {i}");
            }
        }

        [Test]
        public void FromSeed_DifferentFeatureKey_ProducesDifferentSequence()
        {
            var rng1 = DeterministicRandom.FromSeed(12345, 1, 0);
            var rng2 = DeterministicRandom.FromSeed(12345, 2, 0);

            bool allSame = true;
            for (int i = 0; i < 10; i++)
            {
                if (rng1.NextInt() != rng2.NextInt())
                {
                    allSame = false;
                    break;
                }
            }

            Assert.IsFalse(allSame, "Different featureKey should produce different sequences");
        }

        [Test]
        public void FromSeed_DifferentIndex_ProducesDifferentSequence()
        {
            var rng1 = DeterministicRandom.FromSeed(12345, 100, 0);
            var rng2 = DeterministicRandom.FromSeed(12345, 100, 1);

            bool allSame = true;
            for (int i = 0; i < 10; i++)
            {
                if (rng1.NextInt() != rng2.NextInt())
                {
                    allSame = false;
                    break;
                }
            }

            Assert.IsFalse(allSame, "Different index should produce different sequences");
        }

        [Test]
        public void FromSeed_ZeroInputs_ProducesValidSequence()
        {
            var rng = DeterministicRandom.FromSeed(0, 0, 0);

            // Should not produce all zeros
            bool hasNonZero = false;
            for (int i = 0; i < 10; i++)
            {
                if (rng.NextInt() != 0)
                {
                    hasNonZero = true;
                    break;
                }
            }

            Assert.IsTrue(hasNonZero, "FromSeed(0,0,0) should produce a valid non-zero sequence");
        }

        #endregion
    }
}
