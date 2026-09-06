# Total Conversion Mod (standalone SDK v2)

English: [`README.en.md`](README.en.md). Dokumentační rozcestník: [`../README.cs.md`](../README.cs.md).

Toto je kanonický samostatný C# mod pro Tesseris. Projekt odkazuje jen na NuGet SDK
`Tesseris.ModSdk/2.0.1`; nemá `ProjectReference` na `Game`, `Engine` ani zdrojový `ModApi`.
Otevření solution nebo build proto nikdy nesestavuje hru.

## Co ukázka registruje

- vlastní world preset, dimenzi, biome, biome source a úplný deterministický generátor,
- vlastní ECS komponentu, archetyp a systém,
- per-stack komponentu s pravidly split/merge,
- obecný čtyřslotový container, menu a transakční action handler,
- modální obrazovku, HUD, input binding (`M`), příkaz, GPU line/billboard, částice, zvuk a item-use chování,
- typovaný event handler, verzovaný síťový kanál a inter-mod službu,
- vlastní blok, předmět, model, PNG texturu a WAV zvuk.

Neexistuje žádný hardcoded typ „stroj“. Stejnými registry lze složit stroj, magii, quest,
nové moby, technologický strom nebo úplně jinou hru.

## Předpoklady a IDE

Nainstaluj .NET 8 SDK. Kopie příkladu v tomto repozitáři už obsahuje `NuGet.config`, který
ukazuje na přiložený `../../sdk-feed`; není potřeba sestavit hru ani ručně přidávat source.
Otevři `TotalConversionMod.sln` ve Visual Studiu/Rideru nebo samotný `.csproj` ve VS Code
a spusť restore. `using Tesseris.ModApi` pak nebude mít unresolved symboly.

Samostatný projekt mimo tento repozitář používá vydaný Tesseris NuGet feed. Dokud balíčky
nejsou publikované, lze si připravit vlastní lokální feed bez sestavení hry:

```powershell
$feed = Join-Path $env:TEMP "tesseris-mod-feed"
dotnet pack ..\..\src\ModApi\Tesseris.ModApi.csproj -c Release -o $feed
dotnet pack ..\..\src\Loader.Abstractions\Tesseris.Loader.Abstractions.csproj -c Release -o $feed
dotnet pack ..\..\src\ModSdk\Tesseris.ModSdk.csproj -c Release -o $feed
dotnet nuget add source $feed --name TesserisLocal
```

Zdroj lze později odstranit pomocí `dotnet nuget remove source TesserisLocal`.

## Restore, build a výstup

Spouštěj příkazy v této složce:

```powershell
dotnet restore TotalConversionMod.csproj
dotnet build TotalConversionMod.csproj -c Release --no-restore
```

Deterministický výstup vznikne zde:

```text
bin/Release/net8.0/tesseris-mod/total_conversion/
bin/Release/net8.0/tesseris-mod/total_conversion-2.0.0.tmod
```

Běžný build nic nekopíruje do hry. Instalace je vždy explicitní:

```powershell
dotnet build TotalConversionMod.csproj -c Release `
  -t:InstallTesserisMod `
  -p:TesserisModsDir="C:\Games\Tesseris\mods"
```

Nebo zkopíruj `.tmod` přímo do kořene `mods` vedle `Tesseris.exe`. Loader archiv načte
přímo, není třeba ho rozbalovat. Pak restartuj hru;
Game solution ani `Game.csproj` kvůli modu nikdy nesestavuj.

## Mapa zdrojů

- `TotalConversionMod.cs` skládá registry a drží všechna namespaced ID.
- `SkylandsWorld.cs` vlastní biome selection a terén.
- `GameplaySystems.cs` ukazuje ECS, menu state a chování předmětu.
- `ClientFeatures.cs` obsahuje modal UI, HUD, input a příkaz.
- `IntegrationFeatures.cs` ukazuje events a bezpečný přechod network callbacku na game thread.
- `content/` je runtime obsah kopírovaný do výsledného balíčku.

Managed mod běží jako důvěryhodný kód uvnitř procesu hry. `trustedCode: true` je tedy
bezpečnostní rozhodnutí: instaluj jen mody, jejichž autorovi důvěřuješ. Izolace závislostí
a namespaced registry chrání kompatibilitu, nejsou však bezpečnostním sandboxem.
