# Installation — Unity

Install the **client** package first, then (optionally) wire a **dedicated server**. The engine core is shared between them.

> Overview & feature map: [README](../README.md).

---

## 1. Client (UPM)

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter each URL in order (the first two are required git dependencies that UPM cannot auto-resolve):

```text
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill
https://github.com/xpTURN/Klotho.git?path=com.xpturn.klotho
```

Pin a specific Klotho version with `#vX.Y.Z` (e.g. `https://github.com/xpTURN/Klotho.git?path=com.xpturn.klotho#v0.2.9`).

Unity registry packages (`com.unity.inputsystem`, `com.unity.ai.navigation` for the NavMesh exporter, `com.unity.nuget.newtonsoft-json`) resolve automatically via the package's `dependencies` field.

Verified editors: **2022.3.62f3** (the declared minimum), **6000.3.9f1** and **6000.6.0f1**. Each has a smoke project under `Samples/` (`Unity2022.Tests`, `Brawler`, `Unity6000.6.Tests`) that `Tools/run-all-tests.sh` runs in batch mode.

### Polyfill activation (C# 9–11 syntax)

Klotho uses C# 11 features (`required`, `init`, custom interpolated string handlers, etc.) in some assemblies. After installing `xpTURN.Polyfill`, enable the language version once per project:

1. Run **Edit > Polyfill > Player Settings > Apply Additional Compiler Arguments -langversion (All Installed Platforms)**.
2. Settings are persisted to **ProjectSettings/xpTURN.Polyfill.Settings.json**.

This adds `-langversion:preview` to Player Settings (Unity build), inserts `<LangVersion>preview</LangVersion>` into regenerated `.csproj` (IDE), and defines the `CSHARP_PREVIEW` scripting symbol. Without this step, Klotho assemblies that rely on C# 11 syntax may fail to compile. Details: [Polyfill README](https://github.com/xpTURN/Polyfill#project-settings-c-langversion).

---

## 2. Dedicated server (optional)

The dedicated server is a plain `net8.0` console host built on the **engine-agnostic core**. Klotho ships the core as package source, so a server builds those assemblies from your vendored copy of the package: `Server~/` holds per-assembly server projects that mirror the client asmdef structure; your server `.csproj` `<ProjectReference>`s them.

Two ways to reach the `Server~` projects:

### A. git submodule (recommended)

Vendor this repo into your game project as a submodule (e.g. under `<yourGame>/External/Klotho`); the UPM package is its top-level `com.xpturn.klotho/` subfolder. Reference it from Unity via a `file:` entry in `Packages/manifest.json` (`"com.xpturn.klotho": "file:../External/Klotho/com.xpturn.klotho"`), and reference the `Server~` projects from your server csproj at the correct relative depth:

```xml
<!-- Server csproj at <yourGame>/Server/MyDedicatedServer.csproj; submodule at <yourGame>/External/Klotho -->
<ItemGroup>
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\KlothoServer\KlothoServer.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.Runtime\xpTURN.Klotho.Runtime.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.Logging\xpTURN.Klotho.Logging.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.Gameplay\xpTURN.Klotho.Gameplay.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.LiteNetLib\xpTURN.Klotho.LiteNetLib.csproj" />
</ItemGroup>
<!-- Source generator for your game's [KlothoSerializable]/[KlothoComponent] types compiled into the exe -->
<ItemGroup>
  <Analyzer Include="..\External\Klotho\com.xpturn.klotho\Plugins\Analyzers\KlothoGenerator.dll" />
</ItemGroup>
```

Adjust the `..\` depth and submodule path to match where your csproj and submodule sit (the bundled `Samples/SdSample` references the in-repo package the same way — `..\..\..\com.xpturn.klotho\Server~\…`). At startup call `KlothoServerBootstrap.Initialize("YourGamePrefix")` — it force-loads the split assemblies and runs warmups so the cross-assembly `[ModuleInitializer]` registrations (commands / messages / components) complete before the first room is built.

### B. UPM `Library/PackageCache` + `<KlothoServerRoot>`

If you don't want a submodule, point a property at your resolved PackageCache `Server~` path. The `@<hash>` suffix changes on every pull, so this needs occasional refresh:

```xml
<PropertyGroup>
  <KlothoServerRoot>$(MSBuildProjectDirectory)\..\..\Library\PackageCache\com.xpturn.klotho@1a2b3c4d\Server~</KlothoServerRoot>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="$(KlothoServerRoot)\KlothoServer\KlothoServer.csproj" />
  <ProjectReference Include="$(KlothoServerRoot)\xpTURN.Klotho.Runtime\xpTURN.Klotho.Runtime.csproj" />
  <!-- + Logging / Gameplay / LiteNetLib as above -->
</ItemGroup>
```

### Server `.csproj` — full annotated example

A complete, minimal server project (framework references + source generator + shared sim + data/config copy) is documented in [SdSample.md §3.1](Samples/SdSample.md#31-server-project-file-serversdsampleservercsproj). For a full-featured reference (with `Program.cs`, callbacks, config files, single-room/multi-room/test CLI), see [Brawler.H.DedicatedServer.md](Samples/Brawler.H.DedicatedServer.md).
