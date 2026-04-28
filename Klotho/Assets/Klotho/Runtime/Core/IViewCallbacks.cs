namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// View-side callbacks — client-only.
    /// Non-deterministic code is allowed (UI, animation, spawn commands, etc.).
    /// </summary>
    public interface IViewCallbacks
    {
        /// <summary>
        /// Invoked when the game starts (spawn commands, UI initialization, etc.).
        /// Called after Start() completes.
        /// </summary>
        void OnGameStart(IKlothoEngine engine);

        /// <summary>
        /// Invoked after a tick is executed (view refresh, etc.).
        /// </summary>
        void OnTickExecuted(int tick);

        /// <summary>
        /// Invoked when Late Join completes — game code runs initial logic here, such as spawn commands.
        /// The invocation timing depends on the mode:
        ///  - **SD mode**: immediately after catchup ends and the warmup burst completes (~100ms delay).
        ///    `CurrentTick` is already ahead by `SDInputLeadTicks` — commands sent here with
        ///    `CurrentTick + InputDelay` will fall inside the server's acceptance window.
        ///  - **P2P mode**: immediately after CatchingUp → Active transition.
        /// Invoked after OnCatchupComplete (empty input prefill).
        /// </summary>
        void OnLateJoinActivated(IKlothoEngine engine);
    }
}
