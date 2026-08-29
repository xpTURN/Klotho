#!/usr/bin/env bash
# Build the Klotho Godot addon ONCE (pack-godot-addon.sh) and deploy it into the in-repo sample
# projects that consume it via <Import addons/klotho/...>.
#
# Consumers (measured):
#   Samples/GodotP2pSample        -> addons/klotho/Klotho.props          (client)
#   Samples/GodotSdSample         -> addons/klotho/Klotho.props          (client)
#   Samples/GodotSdSampleServer   -> ../GodotSdSample/addons/klotho/Klotho.Server.props  (reuses SdSample's copy)
# So only two addon folders need refreshing; the dedicated server shares GodotSdSample's.
#
# Usage: deploy-addon-to-samples.sh [CONFIG]     CONFIG = Release (default) | Debug — forwarded to
# pack-godot-addon.sh. Debug is for verifying diagnostics in a sample: KDebug and #if DEBUG blocks are
# compiled out of Release, so a Release addon cannot print them. Deploy Release when done.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACK="$REPO_ROOT/Tools/pack-godot-addon.sh"
STAGING="$REPO_ROOT/dist/addons/klotho"
CONFIG="${1:-${KLOTHO_PACK_CONFIG:-Release}}"

TARGETS=(
  "$REPO_ROOT/Samples/GodotPolySample/addons/klotho"
  "$REPO_ROOT/Samples/GodotP2pSample/addons/klotho"
  "$REPO_ROOT/Samples/GodotSdSample/addons/klotho"
)

echo "==> build addon once -> $STAGING (config: $CONFIG)"
bash "$PACK" "$STAGING" "$CONFIG" >/dev/null
echo "    built ($(find "$STAGING" -type f | wc -l | tr -d ' ') files)"

for t in "${TARGETS[@]}"; do
  echo "==> deploy -> ${t#$REPO_ROOT/}"
  rm -rf "$t"
  mkdir -p "$(dirname "$t")"
  cp -R "$STAGING" "$t"
done

echo "==> done. Deployed to ${#TARGETS[@]} sample(s)."
