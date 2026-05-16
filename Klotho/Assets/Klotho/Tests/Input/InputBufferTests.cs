using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Input.Tests
{
    /// <summary>
    /// InputBuffer tests
    /// </summary>
    [TestFixture]
    public class InputBufferTests
    {
        private InputBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            _buffer = new InputBuffer();
        }

        #region Basic Add/Get

        [Test]
        public void AddCommand_IncreasesCount()
        {
            Assert.AreEqual(0, _buffer.Count);

            _buffer.AddCommand(new EmptyCommand(0, 0));
            Assert.AreEqual(1, _buffer.Count);

            _buffer.AddCommand(new EmptyCommand(1, 0));
            Assert.AreEqual(2, _buffer.Count);
        }

        [Test]
        public void AddCommand_Null_DoesNothing()
        {
            _buffer.AddCommand(null);
            Assert.AreEqual(0, _buffer.Count);
        }

        [Test]
        public void GetCommand_ReturnsCorrectCommand()
        {
            var cmd = new MoveCommand(1, 10, new FPVector3(FP64.FromRaw(100), FP64.FromRaw(200), FP64.FromRaw(300)));
            _buffer.AddCommand(cmd);

            var retrieved = _buffer.GetCommand(10, 1);

            Assert.IsNotNull(retrieved);
            Assert.IsInstanceOf<MoveCommand>(retrieved);
            var moveCmd = (MoveCommand)retrieved;
            Assert.AreEqual(FP64.FromRaw(100), moveCmd.Target.x);
        }

        [Test]
        public void GetCommand_NotFound_ReturnsNull()
        {
            var result = _buffer.GetCommand(100, 1);
            Assert.IsNull(result);
        }

        [Test]
        public void GetCommands_ReturnsAllForTick()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(1, 5));
            _buffer.AddCommand(new EmptyCommand(2, 5));
            _buffer.AddCommand(new EmptyCommand(0, 6)); // Different tick

            var commands = _buffer.GetCommands(5).ToList();
            Assert.AreEqual(3, commands.Count);
        }

        [Test]
        public void GetCommands_EmptyTick_ReturnsEmpty()
        {
            var commands = _buffer.GetCommands(100);
            Assert.IsEmpty(commands);
        }

        #endregion

        #region Tick Range

        [Test]
        public void OldestNewestTick_UpdatesCorrectly()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 15));

            Assert.AreEqual(5, _buffer.OldestTick);
            Assert.AreEqual(15, _buffer.NewestTick);
        }

        [Test]
        public void EmptyBuffer_OldestNewestAreZero()
        {
            Assert.AreEqual(0, _buffer.OldestTick);
            Assert.AreEqual(0, _buffer.NewestTick);
        }

        #endregion

        #region HasCommand Tests

        [Test]
        public void HasCommandForTick_ReturnsTrue_WhenExists()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            Assert.IsTrue(_buffer.HasCommandForTick(10));
        }

        [Test]
        public void HasCommandForTick_ReturnsFalse_WhenNotExists()
        {
            Assert.IsFalse(_buffer.HasCommandForTick(10));
        }

        [Test]
        public void HasCommandForTick_WithPlayerId_WorksCorrectly()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10));

            Assert.IsTrue(_buffer.HasCommandForTick(10, 0));
            Assert.IsTrue(_buffer.HasCommandForTick(10, 1));
            Assert.IsFalse(_buffer.HasCommandForTick(10, 2));
        }

        [Test]
        public void HasAllCommands_ReturnsTrue_WhenAllPresent()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10));
            _buffer.AddCommand(new EmptyCommand(2, 10));

            Assert.IsTrue(_buffer.HasAllCommands(10, 3));
        }

        [Test]
        public void HasAllCommands_ReturnsFalse_WhenMissing()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10));

            Assert.IsFalse(_buffer.HasAllCommands(10, 3));
        }

        #endregion

        #region Delete Tests

        [Test]
        public void Clear_RemovesAllCommands()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 20));

            _buffer.Clear();

            Assert.AreEqual(0, _buffer.Count);
        }

        [Test]
        public void ClearBefore_RemovesOlderTicks()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 15));
            _buffer.AddCommand(new EmptyCommand(0, 20));

            _buffer.ClearBefore(12);

            Assert.IsFalse(_buffer.HasCommandForTick(5));
            Assert.IsFalse(_buffer.HasCommandForTick(10));
            Assert.IsTrue(_buffer.HasCommandForTick(15));
            Assert.IsTrue(_buffer.HasCommandForTick(20));
        }

        [Test]
        public void ClearAfter_RemovesNewerTicks()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 15));
            _buffer.AddCommand(new EmptyCommand(0, 20));

            _buffer.ClearAfter(12);

            Assert.IsTrue(_buffer.HasCommandForTick(5));
            Assert.IsTrue(_buffer.HasCommandForTick(10));
            Assert.IsFalse(_buffer.HasCommandForTick(15));
            Assert.IsFalse(_buffer.HasCommandForTick(20));
        }

        [Test]
        public void ClearBefore_UpdatesBounds()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 15));

            _buffer.ClearBefore(8);

            Assert.AreEqual(10, _buffer.OldestTick);
            Assert.AreEqual(15, _buffer.NewestTick);
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        public void ClearBefore_NonPositiveOrFirstTick_PreservesAllEntries(int cleanupTick)
        {
            // KlothoEngine's CleanupOldData uses `cleanupTick = Math.Min(rawCleanupTick, _lastVerifiedTick)`
            // and gates with `if (cleanupTick > 0)`, so transient game-start state (_lastVerifiedTick = -1)
            // never reaches InputBuffer.ClearBefore. Lock in the buffer's own behaviour for cleanup ticks
            // at or below the smallest seeded tick — no entries should be removed.
            _buffer.AddCommand(new EmptyCommand(0, 1));
            _buffer.AddCommand(new EmptyCommand(0, 2));
            _buffer.AddCommand(new EmptyCommand(0, 3));

            _buffer.ClearBefore(cleanupTick);

            Assert.AreEqual(3, _buffer.Count, $"ClearBefore({cleanupTick}) must not remove any entries");
            Assert.IsTrue(_buffer.HasCommandForTick(1));
            Assert.IsTrue(_buffer.HasCommandForTick(2));
            Assert.IsTrue(_buffer.HasCommandForTick(3));
        }

        #endregion

        #region Seal guard — Phase 2-1 ②-B silent desync 마지막 차단막

        [Test]
        public void SealEmpty_MarksTickPlayerPair_IsSealedReturnsTrueForExactMatchOnly()
        {
            _buffer.SealEmpty(tick: 10, playerId: 1);

            Assert.IsTrue(_buffer.IsSealed(10, 1), "Exact (tick, playerId) match must be sealed");
            Assert.IsFalse(_buffer.IsSealed(10, 2), "Different playerId at same tick must not be sealed");
            Assert.IsFalse(_buffer.IsSealed(11, 1), "Different tick for same playerId must not be sealed");
            Assert.IsFalse(_buffer.IsSealed(0, 0), "Unrelated (tick, playerId) must not be sealed");
        }

        [Test]
        public void AddCommand_OnSealedTickPlayer_SilentDropsLateRealCommand()
        {
            // Seal first, then attempt to add a real command for the same (tick, playerId).
            // The seal guard drops the late real packet to keep buffer and simulation state consistent.
            _buffer.SealEmpty(tick: 10, playerId: 1);

            int countBefore = _buffer.Count;
            _buffer.AddCommand(new EmptyCommand(playerId: 1, tick: 10));

            Assert.AreEqual(countBefore, _buffer.Count,
                "AddCommand on sealed (tick, playerId) must be silently dropped — buffer count unchanged");
            Assert.IsFalse(_buffer.HasCommandForTick(10),
                "Sealed slot must remain empty after AddCommand drop");

            // Sealing must NOT affect AddCommand at a different (tick, playerId) — guard is per-key.
            _buffer.AddCommand(new EmptyCommand(playerId: 2, tick: 10));
            Assert.IsTrue(_buffer.HasCommandForTick(10),
                "AddCommand at different playerId on same tick must still succeed");
        }

        [Test]
        public void ClearBefore_RemovesSealsAtTicksBelowCleanup_PreservesSealsAtAndAbove()
        {
            // ClearBefore must keep seals in lockstep with buffer entries — stale seals below cleanup
            // would otherwise persist and block legitimate future AddCommand calls forever.
            _buffer.SealEmpty(tick: 5, playerId: 1);
            _buffer.SealEmpty(tick: 8, playerId: 1);
            _buffer.SealEmpty(tick: 12, playerId: 1);

            _buffer.ClearBefore(10);

            Assert.IsFalse(_buffer.IsSealed(5, 1),
                "Seal at tick 5 (< cleanup 10) must be removed");
            Assert.IsFalse(_buffer.IsSealed(8, 1),
                "Seal at tick 8 (< cleanup 10) must be removed");
            Assert.IsTrue(_buffer.IsSealed(12, 1),
                "Seal at tick 12 (>= cleanup 10) must be preserved");
        }

        #endregion

        #region Overwrite

        [Test]
        public void AddCommand_SamePlayerSameTick_Overwrites()
        {
            var cmd1 = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(100), FP64.Zero, FP64.Zero));
            var cmd2 = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(200), FP64.Zero, FP64.Zero));

            _buffer.AddCommand(cmd1);
            _buffer.AddCommand(cmd2);

            // Count should still be 1 (overwrite)
            var commands = _buffer.GetCommands(10).ToList();
            Assert.AreEqual(1, commands.Count);

            var retrieved = (MoveCommand)_buffer.GetCommand(10, 0);
            Assert.AreEqual(FP64.FromRaw(200), retrieved.Target.x);
        }

        #endregion

        #region GetCommandList

        [Test]
        public void GetCommandList_EmptyTick_ReturnsEmptyList()
        {
            var list = _buffer.GetCommandList(100);
            Assert.IsNotNull(list);
            Assert.AreEqual(0, list.Count);
        }

        #endregion
    }

    /// <summary>
    /// SimpleInputPredictor tests
    /// </summary>
    [TestFixture]
    public class SimpleInputPredictorTests
    {
        [Test]
        public void PredictInput_NoPreviousCommands_ReturnsEmptyCommand()
        {
            var predictor = new SimpleInputPredictor();
            var predicted = predictor.PredictInput(0, 10, new List<ICommand>());

            Assert.IsNotNull(predicted);
            Assert.IsInstanceOf<EmptyCommand>(predicted);
            Assert.AreEqual(0, predicted.PlayerId);
            Assert.AreEqual(10, predicted.Tick);
        }

        [Test]
        public void PredictInput_WithPreviousCommands_ReturnsLastCommand()
        {
            var predictor = new SimpleInputPredictor();
            var previousCommands = new List<ICommand>
            {
                new MoveCommand(0, 5, new FPVector3(FP64.FromRaw(100), FP64.Zero, FP64.Zero)),
                new MoveCommand(0, 8, new FPVector3(FP64.FromRaw(200), FP64.Zero, FP64.Zero)),
                new MoveCommand(0, 6, new FPVector3(FP64.FromRaw(150), FP64.Zero, FP64.Zero))
            };

            var predicted = predictor.PredictInput(0, 10, previousCommands);

            Assert.IsInstanceOf<MoveCommand>(predicted);
            Assert.AreEqual(10, predicted.Tick);
        }

        [Test]
        public void UpdateAccuracy_SameType_IncreasesAccuracy()
        {
            var predictor = new SimpleInputPredictor();

            var predicted = new MoveCommand(0, 10, FPVector3.Zero);
            var actual = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(100), FP64.Zero, FP64.Zero));

            predictor.UpdateAccuracy(predicted, actual);

            Assert.AreEqual(1.0f, predictor.Accuracy, 0.01f);
        }

        [Test]
        public void UpdateAccuracy_DifferentType_DecreasesAccuracy()
        {
            var predictor = new SimpleInputPredictor();

            var predicted = new MoveCommand(0, 10, FPVector3.Zero);
            var actual = new EmptyCommand(0, 10);

            predictor.UpdateAccuracy(predicted, actual);

            Assert.AreEqual(0.0f, predictor.Accuracy, 0.01f);
        }
    }
}
