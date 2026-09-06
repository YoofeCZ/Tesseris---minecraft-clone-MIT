using System.Globalization;
using OpenTK.Mathematics;

namespace Tesseris.Game.UI;

/// <summary>
/// Sazba řádků ladicího overlaye (klávesa F3).
///
/// Zadání říká: FPS, pozice, draw cally. Nic víc tu schválně není — údaje jako počet
/// trojúhelníků nebo obsazená paměť dávají smysl teprve ve fázi, kde je co měřit.
///
/// Metody jsou statické a čisté, aby šly testovat bez GL kontextu. Čísla se sázejí
/// <see cref="CultureInfo.InvariantCulture"/>: na české lokalizaci by se jinak desetinná
/// tečka změnila na čárku a "Pos: -12,34 / 68,00" se špatně čte v řádku, kde je čárka
/// zároveň oddělovač.
/// </summary>
public static class DebugOverlay
{
    /// <summary>
    /// Snímková frekvence a k ní <b>nejhorší snímek</b> za měřené okno.
    /// </summary>
    /// <remarks>
    /// <para>Zaokrouhluje se na celé číslo. Desetina se při každém snímku měnila a číslo
    /// se kvůli ní nedalo přečíst, i když samotný průměr byl klidný.</para>
    ///
    /// <para><b>Nejhorší snímek je tu proto, že průměr záseky schová.</b> Když scéna jede
    /// osm set za vteřinu a jednou za čas cukne na třicet, na průměru to skoro není znát —
    /// ale pozná to každý, kdo se dívá. Tohle číslo ukáže, jak hluboký ten propad byl.</para>
    /// </remarks>
    public static string FpsLine(double fps, double worstFrameMs, StartupLanguage language = StartupLanguage.Czech)
    {
        double worstFps = worstFrameMs > 0.0 ? 1000.0 / worstFrameMs : 0.0;

        string worst = language == StartupLanguage.English ? "worst" : "nejhorsi";
        return string.Create(CultureInfo.InvariantCulture, $"FPS: {fps:F0}   {worst}: {worstFps:F0}");
    }

    public static string PositionLine(Vector3 position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Pos: {position.X:F2} / {position.Y:F2} / {position.Z:F2}");

    public static string DrawCallLine(int drawCalls) =>
        string.Create(CultureInfo.InvariantCulture, $"Draw calls: {drawCalls}");

    /// <summary>Řádek hotbaru: číslo slotu a název vybraného bloku.</summary>
    public static string HotbarLine(int slotNumber, string blockName)
    {
        ArgumentNullException.ThrowIfNull(blockName);
        return string.Create(CultureInfo.InvariantCulture, $"[{slotNumber}] {blockName}");
    }

    /// <summary>Řádek tesání: režim, velikost nástroje a materiál.</summary>
    public static string ChiselLine(string mode, int size, string material, StartupLanguage language = StartupLanguage.Czech)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(material);
        string chisel = language == StartupLanguage.English ? "Chisel" : "Tesani";
        return string.Create(CultureInfo.InvariantCulture, $"{chisel} {mode} /{size} {material}");
    }
}
