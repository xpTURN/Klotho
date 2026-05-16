#pragma warning disable CS0067 // Events on the mock service are required by the interface but never raised in tests.
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using Codice.CM.Common.Tree;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD-Client verified batch resimulation path
    /// (KlothoEngine.ServerDrivenClient.cs ProcessVerifiedBatchCore, lines 249-393).
    ///
    /// Outer-loop pattern: backs up predicted events in a range (lines 244-250), rolls back
    /// to the nearest verified snapshot (line 282), then for each verified entry resimulates
    /// the tick, validates against the server state hash, and dispatches buffered Synced events
    /// inline on promotion-to-verified (lines 444-451). After all verified entries promote,
    /// prediction resim runs (lines 463-512), then DiffRollbackEvents reconciles Regular events.
    ///
    /// The SD-Client UpdateServerDrivenClient loop is bypassed — the test wires
    /// ProcessVerifiedBatch directly via reflection because driving it through
    /// engine.Update would require a full IServerDrivenNetworkService + AdaptiveClock
    /// + handshake setup. Direct invocation exercises the same code path with a minimal
    /// surface (only the happy-path entries: snapshot resolution + hash match).
    ///
    /// Asserts:
    ///   (1) OnSyncedEvent dispatched exactly once on resim promotion-to-verified.
    ///   (2) evt.Tick (stamped by BeginTick) equals the dispatch callback tick — tick-argument
    ///       invariant across resim.
    ///   (3) Regular event diff cascade — OnEventCanceled for old-only (variant 1 from
    ///       prediction), OnEventConfirmed for new-only (variant 2 from resim).
    /// </summary>
    [TestFixture]
    public class EventDispatchSDClientTests
    {
        // ── Shared test events ───────────────────────────────────────────

        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_401;
            public override EventMode Mode => EventMode.Synced;
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        private sealed class TestRegularEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_402;
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        // Custom ECS system — raises events from inside the ECS Tick at a fixed frame tick.
        private sealed class EventRaiserSystem : ISystem
        {
            public int RaiseAtTick = -1;
            public Func<SimulationEvent> Factory;

            public void Update(ref Frame frame)
            {
                if (frame.Tick != RaiseAtTick) return;
                if (frame.EventRaiser == null) return;
                if (Factory == null) return;
                frame.EventRaiser.RaiseEvent(Factory());
            }
        }

        // ── Reflection handles ──────────────────────────────────────────

        private static readonly Type _engineType = typeof(KlothoEngine);

        private static readonly FieldInfo _dispatcherField =
            _engineType.GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _stateField =
            _engineType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _executeClientPredictionTickMethod =
            _engineType.GetMethod("ExecuteClientPredictionTick",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _processVerifiedBatchMethod =
            _engineType.GetMethod("ProcessVerifiedBatch",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _handleVerifiedStateReceivedMethod =
            _engineType.GetMethod("HandleVerifiedStateReceived",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _eventBufferField =
            _engineType.GetField("_eventBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _serverDrivenNetworkField =
            _engineType.GetField("_serverDrivenNetwork", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _handleServerDrivenFullStateReceivedMethod =
            _engineType.GetMethod("HandleServerDrivenFullStateReceived",
                BindingFlags.NonPublic | BindingFlags.Instance);

        // Direct invocation of the Synced batch dispatch helper to simulate the
        // chain-advance dispatch path (TryAdvanceVerifiedChain → DispatchSyncedEventsForTick)
        // without setting up full chain-advance prerequisites (input buffer, _activePlayerIds).
        private static readonly MethodInfo _dispatchSyncedEventsForTickMethod =
            _engineType.GetMethod("DispatchSyncedEventsForTick",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static void InvokeDispatchSyncedEventsForTick(KlothoEngine engine, int tick,
                                                              System.Collections.Generic.IReadOnlyList<SimulationEvent> events)
            => _dispatchSyncedEventsForTickMethod.Invoke(engine, new object[] { tick, events });

        // Minimal IServerDrivenNetworkService stub — only ClearUnackedInputs is touched by the
        // Reconnect/Resync FullState path (line 706). Everything else throws / returns defaults
        // so an unintended caller surfaces immediately rather than silently no-op.
        private sealed class MockSDNetworkService : IServerDrivenNetworkService
        {
            public SessionPhase Phase => SessionPhase.Playing;
            private SharedTimeClock _sharedClock = new SharedTimeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            public SharedTimeClock SharedClock => _sharedClock;
            public int PlayerCount => 1;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady => true;
            public int LocalPlayerId => 0;
            public bool IsHost => false;
            public int RandomSeed => 42;
            public IReadOnlyList<IPlayerInfo> Players => Array.Empty<IPlayerInfo>();
            public bool IsServer => false;

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int> OnFrameAdvantageReceived;
            public event Action<int> OnLocalPlayerIdAssigned;
            public event Action<int, int> OnFullStateRequested;
            public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
            public event Action<IPlayerInfo> OnPlayerDisconnected;
            public event Action<IPlayerInfo> OnPlayerReconnected;
            public event Action OnReconnecting;
            public event Action<string> OnReconnectFailed;
            public event Action OnReconnected;
            public event Action<int, int> OnLateJoinPlayerAdded;
            public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
            public event Action<int> OnInputAckReceived;
            public event Action<int, byte[], long> OnServerFullStateReceived;
            public event Action<int, long> OnBootstrapBegin;
            public event Action<int, int, RejectionReason> OnCommandRejected;

            public void Initialize(INetworkTransport t, ICommandFactory f, ILogger l) { }
            public void CreateRoom(string n, int m) { }
            public void JoinRoom(string n) { }
            public void LeaveRoom() { }
            public void SetReady(bool r) { }
            public void SendCommand(ICommand c) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] data, long hash) { }
            public void BroadcastFullState(int tick, byte[] data, long hash, FullStateKind k = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase config) { }
            public void SendClientInput(int tick, ICommand command) { }
            public void SendBootstrapReady(int playerId) { }
            public int GetMinClientAckedTick() => 0;

            public int ClearUnackedInputsCallCount { get; private set; }
            public void ClearUnackedInputs() { ClearUnackedInputsCallCount++; }
        }

        private static void InjectServerDrivenNetwork(KlothoEngine engine, MockSDNetworkService stub)
            => _serverDrivenNetworkField.SetValue(engine, stub);

        private static void InvokeHandleServerDrivenFullStateReceived(
            KlothoEngine engine, int tick, byte[] data, long hash)
            => _handleServerDrivenFullStateReceivedMethod.Invoke(
                engine, new object[] { tick, data, hash });

        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("EventDispatchSDClientTests");
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static void InjectDispatcher(KlothoEngine engine, ILogger logger)
        {
            if (_dispatcherField.GetValue(engine) != null) return;
            _dispatcherField.SetValue(engine, new EventDispatcher(logger, warnMs: int.MaxValue));
        }

        private static void SetEngineState(KlothoEngine engine, KlothoState state)
            => _stateField.SetValue(engine, state);

        private static void DrivePredictedTick(KlothoEngine engine)
            => _executeClientPredictionTickMethod.Invoke(engine, Array.Empty<object>());

        private static void InvokeProcessVerifiedBatch(KlothoEngine engine)
            => _processVerifiedBatchMethod.Invoke(engine, Array.Empty<object>());

        // HandleVerifiedStateReceived enqueues a VerifiedStateEntry without the test having
        // to construct the private struct directly. Engine state required: State==Running,
        // not catching up / spectator (default), and tick > _lastVerifiedTick.
        private static void EnqueueVerifiedEntry(
            KlothoEngine engine, int tick, IReadOnlyList<ICommand> commands, long stateHash)
            => _handleVerifiedStateReceivedMethod.Invoke(engine, new object[] { tick, commands, stateHash });

        private static SimulationConfig MakeSDClientConfig()
            => new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
            };

        // Run prediction loop until CurrentTick == targetTick, capturing the simulation
        // state hash AFTER each Tick call. The returned array is indexed by post-Tick
        // frame.Tick (i.e., hashes[T] holds the hash after executing tick T-1).
        private static long[] DrivePredictionAndCaptureHashes(
            KlothoEngine engine, EcsSimulation sim, int targetTick)
        {
            var hashes = new long[targetTick + 1];
            for (int t = 0; t < targetTick; t++)
            {
                DrivePredictedTick(engine);
                hashes[t + 1] = sim.GetStateHash();
            }
            return hashes;
        }

        // ── Tests ───────────────────────────────────────────────────────

        [Test]
        public void SDClient_VerifiedBatchResim_SyncedEvent_DispatchAcrossResim_SingleFire()
        {
            const int raiseAtTick = 1;
            const int predictDepth = 3;     // predict ticks 0, 1, 2 → CurrentTick = 3 after.
            const int executionTick = raiseAtTick;
            const int entryTick = executionTick + 1;

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestSyncedEvent { Payload = 1 },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);

            int dispatchedCount = 0;
            int lastCallbackTick = -1;
            int lastEvtTick = -1;
            engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                {
                    dispatchedCount++;
                    lastCallbackTick = tick;
                    lastEvtTick = evt.Tick;
                }
            };

            var hashes = DrivePredictionAndCaptureHashes(engine, sim, predictDepth);

            Assert.AreEqual(0, dispatchedCount,
                "Synced events buffered during Predicted ticks must not dispatch before promotion-to-verified");

            // Enqueue one verified entry covering executionTick. entry.Tick is _frame.Tick
            // AFTER execution (= executionTick + 1). entry.StateHash must match the post-execution
            // hash captured during prediction — events are buffered separately from sim state,
            // so the same input → same hash regardless of event payload.
            EnqueueVerifiedEntry(engine, entryTick, new List<ICommand>(), hashes[entryTick]);

            InvokeProcessVerifiedBatch(engine);

            Assert.AreEqual(1, dispatchedCount,
                $"Synced event must dispatch exactly once on resim promotion-to-verified (SD-Client path). Got {dispatchedCount}. " +
                "If > 1, the inline dispatch at ServerDrivenClient.cs:444-451 fired multiple times " +
                "or DiffRollbackEvents did not skip Synced.");
            Assert.AreEqual(raiseAtTick, lastCallbackTick,
                "OnSyncedEvent callback tick must equal the tick at which the event was raised");
            Assert.AreEqual(raiseAtTick, lastEvtTick,
                "evt.Tick (BeginTick stamp) must match the dispatch callback tick — tick-argument invariant");
        }

        [Test]
        public void SDClient_VerifiedBatchResim_RegularEvent_DiffCascade_OldOnlyCancels_NewOnlyConfirms()
        {
            const int raiseAtTick = 1;
            const int predictDepth = 3;
            const int executionTick = raiseAtTick;
            const int entryTick = executionTick + 1;

            int variant = 1;
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestRegularEvent { Payload = variant },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);

            int predictedCount = 0;
            int confirmedCount = 0;
            int canceledCount = 0;
            engine.OnEventPredicted += (tick, evt) => { if (evt is TestRegularEvent) predictedCount++; };
            engine.OnEventConfirmed += (tick, evt) => { if (evt is TestRegularEvent) confirmedCount++; };
            engine.OnEventCanceled  += (tick, evt) => { if (evt is TestRegularEvent) canceledCount++; };

            var hashes = DrivePredictionAndCaptureHashes(engine, sim, predictDepth);

            Assert.GreaterOrEqual(predictedCount, 1,
                "Regular event must fire OnEventPredicted during Predicted tick execution");

            // Flip the factory so the resim of tick 1 produces a different content hash —
            // DiffRollbackEvents will then surface old-only (v1) as Canceled and new-only (v2)
            // as Confirmed.
            variant = 2;

            int baselineConfirmed = confirmedCount;
            int baselineCanceled = canceledCount;

            EnqueueVerifiedEntry(engine, entryTick, new List<ICommand>(), hashes[entryTick]);
            InvokeProcessVerifiedBatch(engine);

            int newCanceled = canceledCount - baselineCanceled;
            int newConfirmed = confirmedCount - baselineConfirmed;

            Assert.AreEqual(1, newCanceled,
                $"Old-only Regular event (variant 1) must be canceled exactly once via DiffRollbackEvents. Got {newCanceled}.");
            Assert.AreEqual(1, newConfirmed,
                $"New-only Regular event (variant 2) must be confirmed exactly once via DiffRollbackEvents on verified tick. Got {newConfirmed}.");
        }

        // ── (e) PredResim path (ServerDrivenClient.cs:463-512) ─────────
        //
        // The verified entry covers only an early execution tick, leaving an event-bearing
        // tick in the prediction-resim range (lastVerifiedTick+1 .. CurrentTick-1). The
        // PredResim loop re-Tick()s those frames and adds collected events to the buffer
        // without inline dispatch — DiffRollbackEvents then reconciles the new-vs-old set:
        //   - Regular new-only @ tick > _lastVerifiedTick → OnEventPredicted (NOT Confirmed,
        //     distinct from the verified-tick branch in path d).
        //   - Synced new-only → skipped (DiffRollbackEvents line 78). Synced events at
        //     predicted ticks do not dispatch until those ticks subsequently verify.

        [Test]
        public void SDClient_PredResim_RegularEvent_DiffCascade_NewOnlyFiresPredictedNotConfirmed()
        {
            const int raiseAtTick = 2;             // event lives in PredResim range
            const int predictDepth = 4;            // ticks 0..3 predicted → CurrentTick = 4
            const int verifiedExecutionTick = 0;   // verified entry covers only tick 0
            const int entryTick = verifiedExecutionTick + 1;

            int variant = 1;
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestRegularEvent { Payload = variant },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);

            int predictedCount = 0;
            int confirmedCount = 0;
            int canceledCount = 0;
            int lastPredictedTick = -1;
            engine.OnEventPredicted += (tick, evt) =>
            {
                if (evt is TestRegularEvent) { predictedCount++; lastPredictedTick = tick; }
            };
            engine.OnEventConfirmed += (tick, evt) => { if (evt is TestRegularEvent) confirmedCount++; };
            engine.OnEventCanceled  += (tick, evt) => { if (evt is TestRegularEvent) canceledCount++; };

            var hashes = DrivePredictionAndCaptureHashes(engine, sim, predictDepth);

            Assert.AreEqual(1, predictedCount,
                "Initial prediction must fire OnEventPredicted exactly once for the raised Regular event");

            variant = 2;

            int baselinePredicted = predictedCount;
            int baselineConfirmed = confirmedCount;
            int baselineCanceled = canceledCount;

            EnqueueVerifiedEntry(engine, entryTick, new List<ICommand>(), hashes[entryTick]);
            InvokeProcessVerifiedBatch(engine);

            int newPredicted = predictedCount - baselinePredicted;
            int newConfirmed = confirmedCount - baselineConfirmed;
            int newCanceled = canceledCount - baselineCanceled;

            Assert.AreEqual(1, newCanceled,
                $"Old-only variant 1 must fire OnEventCanceled exactly once. Got {newCanceled}.");
            Assert.AreEqual(0, newConfirmed,
                $"OnEventConfirmed must NOT fire — tick {raiseAtTick} is still Predicted (> _lastVerifiedTick after batch). Got {newConfirmed}.");
            Assert.AreEqual(1, newPredicted,
                $"New-only variant 2 at Predicted tick must fire OnEventPredicted exactly once. Got {newPredicted}.");
            Assert.AreEqual(raiseAtTick, lastPredictedTick,
                "Predicted dispatch tick must equal raise tick");
        }

        [Test]
        public void SDClient_PredResim_SyncedEvent_NoDispatchAtPredictedTick_EvtTickPreserved()
        {
            const int raiseAtTick = 2;             // PredResim range
            const int predictDepth = 4;
            const int verifiedExecutionTick = 0;
            const int entryTick = verifiedExecutionTick + 1;

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestSyncedEvent { Payload = 1 },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);

            int dispatchedCount = 0;
            engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent) dispatchedCount++;
            };

            var hashes = DrivePredictionAndCaptureHashes(engine, sim, predictDepth);

            Assert.AreEqual(0, dispatchedCount,
                "Synced events at Predicted ticks must not dispatch during initial prediction");

            EnqueueVerifiedEntry(engine, entryTick, new List<ICommand>(), hashes[entryTick]);
            InvokeProcessVerifiedBatch(engine);

            Assert.AreEqual(0, dispatchedCount,
                $"Synced event at PredResim tick {raiseAtTick} (> verified executionTick {verifiedExecutionTick}) " +
                "must not dispatch — PredResim loop has no inline dispatch and DiffRollbackEvents skips Synced. " +
                $"Got {dispatchedCount}.");

            var eventBuffer = (EventBuffer)_eventBufferField.GetValue(engine);
            var bufferedAtRaiseTick = eventBuffer.GetEvents(raiseAtTick);
            Assert.AreEqual(1, bufferedAtRaiseTick.Count,
                $"Exactly one TestSyncedEvent must remain in the buffer at tick {raiseAtTick} after PredResim");
            Assert.IsInstanceOf<TestSyncedEvent>(bufferedAtRaiseTick[0]);
            Assert.AreEqual(raiseAtTick, bufferedAtRaiseTick[0].Tick,
                $"evt.Tick stamped by BeginTick during PredResim must equal the resim tick ({raiseAtTick}) — tick-argument invariant");
        }

        // ── (f) Reconnect/Resync resim (ServerDrivenClient.cs:712-748) ─────
        //
        // HandleServerDrivenFullStateReceived's else-branch (determinism failure / Reconnect
        // recovery). Path entries: !_expectingInitialFullState && !_expectingFullState.
        //   1. ApplyFullState(reason=ResyncRequest) — restores frame, calls _eventBuffer.ClearAll
        //      (the "outer clear" for this path).
        //   2. CurrentTick = restoreTick; _lastVerifiedTick = restoreTick.
        //   3. _serverDrivenNetwork.ClearUnackedInputs() — covered by the MockSDNetworkService stub.
        //   4. _pendingVerifiedQueue.Clear().
        //   5. Resim loop tick+1 .. previousTick-1 — for each tick: SaveSnapshot, BeginTick,
        //      Tick, AddEvent. No inline dispatch and no DiffRollbackEvents in this path.
        //
        // Asserts (adapted to this path):
        //   - Events buffered during resim carry evt.Tick == resimTick (BeginTick stamping).
        //   - NO inline dispatch fires (Synced/Predicted/Confirmed/Canceled all 0 in the
        //     resim phase). Buffer ClearAll already drained pre-resim events; resim adds new
        //     buffer entries without invoking the dispatcher.
        //   - _serverDrivenNetwork.ClearUnackedInputs is invoked exactly once.

        [Test]
        public void SDClient_ReconnectResyncResim_BuffersEventsAtResimTick_NoInlineDispatch()
        {
            const int raiseAtTick = 3;             // event lives in the resim range
            const int predictDepth = 5;            // ticks 0..4 predicted → CurrentTick = 5
            const int resyncTick = 1;              // ApplyFullState rolls back to here

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestSyncedEvent { Payload = 1 },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);

            var sdNet = new MockSDNetworkService();
            InjectServerDrivenNetwork(engine, sdNet);

            int syncedCount = 0;
            int predictedCount = 0;
            int confirmedCount = 0;
            int canceledCount = 0;
            engine.OnSyncedEvent     += (t, e) => { if (e is TestSyncedEvent)  syncedCount++; };
            engine.OnEventPredicted  += (t, e) => { if (e is TestSyncedEvent || e is TestRegularEvent) predictedCount++; };
            engine.OnEventConfirmed  += (t, e) => { if (e is TestSyncedEvent || e is TestRegularEvent) confirmedCount++; };
            engine.OnEventCanceled   += (t, e) => { if (e is TestSyncedEvent || e is TestRegularEvent) canceledCount++; };

            // Drive prediction up to resyncTick first so we can capture the frame state at that
            // tick — the FullState payload must match what ApplyFullState restores to (otherwise
            // the hash-mismatch branch fires and OnHashMismatch / OnDesyncDetected events arrive).
            for (int t = 0; t < resyncTick; t++)
                DrivePredictedTick(engine);
            byte[] resyncStateData = sim.SerializeFullState();
            long resyncStateHash = sim.GetStateHash();

            // Continue prediction through raiseAtTick and beyond. EventRaiserSystem fires at
            // frame.Tick == raiseAtTick during prediction — the buffered event will be cleared
            // by ApplyFullState's ClearAll and re-raised during resim.
            for (int t = resyncTick; t < predictDepth; t++)
                DrivePredictedTick(engine);

            // Baseline: prior to invoking the Resync path, prediction's DispatchTickEvents
            // would have fired OnEventPredicted only for Regular events. Synced was buffered.
            int baselineSynced = syncedCount;
            int baselinePredicted = predictedCount;
            int baselineConfirmed = confirmedCount;
            int baselineCanceled = canceledCount;

            InvokeHandleServerDrivenFullStateReceived(engine, resyncTick, resyncStateData, resyncStateHash);

            Assert.AreEqual(1, sdNet.ClearUnackedInputsCallCount,
                "Reconnect/Resync path must call _serverDrivenNetwork.ClearUnackedInputs exactly once (line 706)");

            int dispatchSyncedDelta = syncedCount - baselineSynced;
            int dispatchPredictedDelta = predictedCount - baselinePredicted;
            int dispatchConfirmedDelta = confirmedCount - baselineConfirmed;
            int dispatchCanceledDelta = canceledCount - baselineCanceled;

            Assert.AreEqual(0, dispatchSyncedDelta,
                "Reconnect/Resync resim loop has no inline Synced dispatch — buffered events stay until later verification");
            Assert.AreEqual(0, dispatchPredictedDelta,
                "Reconnect/Resync path skips DispatchTickEvents during resim — no OnEventPredicted should fire");
            Assert.AreEqual(0, dispatchConfirmedDelta,
                "Reconnect/Resync path skips DiffRollbackEvents entirely — no OnEventConfirmed should fire");
            Assert.AreEqual(0, dispatchCanceledDelta,
                "Reconnect/Resync path skips DiffRollbackEvents entirely — no OnEventCanceled should fire");

            // Tick-argument invariant: for every tick in the resim window, any buffered event's
            // evt.Tick must equal the buffer key (BeginTick stamp from line 735 of ServerDrivenClient.cs
            // must match AddEvent's tick arg at line 745). Iterates the whole window so the test is
            // robust to the engine's `tick` parameter convention (last-verified vs next-to-compute).
            var eventBuffer = (EventBuffer)_eventBufferField.GetValue(engine);
            int totalBufferedAfterResim = 0;
            for (int t = 0; t <= predictDepth; t++)
            {
                var events = eventBuffer.GetEvents(t);
                totalBufferedAfterResim += events.Count;
                for (int i = 0; i < events.Count; i++)
                {
                    Assert.AreEqual(t, events[i].Tick,
                        $"Tick-argument invariant violated: buffer[{t}][{i}].Tick = {events[i].Tick}");
                }
            }
            Assert.AreEqual(1, totalBufferedAfterResim,
                "Resim must re-raise the TestSyncedEvent exactly once across the resim window " +
                "(ClearAll cleared all pre-resim entries; resim adds one).");
        }

        [Test]
        public void SDClient_ReconnectResyncResim_NonEventTicksHaveEmptyBuffer()
        {
            const int raiseAtTick = 3;
            const int predictDepth = 5;
            const int resyncTick = 1;

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestRegularEvent { Payload = 7 },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);
            InjectServerDrivenNetwork(engine, new MockSDNetworkService());

            for (int t = 0; t < resyncTick; t++)
                DrivePredictedTick(engine);
            byte[] resyncStateData = sim.SerializeFullState();
            long resyncStateHash = sim.GetStateHash();

            for (int t = resyncTick; t < predictDepth; t++)
                DrivePredictedTick(engine);

            InvokeHandleServerDrivenFullStateReceived(engine, resyncTick, resyncStateData, resyncStateHash);

            var eventBuffer = (EventBuffer)_eventBufferField.GetValue(engine);

            // Resim re-raises the Regular event exactly once across the resim window.
            // Whichever tick the engine lands it at, the buffer key must match evt.Tick.
            int regularCount = 0;
            int landedAtTick = -1;
            for (int t = 0; t <= predictDepth; t++)
            {
                var events = eventBuffer.GetEvents(t);
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i] is TestRegularEvent)
                    {
                        regularCount++;
                        landedAtTick = t;
                        Assert.AreEqual(t, events[i].Tick,
                            $"Tick-argument invariant violated: buffer[{t}][{i}].Tick = {events[i].Tick}");
                    }
                }
            }
            Assert.AreEqual(1, regularCount,
                "Resim must re-raise the TestRegularEvent exactly once across the resim window — " +
                "ClearAll cleared everything pre-resim and the resim loop re-adds one.");
            Assert.Greater(landedAtTick, resyncTick,
                $"Re-raised event must land strictly after the resync tick — got tick {landedAtTick}, resyncTick {resyncTick}");
        }

        // ── SD-Client verified batch ring wrap — incomplete cache + false-confirm ─
        //
        // Tests the incomplete-cache + DiffRollbackEvents false-confirm cascade caused by
        // ring wrap erase during stalled prediction.
        //
        // Setup:
        //   - ecsSim.maxRollbackTicks = 150 (ECS snapshot retention must outlast stall length)
        //   - simConfig.MaxRollbackTicks = 50 → SnapshotCapacity = 52 (ring wrap parameter)
        //   - simConfig.QuorumMissDropTicks = int.MaxValue (disable quorum-miss watchdog)
        //   - EventRaiserSystem raises a Regular event ONLY at tick raiseTick (=15)
        //
        // Mechanism:
        //   - Drive predictDepth (=70) prediction ticks (chain never verifies). _lastVerifiedTick stays at -1.
        //   - Tick raiseTick event raised, stored in slot[raiseTick % cap]. At prediction tick
        //     raiseTick + cap, ExecuteClientPredictionTick's ClearTick wipes that slot → event silently lost.
        //   - Inject ONE verified entry for executionTick=raiseTick (entry.Tick=raiseTick+1).
        //     Multiple sparse entries would break frame.Tick continuity (resim only ticks 1 per entry,
        //     not between entries) — single entry keeps frame.Tick aligned with entry.Tick semantics.
        //   - ProcessVerifiedBatchCore's outer ClearTick range loop [raiseTick, CurrentTick):
        //     • t=raiseTick: GetEvents = slot empty (wiped). Cache: [].
        //   - Resim re-raises event at raiseTick → evt_v1 in buffer at tick raiseTick.
        //   - DiffRollbackEvents:
        //     • evt_v1 (new) — NOT in cache → new-only → OnEventConfirmed fires (FALSE-CONFIRM)
        [Test]
        public void SDClientVerifiedBatch_RingWrap_IncompleteCacheCausesFalseConfirm()
        {
            const int maxRollbackTicks = 50;     // SnapshotCapacity = 52
            const int ecsMaxRollback = 150;
            const int raiseTick = 15;            // wiped by ring wrap at tick raiseTick + capacity = 67
            const int predictDepth = 70;         // CurrentTick reaches 70 — well past raiseTick + capacity = 67

            var simConfig = MakeSDClientConfig();
            simConfig.MaxRollbackTicks = maxRollbackTicks;
            // SyncCheckInterval default 30 ≤ MaxRollbackTicks 50 → no validation error.

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: ecsMaxRollback, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseTick,
                Factory = () => new TestRegularEvent { Payload = 7 },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(simConfig, new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);
            InjectServerDrivenNetwork(engine, new MockSDNetworkService());

            int predictedCount = 0;
            int confirmedAtRaiseTick = 0;
            int canceledCount = 0;
            engine.OnEventPredicted += (tick, evt) => { if (evt is TestRegularEvent) predictedCount++; };
            engine.OnEventConfirmed += (tick, evt) =>
            {
                if (evt is TestRegularEvent && tick == raiseTick) confirmedAtRaiseTick++;
            };
            engine.OnEventCanceled += (tick, evt) => { if (evt is TestRegularEvent) canceledCount++; };

            var hashes = DrivePredictionAndCaptureHashes(engine, sim, predictDepth);

            Assert.AreEqual(1, predictedCount,
                $"Initial prediction must fire OnEventPredicted exactly once for the raised event.");

            // Pre-batch buffer state check: slot[raiseTick % cap] should be empty (wiped by ClearTick
            // at tick raiseTick + cap during prediction).
            int capacity = maxRollbackTicks + 2;
            var eventBuffer = (EventBuffer)_eventBufferField.GetValue(engine);
            var bufferedAtRaise = eventBuffer.GetEvents(raiseTick);
            int stillPresent = 0;
            for (int i = 0; i < bufferedAtRaise.Count; i++)
                if (bufferedAtRaise[i] is TestRegularEvent && bufferedAtRaise[i].Tick == raiseTick)
                    stillPresent++;
            Assert.AreEqual(0, stillPresent,
                $"Ring wrap: tick {raiseTick}'s event must be wiped by ClearTick at tick " +
                $"{raiseTick + capacity}. Found {stillPresent} remaining in slot[{raiseTick % capacity}].");

            int baselineConfirmed = confirmedAtRaiseTick;
            int baselineCanceled = canceledCount;

            // Inject single verified entry for executionTick=raiseTick. entry.Tick = raiseTick + 1
            // (Klotho convention: entry.Tick is the post-execution frame.Tick).
            EnqueueVerifiedEntry(engine, raiseTick + 1, new List<ICommand>(), hashes[raiseTick + 1]);
            InvokeProcessVerifiedBatch(engine);

            int newConfirmedAtRaiseTick = confirmedAtRaiseTick - baselineConfirmed;
            int newCanceled = canceledCount - baselineCanceled;

            // FALSE-CONFIRM signature: tick raiseTick's event was wiped → cache missing it →
            // resim's re-raise classified as new-only → OnEventConfirmed fires (incorrect, since it's
            // a regeneration of the lost event, not a genuinely new event).
            Assert.GreaterOrEqual(newConfirmedAtRaiseTick, 1,
                $"FALSE-CONFIRM regression: tick {raiseTick}'s event was wiped by ring wrap " +
                $"before verified batch arrived. Outer ClearTick range loop's incomplete cache caused " +
                $"DiffRollbackEvents to classify the resim regeneration as new-only → OnEventConfirmed " +
                $"fired ({newConfirmedAtRaiseTick} times). Expected bug signature: ≥ 1.");

            // No false-cancel expected: cache was empty for raiseTick, so nothing to cancel.
            Assert.AreEqual(0, newCanceled,
                $"No old events to cancel — cache was empty at raiseTick (event was ring-wrap-erased). " +
                $"Got {newCanceled} OnEventCanceled fires.");
        }

        // ── SD-Client Reconnect/Resync resim stale event leakage ───────────
        //
        // Tests the stale event leakage + tick mismatch + double-fire cascade when the
        // Reconnect/Resync resim loop adds events at ring-wrap-colliding ticks (T_a and
        // T_b = T_a + capacity).
        //
        // Mechanism:
        //   - HandleServerDrivenFullStateReceived (Resync branch) calls ApplyFullState.ClearAll
        //     to wipe the event buffer, then enters a resim loop [tick+1, previousTick) that
        //     calls AddEvent inside the loop with NO ClearTick — so two ticks mapping to the
        //     same slot append into the same list.
        //   - If T_a and T_b = T_a + capacity are both in the resim range, slot[T_a % capacity]
        //     ends up holding [evtA, evtB] (both Synced events, distinct evt.Tick values).
        //   - Subsequent chain-advance fires DispatchSyncedEventsForTick(T_a, slot) → dispatches
        //     BOTH events with callback tick=T_a (evtB.Tick=T_b ≠ T_a → TICK MISMATCH). Watermark
        //     advances to T_a. Then DispatchSyncedEventsForTick(T_b, slot) → tick=T_b > watermark
        //     → proceeds → dispatches BOTH again → 4 total dispatches.
        //
        // The current _syncedDispatchHighWaterMark is tick-level — it cannot detect that the
        // events in the slot are stale from another tick. This test pins down the gap.
        [Test]
        public void SDClientReconnectResym_RingWrap_StaleEventLeakageDoubleDispatch()
        {
            const int maxRollbackTicks = 50;     // SnapshotCapacity = 52
            const int ecsMaxRollback = 150;
            const int resyncTick = 1;            // FullState captured here, ApplyFullState restores to here
            // Resync resim has a frame.Tick off-by-one: after Restore frame.Tick = resyncTick,
            // resim's BeginTick(resimTick) calls sim.Tick which advances frame.Tick from
            // (resimTick - 1) → resimTick. So a system with RaiseAtTick=N fires during the
            // iteration resimTick=(N+1), and the resulting event is stamped evt.Tick = N+1
            // (BeginTick stamp). The buffered slot is therefore slot[(N+1) % cap].
            const int raiseFrameTickA = 5;       // system fires at frame.Tick=5 → evt at slot[6 % cap]
            const int raiseFrameTickB = raiseFrameTickA + 52;  // = 57 → evt at slot[58 % cap]
            const int resimEvtTickA = raiseFrameTickA + 1;     // = 6
            const int resimEvtTickB = raiseFrameTickB + 1;     // = 58 — same slot as resimEvtTickA (both mod 52 = 6)
            const int predictDepth = 100;        // CurrentTick advances to 100, past raiseFrameTickB

            var simConfig = MakeSDClientConfig();
            simConfig.MaxRollbackTicks = maxRollbackTicks;

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: ecsMaxRollback, deltaTimeMs: 25);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseFrameTickA,
                Factory = () => new TestSyncedEvent { Payload = 1 },
            }, SystemPhase.Update);
            sim.AddSystem(new EventRaiserSystem
            {
                RaiseAtTick = raiseFrameTickB,
                Factory = () => new TestSyncedEvent { Payload = 2 },
            }, SystemPhase.Update);

            var engine = new KlothoEngine(simConfig, new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);
            InjectServerDrivenNetwork(engine, new MockSDNetworkService());

            int dispatchedCount = 0;
            int tickMismatchCount = 0;
            engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                {
                    dispatchedCount++;
                    if (tick != evt.Tick) tickMismatchCount++;
                }
            };

            // Drive to resyncTick, then capture FullState there.
            for (int t = 0; t < resyncTick; t++)
                DrivePredictedTick(engine);
            byte[] resyncStateData = sim.SerializeFullState();
            long resyncStateHash = sim.GetStateHash();

            // Drive remaining prediction past raiseTickB to populate the buffer (raiseTickA's
            // event will be overwritten by ring wrap erase at tick raiseTickA + capacity — but
            // ApplyFullState.ClearAll will wipe everything anyway, so the pre-state doesn't matter).
            for (int t = resyncTick; t < predictDepth; t++)
                DrivePredictedTick(engine);

            // Note: Synced events at predicted ticks are NOT dispatched (DispatchTickEvents with
            // Predicted state skips Synced). So dispatchedCount stays at 0 before Resync.
            Assert.AreEqual(0, dispatchedCount,
                "Synced events at predicted ticks must not dispatch — DispatchTickEvents skips Synced in Predicted state.");

            // Trigger Reconnect/Resync. ApplyFullState restores frame.Tick to resyncTick,
            // ClearAll wipes buffer, then resim loop ticks [resyncTick+1, previousTick) =
            // [2, 100), with NO ClearTick in the loop body. So events at raiseTickA and
            // raiseTickB (both Synced, both in resim range) append into slot[5] together.
            InvokeHandleServerDrivenFullStateReceived(engine, resyncTick, resyncStateData, resyncStateHash);

            // Verify post-Resync buffer state — slot for resimEvtTickA should hold BOTH events.
            // Both events landed at evt.Tick = resimEvtTickA / B (= raiseFrameTick + 1) due to
            // the resim off-by-one. Both map to the same slot via ring wrap.
            var eventBuffer = (EventBuffer)_eventBufferField.GetValue(engine);
            var slotEvents = eventBuffer.GetEvents(resimEvtTickA);
            int syncedInSlot = 0;
            for (int i = 0; i < slotEvents.Count; i++)
                if (slotEvents[i] is TestSyncedEvent) syncedInSlot++;
            int slotIdx = resimEvtTickA % (maxRollbackTicks + 2);
            Assert.AreEqual(2, syncedInSlot,
                $"Ring wrap collision: slot[{slotIdx}] must hold BOTH evtA (evt.Tick={resimEvtTickA}) " +
                $"and evtB (evt.Tick={resimEvtTickB}) after resim — no ClearTick inside the resim loop " +
                $"allows accumulation. Got {syncedInSlot} Synced events.");

            // Simulate chain-advance dispatch at resimEvtTickA then resimEvtTickB. Each call dispatches
            // ALL Synced events in the slot (no evt.Tick filter), so both events fire under each
            // callback tick — over-dispatch + tick mismatch.
            InvokeDispatchSyncedEventsForTick(engine, resimEvtTickA, slotEvents);
            // Re-fetch (same slot, no clear) and dispatch at resimEvtTickB.
            var slotEventsB = eventBuffer.GetEvents(resimEvtTickB);
            InvokeDispatchSyncedEventsForTick(engine, resimEvtTickB, slotEventsB);

            // OVER-DISPATCH signature: in correct semantics, 2 events at distinct ticks
            // should fire exactly 2 times total. Stale event leakage causes both events to fire
            // under both callback ticks → 4 dispatches. The current watermark (tick-level)
            // cannot detect the cross-tick contamination.
            Assert.Greater(dispatchedCount, 2,
                $"OVER-DISPATCH regression: stale event leakage causes each Synced event " +
                $"in the shared slot to fire under both callback ticks ({resimEvtTickA} and {resimEvtTickB}). " +
                $"Got {dispatchedCount} dispatches, expected > 2 (correct: 1 per event = 2 total).");

            // TICK MISMATCH signature: at least one dispatch must show callback tick != evt.Tick.
            // The watermark only tracks max dispatched tick, not which events were dispatched.
            Assert.Greater(tickMismatchCount, 0,
                $"TICK MISMATCH regression: at least one OnSyncedEvent dispatch must show " +
                $"callback tick != evt.Tick (a stale event from another slot fired under wrong tick). " +
                $"Got {tickMismatchCount} mismatches. This is the watermark gap signature.");
        }
    }
}
