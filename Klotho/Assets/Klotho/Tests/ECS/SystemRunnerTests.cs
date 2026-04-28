using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

namespace xpTURN.Klotho.ECS.Tests
{
    [TestFixture]
    public class SystemRunnerTests
    {
        private const int MaxEntities = 16;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory configuration (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        // --- Test helpers ---

        private class RecordingSystem : ISystem, IInitSystem, IDestroySystem
        {
            public string Name;
            public List<string> Log;

            public void OnInit(ref Frame frame) => Log.Add($"{Name}:Init");
            public void Update(ref Frame frame) => Log.Add($"{Name}:Update");
            public void OnDestroy(ref Frame frame) => Log.Add($"{Name}:Destroy");
        }

        private class TestCommandSystem : ICommandSystem
        {
            public string Name;
            public List<string> Log;

            public void OnCommand(ref Frame frame, ICommand command)
            {
                Log.Add($"{Name}:Command:{command.CommandTypeId}");
            }
        }

        private class TestCommand : ICommand
        {
            public int PlayerId { get; set; }
            public int Tick { get; set; }
            public int CommandTypeId { get; set; }

            public void Serialize(ref Serialization.SpanWriter writer) { }
            public void Deserialize(ref Serialization.SpanReader reader) { }
            public int GetSerializedSize() => 0;
        }

        private struct TestComponent : IComponent
        {
            public int Value;
            public int GetSerializedSize() => 4;
            public void Serialize(ref xpTURN.Klotho.Serialization.SpanWriter writer) { }
            public void Deserialize(ref xpTURN.Klotho.Serialization.SpanReader reader) { }
            public ulong GetHash(ulong hash) => hash;
        }

        private class ComponentAddedReceiver : ISystem, ISignalOnComponentAdded<TestComponent>
        {
            public List<int> Received = new List<int>();
            public void Update(ref Frame frame) { }
            public void OnAdded(ref Frame frame, EntityRef entity, ref TestComponent component)
                => Received.Add(component.Value);
        }

        private class ComponentRemovedReceiver : ISystem, ISignalOnComponentRemoved<TestComponent>
        {
            public List<int> Received = new List<int>();
            public void Update(ref Frame frame) { }
            public void OnRemoved(ref Frame frame, EntityRef entity, TestComponent component)
                => Received.Add(component.Value);
        }

        private class EntityLifecycleSystem : IEntityCreatedSystem, IEntityDestroyedSystem
        {
            public List<string> Log = new List<string>();
            public void OnEntityCreated(ref Frame frame, EntityRef entity) => Log.Add($"Created:{entity.Index}");
            public void OnEntityDestroyed(ref Frame frame, EntityRef entity) => Log.Add($"Destroyed:{entity.Index}");
        }

        // --- Registration order ---

        [Test]
        public void AddSystem_PreservesRegistrationOrder_WithinSamePhase()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new RecordingSystem { Name = "A", Log = log }, SystemPhase.Update);
            runner.AddSystem(new RecordingSystem { Name = "B", Log = log }, SystemPhase.Update);
            runner.AddSystem(new RecordingSystem { Name = "C", Log = log }, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            runner.RunUpdateSystems(ref frame);

            Assert.AreEqual(new[] { "A:Update", "B:Update", "C:Update" }, log.ToArray());
        }

        // --- Phase-based execution order ---

        [Test]
        public void RunUpdateSystems_ExecutesInPhaseOrder()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new RecordingSystem { Name = "Late", Log = log }, SystemPhase.LateUpdate);
            runner.AddSystem(new RecordingSystem { Name = "Pre", Log = log }, SystemPhase.PreUpdate);
            runner.AddSystem(new RecordingSystem { Name = "Post", Log = log }, SystemPhase.PostUpdate);
            runner.AddSystem(new RecordingSystem { Name = "Upd", Log = log }, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            runner.RunUpdateSystems(ref frame);

            Assert.AreEqual(new[] { "Pre:Update", "Upd:Update", "Post:Update", "Late:Update" }, log.ToArray());
        }

