using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Authored seed vs. effective seed.
    ///
    /// <para><b>The contract being restored.</b> GameDevAPI.md and Specification.md both say
    /// <c>RandomSeed = 0</c> is auto-replaced by <c>Environment.TickCount</c> on the host, which
    /// promises that a NON-zero authored seed is used. It was not: <c>StartGame</c> drew a fresh
    /// TickCount on every match and threw the authored value away.</para>
    ///
    /// <para><b>Why the gates split the way they do.</b> <see cref="KlothoTestHarness"/> builds its
    /// engines directly and never goes through <see cref="KlothoSession.Create"/>, so no harness test
    /// can observe the Create-side change. One gate covers that site alone (through Create); another
    /// covers the StartGame site alone (through the harness). One staying red tells you which half is missing —
    /// merging them into a single assertion would lose that.</para>
    ///
    /// Gates: Create keeps authored 0 · authored → effective · authored survives the match ·
    /// SD, per-service · authored → replay metadata → replay engine.
    /// </summary>
    [TestFixture]
    public class SeedPinningTests
    {
        private const int AuthoredSeed = 4242;

        private KlothoTestHarness _harness;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("SeedPinningTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        // ── KlothoSession.Create keeps the authored value ───────────────────

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
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }
            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { }
        }

        /// <summary>
        /// <c>Create</c> copies the authored seed verbatim; it no longer substitutes TickCount.
        ///
        /// <para>This is the ONLY gate over that site: the harness constructs its engines directly, so
        /// the StartGame tests below stay green even if the Create edit is dropped. Dropping it is not harmless — Create
        /// would stamp a TickCount into the config, StartGame would then read that non-zero value, and
        /// every match on the session would reuse the first match's seed.</para>
        ///
        /// <para>The replay entry kind is used because it reaches the same branch with no transport:
        /// <c>isGuest = setup.Connection != null</c> is false, so construction falls to the host/normal
        /// branch that holds the seed expression.</para>
        ///
        /// <para>Only the authored-0 case is asserted. Authored-non-zero was already honoured before this
        /// change, so it would be green either way and pins nothing.</para>
        /// </summary>
        [Test]
        public void Create_AuthoredZeroSeed_IsCopiedVerbatim()
        {
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _logger,
                SimulationConfig = new SimulationConfig { Mode = NetworkMode.ServerDriven },
                SimulationCallbacks = new StubSimulationCallbacks(),
                ViewCallbacks = new StubViewCallbacks(),
                SessionConfig = new SessionConfig(),   // authored 0
                IsReplay = true,
            });

            Assert.AreEqual(0, session.Engine.SessionConfig.RandomSeed,
                "Create must copy the authored seed verbatim — substituting TickCount here makes the " +
                "field an effective value, which StartGame would then reuse for every later match.");

            session.Stop(saveReplay: false);
        }

        // ── P2P host + guest through the real StartGame ─────────────────────

        /// <summary>An authored non-zero seed becomes the match's effective seed, on host and guest.</summary>
        [Test]
        public void AuthoredSeed_BecomesTheEffectiveSeed()
        {
            _harness.WithSessionConfig(() => new SessionConfig { RandomSeed = AuthoredSeed });
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();

            Assert.AreEqual(AuthoredSeed, _harness.Host.NetworkService.RandomSeed,
                "StartGame must honour the authored seed instead of drawing a fresh TickCount");

            foreach (var guest in _harness.Guests)
            {
                Assert.AreEqual(AuthoredSeed, guest.NetworkService.RandomSeed,
                    "The authored seed must propagate to guests through GameStartMessage");
            }
        }

        /// <summary>
        /// With authored 0 the config field stays 0 across match start (authored invariant).
        ///
        /// <para>This is the gate over the write-back removal. While <c>HandleGameStartMessage</c> still
        /// wrote the effective seed back into the config, the field flipped from *authored request* to
        /// *last effective value* — and once StartGame reads that field, a second CreateRoom on the same
        /// service replays the first match's seed.</para>
        /// </summary>
        [Test]
        public void AuthoredZeroSeed_SurvivesMatchStart()
        {
            _harness.CreateHost(2);   // default factory authors 0
            _harness.AddGuest();
            _harness.StartPlaying();

            Assert.AreEqual(0, _harness.Host.Engine.SessionConfig.RandomSeed,
                "SessionConfig.RandomSeed is the authored request and must stay 0 — the effective seed " +
                "lives on Engine.RandomSeed / NetworkService.RandomSeed.");
        }

        // ── Server-driven, one seed per room ────────────────────────────────

        /// <summary>
        /// Each ServerNetworkService uses the seed of ITS OWN SessionConfig.
        ///
        /// <para>The config arrives through <c>SubscribeEngine</c> (from the engine), not the
        /// constructor, so the fixture needs one engine per service. No RoomManager is needed: StartGame's
        /// only precondition is the duplicate-call guard, and with no joined peers the broadcast loop
        /// does not run.</para>
        /// </summary>
        [Test]
        public void ServerDriven_EachServiceUsesItsOwnAuthoredSeed()
        {
            var svcA = NewServerWithAuthoredSeed(1111);
            var svcB = NewServerWithAuthoredSeed(2222);

            svcA.StartGame();
            svcB.StartGame();

            Assert.AreEqual(1111, svcA.RandomSeed, "Room A must use its own authored seed");
            Assert.AreEqual(2222, svcB.RandomSeed, "Room B must use its own authored seed");
        }

        private ServerNetworkService NewServerWithAuthoredSeed(int seed)
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, _logger);
            svc.CreateRoom("test", 4);   // seeds _sharedClock, which the countdown branch reads

            // SubscribeEngine is the only way in for the SessionConfig.
            var engine = new KlothoEngine(
                new SimulationConfig { Mode = NetworkMode.ServerDriven },
                new SessionConfig { RandomSeed = seed });
            svc.SubscribeEngine(engine);

            return svc;
        }

        // ── The pinned seed reaches the replay file and comes back ──────────

        /// <summary>
        /// The authored seed reaches the replay metadata, and starting a replay from that file
        /// restores it as the replay engine's seed.
        ///
        /// <para>Scope: this pins the seed's <i>transport</i> (authored → StartGame → engine → metadata →
        /// replay engine), which is what the replay-verification model leans on. It does not
        /// re-assert byte-identical playback — the harness simulation does not consume
        /// <c>RandomSeedComponent</c>, so a state-hash comparison here would be insensitive to the seed.
        /// Playback fidelity itself is already covered by the ReplayFidelity tests.</para>
        /// </summary>
        [Test]
        public void AuthoredSeed_ReachesReplayMetadata_AndIsRestoredOnPlayback()
        {
            _harness.WithSessionConfig(() => new SessionConfig { RandomSeed = AuthoredSeed });
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(100);
            _harness.Host.Engine.Stop();

            IReplayData replay = _harness.Host.Engine.GetCurrentReplayData();
            Assert.IsNotNull(replay, "The host must have produced replay data");
            Assert.AreEqual(AuthoredSeed, replay.Metadata.RandomSeed,
                "The recorded metadata must carry the authored seed — a verifier reconstructing tick 0 " +
                "from the file has nothing else to go on.");

            var replaySim = new TestSimulation { UseDeterministicHash = true };
            replaySim.SetPlayerCount(2);
            var replayEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            replayEngine.Initialize(replaySim, _logger);
            replayEngine.SetCommandFactory(new CommandFactory());
            replayEngine.StartReplay(replay);

            Assert.AreEqual(AuthoredSeed, replayEngine.RandomSeed,
                "Playback must restore the seed from the file's metadata");
        }
        // ── Copying a config, and the warning that guards a pinned seed ─────

        [Test] // A room that must not share state with its neighbours needs its own instance, and the copy
               // is memberwise so a field added to SessionConfig later comes along without anyone editing
               // a list. (CopyOf names every field because it converts from ANY ISessionConfig; Clone does
               // not have that problem and must not inherit its drift risk.)
        public void Clone_CopiesEveryField_AndIsANewInstance()
        {
            var src = new SessionConfig
            {
                RandomSeed = 4242, MaxPlayers = 7, MinPlayers = 3, CountdownDurationMs = 0,
                AllowLateJoin = true, EndGraceMs = 1234, ReconnectMaxRetries = 9,
            };

            var copy = src.Clone();

            Assert.AreNotSame(src, copy, "a clone that is the same object defeats the purpose");
            Assert.AreEqual(src.RandomSeed, copy.RandomSeed);
            Assert.AreEqual(src.MaxPlayers, copy.MaxPlayers);
            Assert.AreEqual(src.MinPlayers, copy.MinPlayers);
            Assert.AreEqual(src.CountdownDurationMs, copy.CountdownDurationMs);
            Assert.AreEqual(src.AllowLateJoin, copy.AllowLateJoin);
            Assert.AreEqual(src.EndGraceMs, copy.EndGraceMs);
            Assert.AreEqual(src.ReconnectMaxRetries, copy.ReconnectMaxRetries);

            copy.MaxPlayers = 1;
            Assert.AreEqual(7, src.MaxPlayers, "writing to the copy must not reach the source");
        }

        [Test] // The multi-room warning asks "do two live rooms run the same match", and the answer is the
               // seed VALUE. It used to be instance identity, which read the symptom off its commonest
               // cause — and would have gone quiet the moment a server adopted the per-match factory the
               // warning itself recommends, because a cloned pinned config carries the same seed in a
               // different object. This pins the property the warning is actually about.
        public void ClonedConfigs_StillCarryTheSamePinnedSeed()
        {
            var authored = new SessionConfig { RandomSeed = 777 };

            var roomA = authored.Clone();
            var roomB = authored.Clone();

            Assert.AreNotSame(roomA, roomB, "a per-match factory hands out distinct objects");
            Assert.AreEqual(roomA.RandomSeed, roomB.RandomSeed,
                "...carrying the same pinned seed — which is why the room-manager check compares seeds, not references");
        }

    }
}
