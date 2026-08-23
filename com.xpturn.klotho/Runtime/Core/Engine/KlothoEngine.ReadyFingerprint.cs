using System.Collections.Generic;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Outcome of the Ready-path setup comparison. A layout difference means the registered
    /// component-type set differs, which is state-hash input — the peers would diverge from tick 0,
    /// so the caller with transport control refuses the peer. An environment difference is outside
    /// the state hash and only surfaces when the simulation touches that geometry, so it stays a warning.
    /// </summary>
    public enum ReadyFingerprintVerdict
    {
        /// <summary>Nothing to report: equal, or one side did not provide a value.</summary>
        Ok,
        /// <summary>Component-registry layout differs — fatal, refuse the peer (unless the dev gate is on).</summary>
        LayoutMismatch,
    }

    public partial class KlothoEngine
    {
        /// <summary>
        /// Dev escape hatch for the Ready-path layout check: when true a layout mismatch is logged but
        /// not refused. Default false (refuse), and deliberately NOT part of ISimulationConfig — a guest
        /// runs the config it received over the wire, so a config-borne flag would always read its
        /// default there, which is exactly the peer (an Editor session against a dedicated server) that
        /// needs to turn the check off. Set from KlothoSessionSetup, per peer.
        /// </summary>
        public bool AllowLayoutMismatch { get; set; }

        // Per-peer suppression. A single bool would log the first peer and silence every other one,
        // which on a Ready exchange means the divergence you care about can be the one that never prints.
        private readonly HashSet<int> _readyLayoutMismatchLogged = new HashSet<int>();
        private readonly HashSet<int> _readyEnvMismatchLogged = new HashSet<int>();

        /// <summary>
        /// Component-registry layout fingerprint of this process. Stage- and rebake-invariant (it folds
        /// maxEntities, the sorted type id set, type names, slot capacity and component size, once at
        /// layout freeze), so it is comparable at any point in a session. 0 before the layout is frozen.
        /// </summary>
        public long GetLocalLayoutFingerprint() => xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutFingerprint;

        /// <summary>
        /// Environment-only fingerprint (static colliders XOR navmesh XOR the game's slot), registry
        /// excluded so it stays orthogonal to <see cref="GetLocalLayoutFingerprint"/>. Runtime rebakes
        /// move it, so it is only comparable before the match starts. 0 = "not provided".
        /// </summary>
        public long GetLocalEnvironmentFingerprint() => ComputeLocalEnvironmentFingerprintRaw();

        /// <summary>
        /// Pure comparison used by both halves: a difference counts only when BOTH sides provided a
        /// value. 0 stays the "not provided" sentinel, so an unwired or older peer is never refused.
        /// </summary>
        internal static bool FingerprintsDiffer(long local, long remote)
            => local != 0 && remote != 0 && local != remote;

        /// <summary>
        /// Receiver side of the Ready exchange. Compares the peer's fingerprints against this peer's and
        /// reports; the caller owns the reaction (refuse if it has transport control, otherwise leave).
        /// <paramref name="compareEnvironment"/> must be false once the match is running: the navmesh
        /// changes with runtime rebakes, so a joining peer's base mesh differs from an in-progress one
        /// for entirely legitimate reasons.
        /// </summary>
        public ReadyFingerprintVerdict CheckReadyFingerprints(
            int playerId, long remoteLayoutFingerprint, long remoteEnvironmentFingerprint, bool compareEnvironment)
        {
            var verdict = ReadyFingerprintVerdict.Ok;

            long localLayout = GetLocalLayoutFingerprint();
            if (FingerprintsDiffer(localLayout, remoteLayoutFingerprint))
            {
                verdict = ReadyFingerprintVerdict.LayoutMismatch;
                if (_readyLayoutMismatchLogged.Add(playerId))
                {
                    _logger?.KError(
                        $"[KlothoEngine] Component layout mismatch with playerId={playerId}: " +
                        $"local(types={xpTURN.Klotho.ECS.ComponentStorageRegistry.LayoutTypeCount}, " +
                        $"cleanup={xpTURN.Klotho.ECS.ComponentStorageRegistry.CleanupTypeCount})=0x{localLayout:X16} " +
                        $"remote=0x{remoteLayoutFingerprint:X16}. The registered component-type set is state-hash " +
                        $"input, so these peers would diverge from tick 0. Fix by loading the same assembly set on " +
                        $"both sides (a common cause is a Unity Editor session registering Editor-only test " +
                        $"assemblies against a player/server build), or by pruning the difference via " +
                        $"ISimulationConfig.SetRuntimePrunedComponentTypeIds — a pruned type leaves the layout and " +
                        $"the fingerprints then match. Pruning is an AUTHORITY-side action: the prune set is host/" +
                        $"server authoritative and propagated over the wire, so a guest cannot fix this by itself. " +
                        $"If the type COUNTS match, the difference is component metadata rather than the type set: " +
                        $"[KlothoCleanup] modes are folded in too, so the peers are running different source " +
                        $"(compare the cleanup counts above) — neither matching assemblies nor pruning fixes that." +
                        $"{(AllowLayoutMismatch ? " AllowLayoutMismatch is on — continuing anyway." : "")}");
                }
            }
            else
            {
                _readyLayoutMismatchLogged.Remove(playerId);
            }

            if (compareEnvironment)
            {
                long localEnv = GetLocalEnvironmentFingerprint();
                if (FingerprintsDiffer(localEnv, remoteEnvironmentFingerprint))
                {
                    if (_readyEnvMismatchLogged.Add(playerId))
                    {
                        _logger?.KError(
                            $"[KlothoEngine] Static environment mismatch with playerId={playerId}: " +
                            $"local=0x{localEnv:X16} remote=0x{remoteEnvironmentFingerprint:X16} " +
                            $"(static colliders / navmesh / game slot — outside the state hash, so this surfaces " +
                            $"later as a body or agent divergence rather than a hash mismatch). Check that both " +
                            $"peers resolved the same stage geometry.");
                    }
                }
                else
                {
                    _readyEnvMismatchLogged.Remove(playerId);
                }
            }

            return verdict;
        }
    }
}
