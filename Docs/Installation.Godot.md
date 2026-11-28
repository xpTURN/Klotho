# Installation — Godot (.NET)

Install the **client** addon first, then (optionally) wire a **dedicated server**. The engine core is shared between them.

> Overview & feature map: [README](../README.md). Requires **Godot 4.4+ (mono / .NET)**.

---

## 1. Client (.NET addon)

The Godot adapter ships as a self-contained `addons/klotho/` folder — a prebuilt engine-agnostic core DLL + the Godot adapter **source** (compiled against your own GodotSharp) + the source generator.

1. Copy the `klotho/` folder into your project's `res://addons/`.
2. Add one line to your game `.csproj`:

   ```xml
   <Import Project="addons/klotho/Klotho.props" />
   ```
   > No `.csproj` yet? Generate one via *Project* ▸ *Tools* ▸ *C#* ▸ **Create C# solution**, then add the line above.
3. Build (`dotnet build` or the Godot editor's **Build** button).

`Klotho.props` references the prebuilt core DLL, adds the generator analyzer, and declares the managed runtime dependencies as NuGet `PackageReference`s (`Newtonsoft.Json 13.0.3`, `LiteNetLib 2.1.5`). The adapter sources under `addons/klotho/Adapters/**` are picked up automatically by the `Godot.NET.Sdk` default compile glob — do **not** add an explicit `<Compile Include>`. The adapter is **net8.0**, so **no Polyfill step is needed** (native `init` / `required`). Full details (folder contents, `plugin.cfg`, GodotSharp coupling): [`com.xpturn.klotho/Godot~/README.md`](../com.xpturn.klotho/Godot~/README.md).

> Keep any **dedicated-server** project **outside** the game project folder (a sibling — see §2), so the `Godot.NET.Sdk` default glob never compiles its `Program.cs` / server callbacks into the client. If you must nest it under the game project, exclude it explicitly: `<Compile Remove="Server\**\*.cs" />`.

> Input is captured with Godot's built-in `Input` API — nothing extra to install. Deterministic navigation data is loaded from a pre-baked `.bytes` asset; see [Navigation.md](Navigation.md).

---

## 2. Dedicated server (optional)

The dedicated server is **engine-agnostic** — a plain `net8.0` console host on **`Microsoft.NET.Sdk`** that uses the core, never the Godot adapter. The game therefore ships a server with **zero Godot runtime** on the host.

The same `addons/klotho/` you imported for the client also ships a **server props** file. Add one line to your server `.csproj`:

```xml
<Import Project="addons/klotho/Klotho.Server.props" />
```

`Klotho.Server.props` references the prebuilt `xpTURN.Klotho.Runtime.dll` + `KlothoServer.dll`, adds the generator analyzer, declares the three NuGet deps, and **Removes the Godot adapter sources** from the compile (a server has no GodotSharp). The Server-Driven framework (`RoomRouter` / `RoomManager` / `ServerLoop` / `ServerNetworkService`) lives in the core DLL; `KlothoServer.dll` adds the bootstrap + config-loader helpers.

Then compile your **shared deterministic sim** into the exe and copy the data asset + config next to the output:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ServerGarbageCollection>true</ServerGarbageCollection>
  </PropertyGroup>

  <!-- paths assume the server is a SIBLING of the game project; <game> = your client folder -->
  <Import Project="../<game>/addons/klotho/Klotho.Server.props" />

  <!-- the SAME sim .cs the Godot client compiles, built into the exe -->
  <ItemGroup>
    <Compile Include="..\<game>\Sim\**\*.cs" Link="Game\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- data asset + authoritative config next to the exe -->
  <ItemGroup>
    <Content Include="..\<game>\Data\*.bytes"><Link>Data\%(Filename)%(Extension)</Link><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
    <Content Include="simulationconfig.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
    <Content Include="sessionconfig.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
  </ItemGroup>
</Project>
```

**Recommended layout**: keep the server as a **sibling** of the game project (its own folder, outside the client's tree) so the `Godot.NET.Sdk` glob never sees its `Program.cs`; the paths above then reach back into the client folder for the shared addon / sim / data. The sample does exactly this — `Samples/GodotSdSampleServer/` sits beside `Samples/GodotSdSample/` and imports `../GodotSdSample/addons/klotho/Klotho.Server.props`. At startup call `KlothoServerBootstrap.Initialize("YourGamePrefix")` so the cross-assembly `[ModuleInitializer]` registrations complete before the first room.

> Full annotated walkthrough (with `Program.cs`, callbacks, config files, run commands): [GodotSdSample.md §5-8](Samples/GodotSdSample.md#5-8-server-project-file-godotsdsampleserversdsampleservercsproj).
