#!/usr/bin/env bash
#
# check-versions.sh — verify the package version is consistent across every manifest.
#
# Sources of truth compared here:
#   com.xpturn.klotho/package.json            (UPM package version)
#   com.xpturn.klotho/Godot~/plugin.cfg       (canonical Godot addon)
#   dist/addons/klotho/plugin.cfg             (packed addon shipped by release.yml)
#   Samples/Godot*/addons/klotho/plugin.cfg   (addon copies deployed into the samples)
#
# Usage:
#   .github/scripts/check-versions.sh              # manifests only
#   .github/scripts/check-versions.sh --tag v0.8.1 # also require the tag to match
#
# Exit code: non-zero on any mismatch.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,/^set /p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

PKG_JSON="${REPO_ROOT}/com.xpturn.klotho/package.json"
[[ -f "${PKG_JSON}" ]] || { echo "package.json not found: ${PKG_JSON}" >&2; exit 1; }

# First "version" key in the manifest (dependencies come later and have no such key).
EXPECTED="$(grep -m1 '"version"[[:space:]]*:' "${PKG_JSON}" | sed -E 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"
[[ -n "${EXPECTED}" ]] || { echo "Could not read the version from package.json" >&2; exit 1; }

echo "package.json version: ${EXPECTED}"

FAILED=0

# plugin.cfg files, in a stable order. (No mapfile: bash 3.2 on macOS lacks it.)
CFGS=()
while IFS= read -r line; do
  [[ -n "${line}" ]] && CFGS+=("${line}")
done < <(
  {
    echo "${REPO_ROOT}/com.xpturn.klotho/Godot~/plugin.cfg"
    echo "${REPO_ROOT}/dist/addons/klotho/plugin.cfg"
    find "${REPO_ROOT}/Samples" -mindepth 4 -maxdepth 4 -path '*/addons/klotho/plugin.cfg' -print
  } | sort -u
)

for cfg in "${CFGS[@]}"; do
  rel="${cfg#"${REPO_ROOT}/"}"
  if [[ ! -f "${cfg}" ]]; then
    echo "  MISSING  ${rel}"
    FAILED=1
    continue
  fi
  got="$(grep -m1 '^version[[:space:]]*=' "${cfg}" | sed -E 's/^version[[:space:]]*=[[:space:]]*"?([^"]*)"?.*/\1/')"
  if [[ "${got}" == "${EXPECTED}" ]]; then
    echo "  ok       ${rel} (${got})"
  else
    echo "  MISMATCH ${rel}: '${got}' != '${EXPECTED}'"
    FAILED=1
  fi
done

if [[ -n "${TAG}" ]]; then
  if [[ "${TAG}" == "v${EXPECTED}" ]]; then
    echo "  ok       tag ${TAG}"
  else
    echo "  MISMATCH tag '${TAG}' != 'v${EXPECTED}'"
    FAILED=1
  fi
fi

if [[ ${FAILED} -ne 0 ]]; then
  echo
  echo "Version mismatch. Update the files above (and re-run Tools/deploy-addon-to-samples.sh if the sample copies are stale)."
  exit 1
fi

echo "All versions consistent."
