using OpenTK.Mathematics;

namespace Tesseris.Game.Colony;

/// <summary>
/// Radnice: blok, kterým hráč zakládá kolonii, a hodiny příchodu kolonistů.
/// </summary>
/// <remarks>
/// <para><b>Bez radnice není kolonie.</b> Dřív se šest kolonistů objevilo samo kolem hráče,
/// jakmile dojela navigace. To je špatně hned dvakrát: hráč nemá co rozhodnout a kolonie
/// nemá střed, ke kterému by patřila. Radnice obojí řeší — je to místo, které si hráč vybral,
/// a je to bod, od kterého se počítá, kam kolonisté přijdou.</para>
///
/// <para><b>Kolonisté přicházejí postupně, ne najednou.</b> Šest lidí naráz nedá hráči šanci
/// pochopit, kdo je kdo a co dělá. Jeden po pár vteřinách je událost, kterou jde sledovat.
/// Interval je proto v ticích a drží ho tenhle typ, ne okno.</para>
///
/// <para><b>Proč tenhle typ, a ne pole v okně.</b> Zakládání kolonie je stav, který se ptá
/// na tři věci: existuje kolonie, kde má střed, a je čas na dalšího člověka. To jsou pravidla
/// hry, takže patří k simulaci, kde je lze testovat bez okna a bez Vulkanu.</para>
/// </remarks>
public sealed class TownHall
{
    /// <summary>Id bloku radnice. Váže se na jméno, ne na číslo.</summary>
    public const string BlockId = "tesseris:town_hall";

    /// <summary>
    /// Kolik tiků uplyne mezi dvěma příchozími.
    /// </summary>
    /// <remarks>
    /// Simulace jede 60 Hz, takže 300 tiků je pět vteřin. Dost dlouho na to, aby byl příchod
    /// vidět jako událost, a ne tak dlouho, aby hráč čekal, než se kolonie vůbec rozjede.
    /// </remarks>
    public const int TicksPerArrival = 300;

    /// <summary>Kolik lidí kolonie unese, dokud nemá čím je uživit.</summary>
    /// <remarks>
    /// Strop je zatím pevný. Až budou potřeby, naváže se na jídlo a lůžka;
    /// do té doby je pevné číslo poctivější než počítat kapacitu z něčeho, co neexistuje.
    /// </remarks>
    public const int MaxColonists = 3;

    // TŘI BEZ VYLEPŠENÍ. Bylo jich dvanáct a chodili donekonečna, takže se z nejvzácnější
    // suroviny stal samospád. Kolik lidí kolonie unese, má být něco,
    // co si hráč vyslouží — strop se zvedne až vylepšením radnice.

    /// <summary>
    /// Kolik lidí smí kolonie mít. Normálně <see cref="MaxColonists"/>, sondou víc.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle je ta věc, kvůli které se nedal změřit cíl ze sekce 8.</b>
    /// <c>VOXELITY_COLONISTS</c> je vedená jako sonda „tolik kolonistů,
    /// kolik je cíl", ale <b>nedělala nic</b>: radnice pouštěla jednoho za 300 tiků se
    /// stropem tři, takže se dvě stě lidí ve hře nedalo sejít ani teoreticky. Výkon při
    /// dvou stech se proto dal ověřit jen testem, nikdy v běžící hře.</para>
    ///
    /// <para><b>Strop hry zůstává tři.</b> Kolik lidí kolonie unese, si má hráč vysloužit
    ///; tohle je měřicí přípravek, ne vylepšení radnice. Čte se jednou
    /// při startu, protože měnit strop uprostřed běhu by znamenalo, že se hra chová jinak
    /// podle toho, kdy se kdo podíval na proměnnou prostředí.</para>
    /// </remarks>
    public static readonly int Capacity = ReadCapacity();

    /// <summary>Interval příchodů. Sonda ho zkrátí, ať se cíl sejde v rozumném čase.</summary>
    /// <remarks>
    /// Při 300 ticích na člověka by dvě stě lidí přicházelo 60 000 tiků, tedy šestnáct minut
    /// herního času — to je delší než jakýkoli selftest. Se zapnutou sondou proto chodí
    /// jeden za tik: měří se výkon při plném počtu, ne trpělivost.
    /// </remarks>
    public static readonly int ArrivalTicks = Capacity > MaxColonists ? 1 : TicksPerArrival;

