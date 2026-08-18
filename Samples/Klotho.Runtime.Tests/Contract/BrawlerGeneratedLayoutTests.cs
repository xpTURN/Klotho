using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace xpTURN.Klotho.Runtime.Tests.Contract
{
    /// <summary>
    /// The generated component layout Brawler ships, pinned as TEXT.
    ///
    /// <para>Two builds compile the same ECS tree — the Unity client and the dedicated server —
    /// from two separate generated copies. If only one is regenerated the peers exchange FullState
    /// under different layouts, and nothing catches it: the wire carries a per-component size but
    /// <c>ComponentStorageRegistry</c> reads it without comparing, so the stream is not skipped,
    /// it is misread, and every type after the mismatched one is corrupted in turn. The only
    /// defence today is the same-build-per-match deployment assumption.</para>
    ///
    /// <para>Pinned as text because the alternative is not available here: these types live in
    /// Brawler, which this assembly does not reference, and the Unity EditMode suite cannot run
    /// without an editor. Reading the generated files gets the guard into a suite that actually
    /// runs — and the values it pins (type id, serialized size, slot count) are exactly the ones
    /// changes, so an intentional change has to come here and say so.</para>
    /// </summary>
    [TestFixture]
    public class BrawlerGeneratedLayoutTests
    {
        private const string ClientDir = "Samples/Brawler/Tools/Generated/Brawler.ECS";
        private const string ServerDir = "Samples/Brawler/Tools/Generated/BrawlerDedicatedServer";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        private static string ReadGenerated(string dir, string file)
        {
            string path = Path.Combine(RepoRoot(), dir, file);
            if (!File.Exists(path))
                Assert.Ignore($"generated file missing: {dir}/{file}");
            return File.ReadAllText(path);
        }

        [Test]
        public void BuildingComponent_LayoutIsWhatTheWireExpects()
        {
            string src = ReadGenerated(ClientDir, "Brawler_BuildingComponent.g.cs");

            Assert.IsTrue(Regex.IsMatch(src, @"TYPE_ID\s*=\s*108\b"),
                "BuildingComponent's type id moved — the id is wire state, not an implementation detail");
            Assert.IsTrue(Regex.IsMatch(src, @"GetSerializedSize\(\)\s*=>\s*48\b"),
                "BuildingComponent's serialized size changed. That is a wire change: regenerate BOTH "
                + "copies and update this pin in the same commit. 48 = the original 40 plus "
                + "EffectiveTick and RemovalEffectiveTick");
            Assert.IsTrue(Regex.IsMatch(src, @"maxCount:\s*40\b"),
                "BuildingComponent's slot count changed — it feeds LayoutFingerprint, so every peer "
                + "must ship the same value. 40 is storage, not policy: MaxBuildings still admits 32 "
                + "standing, and the surplus holds tombstones awaiting RemovalEffectiveTick");
        }

        [Test]
        public void ClientAndServerGeneratedCopies_AreIdentical()
        {
            // Every file the two builds share. A pair that drifts is the failure above, and it is
            // silent — so the comparison is byte-for-byte over the whole intersection rather than
            // over whichever component a given change happens to touch.
            string root = RepoRoot();
            var clientDir = new DirectoryInfo(Path.Combine(root, ClientDir));
            var serverDir = new DirectoryInfo(Path.Combine(root, ServerDir));
            if (!clientDir.Exists || !serverDir.Exists)
                Assert.Ignore("generated directories missing");

            int compared = 0;
            foreach (FileInfo client in clientDir.GetFiles("*.g.cs"))
            {
                string serverPath = Path.Combine(serverDir.FullName, client.Name);
                if (!File.Exists(serverPath))
                    continue;                       // server-only or client-only types are fine

                Assert.AreEqual(
                    File.ReadAllText(client.FullName), File.ReadAllText(serverPath),
                    $"{client.Name} differs between the client and server generated copies — "
                    + "one of the two was regenerated alone");
                compared++;
            }

            Assert.Greater(compared, 0, "no shared generated files found — the paths are wrong, not the code");
            Assert.IsTrue(File.Exists(Path.Combine(serverDir.FullName, "Brawler_BuildingComponent.g.cs")),
                "the server copy of BuildingComponent is missing — the server compiles the ECS tree too");
        }
    }
}
