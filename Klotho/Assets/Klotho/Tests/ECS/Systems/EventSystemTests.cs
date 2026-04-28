using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS.Systems.Tests
{
    [TestFixture]
    public class EventSystemTests
    {
        private const int MaxEntities = 32;

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

        private Frame CreateFrame(int tick = 1)
        {
            var frame = new Frame(MaxEntities, _logger);
            frame.Tick = tick;
            frame.DeltaTimeMs = 50;
            return frame;
        }

        // --- Stub event raiser ---

        private class StubRaiser : ISimulationEventRaiser
        {
            public readonly List<SimulationEvent> Raised = new List<SimulationEvent>();

            public void RaiseEvent(SimulationEvent evt)
            {
                Raised.Add(evt);
            }
        }

        // --- Tests ---

        private Frame CreateFrameWithRaiser(ISimulationEventRaiser raiser, int tick = 1)
        {
            var frame = CreateFrame(tick);
            frame.EventRaiser = raiser;
            return frame;
        }

        [Test]
        public void NoEvents_UpdateDoesNotThrow()
        {
            var raiser = new StubRaiser();
            var system = new EventSystem();
            var frame = CreateFrameWithRaiser(raiser);

            Assert.DoesNotThrow(() => system.Update(ref frame));
            Assert.AreEqual(0, raiser.Raised.Count);
        }

        [Test]
        public void EnqueuedEvent_IsRaisedOnUpdate()
        {
            var raiser = new StubRaiser();
            var system = new EventSystem();
            var frame = CreateFrameWithRaiser(raiser);

            system.Enqueue(new DamageEvent { SourceEntityId = 1, TargetEntityId = 2, DamageAmount = 10 });
            system.Update(ref frame);

            Assert.AreEqual(1, raiser.Raised.Count);
            var evt = raiser.Raised[0] as DamageEvent;
            Assert.IsNotNull(evt);
            Assert.AreEqual(10, evt.DamageAmount);
        }

        [Test]
        public void MultipleEvents_RaisedInEnqueueOrder()
        {
            var raiser = new StubRaiser();
            var system = new EventSystem();
            var frame = CreateFrameWithRaiser(raiser);

            system.Enqueue(new DamageEvent { DamageAmount = 5 });
            system.Enqueue(new SpawnEvent { EntityId = 42 });
            system.Enqueue(new DeathEvent { EntityId = 7 });
            system.Update(ref frame);

            Assert.AreEqual(3, raiser.Raised.Count);
            Assert.IsInstanceOf<DamageEvent>(raiser.Raised[0]);
            Assert.IsInstanceOf<SpawnEvent>(raiser.Raised[1]);
            Assert.IsInstanceOf<DeathEvent>(raiser.Raised[2]);
        }

        [Test]
        public void QueueClearedAfterUpdate()
        {
            var raiser = new StubRaiser();
            var system = new EventSystem();
            var frame = CreateFrameWithRaiser(raiser);

            system.Enqueue(new DamageEvent { DamageAmount = 1 });
            system.Update(ref frame);

            raiser.Raised.Clear();
            system.Update(ref frame);

            Assert.AreEqual(0, raiser.Raised.Count, "Queue should be empty after Update");
        }

        [Test]
        public void EnqueueAfterUpdate_RaisedInNextUpdate()
        {
            var raiser = new StubRaiser();
            var system = new EventSystem();
            var frame = CreateFrameWithRaiser(raiser);

            system.Enqueue(new DamageEvent { DamageAmount = 1 });
            system.Update(ref frame);

            raiser.Raised.Clear();
            system.Enqueue(new SpawnEvent { EntityId = 99 });
            system.Update(ref frame);

            Assert.AreEqual(1, raiser.Raised.Count);
            var evt = raiser.Raised[0] as SpawnEvent;
            Assert.IsNotNull(evt);
            Assert.AreEqual(99, evt.EntityId);
        }

        [Test]
        public void EventCollector_TickIsSetByRaiser()
        {
            var collector = new EventCollector();
            collector.BeginTick(tick: 5);
            var system = new EventSystem();
            var frame = CreateFrameWithRaiser(collector, tick: 5);

            system.Enqueue(new DamageEvent { DamageAmount = 20 });
            system.Update(ref frame);

            Assert.AreEqual(1, collector.Count);
            Assert.AreEqual(5, collector.Collected[0].Tick, "EventCollector should stamp the event's Tick");
        }

        [Test]
        public void Determinism_SameInputEvents_SameOrder()
        {
            var raiser1 = new StubRaiser();
            var raiser2 = new StubRaiser();
            var system1 = new EventSystem();
            var system2 = new EventSystem();
            var frame1 = CreateFrameWithRaiser(raiser1);
            var frame2 = CreateFrameWithRaiser(raiser2);

            system1.Enqueue(new DamageEvent { SourceEntityId = 1, TargetEntityId = 2, DamageAmount = 10 });
            system1.Enqueue(new SpawnEvent { EntityId = 3, EntityTypeId = 1, OwnerId = 1 });

            system2.Enqueue(new DamageEvent { SourceEntityId = 1, TargetEntityId = 2, DamageAmount = 10 });
            system2.Enqueue(new SpawnEvent { EntityId = 3, EntityTypeId = 1, OwnerId = 1 });

            system1.Update(ref frame1);
            system2.Update(ref frame2);

            Assert.AreEqual(raiser1.Raised.Count, raiser2.Raised.Count);
            for (int i = 0; i < raiser1.Raised.Count; i++)
            {
                Assert.AreEqual(
                    raiser1.Raised[i].GetContentHash(),
                    raiser2.Raised[i].GetContentHash(),
                    $"Event[{i}] content hash must match");
            }
        }
    }
}
