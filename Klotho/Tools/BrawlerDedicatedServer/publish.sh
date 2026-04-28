#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=== Publishing BrawlerDedicatedServer (Release) ==="
dotnet publish "$SCRIPT_DIR/BrawlerDedicatedServer.csproj" -c Release -r osx-arm64 --self-contained
echo "=== Publish succeeded ==="
