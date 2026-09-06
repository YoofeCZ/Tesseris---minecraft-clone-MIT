# 1. Projects, SDK, build, and packaging

## Source projects and installed mods

Work on C# and content files in a source directory such as `examples/MyMod` or a separate repository.
The game reads installed packages from `mods` beside the launcher:

```text
Tesseris.Launcher.exe
mods/
  author.mymod-1.0.0.tmod
```

An unpacked package in its own subdirectory is also supported, but `.tmod` is the normal distribution
format. Never put loose manifests directly in the `mods` root.

## Install the project template

From this repository:

```powershell
dotnet new install D:\Tesseris\sdk-feed\Tesseris.ModSdk.2.0.1.nupkg
dotnet new tesseris-mod -n MyMod -o MyMod `
  --mod-id author.mymod `
  --mod-name "My Mod"
```

The generated project is a standalone .NET 8 library. It references NuGet contracts, not Game or Engine.

Minimal project file:

```xml
<Project Sdk="Tesseris.ModSdk/2.0.1">
  <PropertyGroup>
    <AssemblyName>MyMod</AssemblyName>
    <ModId>author.mymod</ModId>
    <ModName>My Mod</ModName>
    <ModVersion>1.0.0</ModVersion>
    <ModApiVersion>^2.0.0</ModApiVersion>
    <ModEntryType>Author.MyMod.ModEntry</ModEntryType>
  </PropertyGroup>
</Project>
```

## Manifest schema v2

`tesseris.mod.json` is part of the package contract:

```json
{
  "schemaVersion": 2,
  "id": "author.mymod",
  "name": "My Mod",
  "version": "1.0.0",
  "loaderVersion": "^2.0.0",
  "modApiVersion": "^2.0.0",
  "gameVersion": "*",
  "entrypoints": [
    {
      "phase": "Runtime",
      "assembly": "MyMod.dll",
      "type": "Author.MyMod.ModEntry"
    }
  ],
  "contentRoot": "content",
  "trustedCode": true,
  "capabilities": ["content", "worldgen", "ui"]
}
```

The manifest `id` is the mod's permanent namespace. Do not change it after release. `entrypoints` may contain
`PreLaunch`, `CoreMod`, `Runtime`, `Client`, and `DedicatedServer` entries. Managed entrypoints require
`trustedCode: true`; a CoreMod additionally requires `coreMod: true`.

Dependencies are version ranges keyed by stable mod ID:

```json
"dependencies": {
  "author.library": ">=1.2.0 <2.0.0"
}
```

Missing dependencies, incompatible API/game/loader versions, duplicate IDs, and dependency cycles fail at
startup with an attributed error.

## Runtime entrypoint

```csharp
using Tesseris.ModApi;

namespace Author.MyMod;

public sealed class ModEntry : IMod
{
    public void Configure(IModContext context)
    {
        context.Logger.Info($"Configuring {context.Mod.Id} {context.Mod.Version}");
    }
}
```

`Configure` runs once in resolved dependency order while registries are mutable. Register definitions and
callbacks there; do not mutate frozen registries later.

## Restore and build

Configure a NuGet source containing `Tesseris.ModSdk`, `Tesseris.ModApi`, and
`Tesseris.Loader.Abstractions`. Repository examples already use `../../sdk-feed` through `NuGet.config`.

```powershell
dotnet restore MyMod.csproj
dotnet build MyMod.csproj -c Release --no-restore
```

Output:

```text
bin/Release/net8.0/tesseris-mod/author.mymod/
bin/Release/net8.0/tesseris-mod/author.mymod-1.0.0.tmod
```

The archive is deterministic and excludes platform assemblies such as Game, Engine, ModApi, and loader
contracts. Ordinary managed dependencies and `content/` files are included.

## Explicit install

```powershell
dotnet build MyMod.csproj -c Release `
  -t:InstallTesserisMod `
  -p:TesserisModsDir="C:\Games\Tesseris\mods"
```

A normal build never writes into a game installation. Restart the game after replacing a loaded package.

## Content-only package

A mod containing only manifest and content files requires no DLL and no `trustedCode` marker:

```text
mods/blue_stone/
  tesseris.mod.json
  content/
    blocks/blue_stone.json
    textures/blue_stone.png
```

Use schema v2 metadata but omit `entrypoints`. Content-only packages can add blocks, items, ordinary tools,
recipes, textures, and models.

[Next: content and assets](02-content-and-assets.md)
