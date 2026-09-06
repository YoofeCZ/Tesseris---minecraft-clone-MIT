# Nainstalované mody

Tuto složku čte hra. Každý mod je ve vlastní podsložce a obsahuje
`tesseris.mod.json`.

`mods` není obvyklé místo pro vývoj zdrojového kódu. Soubory `.cs` a `.csproj` patří do
`examples` nebo jiného zdrojového projektu; do `mods` se ukládá až výsledná DLL a runtime
obsah. Build může soubory v této složce přepsat.

Ruby ukázku sestaví:

```powershell
dotnet build examples/RubyWorldgenMod/RubyWorldgenMod.csproj -c Release
```

Výsledek vznikne jako `.tmod` pod `examples/RubyWorldgenMod/bin/Release/net8.0/tesseris-mod`.
Do této složky se kopíruje jen explicitním targetem `InstallTesserisMod` s parametrem
`-p:TesserisModsDir="C:\cesta\ke\hře\mods"`.

Rozcestník příkladů je v [`examples/README.cs.md`](../examples/README.cs.md), podrobný český
návod v [`docs/MODDING.md`](../docs/MODDING.md) a anglický v
[`examples/guides/en-US`](../examples/guides/en-US/README.md).

## Spravce modu ve hre

V hlavnim menu otevri tlacitko `MODY` / `MODS`. Seznam ukazuje nazev, verzi, ID,
uroven duvery a stav kazdeho nalezeneho balicku. Kliknutim mod zapnes nebo vypnes.
Povinne zavislosti se upravi automaticky. Tlacitko `POUZIT A RESTARTOVAT` ulozi volbu
do `mods/.tesseris/mod-manager.json` a restartuje hru; vypnuty mod se pri dalsim startu
nenacte ani nespusti.
