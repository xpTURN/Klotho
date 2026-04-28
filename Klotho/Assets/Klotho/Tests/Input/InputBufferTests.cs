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
