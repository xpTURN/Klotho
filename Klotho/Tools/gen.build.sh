#!/bin/bash
set -e
cd "$(dirname "$0")"
dotnet build KlothoGenerator/KlothoGenerator.csproj -c Release
cp KlothoGenerator/bin/Release/netstandard2.0/KlothoGenerator.dll \
   ../Assets/Klotho/Plugins/Analyzers/KlothoGenerator.dll
echo "Done: KlothoGenerator.dll deployed"
