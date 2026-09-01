using System.Collections.Generic;

using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Rebuilding tick 0 instead of trusting the recorded snapshot.
    ///
    /// <para><b>What this buys.</b> Re-simulating a replay proves "these inputs produce this result" —
    /// it says nothing about the starting point, which is bytes the recording peer wrote and a forger can
    /// replace. Reconstruction does not read those bytes at all: it rebuilds tick 0 from the roster, seed
    /// and config through the same <c>BuildInitialWorld</c> the live path runs, so a forged snapshot is
    /// not detected, it is simply unused.</para>
    ///
    /// <para><b>What these gates do NOT cover.</b> Both the recording and the playback here run in the
    /// same .NET process, so they say the implementation is right — not that Unity's tick 0 and .NET's
    /// tick 0 are the same world. That premise is untested (Plan-Tick0Reconstruction §8-6) and only a
    /// file recorded by the Unity P2P host can put it on trial.</para>
    /// </summary>
    [TestFixture]
    public class Tick0ReconstructionTests
    {
        // ── recording ──────────────────────────────────────────────────────

        /// <summary>
        /// Records a P2P session over a real EcsSimulation and stops it. Real EcsSimulation, not a double:
        /// a stub answers a constant for every hash question, which is exactly how every gate below would
        /// go vacuous. <paramref name="playerIds"/> becomes the tick-0 roster, and it is the only thing
        /// that differs between the two worlds these gates compare.
        /// </summary>
        private static IReplayData Record(params int[] playerIds)
        {
            var sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 50);

            var net = new FakeSdNetworkService();
            var players = (List<IPlayerInfo>)net.Players;
            players.Clear();
            for (int i = 0; i < playerIds.Length; i++)
                players.Add(new FakeSdPlayerInfo(playerIds[i]));

            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, net, new LogCapture());
            engine.SetCommandFactory(new CommandFactory());

            // GameStart, not Start(): the roster, the snapshot and the anchors are all injected by that
            // handler, so a direct Start() records ticks against an empty metadata.
            net.RaiseGameStart();
            engine.Stop();

            var data = engine.GetCurrentReplayData();
            Assert.IsNotNull(data, "recording produced no replay data");
            return data;
        }

        /// <summary>Plays a recording back far enough to have a tick 0, and reports where it came from.</summary>
        private static KlothoEngine Play(IReplayData data, KlothoEngine.ReplayInitialState mode)
        {
            var sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 50);
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, new LogCapture());
            engine.SetCommandFactory(new CommandFactory());
            engine.StartReplay(data, mode);
            return engine;
        }

        // ── the roster is the evidence that tick 0 was built here ──────────

        [Test] // Reconstruction has no flag: a non-empty roster IS the signal that this peer built tick 0.
               // If the recording stops carrying it, every replay silently downgrades to snapshot-restore.
        public void Recording_CarriesTheRosterItBuiltTick0From()
        {
            var meta = Record(0, 1).Metadata;

            CollectionAssert.AreEqual(new[] { 0, 1 }, meta.InitialRoster,
                "the roster must be the participants, in the order tick 0 created them");
            Assert.AreEqual(meta.PlayerCount, meta.InitialRoster.Count,
                "PlayerCount and the roster record the same thing and may not disagree");
        }

        // ── V-3 / V-4: rebuilding lands on the recorded world ──────────────

        [Test] // The whole plan reduces to this equality. Restore is the world the recording claims; the
               // reconstruction is the world this build derives from the same inputs. If they differ, a
               // verifier would call every honest replay unverifiable.
        public void Reconstruct_AndRestore_LandOnTheSameTick0()
        {
            var data = Record(0, 1);

            var restored = Play(data, KlothoEngine.ReplayInitialState.RestoreSnapshot);
            var rebuilt = Play(data, KlothoEngine.ReplayInitialState.Reconstruct);

            Assert.IsFalse(restored.ReplayTick0Reconstructed, "restore must not silently rebuild");
            Assert.IsTrue(rebuilt.ReplayTick0Reconstructed, "the roster is there — this must be a rebuild");
            Assert.AreNotEqual(0, restored.ReplayTick0Hash, "a 0 hash would make the comparison vacuous");
            Assert.AreEqual(restored.ReplayTick0Hash, rebuilt.ReplayTick0Hash);
        }

        [Test] // The rebuilt world must equal what the recording says its tick 0 hashed to — this is the
               // comparison a verifier reports on, and it also pins GetStateHash == the full-state hash.
        public void Reconstruct_MatchesTheRecordedInitialStateHash()
        {
            var data = Record(0, 1);

            var rebuilt = Play(data, KlothoEngine.ReplayInitialState.Reconstruct);

            Assert.AreNotEqual(0, data.Metadata.InitialStateHash, "nothing recorded a hash — the gate would be vacuous");
            Assert.AreEqual(data.Metadata.InitialStateHash, rebuilt.ReplayTick0Hash);
        }

        // ── V-5: the forged snapshot ───────────────────────────────────────

        [Test] // The reason this plan exists. A forger who rewrites the snapshot AND its hash is consistent
               // with itself, so the restore path verifies the forgery. Reconstruction never reads those
               // bytes, so the substituted world simply does not appear — and the disagreement surfaces.
        public void Reconstruct_DoesNotInheritAForgedSnapshot()
        {
            var forgedWorld = Record(0);            // one participant
            var honest = Record(0, 1);              // two — a different tick 0
            var real = honest.Metadata.InitialStateHash;
            Assert.AreNotEqual(forgedWorld.Metadata.InitialStateHash, real,
                "the two rosters must build different worlds or this gate proves nothing");

            // A self-consistent forgery: someone else's world, stamped with that world's own hash.
            var meta = (ReplayMetadata)honest.Metadata;
            meta.InitialStateSnapshot = forgedWorld.Metadata.InitialStateSnapshot;
            meta.InitialStateHash = forgedWorld.Metadata.InitialStateHash;

            var restored = Play(honest, KlothoEngine.ReplayInitialState.RestoreSnapshot);
            Assert.AreEqual(meta.InitialStateHash, restored.ReplayTick0Hash,
                "the forgery is internally consistent, so the restore path cannot tell — that is the gap");

            var rebuilt = Play(honest, KlothoEngine.ReplayInitialState.Reconstruct);
            Assert.AreEqual(real, rebuilt.ReplayTick0Hash,
                "the rebuild must land on the roster's own world, not the injected one");
            Assert.AreNotEqual(meta.InitialStateHash, rebuilt.ReplayTick0Hash,
                "and it must therefore disagree with the forged claim");
        }

        [Test] // The weaker half (Plan D-7): a snapshot that disagrees with its own recorded hash is
               // corruption or a careless forgery, and the restore path catches that much on its own.
        public void Restore_HashDisagreesWhenTheSnapshotIsSwapped()
        {
            var other = Record(0);
            var honest = Record(0, 1);

            ((ReplayMetadata)honest.Metadata).InitialStateSnapshot = other.Metadata.InitialStateSnapshot;

            var restored = Play(honest, KlothoEngine.ReplayInitialState.RestoreSnapshot);

            Assert.AreNotEqual(honest.Metadata.InitialStateHash, restored.ReplayTick0Hash,
                "restoring foreign bytes must not land on the hash the file claims");
        }

        // ── per-player verified data: the second input a rebuild cannot see ──

        [Test] // The engine reads per-player verified data (entitlements) through the network service, and a
               // replay session has none — so without the file carrying it, playback decodes "nothing was
               // issued" and a rebuilt world silently differs. This is that value coming back.
        public void Replay_RecoversTheRecordedEntitlements()
        {
            var data = Record(0, 1);
            ((ReplayData)data).SetInitialEntitlements(new[]
            {
                new byte[] { 0x11, 0x22 },   // roster[0] = player 0
                new byte[] { 0x33 },         // roster[1] = player 1
            });

            var engine = Play(data, KlothoEngine.ReplayInitialState.Reconstruct);

            CollectionAssert.AreEqual(new byte[] { 0x11, 0x22 }, engine.GetPlayerEntitlement(0));
            CollectionAssert.AreEqual(new byte[] { 0x33 }, engine.GetPlayerEntitlement(1));
        }

        [Test] // A match with no issuer has no entitlements, and that is most matches. Playback must answer
               // null rather than inventing something — and must not throw on the empty record.
        public void Replay_WithoutRecordedEntitlements_AnswersNull()
        {
            var data = Record(0, 1);

            var engine = Play(data, KlothoEngine.ReplayInitialState.Reconstruct);

            Assert.IsNull(engine.GetPlayerEntitlement(0));
            Assert.IsNull(engine.GetPlayerEntitlement(1));
        }

        [Test] // The order in GetPlayerEntitlement is a contract: network service first, replay table last.
               // Reversed, a LIVE match would read an empty table and seed tick 0 as though nothing had been
               // issued — the game breaks, not the verifier, and it does so quietly.
        public void LiveSession_PrefersTheNetworkService_OverTheReplayTable()
        {
            var sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 50);
            var net = new FakeSdNetworkService();
            // ServerDriven on purpose: the engine only holds an IServerDrivenNetworkService reference in
            // that mode, and that reference is what "a live session has someone to ask" means here.
            var engine = new KlothoEngine(new SimulationConfig { Mode = NetworkMode.ServerDriven }, new SessionConfig());
            engine.Initialize(sim, net, new LogCapture());
            engine.SetCommandFactory(new CommandFactory());

            // A replay table entry for a player the live service also knows about.
            engine.SetReplayEntitlement(0, new byte[] { 0xDE, 0xAD });

            // FakeSdNetworkService answers null for entitlements, which is exactly the live "nothing was
            // issued" answer — the point is that it ANSWERS, so the table must not be consulted.
            Assert.IsNull(engine.GetPlayerEntitlement(0),
                "a live session must take the network service's answer, even when that answer is 'none'");
        }

        [Test] // Commands are pooled and the pool does not clear fields, so a join that carries no entitlement
               // must ASSIGN null rather than skip the field — otherwise the next joiner inherits the last
               // one's bytes and every node seeds that player's world from them. Value-type fields never had
               // this problem; this is the first reference payload on a command.
        public void PooledJoinCommand_DoesNotCarryThePreviousJoinersEntitlement()
        {
            var first = CommandPool.Get<PlayerJoinCommand>();
            first.JoinedPlayerId = 7;
            first.Entitlement = new byte[] { 0xC0, 0xDE };
            CommandPool.Return(first);

            var second = CommandPool.Get<PlayerJoinCommand>();
            try
            {
                // The deserialize path is what a replayed join goes through, and it must clear the field
                // even when the recorded join had nothing.
                var bytes = new byte[64];
                var writer = new SpanWriter(bytes);
                var source = new PlayerJoinCommand { PlayerId = 1, Tick = 5, JoinedPlayerId = 9, Entitlement = null };
                source.Serialize(ref writer);
                var reader = new SpanReader(bytes, 0, writer.Position);
                second.Deserialize(ref reader);

                Assert.AreEqual(9, second.JoinedPlayerId);
                Assert.IsNull(second.Entitlement,
                    "a rented command must not answer with the previous join's bytes");
            }
            finally
            {
                CommandPool.Return(second);
            }
        }

        // ── V-6: no roster, no rebuild ─────────────────────────────────────

        [Test] // An SD client's recording has no roster: its tick 0 came from the server. Asking for a
               // rebuild there must fall back to the snapshot rather than throw or build an empty world —
               // and it must SAY it fell back, because a verified verdict means less on that path.
        public void Reconstruct_WithoutRoster_FallsBackToRestore()
        {
            var data = Record(0, 1);
            ((ReplayMetadata)data.Metadata).InitialRoster.Clear();

            KlothoEngine engine = null;
            Assert.DoesNotThrow(() => engine = Play(data, KlothoEngine.ReplayInitialState.Reconstruct));

            Assert.IsFalse(engine.ReplayTick0Reconstructed, "no roster means no rebuild");
            Assert.AreEqual(data.Metadata.InitialStateHash, engine.ReplayTick0Hash,
                "the fallback must still land on the recorded world");
        }
    }
}
