using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace xpTURN.Klotho.Runtime.Tests.Contract
{
    /// <summary>
    /// Brawler wiring facts that nothing else pins, asserted against the source as TEXT.
    ///
    /// <para><b>Why text.</b> They all live in Brawler, which this assembly does not reference, and
    /// the Unity EditMode suite needs an editor to run. The choice is a text pin in a suite that runs
    /// or a typed test that nobody executes; for facts whose only other coverage is zero, the former
    /// is worth more. Each assertion is written to fail on removal rather than on reformatting.</para>
    ///
    /// <para><b>What is here is what a text pin can actually hold.</b> These are all statements about
    /// WHERE something is called from — registration order, which callbacks reach a method, that a
    /// command handler does not swap. The properties that live INSIDE the rebake driver used to be
    /// pinned here too and are now real unit tests over the engine
    /// (<c>FPNavMeshRebakeDriverTests</c>), which is strictly better: a regex fails on the edit, a
    /// test fails on the behaviour, and only the latter can be checked by mutating the code and
    /// watching it fail.</para>
    ///
    /// <para><b>System registration order.</b> Within one <c>SystemPhase</c>, execution order is
    /// registration order (<c>SystemRunner</c> stamps an increasing Order and sorts by it). Brawler
    /// depends on bots running before command processing, and on the invariant pump preceding both —
    /// a pump running after the bots leaves them pathing on last tick's mesh. Nothing but this
    /// records that.</para>
    ///
    /// <para><b>Game fingerprint fold.</b> <c>IGameFingerprintSource</c> is how a peer-local input
    /// that no state hash covers gets surfaced at join. Exactly one production type implements it,
    /// and before this test replacing its body with <c>return 0;</c> broke nothing any suite would
    /// notice — while a build-to-build mismatch in the shape catalog stays invisible until the
    /// navmesh quietly diverges.</para>
    /// </summary>
    [TestFixture]
    public class BrawlerWiringContractTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        private static string ReadBrawler(string relative)
        {
            string path = Path.Combine(RepoRoot(), "Samples/Brawler/Assets/Brawler/Scripts", relative);
            if (!File.Exists(path))
                Assert.Ignore($"Brawler source missing: {relative}");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Reads a Brawler source that may live outside the Scripts tree — the dedicated server's
        /// callbacks sit beside the csproj, not under Assets, and the ordering this pins has to
        /// hold on BOTH hosts.
        /// </summary>
        private static string ReadBrawlerOrServer(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(
                RepoRoot(), "Samples/Brawler/Assets/Brawler/Scripts", relative));
            if (!File.Exists(path))
                Assert.Ignore($"Brawler source missing: {relative}");
            return File.ReadAllText(path);
        }

        [Test]
        public void BotFsm_IsRegisteredBeforeCommandProcessing()
        {
            string src = ReadBrawler("ECS/BrawlerSimSetup.cs");

            Match bot = Regex.Match(src, @"AddSystem\(\s*botFSMSystem\s*,\s*SystemPhase\.PreUpdate\s*(?:,[^)]*)?\)");
            Match cmd = Regex.Match(src, @"AddSystem\(\s*platformerCommandSystem\s*,\s*SystemPhase\.PreUpdate\s*(?:,[^)]*)?\)");

            Assert.IsTrue(bot.Success, "botFSMSystem is no longer registered into PreUpdate");
            Assert.IsTrue(cmd.Success, "platformerCommandSystem is no longer registered into PreUpdate");
            Assert.Less(bot.Index, cmd.Index,
                "registration order within a phase IS execution order — bots must still be registered "
                + "before command processing, and the invariant pump has to go before both");
        }

        [Test]
        public void InvariantPump_RunsBeforeBotsAndCommands()
        {
            string src = ReadBrawler("ECS/BrawlerSimSetup.cs");

            // The DRIVER is what is registered, by its own core type — a game wrapper would hide it
            // from the engine's GetSystem<FPNavMeshRebakeDriver> lookup. The regex follows it there.
            // Trailing arguments are tolerated on purpose: the contract here is "registered into this
            // phase, in this order", not the argument count (a perf-report group label may follow).
            Match pump = Regex.Match(src, @"AddSystem\(\s*navMeshPlacementSeam\.Driver\s*,\s*SystemPhase\.PreUpdate\s*(?:,[^)]*)?\)");
            Match bot = Regex.Match(src, @"AddSystem\(\s*botFSMSystem\s*,\s*SystemPhase\.PreUpdate\s*(?:,[^)]*)?\)");
            Match cmd = Regex.Match(src, @"AddSystem\(\s*platformerCommandSystem\s*,\s*SystemPhase\.PreUpdate\s*(?:,[^)]*)?\)");

            Assert.IsTrue(pump.Success, "FPNavMeshRebakeDriver is no longer registered — without it "
                + "a delayed swap never happens at all and buildings never reach the navmesh");
            Assert.Less(pump.Index, bot.Index,
                "the pump must be registered before BotFSMSystem: order within a phase is registration "
                + "order, and a pump running after the bots leaves them pathing on last tick's set");
            Assert.Less(pump.Index, cmd.Index, "the pump must be registered before command processing");
        }

        [Test]
        public void CommandHandlers_DoNotSwapTheNavMesh()
        {
            // A swap performed while handling the command happens on
            // a tick the client may only have PREDICTED, and the navmesh does not roll back with
            // the frame — so the rewind leaves the mesh ahead of the state that describes it and
            // every re-executed tick runs against the wrong one. It diverges quietly: both peers
            // hold identical components, and the hash covers components.
            string src = ReadBrawler("ECS/Systems/PlatformerCommandSystem.cs");

            Assert.IsFalse(Regex.IsMatch(src, @"_botFSM\s*\.\s*Swap"),
                "a command handler swaps the navmesh again. Installing belongs to "
                + "NavMeshEffectiveTickSystem, which derives it from frame state and therefore "
                + "reproduces it across a rollback instead of surviving one");
            Assert.IsFalse(Regex.IsMatch(src, @"_rebakeContext\s*\.\s*CommitSwap"),
                "a command handler commits a mesh. CommitSwap retires the mesh it REPLACES, so "
                + "committing before the delayed install recycles the arrays the agents walk on "
                + "for the whole window");
            Assert.IsTrue(Regex.IsMatch(src, @"EffectiveTick\s*=\s*effectiveTick"),
                "placement no longer records the tick it takes effect on — the delay, the rollback "
                + "reproduction and the join path all read that field");
        }

        [Test]
        public void BotFsm_FoldsTheShapeCatalogIntoTheGameFingerprint()
        {
            string src = ReadBrawler("ECS/Systems/BotFSMSystem.cs");

            Assert.IsTrue(Regex.IsMatch(src, @"IGameFingerprintSource"),
                "BotFSMSystem no longer implements IGameFingerprintSource — the only production "
                + "implementation in the repository");
            Assert.IsTrue(
                Regex.IsMatch(src, @"GetGameFingerprint\(\)[\s\S]{0,600}?Catalog\.Hash"),
                "GetGameFingerprint no longer folds the shape catalog hash. If a value was moved "
                + "rather than dropped, update this pin in the same commit — the point is that a "
                + "silent 'return 0' cannot happen unnoticed");
            Assert.IsTrue(
                Regex.IsMatch(src, @"GetGameFingerprint\(\)[\s\S]{0,600}?BuildDelayTicks"),
                "GetGameFingerprint no longer folds the build delay. K decides which tick the mesh "
                + "swaps on and no state hash covers it, so two builds disagreeing about it swap at "
                + "different times and diverge with every component still matching");
        }
    }
}