        [Test]
        public void RunUpdateSystems_MixedPhaseAndOrder()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new RecordingSystem { Name = "U2", Log = log }, SystemPhase.Update);
            runner.AddSystem(new RecordingSystem { Name = "Pre1", Log = log }, SystemPhase.PreUpdate);
            runner.AddSystem(new RecordingSystem { Name = "U1", Log = log }, SystemPhase.Update);
            runner.AddSystem(new RecordingSystem { Name = "Post1", Log = log }, SystemPhase.PostUpdate);

            var frame = new Frame(MaxEntities, _logger);
            runner.RunUpdateSystems(ref frame);

            Assert.AreEqual(new[] { "Pre1:Update", "U2:Update", "U1:Update", "Post1:Update" }, log.ToArray());
        }

        // --- Init / Destroy ---

        [Test]
        public void Init_CallsOnInit_InPhaseOrder()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new RecordingSystem { Name = "B", Log = log }, SystemPhase.Update);
            runner.AddSystem(new RecordingSystem { Name = "A", Log = log }, SystemPhase.PreUpdate);

            var frame = new Frame(MaxEntities, _logger);
            runner.Init(ref frame);

            Assert.AreEqual(new[] { "A:Init", "B:Init" }, log.ToArray());
        }

        [Test]
        public void Destroy_CallsOnDestroy_InPhaseOrder()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new RecordingSystem { Name = "B", Log = log }, SystemPhase.PostUpdate);
            runner.AddSystem(new RecordingSystem { Name = "A", Log = log }, SystemPhase.PreUpdate);

            var frame = new Frame(MaxEntities, _logger);
            runner.Destroy(ref frame);

            Assert.AreEqual(new[] { "A:Destroy", "B:Destroy" }, log.ToArray());
        }

        // --- Command systems ---

        [Test]
        public void RunCommandSystems_RoutesToCommandSystems()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new TestCommandSystem { Name = "Cmd1", Log = log }, SystemPhase.PreUpdate);
            runner.AddSystem(new TestCommandSystem { Name = "Cmd2", Log = log }, SystemPhase.PreUpdate);
            runner.AddSystem(new RecordingSystem { Name = "NoCmd", Log = log }, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            runner.RunCommandSystems(ref frame, new TestCommand { CommandTypeId = 42 });

            Assert.AreEqual(new[] { "Cmd1:Command:42", "Cmd2:Command:42" }, log.ToArray());
        }

        // --- Signal routing ---

        [Test]
        public void OnComponentAdded_RoutesToImplementingSystems()
        {
            var runner = new SystemRunner();
            var receiver1 = new ComponentAddedReceiver();
            var receiver2 = new ComponentAddedReceiver();

            runner.AddSystem(receiver1, SystemPhase.Update);
            runner.AddSystem(new RecordingSystem { Name = "X", Log = new List<string>() }, SystemPhase.Update);
            runner.AddSystem(receiver2, SystemPhase.PostUpdate);

            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            var component = new TestComponent { Value = 25 };
            runner.OnComponentAdded(ref frame, entity, ref component);

            Assert.AreEqual(new[] { 25 }, receiver1.Received.ToArray());
            Assert.AreEqual(new[] { 25 }, receiver2.Received.ToArray());
        }

        [Test]
        public void OnComponentAdded_NoReceivers_DoesNotThrow()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new RecordingSystem { Name = "X", Log = new List<string>() }, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            var component = new TestComponent { Value = 10 };
            Assert.DoesNotThrow(() => runner.OnComponentAdded(ref frame, entity, ref component));
        }

        [Test]
        public void OnComponentRemoved_RoutesToImplementingSystems()
        {
            var runner = new SystemRunner();
            var receiver = new ComponentRemovedReceiver();
            runner.AddSystem(receiver, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            var component = new TestComponent { Value = 42 };
            runner.OnComponentRemoved(ref frame, entity, component);

            Assert.AreEqual(new[] { 42 }, receiver.Received.ToArray());
        }

        // --- Entity lifecycle ---

        [Test]
        public void OnEntityCreated_NotifiesSystems()
        {
            var runner = new SystemRunner();
            var sys = new EntityLifecycleSystem();
            runner.AddSystem(sys, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            runner.OnEntityCreated(ref frame, entity);

            Assert.AreEqual(1, sys.Log.Count);
            Assert.AreEqual($"Created:{entity.Index}", sys.Log[0]);
        }

        [Test]
        public void OnEntityDestroyed_NotifiesSystems()
        {
            var runner = new SystemRunner();
            var sys = new EntityLifecycleSystem();
            runner.AddSystem(sys, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            runner.OnEntityDestroyed(ref frame, entity);

            Assert.AreEqual(1, sys.Log.Count);
            Assert.AreEqual($"Destroyed:{entity.Index}", sys.Log[0]);
        }

        // --- Edge cases ---

        [Test]
        public void EmptyRunner_DoesNotThrow()
        {
            var runner = new SystemRunner();
            var frame = new Frame(MaxEntities, _logger);

            Assert.DoesNotThrow(() => runner.RunUpdateSystems(ref frame));
            Assert.DoesNotThrow(() => runner.RunCommandSystems(ref frame, new TestCommand { CommandTypeId = 1 }));
            Assert.DoesNotThrow(() => runner.Init(ref frame));
            Assert.DoesNotThrow(() => runner.Destroy(ref frame));
        }

        [Test]
        public void AddSystem_NullSystem_Throws()
        {
            var runner = new SystemRunner();
            Assert.Throws<System.ArgumentNullException>(() => runner.AddSystem(null, SystemPhase.Update));
        }

        [Test]
        public void RunCommandSystems_CrossPhaseOrdering()
        {
            var log = new List<string>();
            var runner = new SystemRunner();

            runner.AddSystem(new TestCommandSystem { Name = "Post", Log = log }, SystemPhase.PostUpdate);
            runner.AddSystem(new TestCommandSystem { Name = "Pre", Log = log }, SystemPhase.PreUpdate);
            runner.AddSystem(new TestCommandSystem { Name = "Upd", Log = log }, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            runner.RunCommandSystems(ref frame, new TestCommand { CommandTypeId = 7 });

            Assert.AreEqual(new[] { "Pre:Command:7", "Upd:Command:7", "Post:Command:7" }, log.ToArray());
        }

        // --- Multiple signal types ---

        private class MultiSignalReceiver : ISystem, ISignalOnComponentAdded<TestComponent>, ISignalOnComponentRemoved<TestComponent>
        {
            public List<string> Log = new List<string>();
            public void Update(ref Frame frame) { }
            public void OnAdded(ref Frame frame, EntityRef entity, ref TestComponent component) => Log.Add($"Added:{component.Value}");
            public void OnRemoved(ref Frame frame, EntityRef entity, TestComponent component) => Log.Add($"Removed:{component.Value}");
        }

        [Test]
        public void Signals_MultipleSignalTypes_RoutedIndependently()
        {
            var runner = new SystemRunner();
            var receiver = new MultiSignalReceiver();
            runner.AddSystem(receiver, SystemPhase.Update);

            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();
            var component = new TestComponent { Value = 10 };
            runner.OnComponentAdded(ref frame, entity, ref component);
            runner.OnComponentRemoved(ref frame, entity, new TestComponent { Value = 25 });

            Assert.AreEqual(2, receiver.Log.Count);
            Assert.AreEqual("Added:10", receiver.Log[0]);
            Assert.AreEqual("Removed:25", receiver.Log[1]);
        }
    }
}
