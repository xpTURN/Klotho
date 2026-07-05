#!/usr/bin/env bash
# NP4 — Generate the no-publish addons/ distribution (hybrid: core DLL + adapter source).
#
# Produces dist/addons/klotho/ from the canonical source (com.xpturn.klotho/):
#   lib/        xpTURN.Klotho.Runtime.dll      (core, prebuilt; engine-agnostic, no GodotSharp coupling)
#               KlothoServer.dll               (server helpers: KlothoServerBootstrap / config loaders)
#   Adapters/   adapter .cs source             (compiled in the consumer against its own GodotSharp)
#   Analyzers/  KlothoGenerator.dll            (source generator for the consumer's own ECS code)
#   Klotho.props                               (client: Reference core DLL + Analyzer + deps)
#   Klotho.Server.props                        (dedicated server: core + KlothoServer DLLs, no Godot adapter)
#
# Consumer integration (one line per project):
#   client game:      <Import Project="addons/klotho/Klotho.props" />
#   dedicated server: <Import Project="addons/klotho/Klotho.Server.props" />
#
# The canonical Godot core csproj and the samples are left untouched; the LiteNetLib NuGet swap is
# scoped to the packaging build (Packaging/Klotho.Core.Pack.csproj).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PKG="$REPO_ROOT/com.xpturn.klotho"
OUT="${1:-$REPO_ROOT/dist/addons/klotho}"
SERVER_PACK="$PKG/Godot~/Packaging/Klotho.Server.Pack.csproj"   # ProjectReferences Klotho.Core.Pack -> builds both DLLs
GEN_DLL="$PKG/Plugins/Analyzers/KlothoGenerator.dll"

echo "==> addons output: $OUT"
rm -rf "$OUT"
mkdir -p "$OUT/lib" "$OUT/Adapters" "$OUT/Analyzers"

echo "==> 1/6 build core + server DLLs (LiteNetLib excluded -> NuGet)"
dotnet build "$SERVER_PACK" -c Release -v q -nologo >/dev/null
BIN="$PKG/Godot~/Packaging/bin/Release"
CORE_DLL="$(find "$BIN" -name 'xpTURN.Klotho.Runtime.dll' | head -1)"
SERVER_DLL="$(find "$BIN" -name 'KlothoServer.dll' | head -1)"
[ -n "$CORE_DLL" ] && [ -n "$SERVER_DLL" ] || { echo "!! core/server DLL not found"; exit 1; }
cp "$CORE_DLL" "$SERVER_DLL" "$OUT/lib/"

echo "==> 2/6 copy adapter source (+ .cs.uid so Godot keeps stable script UIDs)"
( cd "$PKG/Godot~/Adapters" && find . \( -name '*.cs' -o -name '*.cs.uid' \) -print0 | while IFS= read -r -d '' f; do
    mkdir -p "$OUT/Adapters/$(dirname "$f")"; cp "$f" "$OUT/Adapters/$f"; done )

echo "==> 3/6 copy generator analyzer"
cp "$GEN_DLL" "$OUT/Analyzers/"

echo "==> 4/6 write Klotho.props (client)"
cat > "$OUT/Klotho.props" <<'PROPS'
<!-- Import from the consumer game .csproj: <Import Project="addons/klotho/Klotho.props" /> -->
<Project>
  <PropertyGroup>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion Condition="'$(LangVersion)' == ''">latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <!-- core = prebuilt DLL -->
    <Reference Include="xpTURN.Klotho.Runtime">
      <HintPath>$(MSBuildThisFileDirectory)lib/xpTURN.Klotho.Runtime.dll</HintPath>
    </Reference>
    <!-- adapter = source: Adapters/**/*.cs is auto-compiled by the consumer's Godot.NET.Sdk default glob
         (addons/ is under the project tree). Do NOT add an explicit <Compile Include> here — it would
         double-compile (CS0579/CS2002). Only the generator analyzer needs declaring. -->
    <Analyzer Include="$(MSBuildThisFileDirectory)Analyzers/KlothoGenerator.dll" />
    <!-- a DLL <Reference> carries no transitive NuGet deps -> declare the core's runtime deps -->
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="LiteNetLib" Version="2.1.4" />
  </ItemGroup>
</Project>
PROPS

echo "==> 5/6 write Klotho.Server.props (dedicated server)"
cat > "$OUT/Klotho.Server.props" <<'PROPS'
<!-- Import from a DEDICATED SERVER project (.NET console, no Godot):
       <Import Project="addons/klotho/Klotho.Server.props" />
     The Server-Driven framework (RoomRouter/RoomManager/ServerLoop/ServerNetworkService) is in the core DLL;
     KlothoServer.dll adds the server helpers (KlothoServerBootstrap / Config loaders). No Godot adapter. -->
<Project>
  <PropertyGroup>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion Condition="'$(LangVersion)' == ''">latest</LangVersion>
  </PropertyGroup>
  <!-- A server is not a Godot project: never compile the bundled Godot adapter sources (no GodotSharp).
       This Remove keeps the server safe even if addons/ sits under the server project's default glob. -->
  <ItemGroup>
    <Compile Remove="$(MSBuildThisFileDirectory)Adapters/**/*.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="xpTURN.Klotho.Runtime">
      <HintPath>$(MSBuildThisFileDirectory)lib/xpTURN.Klotho.Runtime.dll</HintPath>
    </Reference>
    <Reference Include="KlothoServer">
      <HintPath>$(MSBuildThisFileDirectory)lib/KlothoServer.dll</HintPath>
    </Reference>
    <Analyzer Include="$(MSBuildThisFileDirectory)Analyzers/KlothoGenerator.dll" />
    <!-- a DLL <Reference> carries no transitive NuGet deps -> declare the core's runtime deps -->
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="LiteNetLib" Version="2.1.4" />
  </ItemGroup>
</Project>
PROPS

echo "==> 6/6 copy addon meta (plugin.cfg, plugin.gd[+uid], README, LICENSE)"
cp "$PKG/Godot~/plugin.cfg"    "$OUT/"
cp "$PKG/Godot~/plugin.gd"     "$OUT/"
cp "$PKG/Godot~/plugin.gd.uid" "$OUT/"
cp "$PKG/Godot~/README.md"     "$OUT/"
# Godot Asset Library requires the LICENSE to be bundled with the addon.
cp "$REPO_ROOT/LICENSE"        "$OUT/"

# Drop Unity .meta sidecars; keep .cs.uid (Godot script UIDs, copied above).
find "$OUT/Adapters" -name '*.meta' -delete 2>/dev/null || true

echo "==> done. Tree:"
find "$OUT" -maxdepth 2 -type f | sed "s|$OUT|addons/klotho|" | sort | head -40
