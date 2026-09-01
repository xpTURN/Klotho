using System;
using System.IO;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;


using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Serialization;
using Brawler;

namespace xpTURN.Klotho.BrawlerDedicatedServer
{
    public class BrawlerServerCallbacks : ISimulationCallbacks
    {
        private readonly IKLogger _logger;

        private readonly List<FPStaticCollider> _staticColliders;
        private readonly FPNavMesh _navMesh;
        private readonly FPNavMeshRebakeSnapshot _rebakeSnapshot;   // shared per stage; null → build one here
        private readonly List<IDataAsset> _dataAssets;

        private readonly int _maxPlayers;
        private readonly int _botCount;
        private readonly int _stageId;
        private readonly string _replaySaveDir;    // null → this room does not write a replay (the default)
        private readonly string _matchInstanceId;  // lobby key for this match, or null; names the saved file
#if DEBUG
        private readonly long _devAbortAfterMs; // DEV: >0 → StateDivergence-abort N ms into the match (abort-path e2e)
        private IKlothoEngine _engine;          // captured at OnInitializeWorld for the dev abort trigger
#endif

        /// <param name="rebakeSnapshot">
        /// The stage's shared rebake snapshot, built once at boot (see
        /// <c>BrawlerBuildingShapes.CreateSnapshot</c>). MUST be the snapshot of
        /// <paramref name="navMesh"/> — the rebake carves from the snapshot's base while the nav
        /// systems query <paramref name="navMesh"/>, so two different meshes would put the two out
        /// of step with nothing to refuse it. Resolve both from the same stage key.
        /// <para>null → this room builds its own, which costs a base insertion (and, in a cold
        /// process, the JIT) on whatever thread constructs the room. Fine for a single-room host
        /// or a test; not for the multi-room server, where that thread is the shared main loop.</para>
        /// </param>
        public BrawlerServerCallbacks(IKLogger logger,
                                        List<FPStaticCollider> staticColliders,
                                        FPNavMesh navMesh,
                                        int maxPlayers,
                                        int botCount,
                                        List<IDataAsset> dataAssets = null,
                                        int stageId = 0,
                                        long devAbortAfterMs = 0,
                                        FPNavMeshRebakeSnapshot rebakeSnapshot = null,
                                        string replaySaveDir = null,
                                        string matchInstanceId = null)
        {
            _logger = logger;
            _staticColliders = staticColliders;
            _navMesh = navMesh;
            _rebakeSnapshot = rebakeSnapshot;
            _dataAssets = dataAssets;

            _maxPlayers = maxPlayers;
            _botCount = botCount;
            _stageId = stageId;
            _replaySaveDir = replaySaveDir;
            _matchInstanceId = matchInstanceId;
#if DEBUG
            _devAbortAfterMs = devAbortAfterMs;
#endif
        }

        public FPNavMeshRebakeContext RebakeContext { get; private set; }

        public void RegisterSystems(EcsSimulation simulation)
        {
            BotFSMSystem botFSMSystem = null;

            var query       = new FPNavMeshQuery(_navMesh, _logger);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, _logger);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, _logger);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, _logger);
            agentSystem.SetAvoidance(new FPNavAvoidance());
            // Registers the NavMesh boundary as ORCA static obstacles (wall avoidance) and applies
            // the baked asset's own Agent Radius as the obstacle inset — both peers load the same
            // asset, so the clearance correction stays symmetric without a hand-synced constant.
            agentSystem.LoadNavMeshObstacles();
            if (agentSystem.DebugObstacleCount == 0)
                _logger?.KWarning($"[BrawlerServerCallbacks] ORCA obstacles empty — NavMesh obstacle wiring missing or boundary-free mesh");

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            // Building demo: BRAWLER_BUILDING_DEMO=<interval ticks> — headless-only (bot
            // command injection is tick-execution state; with clients connected every peer
            // would need the identical setting or hashes diverge).
            if (int.TryParse(System.Environment.GetEnvironmentVariable("BRAWLER_BUILDING_DEMO"), out int demoTicks) && demoTicks > 0)
            {
                botFSMSystem.SetBuildingDemo(demoTicks);
                _logger?.KWarning($"[BrawlerServerCallbacks] building demo enabled: every {demoTicks} ticks (headless-only)");
            }

            // Per-room rebake context. The snapshot behind it is per STAGE, not per room: when the
            // host hands one in, this is just a work-buffer allocation. Building it here instead
            // would cost a full base insertion (plus the JIT on the first room) on the caller's
            // thread — which on the multi-room server is the main loop, between the poll and the
            // room dispatch, so every room in the process waits on it.
            //
            // No snapshot supplied → fall back to building one, so a single-room host or a test
            // stays a one-liner. Throws on unsupported bases — building placement is then
            // unavailable for this stage, surfaced at load time.
            try
            {
                RebakeContext = _rebakeSnapshot != null
                    ? new FPNavMeshRebakeContext(_rebakeSnapshot)
                    : BrawlerBuildingShapes.CreateContext(_navMesh, _logger);
            }
            catch (System.Exception e)
            {
                _logger?.KWarning($"[BrawlerServerCallbacks] rebake snapshot unavailable for this stage: {e.Message}");
            }

            BrawlerSimSetup.RegisterSystems(simulation, _logger, _dataAssets, _staticColliders, botFSMSystem, _stageId, RebakeContext);
