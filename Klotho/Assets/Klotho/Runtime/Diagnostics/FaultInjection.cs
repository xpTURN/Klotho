#if KLOTHO_FAULT_INJECTION
// =====================================================================
// Test scenario reference — apply via faultinjectionconfig.json or set
// the static fields directly from a test [SetUp] before the run begins.
//
// ── A. RTT 200ms ─────────────────────────────────────────────────────
//   Title:    Bootstrap handshake + VerifiedState delivery under RTT.
//   Setup:    EmulatedRttMs = 200       (apply on BOTH server and client)
//   Expected: Handshake completes within ~RTT × handshake-rounds; ticks
//             progress; server↔client state hash remains equal; no
//             disconnect / desync.
//
// ── B. Server GC pause 1s ────────────────────────────────────────────
//   Title:    Server tick stall recovery (accumulator catch-up).
//   Setup:    ServerGcPauseMs     = 1000   (server only)
//             ServerGcPauseAtTick = N      (e.g. 100 — mid-match)
//   Expected: Single 1000ms server-tick sleep at tick=N. Server resumes
//             via accumulator catch-up (drift trends negative briefly).
//             Client absorbs the gap silently (verified-batch burst);
//             determinism preserved.
//
// ── C. Spawn cmd drop ────────────────────────────────────────────────
//   Title:    Permanent spawn-cmd drop → state-driven retry self-heal.
//   Setup:    DropSpawnCommandPlayerIds = { LocalPlayerId } (client only)
//   Expected: Client emits a fresh SpawnCharacterCommand every
//             SpawnRetryInterval (20 ticks) but the cmd never leaves the
//             local boundary. Character never spawns for that player.
//             PredResim falls back to predictor; server substitutes
//             EmptyCommand. No reject (cmd never reaches server).
//             Removing the player id mid-match recovers spawn on the
//             next retry (optional verification).
//
// ── D. Bootstrap ack suppress ────────────────────────────────────────
//   Title:    BOOTSTRAP_TIMEOUT_MS path → FullState resync recovery.
//   Setup:    SuppressBootstrapAckPlayerIds = { LocalPlayerId }
//             (apply on the client whose ack should be skipped)
//   Expected: That client never sends PlayerBootstrapReadyMessage.
//             Server reaches BOOTSTRAP_TIMEOUT_MS (1000ms), unicasts a
//             FullState resync to unacked peers, and CompleteBootstrap
//             flips the engine BootstrapPending → Running. Suppressed
//             client recovers via the FullState restore path.
//
// ── E. Force spawn retry (Duplicate reject) ──────────────────────────
//   Title:    Server-side Duplicate reject → CommandRejected unicast.
//   Setup:    ForceSpawnRetryPlayerIds = { LocalPlayerId } (client only)
//   Expected: Client keeps emitting SpawnCharacterCommand on cooldown
//             even after the character exists. Server HandleSpawn guard
//             rejects each as Duplicate, server emits CommandRejected
//             unicast (token-bucket throttled to ~10/sec/peer); client
//             clears spawn cooldown on receipt.
//
// ── F. Force tick offset (PastTick reject) ───────────────────────────
//   Title:    Server-side PastTick reject → CommandRejected unicast.
//   Setup:    ForceTickOffsetDelta = -10  (client only; negative shifts
//             cmd.Tick into the past)
//   Expected: All locally-sent cmds carry a tick that the server has
//             already executed → ServerInputCollector hits the
//             `tick <= _lastExecutedTick` branch and emits PastTick.
//             Phase-1 self-heal still allows the initial spawn to
//             succeed once the offset is absorbed.
// =====================================================================

using System.Collections.Generic;

namespace xpTURN.Klotho.Diagnostics
{
    /// <summary>
    /// Static toggles for reproducing fault scenarios from code (RTT emulation, server GC pause,
    /// spawn-cmd drop, bootstrap-ack suppression). Compiled out unless KLOTHO_FAULT_INJECTION is defined,
    /// so production builds carry zero overhead.
    /// </summary>
    public static class FaultInjection
    {
        /// <summary>
        /// Artificial RTT (one-way delay applied at TestTransport receive).
        /// EmulatedRttMs / 2 is added per direction so a round-trip totals EmulatedRttMs.
        /// </summary>
        public static int EmulatedRttMs;

