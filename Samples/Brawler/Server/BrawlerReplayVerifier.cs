using System;
using System.Collections.Generic;
using System.Text;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Replay;

using Brawler;

namespace xpTURN.Klotho.BrawlerDedicatedServer
{
    /// <summary>
    /// Re-simulates a replay in this server build and compares the result it derives with the result the
    /// client claimed.
    ///
    /// <para><b>What this can and cannot say.</b> Re-simulation is authoritative about what the recorded
    /// INPUTS produce — nothing else. It always agrees with itself, so a replay with no claim proves
    /// nothing and is reported UNVERIFIABLE rather than passed. And every anchor in the file is a
    /// non-cryptographic fold with no signature, so a forger rewrites them along with the payload: what the
    /// anchors buy is the ability to dismiss an HONEST client's failure as a version or content mismatch
    /// instead of calling it cheating.</para>
    ///
    /// <para><b>Where tick 0 comes from.</b> This REBUILDS tick 0 from the recorded roster, seed and
    /// config instead of restoring the snapshot the recording carries: re-simulating from the client's own
    /// bytes says nothing about the starting point those bytes describe. The rebuilt state is compared
    /// against the recorded hash, and a disagreement is UNVERIFIABLE rather than cheating — its commonest
    /// cause is a different content revision, which no anchor in the file covers. A recording that did not
    /// build its own tick 0 (an SD client, whose initial state arrived from the server) carries no roster
    /// and falls back to restoring the snapshot; the verdict line says which of the two happened, because a
    /// verified restore is a weaker statement than a verified rebuild.</para>
    /// </summary>
    internal static class BrawlerReplayVerifier
    {
        /// <summary>Re-simulation matched the claim.</summary>
        public const int ExitVerified = 0;
        /// <summary>Re-simulation disagreed with the claim — a cheat suspicion, and a SUCCESSFUL run.</summary>
        public const int ExitClaimMismatch = 10;
        /// <summary>Nothing could be decided: wrong build/content, no claim, or a truncated recording.</summary>
        public const int ExitUnverifiable = 20;
        /// <summary>The file could not be read at all.</summary>
        public const int ExitFileError = 30;
        /// <summary>Bad arguments. Kept inside 1..9 with the rest of this binary's process failures —
        /// verdicts start at 10 precisely so a crashed runner can never be read as "cheat found".</summary>
        public const int ExitUsage = 2;

        /// <summary>Ticks to allow beyond the recorded length before giving up (D-9: budget in ticks, not
        /// wall clock — nothing here sleeps).</summary>
        private const int TickBudgetMargin = 120;

        internal readonly struct Outcome
        {
            public readonly int Code;
            public readonly string Line;
            public Outcome(int code, string line) { Code = code; Line = line; }
        }

        /// <summary>Argv split into the replay paths and the options that came with them.</summary>
        internal readonly struct ParsedArgs
        {
            /// <summary>Positional arguments only. Every entry is a replay path — no placeholder, no flag.</summary>
            public readonly string[] Files;
            public readonly KLogLevel Level;
            /// <summary>Lines about tokens that were not understood. The caller prints them; see ParseArgs.</summary>
            public readonly string[] Warnings;

            public ParsedArgs(string[] files, KLogLevel level, string[] warnings)
            { Files = files; Level = level; Warnings = warnings; }
        }

