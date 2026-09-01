#!/usr/bin/env bash
#
# run-all-tests.sh — Klotho integrated test runner
#
# Runs the .NET unit tests (dotnet test), the Brawler dedicated server's own
# self-hosted suite (`--test`), and the Unity Test Runner suites (EditMode, via
# command line) from a single entry point, then prints a final console summary
# report. Two Unity projects are covered: Brawler on the primary editor, and
# Unity2022.Tests on the 2022.3 LTS line the package declares as its minimum (a
# separate editor install).
#
# The server suite is not a `dotnet test` project — it is a mini-framework inside
# the server binary (rooms, transports and threads that NUnit's host does not
# give it), so it is run as `dotnet run -- --test` and its console output is
# parsed. It is the only place multi-room lifecycle, the replay verifier and the
# game-side join seeding are covered.
#
# Usage:
#   Tools/run-all-tests.sh                  # run everything (.NET + both Unity projects)
#   Tools/run-all-tests.sh --dotnet-only    # .NET tests only
#   Tools/run-all-tests.sh --unity-only     # Unity EditMode tests only
#   Tools/run-all-tests.sh --no-unity-2022  # skip the 2022.3 LTS project (editor not installed)
#   Tools/run-all-tests.sh --no-server-test # skip the Brawler server's own --test suite
#   Tools/run-all-tests.sh --no-build       # pass --no-build to dotnet test (every configuration
#                                           # below must already be built — checked up front)
#   Tools/run-all-tests.sh --debug          # dotnet test with the Debug configuration only
#   Tools/run-all-tests.sh --release        # dotnet test with the Release configuration only
#   Tools/run-all-tests.sh --both-configs   # Debug and Release, in that order — this is the default
#   Tools/run-all-tests.sh -c <cfg>         # dotnet test with an arbitrary configuration name
#   Tools/run-all-tests.sh -h | --help
#
# The configuration options apply to the .NET tests only; Unity EditMode runs are
# unaffected (the editor compiles the project with its own settings).
#
# Environment variables:
#   UNITY_PATH        Override the Unity executable path (default: Hub 6000.3.9f1)
#   UNITY_2022_PATH   Override the 2022.3 LTS editor path (default: Hub 2022.3.62f3)
#
# Exit code: non-zero if anything fails (CI friendly).

set -uo pipefail

# ── Path resolution (derive repo root from the script's own location) ────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Intermediate artifacts go under the gitignored Logs/ directory.
RESULTS_DIR="${REPO_ROOT}/Logs/test-results"

# Unity project & version
UNITY_VERSION="6000.3.9f1"
UNITY_PROJECT="${REPO_ROOT}/Samples/Brawler"
UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity}"

# Unity 2022.3 LTS compatibility project. Runs on its own editor install by design —
# the suite exists to validate the oldest editor the package supports (package.json
# declares "unity": "2022.3"), so it cannot share the primary editor above.
UNITY_2022_VERSION="2022.3.62f3"
UNITY_2022_PROJECT="${REPO_ROOT}/Samples/Unity2022.Tests"
UNITY_2022_PATH="${UNITY_2022_PATH:-/Applications/Unity/Hub/Editor/${UNITY_2022_VERSION}/Unity.app/Contents/MacOS/Unity}"

# The Brawler dedicated server's self-hosted suite. Runs through `dotnet run`, not `dotnet test`:
# its cases spin up rooms, transports and threads, so they live in the binary rather than in a test
# host. Same configurations as the dotnet tests below, for the same reason (dev-only guards).
SERVER_TEST_PROJECT="Samples/Brawler/Server"

# .NET test projects
DOTNET_TEST_PROJECTS=(
  "Tools/KlothoGenerator.Tests/KlothoGenerator.Tests.csproj"
  "Samples/Klotho.Runtime.Tests/Klotho.Runtime.Tests.csproj"
  "Samples/DevLobbyServer.Tests/DevLobbyServer.Tests.csproj"
)

# ── Option parsing ───────────────────────────────────────────────────────────
RUN_DOTNET=1
RUN_UNITY=1
RUN_UNITY_2022=1
RUN_SERVER_TEST=1
DOTNET_NO_BUILD=0
# Configurations passed to dotnet test, in run order. Debug alone matches the
# dotnet default, so the unqualified invocation behaves as it always has.
# Both by default. A dev-only guard (#if DEBUG / DEVELOPMENT_BUILD / UNITY_EDITOR) compiles out of a
# Release build, so a Debug-only default silently skips every test that covers one — which is exactly
# how an engine warning added under such a guard shipped "passing" while Release failed on it.
# Several guards already exist (Filter loop-mutation watch, rebake pool overlap, EC diagnostics), so the
# gap recurs. The cost is roughly double the dotnet time; --debug restores the old behaviour.
DOTNET_CONFIGS=("Debug" "Release")

usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "${BASH_SOURCE[0]}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dotnet-only)   RUN_UNITY=0 ;;
    --unity-only)    RUN_DOTNET=0 ;;
    --no-unity-2022) RUN_UNITY_2022=0 ;;
    --no-server-test) RUN_SERVER_TEST=0 ;;
    --no-build)      DOTNET_NO_BUILD=1 ;;
    --debug)         DOTNET_CONFIGS=("Debug") ;;
    --release)       DOTNET_CONFIGS=("Release") ;;
    --both-configs)  DOTNET_CONFIGS=("Debug" "Release") ;;
    -c|--configuration)
      [[ $# -ge 2 ]] || { echo "$1 requires a configuration name" >&2; exit 2; }
      DOTNET_CONFIGS=("$2"); shift ;;
    -h|--help)       usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
  shift
done

# ── Colors (only when writing to a tty) ──────────────────────────────────────
if [[ -t 1 ]]; then
  C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YEL=$'\033[33m'; C_CYN=$'\033[36m'; C_BLD=$'\033[1m'; C_RST=$'\033[0m'
else
  C_RED=""; C_GRN=""; C_YEL=""; C_CYN=""; C_BLD=""; C_RST=""
fi

section() { echo; echo "${C_BLD}${C_CYN}=== $* ===${C_RST}"; }

mkdir -p "${RESULTS_DIR}"
# Clean up results from the previous run. Logs are included so a renamed/removed suite
# cannot leave an orphaned editor log behind (they grow to hundreds of MB).
rm -f "${RESULTS_DIR}"/*.trx "${RESULTS_DIR}"/*.xml "${RESULTS_DIR}"/*.log 2>/dev/null || true

# ── .NET tests ───────────────────────────────────────────────────────────────
run_dotnet_tests() {
  section ".NET tests (dotnet test) — configuration: ${DOTNET_CONFIGS[*]}"
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "${C_RED}dotnet not found. The .NET SDK must be installed.${C_RST}" >&2
    return 1
  fi

  local build_flag=()
  if [[ "${DOTNET_NO_BUILD}" -eq 1 ]]; then
    build_flag=(--no-build)
    # --no-build only says "do not rebuild"; it does not say which configurations exist. Since the
    # default set is Debug AND Release, a tree where only one of them was ever built fails per
    # project with `dotnet test`'s "The argument <dll> is invalid" — which never mentions the build —
    # and then again as a missing suite in the summary. Name the configuration instead. Failing is
    # correct here: classifying an unbuilt configuration as "skipped" would blunt the missing-suite
    # gate, whose whole job is to notice a suite that produced no result.
    local missing=() _cfg _proj
    for _cfg in "${DOTNET_CONFIGS[@]}"; do
      for _proj in "${DOTNET_TEST_PROJECTS[@]}"; do
        [[ -d "${REPO_ROOT}/$(dirname "${_proj}")/bin/${_cfg}" ]] \
          || missing+=("${_cfg}: $(dirname "${_proj}")")
      done
      # The server suite shares the flag, so it shares the check — an unbuilt configuration there
      # would otherwise surface as a `dotnet run` build error in the middle of the run.
      [[ "${RUN_SERVER_TEST}" -eq 1 && ! -d "${REPO_ROOT}/${SERVER_TEST_PROJECT}/bin/${_cfg}" ]] \
        && missing+=("${_cfg}: ${SERVER_TEST_PROJECT}")
    done
    if [[ ${#missing[@]} -gt 0 ]]; then
      echo "${C_RED}--no-build was given but these have never been built:${C_RST}" >&2
      printf '  %s\n' "${missing[@]}" >&2
      echo "Run once without --no-build, or narrow the configuration with --debug / --release / -c <cfg>." >&2
      exit 2
    fi
  fi

  local config proj name trx rc
  local overall=0
  for config in "${DOTNET_CONFIGS[@]}"; do
    for proj in "${DOTNET_TEST_PROJECTS[@]}"; do
      name="$(basename "$(dirname "${proj}")")"
      # The configuration is part of the trx name so a --both-configs run keeps both
      # results (the summary parser derives the suite label from this file name).
      trx="dotnet-${name}-${config}.trx"
      echo "${C_BLD}▶ ${name} [${config}]${C_RST}"
      dotnet test "${REPO_ROOT}/${proj}" \
        --configuration "${config}" \
        ${build_flag[@]+"${build_flag[@]}"} \
        --nologo \
        --results-directory "${RESULTS_DIR}" \
        --logger "trx;LogFileName=${trx}"
      rc=$?
      [[ ${rc} -ne 0 ]] && overall=1
    done
  done
  return ${overall}
}

# ── Brawler server self-hosted suite (dotnet run -- --test) ──────────────────
# Writes one log per configuration; the summary parses the console output because this suite has no
# machine-readable result format. Its exit code is the failure count, so it is authoritative even if
# the parse comes up empty (a crash mid-suite prints no final line).
run_server_tests() {
  section "Brawler server suite (dotnet run -- --test) — configuration: ${DOTNET_CONFIGS[*]}"

  local build_flag=()
  [[ "${DOTNET_NO_BUILD}" -eq 1 ]] && build_flag=(--no-build)

  local config log rc
  local overall=0
  for config in "${DOTNET_CONFIGS[@]}"; do
    log="${RESULTS_DIR}/servertest-brawler-${config}.log"
    echo "${C_BLD}▶ Brawler server --test [${config}]${C_RST}  (log: ${log})"
    # `-v q` and `-c` belong to `dotnet run`; everything after `--` is the app's argv. Order matters:
    # a flag `dotnet run` does not recognise is forwarded, and this app reads argv[0] as a port —
    # `--nologo` lands there and dies in int.Parse.
    dotnet run --project "${REPO_ROOT}/${SERVER_TEST_PROJECT}" \
      --configuration "${config}" \
      ${build_flag[@]+"${build_flag[@]}"} \
      -v q \
      -- --test 2>&1 | tee "${log}"
    rc=${PIPESTATUS[0]}
    [[ ${rc} -ne 0 ]] && overall=1
  done
  return ${overall}
}

# ── Unity EditMode tests ─────────────────────────────────────────────────────
# run_unity_editmode <label> <editor-exe> <project-dir> <slug> <path-env-var>
# The slug names both artifacts (unity-<slug>.xml / .log); the .xml name is what the
# summary parser globs, so it must stay unique per project.
run_unity_editmode() {
  local label="$1" editor="$2" project="$3" slug="$4" path_var="$5"

  if [[ ! -x "${editor}" ]]; then
    echo "${C_RED}Unity executable not found:${C_RST} ${editor}" >&2
    echo "  Set the ${path_var} environment variable to point at it." >&2
    return 1
  fi

  local results_xml="${RESULTS_DIR}/unity-${slug}.xml"
  local unity_log="${RESULTS_DIR}/unity-${slug}.log"

  echo "${C_BLD}▶ ${label}${C_RST}  (log: ${unity_log})"
  # -runTests handles batch mode and exit automatically (do not combine with -quit).
  # Unity returns an exit code based on the test result (0=pass, 2=test failure, etc.).
  "${editor}" \
    -runTests \
    -batchmode \
    -nographics \
    -projectPath "${project}" \
    -testPlatform EditMode \
    -testResults "${results_xml}" \
    -logFile "${unity_log}" \
    -buildTarget StandaloneOSX
  local rc=$?

  if [[ ! -f "${results_xml}" ]]; then
    echo "${C_RED}Unity result file was not produced. Check the log:${C_RST} ${unity_log}" >&2
    # Print the last log lines to help diagnose license/compile errors
    tail -n 20 "${unity_log}" 2>/dev/null
    return 1
  fi
  return ${rc}
}

run_unity_tests() {
  section "Unity Test Runner (EditMode, batch mode)"
  local overall=0

  run_unity_editmode "Brawler EditMode (${UNITY_VERSION})" \
    "${UNITY_PATH}" "${UNITY_PROJECT}" "brawler-editmode" "UNITY_PATH" || overall=1

  if [[ "${RUN_UNITY_2022}" -eq 1 ]]; then
    run_unity_editmode "Unity2022.Tests EditMode (${UNITY_2022_VERSION})" \
      "${UNITY_2022_PATH}" "${UNITY_2022_PROJECT}" "2022lts-editmode" "UNITY_2022_PATH" || overall=1
  else
    echo "${C_YEL}▶ Unity2022.Tests EditMode — skipped (--no-unity-2022)${C_RST}"
  fi

  return ${overall}
}

# ── Final summary report (parses TRX + Unity NUnit XML) ──────────────────────
print_summary() {
  section "Final report"
  # Which suites this invocation was supposed to produce. Without it the summary cannot tell
  # "the suite passed" from "the suite left no file": a missing result is simply an absent row, and
  # `--dotnet-only` makes absent Unity rows legitimate, so counting rows is not enough. The labels
  # below have to match the ones the parser derives from file names.
  local expected=()
  if [[ "${RUN_DOTNET}" -eq 1 ]]; then
    local _cfg _proj _name
    for _cfg in "${DOTNET_CONFIGS[@]}"; do
      for _proj in "${DOTNET_TEST_PROJECTS[@]}"; do
        _name="$(basename "$(dirname "${_proj}")")"
        expected+=("${_name}-${_cfg}")
      done
    done
  fi
  if [[ "${RUN_SERVER_TEST}" -eq 1 && "${RUN_DOTNET}" -eq 1 ]]; then
    local _scfg
    for _scfg in "${DOTNET_CONFIGS[@]}"; do
      expected+=("Server: brawler-${_scfg}")
    done
  fi
  if [[ "${RUN_UNITY}" -eq 1 ]]; then
    expected+=("Unity: brawler-editmode")
    [[ "${RUN_UNITY_2022}" -eq 1 ]] && expected+=("Unity: 2022lts-editmode")
  fi

  EXPECTED_SUITES="$(printf '%s\n' ${expected[@]+"${expected[@]}"})" \
  RESULTS_DIR="${RESULTS_DIR}" python3 - <<'PY'
import glob, os, re, sys
import xml.etree.ElementTree as ET

results_dir = os.environ["RESULTS_DIR"]

# Colors (only when stdout is a tty)
tty = sys.stdout.isatty()
RED = "\033[31m" if tty else ""
GRN = "\033[32m" if tty else ""
YEL = "\033[33m" if tty else ""
BLD = "\033[1m"  if tty else ""
RST = "\033[0m"  if tty else ""

rows = []          # (suite, total, passed, failed, skipped)
failures = []      # (suite, test_fullname)

# Suites the runner said it would produce. Empty when nothing was asked for.
expected = [x.strip() for x in os.environ.get("EXPECTED_SUITES", "").split("\n") if x.strip()]

def local(tag):
    return tag.rsplit('}', 1)[-1]

# --- TRX (dotnet test) ---
for path in sorted(glob.glob(os.path.join(results_dir, "dotnet-*.trx"))):
    suite = os.path.basename(path)[len("dotnet-"):-len(".trx")]
    try:
        root = ET.parse(path).getroot()
    except Exception as e:
        rows.append((suite, 0, 0, 0, 0)); failures.append((suite, f"<parse failed: {e}>")); continue
    total = passed = failed = 0
    # ResultSummary/Counters attributes
    for el in root.iter():
        if local(el.tag) == "Counters":
            total  = int(el.get("total", 0))
            passed = int(el.get("passed", 0))
            failed = int(el.get("failed", 0)) + int(el.get("error", 0))
            break
    skipped = max(total - passed - failed, 0)
    rows.append((suite, total, passed, failed, skipped))
    # Failed test names
    for el in root.iter():
        if local(el.tag) == "UnitTestResult" and el.get("outcome") not in ("Passed", "NotExecuted", None):
            if el.get("outcome") == "Failed":
                failures.append((suite, el.get("testName", "?")))

# --- Brawler server suite (console log) ---
# The suite prints one "=== <label> results: N passed, M failed ===" line per sub-suite (one of them
# has no label: "=== Results: ..."), and one "  FAIL: <name>" line per failed case. Counts are summed
# into a single row per configuration: sub-suites get added and renamed over time, and an expected-row
# list that enumerated them would break every time one does — which is how this suite came to be
# missing from the runner in the first place.
for path in sorted(glob.glob(os.path.join(results_dir, "servertest-*.log"))):
    suite = "Server: " + os.path.basename(path)[len("servertest-"):-len(".log")]
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except Exception as e:
        rows.append((suite, 0, 0, 0, 0)); failures.append((suite, f"<parse failed: {e}>")); continue
    counts = re.findall(r"^=== .*?[Rr]esults: (\d+) passed, (\d+) failed ===", text, re.M)
    if not counts:
        # No final line at all: the run died before any sub-suite finished. Reported as a parse
        # error so the summary fails even though the table would otherwise show a clean 0/0.
        rows.append((suite, 0, 0, 0, 0))
        failures.append((suite, "<parse failed: no result lines — suite crashed or produced no output>"))
        continue
    passed = sum(int(p) for p, _ in counts)
    failed = sum(int(f) for _, f in counts)
    rows.append((suite, passed + failed, passed, failed, 0))
    for name in re.findall(r"^  FAIL: (.+)$", text, re.M):
        failures.append((suite, name.strip()))

# --- Unity NUnit3 XML ---
for path in sorted(glob.glob(os.path.join(results_dir, "unity-*.xml"))):
    suite = "Unity: " + os.path.basename(path)[len("unity-"):-len(".xml")]
    try:
        root = ET.parse(path).getroot()   # <test-run ...>
    except Exception as e:
        rows.append((suite, 0, 0, 0, 0)); failures.append((suite, f"<parse failed: {e}>")); continue
    total   = int(root.get("total", root.get("testcasecount", 0)))
    passed  = int(root.get("passed", 0))
    failed  = int(root.get("failed", 0))
    skipped = int(root.get("skipped", 0)) + int(root.get("inconclusive", 0))
    rows.append((suite, total, passed, failed, skipped))
    for tc in root.iter("test-case"):
        if tc.get("result") == "Failed":
            failures.append((suite, tc.get("fullname", tc.get("name", "?"))))

missing = []   # filled after the table is built

if not rows:
    # Exiting 0 here would call a run that produced nothing a success.
    if expected:
        print(f"{RED}{BLD}No test results were collected, but {len(expected)} suite(s) were expected:{RST}")
        for name in expected:
            print(f"  {RED}✗{RST} {name}")
        sys.exit(1)
    print(f"{YEL}No test results were collected.{RST}")
    sys.exit(0)

# Print the table
name_w = max(len(r[0]) for r in rows + [("SUITE", 0, 0, 0, 0)])
def fmt(s, t, p, f, k):
    return f"  {s:<{name_w}}  {t:>6}  {p:>6}  {f:>6}  {k:>7}"
print(f"{BLD}{fmt('SUITE', 'TOTAL', 'PASS', 'FAIL', 'SKIP')}{RST}")
print("  " + "-" * (name_w + 31))
tot = pas = fai = ski = 0
for s, t, p, f, k in rows:
    tot += t; pas += p; fai += f; ski += k
    color = RED if f > 0 else GRN
    print(f"{color}{fmt(s, t, p, f, k)}{RST}")
print("  " + "-" * (name_w + 31))
print(f"{BLD}{fmt('TOTAL', tot, pas, fai, ski)}{RST}")

# Suites that were expected but left no result file. A compile error, a licence failure or a hung
# editor all land here, and none of them produce a row of their own.
found = {r[0] for r in rows}
missing = [name for name in expected if name not in found]
if missing:
    print()
    print(f"{RED}{BLD}Missing suites ({len(missing)}) — expected but produced no result file:{RST}")
    for name in missing:
        print(f"  {RED}✗{RST} {name}")

# Failure list
real_failures = [x for x in failures if not x[1].startswith("<parse")]
parse_errors  = [x for x in failures if x[1].startswith("<parse")]
if real_failures:
    print()
    print(f"{RED}{BLD}Failed tests ({len(real_failures)}):{RST}")
    for suite, name in real_failures:
        print(f"  {RED}✗{RST} [{suite}] {name}")
if parse_errors:
    print()
    print(f"{YEL}Result parse warnings:{RST}")
    for suite, msg in parse_errors:
        print(f"  {YEL}![{suite}] {msg}{RST}")

print()
if fai == 0 and not parse_errors and not missing:
    print(f"{GRN}{BLD}✔ All passed ({pas}/{tot}){RST}")
    sys.exit(0)
else:
    suffix = f", {len(missing)} suite(s) missing" if missing else ""
    print(f"{RED}{BLD}✗ {fai} failed / {tot} total{suffix}{RST}")
    sys.exit(1)
PY
}

# ── Run ──────────────────────────────────────────────────────────────────────
DOTNET_RC=0
SERVER_RC=0
UNITY_RC=0

[[ "${RUN_DOTNET}" -eq 1 ]] && { run_dotnet_tests; DOTNET_RC=$?; }
[[ "${RUN_DOTNET}" -eq 1 && "${RUN_SERVER_TEST}" -eq 1 ]] && { run_server_tests; SERVER_RC=$?; }
[[ "${RUN_UNITY}"  -eq 1 ]] && { run_unity_tests;  UNITY_RC=$?; }

print_summary
SUMMARY_RC=$?

echo
echo "Result files: ${RESULTS_DIR}"

# Exit code: non-zero if any run or the summary failed
if [[ ${DOTNET_RC} -ne 0 || ${SERVER_RC} -ne 0 || ${UNITY_RC} -ne 0 || ${SUMMARY_RC} -ne 0 ]]; then
  exit 1
fi
exit 0
