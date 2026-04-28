using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.DeterminismVerification;
using xpTURN.Klotho.ECS;

const int TotalTicks = 10_000;
const int MaxEntities = 512;
const int DeltaTimeMs = 25; // 40Hz
int[] seeds = [42, 12345, 987654321];

Console.WriteLine($"=== Determinism Verification (.NET {Environment.Version}) ===");
Console.WriteLine($"Ticks: {TotalTicks}, Seeds: {seeds.Length}, MaxEntities: {MaxEntities}");
Console.WriteLine();

string outputDir = args.Length > 0 ? args[0] : "output";
Directory.CreateDirectory(outputDir);

bool allPassed = true;

foreach (int seed in seeds)
{
    Console.Write($"Seed {seed}: running {TotalTicks} ticks... ");
    var sw = Stopwatch.StartNew();

    string csvPath = Path.Combine(outputDir, $"hashes_seed{seed}.csv");
    using var writer = new HashDumpWriter(csvPath);

    var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 2, deltaTimeMs: DeltaTimeMs);

    // Register test systems (order matters for determinism)
    sim.AddSystem(new EntityLifecycleSystem(), SystemPhase.PreUpdate);
    sim.AddSystem(new ArithmeticStressSystem(), SystemPhase.Update);
    sim.AddSystem(new TrigStressSystem(), SystemPhase.PostUpdate);
    sim.AddSystem(new RandomStressSystem(), SystemPhase.LateUpdate);

    sim.Initialize();
    sim.SetPlayerCount(1);

    // Seed initial entities
    SeedInitialEntities(sim, seed);

    // Generate deterministic input sequence
    var inputRng = new DeterministicRandom(seed);
    var commands = new List<ICommand>();

    for (int tick = 0; tick < TotalTicks; tick++)
    {
        commands.Clear();

        // Generate deterministic command
        var moveDir = new FPVector3(
            inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)),
            FP64.Zero,
            inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)));
        int actionId = inputRng.NextInt(0, 10);

        var cmd = new DeterminismTestCommand(playerId: 1, tick: tick, moveDir, actionId);
        commands.Add(cmd);

        sim.Tick(commands);

        long hash = sim.GetStateHash();
        writer.WriteHash(tick, hash);
    }

    sw.Stop();
    Console.WriteLine($"done ({sw.ElapsedMilliseconds}ms) -> {csvPath}");
}

// Self-verification: run seed 42 again and compare
Console.WriteLine();
Console.Write("Self-verify seed 42... ");
{
    var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 2, deltaTimeMs: DeltaTimeMs);
    sim.AddSystem(new EntityLifecycleSystem(), SystemPhase.PreUpdate);
    sim.AddSystem(new ArithmeticStressSystem(), SystemPhase.Update);
    sim.AddSystem(new TrigStressSystem(), SystemPhase.PostUpdate);
    sim.AddSystem(new RandomStressSystem(), SystemPhase.LateUpdate);
    sim.Initialize();
    sim.SetPlayerCount(1);
    SeedInitialEntities(sim, 42);

    var inputRng = new DeterministicRandom(42);
    var commands = new List<ICommand>();

    // Read original hashes
    string[] lines = File.ReadAllLines(Path.Combine(outputDir, "hashes_seed42.csv"));

    for (int tick = 0; tick < TotalTicks; tick++)
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

        // Compare with first run (line index = tick + 1 for header)
        string[] parts = lines[tick + 1].Split(',');
        long originalHash = long.Parse(parts[1]);

        if (hash != originalHash)
        {
            Console.WriteLine($"MISMATCH at tick {tick}: {originalHash} vs {hash}");
            allPassed = false;
            break;
        }
    }

    if (allPassed)
        Console.WriteLine("PASS (same-runtime determinism confirmed)");
}

Console.WriteLine();
Console.WriteLine(allPassed ? "=== ALL PASSED ===" : "=== FAILED ===");
return allPassed ? 0 : 1;

static void SeedInitialEntities(EcsSimulation sim, int seed)
{
    // Access frame via a dummy tick to create initial entities
    // We create entities by running a tick with no commands after seeding
    var rng = new DeterministicRandom(seed);

    // Create 10 initial entities with test components
    // We use Tick with empty commands to let EntityLifecycleSystem create some,
    // but also pre-seed a few for immediate arithmetic/trig testing
    var emptyCommands = new List<ICommand>();
    for (int i = 0; i < 5; i++)
    {
        sim.Tick(emptyCommands); // EntityLifecycleSystem will create 0-2 per tick
    }
}