        /// <summary>
        /// Splits the process argv into replay paths and options.
        ///
        /// <para><b>A token starting with <c>--</c> is an option, never a path.</b> That rule — not a list of
        /// known flags — is what keeps the exit code honest. Argv used to be filtered for a well-formed
        /// <c>--log=</c> and everything else handed to <see cref="Verify"/>, so a mistyped level, or any other
        /// flag this binary advertises (<c>--rtt-metrics</c>, <c>--save-replays</c>, …), was opened as a replay
        /// and came back <c>file-error</c>. Since the process returns the WORST verdict, one such token ended a
        /// batch of good files at 30 — which tells a queue consumer "that file could not be read" when the
        /// truth is "you invoked me wrong", and this binary already has <see cref="ExitUsage"/> for that. A
        /// replay path never starts with <c>--</c>; even on POSIX such a file must be spelled <c>./--x.rply</c>.
        /// <c>--verify</c> itself is covered by the same rule, which is why it needs no special case.</para>
        ///
        /// <para><b>Unknown options warn, they do not fail.</b> The exit code belongs to the files. Aborting on
        /// a stray flag would swap one wrong code for another, and a bad log level says nothing about whether
        /// the replays verify.</para>
        /// </summary>
        internal static ParsedArgs ParseArgs(string[] argv, KLogLevel defaultLevel)
        {
            var files = new List<string>(argv?.Length ?? 0);
            var warnings = new List<string>();
            var level = defaultLevel;

            for (int i = 0; argv != null && i < argv.Length; i++)
            {
                string arg = argv[i];
                if (string.IsNullOrEmpty(arg)) continue;

                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    files.Add(arg);
                    continue;
                }

                if (arg.StartsWith("--log=", StringComparison.Ordinal))
                {
                    string value = arg.Substring("--log=".Length);
                    // IsDefined as well as TryParse: TryParse accepts any NUMBER, so `--log=999` would
                    // otherwise "succeed" into an undefined level and silence the run instead of opening it.
                    if (Enum.TryParse<KLogLevel>(value, ignoreCase: true, out var parsed)
                        && Enum.IsDefined(typeof(KLogLevel), parsed))
                        level = parsed;
                    else
                        warnings.Add($"[verify] unrecognised log level '{value}' — continuing at {defaultLevel}. "
                                     + $"Valid: {string.Join(", ", Enum.GetNames(typeof(KLogLevel)))}.");
                    continue;
                }

                if (!string.Equals(arg, "--verify", StringComparison.Ordinal))
                    warnings.Add($"[verify] ignored option '{arg}' — --verify takes replay paths and --log=<level>.");
            }

            return new ParsedArgs(files.ToArray(), level, warnings.ToArray());
        }

        /// <summary>
        /// Verifies every path in <paramref name="files"/>. Positional arguments only — options are split off
        /// by <see cref="ParseArgs"/> before this is called, so anything here IS meant to be a replay.
        /// </summary>
        public static int Run(string[] files, IKLogger logger)
        {
            if (files == null || files.Length == 0)
            {
                Console.Error.WriteLine("usage: --verify <replay.rply> [more.rply ...] [--log=<level>]");
                return ExitUsage;
            }

            // N files in one process. The saving is the process itself — a run costs ~0.6s of start-up before
            // it simulates anything, which is around 40% of a short match's verification. What it does NOT buy
            // is a mixed queue: the component layout freezes process-wide on the first file, so every file
            // here must share one (a different one comes back as `layoutFrozenDifferently`, loudly). Batch
            // callers should group by build/content revision — that grouping is forced, not advisory.
            //
            // The exit code is the WORST verdict seen, so a batch cannot bury one bad file among good ones.
            // Every file still prints its own line: the codes are ordered (0 < 10 < 20 < 30) but the reasons
            // are not, and a caller needs the reasons.
            int worst = ExitVerified;
            for (int i = 0; i < files.Length; i++)
            {
                var outcome = Verify(files[i], logger);
                Console.WriteLine(outcome.Line);
                if (outcome.Code > worst) worst = outcome.Code;
            }
            return worst;
        }

