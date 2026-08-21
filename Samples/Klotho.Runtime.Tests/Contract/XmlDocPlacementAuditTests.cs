using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace xpTURN.Klotho.Runtime.Tests.Contract
{
    /// <summary>
    /// Catches a doc comment that has come loose from the member it describes, asserted against the
    /// source as TEXT.
    ///
    /// <para><b>The edit that causes it.</b> A new member gets inserted BETWEEN an existing
    /// <c>/// &lt;summary&gt;</c> block and the member it documented. The old block now sits on the
    /// newcomer, and two <c>&lt;summary&gt;</c> elements end up in one doc comment. Measured twice in
    /// this repo: <c>SegmentsProperlyCross</c>'s contract landed on <c>PointOnSegmentScaled</c>, and
    /// <c>KlothoSessionDriver</c>'s whole class doc — a public MonoBehaviour, the Unity entry point —
    /// landed on a two-value enum and stayed there for two months.</para>
    ///
    /// <para><b>Why a text pin and not the compiler.</b> The build cannot see this, twice over.
    /// <c>GenerateDocumentationFile</c> is set in no csproj or props file here, so no XML is produced
    /// to validate; and turning it on does not help — csc emits no diagnostic for a duplicate
    /// <c>&lt;summary&gt;</c>, only a flood of CS1591 for members that have no doc at all. Verified by
    /// building with the flag forced on: the generated XML carried the wrong summary first and said
    /// nothing about it.</para>
    ///
    /// <para><b>Why the outcome is worse than a missing doc.</b> The victim keeps its own summary,
    /// but SECOND. A consumer that reads the first one — IDE quick info, a doc generator — shows the
    /// other member's contract on it, and the described member gets no entry at all. Silence would be
    /// better than a confident wrong answer.</para>
    ///
    /// <para><b>Known limit.</b> The check is purely lexical: a doc comment whose
    /// <c>&lt;code&gt;</c> example legitimately contains a <c>/// &lt;summary&gt;</c> line would trip
    /// it. No such example exists today (the scan finds exactly the real cases and nothing else); if
    /// one is ever written, exempt that file rather than loosening the pattern.</para>
    /// </summary>
    [TestFixture]
    public class XmlDocPlacementAuditTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        private static readonly Regex SummaryOpen = new Regex(@"^\s*///\s*<summary>", RegexOptions.Compiled);

        [Test]
        public void NoDocCommentSitsOnTheWrongMember()
        {
            string packageRoot = Path.Combine(RepoRoot(), "com.xpturn.klotho");
            var offenders = new List<string>();
            int scanned = 0;

            foreach (string path in Directory.EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories))
            {
                // Build output only — the package's own sources are what ships.
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                scanned++;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i + 1 < lines.Length; i++)
                {
                    // A closed summary followed immediately by another opening one: the first block
                    // describes a member that is no longer the next thing in the file.
                    if (lines[i].Contains("</summary>") && SummaryOpen.IsMatch(lines[i + 1]))
                    {
                        offenders.Add($"{Path.GetRelativePath(RepoRoot(), path)}:{i + 1}");
                    }
                }
            }

            Assert.Greater(scanned, 100,
                $"only {scanned} source files scanned under {packageRoot} — the walk is not reaching "
                + "the package, so this audit would pass by finding nothing");
            Assert.IsEmpty(offenders,
                "a doc comment is sitting on the wrong member — two <summary> elements in one doc "
                + "comment, which means a member was inserted between an older block and its own "
                + "member. Move the first block down to the member it describes:\n  "
                + string.Join("\n  ", offenders));
        }
    }
}
