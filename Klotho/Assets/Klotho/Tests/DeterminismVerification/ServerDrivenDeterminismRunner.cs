using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

/// <summary>
/// ServerDriven cross-runtime determinism verification runner (section 7.3.2).
/// In SD mode, compares the server VerifiedStateMessage hash with the client re-simulation hash
/// and records whether determinism fails.
///
/// Usage:
/// 1. Server: run DedicatedServerLoop + DeterminismVerification simulation in a .NET console
/// 2. Client: place this runner in a scene and connect to the server with an IL2CPP build
/// 3. After N ticks, check the result log (hash mismatch = determinism failure)
///
/// Automated test mode: also runs in the Editor with TestTransport + in-process server.
/// </summary>
public class ServerDrivenDeterminismRunner : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private string serverAddress = "localhost";
    [SerializeField] private int serverPort = 7777;
    [SerializeField] private int targetTicks = 1000;

    [Header("Results")]
    [SerializeField] private int verifiedTicks;
    [SerializeField] private int hashMismatches;
    [SerializeField] private bool completed;

    private ServerDrivenClientService _clientService;
    private KlothoEngine _engine;
    private INetworkTransport _transport;

    private readonly List<(int tick, long serverHash, long clientHash)> _mismatches
        = new List<(int, long, long)>();

    private void Start()
    {
        Debug.Log($"=== SD Determinism Verification ===");
        Debug.Log($"Server: {serverAddress}:{serverPort}, Target: {targetTicks} ticks");

        // For real networking, replace with LiteNetLibTransport
        // Here only the interface is defined — concrete transport is injected from the game project
        Debug.LogWarning("ServerDrivenDeterminismRunner: please inject transport from the game project.");
    }

    /// <summary>
    /// Initialize externally and use (test or game project).
    /// </summary>
    public void Initialize(
        INetworkTransport transport,
        ICommandFactory commandFactory,
        ISimulation simulation,
        ISimulationConfig config,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _transport = transport;

        _clientService = new ServerDrivenClientService();
        _clientService.Initialize(transport, commandFactory, logger);

        _engine = new KlothoEngine(
            new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = config.TickIntervalMs,
                MaxRollbackTicks = config.MaxRollbackTicks,
                InputDelayTicks = 1
            },
            new SessionConfig());
        _engine.Initialize(simulation, _clientService, logger);
        _engine.SetCommandFactory(commandFactory);
        _clientService.SubscribeEngine(_engine);

        // Track hash mismatches — event fired when ProcessVerifiedBatch detects a desync
        _engine.OnDesyncDetected += (localHash, remoteHash) =>
        {
            hashMismatches++;
            _mismatches.Add((_engine.CurrentTick, remoteHash, localHash));
        };

        // VerifiedState receive count
        _clientService.OnVerifiedStateReceived += (tick, cmds, hash) =>
        {
            verifiedTicks++;
        };

        _clientService.JoinRoom("test");
        _clientService.Connect(serverAddress, serverPort);
    }

    private void Update()
    {
        if (completed || _clientService == null) return;

        _clientService.Update();

        if (_clientService.Phase == SessionPhase.Playing)
            _engine.Update(Time.deltaTime);

        if (verifiedTicks >= targetTicks)
        {
            completed = true;
            ReportResults();
        }
    }

    private void ReportResults()
    {
        Debug.Log($"=== SD Determinism Verification Complete ===");
        Debug.Log($"Verified ticks: {verifiedTicks}");
        Debug.Log($"Hash mismatches: {hashMismatches}");

        if (hashMismatches == 0)
        {
            Debug.Log("PASS: cross-runtime determinism verified");
        }
        else
        {
            Debug.LogError($"FAIL: {hashMismatches} hash mismatches occurred");
            string outputPath = Path.Combine(Application.persistentDataPath, "sd_determinism_mismatches.csv");
            using (var writer = new StreamWriter(outputPath))
            {
                writer.WriteLine("tick,serverHash,clientHash");
                foreach (var m in _mismatches)
                    writer.WriteLine($"{m.tick},0x{m.serverHash:X16},0x{m.clientHash:X16}");
            }
            Debug.LogError($"Mismatch details: {outputPath}");
        }
    }

    private void OnDestroy()
    {
        _clientService?.LeaveRoom();
    }
}
