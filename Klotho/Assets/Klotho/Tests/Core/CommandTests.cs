using System;
using NUnit.Framework;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Command serialization/deserialization tests
    /// </summary>
    [TestFixture]
    public class CommandTests
    {
        #region EmptyCommand

        [Test]
        public void EmptyCommand_Serialize_Deserialize_PreservesData()
        {
            var original = new EmptyCommand(1, 100);

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var restored = new EmptyCommand();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.CommandTypeId, restored.CommandTypeId);
        }

        [Test]
        public void EmptyCommand_HasCorrectTypeId()
        {
            var cmd = new EmptyCommand();
            Assert.AreEqual(EmptyCommand.TYPE_ID, cmd.CommandTypeId);
            Assert.AreEqual(0, cmd.CommandTypeId);
        }

        #endregion

        #region MoveCommand

        [Test]
        public void MoveCommand_Serialize_Deserialize_PreservesData()
        {
            var original = new MoveCommand(2, 50, new FPVector3(FP64.FromRaw(1000L), FP64.FromRaw(2000L), FP64.FromRaw(3000L)));

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var restored = new MoveCommand();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.Target, restored.Target);
        }

        [Test]
        public void MoveCommand_HasCorrectTypeId()
        {
            var cmd = new MoveCommand();
            Assert.AreEqual(MoveCommand.TYPE_ID, cmd.CommandTypeId);
            Assert.AreEqual(1, cmd.CommandTypeId);
        }

        [Test]
        public void MoveCommand_NegativeCoordinates_WorksCorrectly()
        {
            var original = new MoveCommand(1, 10, new FPVector3(FP64.FromRaw(-500L), FP64.FromRaw(-1000L), FP64.FromRaw(-1500L)));

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var restored = new MoveCommand();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.Target, restored.Target);
        }

        #endregion

        #region ActionCommand

        [Test]
        public void ActionCommand_Serialize_Deserialize_PreservesData()
        {
            var original = new ActionCommand(3, 75, 5, 10)
            {
                Position = new FPVector3(FP64.FromRaw(100L), FP64.FromRaw(200L), FP64.FromRaw(300L))
            };

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var restored = new ActionCommand();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.ActionId, restored.ActionId);
            Assert.AreEqual(original.TargetEntityId, restored.TargetEntityId);
            Assert.AreEqual(original.Position, restored.Position);
        }

        [Test]
        public void ActionCommand_HasCorrectTypeId()
        {
            var cmd = new ActionCommand();
            Assert.AreEqual(ActionCommand.TYPE_ID, cmd.CommandTypeId);
            Assert.AreEqual(2, cmd.CommandTypeId);
        }

        #endregion

        #region SkillCommand

        [Test]
        public void SkillCommand_Serialize_Deserialize_PreservesData()
        {
            var original = new SkillCommand(4, 120, 7, 15)
            {
                Target = new FPVector3(FP64.FromRaw(400L), FP64.FromRaw(500L), FP64.FromRaw(600L))
            };

            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var restored = new SkillCommand();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);

            Assert.AreEqual(original.PlayerId, restored.PlayerId);
            Assert.AreEqual(original.Tick, restored.Tick);
            Assert.AreEqual(original.SkillId, restored.SkillId);
            Assert.AreEqual(original.TargetEntityId, restored.TargetEntityId);
            Assert.AreEqual(original.Target, restored.Target);
        }

        [Test]
        public void SkillCommand_HasCorrectTypeId()
        {
            var cmd = new SkillCommand();
            Assert.AreEqual(SkillCommand.TYPE_ID, cmd.CommandTypeId);
            Assert.AreEqual(3, cmd.CommandTypeId);
        }

        #endregion

        #region CommandFactory

        [Test]
        public void CommandFactory_CreateCommand_EmptyCommand()
        {
            var factory = new CommandFactory();
            var cmd = factory.CreateCommand(EmptyCommand.TYPE_ID);

            Assert.IsNotNull(cmd);
            Assert.IsInstanceOf<EmptyCommand>(cmd);
        }

        [Test]
        public void CommandFactory_CreateCommand_MoveCommand()
        {
            var factory = new CommandFactory();
            var cmd = factory.CreateCommand(MoveCommand.TYPE_ID);

            Assert.IsNotNull(cmd);
            Assert.IsInstanceOf<MoveCommand>(cmd);
        }

        [Test]
        public void CommandFactory_CreateCommand_ActionCommand()
        {
            var factory = new CommandFactory();
            var cmd = factory.CreateCommand(ActionCommand.TYPE_ID);

            Assert.IsNotNull(cmd);
            Assert.IsInstanceOf<ActionCommand>(cmd);
        }

        [Test]
        public void CommandFactory_CreateCommand_SkillCommand()
        {
            var factory = new CommandFactory();
            var cmd = factory.CreateCommand(SkillCommand.TYPE_ID);

            Assert.IsNotNull(cmd);
            Assert.IsInstanceOf<SkillCommand>(cmd);
        }

        [Test]
        public void CommandFactory_DeserializeCommand_WorksCorrectly()
        {
            var factory = new CommandFactory();

            var original = new MoveCommand(1, 100, new FPVector3(FP64.FromRaw(500L), FP64.FromRaw(600L), FP64.FromRaw(700L)));
            int size = original.GetSerializedSize();
            var buf = new byte[4 + size]; // length prefix + data
            var writer = new SpanWriter(buf);
            writer.WriteInt32(size);
            original.Serialize(ref writer);

            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            var restored = factory.DeserializeCommand(ref reader);

            Assert.IsNotNull(restored);
            Assert.IsInstanceOf<MoveCommand>(restored);

            var moveCmd = (MoveCommand)restored;
            Assert.AreEqual(original.PlayerId, moveCmd.PlayerId);
            Assert.AreEqual(original.Tick, moveCmd.Tick);
            Assert.AreEqual(original.Target, moveCmd.Target);
        }

        #endregion

        #region Determinism Tests

        [Test]
        public void Serialization_IsDeterministic()
        {
            var cmd = new MoveCommand(1, 100, new FPVector3(FP64.FromRaw(12345L), FP64.FromRaw(67890L), FP64.FromRaw(11111L)));

            int size = cmd.GetSerializedSize();
            var buf1 = new byte[size];
            var w1 = new SpanWriter(buf1);
            cmd.Serialize(ref w1);

            var buf2 = new byte[size];
            var w2 = new SpanWriter(buf2);
            cmd.Serialize(ref w2);

            Assert.AreEqual(w1.Position, w2.Position);
            for (int i = 0; i < w1.Position; i++)
            {
                Assert.AreEqual(buf1[i], buf2[i], $"Byte {i} differs");
            }
        }

        [Test]
        public void MultipleSerializeDeserialize_PreservesData()
        {
            var original = new ActionCommand(5, 200, 10, 20)
            {
                Position = new FPVector3(FP64.FromRaw(999L), FP64.FromRaw(888L), FP64.FromRaw(777L))
            };

            // First round-trip
            int size = original.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var temp = new ActionCommand();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            temp.Deserialize(ref reader);

            // Second round-trip
            var buf2 = new byte[size];
            var w2 = new SpanWriter(buf2);
            temp.Serialize(ref w2);
            var final_ = new ActionCommand();
            var r2 = new SpanReader(new ReadOnlySpan<byte>(buf2, 0, w2.Position));
            final_.Deserialize(ref r2);

            Assert.AreEqual(original.PlayerId, final_.PlayerId);
            Assert.AreEqual(original.ActionId, final_.ActionId);
            Assert.AreEqual(original.Position, final_.Position);
        }

        #endregion
    }
}
