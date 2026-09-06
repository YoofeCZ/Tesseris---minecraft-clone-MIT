# Tesseris modding examples

This directory is the source workspace for mod authors. It is intentionally separate from `mods/`, which
contains installed runtime packages. Building an example produces a `.tmod`; it does not rebuild Tesseris.

## Choose a starting point

| Path | Purpose |
|---|---|
| [`RubyWorldgenMod`](RubyWorldgenMod/README.md) | Small first mod: content, recipes, a tool, and an ore-generation hook |
| [`TotalConversionMod`](TotalConversionMod/README.en.md) | Full API-v2 reference: custom world, ECS, stacks, containers, UI, rendering, audio, events, and networking |
| [`LuantiLuaMob`](LuantiLuaMob/README.md) | Luanti-style Lua entity callbacks running on a C# mob |
| [`guides/en-US`](guides/en-US/README.md) | Structured English modding guide and API map |
| [`guides/cs-CZ`](guides/cs-CZ/README.md) | Czech documentation index |

## Repository layout

```text
examples/
  README.md                         this English index
  README.cs.md                      Czech index
  guides/
    en-US/                          complete structured English guide
    cs-CZ/                          Czech guide index
  RubyWorldgenMod/                  small standalone source project
  TotalConversionMod/               total-conversion source project
sdk-feed/                           repository-local NuGet packages
mods/                               installed .tmod files or unpacked runtime packages
src/ModApi/                         public stable contracts
src/Loader.Abstractions/            PreLaunch/CoreMod contracts
```

## Build an example

Both projects include a `NuGet.config` that points to the repository-local `sdk-feed`.

```powershell
cd examples\RubyWorldgenMod
dotnet restore
dotnet build -c Release --no-restore
```

The mod archive appears under `bin/Release/net8.0/tesseris-mod/`. Copy that single `.tmod` file into the
`mods` directory beside an already-built game, or use the explicit install target documented in each example.

The game, renderer, Engine project, and shaders are not project references of either mod.
