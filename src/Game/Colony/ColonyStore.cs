using OpenTK.Mathematics;

namespace Tesseris.Game.Colony;

/// <summary>
/// Sklad kolonie: ví, <b>co</b> v něm leží, ne jen kolik toho je.
/// </summary>
/// <remarks>
/// <para><b>Proč to není <c>int</c>.</b> Do teď byl sklad jedno počítadlo
/// (<c>ColonySimulation.StoredItems</c>): vykopaná ruda nikam nedošla, jen se zvětšilo číslo.
/// Nedalo se podívat, co kolonie má, a hlavně se z toho nedalo nic vzít — bez toho nemůže
/// existovat hlad, protože kolonista nemá kam si dojít pro jídlo.</para>
///
/// <para><b>Struct-of-arrays, ne slovník objektů</b> (pravidlo 6.3). Sahá se sem z tick cesty:
/// každé odevzdané kopnutí, každé jídlo a každý hladový kolonista, který se ptá, jestli je co
/// jíst. Dvě paralelní pole a binární hledání to zvládnou bez alokace i bez hashe; nové pole
/// se alokuje jen tehdy, když do skladu přibude <b>druh</b>, který tam ještě nebyl.</para>
///
/// <para><b>Druhy jsou setříděné podle id, a to schválně.</b> Pořadí pak nezávisí na tom,
/// v jakém sledu se co doneslo, takže sav vyjde po kolečku uložit → načíst → uložit bajt po
/// bajtu stejně. Pořadí podle prvního příchodu by ten test rozbilo při prvním prohození.</para>
///
/// <para><b>Sklad je místo, ne abstrakce.</b> Patří k radnici — je to střed kolonie a hráč si ho
/// umístil sám (viz <see cref="TownHall"/>). Dokud radnice nestojí, sklad polohu nemá: materiál
/// se do něj pořád dá odevzdat, protože ztratit se nesmí nikdy, ale <b>nedá se k němu dojít</b>,
/// takže se v něm nedá ani najíst.</para>
///
/// <para><b>Jídlo se počítá průběžně, ne dopočítává.</b> Hladový kolonista se každý tik ptá,
/// jestli je co jíst. Sčítat kvůli tomu druhy by při dvou stech lidech znamenalo stovky
/// průchodů polem za tik — <see cref="FoodCount"/> se proto udržuje při každé změně.</para>
/// </remarks>
public sealed class ColonyStore
{
    /// <summary>Kolik druhů se vejde, než se pole zvětší. Víc jich kolonie na začátku nemá.</summary>
    private const int InitialKinds = 8;

    private ushort[] _item = new ushort[InitialKinds];
    private int[] _count = new int[InitialKinds];
    private int _kinds;

    /// <summary>Které druhy jsou jídlo. Váže se na jméno přes <see cref="ColonyFood"/>.</summary>
    private ushort[] _food = [];

    /// <summary>Kolik kusů celkem leží ve skladu. Držené průběžně, ať to panel nemusí sčítat.</summary>
    public int Total { get; private set; }

    /// <summary>Kolik jídla ve skladu je. Ptá se na to každý hladový kolonista každý tik.</summary>
    public int FoodCount { get; private set; }

    /// <summary>Kolik různých druhů sklad drží.</summary>
    public int KindCount => _kinds;

    /// <summary>Leží ve skladu aspoň jedno jídlo?</summary>
    public bool HasFood => FoodCount > 0;

    /// <summary>Má sklad místo ve světě? Bez radnice ne.</summary>
    public bool HasCell { get; private set; }

    /// <summary>Kde sklad stojí. Platné jen když <see cref="HasCell"/>.</summary>
    public Vector3i Cell { get; private set; }

