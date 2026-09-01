using System;
using System.Collections.Generic;

using Brawler;                       // BrawlerServerCallbacks lives here via the game ECS; DemoEntitlement
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Samples.Identity; // DemoEntitlement
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// The join tick reaches the GAME on a replay-shaped session.
    ///
    /// <para><b>The gap this closes.</b> The core suite proves the engine raises OnPlayerJoinedWorld and
    /// that the joiner's recorded bytes are readable at that instant. It cannot prove the game turned them
    /// into world state, because the component that holds them is Brawler's and the core test project does
    /// not reference Brawler. This is the only .NET suite that compiles both, so the end of the chain —
    /// join command → engine → BrawlerServerCallbacks → LoadoutSeedComponent — is only checkable here.</para>
    ///
    /// <para><b>Why it matters more than it looks.</b> The whole join half of the entitlement feature was
    /// dead on the replay path and nothing caught it: tick 0 was byte-compared, the end result was compared,
    /// and everything between was unchecked — a replay seeded from the wrong loadout still reached the same
    /// winner and printed `verified`. This gate is one of the two that watch that middle.</para>
    ///
    /// Run: dotnet run -- --test
    /// </summary>
    public static class ReplayJoinSeedTests
    {
        private static int _passed;
        private static int _failed;

        // Must match every other suite in this process: the component layout freezes on first use.
        private const int LayoutMaxEntities = 64;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Console.WriteLine("\n=== ReplayJoinSeed Tests ===\n");

            // The lobby-issued shape this feature exists for: class 15 / skills 0xFF / consumable 1.
            var issued = new byte[] { 0x0f, 0, 0, 0, 0xff, 0, 0, 0, 0x01, 0, 0, 0 };
            var expected = DemoEntitlement.Decode(issued);

            var seeded = SeedThroughJoin(joinedPlayerId: 3, entitlement: issued);
            Assert("late joiner gets a loadout seed", seeded.HasValue);
            if (seeded.HasValue)
            {
                Assert($"skill mask travelled (0x{seeded.Value.OwnedSkillMask:X} == 0x{expected.OwnedSkillMask:X})",
                    seeded.Value.OwnedSkillMask == expected.OwnedSkillMask);
                Assert($"consumable mask travelled (0x{seeded.Value.OwnedConsumableMask:X} == 0x{expected.OwnedConsumableMask:X})",
                    seeded.Value.OwnedConsumableMask == expected.OwnedConsumableMask);
                Assert("the seed belongs to the joiner", seeded.Value.PlayerId == 3);
            }

            // A restricted joiner must stay restricted. This is the direction that matters for fairness:
            // an unread entitlement decodes to "all owned", so a broken read makes a gated player POWERFUL —
            // and the masks above cannot tell the two apart on their own.
            var restricted = new byte[] { 0x01, 0, 0, 0, 0x03, 0, 0, 0, 0x00, 0, 0, 0 };
            var restrictedSeed = SeedThroughJoin(joinedPlayerId: 4, entitlement: restricted);
            Assert("restricted joiner gets a loadout seed", restrictedSeed.HasValue);
            if (restrictedSeed.HasValue)
            {
                var want = DemoEntitlement.Decode(restricted);
                Assert("restricted skill mask is not widened", restrictedSeed.Value.OwnedSkillMask == want.OwnedSkillMask);
                Assert("restricted consumable mask is not widened", restrictedSeed.Value.OwnedConsumableMask == want.OwnedConsumableMask);
                Assert("a restriction actually restricts (differs from all-owned)",
                    want.OwnedSkillMask != DemoEntitlement.Decode(null).OwnedSkillMask);
            }

            // No entitlement issued is a legitimate state (lobbyless / P2P): the seed still exists, on the
            // "all owned" default. Absence of a seed would leave the joiner's loadout undefined.
            var none = SeedThroughJoin(joinedPlayerId: 5, entitlement: null);
            Assert("joiner with nothing issued still gets a seed", none.HasValue);
            if (none.HasValue)
            {
                var allOwned = DemoEntitlement.Decode(null);
                Assert("no entitlement → all owned", none.Value.OwnedSkillMask == allOwned.OwnedSkillMask);
            }

            Console.WriteLine($"\n=== ReplayJoinSeed results: {_passed} passed, {_failed} failed ===");
            return _failed;
        }

        /// <summary>
        /// Builds an engine the way a replay session builds one — game callbacks, NO network service — and
        /// executes one join command through the simulation, exactly as playback re-executes the recorded
        /// one. Returns the joiner's loadout seed, or null when the game was never asked to seed it.
        /// </summary>
        private static LoadoutSeedComponent? SeedThroughJoin(int joinedPlayerId, byte[] entitlement)
        {
            var logger = KLoggerFactory.Create(b => { b.SetMinimumLevel(KLogLevel.Error); b.AddConsole(); })
                                       .CreateLogger("ReplayJoinSeed");

            ComponentStorageRegistry.EnsureLayoutComputed(LayoutMaxEntities, null, null);

            var sim = new EcsSimulation(LayoutMaxEntities, maxRollbackTicks: 1, deltaTimeMs: 50, logger);

            // Colliders / navmesh / assets are OnInitializeWorld's inputs and this never runs it: the join
            // seed writes through the frame it is handed and reads only the engine's entitlement.
            var callbacks = new BrawlerServerCallbacks(logger, null, null, maxPlayers: 4, botCount: 0);

            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, logger, callbacks);
            engine.SetCommandFactory(new CommandFactory());

            sim.Tick(new List<ICommand>
            {
                new PlayerJoinCommand { JoinedPlayerId = joinedPlayerId, Entitlement = entitlement },
            });

            var frame = sim.Frame;
            var filter = frame.Filter<LoadoutSeedComponent>();
            while (filter.Next(out var e))
            {
                var seed = frame.GetReadOnly<LoadoutSeedComponent>(e);
                if (seed.PlayerId == joinedPlayerId)
                    return seed;
            }
            return null;
        }

        private static void Assert(string name, bool condition)
        {
            if (condition) { _passed++; Console.WriteLine($"  PASS: {name}"); }
            else { _failed++; Console.WriteLine($"  FAIL: {name}"); }
        }
    }
}
