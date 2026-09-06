# Příklady modů pro Tesseris

`examples` obsahuje zdrojové projekty pro modery. `mods` obsahuje až nainstalované runtime balíčky.
Sestavení modu vytvoří `.tmod` a nesestavuje hru.

| Cesta | Účel |
|---|---|
| [`RubyWorldgenMod`](RubyWorldgenMod/README.cs.md) | Malý první mod: obsah, recepty, nástroj a worldgen hook |
| [`TotalConversionMod`](TotalConversionMod/README.md) | Úplná referenční ukázka API v2 |
| [`LuantiLuaMob`](LuantiLuaMob/README.md) | Entita v Lua ve stylu Luanti napojená na C# moba |
| [`guides/en-US`](guides/en-US/README.md) | Kompletní anglický návod |
| [`guides/cs-CZ`](guides/cs-CZ/README.md) | Český dokumentační rozcestník |

```powershell
cd examples\TotalConversionMod
dotnet restore
dotnet build -c Release --no-restore
```

Výsledný `.tmod` z `bin/Release/net8.0/tesseris-mod/` zkopíruj do `mods` vedle hotové hry.
