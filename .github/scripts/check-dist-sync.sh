#!/usr/bin/env bash
#
# check-dist-sync.sh — verify the committed dist/addons/klotho matches the source.
#
# release.yml zips dist/addons/ as committed; it never regenerates it. So a stale
# dist/ silently ships an old addon. This regenerates the addon into a temporary
# directory via Tools/pack-godot-addon.sh and diffs it against the committed tree.
#
# DLLs are excluded from the comparison: every build stamps a fresh MVID, so the
# bytes never match even when the sources are identical. Only the text artifacts
# (adapter sources, .cs.uid, Klotho*.props, plugin.cfg/gd, README, LICENSE) are compared.
#
# Usage: .github/scripts/check-dist-sync.sh
# Exit code: non-zero if the committed dist/ differs from a fresh pack.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMMITTED="${REPO_ROOT}/dist/addons/klotho"

[[ -d "${COMMITTED}" ]] || { echo "dist/addons/klotho not found — nothing to compare." >&2; exit 1; }

# Pack into a scratch directory so the working tree is never touched
# (pack-godot-addon.sh does rm -rf on its output path).
TMP_OUT="$(mktemp -d)"
trap 'rm -rf "${TMP_OUT}"' EXIT
FRESH="${TMP_OUT}/addons/klotho"

echo "==> regenerating the addon into ${FRESH}"
if ! "${REPO_ROOT}/Tools/pack-godot-addon.sh" "${FRESH}"; then
  echo "pack-godot-addon.sh failed." >&2
  exit 1
fi

echo
echo "==> diffing against the committed dist/addons/klotho (DLLs excluded)"
if diff -r -x '*.dll' -x '.DS_Store' "${COMMITTED}" "${FRESH}"; then
  echo "dist/ is in sync with the source."
  exit 0
fi

cat >&2 <<'MSG'

dist/addons/klotho is out of sync with the source.
Regenerate and commit it:

    Tools/pack-godot-addon.sh
    git add dist && git commit

(DLLs are not compared here — rebuilding them is still part of the same step.)
MSG
exit 1
