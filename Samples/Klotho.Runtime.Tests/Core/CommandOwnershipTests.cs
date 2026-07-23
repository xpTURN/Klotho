using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD verified batch command lifetime/ownership unification.
    ///
    /// Pins the three engine-side mechanisms that make the queue's CommandPool returns
    /// provably single-owner:
    ///   (1) Initialize re-entry guard — a second Initialize without Stop throws instead of
    ///       silently double-subscribing network handlers (the double-dispatch
    ///       precondition); Stop() resets the guard.
    ///   (2) Same-tick duplicate enqueue guard — a VerifiedState arrival whose tick is already
    ///       queued is discarded before copying, so no two queued containers share instances.
    ///   (3) DrainPendingVerifiedQueue — returns each entry's ICommand instances and ListPool
    ///       container (restores the original intent).
    ///
    /// Drives HandleVerifiedStateReceived / DrainPendingVerifiedQueue via reflection, same
    /// pattern as EventDispatchSDClientTests (a full engine.Update loop would require a
    /// complete IServerDrivenNetworkService + handshake setup).
    /// </summary>
    [TestFixture]
    public class CommandOwnershipTests
    {
        private static readonly Type _engineType = typeof(KlothoEngine);

        private static readonly FieldInfo _stateField =
            _engineType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _pendingVerifiedQueueField =
            _engineType.GetField("_pendingVerifiedQueue", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _handleVerifiedStateReceivedMethod =
            _engineType.GetMethod("HandleVerifiedStateReceived",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _drainPendingVerifiedQueueMethod =
            _engineType.GetMethod("DrainPendingVerifiedQueue",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("CommandOwnershipTests");
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static SimulationConfig MakeSDClientConfig()
            => new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
            };

        private static EcsSimulation MakeSim()
            => new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);

        private KlothoEngine MakeRunningSDClientEngine()
        {
            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(MakeSim(), _logger);
            _stateField.SetValue(engine, KlothoState.Running);
            return engine;
        }

        private static void EnqueueVerifiedEntry(
            KlothoEngine engine, int tick, IReadOnlyList<ICommand> commands, long stateHash)
            => _handleVerifiedStateReceivedMethod.Invoke(engine, new object[] { tick, commands, stateHash });

        private static int PendingVerifiedQueueCount(KlothoEngine engine)
            => ((ICollection)_pendingVerifiedQueueField.GetValue(engine)).Count;

        private static void InvokeDrain(KlothoEngine engine)
            => _drainPendingVerifiedQueueMethod.Invoke(engine, Array.Empty<object>());

        // ── (1) Initialize re-entry guard ───────────────────────────────

        [Test]
        public void Initialize_Twice_WithoutStop_Throws()
        {
            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(MakeSim(), _logger);

            Assert.Throws<InvalidOperationException>(
                () => engine.Initialize(MakeSim(), _logger),
                "A second Initialize without Stop must throw — it would double-subscribe handlers");
        }

        [Test]
        public void Initialize_AfterStop_PassesGuard()
        {
            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(MakeSim(), _logger);

            engine.Stop();

            // Guard-level check only: passing means re-subscription is clean. Engine reuse
            // (Stop -> Initialize -> Start) is unsupported — session state is not reset.
            Assert.DoesNotThrow(() => engine.Initialize(MakeSim(), _logger));
        }

        // ── (2) Same-tick duplicate enqueue guard ──────────────────

        [Test]
        public void VerifiedState_SameTickTwice_EnqueuesOnce()
        {
            var engine = MakeRunningSDClientEngine();

            EnqueueVerifiedEntry(engine, tick: 5, new List<ICommand> { new EmptyCommand(0, 4) }, stateHash: 1);
            EnqueueVerifiedEntry(engine, tick: 5, new List<ICommand> { new EmptyCommand(0, 4) }, stateHash: 1);

            Assert.AreEqual(1, PendingVerifiedQueueCount(engine),
                "A duplicate same-tick VerifiedState (double-dispatch shape) must be discarded at enqueue");
        }

        [Test]
        public void VerifiedState_MonotonicTicks_AllEnqueue()
        {
            var engine = MakeRunningSDClientEngine();

            EnqueueVerifiedEntry(engine, tick: 5, new List<ICommand>(), stateHash: 1);
            EnqueueVerifiedEntry(engine, tick: 6, new List<ICommand>(), stateHash: 2);
            EnqueueVerifiedEntry(engine, tick: 7, new List<ICommand>(), stateHash: 3);

            Assert.AreEqual(3, PendingVerifiedQueueCount(engine),
                "Strictly increasing ticks (the ReliableOrdered normal path) must all enqueue");
        }

        // ── (3) Drain returns instances + containers ────────────────────

        // Rents from CommandPool instead of `new` — the ownership diagnostic makes
        // Return skip non-pool-origin instances, so pooled-count assertions require
        // pool-rented commands.
        private static EmptyCommand RentEmpty(int playerId, int tick)
        {
            var cmd = CommandPool.Get<EmptyCommand>();
            cmd.PlayerId = playerId;
            cmd.Tick = tick;
            return cmd;
        }

        [Test]
        public void Drain_ReturnsQueuedCommandInstancesToPool()
        {
            CommandPool.ClearAll();
            var engine = MakeRunningSDClientEngine();

            // 2 + 3 commands across two entries — all pre-store (never entered the InputBuffer),
            // so the queue is their sole owner and the drain must return every one.
            EnqueueVerifiedEntry(engine, tick: 5,
                new List<ICommand> { RentEmpty(0, 4), RentEmpty(1, 4) }, stateHash: 1);
            EnqueueVerifiedEntry(engine, tick: 6,
                new List<ICommand> { RentEmpty(0, 5), RentEmpty(1, 5), RentEmpty(2, 5) }, stateHash: 2);
            Assert.AreEqual(2, PendingVerifiedQueueCount(engine));

            int pooledBefore = CommandPool.GetTotalPooledCount();
            InvokeDrain(engine);

            Assert.AreEqual(0, PendingVerifiedQueueCount(engine), "Drain must empty the queue");
            Assert.AreEqual(pooledBefore + 5, CommandPool.GetTotalPooledCount(),
                "Drain must return every queued ICommand instance to CommandPool (intent restored)");
        }
    }
}