        /// <summary>
        /// RTT spike injection schedule. Entry = (atSecondsFromMatchStart, newRttMs).
        /// Driver polls anchor-elapsed time once Phase == Playing and overwrites EmulatedRttMs
        /// at each entry's atSec. Empty list = disabled (static EmulatedRttMs only).
        /// Config-replicated: each client loads the same schedule from faultinjectionconfig.json
        /// so all clients trigger spikes at the same anchor offset (drift = match-start receive jitter).
        /// </summary>
        public static readonly List<(float atSec, int rttMs)> EmulatedRttSchedule = new List<(float, int)>();

        /// <summary>
        /// Disconnect injection schedule. Entry = (atSecondsFromMatchStart, durationSec, playerId).
        /// Driver calls DisconnectPeer(playerId) at atSec, then reconnects after durationSec elapses.
        /// playerId = null disconnects all non-host peers. Empty list = disabled.
        /// </summary>
        public static readonly List<(float atSec, float durationSec, int? playerId)> EmulatedDisconnectSchedule = new List<(float, float, int?)>();

        /// <summary>
        /// Server GC pause: blocks the server tick once when CurrentTick == ServerGcPauseAtTick,
        /// then auto-resets the trigger so it fires only once per arming.
        /// </summary>
        public static int ServerGcPauseMs;

        /// <summary>
        /// Tick at which ServerGcPauseMs fires. -1 disables the trigger.
        /// </summary>
        public static int ServerGcPauseAtTick = -1;

        /// <summary>
        /// Drop SpawnCharacterCommand on the listed local players (Brawler client side).
        /// </summary>
        public static readonly HashSet<int> DropSpawnCommandPlayerIds = new HashSet<int>();

        /// <summary>
        /// Suppress PlayerBootstrapReadyMessage from the listed local players to exercise the
        /// server-side bootstrap timeout path.
        /// </summary>
        public static readonly HashSet<int> SuppressBootstrapAckPlayerIds = new HashSet<int>();

        /// <summary>
        /// Duplicate path: force the listed local players to keep emitting SpawnCharacterCommand
        /// on cooldown even after their character already exists (bypasses the HasOwnCharacter gate in
        /// BrawlerSimulationCallbacks.OnPollInput). Each retry triggers the server-side Duplicate reject
        /// (PlatformerCommandSystem.HandleSpawn TryFindCharacter guard) → CommandRejectedMessage unicast.
        /// </summary>
        public static readonly HashSet<int> ForceSpawnRetryPlayerIds = new HashSet<int>();

        /// <summary>
        /// PastTick path: shift outgoing cmd.Tick by this delta on the local client (negative = past).
        /// Applied at KlothoEngine.InputCommand for ALL locally-sent cmds. With a negative delta larger than
        /// InputDelayTicks, server's ServerInputCollector hits the `tick &lt;= _lastExecutedTick` branch and
        /// emits RejectionReason.PastTick → CommandRejectedMessage unicast. Token bucket throttles to
        /// ~10 reject msgs/sec/peer.
        /// </summary>
        public static int ForceTickOffsetDelta;

        /// <summary>Reset all toggles to defaults. Call from test [SetUp] / [TearDown] for hygiene.</summary>
        public static void Reset()
        {
            EmulatedRttMs = 0;
            EmulatedRttSchedule.Clear();
            EmulatedDisconnectSchedule.Clear();
            ServerGcPauseMs = 0;
            ServerGcPauseAtTick = -1;
            DropSpawnCommandPlayerIds.Clear();
            SuppressBootstrapAckPlayerIds.Clear();
            ForceSpawnRetryPlayerIds.Clear();
            ForceTickOffsetDelta = 0;
        }
    }
}
#endif