    /// <summary>Druh na daném pořadí. Pořadí je setříděné podle id, takže je stabilní.</summary>
    public ushort ItemAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _kinds);
        return _item[index];
    }

    /// <inheritdoc cref="ItemAt"/>
    public int CountAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _kinds);
        return _count[index];
    }

    /// <summary>Kolik kusů daného druhu sklad má.</summary>
    public int CountOf(ushort item)
    {
        int slot = Find(item);
        return slot >= 0 ? _count[slot] : 0;
    }

    /// <summary>Přiřadí skladu místo ve světě. Volá se, když hráč postaví radnici.</summary>
    public void SetCell(Vector3i cell)
    {
        HasCell = true;
        Cell = cell;
    }

    /// <summary>Sklad zase nemá kam patřit. Obsah zůstává — ztratit se nesmí.</summary>
    public void ForgetCell()
    {
        HasCell = false;
        Cell = default;
    }

    /// <summary>
    /// Řekne skladu, které druhy jsou jídlo.
    /// </summary>
    /// <remarks>
    /// <b>Vždycky přes jméno, nikdy natvrdo číslem</b>. Id bloků se mění
    /// s obsahem, takže se jídlo překládá z <see cref="ColonyFood.Names"/> a sem přijde až
    /// výsledek. Kopie je vlastní, aby ji nešlo změnit zvenku pod rukama.
    /// </remarks>
    public void SetFood(ReadOnlySpan<ushort> food)
    {
        _food = food.ToArray();

        // Jídlo se může přihlásit až po načtení savu, takže se počítadlo musí dopočítat teď.
        // Jinak by sklad plný jablek tvrdil, že jídlo nemá.
        FoodCount = 0;
        for (int slot = 0; slot < _kinds; slot++)
        {
            if (IsFood(_item[slot]))
            {
                FoodCount += _count[slot];
            }
        }
    }

    /// <summary>Je tenhle druh jídlo?</summary>
    public bool IsFood(ushort item)
    {
        for (int i = 0; i < _food.Length; i++)
        {
            if (_food[i] == item)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Uloží materiál.</summary>
    public void Add(ushort item, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
        {
            return;
        }

        int slot = Find(item);
        if (slot >= 0)
        {
            _count[slot] += count;
        }
        else
        {
            Insert(~slot, item, count);
        }

        Total += count;

        if (IsFood(item))
        {
            FoodCount += count;
        }
    }

    /// <summary>
    /// Vezme ze skladu.
    /// </summary>
    /// <returns>false, když tolik kusů toho druhu ve skladu není. Sklad zůstane nedotčený.</returns>
    public bool TryTake(ushort item, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int slot = Find(item);
        if (slot < 0 || _count[slot] < count)
        {
            return false;
        }

        _count[slot] -= count;
        Total -= count;

        if (IsFood(item))
        {
            FoodCount -= count;
        }

        // Vyčerpaný druh z evidence pryč, jinak by sklad rostl o každou položku, která tudy
        // jen prošla, a panel by ukazoval řádky s nulou. Posun je levný: druhů jsou desítky.
        if (_count[slot] == 0)
        {
            Remove(slot);
        }

        return true;
    }

    /// <summary>
    /// Vezme jedno jídlo.
    /// </summary>
    /// <remarks>
    /// Prochází se pořadí <b>druhů</b>, ne pořadí jídel: druhy jsou setříděné, takže je výběr
    /// deterministický (pravidlo 6.6) a nezávisí na tom, jak se poskládal seznam jídel.
    /// </remarks>
    public bool TryTakeFood(out ushort item)
    {
        for (int slot = 0; slot < _kinds; slot++)
        {
            if (_count[slot] > 0 && IsFood(_item[slot]))
            {
                item = _item[slot];
                return TryTake(item);
            }
        }

        item = default;
        return false;
    }

    /// <summary>Vyprázdní sklad. Volá se před načtením savu.</summary>
    /// <remarks>Místo zůstává: to patří radnici, a ta se buď načte, nebo pořád stojí.</remarks>
    public void Clear()
    {
        _kinds = 0;
        Total = 0;
        FoodCount = 0;
    }

    /// <summary>Binární hledání. Vrací index, nebo dvojkový doplněk místa, kam druh patří.</summary>
    private int Find(ushort item)
    {
        int low = 0;
        int high = _kinds - 1;

        while (low <= high)
        {
            int middle = (low + high) >> 1;
            ushort value = _item[middle];

            if (value == item)
            {
                return middle;
            }

            if (value < item)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    private void Insert(int at, ushort item, int count)
    {
        if (_kinds == _item.Length)
        {
            Array.Resize(ref _item, _item.Length * 2);
            Array.Resize(ref _count, _count.Length * 2);
        }

        for (int i = _kinds; i > at; i--)
        {
            _item[i] = _item[i - 1];
            _count[i] = _count[i - 1];
        }

        _item[at] = item;
        _count[at] = count;
        _kinds++;
    }

    private void Remove(int at)
    {
        for (int i = at; i < _kinds - 1; i++)
        {
            _item[i] = _item[i + 1];
            _count[i] = _count[i + 1];
        }

        _kinds--;
    }
}
