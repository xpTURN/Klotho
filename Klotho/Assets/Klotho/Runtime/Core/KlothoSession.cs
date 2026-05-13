using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Klotho session implementation.
    /// A factory and facade responsible for creating/composing engine core objects.
    /// Operates as pure C# with no MonoBehaviour dependency.
    /// </summary>
    public sealed class KlothoSession : IKlothoSession
    {
        public KlothoEngine Engine { get; private set; }
        public EcsSimulation Simulation { get; private set; }
        public IKlothoNetworkService NetworkService { get; private set; }
        public CommandFactory CommandFactory { get; private set; }

        public int LocalPlayerId => Engine.LocalPlayerId;
        public KlothoState State => Engine.State;

        private KlothoSession() { }

        public void Update(float deltaTime) => Engine.Update(deltaTime);
        public void InputCommand(ICommand command) => Engine.InputCommand(command);
        public void Stop()
        {
            Engine.Stop();
            NetworkService.LeaveRoom();
        }

        // ── Factory ──

        /// <summary>
        /// Create a session (new Config-tier API).
        /// </summary>
        public static KlothoSession Create(KlothoSessionSetup setup)
        {
            bool isGuest = setup.Connection != null;
            var simConfig = isGuest
                ? setup.Connection.SimulationConfig
                : setup.SimulationConfig;
            var transport = isGuest
                ? setup.Connection.Transport
                : setup.Transport;

            // 1. Create EcsSimulation
            var simulation = new EcsSimulation(
                simConfig.MaxEntities,
                simConfig.MaxRollbackTicks,
                simConfig.TickIntervalMs,
                setup.Logger,
                assetRegistry: setup.AssetRegistry);

            // 2. Register systems via callback
            setup.SimulationCallbacks?.RegisterSystems(simulation);
            simulation.LockAssetRegistry();

            // 3. Create CommandFactory
            var commandFactory = new CommandFactory();

            // 4. Create SessionConfig
            // Guest: RandomSeed stays 0 because it is overwritten when GameStartMessage is received
            // Host: if 0, auto-generated from TickCount
            // Guest Late Join: overwritten with LateJoinAcceptMessage fields (replaces the GameStartMessage path)
            // Guest cold-start Reconnect: overwritten with ReconnectAcceptMessage fields
            JoinKind joinKind = isGuest ? setup.Connection.Kind : JoinKind.Normal;
            bool isLateJoin = (joinKind == JoinKind.LateJoin);
            bool isReconnect = (joinKind == JoinKind.Reconnect);
            SessionConfig sessionConfig;
            if (isLateJoin)
            {
                var accept = setup.Connection.LateJoinPayload.AcceptMessage;
                int clampedMinPlayers = System.Math.Clamp(accept.MinPlayers, 1, accept.MaxPlayers);
                if (clampedMinPlayers != accept.MinPlayers)
                {
                    setup.Logger?.ZLogWarning($"[KlothoSession] MinPlayers clamped (LateJoin): {accept.MinPlayers} -> {clampedMinPlayers} (range: 1..{accept.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = accept.RandomSeed,
                    MaxPlayers = accept.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    AllowLateJoin = accept.AllowLateJoin,
                    ReconnectTimeoutMs = accept.ReconnectTimeoutMs,
                    ReconnectMaxRetries = accept.ReconnectMaxRetries,
                    LateJoinDelayTicks = accept.LateJoinDelayTicks,
                    ResyncMaxRetries = accept.ResyncMaxRetries,
                    DesyncThresholdForResync = accept.DesyncThresholdForResync,
                    CountdownDurationMs = accept.CountdownDurationMs,
                    CatchupMaxTicksPerFrame = accept.CatchupMaxTicksPerFrame,
                };
            }
            else if (isReconnect)
            {
                var accept = setup.Connection.ReconnectPayload.AcceptMessage;
                int clampedMinPlayers = System.Math.Clamp(accept.MinPlayers, 1, accept.MaxPlayers);
                if (clampedMinPlayers != accept.MinPlayers)
                {
                    setup.Logger?.ZLogWarning($"[KlothoSession] MinPlayers clamped (Reconnect): {accept.MinPlayers} -> {clampedMinPlayers} (range: 1..{accept.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = accept.RandomSeed,
                    MaxPlayers = accept.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    AllowLateJoin = accept.AllowLateJoin,
                    ReconnectTimeoutMs = accept.ReconnectTimeoutMs,
                    ReconnectMaxRetries = accept.ReconnectMaxRetries,
                    LateJoinDelayTicks = accept.LateJoinDelayTicks,
                    ResyncMaxRetries = accept.ResyncMaxRetries,
                    DesyncThresholdForResync = accept.DesyncThresholdForResync,
                    CountdownDurationMs = accept.CountdownDurationMs,
                    CatchupMaxTicksPerFrame = accept.CatchupMaxTicksPerFrame,
                };
            }
            else
            {
                int clampedMinPlayers = System.Math.Clamp(setup.MinPlayers, 1, setup.MaxPlayers);
                if (clampedMinPlayers != setup.MinPlayers)
                {
                    setup.Logger?.ZLogWarning($"[KlothoSession] MinPlayers clamped: {setup.MinPlayers} -> {clampedMinPlayers} (range: 1..{setup.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = isGuest
                        ? 0
                        : (setup.RandomSeed == 0 ? System.Environment.TickCount : setup.RandomSeed),
                    MaxPlayers = setup.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    AllowLateJoin = setup.AllowLateJoin,
                    ReconnectTimeoutMs = setup.ReconnectTimeoutMs,
                    ReconnectMaxRetries = setup.ReconnectMaxRetries,
                    LateJoinDelayTicks = setup.LateJoinDelayTicks,
                    ResyncMaxRetries = setup.ResyncMaxRetries,
                    DesyncThresholdForResync = setup.DesyncThresholdForResync,
                    CountdownDurationMs = setup.CountdownDurationMs,
                    CatchupMaxTicksPerFrame = setup.CatchupMaxTicksPerFrame,
                };
            }

            // 5. Create + initialize NetworkService — guest (Connection) uses the skip-handshake path
            IKlothoNetworkService networkService;
            if (simConfig.Mode == NetworkMode.ServerDriven)
            {
                var sdService = new ServerDrivenClientService();
                if (isGuest) sdService.InitializeFromConnection(setup.Connection, commandFactory, setup.Logger, setup.RoomId);
                else         sdService.Initialize(transport, commandFactory, setup.Logger);
                networkService = sdService;
            }
            else
            {
                var p2pService = new KlothoNetworkService();
                if (isGuest) p2pService.InitializeFromConnection(setup.Connection, commandFactory, setup.Logger);
                else         p2pService.Initialize(transport, commandFactory, setup.Logger);
                networkService = p2pService;
            }

            // 5.5 Late Join / cold-start Reconnect seed — restore _players / _sessionMagic / _randomSeed.
            //     Must be done at this point so the engine.Initialize _activePlayerIds auto-copy loop ([L278-280])
            //     can populate correctly.
            if (isLateJoin)
            {
                if (networkService is ServerDrivenClientService sdClient)
                    sdClient.SeedLateJoinPlayers(setup.Connection.LateJoinPayload);
                else if (networkService is KlothoNetworkService p2pClient)
                    p2pClient.SeedLateJoinPlayers(setup.Connection.LateJoinPayload);
            }
            else if (isReconnect)
            {
                if (networkService is ServerDrivenClientService sdClient)
                    sdClient.SeedReconnectPlayers(setup.Connection.ReconnectPayload);
                else if (networkService is KlothoNetworkService p2pClient)
                    p2pClient.SeedReconnectPlayers(setup.Connection.ReconnectPayload);
            }

            // 6. Create Engine: inject both SimulationConfig and SessionConfig
            var engine = new KlothoEngine(simConfig, sessionConfig);
            engine.Initialize(simulation, networkService, setup.Logger,
                setup.SimulationCallbacks, setup.ViewCallbacks);
            engine.SetCommandFactory(commandFactory);
            if (networkService is KlothoNetworkService p2pNs)
                p2pNs.SubscribeEngine(engine);
            else if (networkService is ServerDrivenClientService sdNs)
                sdNs.SubscribeEngine(engine);

            // 7.5 Late Join injection: restore FullState + start Catchup + seed existing players' PlayerConfig.
            //     Since there is no HandleGameStart path, seed manually at this point.
            //     The extra-delay value from the accept message is applied by SDClientService.SubscribeEngine
            //     (drains a pending value buffered when the handshake handler fired before the engine existed).
            if (isLateJoin)
            {
                engine.SeedLateJoinFullState(setup.Connection.LateJoinPayload);
                SeedLateJoinPlayerConfigs(engine, setup.Connection.LateJoinPayload);
            }
            else if (isReconnect)
            {
                // cold-start Reconnect: FullState restore + Catchup. PlayerConfig is re-broadcast by the host
                // upon reconnect (the existing runtime path), so no PlayerConfig seed array on this message.
                engine.SeedReconnectFullState(setup.Connection.ReconnectPayload);
            }

            return new KlothoSession
            {
                Engine = engine,
                Simulation = simulation,
                NetworkService = networkService,
                CommandFactory = commandFactory,
            };
        }

        /// <summary>
        /// Late Join path PlayerConfig injection.
        /// Sequentially deserializes LateJoinAcceptMessage.PlayerConfigData + PlayerConfigLengths and
        /// calls engine.HandlePlayerConfigReceived(playerId, configMsg). Same pattern as the regular runtime path.
        /// Since MessageSerializer._messageCache reuses singletons by type, HandlePlayerConfigReceived must be invoked
        /// immediately inside the loop (the engine copies/extracts into its internal store) — do not buffer into an intermediate array.
        /// </summary>
        private static void SeedLateJoinPlayerConfigs(KlothoEngine engine, LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            if (msg.PlayerConfigData == null || msg.PlayerConfigData.Length == 0) return;
            if (msg.PlayerConfigLengths == null || msg.PlayerConfigLengths.Count == 0) return;

            var serializer = new MessageSerializer();
            int offset = 0;
            int count = System.Math.Min(msg.PlayerConfigLengths.Count, msg.PlayerIds.Count);
            for (int i = 0; i < count; i++)
            {
                int len = msg.PlayerConfigLengths[i];
                if (len <= 0) continue;

                var configMsg = serializer.Deserialize(msg.PlayerConfigData, len, offset) as PlayerConfigBase;
                if (configMsg != null)
                    engine.HandlePlayerConfigReceived(msg.PlayerIds[i], configMsg);
                offset += len;
            }
        }

        // ── Convenience methods ──

        public void HostGame(string roomName, int maxPlayers)
        {
            NetworkService.CreateRoom(roomName, maxPlayers);
        }

        public void JoinGame(string roomName)
        {
            NetworkService.JoinRoom(roomName);
        }

        public void LeaveRoom()
        {
            NetworkService.LeaveRoom();
        }

        /// <summary>
        /// Sends the local player's PlayerConfig to the host.
        /// Upon receipt, the host broadcasts it to all peers.
        /// </summary>
        public void SendPlayerConfig(PlayerConfigBase playerConfig)
        {
            NetworkService.SendPlayerConfig(LocalPlayerId, playerConfig);
        }

        public void SetReady(bool ready)
        {
            NetworkService.SetReady(ready);
        }
    }
}