    private static int ReadCapacity()
    {
        string? value = Environment.GetEnvironmentVariable("VOXELITY_COLONISTS");

        // Nesmysl v proměnné se ignoruje. Spadnout kvůli měřicí sondě by bylo horší než
        // neměřit — a tichý pád při startu se hledá hůř než chybějící číslo.
        if (!int.TryParse(value, out int pozadovany) || pozadovany <= MaxColonists)
        {
            return MaxColonists;
        }

        // Strop nese kapacita ColonySimulation; víc lidí by při Add spadlo na výjimku.
        return Math.Min(pozadovany, 256);
    }

    private int _ticksSinceArrival;

    /// <summary>Je kolonie založená? Bez toho nepřijde nikdo.</summary>
    public bool IsFounded { get; private set; }

    /// <summary>Kde stojí radnice. Platné jen když <see cref="IsFounded"/>.</summary>
    public Vector3i Cell { get; private set; }

    /// <summary>Kolik kolonistů už dorazilo. Pro rozhraní a pro save.</summary>
    public int Arrived { get; private set; }

    /// <summary>
    /// Založí kolonii na dané buňce.
    /// </summary>
    /// <remarks>
    /// Druhá radnice kolonii nepřesouvá. Přesun střediska by znamenal přepočítat, kam všichni
    /// patří, a to je rozhodnutí o hře, ne detail — dokud nepadne, drží se první.
    /// </remarks>
    /// <returns>Založila tahle radnice kolonii?</returns>
    public bool Found(Vector3i cell)
    {
        if (IsFounded)
        {
            return false;
        }

        IsFounded = true;
        Cell = cell;

        // První člověk má přijít hned, ne za pět vteřin ticha. Prázdná kolonie po založení
        // vypadá jako by se nic nestalo.
        _ticksSinceArrival = TicksPerArrival;
        return true;
    }

    /// <summary>Zbourání radnice kolonii ruší i s hodinami příchodů.</summary>
    public void Abandon()
    {
        IsFounded = false;
        Arrived = 0;
        _ticksSinceArrival = 0;
    }

    /// <summary>
    /// Posune hodiny o tik a řekne, jestli má přijít další člověk.
    /// </summary>
    /// <param name="population">Kolik kolonistů kolonie právě má.</param>
    /// <remarks>
    /// Vrací jen doporučení. Jestli je kam člověka postavit, ví svět, ne radnice — a kdyby to
    /// radnice rozhodovala sama, musela by znát mřížku pochůznosti a přestala by být
    /// testovatelná bez světa.
    /// </remarks>
    public bool Tick(int population)
    {
        if (!IsFounded || population >= Capacity)
        {
            return false;
        }

        _ticksSinceArrival++;
        if (_ticksSinceArrival < ArrivalTicks)
        {
            return false;
        }

        _ticksSinceArrival = 0;
        return true;
    }

    /// <summary>Zapíše, že člověk opravdu dorazil. Volá se až po úspěšném postavení.</summary>
    /// <remarks>
    /// Oddělené od <see cref="Tick"/> schválně: když se nikam nevešel, nesmí se to počítat
    /// jako příchod, jinak by kolonie tiše přišla o lidi, na které měla nárok.
    /// </remarks>
    public void CountArrival() => Arrived++;

    /// <summary>Kolik tiků zbývá do dalšího příchozího. Pro rozhraní a sondy.</summary>
    /// <summary>Kolik tiků uplynulo od posledního příchodu. Pro ukládání.</summary>
    public int TicksSinceArrival => _ticksSinceArrival;

    public int TicksUntilArrival =>
        IsFounded ? Math.Max(0, TicksPerArrival - _ticksSinceArrival) : -1;

    /// <summary>Obnoví stav ze savu.</summary>
    public void Restore(Vector3i cell, int arrived, int ticksSinceArrival)
    {
        IsFounded = true;
        Cell = cell;
        Arrived = arrived;
        _ticksSinceArrival = Math.Clamp(ticksSinceArrival, 0, TicksPerArrival);
    }
}
