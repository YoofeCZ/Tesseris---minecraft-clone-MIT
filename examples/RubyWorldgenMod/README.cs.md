# Ruby Worldgen Mod

Malý samostatný SDK příklad ukazuje JSON blok, item, nástroj a recepty spolu s deterministickým
worldgen hookem. Projekt neodkazuje na Game ani Engine.

```powershell
dotnet restore
dotnet build -c Release --no-restore
```

Výsledkem je `bin/Release/net8.0/tesseris-mod/ruby-1.0.0.tmod`. Rozsáhlý příklad je v
[`../TotalConversionMod`](../TotalConversionMod/README.md) a český návod v
[`../../docs/MODDING.md`](../../docs/MODDING.md).