        internal static Outcome Verify(string path, IKLogger logger)
        {
            // ── load ───────────────────────────────────────────────────────
            // Every load failure funnels through ReplayLoadException: missing file, read error, wrong
            // magic, and — since the attribution fields landed — a version this build does not read.
            IReplayData replayData;
            try
            {
                var loader = new ReplaySystem(new CommandFactory(), logger);
                loader.LoadFromFile(path);
                replayData = loader.CurrentReplayData;
            }
            catch (Exception e)
            {
                return Line(ExitFileError, path, $"error={Sanitize(e.Message)}");
            }
            if (replayData == null)
                return Line(ExitFileError, path, "error=load produced no data");

            var meta = replayData.Metadata;

            // ── attribution ────────────────────────────────────────────────
            // An axis of its own, orthogonal to the verdict: "is this file honest" and "which match is it"
            // are different questions, and folding them into one exit code makes a consumer read the weaker
            // answer as the stronger one. Computed the moment the metadata is readable so that EVERY verdict
            // below carries it — a queue consumer sorting REJECTED files needs to know which match they
            // belong to just as much as it does for a verified one.
            string attribution = Attribution(meta.MatchConfigData, BrawlerMatchConfig.Decode(meta.MatchConfigData));

            // ── completeness ───────────────────────────────────────────────
            // A recording cut by a desync recovery is faithful only up to the cut, so its final result is
            // not the match's. Short is honest here, not suspicious — that distinction is the reason
            // EndReason exists at all.
            if (meta.EndReason != ReplayEndReason.Normal)
                return Line(ExitUnverifiable, path, $"endReason={meta.EndReason} (recording did not end normally)", attribution);

            // A snapshot taken mid-match (an SD client that joined late) leaves playback's clock and the
            // restored frame on different tick bases, so nothing derived from it can be trusted.
            if (meta.InitialStateTick != 0)
                return Line(ExitUnverifiable, path, $"initialStateTick={meta.InitialStateTick} (snapshot is not tick 0)", attribution);

            // The tick-0 hash is what the rebuilt world is judged against. Absence is unverifiable for the
            // same reason it is for the anchors and the claim: nothing to disagree with, and a
            // re-simulation always agrees with itself.
            if (meta.InitialStateHash == 0)
                return Line(ExitUnverifiable, path, "tick0hash=absent", attribution);

            // ── layout, before any simulation exists ───────────────────────
            // The component layout freezes process-wide, so it has to be built from the file's inputs
            // before anything constructs a simulation. ToSimulationConfig deliberately does NOT restore
            // these two — they are recorded so a VERIFIER can boot with them, and assigning them to a
            // config after the freeze would only look like a restore. This is that boot.
            var simConfig = meta.ToSimulationConfig();
            var pruned = new int[meta.PrunedComponentTypeIds.Count];
            for (int i = 0; i < pruned.Length; i++) pruned[i] = meta.PrunedComponentTypeIds[i];
            simConfig.SetRuntimePrunedComponentTypeIds(pruned);
            for (int i = 0; i < meta.ComponentMaxCountTypeIds.Count; i++)
                simConfig.ComponentMaxCountOverrides[meta.ComponentMaxCountTypeIds[i]] = meta.ComponentMaxCountValues[i];

            try
            {
                ComponentStorageRegistry.EnsureLayoutComputed(
                    simConfig.MaxEntities, simConfig.ComponentMaxCountOverrides, simConfig.PrunedComponentTypeIds);
            }
            catch (Exception e)
            {
                // The layout is already frozen with different inputs — this process has verified another
                // replay whose build differs. Loud, not silent, and a batch runner should group by layout.
                return Line(ExitUnverifiable, path, $"layoutFrozenDifferently={Sanitize(e.Message)}", attribution);
            }

            long localLayout = ComponentStorageRegistry.LayoutFingerprint;

            // Environment anchors that are all zero mean the file claims to know nothing about the world it
            // ran in. Treating that as "nothing to compare, therefore fine" would hand an attacker a
            // one-line bypass, so absence is unverifiable — the same rule the claim gets below.
            //
            // The THREE environment terms, deliberately not four. This used to include LayoutFingerprint in
            // the AND, which made the gate unreachable: the layout term is an FNV fold that cannot come out
            // zero and the recorder always fills it, so a file needed a value the format never produces.
            // Zeroing the three environment terms therefore walked straight past the bypass this gate
            // names. Layout is compared on its own line below, so it has no business in this condition.
            if (meta.StaticColliderFingerprint == 0 && meta.NavFingerprint == 0 && meta.GameFingerprint == 0)
                return Line(ExitUnverifiable, path, "anchors=absent", attribution);

            if (Differs(meta.LayoutFingerprint, localLayout))
                return Line(ExitUnverifiable, path,
                    $"anchors=mismatch:layout file=0x{meta.LayoutFingerprint:X16} local=0x{localLayout:X16}", attribution);

            // ── session ────────────────────────────────────────────────────
            var assets = BrawlerStageAssets.Load(logger);
            int stageId = simConfig.StageId;
            var matchCfg = BrawlerMatchConfig.Decode(simConfig.MatchConfigData);

            // Room capacity is a tick-0 state input for this game: bot ids are numbered past it
            // (maxPlayers + 1 + i) and their spawn slots follow from that id. It is NOT PlayerCount — a
            // 2-player match in a 4-slot room numbers its bots from 5, and rebuilding with 2 lands on a
            // different world whose entity and component COUNTS are identical, so the only symptom is a hash
            // that does not match. Substituting a guess is how an honest recording gets called a mismatch,
            // so absence is a verdict instead.
            //
            // Only when tick 0 will actually be rebuilt. A recording with no roster restores its snapshot,
            // and InitializeWorldState never runs on that path — demanding an input nothing reads would
            // refuse files this runner verified correctly before.
            if (meta.InitialRoster.Count > 0 && matchCfg.MaxPlayers <= 0)
                return Line(ExitUnverifiable, path,
                    "matchConfig=noMaxPlayers (the recorded match config does not carry the room capacity "
                    + "tick 0 was built with — re-record this replay)", attribution);

            KlothoSession session;
            try
            {
                var setup = new KlothoFlowSetupBuilder((sim, sess) => new SessionCallbacks(
                        new BrawlerServerCallbacks(
                            logger,
                            assets.CollidersFor(stageId),
                            assets.NavMeshFor(stageId),
                            maxPlayers: matchCfg.MaxPlayers,
                            botCount: matchCfg.BotCount,
                            dataAssets: assets.DataAssets,
                            stageId: stageId,
                            rebakeSnapshot: assets.RebakeSnapshotFor(stageId)),
                        null))
                    .WithLogger(logger)
                    // No WithAssetRegistry on purpose. BrawlerSimSetup.RegisterSystems casts the frame's
                    // registry back to IDataAssetRegistryBuilder and registers the assets it was handed, so
                    // it needs one that is still open. Passing a Build()-ed registry hands it a locked one
                    // and the game throws during RegisterSystems. The assets reach the simulation through
                    // the callbacks either way; the session builds and locks the registry at the end.
                    .Build();

                // Reconstruct, not restore: see the type doc. A file with no roster falls back on its own
                // (and says so), and any failure inside the rebuild lands in the catch below as a verdict
                // rather than escaping as a process failure.
                session = new KlothoSessionFlow(setup).StartReplay(
                    replayData, simConfig, KlothoEngine.ReplayInitialState.Reconstruct);
            }
            catch (Exception e)
            {
                return Line(ExitUnverifiable, path, $"sessionSetupFailed={Sanitize(e.Message)}", attribution);
            }

            // ── tick 0 ─────────────────────────────────────────────────────
            var engine = session.Engine;

            // Reported separately from the result on purpose. `tick0` is the PROVENANCE — a verified
            // `restored` means "these inputs produce this result", while a verified `reconstructed` also
            // means the starting point is this build's own. Folding the two into one field would let a
            // consumer read the weaker verdict as the stronger one.
            string tick0 = engine.ReplayTick0Reconstructed ? "reconstructed" : "restored";
            if (engine.ReplayTick0Hash != meta.InitialStateHash)
                return Line(ExitUnverifiable, path,
                    $"tick0={tick0} tick0hash=mismatch file=0x{meta.InitialStateHash:X16} local=0x{engine.ReplayTick0Hash:X16}", attribution);

            // ── run ────────────────────────────────────────────────────────
            float dt = simConfig.TickIntervalMs / 1000f;
            int budget = meta.TotalTicks + TickBudgetMargin;
            int updates = 0;

            // default = nothing compared yet, nothing mismatched. If playback never reaches tick 1 the
            // verdict inherits that and says `anchors=unchecked` rather than `ok`.
            EnvComparison env = default;
            bool envChecked = false;

            // The result is taken from the SAME source the claim was built from: the engine's match-end
            // event, which carries the GAME's event (winner AND reason) at the tick it fired.
            //
            // Polling ISimulation.IsMatchEndedState/GetActiveMatchEnd looks equivalent and is not. That pair
            // is the engine's resync BACKSTOP view, rebuilt from a component that stores only {Ended,
            // WinnerPlayerId} — the reason it hands back is a sentinel, and the tick is whenever the poll
            // happened to notice. Comparing that against a claim reports a mismatch on a match that agrees.
            int endTick = -1;
            int resultWinner = int.MinValue;
            string resultReason = null;
            engine.OnMatchEnded += (t, e) =>
            {
                if (endTick >= 0) return;   // first end wins; later fires are the backstop re-reporting
                endTick = t;
                // Copied on the spot: an IMatchEndEvent can be a reused payload.
                resultWinner = e?.WinnerPlayerId ?? -1;
                resultReason = e?.Reason.ToString() ?? string.Empty;
            };

            while (engine.State == KlothoState.Running && updates < budget)
            {
                session.Update(dt);
                updates++;

                // The environment anchors were taken at the recording's snapshot instant, so the
                // comparable moment here is right after the restored world has run its first tick —
                // that is when the game's systems have installed their geometry. Later is wrong: runtime
                // rebakes move the navigation term during a match, by design.
                if (!envChecked && engine.CurrentTick >= 1)
                {
                    envChecked = true;
                    env = CompareEnvironment(session.Simulation, meta);
                    if (env.Mismatched != null) break;
                }
            }

            if (env.Mismatched != null)
                return Line(ExitUnverifiable, path, $"anchors=mismatch:{env.Mismatched}", attribution);

            // How many of the three environment terms were actually compared. `ok` alone used to be
            // printed whether three were checked or none were: a term is only compared when BOTH sides
            // carry a value (see Differs), and a game that registers no IGameFingerprintSource reports 0
            // there legitimately. Reporting "ok" for a term nobody looked at is the same mistake this
            // runner refuses everywhere else — absence must be visible, not folded into a pass.
            string anchors = AnchorsField(env.ComparedCount, env.ComparedNames);

            if (engine.State == KlothoState.Running)
                return Line(ExitUnverifiable, path,
                    $"playbackUnfinished ticks={engine.CurrentTick}/{meta.TotalTicks} budget={budget}", attribution);

            // Taken from the finished world, and reported on every line that got this far — including the
            // claimless one, which has nothing else to compare. Differential by nature: two runs of one file
            // must print the same value, so a run whose inputs were altered prints a different one. That is
            // the divergence `result=` cannot show, because a diverged re-sim can still reach one winner.
            string endHash = $"endhash=0x{engine.CurrentStateHash:X16}";

            // ── claim ──────────────────────────────────────────────────────
            // No claim means there is nothing to disagree with, and a re-simulation always agrees with
            // itself. Reporting that as a pass would let an attacker strip the blob to verify anything.
            if (!BrawlerReplayClaim.TryDecode(meta.GameCustomData, out var claim))
                return Line(ExitUnverifiable, path,
                    $"claim=absent ticks={engine.CurrentTick}/{meta.TotalTicks} {anchors} "
                    + $"tick0={tick0} tick0hash=ok {endHash} "
                    + $"result={FormatResult(resultWinner, endTick, resultReason)}", attribution);

            string claimed = FormatResult(claim.WinnerPlayerId, claim.EndTick, claim.MatchEndReason.ToString());
            string derived = FormatResult(resultWinner, endTick, resultReason);
            bool agrees = claim.WinnerPlayerId == resultWinner
                       && claim.EndTick == endTick
                       && string.Equals(claim.MatchEndReason.ToString(), resultReason, StringComparison.Ordinal);

            string tail = $"ticks={engine.CurrentTick}/{meta.TotalTicks} endReason={meta.EndReason} {anchors} "
                        + $"tick0={tick0} tick0hash=ok {endHash} "
                        + $"claim={(agrees ? "ok" : "mismatch")} result={derived} claimed={claimed}";

            return agrees ? Line(ExitVerified, path, tail, attribution)
                          : Line(ExitClaimMismatch, path, tail, attribution);
        }

