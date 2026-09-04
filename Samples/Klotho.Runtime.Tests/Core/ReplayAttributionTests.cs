using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Replay attribution fields — what a recording says about the world it ran in.
    ///
    /// <para><b>What these gates are for.</b> When a re-simulation of a replay disagrees with the
    /// recording, the file must say enough to tell "the player cheated" from "this is a different build
    /// or different content". That is attribution, not integrity: every value here is a non-cryptographic
    /// fold and nothing is signed, so a forger rewrites the anchors along with the payload. What the
    /// anchors buy is the ability to dismiss an HONEST client's failure.</para>
    ///
    /// <para><b>Why the anchors are captured with the snapshot.</b> The navigation term moves with runtime
    /// rebakes, so anchors taken at any other moment describe a different world than the snapshot does.
    /// <see cref="Anchors_AreTheSnapshotInstant_NotTheStopInstant"/> is the gate that pins it.</para>
    ///
    /// <para><b>Vacuity.</b> Every fingerprint source returns 0 when it is not registered, so a test built
    /// on a bare simulation would assert 0 == 0 ^ 0 ^ 0 and prove nothing. Each fingerprint gate therefore
    /// registers all three sources with distinct non-zero values and asserts that at least one is non-zero.</para>
    /// </summary>
    [TestFixture]
    public class ReplayAttributionTests
    {
        private const long ColliderFp = 0x0000_1111_2222_3333L;
        private const long NavFp      = 0x0000_4444_5555_6666L;
        private const long GameFp     = 0x0000_7777_8888_9999L;

        // ── fakes ──────────────────────────────────────────────────────────

        private sealed class FakeColliders : IStaticColliderService, ISystem
        {
            public long Fp;
            public FakeColliders(long fp) { Fp = fp; }
            public long GetStaticFingerprint() => Fp;
            public void LoadStaticColliders(string sceneKey, List<FPStaticCollider> colliders) { }
            public void UnloadStaticColliders(string sceneKey) { }
            public void GetStaticColliders(out FPStaticCollider[] colliders, out int count) { colliders = null; count = 0; }
            public void Update(ref Frame frame) { }
        }

        private sealed class FakeNav : INavFingerprintSource, ISystem
        {
            public long Fp;
            public FakeNav(long fp) { Fp = fp; }
            public long GetNavFingerprint() => Fp;
            public void Update(ref Frame frame) { }
        }

        private sealed class FakeGame : IGameFingerprintSource, ISystem
        {
            public long Fp;
            public FakeGame(long fp) { Fp = fp; }
            public long GetGameFingerprint() => Fp;
            public void Update(ref Frame frame) { }
        }

        /// <summary>
        /// A P2P engine over a real EcsSimulation with all three environment sources registered. Real
        /// EcsSimulation, not a double: the breakdown returns all-zero for anything else, which is exactly
        /// how these gates would go vacuous.
        /// </summary>
        private static KlothoEngine NewRecordingEngine(
            out FakeSdNetworkService net, out FakeColliders colliders, out FakeNav nav, out FakeGame game)
        {
            EcsSimulation sim;
            sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 50);
            colliders = new FakeColliders(ColliderFp);
            nav = new FakeNav(NavFp);
            game = new FakeGame(GameFp);
            sim.AddSystem(colliders, SystemPhase.Update);
            sim.AddSystem(nav, SystemPhase.Update);
            sim.AddSystem(game, SystemPhase.Update);

            // A network service is required: the no-network Initialize overload is the spectator/replay
            // path and never reaches WaitingForPlayers, so Start() would return without recording.
            // A network service is required twice over: the no-network Initialize overload never reaches
            // WaitingForPlayers, and the snapshot + anchors are injected by the GameStart handler — calling
            // Start() directly records ticks but anchors nothing.
            net = new FakeSdNetworkService();
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, net, new LogCapture());
            engine.SetCommandFactory(new CommandFactory());
            return engine;
        }

        private static IReplayMetadata RecordAndStop(KlothoEngine engine, FakeSdNetworkService net)
        {
            net.RaiseGameStart();
            engine.Stop();
            var replay = engine.GetCurrentReplayData();
            Assert.IsNotNull(replay, "recording produced no replay data");
            return replay.Metadata;
        }

        // ── V-1: the split stays the fold ──────────────────────────────────

        [Test] // The environment half must remain exactly Static ^ Nav ^ Game after being split into four
               // reported terms — otherwise the file and the Ready/FullState wire disagree about the same world.
        public void EnvironmentFingerprint_EqualsStaticXorNavXorGame()
        {
            var engine = NewRecordingEngine(out _, out var colliders, out var nav, out var game);

            long env = engine.GetLocalEnvironmentFingerprint();

            // Not vacuous: assert the sources actually contribute before asserting the identity.
            Assert.AreNotEqual(0, colliders.Fp | nav.Fp | game.Fp,
                "all three sources are 0 — the identity below would hold trivially and prove nothing");
            Assert.AreEqual(colliders.Fp ^ nav.Fp ^ game.Fp, env,
                "environment half must stay bit-identical to the three-way fold");
            long combined = engine.GetLocalLayoutFingerprint() ^ env;
            if (combined == 0) combined = 1;   // 0 -> 1 is the wire sentinel, applied by the combined fold only
            Assert.AreEqual(combined, engine.GetLocalStaticFingerprint(),
                "the combined wire value must still be layout ^ environment");
        }

        // ── V-7: the anchors are the sources ───────────────────────────────

        [Test] // The four recorded terms must be the four live source values — a fold or a re-normalization
               // here would destroy the one thing the fields exist for (telling WHICH source diverged).
        public void Anchors_MatchTheLiveSourceValues()
        {
            var engine = NewRecordingEngine(out var net, out var colliders, out var nav, out var game);
            long layout = engine.GetLocalLayoutFingerprint();

            var meta = RecordAndStop(engine, net);

            Assert.AreNotEqual(0, layout, "layout is frozen once a simulation exists");
            Assert.AreEqual(layout, meta.LayoutFingerprint);
            Assert.AreEqual(colliders.Fp, meta.StaticColliderFingerprint);
            Assert.AreEqual(nav.Fp, meta.NavFingerprint);
            Assert.AreEqual(game.Fp, meta.GameFingerprint);
        }

        // ── V-6: captured with the snapshot, not at stop ───────────────────

        [Test] // The navigation term moves during a match (runtime rebakes). The recorded anchor must be the
               // value at the snapshot instant; capturing at Stop/save time would describe a world the
               // recorded inputs never ran in. Mutating the source mid-recording stands in for a rebake.
        public void Anchors_AreTheSnapshotInstant_NotTheStopInstant()
        {
            var engine = NewRecordingEngine(out var net, out _, out var nav, out _);

            net.RaiseGameStart();
            long atSnapshot = NavFp;
            long afterRebake = NavFp ^ 0x0F0F_0F0F_0F0F_0F0FL;
            nav.Fp = afterRebake;                      // stands in for a runtime rebake
            engine.Stop();

            var meta = engine.GetCurrentReplayData().Metadata;
            Assert.AreEqual(atSnapshot, meta.NavFingerprint,
                "anchor must be the snapshot-instant value");
            Assert.AreNotEqual(afterRebake, meta.NavFingerprint,
                "anchor must NOT be re-read at stop time — a mid-match rebake would rewrite history");
        }

        // ── V-11a / snapshot triple: hash and tick reach the file ──────────

        [Test] // The snapshot hash used to exist only as an event argument, and the tick did not exist at
               // all. Both must land in the metadata: a hash with no tick cannot say WHICH tick it hashes.
        public void InitialStateSnapshot_RecordsHashAndTick()
        {
            var engine = NewRecordingEngine(out var net, out _, out _, out _);

            var meta = RecordAndStop(engine, net);

            Assert.IsNotNull(meta.InitialStateSnapshot, "snapshot must be persisted");
            Assert.AreNotEqual(0, meta.InitialStateSnapshot.Length);
            Assert.AreNotEqual(0, meta.InitialStateHash, "snapshot hash must be persisted, not only evented");
            Assert.AreEqual(0, meta.InitialStateTick, "a normal bootstrap snapshot is tick 0");
        }

        // ── V-10: the completion marker is stamped ─────────────────────────

        [Test] // Normal is 1, not 0, so this gate can tell "the default was applied" from "nobody stamped
               // anything". With Normal = 0 the two are the same bytes and the assertion proves nothing.
        public void NormalStop_StampsNormal_NotUnspecified()
        {
            var engine = NewRecordingEngine(out var net, out _, out _, out _);

            var meta = RecordAndStop(engine, net);

            Assert.AreEqual(ReplayEndReason.Normal, meta.EndReason);
            Assert.AreNotEqual(ReplayEndReason.Unspecified, meta.EndReason,
                "an unstamped recording cannot be told apart from a truncated one");
        }

        // ── V-9: truncation says why ───────────────────────────────────────

        [Test] // A replay cut by a state jump is honest-but-short. Without the marker a verifier reading only
               // TotalTicks cannot distinguish it from a run that was stopped early on purpose.
        public void ResyncTruncation_StampsResyncRequest()
        {
            var h = new SdReplayHarness();
            for (int i = 0; i < 5; i++)
                h.DeliverVerified();
            Assert.IsTrue(h.Engine.IsRecording);

            int resyncTick = h.Engine.LastVerifiedTick + 3;
            long distinct = h.Sim.GetStateHash() ^ 0x5A5A_5A5A_5A5A_5A5AL;
            h.Net.RaiseServerFullState(resyncTick, BitConverter.GetBytes(distinct), distinct);

            Assert.IsFalse(h.Engine.IsRecording, "resync must truncate the recording");
            var meta = h.Engine.GetCurrentReplayData().Metadata;
            Assert.AreEqual(ReplayEndReason.ResyncRequest, meta.EndReason);
            Assert.Less(meta.TotalTicks, resyncTick, "still truncated before the reset tick");
        }

        [Test] // The SD client is the only producer of a snapshot that is NOT tick 0, and it is the path a
               // P2P smoke test can never reach. A hash with no tick cannot say which tick it hashes, so both
               // have to survive the bootstrap plumbing — including the retain-until-StartRecording buffer.
        public void SdBootstrap_RecordsTheSnapshotTickAndHash()
        {
            const int BootstrapTick = 7;                       // stands in for a mid-match join
            const long SnapshotHash = 0x0123_4567_89AB_CDEFL;

            var sim = new DeterministicReplaySim();
            var net = new FakeSdNetworkService();
            var engine = new KlothoEngine(new SimulationConfig { Mode = NetworkMode.ServerDriven }, new SessionConfig());
            engine.Initialize(sim, net, new LogCapture());
            engine.SetCommandFactory(new CommandFactory());

            net.RaiseCountdownStarted(0);
            net.RaiseGameStart();
            var serverSim = new DeterministicReplaySim();
            serverSim.Initialize();
            var (data, _) = serverSim.SerializeFullStateWithHash();
            net.RaiseServerFullState(BootstrapTick, data, SnapshotHash);

            engine.Stop();
            var meta = engine.GetCurrentReplayData().Metadata;

            Assert.AreEqual(BootstrapTick, meta.InitialStateTick,
                "the tick the server sent must reach the file — it is what makes the hash meaningful");
            Assert.AreEqual(SnapshotHash, meta.InitialStateHash,
                "the hash used to flow only through the event; it must now be persisted too");
        }

        // ── V-8: layout inputs are recorded, never restored ────────────────

        [Test] // Both are process-global layout inputs, frozen long before a replay loads. Recording them lets
               // a verifier BOOT correctly; restoring them into the playback config would change nothing while
               // signalling that it had — the run would reproduce under a different layout and only the
               // fingerprint mismatch would ever say so.
        public void LayoutInputs_AreRecordedSorted_AndNotRestored()
        {
            var cfg = new SimulationConfig();
            cfg.PrunedComponentTypeIds.Add(7);
            cfg.PrunedComponentTypeIds.Add(3);
            cfg.ComponentMaxCountOverrides[9] = 90;
            cfg.ComponentMaxCountOverrides[2] = 20;

            var data = new ReplayData();
            data.Initialize(playerCount: 1, simConfig: cfg, randomSeed: 0);
            var meta = (ReplayMetadata)data.Metadata;

            CollectionAssert.AreEqual(new[] { 3, 7 }, meta.PrunedComponentTypeIds,
                "sorted, unlike the wire codec: this file is read for comparison, so the bytes must be stable");
            CollectionAssert.AreEqual(new[] { 2, 9 }, meta.ComponentMaxCountTypeIds);
            CollectionAssert.AreEqual(new[] { 20, 90 }, meta.ComponentMaxCountValues,
                "values must stay parallel to the sorted ids");

            var restored = meta.ToSimulationConfig();
            CollectionAssert.IsEmpty(restored.PrunedComponentTypeIds,
                "restoring would be a no-op that looks like a restore");
            CollectionAssert.IsEmpty(restored.ComponentMaxCountOverrides);
        }

        // ── V-2: round trip ────────────────────────────────────────────────

        [Test] // Every new field must survive the file, including the two parallel arrays. A field added to
               // Serialize but forgotten in Deserialize shifts everything after it.
        public void NewFields_SurviveRoundTrip()
        {
            var written = new ReplayData();
            var meta = (ReplayMetadata)written.Metadata;
            meta.LayoutFingerprint = 0x1111_2222_3333_4444L;
            meta.StaticColliderFingerprint = ColliderFp;
            meta.NavFingerprint = NavFp;
            meta.GameFingerprint = GameFp;
            meta.InitialStateHash = 0x0BAD_F00D_0BAD_F00DL;
            meta.InitialStateTick = 41;
            meta.EndReason = ReplayEndReason.CorrectiveReset;
            meta.PrunedComponentTypeIds.AddRange(new[] { 3, 7 });
            meta.ComponentMaxCountTypeIds.AddRange(new[] { 2, 9 });
            meta.ComponentMaxCountValues.AddRange(new[] { 20, 90 });
            meta.InitialStateSnapshot = new byte[] { 1, 2, 3, 4, 5 };
            meta.PlayerCount = 3;
            meta.InitialRoster.AddRange(new[] { 3, 1, 2 });

            var read = new ReplayData();
            read.Deserialize(written.Serialize());
            var rt = (ReplayMetadata)read.Metadata;

            Assert.AreEqual(meta.LayoutFingerprint, rt.LayoutFingerprint);
            Assert.AreEqual(meta.StaticColliderFingerprint, rt.StaticColliderFingerprint);
            Assert.AreEqual(meta.NavFingerprint, rt.NavFingerprint);
            Assert.AreEqual(meta.GameFingerprint, rt.GameFingerprint);
            Assert.AreEqual(meta.InitialStateHash, rt.InitialStateHash);
            Assert.AreEqual(meta.InitialStateTick, rt.InitialStateTick);
            Assert.AreEqual(meta.EndReason, rt.EndReason);
            CollectionAssert.AreEqual(meta.PrunedComponentTypeIds, rt.PrunedComponentTypeIds);
            CollectionAssert.AreEqual(meta.ComponentMaxCountTypeIds, rt.ComponentMaxCountTypeIds);
            CollectionAssert.AreEqual(meta.ComponentMaxCountValues, rt.ComponentMaxCountValues);
            CollectionAssert.AreEqual(meta.InitialStateSnapshot, rt.InitialStateSnapshot);
            CollectionAssert.AreEqual(meta.InitialRoster, rt.InitialRoster);
        }

        // ── the tick-0 roster ──────────────────────────────────────────────

        [Test] // The roster is the order participant entities were CREATED in, which makes it state-hash
               // input: reconstructing tick 0 from a sorted or otherwise re-ordered roster builds a
               // different world and calls an honest recording a mismatch.
        public void InitialRoster_PreservesOrder_AcrossTheFile()
        {
            var written = new ReplayData();
            var meta = (ReplayMetadata)written.Metadata;
            meta.PlayerCount = 3;
            meta.InitialRoster.AddRange(new[] { 3, 1, 2 });

            var read = new ReplayData();
            read.Deserialize(written.Serialize());

            CollectionAssert.AreEqual(new[] { 3, 1, 2 }, ((ReplayMetadata)read.Metadata).InitialRoster,
                "the roster must come back in creation order, not sorted");
        }

        [Test] // Empty is not "missing data" — it is the signal that this peer did not build tick 0 (an SD
               // client receives its initial state from the server). Reconstruction keys off exactly this,
               // so an empty roster must survive the file as empty rather than becoming a parse failure.
        public void InitialRoster_EmptyIsLegal_AndMeansDidNotBuildTick0()
        {
            var written = new ReplayData();
            ((ReplayMetadata)written.Metadata).PlayerCount = 2;

            var read = new ReplayData();
            Assert.DoesNotThrow(() => read.Deserialize(written.Serialize()));
            Assert.IsEmpty(((ReplayMetadata)read.Metadata).InitialRoster);
        }

        [Test] // PlayerCount and the roster record the same thing twice. Any disagreement other than
               // "empty" is a corrupted file: reconstructing from it would build a world with the wrong
               // number of participants and blame the recording for the difference.
        public void InitialRoster_DisagreeingWithPlayerCount_IsRejected()
        {
            var written = new ReplayData();
            var meta = (ReplayMetadata)written.Metadata;
            meta.PlayerCount = 4;
            meta.InitialRoster.AddRange(new[] { 1, 2 });

            var ex = Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(written.Serialize()));
            StringAssert.Contains("PlayerCount", ex.Message);
        }

        [Test] // SetInitialRoster copies; it must not alias the engine's live list, which keeps growing as
               // late joiners arrive. Aliasing would silently turn the recorded tick-0 roster into the
               // match's current participants and make reconstruction build the wrong world.
        public void SetInitialRoster_CopiesInsteadOfAliasing()
        {
            var data = new ReplayData();
            var live = new List<int> { 1, 2 };
            data.SetInitialRoster(live);

            live.Add(3); // a late joiner

            CollectionAssert.AreEqual(new[] { 1, 2 }, ((ReplayMetadata)data.Metadata).InitialRoster);
        }

        // ── V-3 / V-4 / V-5: the version guard ─────────────────────────────

        private static byte[] SerializedWithVersion(int version)
        {
            var data = new ReplayData();
            byte[] bytes = data.Serialize();
            // Version is the first metadata field, immediately after the 4-byte RPLY magic.
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), version);
            return bytes;
        }

        [Test] // The current version must load — the control for the two rejection gates below.
        public void CurrentVersion_Loads()
        {
            Assert.DoesNotThrow(() => new ReplayData().Deserialize(SerializedWithVersion(ReplayMetadata.CURRENT_VERSION)));
            Assert.AreEqual(6, ReplayMetadata.CURRENT_VERSION,
                "4 carried the tick-0 roster; 5 is that layout plus the roster-parallel "
                + "entitlements; 6 is the per-agent area masks, which grew NavAgentComponent's "
                + "wire size 700 -> 708 and so made every version-5 component stream unreadable");
        }

        [Test] // The old guard was `>`, which let version 1 through to be misparsed with the current layout.
        public void Version1_IsRejectedCleanly()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => new ReplayData().Deserialize(SerializedWithVersion(1)));
            StringAssert.Contains("re-record", ex.Message,
                "the message is what a game shows the player, so it must say what to do");
        }

        [Test] // Regression for the collision the version choice exists to avoid: 2 was issued by an earlier
               // build and files carrying it exist, so re-using it would admit them under a different layout.
        public void Version2_IsRejectedCleanly()
        {
            Assert.Throws<InvalidDataException>(
                () => new ReplayData().Deserialize(SerializedWithVersion(2)),
                "version 2 was issued and must never be re-used as 'current'");
        }

        [Test] // 3 is the version immediately before the roster. Its layout is a strict prefix of 4, so a
               // `>=` or a "read what is there" reader would parse it happily and hand a verifier an empty
               // roster — i.e. silently downgrade to snapshot-restore instead of refusing the file.
        public void Version3_IsRejectedCleanly()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => new ReplayData().Deserialize(SerializedWithVersion(3)));
            StringAssert.Contains("re-record", ex.Message);
        }

        [Test] // 4 is the version immediately before the entitlements, and the same prefix argument applies:
               // read leniently it would yield an empty per-player record, which this format deliberately
               // treats as "nothing was issued" — a silently wrong tick-0 rebuild instead of a refusal.
        public void Version4_IsRejectedCleanly()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => new ReplayData().Deserialize(SerializedWithVersion(4)));
            StringAssert.Contains("re-record", ex.Message);
        }

        // ── V-5: the roster-parallel per-player record ─────────────────────

        [Test] // The record is sliced by roster INDEX, so order is the whole contract: a swap hands each
               // player the other's data, and nothing downstream can notice — the counts are identical.
        public void InitialEntitlements_SurviveRoundTrip_InRosterOrder()
        {
            var written = new ReplayData();
            var meta = (ReplayMetadata)written.Metadata;
            meta.PlayerCount = 2;
            meta.InitialRoster.AddRange(new[] { 7, 9 });
            written.SetInitialEntitlements(new[]
            {
                new byte[] { 0xA1, 0xA2 },        // player 7
                new byte[] { 0xB1, 0xB2, 0xB3 },  // player 9
            });

            var read = new ReplayData();
            read.Deserialize(written.Serialize());
            var rt = (ReplayMetadata)read.Metadata;

            CollectionAssert.AreEqual(new[] { 2, 3 }, rt.InitialEntitlementLengths);
            CollectionAssert.AreEqual(new byte[] { 0xA1, 0xA2, 0xB1, 0xB2, 0xB3 }, rt.InitialEntitlementData);
        }

        [Test] // A match with no issuer really has none. Empty must survive as a valid record — reading it
               // as "missing" would refuse every solo, P2P and lobbyless recording, which is most of them.
        public void InitialEntitlements_EmptyIsLegal()
        {
            var written = new ReplayData();
            var meta = (ReplayMetadata)written.Metadata;
            meta.PlayerCount = 2;
            meta.InitialRoster.AddRange(new[] { 1, 2 });
            written.SetInitialEntitlements(new byte[][] { null, null });

            var read = new ReplayData();
            Assert.DoesNotThrow(() => read.Deserialize(written.Serialize()));
            var rt = (ReplayMetadata)read.Metadata;
            CollectionAssert.AreEqual(new[] { 0, 0 }, rt.InitialEntitlementLengths);
            Assert.IsNull(rt.InitialEntitlementData, "no payload is needed when every entry is empty");
        }

        [Test] // The per-player record is sliced by roster index. A count that disagrees would walk off the
               // roster and hand players someone else's bytes, so it is corruption — not a fallback.
        public void InitialEntitlements_DisagreeingWithRoster_IsRejected()
        {
            var written = new ReplayData();
            var meta = (ReplayMetadata)written.Metadata;
            meta.PlayerCount = 2;
            meta.InitialRoster.AddRange(new[] { 1, 2 });
            meta.InitialEntitlementLengths.AddRange(new[] { 1 });   // one entry, two players
            meta.InitialEntitlementData = new byte[] { 0xFF };

            var ex = Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(written.Serialize()));
            StringAssert.Contains("entitlement", ex.Message);
        }
    }
}
