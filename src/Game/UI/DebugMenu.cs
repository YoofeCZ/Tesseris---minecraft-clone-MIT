using System.Globalization;

namespace Tesseris.Game.UI;

/// <summary>
/// Vývojářské menu pro ladění za běhu.
///
/// <para>Je to seznam řádků, kde každý řádek umí přečíst svou hodnotu a posunout ji o krok.
/// Menu samo neví, co ta hodnota znamená — jen ji zobrazí a zavolá zpátky. Díky tomu se
/// nový přepínač přidá jedním řádkem tam, kde se menu skládá, a nemusí se sahat sem.</para>
///
/// <para><b>Bez GUI knihovny.</b> Kreslí se stejným textovým rendererem jako F3 overlay,
/// takže nepřibyla žádná závislost. Zadání původně GUI framework zakazovalo; tohle menu
/// vzniklo na výslovné vyžádání a žádný framework nepoužívá.</para>
/// </summary>
public sealed class DebugMenu
{
    private readonly List<Row> _rows = [];

    /// <summary>Jeden řádek menu: popisek, čtení hodnoty a posun o krok.</summary>
    /// <param name="Adjust">Dostane směr (−1 nebo +1) a násobitel kroku.</param>
    /// <param name="Fraction">
    /// Kde hodnota leží mezi svým minimem a maximem, 0 až 1. Slouží k vykreslení posuvníku.
    /// U řádků bez rozsahu (jen ke čtení) je <c>null</c> a posuvník se nekreslí.
    /// </param>
    public sealed record Row(
        string Label,
        Func<string> Read,
        Action<int, int> Adjust,
        Func<float>? Fraction = null);

    public bool Visible { get; set; }

    public int Selected { get; private set; }

    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>Přidá řádek s číselnou hodnotou.</summary>
    public void AddInt(string label, Func<int> read, Action<int> write, int step, int min, int max) =>
        _rows.Add(new Row(
            label,
            () => read().ToString(CultureInfo.InvariantCulture),
            (direction, multiplier) => write(Math.Clamp(read() + (direction * step * multiplier), min, max)),
            () => Normalize(read(), min, max)));

    /// <summary>Přidá řádek s desetinnou hodnotou.</summary>
    public void AddFloat(string label, Func<float> read, Action<float> write, float step, float min, float max) =>
        _rows.Add(new Row(
            label,
            () => read().ToString("F2", CultureInfo.InvariantCulture),
            (direction, multiplier) => write(Math.Clamp(read() + (direction * step * multiplier), min, max)),
            () => Normalize(read(), min, max)));

    /// <summary>Přidá přepínač.</summary>
    public void AddToggle(string label, Func<bool> read, Action<bool> write) =>
        _rows.Add(new Row(
            label,
            () => read() ? "zapnuto" : "vypnuto",
            (_, _) => write(!read()),
            () => read() ? 1f : 0f));

    /// <summary>Podíl hodnoty v jejím rozsahu. Prázdný rozsah dá nulu, ne dělení nulou.</summary>
    private static float Normalize(float value, float minimum, float maximum)
    {
        float span = maximum - minimum;
        return span > 0f ? Math.Clamp((value - minimum) / span, 0f, 1f) : 0f;
    }

    /// <summary>Přidá řádek jen ke čtení. Slouží k zobrazení naměřených hodnot.</summary>
    public void AddReadOnly(string label, Func<string> read) =>
        _rows.Add(new Row(label, read, (_, _) => { }));

    public void Move(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        // Modulo se dvěma kroky: v C# dává záporný operand záporný zbytek.
        Selected = ((Selected + delta) % _rows.Count + _rows.Count) % _rows.Count;
    }

    public void Adjust(int direction, int multiplier)
    {
        if (_rows.Count > 0)
        {
            _rows[Selected].Adjust(direction, multiplier);
        }
    }

    /// <summary>
    /// Sestaví řádky s posuvníkem, tedy: kurzor, popisek, <c>-</c>, pruh, <c>+</c> a hodnota.
    /// </summary>
    /// <remarks>
    /// <para>Pruh se skládá ze znaků, protože se kreslí týmž textovým rendererem jako
    /// zbytek overlaye. Grafický posuvník by znamenal vlastní pipeline a novou závislost
    /// jen kvůli obdélníku.</para>
    ///
    /// <para><b>Bez diakritiky.</b> Font pokrývá ASCII 32–126 a nic víc; písmeno s háčkem
    /// se nevykreslí vůbec. Popisky se proto píšou bez ní.</para>
    /// </remarks>
    /// <param name="labelWidth">Na kolik znaků se zarovná popisek.</param>
    /// <param name="barWidth">Z kolika dílků je pruh posuvníku.</param>
    public IEnumerable<string> SliderLines(int labelWidth = 20, int barWidth = 10)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            char cursor = i == Selected ? '>' : ' ';
            string label = row.Label.PadRight(Math.Max(labelWidth, 0));

            if (row.Fraction is null)
            {
                // Nadpis oddílu nebo naměřená hodnota. Posuvník by tvrdil, že se s tím
                // dá hýbat — místo něj jdou mezery, aby hodnoty zůstaly ve sloupci.
                yield return $"{cursor} {label} {new string(' ', barWidth + 4)}{row.Read()}";
                continue;
            }

            int filled = (int)MathF.Round(Math.Clamp(row.Fraction(), 0f, 1f) * barWidth);
            string bar = new string('#', filled) + new string('-', Math.Max(barWidth - filled, 0));
            yield return $"{cursor} {label} -[{bar}]+ {row.Read()}";
        }
    }

    /// <summary>Sestaví řádky k vykreslení, včetně kurzoru u vybraného.</summary>
    public IEnumerable<string> Lines()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"{(i == Selected ? '>' : ' ')} {_rows[i].Label,-22} {_rows[i].Read()}");
        }
    }
}
