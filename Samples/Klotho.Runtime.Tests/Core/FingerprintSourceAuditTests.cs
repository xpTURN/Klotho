using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// The fingerprint-source registration audit. <c>GetSystem&lt;T&gt;</c> is
    /// <c>SystemRunner.Find&lt;T&gt;</c> — the first registered match — so a game with two nav systems
    /// has exactly one mesh folded into the static environment fingerprint and the other one outside
    /// every cross-peer check, chosen by wiring order. These tests pin the warning, its boundary, and
    /// above all that the audit does not perturb the value it audits.
    /// </summary>
    [TestFixture]
    public class FingerprintSourceAuditTests
    {
        // ── log capture (CaptureSink precedent: KLogFormattingTests) ─────────

        private sealed class CaptureSink : IKLogSink
        {
            public readonly List<string> Warnings = new List<string>();
            public void Write(KLogLevel level, string message, Exception exception)
            {
                if (level >= KLogLevel.Warning) Warnings.Add(message ?? string.Empty);
            }
            public void Flush() { }
            public void Dispose() { }
        }

        // ── fingerprint source doubles ───────────────────────────────────────
        // Fixed values, so the environment fold is an arithmetic identity rather than a captured magic
        // number. None of them needs SystemRunner.Init — which is what lets gate 3 register one after
        // Initialize, where Init no longer runs.

        private sealed class ProbeColliderService : IStaticColliderService
        {
            private readonly long _fp;
            public ProbeColliderService(long fp) => _fp = fp;
            public void LoadStaticColliders(string sceneKey, List<FPStaticCollider> colliders) { }
            public void UnloadStaticColliders(string sceneKey) { }
            public void GetStaticColliders(out FPStaticCollider[] colliders, out int count)
            {
                colliders = Array.Empty<FPStaticCollider>(); count = 0;
            }
            public long GetStaticFingerprint() => _fp;
        }

        private sealed class ProbeNavSource : INavFingerprintSource
        {
            private readonly long _fp;
            public ProbeNavSource(long fp) => _fp = fp;
            public long GetNavFingerprint() => _fp;
        }

        private sealed class ProbeGameSource : IGameFingerprintSource
        {
            private readonly long _fp;
            public ProbeGameSource(long fp) => _fp = fp;
            public long GetGameFingerprint() => _fp;
        }

        private const long ColliderFp = 0x0000_1111_2222_3333L;
        private const long NavFp      = 0x0000_4444_5555_6666L;
        private const long NavFp2     = 0x0000_7777_8888_9999L;   // the second, uncovered mesh
        private const long GameFp     = 0x0000_0AAA_0BBB_0CCCL;
        private const long GameFp2    = 0x0000_0DDD_0EEE_0FFFL;

        /// <summary>Environment fold with one source of each kind, captured before the audit existed
        /// (2026-09-02) and equal to the XOR of the three constants. Absolute rather than derived at
        /// assert time, and stable across component-set churn because the layout term is excluded —
        /// an absolute golden on the layout-inclusive fold would break whenever any fixture in this
        /// assembly adds a component.</summary>
        private const long EnvGolden = 0x00005FFF7CCC5999L;

        // ── minimal rebake driver doubles (gate 5 only) ──────────────────────
        // The sibling warning only fires for a game that rebakes, which means a driver has to resolve.

        private sealed class EmptyPlacementSource : IFPNavMeshPlacementSource
        {
            public int Capacity => 4;
            public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
            {
                eligible = 0; return 0;
            }
            public void DestroyDue(ref Frame frame, int tick) { }
        }

        private sealed class NoOpInstaller : IFPNavMeshInstaller
        {
            public void Install(ref Frame frame, FPNavMesh mesh) { }
            public void Reseed(ref Frame frame) { }
        }

        // ── harness ──────────────────────────────────────────────────────────

        private const int MaxEntities = 64;

        private CaptureSink _sink;
        private IKLoggerFactory _factory;
        private IKLogger _logger;
        private KlothoEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _sink = new CaptureSink();
            _factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Trace).AddSink(_sink));
            _logger = _factory.CreateLogger("AuditTest");
        }

        [TearDown]
        public void TearDown()
        {
            _engine = null;
            _factory?.Dispose();
        }

        /// <summary>Engine + simulation with the given sources registered BEFORE Initialize.</summary>
        private EcsSimulation Build(params object[] systems)
        {
            var simulation = new EcsSimulation(MaxEntities, logger: _logger);
            foreach (var sys in systems)
                simulation.AddSystem(sys, SystemPhase.Update);

            _engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            _engine.Initialize(simulation, _logger);
            return simulation;
        }

        /// <summary>Only the audit's lines — the sibling warning and any engine chatter stay out.</summary>
        private List<string> AuditLines => _sink.Warnings
            .Where(w => w.Contains("is folded into the static environment fingerprint"))
            .ToList();

        private static void AssertNamesInterface(string line, string interfaceName)
            => Assert.IsTrue(line.Contains(interfaceName),
                $"the warning must name the interface it is about; got: {line}");

        // ── gate 1 ───────────────────────────────────────────────────────────

        [Test]
        public void SingleSourceOfEachKind_SaysNothing()
        {
            Build(new ProbeColliderService(ColliderFp),
                  new ProbeNavSource(NavFp),
                  new ProbeGameSource(GameFp));

            _engine.GetLocalStaticFingerprint();

            CollectionAssert.IsEmpty(AuditLines,
                "today's correct wiring must stay silent — a warning every game sees is one every game "
                + "learns to scroll past");
        }

        // ── gate 2 ───────────────────────────────────────────────────────────

        [Test]
        public void TwoNavSources_WarnOnce_AndNamesTheFoldedOne()
        {
            var folded = new ProbeNavSource(NavFp);
            Build(new ProbeColliderService(ColliderFp), folded, new ProbeNavSource(NavFp2));

            _engine.GetLocalStaticFingerprint();

            Assert.AreEqual(1, AuditLines.Count, "one violating interface, one line");
            AssertNamesInterface(AuditLines[0], nameof(INavFingerprintSource));
            Assert.IsTrue(AuditLines[0].Contains("2 systems implement"),
                $"the count belongs in the message; got: {AuditLines[0]}");

            // found[0] is what Find<T> folds — the message must name THAT one, not the shadowed source.
            Assert.IsTrue(AuditLines[0].Contains(folded.GetType().Name));
        }

        [Test]
        public void RepeatedFingerprintCalls_StillWarnOnce()
        {
            Build(new ProbeNavSource(NavFp), new ProbeNavSource(NavFp2));

            for (int i = 0; i < 5; i++)
                _engine.GetLocalStaticFingerprint();
            _engine.GetLocalEnvironmentFingerprint();

            Assert.AreEqual(1, AuditLines.Count,
                "the latch records that the audit RAN — resync and the replay snapshot path both come "
                + "back through here");
        }

        [Test]
        public void TwoInterfacesMultiplyRegistered_WarnOncePerInterface()
        {
            Build(new ProbeNavSource(NavFp), new ProbeNavSource(NavFp2),
                  new ProbeGameSource(GameFp), new ProbeGameSource(GameFp2));

            _engine.GetLocalStaticFingerprint();

            Assert.AreEqual(2, AuditLines.Count,
                "the latch is one audit, not one warning: a single run reports every violating interface");
            Assert.IsTrue(AuditLines.Any(l => l.Contains(nameof(INavFingerprintSource))));
            Assert.IsTrue(AuditLines.Any(l => l.Contains(nameof(IGameFingerprintSource))));
        }

        // ── gate 3 ───────────────────────────────────────────────────────────

        [Test]
        public void SourceRegisteredAfterInitialize_IsStillAudited()
        {
            // The engine has no point guaranteed to be after wiring — AddSystem is public and the game
            // schedules it. Auditing at the fingerprint call is what covers this; a startup hook would
            // count 1 here and say nothing. (Past the FIRST fingerprint call the audit is done for
            // good — that horizon is deliberate, see the audit's remarks.)
            var simulation = Build(new ProbeNavSource(NavFp));
            simulation.AddSystem(new ProbeNavSource(NavFp2), SystemPhase.Update);

            _engine.GetLocalStaticFingerprint();

            Assert.AreEqual(1, AuditLines.Count,
                "a source wired after Initialize but before the first fingerprint must still be seen");
            AssertNamesInterface(AuditLines[0], nameof(INavFingerprintSource));
        }

        // ── gate 4 ───────────────────────────────────────────────────────────

        [Test]
        public void AuditDoesNotMoveTheFingerprint()
        {
            Build(new ProbeColliderService(ColliderFp),
                  new ProbeNavSource(NavFp),
                  new ProbeGameSource(GameFp));

            // (a) the audited call and the latched call must agree — the direct evidence that the audit
            //     is side-effect free on the value.
            long env1 = _engine.GetLocalEnvironmentFingerprint();
            long static1 = _engine.GetLocalStaticFingerprint();
            long env2 = _engine.GetLocalEnvironmentFingerprint();
            long static2 = _engine.GetLocalStaticFingerprint();

            Assert.AreEqual(env1, env2, "environment fold moved between the audited and latched calls");
            Assert.AreEqual(static1, static2, "static fold moved between the audited and latched calls");

            // (b) and they must still be the values from before the audit existed.
            Assert.AreEqual(EnvGolden, env1, "environment fold changed against the pre-audit capture");
            Assert.AreEqual(_engine.GetLocalLayoutFingerprint() ^ env1, static1,
                "static fold must stay layout ^ environment — asserted as an identity rather than a "
                + "captured constant, because the layout term moves whenever any fixture in this "
                + "assembly adds a component");
        }

        [Test]
        public void AuditIsSilentAndValuePreservingWhenNothingIsRegistered()
        {
            Build();

            Assert.AreEqual(0, _engine.GetLocalEnvironmentFingerprint(),
                "no source at all folds to 0 — the 'not provided' wire sentinel");
            CollectionAssert.IsEmpty(AuditLines);
        }

        // ── gate 5 ───────────────────────────────────────────────────────────

        [Test]
        public void SiblingWarningAndThisOne_CoexistAndReadAlike()
        {
            // Exclusivity holds only per interface (0 vs 2+). Across interfaces both fire in one
            // session: no nav source at all (sibling) plus a doubled game slot (this audit).
            var driver = new FPNavMeshRebakeDriver(new EmptyPlacementSource(), new NoOpInstaller());
            Build(driver, new ProbeGameSource(GameFp), new ProbeGameSource(GameFp2));

            _engine.GetLocalStaticFingerprint();

            string sibling = _sink.Warnings.FirstOrDefault(
                w => w.Contains("no INavFingerprintSource"));
            Assert.IsNotNull(sibling,
                "the sibling startup warning should fire: a rebake driver is wired and no nav source is");

            Assert.AreEqual(1, AuditLines.Count, "and the doubled game slot is this audit's line");

            foreach (string line in new[] { sibling, AuditLines[0] })
                Assert.IsTrue(line.StartsWith("[KlothoEngine]"),
                    $"the two must read as one family; got: {line}");

            // Each carries a remedy the reader can act on — the sibling's is "register the agent
            // system", ours is "combine them". Asserted as the actual wording rather than a shared
            // token, because the two prescribe different things and a generic probe would pass on a
            // bare complaint.
            Assert.IsTrue(sibling.Contains("registering the agent system"),
                $"sibling must stay actionable; got: {sibling}");
            Assert.IsTrue(AuditLines[0].Contains("Combine them into ONE IGameFingerprintSource"),
                $"a doubled game slot cannot be told to fold itself into the game slot; got: {AuditLines[0]}");

            Assert.IsTrue(AuditLines[0].Contains("known false positive"),
                "and this one owns its false positives the way the sibling does");
        }
    }
}
