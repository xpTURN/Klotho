#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "=== Building BrawlerDedicatedServer (Debug) ==="
dotnet build "$SCRIPT_DIR/BrawlerDedicatedServer.csproj" -c Debug
echo "=== Build succeeded ==="
