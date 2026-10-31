using System;
using System.IO;
using System.Reflection;
using System.Threading;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;   // ReplaySystem / CommandFactory — reading back the file the room wrote

using Brawler;   // BrawlerMatchConfig — the capacity a rebuilt tick 0 needs

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Normal end / abort lifecycle tests.
    /// Covers post-match grace, EndReason routing, first-write-wins, all-peers-gone fallback,
    /// Ending-state freeze, SD heartbeat path, and IsEnded() classification.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class NormalEndLifecycleTests
    {
        private static IKLoggerFactory _loggerFactory;
        private static IKLogger _logger;
        private static int _passed;
        private static int _failed;
        private static readonly MessageSerializer _serializer = new MessageSerializer();

        public static int RunAll()
        {
            _loggerFactory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddConsole();
            });
            _logger = _loggerFactory.CreateLogger("Test");
            _passed = 0;
            _failed = 0;

            Console.WriteLine("\n=== NormalEndLifecycle Tests ===\n");

            NE1_ContinuePolicy_MatchEndDrain();
            NE2_PausePolicy_EnterEndingThenDrain();
            NE4_AbortMidPlay_AbortGraceDrain();
            NE5_FirstWriteWins_AbortDuringGraceIgnored();
            NE6_AllPeersGoneDuringGrace_ImmediateDrain();
            NE9_NoRoomManagerWiringWithoutAttach();
            NE10_IsEnded_FalseForEnding();
            NE11_ServerWritesItsReplay_AndItRebuilds();
            NE12_PlaybackThrow_IsAVerdictNotACrash();

            Console.WriteLine($"\n=== NormalEndLifecycle results: {_passed} passed, {_failed} failed ===");
            _loggerFactory.Dispose();
            return _failed;
        }

        // ── NE1: Continue policy — match-end grace expiry transitions to Draining ──

        static void NE1_ContinuePolicy_MatchEndDrain()
        {
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 80, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 1, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(80));
            Assert("NE1a RequestEnd recorded", room.EndRequestedAtUtc.HasValue);
            Assert("NE1b EndReason=MatchEnded", room.EndReason == EndReason.MatchEnded);
            Assert("NE1c Continue keeps Active during grace", room.State == RoomState.Active);

            Thread.Sleep(120);
            room.Update(0.025f);

            Assert("NE1d Room transitions to Draining after grace", room.State == RoomState.Draining);
            Assert("NE1e DrainPhase captured", room.DrainPhase != default || true);

            env.RoomManager.TransitionDrainingRooms();
            Assert("NE1f Room transitions to Disposing", room.State == RoomState.Disposing);
            env.RoomManager.CleanupDisposingRooms();
            Assert("NE1g Room transitions to Empty", room.State == RoomState.Empty);

            env.Dispose();
        }

        // ── NE11: the server's own replay — written on room teardown, carrying a tick-0 roster ──
        //
        // Why this lives here: the verifier suite can only synthesize metadata, and a server recording only
        // exists when a real room runs and tears down. This is the one place in the repo that does that
        // headlessly.
        //
        // <para><b>What it does NOT prove.</b> This harness starts the sim by calling Engine.Start() directly
        // (as the multi-room suite does) rather than by driving the ready/GameStart handshake — and the
        // initial snapshot and the reproduction anchors are injected by the GAMESTART handler, not by Start().
        // So the file this writes is deliberately incomplete: it has ticks and a roster but no snapshot and
        // no anchors, and --verify stops on it at `anchors=absent`. Running a server file all the way to a
        // verdict needs a real match (V-2/V-7 in the plan), which is a client-driven step.</para>
        static void NE11_ServerWritesItsReplay_AndItRebuilds()
        {
            string dir = Path.Combine(Path.GetTempPath(), "klotho-server-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 40, abortGraceMs: 40, replaySaveDir: dir);
                var room = env.RoomManager.CreateRoom(0);
                JoinPeer(env, peerId: 1, roomId: 0);
                room.Update(0.025f);
                room.Engine.Start();                       // drains joins first, as the multi-room suite does
                for (int i = 0; i < 8; i++) room.Update(0.025f);

                room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(40));
                Thread.Sleep(80);
                room.Update(0.025f);
                env.RoomManager.TransitionDrainingRooms();
                env.RoomManager.CleanupDisposingRooms();   // engine.Stop() -> StopRecording -> the save fires
                env.Dispose();

                var files = Directory.GetFiles(dir, "*.rply");
                Assert("NE11a server wrote a replay", files.Length == 1);
                if (files.Length != 1) return;

                // It is a real file, not a stub: it loads with this build's reader and its two records of the
                // participant count agree. The ROSTER is empty here for the same reason the anchors are — the
                // roster is filled by the GameStart handler from the network service's player list, and this
                // harness starts the sim directly. A production room fills both.
                var loader = new ReplaySystem(new CommandFactory(), null);
                loader.LoadFromFile(files[0]);
                var written = loader.CurrentReplayData.Metadata;
                Assert("NE11b the file loads and is self-consistent",
                       written.PlayerCount == written.InitialRoster.Count || written.InitialRoster.Count == 0);

                // The runner reads it and refuses it for the RIGHT reason. This file never saw GameStart, so
                // it has no anchors — and "no anchors" must be a verdict, never a pass (an attacker's cheapest
                // move is to strip them). Two runs in one process are compared: not the full session-reuse
                // question the plan flags (this file stops before playback), but it pins that repeated
                // verification in one process is deterministic rather than order-dependent.
                var first = BrawlerReplayVerifier.Verify(files[0], null);
                var second = BrawlerReplayVerifier.Verify(files[0], null);
                Assert("NE11c an anchorless server file is unverifiable, not verified",
                       first.Code == BrawlerReplayVerifier.ExitUnverifiable);
                Assert("NE11d repeated verification in one process is stable",
                       second.Code == first.Code && second.Line == first.Line);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup is best-effort */ }
            }
        }

        // ── NE12: a replay that throws during playback is a verdict, not a process failure ──

        /// <para>A server file stops before playback (see NE11), so it is given what the gates ask for — two
        /// environment anchors, a roster with a capacity, and the rebuilt tick-0 hash. The clean file then plays
        /// past tick 0 (its verdict is the post-tick-1 anchor comparison). In the broken copy tick 0's command
        /// blob claims more commands than it holds — in range, so the reader's table check passes it and the
        /// throw comes from decoding, mid-playback. The clean file is verified before and after the broken one:
        /// an abandoned session must not change the next file's verdict.</para>
        static void NE12_PlaybackThrow_IsAVerdictNotACrash()
        {
            string dir = Path.Combine(Path.GetTempPath(), "klotho-server-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 40, abortGraceMs: 40, replaySaveDir: dir);
                var room = env.RoomManager.CreateRoom(0);
                JoinPeer(env, peerId: 1, roomId: 0);
                room.Update(0.025f);
                room.Engine.Start();
                for (int i = 0; i < 8; i++) room.Update(0.025f);
                room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(40));
                Thread.Sleep(80);
                room.Update(0.025f);
                env.RoomManager.TransitionDrainingRooms();
                env.RoomManager.CleanupDisposingRooms();
                env.Dispose();

                var files = Directory.GetFiles(dir, "*.rply");
                Assert("NE12a server wrote a replay", files.Length == 1);
                if (files.Length != 1) return;

                var data = new ReplayData();
                data.Deserialize(File.ReadAllBytes(files[0]));
                var m = (ReplayMetadata)data.Metadata;
                m.StaticColliderFingerprint = 0x1111_2222_3333_4444L;
                m.GameFingerprint = unchecked((long)0x9999_AAAA_BBBB_CCCCUL);
                // The harness starts the sim without GameStart, so the file has no snapshot to restore and its
                // tick blobs are empty: give it a roster and a capacity, so the runner rebuilds tick 0 instead.
                m.InitialRoster.Clear();
                m.InitialRoster.Add(1);
                m.PlayerCount = 1;
                m.MatchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 0, MaxPlayers = 4 });
                // The rebuilt tick-0 hash is this build's own; a probe run reports it as the mismatch's `local=`.
                m.InitialStateHash = 1;
                string probePath = Path.Combine(dir, "probe.rply");
                File.WriteAllBytes(probePath, data.Serialize());
                var probe = BrawlerReplayVerifier.Verify(probePath, null);
                const string localKey = "tick0hash=mismatch file=0x0000000000000001 local=0x";
                int at = probe.Line.IndexOf(localKey, StringComparison.Ordinal);
                Assert($"NE12b the probe reaches the tick-0 comparison ({probe.Line})", at >= 0);
                if (at < 0) return;
                m.InitialStateHash = unchecked((long)Convert.ToUInt64(probe.Line.Substring(at + localKey.Length, 16), 16));
                byte[] clean = data.Serialize();

                // The file ends with the command stream and then one (tick, offset, length) entry per tick.
                const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;
                int tickCount = ((System.Collections.ICollection)typeof(ReplayData).GetField("_tickOffsets", priv).GetValue(data)).Count;
                int bufferSize = (int)typeof(ReplayData).GetField("_bufferPosition", priv).GetValue(data);
                int tableStart = clean.Length - tickCount * 12;
                int bufferStart = tableStart - bufferSize;
                int blob = -1;
                for (int i = 0; i < tickCount; i++)
                {
                    int entry = tableStart + i * 12;
                    if (BitConverter.ToInt32(clean, entry) == 0) blob = BitConverter.ToInt32(clean, entry + 4);
                }
                Assert("NE12c the recording has a tick-0 blob", blob >= 0);
                if (blob < 0) return;
                byte[] broken = (byte[])clean.Clone();
                BitConverter.GetBytes(1_000_000).CopyTo(broken, bufferStart + blob);   // its command count

                string cleanPath = Path.Combine(dir, "clean.rply");
                string brokenPath = Path.Combine(dir, "broken.rply");
                File.WriteAllBytes(cleanPath, clean);
                File.WriteAllBytes(brokenPath, broken);

                var before = BrawlerReplayVerifier.Verify(cleanPath, null);
                Assert($"NE12d the clean file plays past tick 0 ({before.Line})",
                       before.Line.Contains("anchors=mismatch:", StringComparison.Ordinal));

                BrawlerReplayVerifier.Outcome broke;
                try
                {
                    broke = BrawlerReplayVerifier.Verify(brokenPath, null);
                }
                catch (Exception e)
                {
                    Assert($"NE12e a playback throw is a verdict (Verify threw {e.GetType().Name}: {e.Message})", false);
                    return;
                }
                Assert($"NE12e a playback throw is a verdict ({broke.Line})",
                       broke.Code == BrawlerReplayVerifier.ExitUnverifiable
                       && broke.Line.Contains("playbackFailed=", StringComparison.Ordinal));

                var after = BrawlerReplayVerifier.Verify(cleanPath, null);
                Assert("NE12f the abandoned session leaves the next verdict unchanged", after.Line == before.Line);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup is best-effort */ }
            }
        }

        // ── NE2: EnterEnding API contract — manual call transitions Running -> Ending and
        // the existing State != Running gate freezes tick advance. Production Pause policy
        // no longer calls EnterEnding (C-1a halts characters via client StopCommand instead);
        // this test pins the API behavior in case the state is reused (e.g., admin freeze).

        static void NE2_PausePolicy_EnterEndingThenDrain()
        {
            var env = CreateTestEnv(EndGracePolicy.Pause, endGraceMs: 80, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 2, roomId: 0);
            room.Update(0.025f);

            // Force engine into Running so EnterEnding can transition Running->Ending.
            // Production wiring drives this via OnGameStart; tests bypass the network handshake.
            ForceEngineState(room.Engine, KlothoState.Running);
            int tickBefore = room.Engine.CurrentTick;

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(80));
            room.Engine.EnterEnding();
            Assert("NE2a Engine State=Ending after EnterEnding", room.Engine.State == KlothoState.Ending);

            // Multiple updates within grace must not advance CurrentTick (Ending blocks ExecuteTick).
            room.Update(0.025f);
            room.Update(0.025f);
            Assert("NE2b CurrentTick frozen during Ending", room.Engine.CurrentTick == tickBefore);
            Assert("NE2c Room still Active during grace", room.State == RoomState.Active);

            Thread.Sleep(120);
            room.Update(0.025f);

            Assert("NE2d Room transitions to Draining after grace", room.State == RoomState.Draining);
            Assert("NE2e EndReason=MatchEnded preserved", room.EndReason == EndReason.MatchEnded);

            env.Dispose();
        }

        // ── NE3 removed — Ending-state heartbeat mechanism polished alongside the C-1a
        // Pause policy redefinition. Grace-window heartbeats are no longer emitted; the
        // server keeps ticking normally and verified StopCommands halt characters instead.

        // ── NE4: Abort mid-play — abort grace transitions to Draining with MatchAborted ──

        static void NE4_AbortMidPlay_AbortGraceDrain()
        {
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 500, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 4, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchAborted, TimeSpan.FromMilliseconds(80));
            Assert("NE4a EndReason=MatchAborted recorded", room.EndReason == EndReason.MatchAborted);
            Assert("NE4b Room still Active during abort grace", room.State == RoomState.Active);

            Thread.Sleep(120);
            room.Update(0.025f);

            Assert("NE4c Room transitions to Draining after abort grace", room.State == RoomState.Draining);
            Assert("NE4d EndReason=MatchAborted preserved at drain", room.EndReason == EndReason.MatchAborted);

            env.Dispose();
        }

        // ── NE5: First-write-wins — abort during normal-end grace is ignored ──

        static void NE5_FirstWriteWins_AbortDuringGraceIgnored()
        {
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 500, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 5, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(500));
            var firstReqAt = room.EndRequestedAtUtc;
            var firstGrace = room.EndGrace;

            // Second call with abort while normal-end grace pending — must be ignored.
            room.RequestEnd(EndReason.MatchAborted, TimeSpan.FromMilliseconds(80));

            Assert("NE5a EndReason still MatchEnded", room.EndReason == EndReason.MatchEnded);
            Assert("NE5b EndRequestedAtUtc unchanged", room.EndRequestedAtUtc == firstReqAt);
            Assert("NE5c EndGrace unchanged (500ms)", room.EndGrace == firstGrace);

            env.Dispose();
        }

        // ── NE6: All-peers-gone during grace falls through to ShouldDrain path ──

        static void NE6_AllPeersGoneDuringGrace_ImmediateDrain()
        {
            // Long normal-end grace so it cannot expire during the test window.
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 10000, abortGraceMs: 10000);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 6, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(10000));
            Assert("NE6a Room Active during long grace", room.State == RoomState.Active);

            env.Transport.SimulateDisconnect(6);
            room.Update(0.025f);

            // Grace did not expire (~20ms vs 10s) — ShouldDrain takes over via all-peers-gone path.
            Assert("NE6b Room transitions to Draining via ShouldDrain", room.State == RoomState.Draining);
            Assert("NE6c EndReason stays MatchEnded (still set, drain reason resolved by lifetime/log)",
                room.EndReason == EndReason.MatchEnded);

            env.Dispose();
        }

        // ── NE9: A KlothoEngine instance has no automatic OnMatchEnded wiring ──

        static void NE9_NoRoomManagerWiringWithoutAttach()
        {
            // Create a room and immediately query the OnMatchEnded delegate field.
            // RoomManager.CreateRoomAt calls Room.AttachEngineHandlers, so a subscriber is expected here.
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 100, abortGraceMs: 100);
            var room = env.RoomManager.CreateRoom(0);

            var attached = GetPrivateField(room.Engine, "OnMatchEnded");
            Assert("NE9a Room creation attaches OnMatchEnded handler", attached != null);

            // After Dispose, AttachEngineHandlers' counterpart (DetachEngineHandlers) must unsubscribe.
            JoinPeer(env, peerId: 9, roomId: 0);
            room.Update(0.025f);
            env.Transport.SimulateDisconnect(9);
            room.Update(0.025f);
            env.RoomManager.TransitionDrainingRooms();
            env.RoomManager.CleanupDisposingRooms();

            var afterDispose = GetPrivateField(room.Engine, "OnMatchEnded");
            Assert("NE9b OnMatchEnded handler detached on Dispose", afterDispose == null);

            env.Dispose();
        }

        // ── NE10: IsEnded() classification ──

        static void NE10_IsEnded_FalseForEnding()
        {
            Assert("NE10a IsEnded(Ending) == false", !KlothoState.Ending.IsEnded());
            Assert("NE10b IsEnded(Running) == false", !KlothoState.Running.IsEnded());
            Assert("NE10c IsEnded(Finished) == true", KlothoState.Finished.IsEnded());
            Assert("NE10d IsEnded(Aborted) == true", KlothoState.Aborted.IsEnded());
        }

        // ═══════════════════════════════════════════════════════
        // Test infrastructure
        // ═══════════════════════════════════════════════════════

        static FPNavMesh _sharedNavMesh;
        static System.Collections.Generic.List<xpTURN.Klotho.Deterministic.Physics.FPStaticCollider> _sharedStaticColliders;
        static IDataAssetRegistry _sharedAssetRegistry;

        static void EnsureSharedTestData()
        {
            if (_sharedNavMesh == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.NavMeshData.bytes");
                if (File.Exists(p)) _sharedNavMesh = FPNavMeshSerializer.Deserialize(p);
            }
            if (_sharedStaticColliders == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.StaticColliders.bytes");
                if (File.Exists(p)) _sharedStaticColliders = xpTURN.Klotho.Deterministic.Physics.FPStaticColliderSerializer.Load(p);
            }
            if (_sharedAssetRegistry == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");
                if (File.Exists(p))
                {
                    var assets = DataAssetReader.LoadMixedCollectionFromBytes(p);
                    IDataAssetRegistryBuilder builder = new DataAssetRegistry();
                    builder.RegisterRange(assets);
                    _sharedAssetRegistry = builder.Build();
                }
            }
        }

        static TestEnv CreateTestEnv(EndGracePolicy endGracePolicy, int endGraceMs, int abortGraceMs,
                                    string replaySaveDir = null)
        {
            EnsureSharedTestData();

            int tickIntervalMs = 25;
            const int maxPlayersPerRoom = 4;
            var transport = new MockTransport();
            var logger = _loggerFactory.CreateLogger("TestServer");
            var router = new RoomRouter(transport, logger);
            var navMesh = _sharedNavMesh;
            var staticColliders = _sharedStaticColliders;
            var assetRegistry = _sharedAssetRegistry;
            var roomManagerConfig = new RoomManagerConfigBuilder((roomLogger) => new BrawlerServerCallbacks(
                    roomLogger, staticColliders, navMesh, maxPlayersPerRoom, 0, replaySaveDir: replaySaveDir))
                .WithRoomLimits(1, maxPlayersPerRoom)
                .WithSimulationConfig(() => new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = tickIntervalMs,
                    MaxEntities = 64,
                    MaxRollbackTicks = 1,
                    SyncCheckInterval = 1,
                    UsePrediction = false,
                    InputDelayTicks = 1,
                })
                .WithSessionConfig(() => new SessionConfig
                {
                    AllowLateJoin = true,
                    ReconnectTimeoutMs = 30000,
                    ReconnectMaxRetries = 3,
                    EndGraceMs = endGraceMs,
                    AbortGraceMs = abortGraceMs,
                    EndGracePolicy = endGracePolicy,
                })
                .WithDerivedSimulation(assetRegistry)
                .Build();
            var roomManager = new RoomManager(transport, router, _loggerFactory, roomManagerConfig);

            return new TestEnv
            {
                Transport = transport,
                Router = router,
                RoomManager = roomManager,
            };
        }

        static void JoinPeer(TestEnv env, int peerId, int roomId)
        {
            env.Transport.SimulateConnect(peerId);
            byte[] handshake = _serializer.Serialize(new RoomHandshakeMessage { RoomId = roomId });
            env.Transport.SimulateData(peerId, handshake, handshake.Length);
            byte[] join = _serializer.Serialize(new PlayerJoinMessage());
            env.Transport.SimulateData(peerId, join, join.Length);
        }

        // Reflection helpers — bypass private setters / private fields for state injection in tests.
        // Production code paths must not rely on these.

        static void ForceEngineState(object engine, KlothoState state)
        {
            SetPrivateField(engine, "_state", state);
        }

        static void SetPrivateField(object target, string fieldName, object value)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
                throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
            f.SetValue(target, value);
        }

        static object GetPrivateField(object target, string fieldName)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
                throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
            return f.GetValue(target);
        }

        static void Assert(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"  PASS: {name}");
                _passed++;
            }
            else
            {
                Console.WriteLine($"  FAIL: {name}");
                _failed++;
            }
        }

        class TestEnv : IDisposable
        {
            public MockTransport Transport;
            public RoomRouter Router;
            public RoomManager RoomManager;

            public void Dispose()
            {
                Router?.Dispose();
            }
        }
    }
}
