using System.Collections.Generic;

using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke for the simulation core: component storage on the flat
    /// byte heap, the tick loop, the rollback ring, and full-state serialization.
    ///
    /// This is also the coverage for the package's internal pointer helpers (<c>KUnsafe</c>), which
    /// take a Unity-specific branch — outside .NET 8 they reinterpret through <c>fixed</c> pointers
    /// instead of the BCL <c>Unsafe</c> intrinsics, relying on Unity's non-moving Boehm GC. Nothing
    /// else in the suite exercises that branch's correctness at scale; a broken reinterpret shows up
    /// here as a wrong state hash or a failed round-trip rather than as a compile error.
    /// </summary>
    [TestFixture]
    public class EcsSimulationSmokeTests
    {
        // One value for the whole fixture: the component layout is process-global and freezes on the
        // first Frame construction (the editor relaxes a conflicting re-freeze, but keeping it uniform
        // means the layout under test is the layout the tests reason about).
        private const int MaxEntities = 64;

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Warning floor: Info/Debug would flood the editor console, while warnings and errors
            // from the engine still surface (an unexpected LogError fails the test).
            var factory = KLoggerFactory.Create(logging => logging
                .SetMinimumLevel(KLogLevel.Warning)
                .AddUnityDebug());
            _logger = factory.CreateLogger("Unity2022Smoke");
        }

        // Advances every probe by a fixed deterministic delta — enough for the state hash to move
        // every tick without depending on any game content.
        private sealed class SmokeAdvanceSystem : ISystem
        {
            public void Update(ref Frame frame)
            {
                var filter = frame.Filter<SmokeProbeComponent>();
                while (filter.Next(out var entity))
                {
                    ref var probe = ref frame.Get<SmokeProbeComponent>(entity);
                    probe.Counter++;
                    probe.Value += FP64.FromDouble(0.25);
                    probe.Position += new FPVector3(FP64.Zero, FP64.Zero, FP64.FromDouble(0.5));
                }
            }
        }

        private EcsSimulation CreateSimulation()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50, logger: _logger);
            sim.AddSystem(new SmokeAdvanceSystem(), SystemPhase.Update);
            sim.LockAssetRegistry();
            sim.Initialize();
            return sim;
        }

        private static void SeedProbes(EcsSimulation sim, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var entity = sim.Frame.CreateEntity();
                sim.Frame.Add(entity, new SmokeProbeComponent
                {
                    Counter = i,
                    Flag = (i % 2) == 0,
                    Value = FP64.FromInt(i),
                    Position = new FPVector3(i, 0, 0),
                });
            }
        }

        [Test]
        public void Frame_ComponentStorageAddGetRemove()
        {
            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();

            Assert.IsFalse(frame.Has<SmokeProbeComponent>(entity));

            frame.Add(entity, new SmokeProbeComponent { Counter = 7, Value = FP64.FromInt(3) });
            Assert.IsTrue(frame.Has<SmokeProbeComponent>(entity));
            Assert.AreEqual(7, frame.Get<SmokeProbeComponent>(entity).Counter);

            // ref-return write-through: the storage must be mutated in place, not a copy.
            frame.Get<SmokeProbeComponent>(entity).Counter = 11;
            Assert.AreEqual(11, frame.Get<SmokeProbeComponent>(entity).Counter);

            frame.Remove<SmokeProbeComponent>(entity);
            Assert.IsFalse(frame.Has<SmokeProbeComponent>(entity));
        }

        [Test]
        public void Frame_SingletonComponentResolvesFromAnyEntity()
        {
            var frame = new Frame(MaxEntities, _logger);
            var entity = frame.CreateEntity();

            frame.Add(entity, new SmokeSingletonComponent { Value = 99 });

            Assert.IsTrue(frame.TryGetSingleton<SmokeSingletonComponent>(out var owner));
            Assert.AreEqual(entity, owner);
            Assert.AreEqual(99, frame.GetSingleton<SmokeSingletonComponent>().Value);
        }

        [Test]
        public void Simulation_TickAdvancesStateAndHash()
        {
            var sim = CreateSimulation();
            SeedProbes(sim, 4);

            long hashBefore = sim.GetStateHash();
            sim.Tick(NoCommands);

            Assert.AreEqual(1, sim.Frame.Tick);
            Assert.AreNotEqual(hashBefore, sim.GetStateHash(), "a tick that mutated components must change the state hash");
        }

        [Test]
        public void Simulation_TwoInstancesStayBitIdentical()
        {
            var a = CreateSimulation();
            var b = CreateSimulation();
            SeedProbes(a, 4);
            SeedProbes(b, 4);

            for (int tick = 0; tick < 20; tick++)
            {
                a.Tick(NoCommands);
                b.Tick(NoCommands);
                Assert.AreEqual(a.GetStateHash(), b.GetStateHash(), $"state hash diverged at tick {tick}");
            }
        }

        [Test]
        public void Simulation_RollbackAndResimReproducesHash()
        {
            var sim = CreateSimulation();
            SeedProbes(sim, 4);

            // Ring holds pre-tick snapshots: snapshot, then tick.
            for (int i = 0; i < 5; i++)
            {
                sim.SaveSnapshot();
                sim.Tick(NoCommands);
            }

            long hashAtTick5 = sim.GetStateHash();
            Assert.AreEqual(5, sim.Frame.Tick);

            Assert.IsTrue(sim.HasSnapshot(2), "tick 2 must still be in the rollback ring");
            sim.Rollback(2);
            Assert.AreEqual(2, sim.Frame.Tick);
            Assert.AreNotEqual(hashAtTick5, sim.GetStateHash(), "rollback must actually rewind state");

            for (int i = 0; i < 3; i++)
            {
                sim.SaveSnapshot();
                sim.Tick(NoCommands);
            }

            Assert.AreEqual(5, sim.Frame.Tick);
            Assert.AreEqual(hashAtTick5, sim.GetStateHash(), "re-simulation must reproduce the original hash");
        }

        [Test]
        public void Simulation_FullStateRoundTripsIntoAFreshInstance()
        {
            var source = CreateSimulation();
            SeedProbes(source, 4);
            for (int i = 0; i < 7; i++) source.Tick(NoCommands);

            byte[] fullState = source.SerializeFullState();
            Assert.IsNotNull(fullState);
            Assert.Greater(fullState.Length, 0);

            var target = CreateSimulation();
            target.RestoreFromFullState(fullState);

            Assert.AreEqual(source.GetStateHash(), target.GetStateHash(),
                "restored state hash must equal the source's");

            // Spot-check the payload actually carried values, not just an equal-length buffer:
            // the four probes were seeded 0..3 and each advanced 7 ticks.
            var restoredCounters = new List<int>();
            var probes = target.Frame.Filter<SmokeProbeComponent>();
            while (probes.Next(out var entity))
                restoredCounters.Add(target.Frame.GetReadOnly<SmokeProbeComponent>(entity).Counter);

            restoredCounters.Sort();
            Assert.AreEqual(new[] { 7, 8, 9, 10 }, restoredCounters.ToArray(), "restored probe counters");
        }
    }
}
