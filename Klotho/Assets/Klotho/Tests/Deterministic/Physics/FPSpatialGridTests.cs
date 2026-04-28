using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPSpatialGridTests
    {
        #region Constructor

        [Test]
        public void Constructor_SetsCellSizeCorrectly()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            var bounds = new FPBounds3(
                new FPVector3(FP64.FromInt(1), FP64.FromInt(1), FP64.FromInt(1)),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2)));
            grid.Insert(1, bounds);
            grid.Insert(2, bounds);
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(1, count);
            Assert.AreEqual((1, 2), output[0]);
        }

        #endregion

        #region Insert

        [Test]
        public void Insert_SingleEntity_ZeroPairs()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(0, count);
            Assert.AreEqual(0, output.Count);
        }

        [Test]
        public void Insert_TwoInSameCell_OnePair()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(1, count);
            Assert.AreEqual((1, 2), output[0]);
        }

        [Test]
        public void Insert_TwoInDifferentCells_ZeroPairs()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(
                new FPVector3(FP64.FromInt(40), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(0, count);
        }

        [Test]
        public void Insert_EntitySpanningMultipleCells()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2)),
                new FPVector3(FP64.FromInt(6), FP64.FromInt(6), FP64.FromInt(6))));
            grid.Insert(2, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(1), FP64.FromInt(1), FP64.FromInt(1))));
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(1, count);
            Assert.AreEqual((1, 2), output[0]);
        }

        #endregion

        #region GetPairs

        [Test]
        public void PairNormalization_SmallerIdFirst()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(5, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            grid.GetPairs(output);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(2, output[0].Item1);
            Assert.AreEqual(5, output[0].Item2);
        }

        [Test]
        public void DuplicatePairs_Deduplicated()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(6), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(
                new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(6), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(1, count);
            Assert.AreEqual((1, 2), output[0]);
        }

        [Test]
        public void MultiplePairs_SortedCorrectly()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(3, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            grid.GetPairs(output);
            Assert.AreEqual(3, output.Count);
            Assert.AreEqual((1, 2), output[0]);
            Assert.AreEqual((1, 3), output[1]);
            Assert.AreEqual((2, 3), output[2]);
        }

        #endregion

        #region Clear

        [Test]
        public void Clear_ResetsState()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Clear();
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(0, count);
        }

        [Test]
        public void ClearAndReinsert_TickCycle()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            var output = new List<(int, int)>();

            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.GetPairs(output);
            Assert.AreEqual(1, output.Count);

            grid.Clear();
            grid.Insert(1, new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.GetPairs(output);
            Assert.AreEqual(0, output.Count);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_InsertOrderIndependent()
        {
            var boundsA = new FPBounds3(FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2)));
            var boundsB = new FPBounds3(
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2)));
            var boundsC = new FPBounds3(
                new FPVector3(FP64.Zero, FP64.FromInt(1), FP64.Zero),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2)));

            var gridA = new FPSpatialGrid(FP64.FromInt(4));
            gridA.Insert(1, boundsA);
            gridA.Insert(2, boundsB);
            gridA.Insert(3, boundsC);
            var outputA = new List<(int, int)>();
            gridA.GetPairs(outputA);

            var gridB = new FPSpatialGrid(FP64.FromInt(4));
            gridB.Insert(3, boundsC);
            gridB.Insert(1, boundsA);
            gridB.Insert(2, boundsB);
            var outputB = new List<(int, int)>();
            gridB.GetPairs(outputB);

            Assert.AreEqual(outputA.Count, outputB.Count);
            for (int i = 0; i < outputA.Count; i++)
            {
                Assert.AreEqual(outputA[i], outputB[i]);
            }
        }

        #endregion

        #region NegativeCoordinates

        [Test]
        public void NegativeCoordinates_Work()
        {
            var grid = new FPSpatialGrid(FP64.FromInt(4));
            grid.Insert(1, new FPBounds3(
                new FPVector3(FP64.FromInt(-10), FP64.FromInt(-10), FP64.FromInt(-10)),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            grid.Insert(2, new FPBounds3(
                new FPVector3(FP64.FromInt(-10), FP64.FromInt(-10), FP64.FromInt(-10)),
                new FPVector3(FP64.FromInt(2), FP64.FromInt(2), FP64.FromInt(2))));
            var output = new List<(int, int)>();
            int count = grid.GetPairs(output);
            Assert.AreEqual(1, count);
            Assert.AreEqual((1, 2), output[0]);
        }

        #endregion
    }
}
