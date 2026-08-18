using System;
using SysRandom = System.Random;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// A/B gate for <see cref="FPConstrainedDelaunay.Cdt.SortTriples"/> — the radix sort that
    /// replaced <see cref="Array.Sort{T}(T[],int,int)"/> in Extract's canonicalize (61% of Extract
    /// on Field: 1.52 ms of 2.49 ms, measured by
    /// <c>FPNavMeshRebakerPerfTests.ExtractPhaseSplit_RadixGate</c>).
    ///
    /// <para>Byte-identical output is the only acceptable bar, and the goldens do not establish
    /// it on their own: they pin the whole rebake, so a sort defect that happens to be invisible
    /// on one triangulation would still ship. These compare the two sorts element by element on
    /// the same unsorted buffer.</para>
    ///
    /// <para>Histogram reuse across rebakes (the bucket count grows AND shrinks with the mesh) is
    /// covered by <c>FPNavMeshAdjacencyRadixTests.PooledRebake_MatchesUnpooled_AsBucketCountGrowsAndShrinks</c>,
    /// which drives both radix sorts through one pooled context.</para>
    /// </summary>
    [TestFixture]
    public class FPCdtExtractRadixTests
    {
        private static void GridPoints(int half, out long[] xs, out long[] zs)
        {
            int side = half * 2 + 1;
            xs = new long[side * side];
            zs = new long[side * side];
            int n = 0;
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                {
                    xs[n] = FPGeoPredicates.Snap(FP64.FromInt(x));
                    zs[n] = FPGeoPredicates.Snap(FP64.FromInt(z));
                    n++;
                }
        }

        /// <summary>Outer square of the grid as a constraint loop, so the erase pass keeps the interior.</summary>
        private static int[] OuterSquare(int half)
        {
            int side = half * 2 + 1;
            int Idx(int x, int z) => (x + half) * side + (z + half);
            int a = Idx(-half, -half), b = Idx(half, -half), c = Idx(half, half), d = Idx(-half, half);
            return new[] { a, b, b, c, c, d, d, a };
        }

        private static void AssertRadixMatchesComparisonSort(int half, bool eraseOuterAndHoles)
        {
            GridPoints(half, out long[] xs, out long[] zs);

            var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
            cdt.InsertAllVertices();
            if (eraseOuterAndHoles)
            {
                int[] cons = OuterSquare(half);
                cdt.InsertConstraints(cons, cons.Length);
            }

            bool[] keep = cdt.ExtractKeepMask(eraseOuterAndHoles);
            int kept = cdt.ExtractTriples(keep, out var triples);
            Assert.Greater(kept, 0, "fixture produced no kept triangles — the sort would be untested");

            // SortTriples ping-pongs through the caller's buffer, so snapshot the unsorted input first.
            var expected = new FPConstrainedDelaunay.Cdt.Triple[kept];
            Array.Copy(triples, expected, kept);
            Array.Sort(expected, 0, kept);

            var actual = cdt.SortTriples(triples, kept);

            for (int i = 0; i < kept; i++)
            {
                Assert.IsTrue(
                    expected[i].A == actual[i].A && expected[i].B == actual[i].B && expected[i].C == actual[i].C,
                    $"triple {i} of {kept} differs: comparison sort ({expected[i].A},{expected[i].B},{expected[i].C}) " +
                    $"vs radix ({actual[i].A},{actual[i].B},{actual[i].C})");
            }
        }

        [TestCase(4)]
        [TestCase(12)]
        [TestCase(20)]
        public void RadixTriples_EqualComparisonSort_NoErase(int half)
        {
            AssertRadixMatchesComparisonSort(half, eraseOuterAndHoles: false);
        }

        [TestCase(4)]
        [TestCase(12)]
        [TestCase(20)]
        public void RadixTriples_EqualComparisonSort_WithErase(int half)
        {
            AssertRadixMatchesComparisonSort(half, eraseOuterAndHoles: true);
        }

        private static int[] OneShotExtract(int half, bool erase, out int count)
        {
            GridPoints(half, out long[] xs, out long[] zs);
            var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
            cdt.InsertAllVertices();
            if (erase)
            {
                int[] cons = OuterSquare(half);
                cdt.InsertConstraints(cons, cons.Length);
            }
            int[] buffer = cdt.Extract(erase, out count);
            var copy = new int[count];          // the output is a pool buffer — copy before reuse
            Array.Copy(buffer, copy, count);
            return copy;
        }

        private static void AssertSteppedExtractMatches(int half, bool erase, Func<int> budget)
        {
            int[] expected = OneShotExtract(half, erase, out int expectedCount);

            GridPoints(half, out long[] xs, out long[] zs);
            var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
            cdt.InsertAllVertices();
            if (erase)
            {
                int[] cons = OuterSquare(half);
                cdt.InsertConstraints(cons, cons.Length);
            }

            cdt.ExtractBegin(erase);
            int guard = 0;
            int limit = xs.Length * 40 + 4096;
            while (!cdt.ExtractStep(budget()))
                Assert.Less(++guard, limit, "the extract did not converge — a phase failed to advance");

            int[] actual = cdt.ExtractResult(out int actualCount);
            Assert.AreEqual(expectedCount, actualCount, $"half={half}, erase={erase}: index count differs");
            for (int i = 0; i < expectedCount; i++)
                Assert.AreEqual(expected[i], actual[i], $"half={half}, erase={erase}: index {i} differs");
        }

        [TestCase(4, false)]
        [TestCase(4, true)]
        [TestCase(12, false)]
        [TestCase(12, true)]
        [TestCase(20, true)]
        public void SteppedExtract_EqualsOneShot_AtEveryFixedBudget(int half, bool erase)
        {
            // Every phase has an interior cursor except the sort, so a budget of 1 exercises each
            // boundary. int.MaxValue is the "finish it" budget and must not overflow a cursor.
            foreach (int budget in new[] { 1, 2, 7, 1000, int.MaxValue })
                AssertSteppedExtractMatches(half, erase, () => budget);
        }

        [TestCase(12, true)]
        [TestCase(20, true)]
        public void SteppedExtract_EqualsOneShot_AtRandomBudgets(int half, bool erase)
        {
            // The 0-1 BFS is the reason this matters most: it is interrupted mid-relaxation, and
            // the claim that cutting it is safe rests on the fixed point being independent of pop
            // order. A schedule that always cuts at the same place would not test that.
            for (int seed = 0; seed < 12; seed++)
            {
                var rng = new SysRandom(seed);
                AssertSteppedExtractMatches(half, erase, () => rng.Next(1, 9));
            }
        }

        [Test]
        public void RadixTriples_DegenerateCounts_AreLeftAlone()
        {
            // kept < 2 returns the input untouched — the pass loops would still be correct, but
            // the early exit is what keeps a 0- or 1-triangle mesh from renting buffers.
            GridPoints(2, out long[] xs, out long[] zs);
            var cdt = new FPConstrainedDelaunay.Cdt(xs, zs, null);
            cdt.InsertAllVertices();

            bool[] keep = cdt.ExtractKeepMask(false);
            int kept = cdt.ExtractTriples(keep, out var triples);
            Assert.Greater(kept, 1);

            Assert.AreSame(triples, cdt.SortTriples(triples, 0));
            Assert.AreSame(triples, cdt.SortTriples(triples, 1));
        }
    }
}
