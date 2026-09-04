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

        // ── Retain mode: the mode is a determinism input and flows through the payload only ──

        [Test]
        public void RetainMode_FlowsOnlyThroughTheCommandPayload()
        {
            // A toggle the simulation read directly (Input, PlayerPrefs, a static) would give each
            // peer its own navmesh under a matching state hash — the failure with no detector. So
            // the two systems that turn a placement into geometry may not read local input at all,
            // and the handler must take the mode from the command it was handed.
            string cmdSys = ReadBrawler("ECS/Systems/PlatformerCommandSystem.cs");
            string botSys = ReadBrawler("ECS/Systems/BotFSMSystem.cs");
            foreach (var (name, src) in new[] { ("PlatformerCommandSystem", cmdSys), ("BotFSMSystem", botSys) })
            {
                Assert.IsFalse(Regex.IsMatch(src, @"UnityEngine\.Input\b|\bInput\.Get|PlayerPrefs"),
                    $"{name} reads local input — a placement mode taken from it diverges the navmesh");
            }

            // Both handlers — the command path is two, and a hexagon command that lost its field
            // would silently carve forever with nothing else to notice (wiring plan trap 6).
            Assert.AreEqual(2, Regex.Matches(cmdSys, @"PlaceBuildingAt\([^;]*cmd\.Retain\s*\)").Count,
                "a placement handler no longer passes cmd.Retain into the shared placement body — "
                + "the box and the hexagon must both take the mode from their own payload");
            Assert.IsFalse(Regex.IsMatch(cmdSys, @"PlaceBuildingAt\([^;]*retain:\s*(true|false)\s*\)"),
                "a placement handler pins the mode to a constant instead of reading its payload");
            Assert.IsTrue(Regex.IsMatch(cmdSys, @"new FPBuildingPlacement\(\s*shapeId,\s*orientation,\s*centre\.x,\s*centre\.z,\s*centre\.y,\s*retain\)"),
                "the trial placement no longer carries the mode — the validation would answer for a carve");
            Assert.IsTrue(Regex.IsMatch(cmdSys, @"Retain\s*=\s*retain"),
                "the BuildingComponent no longer records the mode — the installed mesh is derived from "
                + "the component, so a carve would land where a retain was accepted");
            Assert.IsTrue(Regex.IsMatch(ReadBrawler("ECS/Systems/NavMeshEffectiveTickSystem.cs"),
                    @"b\.Centre\.y,\s*b\.Retain\s*\)"),
                "Collect no longer passes the component's mode to the rebaker");
        }

        [Test]
        public void PooledPlaceCommands_AssignRetainOnEveryIssue()
        {
            // CommandPool.Get resets only PlayerId and Tick, so a recycled PlaceBuildingCommand
            // still carries the previous caller's mode. Every field is assigned on every issue,
            // deliberately — this pins that Retain joined that list at both issuing sites.
            string sender = ReadBrawler("Manager/BrawlerSimulationCallbacks.cs");
            Assert.IsTrue(
                Regex.IsMatch(sender, @"CommandPool\.Get<PlaceBuildingCommand>\(\)[\s\S]{0,400}?cmd\.Retain\s*=\s*retain\s*;"),
                "SendPlaceBuildingCommand no longer assigns cmd.Retain from its parameter — a pooled "
                + "instance would go out with whatever mode the previous send had");
            Assert.IsTrue(
                Regex.IsMatch(sender, @"CommandPool\.Get<PlaceHexBuildingCommand>\(\)[\s\S]{0,400}?cmd\.Retain\s*=\s*retain\s*;"),
                "SendPlaceHexBuildingCommand no longer assigns cmd.Retain from its parameter");

            string bots = ReadBrawler("ECS/Systems/BotFSMSystem.cs");
            Assert.IsTrue(Regex.IsMatch(bots, @"_placeCmd\.Retain\s*=\s*[^;]*beat[^;]*;"),
                "the headless demo no longer assigns _placeCmd.Retain as a function of the beat — "
                + "the reused instance would carry the previous box's mode, and a mode not derived "
                + "from the frame would not survive a re-executed tick");
            Assert.IsTrue(Regex.IsMatch(bots, @"_placeHexCmd\.Retain\s*=\s*[^;]*beat[^;]*;"),
                "the headless demo no longer assigns _placeHexCmd.Retain as a function of the beat");
        }

        [Test]
        public void BotFallback_DoesNotSteerStraightThroughARetainedBuilding()
        {
            // The PathFailed fallback used to steer straight at the destination whenever the agent's
            // velocity was zero — which defeats every FindPath-based block, the retained footprint
            // included (no collider stops a Brawler building). The narrowed form fires only for a
            // bot standing INSIDE a retained footprint, and drops the destination otherwise. Text,
            // because the EditMode fixture's bots never acquire a destination
            // (NavMeshEffectiveTickTests.Fixture_HowFarTheBotsGet_AndWhereItStops), so the live
            // form of this gate has nothing to drive it yet.
            //
            // Being text has a second cost worth naming: when FindPath began exempting a masked-out
            // START, the case this fallback was written for stopped reaching it — a bot a building
            // lands on now gets an ordinary route out under the default masks — and this gate stayed
            // green throughout, because the source still matches. Nothing was going to redden. The
            // branch is kept deliberately (a narrowed per-agent PLAN mask still produces PathFailed
            // inside a footprint), and that reasoning lives in the comment this regex sits next to,
            // which is the only thing keeping it from becoming code nobody can explain.
            string bots = ReadBrawler("ECS/Systems/BotFSMSystem.cs");
            Match fallback = Regex.Match(bots,
                // 2600, not 1600: the budget is there to stop the match running into a neighbouring
            // method, not to cap how much the branch is allowed to explain itself — and raising
            // it is what this gate asked for the moment the comment grew (measured gap: 2129).
            @"PathFailed fallback[\s\S]{0,2600}?FPNavAgentStatus\.PathFailed[\s\S]{0,400}?StandsInsideBuilding\([\s\S]{0,900}?HasDestination\s*=\s*false");
            Assert.IsTrue(fallback.Success,
                "the fallback is no longer gated on PathFailed + standing inside a retained footprint, "
                + "or no longer drops an unreachable destination on ordinary ground — the straight steer "
                + "would walk bots through retained buildings again");
            Assert.IsTrue(Regex.IsMatch(bots, @"StandsInsideBuilding\(int[\s\S]{0,400}?FPNavMeshAreas\.BUILDING_MASK"),
                "StandsInsideBuilding no longer reads the installed mesh's building stamp");
        }
    }
}
