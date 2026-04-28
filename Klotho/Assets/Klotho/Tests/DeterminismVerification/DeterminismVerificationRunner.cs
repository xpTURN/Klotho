using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.DeterminismVerification;
using xpTURN.Klotho.ECS;

public class DeterminismVerificationRunner : MonoBehaviour
{
    [SerializeField] private int totalTicks = 10_000;
    [SerializeField] private int maxEntities = 512;
    [SerializeField] private int deltaTimeMs = 25;

    private readonly int[] _seeds = { 42, 12345, 987654321 };

    private void Start()
    {
        string outputDir = Path.Combine(Application.persistentDataPath, "DeterminismVerification");
        Directory.CreateDirectory(outputDir);

        UnityEngine.Debug.Log($"=== Determinism Verification (Unity IL2CPP) ===");
        UnityEngine.Debug.Log($"Ticks: {totalTicks}, Seeds: {_seeds.Length}, MaxEntities: {maxEntities}");
        UnityEngine.Debug.Log($"Output: {outputDir}");

        bool allPassed = true;

        foreach (int seed in _seeds)
        {
            var sw = Stopwatch.StartNew();
            string csvPath = Path.Combine(outputDir, $"hashes_seed{seed}.csv");

            using (var writer = new HashDumpWriter(csvPath))
            {
                var sim = CreateSimulation();
                SeedInitialEntities(sim);

                var inputRng = new DeterministicRandom(seed);
                var commands = new List<ICommand>();

                for (int tick = 0; tick < totalTicks; tick++)
                {
                    commands.Clear();

                    var moveDir = new FPVector3(
                        inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)),
                        FP64.Zero,
                        inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)));
                    int actionId = inputRng.NextInt(0, 10);

                    commands.Add(new DeterminismTestCommand(playerId: 1, tick: tick, moveDir, actionId));
                    sim.Tick(commands);

                    long hash = sim.GetStateHash();
                    writer.WriteHash(tick, hash);
                }
            }

            sw.Stop();
            UnityEngine.Debug.Log($"Seed {seed}: done ({sw.ElapsedMilliseconds}ms) -> {csvPath}");
        }

        // Self-verification
        {
            var sim = CreateSimulation();
            SeedInitialEntities(sim);
            var inputRng = new DeterministicRandom(42);
            var commands = new List<ICommand>();

            string[] lines = File.ReadAllLines(
                Path.Combine(outputDir, "hashes_seed42.csv"));

            for (int tick = 0; tick < totalTicks; tick++)
            {
                commands.Clear();
                var moveDir = new FPVector3(
                    inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)),
                    FP64.Zero,
                    inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)));
                int actionId = inputRng.NextInt(0, 10);
                commands.Add(new DeterminismTestCommand(1, tick, moveDir, actionId));

                sim.Tick(commands);
                long hash = sim.GetStateHash();

                string[] parts = lines[tick + 1].Split(',');
                long originalHash = long.Parse(parts[1]);

                if (hash != originalHash)
                {
                    UnityEngine.Debug.LogError($"MISMATCH at tick {tick}: {originalHash} vs {hash}");
                    allPassed = false;
                    break;
                }
            }

            if (allPassed)
                UnityEngine.Debug.Log("Self-verify: PASS");
        }

        UnityEngine.Debug.Log(allPassed ? "=== ALL PASSED ===" : "=== FAILED ===");

#if !UNITY_EDITOR
        Application.Quit(allPassed ? 0 : 1);
#endif
    }

    private EcsSimulation CreateSimulation()
    {
        var sim = new EcsSimulation(maxEntities, maxRollbackTicks: 2, deltaTimeMs: deltaTimeMs);
        sim.AddSystem(new EntityLifecycleSystem(), SystemPhase.PreUpdate);
        sim.AddSystem(new ArithmeticStressSystem(), SystemPhase.Update);
        sim.AddSystem(new TrigStressSystem(), SystemPhase.PostUpdate);
        sim.AddSystem(new RandomStressSystem(), SystemPhase.LateUpdate);
        sim.Initialize();
        sim.SetPlayerCount(1);
        return sim;
    }

    private void SeedInitialEntities(EcsSimulation sim)
    {
        var emptyCommands = new List<ICommand>();
        for (int i = 0; i < 5; i++)
        {
            sim.Tick(emptyCommands);
        }
    }
}
