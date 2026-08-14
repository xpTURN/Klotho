namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation-side callbacks — common to all peers (server / client / replay).
    /// Only deterministic code is allowed.
    /// </summary>
    public interface ISimulationCallbacks
    {
        /// <summary>
        /// Register simulation systems.
        /// Called immediately after EcsSimulation is created and before Engine.Initialize().
        /// </summary>
        void RegisterSystems(ECS.EcsSimulation simulation);

        /// <summary>
        /// Create world-initialization entities.
        /// Called inside Engine.Start(), before SaveSnapshot(0).
        /// Invoked identically on every peer, so only deterministic code is allowed.
        /// </summary>
        void OnInitializeWorld(IKlothoEngine engine);

        /// <summary>
        /// Input polling immediately before a tick.
        /// The game sends as many commands as desired via sender.
        /// If no command is sent, an EmptyCommand is automatically injected.
        /// </summary>
        void OnPollInput(int playerId, int tick, ICommandSender sender);

        /// <summary>
        /// A player entered the simulated world at its (deterministic) join tick — the late-join analog of
        /// <see cref="OnInitializeWorld"/> for a single joiner. Invoked inside the engine's participant-slot
        /// creation (create-iff-not-exists → fires once per join, rollback-safe) with the live frame, so a game
        /// can seed deterministic per-player world state (e.g. an entitlement-derived loadout) the same way
        /// OnInitializeWorld seeds tick-0 players. The engine is provided for reads (e.g. GetPlayerEntitlement);
        /// note engine.InitFrame is NOT valid here (init-only) — write via the supplied <paramref name="frame"/>.
        /// Only deterministic code is allowed (runs identically on every peer, incl. rollback re-sim).
        /// Games with no per-join world state leave this empty.
        /// </summary>
        void OnPlayerJoinedWorld(IKlothoEngine engine, ECS.Frame frame, int playerId);

        /// <summary>
        /// Called after a received full state has been applied locally (late join /
        /// corrective reset) — the frame now holds the restored world. Use this to rebuild
        /// peer-local derivatives that live outside the state hash (for example: rebake
        /// the navmesh from restored building components so the nav fingerprint matches).
        /// Default no-op (default interface method) — existing implementations unaffected.
        /// <para><b>Do not throw.</b> The state is already applied and live by the time this runs,
        /// and the engine cannot undo it — so there is no failure this can usefully signal by
        /// unwinding. Handle domain errors here (a rejected placement is the game's to report),
        /// and treat a throw as a bug. The engine does guard the call so that a violation cannot
        /// cost it the return value its callers depend on, but that guard is a last line of
        /// defence, not a licence: it logs a KError and marks the apply as having a broken
        /// derivative, which leaves this peer simulating a sound state on a stale navmesh.</para>
        /// <para>Also runs when the applied state's hash did NOT match, i.e. on an untrusted
        /// world — implementations that validate their inputs should keep doing so.</para>
        /// </summary>
        void OnFullStateApplied(IKlothoEngine engine, ECS.Frame frame) { }
    }
}
