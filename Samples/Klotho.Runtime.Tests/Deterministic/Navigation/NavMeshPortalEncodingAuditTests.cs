using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Audits the two premises that let a triangle's portals be stored as ONE BIT per edge
    /// (<c>byte portalFlip</c>) instead of six ints:
    ///
    ///   (1) on an interior edge, <c>portal in {(va,vb), (vb,va)}</c> — a portal is always a
    ///       permutation of that edge's own vertex pair, never an unrelated pair;
    ///   (2) <c>neighbor &gt;= 0</c> iff <c>portal &gt;= 0</c> — so the neighbour index alone
    ///       decides whether an edge is on the boundary.
    ///
    /// <para><b>Only those two are checked here</b>, deliberately. Running the full
    /// <c>NavMeshFixtureValidator</c> would also assert winding, centroid and degeneracy, mixing in
    /// failures that say nothing about the encoding — for instance
    /// <c>FPNavMeshQueryTests.SampleHeight_DegenerateTriangle_DoesNotCrash</c> is collinear with
    /// area 0 by design and trips the degeneracy assert.</para>
    ///
    /// <para>The goldens below are captured against the OLD six-int layout, and that baseline
    /// cannot be recreated afterwards: the compacted reader refuses the old asset version outright,
    /// and the assets themselves are overwritten by a re-export.</para>
    /// </summary>
    [TestFixture]
    public class NavMeshPortalEncodingAuditTests
    {
        #region Shared

        private static readonly string[] BakedAssets =
        {
            "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes",
            "Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes",
            "Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes",
            "Samples/Brawler/Assets/NavMesh/Data/8_heightmesh.NavMeshData.bytes",
            "Samples/GodotPolySample/NavigationRegion3D.NavMeshData.bytes",
        };

        private const string GoldenRelPath =
            "Samples/Klotho.Runtime.Tests/Deterministic/Navigation/Goldens/NavMeshPortalGolden.txt";

        private const string V3SampleRelPath =
            "Samples/Klotho.Runtime.Tests/TestData/NavMesh.V3.Stage02.bytes";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static void Mix(ref ulong h, long v)
        {
            unchecked
            {
                h = (h ^ (ulong)v) * FnvPrime;
                h = (h ^ ((ulong)v >> 32)) * FnvPrime;
            }
        }

        #endregion

        #region Premise checks

        /// <summary>
        /// Checks both premises and fills in the per-triangle flip byte.
        /// Returns the list of violations; empty means the mesh satisfies them.
        /// </summary>
        private static List<string> CheckPremises(FPNavMesh mesh, string label, out byte[] flips)
            => CheckPremises(mesh, label, out flips, out _);

        private static List<string> CheckPremises(
            FPNavMesh mesh, string label, out byte[] flips, out byte[] interiorMasks)
        {
            var violations = new List<string>();
            int triCount = mesh.Triangles.Length;
            flips = new byte[triCount];
            interiorMasks = new byte[triCount];

            for (int i = 0; i < triCount; i++)
            {
                FPNavMeshTriangle t = mesh.Triangles[i];
                for (int e = 0; e < 3; e++)
                {
                    int nb = t.GetNeighbor(e);
                    t.GetEdgeVertices(e, out int va, out int vb);
                    t.GetPortal(e, out int pl, out int pr);

                    bool portalSet = pl >= 0 && pr >= 0;

                    // Premise 2: neighbor >= 0 iff portal >= 0.
                    if ((nb >= 0) != portalSet)
                    {
                        violations.Add(
                            $"[{label}] T{i} e{e}: premise 2 violated — neighbor={nb}, portal=({pl},{pr})");
                        continue;
                    }

                    if (nb < 0)
                        continue;   // boundary edge — premise 1 applies to interior edges only

                    interiorMasks[i] |= (byte)(1 << e);

                    // Premise 1: portal in {(va,vb), (vb,va)}.
                    if (pl == va && pr == vb)
                    {
                        // flip = 0
                    }
                    else if (pl == vb && pr == va)
                    {
                        flips[i] |= (byte)(1 << e);
                    }
                    else
                    {
                        violations.Add(
                            $"[{label}] T{i} e{e}: premise 1 violated — portal=({pl},{pr}) != edge=({va},{vb})");
                    }

                    if (violations.Count > 20)
                        return violations;   // stop runaway output — 20 is plenty to judge by
                }
            }

            return violations;
        }

        /// <summary>
        /// Classifies winding and, in doing so, tests the stronger claim the encoding rests on:
        /// that flip is ONE BIT PER TRIANGLE, identical across its three edges.
        ///
        /// <para>Boundary edges carry no portal, so their bits stay 0 and the invariant that has to
        /// hold is <c>flip == 0</c> (CCW) or <c>flip == interiorMask</c> (CW). A flip that is a
        /// proper non-empty subset of the interior mask means the edges of one triangle disagree,
        /// which is a counterexample to the per-triangle claim.</para>
        /// </summary>
        private static string ClassifyWinding(byte[] flips, byte[] interiorMasks, List<string> violations, string label)
        {
            int ccw = 0, cw = 0, mixed = 0;
            for (int i = 0; i < flips.Length; i++)
            {
                if (flips[i] == 0) ccw++;
                else if (flips[i] == interiorMasks[i]) cw++;
                else
                {
                    mixed++;
                    if (violations.Count <= 20)
                    {
                        violations.Add(
                            $"[{label}] T{i}: per-triangle flip counterexample — flip=0x{flips[i]:X2} is " +
                            $"neither 0 nor interiorMask(0x{interiorMasks[i]:X2})");
                    }
                }
            }
            return $"CCW={ccw} CW={cw} mixed={mixed}";
        }

        #endregion

        #region Baked asset audit

        [Test]
        public void BakedAssets_SatisfyEncodingPremises()
        {
            string root = RepoRoot();
            var failures = new List<string>();
            int checkedAssets = 0, totalDirectionChecks = 0;

            TestContext.Out.WriteLine("=== baked asset premises + flip distribution ===");

            foreach (string rel in BakedAssets)
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path))
                {
                    TestContext.Out.WriteLine($"{rel}: MISSING — skipped");
                    continue;
                }

                FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
                string name = Path.GetFileNameWithoutExtension(rel);
                var v = CheckPremises(mesh, name, out byte[] flips, out byte[] interior);
                string winding = ClassifyWinding(flips, interior, v, name);
                int directionChecks = CheckPortalDirections(mesh, name, v);
                totalDirectionChecks += directionChecks;
                checkedAssets++;

                TestContext.Out.WriteLine(
                    $"{name,-22} tris={mesh.Triangles.Length,7} verts={mesh.Vertices.Length,7} " +
                    $"violations={v.Count,3}  dirChecks={directionChecks,7}  {winding}");

                failures.AddRange(v);
            }

            Assert.Greater(checkedAssets, 0, "no baked asset could be read — check the paths");

            // Liveness for check 4 specifically. The premise counters above stay healthy no matter
            // what, because premises 1 and 2 are tautologies now — so a green run says nothing
            // about whether the one real portal check examined anything. This is the counter that
            // does, and it must not be allowed to drift to zero the way check 4's asset coverage
            // silently was zero until it was wired up here.
            Assert.Greater(totalDirectionChecks, 0,
                "no interior edge of any baked asset had its portal direction checked — the only "
                + "non-tautological portal check on baked data is examining nothing");

            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// Runs check 4 — stored flip bit vs geometry — over every interior edge of a baked asset,
        /// appending violations. Returns how many edges were actually checked.
        ///
        /// <para>This is the audit's only NON-TAUTOLOGICAL portal check on baked data. Premises 1
        /// and 2 above became statements about <c>GetPortal</c>'s own arithmetic when portals
        /// collapsed to one bit per edge — <c>GetPortal</c> returns (-1,-1) exactly when the
        /// neighbour is negative, and otherwise builds the pair from the same
        /// <c>GetEdgeVertices(e)</c> the caller compares against, so neither can fail. The same
        /// change removed the portal literals from the baked JSON (2,598 lines from Field alone),
        /// leaving no independent record to compare against either. Check 4 survives because it
        /// tests the stored BIT against the mesh's geometry rather than against itself, and it was
        /// running on three hand-written fixtures and no asset at all.</para>
        /// </summary>
        private static int CheckPortalDirections(FPNavMesh mesh, string label, List<string> violations)
        {
            int checkedEdges = 0;
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                FPNavMeshTriangle t = mesh.Triangles[i];
                for (int e = 0; e < 3; e++)
                {
                    if (t.GetNeighbor(e) < 0)
                        continue;   // boundary edge — no portal to orient
                    t.GetEdgeVertices(e, out int va, out int vb);
                    t.GetPortal(e, out int pl, out int pr);
                    checkedEdges++;

                    string violation = NavMeshFixtureValidator.CheckPortalDirection(
                        mesh, t, i, e, va, vb, pl, pr, label);
                    if (violation != null && violations.Count <= 20)
                        violations.Add(violation);
                }
            }
            return checkedEdges;
        }

        #endregion

        #region Hand-written fixture literals (source scan)

        // Fixture triangles are object-initializer literals, and many of them are inline inside a
        // single test method rather than built by a shared helper — so no runtime path reaches them
        // all. This reads the source instead and checks both premises literal by literal. It also
        // covers the failure mode where an initializer simply loses a line and passes silently.
        [Test]
        public void HandWrittenFixtureLiterals_SatisfyEncodingPremises()
        {
            string root = RepoRoot();
            string dir = Path.Combine(root, "Samples/Klotho.Runtime.Tests/Deterministic/Navigation");
            Assert.IsTrue(Directory.Exists(dir), $"fixture directory not found: {dir}");

            var failures = new List<string>();
            int totalLiterals = 0, checkedLiterals = 0, skipped = 0, flipLiterals = 0;
            var perFile = new SortedDictionary<string, (int total, int ok, int skip)>();

            foreach (string file in Directory.GetFiles(dir, "*.cs"))
            {
                string fileName = Path.GetFileName(file);
                if (fileName == "NavMeshPortalEncodingAuditTests.cs")
                    continue;   // the scanner itself — its own marker string would be a false hit

                string src = File.ReadAllText(file);
                int fTotal = 0, fOk = 0, fSkip = 0;

                foreach ((string body, int line) in EnumerateTriangleInitializers(src))
                {
                    fTotal++;
                    totalLiterals++;

                    if (!TryReadInt(body, "v0", out int v0) ||
                        !TryReadInt(body, "v1", out int v1) ||
                        !TryReadInt(body, "v2", out int v2))
                    {
                        fSkip++; skipped++;   // non-literal (loop index and such) — not statically decidable
                        continue;
                    }

                    fOk++; checkedLiterals++;

                    // With the encoding in place a portal is one byte, and an unspecified one is 0,
                    // which is CCW and always valid. So the invariant to check becomes: flip bits
                    // may only be set on interior edges, and they are either all set or all clear
                    // (one bit per triangle).
                    //
                    // A field that is ASSIGNED but unreadable is reported rather than defaulted.
                    // Defaulting it is what made this audit vacuous: every flip literal is hex, the
                    // reader took only decimal, and "unreadable" silently became "0" — a value that
                    // satisfies both invariants. Failing here means the next reader-vs-house-style
                    // mismatch surfaces as a scanner bug instead of as quiet green.
                    if (!TryReadInt(body, "portalFlip", out int flip, defaultValue: 0)
                        && AssignsField(body, "portalFlip"))
                    {
                        failures.Add($"{fileName}:{line}: portalFlip is assigned but the scanner could " +
                                     "not read it — the audit would silently treat it as 0");
                        continue;
                    }
                    if (AssignsField(body, "portalFlip"))
                        flipLiterals++;

                    int interiorMask = 0;
                    bool neighborsReadable = true;
                    for (int e = 0; e < 3; e++)
                    {
                        // An OMITTED neighbour genuinely is 0, and 0 is a valid triangle index, so
                        // absent-means-interior is correct. An assigned-but-unreadable one is not
                        // decidable, and defaulting it to 0 would widen interiorMask — which biases
                        // the boundary check below toward passing.
                        if (!TryReadInt(body, $"neighbor{e}", out int nb, defaultValue: 0)
                            && AssignsField(body, $"neighbor{e}"))
                        {
                            failures.Add($"{fileName}:{line}: neighbor{e} is assigned but the scanner " +
                                         "could not read it — interiorMask would be guessed, not derived");
                            neighborsReadable = false;
                            break;
                        }
                        if (nb >= 0)
                            interiorMask |= 1 << e;
                    }
                    if (!neighborsReadable)
                        continue;

                    if ((flip & ~interiorMask) != 0)
                    {
                        failures.Add(
                            $"{fileName}:{line}: portalFlip=0x{flip:X2} sets a bit on a boundary edge " +
                            $"(interiorMask=0x{interiorMask:X2})");
                    }
                    else if (flip != 0 && flip != interiorMask)
                    {
                        failures.Add(
                            $"{fileName}:{line}: portalFlip=0x{flip:X2} is neither 0 nor interiorMask" +
                            $"(0x{interiorMask:X2}) — counterexample to one flip bit per triangle");
                    }
                }

                if (fTotal > 0)
                    perFile[fileName] = (fTotal, fOk, fSkip);
            }

            TestContext.Out.WriteLine("=== hand-written FPNavMeshTriangle literals ===");
            foreach (var kv in perFile)
            {
                TestContext.Out.WriteLine(
                    $"{kv.Key,-42} literals={kv.Value.total,3}  checked={kv.Value.ok,3}  skipped={kv.Value.skip,3}");
            }
            TestContext.Out.WriteLine(
                $"total: literals={totalLiterals} checked={checkedLiterals} skipped={skipped} "
                + $"withPortalFlip={flipLiterals}");

            Assert.Greater(checkedLiterals, 0, "no fixture literal could be read — check the scanner");

            // Liveness for the flip path specifically. checkedLiterals above only says the v0/v1/v2
            // reader works; it stayed comfortably positive through the entire period in which every
            // portalFlip was misread as 0 and neither invariant below could fire. A count of the
            // literals that actually carry a flip is the thing that would have caught that, so it
            // is asserted separately rather than folded into the coordinate counter.
            Assert.Greater(flipLiterals, 0,
                "no hand-written literal specifies portalFlip — either the fixtures stopped covering "
                + "the encoding this audit exists for, or the scanner has gone blind to it again");
            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        }

        /// <summary>Extracts each <c>new FPNavMeshTriangle { ... }</c> initializer body by brace depth.</summary>
        private static IEnumerable<(string body, int line)> EnumerateTriangleInitializers(string src)
        {
            const string marker = "new FPNavMeshTriangle";
            int pos = 0;
            while (true)
            {
                int start = src.IndexOf(marker, pos, StringComparison.Ordinal);
                if (start < 0)
                    yield break;

                int brace = src.IndexOf('{', start);
                if (brace < 0)
                    yield break;

                // Skip a constructor call (`new FPNavMeshTriangle()`) — only initializers qualify.
                string between = src.Substring(start + marker.Length, brace - start - marker.Length);
                if (between.IndexOf(';') >= 0)
                {
                    pos = start + marker.Length;
                    continue;
                }

                int depth = 0, i = brace;
                for (; i < src.Length; i++)
                {
                    if (src[i] == '{') depth++;
                    else if (src[i] == '}') { depth--; if (depth == 0) break; }
                }
                if (i >= src.Length)
                    yield break;

                int line = 1;
                for (int k = 0; k < start; k++)
                    if (src[k] == '\n') line++;

                yield return (src.Substring(brace, i - brace + 1), line);
                pos = i + 1;
            }
        }

        /// <summary>
        /// Reads an integer field out of an initializer body. Accepts hex as well as decimal.
        /// <para>The hex alternative is not a nicety. This file's own subject — <c>portalFlip</c> —
        /// is a bit mask, so every flip literal in the tree is written <c>0x01</c>-style, and a
        /// decimal-only <c>(-?\d+)</c> matches none of them: <c>\d+</c> takes the <c>0</c> and the
        /// trailing <c>\b</c> then fails between <c>0</c> and <c>x</c>. The scanner read every flip
        /// as the default 0, which is a legal value, so both invariants below were unreachable
        /// while the audit reported the literals as checked. The alternation is ordered hex-first;
        /// decimal-first would re-create exactly that failure.</para>
        /// </summary>
        private static bool TryReadInt(string body, string field, out int value, int defaultValue = 0)
        {
            var m = Regex.Match(body, $@"\b{Regex.Escape(field)}\s*=\s*(-?(?:0[xX][0-9a-fA-F]+|\d+))\b");
            if (m.Success)
            {
                string raw = m.Groups[1].Value;
                bool negative = raw[0] == '-';
                if (negative)
                    raw = raw.Substring(1);
                value = raw.Length > 1 && raw[0] == '0' && (raw[1] == 'x' || raw[1] == 'X')
                    ? Convert.ToInt32(raw.Substring(2), 16)
                    : int.Parse(raw);
                if (negative)
                    value = -value;
                return true;
            }
            value = defaultValue;
            return false;
        }

        /// <summary>
        /// Whether the initializer assigns this field at all, regardless of what it assigns.
        /// <para>Needed to tell "omitted" from "present but unreadable". They are not the same and
        /// must not share a default: an omitted field really is 0 (C# leaves it there), whereas an
        /// unreadable one means the scanner cannot decide — and silently calling that 0 is how the
        /// hex blindness above stayed invisible.</para>
        /// </summary>
        private static bool AssignsField(string body, string field)
        {
            return Regex.IsMatch(body, $@"\b{Regex.Escape(field)}\s*=");
        }

        #endregion

        #region Goldens

        /// <summary>
        /// Compares the five shipped assets against the pre-compaction baseline.
        ///
        /// <para>It does NOT create the baseline when it is missing, and that is the whole point.
        /// The file records what the SIX-INT portal layout said, and the header spells out why it
        /// cannot be produced again: the current reader refuses the old asset version, and the
        /// assets themselves were overwritten by a re-export. Regenerating would therefore capture
        /// what today's code happens to do and then compare it with itself forever — the baseline's
        /// only job, tying the compacted encoding back to the layout it replaced, would be gone
        /// with nothing to show it ever left.</para>
        ///
        /// <para>It used to write the file and call <c>Assert.Inconclusive</c>, which NUnit reports
        /// as Skipped and <c>dotnet test</c> exits 0 for. So resolving a merge conflict by deleting
        /// the file, a <c>git clean</c>, or a stale checkout destroyed an irreproducible baseline
        /// and reported success.</para>
        /// </summary>
        [Test]
        public void Goldens_MatchThePreCompactionBaseline()
        {
            string root = RepoRoot();
            string goldenPath = Path.Combine(root, GoldenRelPath);

            var lines = new List<string>
            {
                "# NavMesh portal baseline, captured against the six-int portal layout.",
                "# Compared after the one-bit encoding lands: GetPortal results, the rebake",
                "# fingerprint, and a geometry hash that separates our bugs from scene drift.",
                "# This baseline cannot be regenerated by the newer code — it refuses the old asset",
                "# version, and the assets are overwritten by a re-export.",
                "# format: asset|tris|verts|portalHash|fingerprint|geomHash|flipHistogram|blockHashes(4096tri)",
            };

            foreach (string rel in BakedAssets)
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path))
                {
                    lines.Add($"{Path.GetFileNameWithoutExtension(rel)}|MISSING");
                    continue;
                }

                FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
                string name = Path.GetFileNameWithoutExtension(rel);
                var ignored = new List<string>();
                CheckPremises(mesh, name, out byte[] flips, out byte[] interior);

                lines.Add(string.Join("|",
                    name,
                    mesh.Triangles.Length.ToString(),
                    mesh.Vertices.Length.ToString(),
                    $"0x{PortalHash(mesh, out string blocks):X16}",
                    $"0x{FPNavMeshRebaker.ComputeFingerprint(mesh):X16}",
                    $"0x{GeometryHash(mesh):X16}",
                    ClassifyWinding(flips, interior, ignored, name),
                    blocks));
            }

            string produced = string.Join("\n", lines) + "\n";

            if (!File.Exists(goldenPath))
            {
                // Deliberately not recreated — see this test's summary. What the run would have
                // produced is printed so the loss is diagnosable, but it is NOT written: a file
                // generated here would be today's behaviour wearing the baseline's name.
                TestContext.Out.WriteLine("would have produced:");
                TestContext.Out.WriteLine(produced);
                Assert.Fail(
                    $"the pre-compaction baseline is missing: {GoldenRelPath}\n" +
                    "It CANNOT be regenerated — it was captured against the six-int portal layout, "
                    + "the current reader refuses that asset version, and the assets were "
                    + "overwritten by a re-export. Restore it from git (it has been unchanged since "
                    + "e3673b1ed); do not let this test write a new one.");
            }

            string expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
            TestContext.Out.WriteLine(produced);
            Assert.AreEqual(expected, produced,
                "golden mismatch — read geomHash first: if it also moved the scene drifted, " +
                "if it matches and the rest moved the change is ours.");
        }

        /// <summary>Hash of <c>GetPortal</c> over every edge, plus per-4096-triangle block hashes so a
        /// mismatch can be localised instead of only detected.</summary>
        private static ulong PortalHash(FPNavMesh mesh, out string blockHashes)
        {
            const int BlockSize = 4096;
            ulong h = FnvOffset;
            ulong block = FnvOffset;
            var blocks = new List<string>();

            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                FPNavMeshTriangle t = mesh.Triangles[i];
                for (int e = 0; e < 3; e++)
                {
                    t.GetPortal(e, out int l, out int r);
                    Mix(ref h, l); Mix(ref h, r);
                    Mix(ref block, l); Mix(ref block, r);
                }

                if ((i + 1) % BlockSize == 0)
                {
                    blocks.Add($"{block:X16}");
                    block = FnvOffset;
                }
            }

            if (mesh.Triangles.Length % BlockSize != 0)
                blocks.Add($"{block:X16}");

            blockHashes = string.Join(",", blocks);
            return h;
        }

        /// <summary>Hash of vertex coordinates and triangle vertex indices — the separator between
        /// "our encoding changed" and "the source scene changed".</summary>
        private static ulong GeometryHash(FPNavMesh mesh)
        {
            ulong h = FnvOffset;
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                FPVector3 v = mesh.Vertices[i];
                Mix(ref h, v.x.RawValue); Mix(ref h, v.y.RawValue); Mix(ref h, v.z.RawValue);
            }
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                FPNavMeshTriangle t = mesh.Triangles[i];
                Mix(ref h, t.v0); Mix(ref h, t.v1); Mix(ref h, t.v2);
            }
            return h;
        }

        #endregion

        #region Clockwise winding path (flip = 1)

        /// <summary>
        /// A clockwise fixture must classify as <c>flip == interiorMask</c>. Every other
        /// hand-written fixture is counter-clockwise (<c>flip == 0</c>), so this is the only place
        /// the flip-set path is covered without loading a baked asset.
        ///
        /// <para>The expected value is not <c>0x07</c>: boundary edges carry no portal and leave
        /// their bits clear, so T0 — whose only interior edge is e0 — is <c>0x01</c>, and T1, whose
        /// only interior edge is e2, is <c>0x04</c>.</para>
        /// </summary>
        [Test]
        public void CwSquareFixture_IsClassifiedAsCw()
        {
            FPNavMesh mesh = NavAgentTestHelper.CreateCwSquareNavMesh();

            var violations = CheckPremises(mesh, "CW-square", out byte[] flips, out byte[] interior);
            Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));

            string winding = ClassifyWinding(flips, interior, violations, "CW-square");
            TestContext.Out.WriteLine($"CW-square: {winding}  flips=[0x{flips[0]:X2}, 0x{flips[1]:X2}]");
            Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));

            Assert.AreEqual(0x01, flips[0], "T0's only interior edge is e0 — only bit 0 may be set");
            Assert.AreEqual(0x04, flips[1], "T1's only interior edge is e2 — only bit 2 may be set");
            Assert.AreEqual(interior[0], flips[0], "T0: clockwise means every interior edge is flipped");
            Assert.AreEqual(interior[1], flips[1], "T1: clockwise means every interior edge is flipped");
            Assert.AreEqual("CCW=0 CW=2 mixed=0", winding);
        }

        /// <summary>
        /// <c>SetPortal</c> then <c>GetPortal</c> must round-trip in BOTH vertex orders. With six
        /// ints stored verbatim this passes trivially; once a portal is one bit, this is what
        /// directly verifies the flip bit survives the round trip — which is why it is written
        /// before the encoding lands rather than after.
        /// </summary>
        [Test]
        public void SetPortal_GetPortal_RoundTripsBothOrders()
        {
            for (int e = 0; e < 3; e++)
            {
                var tri = new FPNavMeshTriangle { v0 = 7, v1 = 11, v2 = 13 };
                tri.SetNeighbor(e, 1);   // make it an interior edge — on a boundary (-1,-1) is correct
                tri.GetEdgeVertices(e, out int va, out int vb);

                tri.SetPortal(e, va, vb);
                tri.GetPortal(e, out int l, out int r);
                Assert.AreEqual(va, l, $"e{e}: (va,vb) round trip, left");
                Assert.AreEqual(vb, r, $"e{e}: (va,vb) round trip, right");

                tri.SetPortal(e, vb, va);
                tri.GetPortal(e, out l, out r);
                Assert.AreEqual(vb, l, $"e{e}: (vb,va) round trip, left — the flip path");
                Assert.AreEqual(va, r, $"e{e}: (vb,va) round trip, right — the flip path");
            }
        }

        #endregion

        #region Old-format sample

        // After the assets are re-exported no old-format file is left in the repository, so this
        // pinned copy is the only input that can verify the reader refuses one.
        //
        // Reading the old format was deliberately NOT kept: silently misreading it would put the
        // portals out of step with the geometry, which is precisely the quiet-wrong-mesh failure
        // the encoding work is meant to avoid. A clear throw is the required behaviour.
        [Test]
        public void V3Sample_IsRejectedWithClearError()
        {
            string path = Path.Combine(RepoRoot(), V3SampleRelPath);
            Assert.IsTrue(File.Exists(path), $"old-format sample is not pinned: {V3SampleRelPath}");

            byte[] bytes = File.ReadAllBytes(path);
            int version = BitConverter.ToInt32(bytes, 0);
            Assert.AreEqual(3, version, "the pinned sample is not the expected old format version");

            var ex = Assert.Throws<InvalidOperationException>(
                () => FPNavMeshSerializer.Deserialize(bytes),
                "the old-format asset was read silently — it must be rejected");

            Assert.That(ex.Message, Does.Contain("version mismatch"),
                "the message does not name the cause");
            TestContext.Out.WriteLine($"old format rejected: {ex.Message}");
        }

        #endregion
    }
}
