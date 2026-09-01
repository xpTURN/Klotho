using System;
using System.Buffers.Binary;
using System.IO;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Replay;

using Brawler; // BrawlerReplayClaim

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Gates for the <c>--verify</c> runner.
    ///
    /// <para><b>What is covered.</b> Every verdict the runner can reach from the file alone — the ones that
    /// decide BEFORE a simulation exists. Those are exactly the ones worth pinning: each of them is a way
    /// for a bad or unverifiable replay to be waved through, and two of them (a missing claim, zeroed
    /// anchors) are things an attacker controls directly.</para>
    ///
    /// <para><b>What is not.</b> The happy path (verified), a forged claim, and everything about the
    /// rebuilt tick 0 (<c>tick0=reconstructed</c>, the hash comparison against a world that exists) need a
    /// real recorded Brawler match to re-simulate, which this suite cannot produce — it would have to run a
    /// full match through the Unity client. Those stay manual: record a match, then <c>--verify</c> the
    /// file. Synthetic metadata reaches only the verdicts below, which is exactly why the two runner bugs
    /// found in Phase A were found against a real file and not here.</para>
    ///
    /// <para>The synthetic metadata deliberately uses <c>MaxEntities = 64</c> with no prune set and no
    /// maxCount overrides — the same layout inputs the room suites in this binary freeze the process with,
    /// so the layout call is idempotent rather than a conflict.</para>
    ///
    /// Run: dotnet run -- --test
    /// </summary>
    public static class BrawlerReplayVerifierTests
    {
        private static int _passed;
        private static int _failed;

        private const int LayoutMaxEntities = 64;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Console.WriteLine("\n=== BrawlerReplayVerifier Tests ===\n");

            // No logger: the runner logs nothing that these gates assert on, and a silent suite
            // keeps the --test output readable.
            IKLogger logger = null;
            string dir = Path.Combine(Path.GetTempPath(), "klotho-verify-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ClaimCodec();
                ArgParsing();
                AnchorsReporting();
                FileLevel(dir, logger);
                MetadataGates(dir, logger);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup is best-effort */ }
            }

            Console.WriteLine($"\n=== BrawlerReplayVerifier results: {_passed} passed, {_failed} failed ===");
            return _failed;
        }

        // ── the claim codec ────────────────────────────────────────────────

        static void ClaimCodec()
        {
            byte[] blob = BrawlerReplayClaim.Encode(2, 4800, FixedString32.FromString("timeout"));
            Assert("claim round-trips", BrawlerReplayClaim.TryDecode(blob, out var back)
                && back.WinnerPlayerId == 2 && back.EndTick == 4800
                && back.MatchEndReason.ToString() == "timeout");

            // Absence must be distinguishable from a valid claim. If "no claim" decoded to a default
            // claim, stripping the blob would make every replay verify against a default result.
            Assert("null is not a claim", !BrawlerReplayClaim.TryDecode(null, out _));
            Assert("empty is not a claim", !BrawlerReplayClaim.TryDecode(Array.Empty<byte>(), out _));
            Assert("short buffer is not a claim", !BrawlerReplayClaim.TryDecode(new byte[] { 1, 2, 3 }, out _));

            // GameCustomData is a free-form game slot: something else living there must not decode as a claim.
            byte[] foreign = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 3 });
            Assert("foreign blob is not a claim", !BrawlerReplayClaim.TryDecode(foreign, out _));

            // A claim from a future layout is refused rather than read with today's fields.
            byte[] wrongVersion = (byte[])blob.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(wrongVersion.AsSpan(4, 4), BrawlerReplayClaim.CurrentVersion + 1);
            Assert("wrong claim version is refused", !BrawlerReplayClaim.TryDecode(wrongVersion, out _));
        }

        // ── argv → paths + options ─────────────────────────────────────────
        //
        // This path had NO coverage, which is how it shipped opening `--rtt-metrics` as a replay: every other
        // gate here calls Verify directly, and Run had exactly one caller (Program.cs). The rule under test is
        // "a token starting with -- is an option, never a path" — pinned because the failure mode is silent
        // and lands on the exit code, the one thing a queue consumer branches on.

        static void ArgParsing()
        {
            var run = BrawlerReplayVerifier.ParseArgs(
                new[] { "--verify", "a.rply", "b.rply" }, KLogLevel.Warning);
            Assert("positional args are the file list", run.Files.Length == 2
                && run.Files[0] == "a.rply" && run.Files[1] == "b.rply");
            Assert("--verify is not a file", Array.IndexOf(run.Files, "--verify") < 0);
            Assert("a clean argv warns about nothing", run.Warnings.Length == 0);
            Assert("default level is kept", run.Level == KLogLevel.Warning);

            var lvl = BrawlerReplayVerifier.ParseArgs(
                new[] { "--verify", "--log=information", "a.rply" }, KLogLevel.Warning);
            Assert("--log= sets the level (case-insensitive)", lvl.Level == KLogLevel.Information);
            Assert("--log= is consumed, not verified", lvl.Files.Length == 1 && lvl.Files[0] == "a.rply");

            // The regression this whole block exists for: a mistyped level used to become a replay path and
            // end the batch at file-error(30) — "that file is unreadable" for what is really a usage error.
            var typo = BrawlerReplayVerifier.ParseArgs(
                new[] { "--verify", "--log=Infomation", "a.rply" }, KLogLevel.Warning);
            Assert("a mistyped level is not a file", typo.Files.Length == 1 && typo.Files[0] == "a.rply");
            Assert("a mistyped level keeps the default", typo.Level == KLogLevel.Warning);
            Assert("a mistyped level is said out loud", typo.Warnings.Length == 1);

            // TryParse alone accepts any number, so this used to "succeed" into an undefined level and
            // silence the run — the opposite of what --log= is reached for.
            var numeric = BrawlerReplayVerifier.ParseArgs(
                new[] { "--verify", "--log=999", "a.rply" }, KLogLevel.Warning);
            Assert("an out-of-range level is refused", numeric.Level == KLogLevel.Warning
                && numeric.Warnings.Length == 1);

            // Not a --log= problem: every flag this binary advertises hit the same path.
            var other = BrawlerReplayVerifier.ParseArgs(
                new[] { "--verify", "a.rply", "--rtt-metrics" }, KLogLevel.Warning);
            Assert("an unrelated flag is not a file", other.Files.Length == 1 && other.Files[0] == "a.rply");
            Assert("an unrelated flag is said out loud", other.Warnings.Length == 1);

            // No files left → usage(2), never a verdict. Verdicts start at 10 so a misinvocation can
            // never be read as "cheat found".
            var flagsOnly = BrawlerReplayVerifier.ParseArgs(new[] { "--verify", "--log=Debug" }, KLogLevel.Warning);
            Assert("options alone leave no files", flagsOnly.Files.Length == 0);
            Assert("no files → usage, not a verdict",
                BrawlerReplayVerifier.Run(flagsOnly.Files, null) == BrawlerReplayVerifier.ExitUsage);
        }

        // ── the anchors= field ─────────────────────────────────────────────
        //
        // `anchors=ok` used to be printed whether three terms were compared or none were, because a term is
        // only compared when both sides carry a value and 0 is a legitimate "not provided" (a game with no
        // IGameFingerprintSource). The count is what makes "nobody looked" visible, and these pin the three
        // shapes apart. The comparison itself needs a real simulation, so only the formatter is reachable
        // here — that is exactly the piece that decided the word.

        static void AnchorsReporting()
        {
            Assert("all three compared → ok:3/3",
                BrawlerReplayVerifier.AnchorsField(3, "colliders,nav,game") == "anchors=ok:3/3");

            // The regression that matters: fewer than three must NOT read as ok, and must name what it saw.
            Assert("one compared → partial, and says which",
                BrawlerReplayVerifier.AnchorsField(1, "colliders") == "anchors=partial:1/3(colliders)");
            Assert("two compared → partial",
                BrawlerReplayVerifier.AnchorsField(2, "colliders,nav") == "anchors=partial:2/3(colliders,nav)");

            // Nothing compared is the 0-tick playback case, and it is not a pass.
            Assert("none compared → unchecked",
                BrawlerReplayVerifier.AnchorsField(0, "") == "anchors=unchecked");
            Assert("unchecked never says ok",
                !BrawlerReplayVerifier.AnchorsField(0, "").Contains("ok", StringComparison.Ordinal));
        }

        // ── things that stop at the file ───────────────────────────────────

        static void FileLevel(string dir, IKLogger logger)
        {
            var missing = BrawlerReplayVerifier.Verify(Path.Combine(dir, "nope.rply"), logger);
            Assert("missing file → file-error", missing.Code == BrawlerReplayVerifier.ExitFileError);

            // A replay from another format version must be refused outright, not read with today's layout.
            byte[] bytes = NewReplayBytes(m => { });
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 1); // Version sits right after the magic
            string path = Write(dir, "v1.rply", bytes);
            var v1 = BrawlerReplayVerifier.Verify(path, logger);
            Assert("other version → file-error", v1.Code == BrawlerReplayVerifier.ExitFileError);
            Assert("other version says re-record", v1.Line.Contains("re-record", StringComparison.Ordinal));

            // 3 is the version immediately before the tick-0 roster, and its layout is a strict prefix of
            // the current one — the one old version a lenient reader would parse happily, handing the
            // verifier an empty roster and silently downgrading it to snapshot-restore.
            byte[] v3bytes = NewReplayBytes(m => { });
            BinaryPrimitives.WriteInt32LittleEndian(v3bytes.AsSpan(4, 4), 3);
            var v3 = BrawlerReplayVerifier.Verify(Write(dir, "v3.rply", v3bytes), logger);
            Assert("version 3 → file-error", v3.Code == BrawlerReplayVerifier.ExitFileError);
        }

        // ── verdicts decided from the metadata, before any simulation ──────

        static void MetadataGates(string dir, IKLogger logger)
        {
            // A recording cut by a desync recovery is faithful only up to the cut. Short is honest, and
            // reading it as a cheat (claim-mismatch) would be the worst possible misread.
            var truncated = Run(dir, logger, "truncated", m => m.EndReason = ReplayEndReason.ResyncRequest);
            Assert("truncated → unverifiable", truncated.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("truncated is not a cheat verdict", truncated.Code != BrawlerReplayVerifier.ExitClaimMismatch);

            // An unstamped recording is a bug in whatever ended it, and nothing about it can be trusted.
            var unspecified = Run(dir, logger, "unspecified", m => m.EndReason = ReplayEndReason.Unspecified);
            Assert("unspecified endReason → unverifiable", unspecified.Code == BrawlerReplayVerifier.ExitUnverifiable);

            // A snapshot taken mid-match leaves playback's clock and the restored frame on different bases.
            var midMatch = Run(dir, logger, "midmatch", m => m.InitialStateTick = 7);
            Assert("mid-match snapshot → unverifiable", midMatch.Code == BrawlerReplayVerifier.ExitUnverifiable);

            // Zeroed anchors are an attacker-controlled bypass if absence is read as "nothing to compare".
            var noAnchors = Run(dir, logger, "noanchors", m =>
            {
                m.LayoutFingerprint = 0;
                m.StaticColliderFingerprint = 0;
                m.NavFingerprint = 0;
                m.GameFingerprint = 0;
            });
            Assert("absent anchors → unverifiable", noAnchors.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("absent anchors are named as absent", noAnchors.Line.Contains("anchors=absent", StringComparison.Ordinal));
            Assert("absent anchors are never verified", noAnchors.Code != BrawlerReplayVerifier.ExitVerified);

            // …and the bypass is the ENVIRONMENT terms, not all four. The gate used to AND in the layout
            // term, which the format cannot produce as zero (it is an FNV fold the recorder always fills),
            // so zeroing just these three walked straight past a gate whose own comment names that bypass.
            var noEnvAnchors = Run(dir, logger, "noenvanchors", m =>
            {
                m.StaticColliderFingerprint = 0;
                m.NavFingerprint = 0;
                m.GameFingerprint = 0;
                // LayoutFingerprint left valid on purpose — that is what made the old gate unreachable.
            });
            Assert("zeroed environment anchors → unverifiable", noEnvAnchors.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("zeroed environment anchors are named as absent",
                noEnvAnchors.Line.Contains("anchors=absent", StringComparison.Ordinal));

            // A file recorded against a different component layout cannot be judged here at all — and the
            // verdict must say so by name, which is why the four terms are recorded separately.
            var wrongLayout = Run(dir, logger, "wronglayout", m => m.LayoutFingerprint = 0x0BAD_0BAD_0BAD_0BADL);
            Assert("layout mismatch → unverifiable", wrongLayout.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("layout mismatch is named", wrongLayout.Line.Contains("mismatch:layout", StringComparison.Ordinal));

            // A file that stamped no tick-0 hash cannot be attributed to any world, so there is nothing for
            // the rebuild to be judged against. Same rule as the anchors: absence is not agreement.
            var noHash = Run(dir, logger, "nohash", m => m.InitialStateHash = 0);
            Assert("absent tick-0 hash → unverifiable", noHash.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("absent tick-0 hash is named", noHash.Line.Contains("tick0hash=absent", StringComparison.Ordinal));
            Assert("absent tick-0 hash is never verified", noHash.Code != BrawlerReplayVerifier.ExitVerified);

            // Reconstruction needs a roster; without one it falls back to the snapshot, and these synthetic
            // files carry none. The point of the gate is the EXIT CODE: a failure inside StartReplay must
            // come back as a verdict (20), never escape as a process failure (1..9) — verdicts start at 10
            // so that a crashed runner can never be read as "cheat found".
            // Nothing to mutate: the base file already has everything the pre-session gates want, and no
            // snapshot — which is what makes StartReplay fail.
            var noStart = Run(dir, logger, "nosnapshot", m => { });
            Assert("unstartable replay → unverifiable", noStart.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("unstartable replay is not a process failure", noStart.Code >= BrawlerReplayVerifier.ExitClaimMismatch);
            Assert("unstartable replay is not a cheat verdict", noStart.Code != BrawlerReplayVerifier.ExitClaimMismatch);

            // Rebuilding tick 0 needs the room capacity the world was built with — bot ids are numbered past
            // it. A recording that carries a roster (so the runner WILL rebuild) but no capacity must say
            // that, rather than substituting a guess and reporting the resulting hash difference: that
            // failure looks exactly like a determinism bug and is not one.
            byte[] rosterNoCap = NewReplayBytes(m => { m.PlayerCount = 2; m.InitialRoster.AddRange(new[] { 7, 9 }); });
            var noCap = BrawlerReplayVerifier.Verify(Write(dir, "nocapacity.rply", rosterNoCap), logger);
            Assert("roster without capacity → unverifiable", noCap.Code == BrawlerReplayVerifier.ExitUnverifiable);
            Assert("missing capacity is named", noCap.Line.Contains("noMaxPlayers", StringComparison.Ordinal));
            Assert("missing capacity is not a cheat verdict", noCap.Code != BrawlerReplayVerifier.ExitClaimMismatch);

            // ...and the demand is scoped to rebuilding. A roster-less recording restores its snapshot, and
            // InitializeWorldState never runs there, so the same missing capacity must NOT refuse it — the
            // base file above has no capacity either and still reaches playback (the gate before this one).
            byte[] capStamped = NewReplayBytes(m =>
            {
                m.PlayerCount = 2;
                m.InitialRoster.AddRange(new[] { 7, 9 });
                m.MatchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 0, MaxPlayers = 4 });
            });
            var capOk = BrawlerReplayVerifier.Verify(Write(dir, "capacity.rply", capStamped), logger);
            Assert("stamped capacity passes the check",
                !capOk.Line.Contains("noMaxPlayers", StringComparison.Ordinal));

            // ── attribution: three answers, deliberately not one ───────────────
            // The runner has to tell "no identity in the payload" (normal) from "this payload predates the
            // field" (old) from an actual id. Folding them into one word makes operations treat normal files
            // as suspects on the day the field lands.
            //
            // There is no fourth "an identity was expected and is missing" case, and attr-local below PINS
            // that: the same bytes a lobby-issued-but-empty file would carry are what a lobbyless server and
            // a P2P host produce, so the runner cannot separate them and answers `unattributed` for all
            // three. Whether an id was OWED is the submission service's call — it knows what it dispatched.
            Assert("no payload → unattributed",
                AttributionOf(BrawlerReplayVerifier.Verify(Write(dir, "attr-none.rply",
                    NewReplayBytes(m => m.MatchConfigData = null)), logger)) == "unattributed");

            Assert("locally authored config → unattributed",
                AttributionOf(BrawlerReplayVerifier.Verify(Write(dir, "attr-local.rply", NewReplayBytes(m =>
                    m.MatchConfigData = BrawlerMatchConfig.Encode(
                        new BrawlerMatchConfigData { BotCount = 1, MaxPlayers = 4 }))), logger)) == "unattributed");

            // A blob SHORTER than the current layout is OLD, not stripped. Without this the first run after
            // the field lands buckets every pre-existing replay as suspicious.
            Assert("pre-identity payload → legacy-payload",
                AttributionOf(BrawlerReplayVerifier.Verify(Write(dir, "attr-legacy.rply",
                    NewReplayBytes(m => m.MatchConfigData = new byte[] { 1, 0, 0, 0, 4, 0, 0, 0 })), logger))
                    == "legacy-payload");

            // …and a CURRENT payload left at its defaults is NOT old. Every one of those values is legal on
            // its own — no bots, capacity not stamped (the codec calls 0 "not stamped"), no identity — and
            // together they decode to exactly what an older blob decodes to. Judging by the decoded values
            // therefore told the operator to re-record a file whose issuer is what needs fixing. The length
            // is what separates them: fixed-width Encode, so this blob is full size and the old one is 4.
            byte[] emptyCurrent = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData());
            Assert("a current payload is full size", emptyCurrent.Length == BrawlerMatchConfig.EncodedSize);
            Assert("current layout at defaults → unattributed, not legacy",
                AttributionOf(BrawlerReplayVerifier.Verify(Write(dir, "attr-emptycurrent.rply",
                    NewReplayBytes(m => m.MatchConfigData = emptyCurrent)), logger)) == "unattributed");

            Assert("lobby-issued identity is reported",
                AttributionOf(BrawlerReplayVerifier.Verify(Write(dir, "attr-id.rply", NewReplayBytes(m =>
                    m.MatchConfigData = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData
                    {
                        BotCount = 1, MaxPlayers = 4, MatchInstanceId = FixedString64.FromString("m9#t2"),
                    }))), logger)) == "m9#t2");

            // PlayerCount and the roster record the same thing twice. A disagreement is a corrupted file,
            // not a fallback: reconstructing from it would build the wrong number of participants.
            byte[] inconsistent = NewReplayBytes(m => { m.PlayerCount = 2; m.InitialRoster.Add(1); });
            var badRoster = BrawlerReplayVerifier.Verify(Write(dir, "badroster.rply", inconsistent), logger);
            Assert("roster disagreeing with PlayerCount → file-error",
                badRoster.Code == BrawlerReplayVerifier.ExitFileError);

            // Every verdict is one machine-readable line carrying its own code.
            Assert("verdict line is single-line", !truncated.Line.Contains('\n'));
            Assert("verdict line carries the code", truncated.Line.Contains(
                $"code={BrawlerReplayVerifier.ExitUnverifiable}", StringComparison.Ordinal));
        }

        // ── helpers ────────────────────────────────────────────────────────

        static BrawlerReplayVerifier.Outcome Run(string dir, IKLogger logger, string name, Action<ReplayMetadata> mutate)
            => BrawlerReplayVerifier.Verify(Write(dir, name + ".rply", NewReplayBytes(mutate)), logger);

        /// <summary>
        /// A replay whose metadata is plausible enough to reach the gate under test. It carries no commands
        /// and no snapshot — every assertion here decides before playback would need either.
        /// </summary>
        static byte[] NewReplayBytes(Action<ReplayMetadata> mutate)
        {
            var data = new ReplayData();
            var m = (ReplayMetadata)data.Metadata;
            m.PlayerCount = 2;
            m.TickIntervalMs = 25;
            m.MaxEntities = LayoutMaxEntities;
            m.TotalTicks = 0;
            m.EndReason = ReplayEndReason.Normal;
            m.InitialStateTick = 0;
            // Anchors that would pass: the layout term must be this process's, since a verifier compares it
            // against a layout it builds from the file's own inputs.
            ComponentStorageRegistry.EnsureLayoutComputed(LayoutMaxEntities, null, null);
            m.LayoutFingerprint = ComponentStorageRegistry.LayoutFingerprint;
            m.StaticColliderFingerprint = 0x1111_2222_3333_4444L;
            m.NavFingerprint = 0x5555_6666_7777_8888L;
            m.GameFingerprint = unchecked((long)0x9999_AAAA_BBBB_CCCCUL);
            // A tick-0 hash that would pass the completeness check. Its VALUE cannot pass the comparison
            // against a rebuilt world — nothing here builds one — so every gate below stops before that.
            m.InitialStateHash = 0x0123_4567_89AB_CDEFL;
            mutate(m);
            return data.Serialize();
        }

        static string Write(string dir, string name, byte[] bytes)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>Reads the verdict line's attribution field. These synthetic files stop before playback,
        /// so the field is asserted where the runner emits it — on the early-return lines too.</summary>
        static string AttributionOf(BrawlerReplayVerifier.Outcome outcome)
        {
            const string key = "match=";
            int i = outcome.Line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + key.Length;
            int end = outcome.Line.IndexOf(' ', start);
            return end < 0 ? outcome.Line.Substring(start) : outcome.Line.Substring(start, end - start);
        }

        static void Assert(string name, bool condition)
        {
            if (condition) { Console.WriteLine($"  PASS: {name}"); _passed++; }
            else           { Console.WriteLine($"  FAIL: {name}"); _failed++; }
        }
    }
}
