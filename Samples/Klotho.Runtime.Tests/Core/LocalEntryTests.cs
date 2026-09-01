using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// The single-player entry point: one local player, no socket.
    ///
    /// <para><b>Why this fixture exists.</b> A one-player session was believed to work — the start
    /// gate clamps MinPlayers to 1, the host self-dispatches its own Ready, and every tick promotes
    /// to verified the moment it executes — but the belief rested entirely on reading the code:
    /// the repository had no one-player end-to-end test at all. This fixture is the first execution
    /// of that path.</para>
    ///
    /// <para><b>Ordering is load-bearing.</b> SetReady returns silently unless Phase is already
    /// Synchronized, which only CreateRoom (HostGame) sets, and a MinPlayers that is not 1 makes the
    /// start gate false forever with no log. Both failures are silent, so a broken order shows up
    /// here as a timeout rather than an assertion.</para>
    /// </summary>
    [TestFixture]
    public class LocalEntryTests
    {
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("LocalEntryTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── Stubs ────────────────────────────────────────────────────────────

        private sealed class StubViewCallbacks : IViewCallbacks
        {
            public void OnGameStart(IKlothoEngine engine) { }
            public void OnTickExecuted(int tick) { }
            public void OnLateJoinActivated(IKlothoEngine engine) { }
        }

        private sealed class StubSimulationCallbacks : ISimulationCallbacks
        {
            public void RegisterSystems(EcsSimulation simulation) { }
            public void OnInitializeWorld(IKlothoEngine engine) { }
            // Sends nothing: the engine auto-injects an EmptyCommand for the local player, which is
            // what a solo session with no input looks like.
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }
            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { }
        }

        private static KlothoFlowSetup NewSetup(IKLogger logger, INetworkTransport transport,
                                                bool enableReplayRecording = true)
            => new KlothoFlowSetup
            {
                Logger = logger,
                Transport = transport,
                EnableReplayRecording = enableReplayRecording,
                CallbacksFactory = (_, __) =>
                    new SessionCallbacks(new StubSimulationCallbacks(), new StubViewCallbacks()),
            };

        /// <summary>Starts a solo session through StartLocal and runs it to Running.</summary>
        private KlothoSession StartSoloSession(bool enableReplayRecording = true)
        {
            var flow = new KlothoSessionFlow(NewSetup(_logger, new NullTransport(_logger), enableReplayRecording));
            var session = flow.StartLocal(new SimulationConfig(), SoloSessionConfig());
            PumpUntilRunning(session);
            return session;
        }

        private static T PrivateField<T>(object target, string name)
        {
            var f = target.GetType().GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(f, $"Field '{name}' is gone — the gate below no longer observes what it claims to.");
            return (T)f.GetValue(target);
        }

        private static SessionConfig SoloSessionConfig() => new SessionConfig
        {
            MinPlayers = 1,
            CountdownDurationMs = 0,
        };

        /// <summary>Pumps the session until the engine is Running, or fails after <paramref name="maxUpdates"/>.</summary>
        private static void PumpUntilRunning(KlothoSession session, int maxUpdates = 200)
        {
            for (int i = 0; i < maxUpdates && session.Engine.State != KlothoState.Running; i++)
                session.Update(0.025f);

            Assert.AreEqual(KlothoState.Running, session.Engine.State,
                "The match never started. Both ways this fails are silent: SetReady before HostGame " +
                "(Phase gate returns with no log) and MinPlayers != 1 (start gate false forever).");
        }

        // ── V-1 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A one-player session starts and every tick it runs is verified.
        ///
        /// <para>This began as a characterization test against the three-step API (StartHost +
        /// HostGame + SetReady) and passed before StartLocal existed, so it is also the evidence that
        /// the one-call entry preserved the behaviour rather than inventing it.</para>
        ///
        /// <para>Verified-chain promotion consults no peer — it fires when the executed tick is
        /// exactly one past the last verified one — so with a single active player there is never a
        /// prediction gap and the chain tracks CurrentTick with no rollback path involved.</para>
        /// </summary>
        [Test]
        public void SoloHost_StartsAndVerifiesEveryTick()
        {
            var session = StartSoloSession();

            int tickAtStart = session.Engine.CurrentTick;
            for (int i = 0; i < 40; i++)
                session.Update(0.025f);

            Assert.Greater(session.Engine.CurrentTick, tickAtStart,
                "The tick loop did not advance — a solo host has all input every tick, so it must.");
            Assert.AreEqual(session.Engine.CurrentTick - 1, session.Engine.LastVerifiedTick,
                "Every executed tick must be verified in a solo session: the promotion needs no remote " +
                "confirmation, so there is no prediction gap to leave behind.");

            session.Stop(saveReplay: false);
        }

        // ── V-2 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// NullTransport reports no socket and does nothing, on every member.
        ///
        /// <para>Listen and Connect returning false is the contract, not an oversight: it is what
        /// makes StartHostAndListen tear down instead of handing back a room nobody can reach.</para>
        /// </summary>
        [Test]
        public void NullTransport_HasNoSocketAndNeverRaisesAnything()
        {
            var transport = new NullTransport(_logger);

            Assert.IsFalse(transport.Listen("0.0.0.0", 9050, 4),
                "Listen must report failure — reporting success would tell a game that remote players can arrive.");
            Assert.IsFalse(transport.Connect("127.0.0.1", 9050),
                "Connect must report failure — there is no socket to connect with.");
            Assert.IsFalse(transport.IsConnected);
            Assert.AreEqual(0, transport.LocalPeerId);
            Assert.AreEqual(-1, transport.LastDisconnectPayload);
            Assert.AreEqual(string.Empty, transport.RemoteAddress);
            Assert.AreEqual(0, transport.RemotePort);
            CollectionAssert.IsEmpty(transport.GetConnectedPeerIds());

            bool raised = false;
            transport.OnConnected += () => raised = true;
            transport.OnDisconnected += _ => raised = true;
            transport.OnDataReceived += (_, __, ___) => raised = true;
            transport.OnPeerConnected += _ => raised = true;
            transport.OnPeerDisconnected += _ => raised = true;

            var payload = new byte[] { 1, 2, 3 };
            Assert.DoesNotThrow(() =>
            {
                transport.Send(0, payload, DeliveryMethod.Reliable);
                transport.Send(0, payload, payload.Length, DeliveryMethod.Reliable);
                transport.Broadcast(payload, DeliveryMethod.ReliableOrdered);
                transport.Broadcast(payload, payload.Length, DeliveryMethod.ReliableOrdered);
                transport.DisconnectPeer(0);
                transport.DisconnectPeer(0, payload);
                transport.Disconnect();
                transport.PollEvents();
                transport.FlushSendQueue();
            }, "Every member must be a harmless no-op — that is what removes the 47 unguarded dereferences as a hazard.");

            Assert.IsFalse(raised, "No event may ever fire: nothing arrives on a transport with no socket.");
        }

        // ── V-5 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Recording is on unless asked otherwise, and asking otherwise actually stops it.
        ///
        /// <para>The replay data is read after Stop, not during the match: CurrentReplayData is
        /// assigned when recording ENDS (StopRecording / the OnRecordingStopped forward), so it is
        /// null mid-match even while IsRecording is true. IsRecording is the live signal; the data is
        /// the post-mortem one.</para>
        /// </summary>
        [Test]
        public void ReplayRecording_IsOnByDefaultAndCanBeTurnedOff()
        {
            var recording = StartSoloSession();
            Assert.IsTrue(recording.Engine.IsRecording, "Recording must default to on — everything downstream is made of it.");
            for (int i = 0; i < 10; i++) recording.Update(0.025f);
            recording.Stop(saveReplay: false);

            var data = recording.Engine.GetCurrentReplayData();
            Assert.IsNotNull(data, "Stopping a recording session must hand back the recorded replay.");
            Assert.IsNotNull(data.Metadata.InitialStateSnapshot,
                "The tick-0 snapshot is auto-injected on the host while recording — a replay without it cannot be replayed from the start.");

            var silent = StartSoloSession(enableReplayRecording: false);
            Assert.IsFalse(silent.Engine.IsRecording, "WithoutReplayRecording must reach Start() and stop the recorder.");
            for (int i = 0; i < 10; i++) silent.Update(0.025f);
            silent.Stop(saveReplay: false);

            Assert.IsNull(silent.Engine.GetCurrentReplayData(), "Nothing may be recorded when recording is off.");
        }

        // ── V-6 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Turning recording off does not take the bootstrap state with it.
        ///
        /// <para>The tick-0 snapshot injection sits behind an IsRecording gate, which invites the
        /// assumption that the host's bootstrap FullState and its pre-tick-0 divergence hash go with
        /// it. They do not — both are taken unconditionally — and this is the assertion that keeps
        /// the recording knob from quietly disabling desync diagnosis.</para>
        /// </summary>
        [Test]
        public void ReplayRecordingOff_LeavesBootstrapStateIntact()
        {
            var session = StartSoloSession(enableReplayRecording: false);

            Assert.AreNotEqual(0L, PrivateField<long>(session.Engine, "_bootstrapStateHash"),
                "The pre-tick-0 divergence hash must be taken whether or not the match is recorded — " +
                "it used to be piggy-backed on the recording branch, which left it 0 for every game that does not record.");
            Assert.IsNotNull(PrivateField<byte[]>(session.Engine, "_cachedFullState"),
                "The host's authoritative tick-0 broadcast is re-serialized outside the recording gate.");

            session.Stop(saveReplay: false);
        }

        // ── V-3 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// StartLocal never writes to the config it is handed.
        ///
        /// <para>This is the only thing standing between a shared inspector config and a silent
        /// corruption: USessionConfig is a ScriptableObject, and a MinPlayers forced in place during
        /// play mode survives play mode — every later multiplayer match would start at one player.</para>
        /// </summary>
        [Test]
        public void StartLocal_DoesNotMutateTheAuthoredConfig()
        {
            var authored = new SessionConfig { MinPlayers = 3, CountdownDurationMs = 0 };
            var flow = new KlothoSessionFlow(NewSetup(_logger, new NullTransport(_logger)));

            var session = flow.StartLocal(new SimulationConfig(), authored);
            PumpUntilRunning(session);

            Assert.AreEqual(3, authored.MinPlayers,
                "The caller's config must come back exactly as authored — writing to it corrupts a shared " +
                "ScriptableObject for every session after this one.");
            Assert.AreEqual(1, session.Engine.SessionConfig.MinPlayers,
                "The session must run with MinPlayers forced to 1 — the match cannot start otherwise.");

            session.Stop(saveReplay: false);
        }

        // ── V-4 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A local session reaches both auto-wiring gates, and the PlayerConfig send happens while
        /// there is still time for it.
        ///
        /// <para>Adding the enum value without widening the gates compiles, runs, and passes almost
        /// every test — the only losses are the un-injected network service and the unsent config, both
        /// silent. The phase check is what pins the ordering: Synchronized means HostGame has run and
        /// SetReady has not, which is the window StartLocal promises.</para>
        /// </summary>
        [Test]
        public void StartLocal_FiresLocalKindAndReachesBothAutoWiringGates()
        {
            var observer = new RecordingObserver();
            var callbacks = new ReceiverSimulationCallbacks();
            SessionPhase phaseAtConfigSend = SessionPhase.None;
            int configFactoryCalls = 0;

            var setup = new KlothoFlowSetup
            {
                Logger = _logger,
                Transport = new NullTransport(_logger),
                LifecycleObserver = observer,
                CallbacksFactory = (_, __) => new SessionCallbacks(callbacks, new StubViewCallbacks()),
                InitialPlayerConfigFactory = () =>
                {
                    configFactoryCalls++;
                    phaseAtConfigSend = callbacks.NetworkService?.Phase ?? SessionPhase.None;
                    return new StubPlayerConfig();
                },
            };

            var session = new KlothoSessionFlow(setup).StartLocal(new SimulationConfig(), SoloSessionConfig());
            PumpUntilRunning(session);

            Assert.AreEqual(SessionEntryKind.Local, observer.Kind,
                "A local session must be distinguishable from a host that others can join.");
            Assert.IsNotNull(callbacks.NetworkService,
                "INetworkServiceReceiver auto-injection must include Local — missing it is silent, not a null reference.");
            Assert.AreEqual(1, configFactoryCalls,
                "The local player's PlayerConfig must be sent exactly once.");
            Assert.AreEqual(SessionPhase.Synchronized, phaseAtConfigSend,
                "The send must land after HostGame (identity assigned) and before SetReady (match start) — " +
                "Synchronized is precisely that window.");

            session.Stop(saveReplay: false);
        }

        // ── V-8 ──────────────────────────────────────────────────────────────

        /// <summary>An authored seed reaches a solo match, which is what makes a daily-challenge run reproducible.</summary>
        [Test]
        public void StartLocal_HonoursTheAuthoredSeed()
        {
            const int AuthoredSeed = 20260830;
            var flow = new KlothoSessionFlow(NewSetup(_logger, new NullTransport(_logger)));
            var cfg = SoloSessionConfig();
            cfg.RandomSeed = AuthoredSeed;

            var session = flow.StartLocal(new SimulationConfig(), cfg);
            PumpUntilRunning(session);

            Assert.AreEqual(AuthoredSeed, session.Engine.RandomSeed,
                "StartGame honours the authored seed, and StartLocal must not lose it on the way through the copy.");

            session.Stop(saveReplay: false);
        }

        // ── V-9 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A solo session's replay carries the match's own seed and length into a replay engine.
        ///
        /// <para>Scoped to the seed and tick count reaching playback, not to a state-hash match: this
        /// fixture's simulation registers no systems, so its state is not a function of the seed and a
        /// hash comparison would pass without proving anything.</para>
        /// </summary>
        [Test]
        public void StartLocal_ReplayRoundTripsSeedAndLength()
        {
            const int AuthoredSeed = 987654;
            var flow = new KlothoSessionFlow(NewSetup(_logger, new NullTransport(_logger)));
            var cfg = SoloSessionConfig();
            cfg.RandomSeed = AuthoredSeed;

            var session = flow.StartLocal(new SimulationConfig(), cfg);
            PumpUntilRunning(session);
            for (int i = 0; i < 20; i++) session.Update(0.025f);
            session.Stop(saveReplay: false);

            var recorded = session.Engine.GetCurrentReplayData();
            Assert.IsNotNull(recorded);
            Assert.AreEqual(AuthoredSeed, recorded.Metadata.RandomSeed,
                "The seed must be in the replay metadata — a verifier reconstructs tick 0 from it.");
            Assert.Greater(recorded.Metadata.TotalTicks, 0, "A replay of a match that ran must contain ticks.");

            var replaySession = new KlothoSessionFlow(NewSetup(_logger, new NullTransport(_logger)))
                .StartReplay(recorded, recorded.Metadata.ToSimulationConfig());
            Assert.AreEqual(AuthoredSeed, replaySession.Engine.RandomSeed,
                "Playback must restore the recorded seed, otherwise a re-simulation diverges from the run it is checking.");
            replaySession.Stop(saveReplay: false);
        }

        // ── Test doubles used above ──────────────────────────────────────────

        private sealed class RecordingObserver : IKlothoSessionObserver
        {
            public SessionEntryKind? Kind { get; private set; }
            public void OnSessionCreated(KlothoSession session, SessionEntryKind kind) => Kind = kind;
        }

        private sealed class ReceiverSimulationCallbacks : ISimulationCallbacks, INetworkServiceReceiver
        {
            public IKlothoNetworkService NetworkService { get; private set; }
            public void SetNetworkService(IKlothoNetworkService service) => NetworkService = service;

            public void RegisterSystems(EcsSimulation simulation) { }
            public void OnInitializeWorld(IKlothoEngine engine) { }
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }
            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { }
        }

        private sealed class StubPlayerConfig : PlayerConfigBase
        {
            public override NetworkMessageType MessageTypeId => (NetworkMessageType)201; // UserDefined range
            protected override void SerializeData(ref SpanWriter writer) { }
            protected override void DeserializeData(ref SpanReader reader) { }
        }
    }
}
