# Total Conversion Mod — standalone SDK v2

This is the canonical standalone C# reference for Tesseris. The project uses `Tesseris.ModSdk/2.0.1` and
has no project reference to Game, Engine, or the Mod API source tree. Opening or building it never builds
the game.

## What the example registers

- its own world preset, dimension, biome, biome source, and complete deterministic generator;
- an ECS component, archetype, and system;
- a per-stack component with split and merge rules;
- a generic four-slot container, menu, and transactional action handler;
- a modal screen, HUD, input binding (`M`), slash command, GPU line and billboard, particles, and audio;
- arbitrary item-use behavior;
- a typed event subscription, versioned network channel, serializer, and inter-mod service;
- a custom block, item, model, PNG texture, and WAV sound.

There is no built-in “machine” type. The same generic registries can implement machinery, magic, quests,
mobs, technology trees, or a different game.

## Requirements and IDE setup

Install the .NET 8 SDK. This repository copy already contains a `NuGet.config` that points at
`../../sdk-feed`, so no game build and no manually configured package source are required. Open
`TotalConversionMod.sln` in Visual Studio or Rider, or open the `.csproj` in VS Code, and restore packages.
`using Tesseris.ModApi` will then resolve normally.

For a project outside this repository, use the published Tesseris NuGet source. Until packages are
published, create a local feed without building the game:

```powershell
$feed = Join-Path $env:TEMP "tesseris-mod-feed"
dotnet pack ..\..\src\ModApi\Tesseris.ModApi.csproj -c Release -o $feed
dotnet pack ..\..\src\Loader.Abstractions\Tesseris.Loader.Abstractions.csproj -c Release -o $feed
dotnet pack ..\..\src\ModSdk\Tesseris.ModSdk.csproj -c Release -o $feed
dotnet nuget add source $feed --name TesserisLocal
```

## Restore, build, and install

Run these commands from this directory:

```powershell
dotnet restore TotalConversionMod.csproj
dotnet build TotalConversionMod.csproj -c Release --no-restore
```

Deterministic output:

```text
bin/Release/net8.0/tesseris-mod/total_conversion/
bin/Release/net8.0/tesseris-mod/total_conversion-2.0.0.tmod
```

A normal build never copies anything into the game. Installation is explicit:

```powershell
dotnet build TotalConversionMod.csproj -c Release `
  -t:InstallTesserisMod `
  -p:TesserisModsDir="C:\Games\Tesseris\mods"
```

Alternatively, copy the `.tmod` directly into the `mods` directory beside `Tesseris.Launcher.exe`. Do not
unpack it, and do not rebuild Game or Engine.

## Source map

- `TotalConversionMod.cs` composes registries and owns stable namespaced IDs.
- `SkylandsWorld.cs` implements biome selection and complete terrain generation.
- `GameplaySystems.cs` demonstrates ECS, container state, menu actions, and item behavior.
- `ClientFeatures.cs` implements modal UI, HUD, input, commands, and renderer-neutral drawing.
- `IntegrationFeatures.cs` demonstrates typed events and safe network-to-game-thread handoff.
- `content/` contains runtime block, item, model, texture, and sound assets.

Managed mods are trusted in-process code. `trustedCode: true` is a security decision, not a sandbox. Install
managed mods and CoreMods only from authors you trust.

Continue with the [structured English guide](../guides/en-US/README.md).
