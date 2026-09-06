# Ruby Worldgen Mod

This is the small standalone SDK example. It demonstrates the minimum useful combination of:

- schema-v2 `.tmod` packaging;
- JSON block, item, tool, and recipe content;
- namespaced IDs;
- a deterministic, thread-safe chunk feature hook.

The project uses the repository-local NuGet feed, so it never builds or references the game:

```powershell
dotnet restore
dotnet build -c Release --no-restore
```

Output:

```text
bin/Release/net8.0/tesseris-mod/ruby-1.0.0.tmod
```

Install explicitly:

```powershell
dotnet build -c Release -t:InstallTesserisMod `
  -p:TesserisModsDir="C:\Games\Tesseris\mods"
```

For a broad total-conversion reference, use
[`../TotalConversionMod`](../TotalConversionMod/README.en.md). The complete English guide starts at
[`../guides/en-US`](../guides/en-US/README.md).