        /// <summary>The three environment terms a replay carries: colliders, nav, game.</summary>
        private const int EnvAnchorCount = 3;

        /// <summary>What an environment comparison actually managed to do.</summary>
        internal readonly struct EnvComparison
        {
            /// <summary>Names of the terms that disagreed, comma-joined. Null when none did.</summary>
            public readonly string Mismatched;
            /// <summary>Names of the terms that were actually COMPARED, comma-joined. Empty when none were.</summary>
            public readonly string ComparedNames;
            public readonly int ComparedCount;

            public EnvComparison(string mismatched, string comparedNames, int comparedCount)
            { Mismatched = mismatched; ComparedNames = comparedNames; ComparedCount = comparedCount; }
        }

        /// <summary>
        /// Compares the environment terms, and reports how many of them it could compare.
        ///
        /// <para>Names rather than a fold: the file records the terms separately so a mismatch can say WHICH
        /// source moved, which is the whole point of recording them.</para>
        ///
        /// <para><b>The count is not decoration.</b> A term is compared only when BOTH sides carry a value
        /// (see <see cref="Differs"/>) — a game that registers no <c>IGameFingerprintSource</c> reports 0
        /// there legitimately, and so does a build without colliders or nav. Without the count the verdict
        /// said <c>anchors=ok</c> whether three terms matched or none were looked at, which is the one thing
        /// this runner refuses to do anywhere else: absence is reported, never folded into a pass.</para>
        /// </summary>
        private static EnvComparison CompareEnvironment(ISimulation simulation, IReplayMetadata meta)
        {
            if (simulation is not EcsSimulation ecs) return default;

            long collider = ecs.GetSystem<IStaticColliderService>()?.GetStaticFingerprint() ?? 0;
            long nav = ecs.GetSystem<INavFingerprintSource>()?.GetNavFingerprint() ?? 0;
            long game = ecs.GetSystem<IGameFingerprintSource>()?.GetGameFingerprint() ?? 0;

            var mismatched = new StringBuilder();
            var compared = new StringBuilder();
            int comparedCount = 0;

            Term(meta.StaticColliderFingerprint, collider, "colliders");
            Term(meta.NavFingerprint, nav, "nav");
            Term(meta.GameFingerprint, game, "game");

            return new EnvComparison(
                mismatched.Length == 0 ? null : mismatched.ToString(),
                compared.ToString(),
                comparedCount);

            void Term(long fromFile, long local, string name)
            {
                if (fromFile == 0 || local == 0) return;   // not provided on one side — nothing to compare
                comparedCount++;
                Append(compared, name);
                if (fromFile != local) Append(mismatched, name);
            }

            static void Append(StringBuilder sb, string name)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(name);
            }
        }

        /// <summary>
        /// The verdict line's <c>anchors=</c> field. Three shapes, because a consumer has to tell them apart:
        /// <c>ok:3/3</c> (every term compared and agreed), <c>partial:n/3(names)</c> (only those terms had a
        /// value on both sides), <c>unchecked</c> (nothing was compared — playback never reached the tick
        /// where the environment exists). A mismatch never reaches here; it returns earlier.
        /// </summary>
        internal static string AnchorsField(int comparedCount, string comparedNames)
            => comparedCount <= 0
                ? "anchors=unchecked"
                : comparedCount >= EnvAnchorCount
                    ? $"anchors=ok:{comparedCount}/{EnvAnchorCount}"
                    : $"anchors=partial:{comparedCount}/{EnvAnchorCount}({comparedNames})";

        /// <summary>Both sides must have a value; 0 stays "not provided". Absence is handled separately
        /// (and treated as unverifiable) — this only decides whether two present values disagree.</summary>
        private static bool Differs(long a, long b) => a != 0 && b != 0 && a != b;

        private static string FormatResult(int winner, int endTick, string reason)
            => winner == int.MinValue && endTick < 0
                ? "none"
                : $"winner:{winner},tick:{endTick},reason:{(string.IsNullOrEmpty(reason) ? "-" : reason)}";

        /// <summary>
        /// One machine-readable verdict line. <paramref name="attribution"/> is threaded rather than folded
        /// into <paramref name="detail"/> so it lands in the same position on every line — including the
        /// early returns, where a queue consumer still needs to know which match a rejected file belongs to.
        /// It is null only before the match config has been decoded (file-level failures).
        /// </summary>
        private static Outcome Line(int code, string path, string detail, string attribution = null)
            => new Outcome(code, $"verify {Verdict(code)} code={code} file={path} "
                               + (attribution == null ? string.Empty : attribution + " ") + detail);

        private static string Verdict(int code) => code switch
        {
            ExitVerified => "verified",
            ExitClaimMismatch => "claim-mismatch",
            ExitUnverifiable => "unverifiable",
            ExitFileError => "file-error",
            _ => "failed",
        };

        /// <summary>
        /// Which match this recording belongs to, or why that cannot be said. Three answers, deliberately not
        /// one: operations has to treat them differently.
        /// <list type="bullet">
        /// <item><c>match=&lt;id&gt;</c> — attributed.</item>
        /// <item><c>match=unattributed</c> — the payload carries no identity. Normal, and it means the file
        /// cannot back a ranked result.</item>
        /// <item><c>match=legacy-payload</c> — the game payload predates the identity field, so it does not
        /// decode. Old, not suspicious: without this, every pre-existing replay lands in the bucket above.</item>
        /// </list>
        ///
        /// <para><b>There is deliberately no "an identity was expected and is missing" answer here.</b> That
        /// question needs to know the match was lobby-issued, and nothing in the file says so — a lobby's
        /// payload and a lobbyless server's have the same fields, and both stamp a capacity. Reading a
        /// stamped <c>MaxPlayers</c> as "an issuer was involved, so an id was owed" would put every
        /// lobbyless-server and P2P-host recording in the suspicious bucket, which is the mistake
        /// <c>legacy-payload</c> exists to prevent, pointed the other way. So this method reports whether an
        /// identity is PRESENT; whether one was OWED is ruled on by whoever holds the issue record — the
        /// submission service, which knows what it dispatched.</para>
        /// </summary>
        private static string Attribution(byte[] matchConfigData, BrawlerMatchConfigData decoded)
        {
            if (matchConfigData == null || matchConfigData.Length == 0)
                return "match=unattributed";          // nothing was propagated at all: lobbyless host or solo

            // Short of the current layout = an older one. The LENGTH is the signal, not the decoded values.
            // Encode is fixed-width, so a current payload is EncodedSize bytes whatever it carries, and an
            // older blob is shorter (the pre-MaxPlayers one is exactly 4).
            //
            // Reading "decodes to all zero" as the signal instead misfiled a legitimate CURRENT payload:
            // no bots, capacity not stamped, no identity — each a normal value on its own, and together they
            // decode to exactly what an older blob decodes to, because Decode is lenient. That put the file
            // in the "old, re-record it" bucket, where re-recording changes nothing: what needs fixing is
            // the issuer, and the word erased that.
            if (matchConfigData.Length < BrawlerMatchConfig.EncodedSize)
                return "match=legacy-payload";

            string id = decoded.MatchInstanceId.ToString();
            if (string.IsNullOrEmpty(id))
                return "match=unattributed";          // a real config carrying no identity: normally a lobbyless
                                                      // server, a P2P host or a solo session authoring its own.
                                                      // "issued but absent" would land here too and is not
                                                      // separable from the file — see the summary above.

            return $"match={Sanitize(id)}";
        }

        /// <summary>Keeps an exception message on one line so the verdict stays machine-readable.</summary>
        private static string Sanitize(string s)
            => string.IsNullOrEmpty(s) ? "?" : s.Replace('\n', ' ').Replace('\r', ' ');
    }
}
