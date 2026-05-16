using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    public enum SpectatorState
    {
        Idle,
        Connecting,
        Synchronizing,
        Watching,
        Disconnected
    }

    public class SpectatorStartInfo
    {
        public int RandomSeed;
        public int TickInterval;
        public int PlayerCount;
        public List<int> PlayerIds;
    }

    public interface ISpectatorService
    {
        SpectatorState State { get; }

        void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKlothoEngine engine, ILogger logger);

        void Connect(string hostAddress, int port, int roomId = -1);

        void Disconnect();

        void Update();

        int DelayFrames { get; }

        int LatestReceivedTick { get; }

        event Action<SpectatorStartInfo> OnSpectatorStarted;

        event Action<int, ICommand> OnConfirmedInputReceived;

        event Action<string> OnSpectatorStopped;

        event Action<int, byte[], long, FullStateKind> OnFullStateReceived;

        event Action<ISessionConfig> OnSessionConfigReceived;
    }
}
