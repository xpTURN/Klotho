#!/usr/bin/env bash
#
# check-deprecated-defines.sh — fail if a scripting symbol Unity has deprecated comes back.
#
# Unity 6000.6 warns on DEVELOPMENT_BUILD (UAC0009) and UNITY_64 (UAC0008); Unity 6.8 turns both into
# hard compile errors and stops defining them. The analyzer reads preprocessor directives even inside
# inactive #else regions, so a version-gated #if does not hide one — the token has to be gone. CI never
# runs Unity, and Unity's warning does not fail the local test runner, so this is the check that fails
# when one comes back.
#
# Scans:
#   - tracked C# sources: #if / #elif / #define / #undef directives, and [Conditional("…")] outside
#     comments;
#   - the committed DLLs of the Godot addon (dist/ and the Godot samples). Their metadata carries the
#     [Conditional] strings, and check-dist-sync.sh does not compare DLLs, so nothing else would notice
#     a stale one.
#
# The patterns avoid \b: `git grep -E` has been seen to match nothing with it, without an error, which
# would turn this check green without looking. Against that whole class of failure the script first runs
# its own pipeline over built-in samples and refuses to scan if they do not behave.
#
# Usage: .github/scripts/check-deprecated-defines.sh [<rev>]    (default: the working tree)
# Exit code: non-zero if anything is found, or if the self-test fails.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REV="${1:-}"

SYMS='DEVELOPMENT_BUILD|UNITY_64'
NW='[^A-Za-z0-9_]'
DIRECTIVE_RE="^[[:space:]]*#[[:space:]]*(if|elif|define|undef)([[:space:](!]|[[:space:](!].*${NW})(${SYMS})(${NW}|\$)"
CONDITIONAL_RE="Conditional(Attribute)?[[:space:]]*\\([[:space:]]*\"(${SYMS})\"[[:space:]]*\\)"

DLL_PATHS=('dist/addons/klotho/lib/*.dll' 'Samples/Godot*/addons/klotho/lib/*.dll')

# scan_sources <prefix-fields> <git grep args…> — prints offending lines; returns 2 on a git error.
# <prefix-fields> is how many "name:" fields precede the line text: 2 for path:line, 3 for rev:path:line.
scan_sources() {
  local fields="$1"; shift
  local out rc
  out="$(git grep -n -E -e "${DIRECTIVE_RE}" -e "${CONDITIONAL_RE}" "$@")"; rc=$?
  [[ ${rc} -le 1 ]] || return 2
  [[ -z "${out}" ]] && return 0
  # Drop comment lines: a doc comment quoting an old [Conditional] is prose, not a symbol use.
  printf '%s\n' "${out}" | awk -v n="${fields}" '{
    c = $0
    for (i = 0; i < n; i++) sub(/^[^:]*:/, "", c)
    if (c ~ /^[ \t]*(\/\/|\/\*|\*)/) next
    print
  }'
}

# scan_dlls <git grep args…> — prints DLLs containing a deprecated symbol; returns 2 on a git error.
scan_dlls() {
  local out rc
  out="$(git grep -l -e DEVELOPMENT_BUILD -e UNITY_64 "$@")"; rc=$?
  [[ ${rc} -le 1 ]] || return 2
  [[ -n "${out}" ]] && printf '%s\n' "${out}"
  return 0
}

self_test() {
  local dir; dir="$(mktemp -d)"
  local bad=(
    '#if DEVELOPMENT_BUILD'
    '#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR'
    '    #elif !DEVELOPMENT_BUILD'
    '#if (UNITY_64 && DEBUG)'
    '#define DEVELOPMENT_BUILD'
    '        [Conditional("DEVELOPMENT_BUILD")]'
    '[System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("UNITY_64")]'
  )
  local good=(
    '#if DEBUG || UNITY_EDITOR'
    '#if MY_DEVELOPMENT_BUILD_FLAG'
    '#endif // DEVELOPMENT_BUILD'
    '// [Conditional("DEVELOPMENT_BUILD")] quoted in a comment'
    '    /// <c>[Conditional("DEVELOPMENT_BUILD")]</c>'
    'const string S = "DEVELOPMENT_BUILD";'
  )
  printf '%s\n' "${bad[@]}" > "${dir}/bad.cs"
  printf '%s\n' "${good[@]}" > "${dir}/good.cs"
  printf 'MZ\0\0\0Conditional\0DEVELOPMENT_BUILD\0' > "${dir}/bad.dll"
  printf 'MZ\0\0\0Conditional\0DEBUG\0' > "${dir}/good.dll"

  local bad_hits good_hits dll_hits failed=0
  bad_hits="$(cd "${dir}" && scan_sources 2 --no-index -- bad.cs | grep -c .)"
  good_hits="$(cd "${dir}" && scan_sources 2 --no-index -- good.cs | grep -c .)"
  dll_hits="$(cd "${dir}" && scan_dlls --no-index -- '*.dll')"
  rm -rf "${dir}"

  if [[ "${bad_hits}" != "${#bad[@]}" ]]; then
    echo "self-test: the source patterns caught ${bad_hits} of ${#bad[@]} known-bad lines." >&2; failed=1
  fi
  if [[ "${good_hits}" != "0" ]]; then
    echo "self-test: the source patterns flagged ${good_hits} known-good lines." >&2; failed=1
  fi
  if [[ "${dll_hits}" != "bad.dll" ]]; then
    echo "self-test: the DLL scan returned '${dll_hits}' instead of 'bad.dll'." >&2; failed=1
  fi
  return ${failed}
}

cd "${REPO_ROOT}" || exit 1

if ! self_test; then
  echo "The patterns do not behave on this platform's git grep; refusing to report a clean tree." >&2
  exit 1
fi

if [[ -n "${REV}" ]]; then
  sources="$(scan_sources 3 "${REV}" -- '*.cs')" || { echo "git grep failed on ${REV}." >&2; exit 1; }
  dlls="$(scan_dlls "${REV}" -- "${DLL_PATHS[@]}")" || { echo "git grep failed on ${REV}." >&2; exit 1; }
  where="${REV}"
else
  sources="$(scan_sources 2 -- '*.cs')" || { echo "git grep failed." >&2; exit 1; }
  dlls="$(scan_dlls -- "${DLL_PATHS[@]}")" || { echo "git grep failed." >&2; exit 1; }
  where="the working tree"
fi

n_sources=0; [[ -n "${sources}" ]] && n_sources="$(printf '%s\n' "${sources}" | grep -c .)"
n_dlls=0;    [[ -n "${dlls}" ]]    && n_dlls="$(printf '%s\n' "${dlls}" | grep -c .)"

if [[ ${n_sources} -eq 0 && ${n_dlls} -eq 0 ]]; then
  echo "No DEVELOPMENT_BUILD / UNITY_64 in ${where}."
  exit 0
fi

if [[ ${n_sources} -gt 0 ]]; then
  echo "${n_sources} C# directive/attribute line(s) in ${where} use a symbol Unity has deprecated:" >&2
  printf '%s\n' "${sources}" >&2
fi
if [[ ${n_dlls} -gt 0 ]]; then
  echo "${n_dlls} committed DLL(s) in ${where} still carry one (rebuild with Tools/deploy-addon-to-samples.sh):" >&2
  printf '%s\n' "${dlls}" >&2
fi
cat >&2 <<'MSG'

Unity 6000.6 warns on these (UAC0009 / UAC0008) and Unity 6.8 makes them compile errors, even
inside an inactive #else. Every development build also defines DEBUG, so guard with DEBUG instead.
The 0.14.1 entry in CHANGELOG.md explains what changes outside Unity.
MSG
exit 1
