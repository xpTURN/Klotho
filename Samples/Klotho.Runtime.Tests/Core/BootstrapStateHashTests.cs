using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// The local tick-0 hash the bootstrap-FullState check compares against (IMP105 C-8).
    ///
    /// It used to be a by-product of the replay-recording branch: assigned only where a recording was
    /// being started, and never cleared. Recording defaults to on, so the common P2P path did seed it —
    /// but a game that starts with Start(false), and every path that skips that branch, left it 0 and the
    /// check silently disabled. And because nothing cleared it, an engine instance reused for a second
    /// session carried the first session's hash into a match whose tick-0 state has nothing to do with
    /// it, where a mismatch is reported as "the peers diverged before tick 0".
    ///
    /// Both halves are lifecycle claims, so both are asserted against the field itself.
    /// </summary>
    [TestFixture]
    public class BootstrapStateHashTests
    {
        private static readonly FieldInfo BootstrapHashField = typeof(KlothoEngine)
            .GetField("_bootstrapStateHash", BindingFlags.NonPublic | BindingFlags.Instance);

        private LogCapture        _log;
        private KlothoTestHarness _harness;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _log = new LogCapture();
            _harness = new KlothoTestHarness(_log);
            _harness.CreateHost(4);
            _harness.AddGuest();

            // TestSimulation answers a fixed 12345 for every hash question by default, which would make
            // "the seed came from the simulation" true no matter where the value actually came from.
            // Give each peer its own marker instead, set before StartPlaying so the seeding reads it.
            // (UseDeterministicHash is not the way: its accumulator is 0 at tick 0, and 0 is precisely
            // the value this field uses to mean "never seeded".)
            ((TestSimulation)_harness.Host.Simulation).StateHash      = 0x0A11CE0000000001L;
            ((TestSimulation)_harness.Guests[0].Simulation).StateHash = 0x0B0B0B0000000002L;

            _harness.StartPlaying();
        }

        [TearDown]
        public void TearDown() => _harness.Reset();

        private static long HashOf(KlothoEngine engine) => (long)BootstrapHashField.GetValue(engine);

        [Test]
        public void GameStart_SeedsTheBootstrapHashFromTheLocalTickZeroState()
        {
            // Each peer's seed must be ITS OWN simulation's hash — the distinct markers are what make
            // this an assertion about the source rather than about the stub's constant.
            //
            // What this does not prove: TestSimulation is a stub, so nothing here says GetStateHash
            // equals a FullState hash. That equality is EcsSimulation's invariant — it folds snapshot
            // participants one at a time to match SerializeFullStateWithHash — and the seeding now
            // leans on it, which is why the comment at the assignment names it.
            Assert.That(HashOf(_harness.Host.Engine), Is.EqualTo(_harness.Host.Simulation.GetStateHash()),
                "the host's seed has to come from the host's own tick-0 state");
            Assert.That(HashOf(_harness.Guests[0].Engine), Is.EqualTo(_harness.Guests[0].Simulation.GetStateHash()),
                "and the guest's from its own — a peer with no local hash cannot answer the question the check asks");
            Assert.That(HashOf(_harness.Host.Engine), Is.Not.Zero,
                "0 is the field's 'never seeded' value, so a peer that built a tick-0 world must not be left holding it");
        }

        [Test]
        public void Stop_ClearsTheBootstrapHash()
        {
            Assert.That(HashOf(_harness.Host.Engine), Is.Not.Zero, "precondition: the session seeded it");

            _harness.Host.Engine.Stop();

            Assert.That(HashOf(_harness.Host.Engine), Is.Zero,
                "a hash from a finished session describes no world the next one will build — Stop clears it like the rest of the per-session state");
        }
    }
}
