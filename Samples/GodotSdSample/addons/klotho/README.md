# Klotho — Godot (.NET) addon

Deterministic lockstep / rollback netcode for **Godot 4.4+ (mono / .NET)**. This `addons/klotho/` folder is a
self-contained distribution — drop it into your Godot project and add one line to your game `.csproj`.

> Klotho's runtime is a **C# library** — it works via the `Klotho.props` import whether or not the plugin is
> enabled. The bundled `plugin.gd` registers the addon under *Project Settings ▸ Plugins* and adds editor
> tooling (DataAsset JSON→bytes conversion menu and FileSystem dock context menu).

---

## Contents

```
addons/klotho/
├── bin/xpTURN.Klotho.Runtime.dll   engine-agnostic core (prebuilt; no Godot API → no GodotSharp coupling)
├── Adapters/                       Godot adapter SOURCE (+ .cs.uid for stable script UIDs)
│   ├── View/                       entity view layer (EntityViewNode, EntityViewUpdaterNode, pool, interpolation)
│   ├── Deterministic/              FP type bridges: FPVector2/3/4, FPQuaternion, FPRay3, FPPlane, FPBounds3
│   ├── Editor/                     DataAsset JSON→bytes editor tool
│   ├── GodotSessionDriver          transport + session pump (Node-based)
│   ├── GodotSessionFlowAsync       JoinP2PAsync / JoinServerDrivenAsync / ReconnectAsync (Task-based)
│   ├── GodotFlowSetupBuilderExtensions  WithGodotDefaults() — AppVersion + GodotDeviceIdProvider in one call
│   ├── GodotKlothoLogger           CreateDefault() — GodotLogSink + RollingFileSink under user://logs
│   ├── GodotConnectionAsync        low-level connect/reconnect Task wrappers
│   ├── GodotDebugSink / GodotLogSink   IKLogger / IKLogSink routing to the Godot console
│   └── (+ reconnect, Resource-based config helpers)
│                                   compiled into YOUR assembly against YOUR GodotSharp
├── Analyzers/KlothoGenerator.dll   source generator (runs on your own ECS components/commands)
├── Klotho.props                    wires the above into your project (import this)
├── plugin.cfg / plugin.gd          EditorPlugin: registers the addon + DataAsset JSON→bytes editor tool
└── README.md                       (this file)
```

The core is a prebuilt DLL; the adapter is shipped as **source** so it builds against your project's own
GodotSharp version. Runtime deps (`Newtonsoft.Json 13.0.3`, `LiteNetLib 2.1.5`)
are declared by `Klotho.props` as NuGet `PackageReference`s.

---

## Install

1. Copy this `klotho/` folder into your project's `addons/` (i.e. `res://addons/klotho/`).
2. Add one line to your game `.csproj`:

   ```xml
   <Import Project="addons/klotho/Klotho.props" />
   ```
   > No `.csproj` yet? Generate one via *Project* ▸ *Tools* ▸ *C#* ▸ **Create C# solution**, then add the line above.
3. Build (`dotnet build` or the Godot editor's *Build* button).

`Klotho.props` references the core DLL, adds the generator analyzer, declares the three NuGet deps, and sets
`AllowUnsafeBlocks`. The adapter's `Adapters/**/*.cs` are picked up automatically by the Godot.NET.Sdk default
glob (do **not** add an explicit `<Compile Include>` — it would double-compile).

> Requires **Godot 4.4+ mono** — the floor where the bundled `.cs.uid` script identities are honored; all
> adapter APIs predate it (`[GlobalClass]` since 4.1, `Quaternion.FromEuler` / `Mathf.DegToRad` since 4.0).
> Built and verified on 4.6.x.

---

## Enable the plugin

> **Prerequisite**: build the project at least once before enabling the plugin.
> The plugin references C# `[GlobalClass]` types — enabling before a successful build causes
> `plugin.gd` to fail to parse and the plugin will not activate.

1. *Project* ▸ *Project Settings* ▸ **Plugins** tab.
2. Find **Klotho** in the list and turn the **Enable** toggle on.

Once enabled:

- **Project** menu gains a `Klotho: Convert DataAsset JSON -> bytes` item.
- FileSystem dock **right-click** on any `.json` file shows the same item.

Both invoke the same conversion: reads the selected `.json` DataAsset file(s), converts to binary
`.bytes` via `DataAssetJsonConverter`, writes the output next to the source, rescans the filesystem,
and navigates the FileSystem dock to the newly created `.bytes` file.

To disable: toggle **Enable** off. The menu item and context menu entry are removed automatically.

> **Note for ProjectReference setups** (in-repo samples): the plugin files (`plugin.cfg`, `plugin.gd`)
> must be present at `res://addons/klotho/` inside the Godot project for Godot to discover the plugin.
> Referencing the adapter via an external `<ProjectReference>` compiles the C# types correctly, but
> Godot only scans `res://addons/*/plugin.cfg` — not external paths.

---

## Usage notes

- **Flow setup** — use `KlothoFlowSetupBuilder` + `WithGodotDefaults()` to wire AppVersion (read from
  `ProjectSettings`) and `GodotDeviceIdProvider` in one call:
  ```csharp
  _flow = new KlothoSessionFlow(
      new KlothoFlowSetupBuilder(callbacksFactory)
          .WithLogger(_logger).WithTransport(_transport).WithAssetRegistry(_registry)
          .WithGodotDefaults()
          .Build());
  ```
- **Logging** — `GodotKlothoLogger.CreateDefault()` returns a logger that writes to both the Godot console
  and a rolling file under `user://logs` (the path is resolved via `ProjectSettings.GlobalizePath` —
  writable in both the editor and exported apps):
  ```csharp
  _logger = GodotKlothoLogger.CreateDefault(filePrefix: "MyGame", categoryName: "MyGame");
  ```
- **Deterministic geometry** — `FPRay3`, `FPPlane`, `FPBounds3` have Godot conversion helpers in
  `Adapters/Deterministic/`. `FPPlane.ToPlane()` / `ToFPPlane()` apply a sign inversion
  (`Godot.Plane.D = −FPPlane.distance` — a convention difference, not a handedness issue).
- **Editor authoring** — `GodotSessionConfig` / `GodotSimulationConfig` are `[GlobalClass]` `Resource`s; once
  built they appear in the editor's *New Resource* menu, can be saved as `.tres`, and injected into
  `KlothoFlowSetupBuilder` / `StartHostAndListen`.
- **Your own components** — declaring `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` /
  `[KlothoSerializableStruct]` types triggers `KlothoGenerator` (shipped here as the analyzer). The same DLL
  also runs the `DeterminismAnalyzer`, flagging float / non-deterministic API use in simulation code at build time.
- **Stable UIDs** — `.cs.uid` files ship beside the adapter sources so Godot keeps script UIDs instead of
  regenerating them per project.