#if DEBUG
            // DEV: after BrawlerSimSetup so it runs last in the tick; fires the abort on the engine thread.
            if (_devAbortAfterMs > 0)
                simulation.AddSystem(
                    new DevAbortSystem(() => _engine?.AbortMatch(AbortReason.StateDivergence), _devAbortAfterMs),
                    SystemPhase.PostUpdate);
#endif
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            _logger?.KInformation($"[BrawlerServerCallbacks] OnInitializeWorld: seed={engine.RandomSeed}");
#if DEBUG
            _engine = engine; // capture for the dev abort trigger (called before any tick)
#endif
            ArmReplaySave(engine);
            BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
        }

        /// <summary>
        /// Writes this room's replay when recording stops. Opt-in (<c>--save-replays</c>): the engine records
        /// either way, so a server that never saves pays the buffer and gets nothing — that is the state this
        /// exists to end. The server's file is the only SD recording that carries a tick-0 roster (a client
        /// receives its initial state from us), which makes it the only one a verifier can REBUILD rather than
        /// restore, and it is the authority's own record when a result is disputed.
        ///
        /// <para>Hooked here because <c>OnInitializeWorld</c> is where a room first sees its engine, and it
        /// runs before any tick — well before <c>StopRecording</c>. The handler holds no state of its own: the
        /// recorder hands the finished data to <c>ReplaySystem</c>, which sets CurrentReplayData before this
        /// fires, so saving through the system writes exactly the recording that just ended.</para>
        /// </summary>
        private void ArmReplaySave(IKlothoEngine engine)
        {
            if (string.IsNullOrEmpty(_replaySaveDir)) return;
            if (engine is not KlothoEngine concrete || concrete.ReplaySystem == null) return;

            // A room that ran without a lobby has no instance id; name the file after the seed so two matches
            // in one process cannot collide. NEVER a fixed name — that is how a match erases its predecessor.
            string name = string.IsNullOrEmpty(_matchInstanceId)
                ? $"stage{_stageId}-seed{engine.RandomSeed}.rply"
                : SanitizeFileName(_matchInstanceId) + ".rply";
            string path = System.IO.Path.Combine(_replaySaveDir, name);

            concrete.ReplaySystem.OnRecordingStopped += _ =>
            {
                try
                {
                    concrete.ReplaySystem.SaveToFile(path);
                }
                catch (Exception e)
                {
                    // A room must not die because the disk did. The match result is already reported.
                    _logger?.KError(e, $"[BrawlerServerCallbacks] replay save failed: {path}");
                }
            };
        }

        /// <summary>The lobby's instance id is <c>{matchId}#{token}</c>; '#' and friends are not file names.</summary>
        private static string SanitizeFileName(string s)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '#' ? '_' : c);
            return sb.ToString();
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            // no-op: ServerInputCollector handles network input collection
        }

        // A late-joiner enters the world at its join tick — seed its entitlement loadout the same way
        // tick-0 players are seeded (OnInitializeWorld), so a restricted (e.g. "guest") late-joiner is
        // gated in-match. The server's verified entitlement is authoritative; clients receive the same
        // bytes via the late-join propagation, so every node seeds identical state at the join tick.
        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
        {
            BrawlerSimSetup.SeedOneLoadout(ref frame, engine, playerId);
        }
    }
}
