using System;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Pair of callback sources returned by <see cref="SpectatorSessionSetup.CallbacksFactory"/>.
    /// Built atomically after SimulationConfig and SessionConfig arrive so the game can size
    /// its callback objects against server-authoritative values.
    /// </summary>
    public readonly struct SpectatorCallbacks
    {
        public readonly ISimulationCallbacks Simulation;
        public readonly IViewCallbacks View;

        public SpectatorCallbacks(ISimulationCallbacks simulation, IViewCallbacks view)
        {
            Simulation = simulation;
            View = view;
        }
    }

    /// <summary>
    /// Configuration required to create a spectator KlothoSession via
    /// <see cref="KlothoSession.CreateSpectator"/>.
    /// Spectator mode has no host-decided SessionConfig or CredentialsStore — all simulation
    /// and session parameters arrive in SpectatorAcceptMessage. Only the connect target
    /// (host:port plus optional roomId) and the local Sim/View callback wiring are needed.
    /// </summary>
    public class SpectatorSessionSetup
    {
        public IKLogger Logger { get; set; }

        /// <summary>Dev escape hatch — see KlothoSessionSetup.AllowLayoutMismatch.</summary>
        public bool AllowLayoutMismatch { get; set; }
        public IDataAssetRegistry AssetRegistry { get; set; }
        public Network.INetworkTransport Transport { get; set; }

        public string HostAddress { get; set; }
        public int Port { get; set; }
        public int RoomId { get; set; } = -1;

        public IKlothoSessionObserver LifecycleObserver { get; set; }

        /// <summary>
        /// Required — invoked after SpectatorAcceptMessage delivers SimulationConfig and SessionConfig
        /// from the server. Builds Sim/View callbacks against server-authoritative config
        /// (for example MaxPlayers). Both callbacks are constructed atomically because game ViewCallbacks
        /// typically depend on the SimulationCallbacks instance.
        /// </summary>
        public Func<ISimulationConfig, ISessionConfig, SpectatorCallbacks> CallbacksFactory { get; set; }
    }
}
