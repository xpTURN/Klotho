#!/usr/bin/env bash
#
# run-all-tests.sh — Klotho integrated test runner
#
# Runs the .NET unit tests (dotnet test) plus the Unity Test Runner suites
# (EditMode, via command line) from a single entry point and prints a final
# console summary report. Two Unity projects are covered: Brawler on the primary
# editor, and Unity2022.Tests on the 2022.3 LTS line the package declares as its
# minimum (a separate editor install).
#
# Usage:
#   Tools/run-all-tests.sh                  # run everything (.NET + both Unity projects)
#   Tools/run-all-tests.sh --dotnet-only    # .NET tests only
#   Tools/run-all-tests.sh --unity-only     # Unity EditMode tests only
#   Tools/run-all-tests.sh --no-unity-2022  # skip the 2022.3 LTS project (editor not installed)
#   Tools/run-all-tests.sh --no-build       # pass --no-build to dotnet test
#   Tools/run-all-tests.sh -h | --help
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
DOTNET_NO_BUILD=0

usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "${BASH_SOURCE[0]}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dotnet-only)   RUN_UNITY=0 ;;
    --unity-only)    RUN_DOTNET=0 ;;
    --no-unity-2022) RUN_UNITY_2022=0 ;;
    --no-build)      DOTNET_NO_BUILD=1 ;;
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
  section ".NET tests (dotnet test)"
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "${C_RED}dotnet not found. The .NET SDK must be installed.${C_RST}" >&2
    return 1
  fi

  local build_flag=()
  [[ "${DOTNET_NO_BUILD}" -eq 1 ]] && build_flag=(--no-build)

  local proj name trx rc
  local overall=0
  for proj in "${DOTNET_TEST_PROJECTS[@]}"; do
    name="$(basename "$(dirname "${proj}")")"
    trx="dotnet-${name}.trx"
    echo "${C_BLD}▶ ${name}${C_RST}"
    dotnet test "${REPO_ROOT}/${proj}" \
      ${build_flag[@]+"${build_flag[@]}"} \
      --nologo \
      --results-directory "${RESULTS_DIR}" \
      --logger "trx;LogFileName=${trx}"
    rc=$?
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
  RESULTS_DIR="${RESULTS_DIR}" python3 - <<'PY'
import glob, os, sys
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

if not rows:
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
if fai == 0 and not parse_errors:
    print(f"{GRN}{BLD}✔ All passed ({pas}/{tot}){RST}")
    sys.exit(0)
else:
    print(f"{RED}{BLD}✗ {fai} failed / {tot} total{RST}")
    sys.exit(1)
PY
}

# ── Run ──────────────────────────────────────────────────────────────────────
DOTNET_RC=0
UNITY_RC=0

[[ "${RUN_DOTNET}" -eq 1 ]] && { run_dotnet_tests; DOTNET_RC=$?; }
[[ "${RUN_UNITY}"  -eq 1 ]] && { run_unity_tests;  UNITY_RC=$?; }

print_summary
SUMMARY_RC=$?

echo
echo "Result files: ${RESULTS_DIR}"

# Exit code: non-zero if any run or the summary failed
if [[ ${DOTNET_RC} -ne 0 || ${UNITY_RC} -ne 0 || ${SUMMARY_RC} -ne 0 ]]; then
  exit 1
fi
exit 0
