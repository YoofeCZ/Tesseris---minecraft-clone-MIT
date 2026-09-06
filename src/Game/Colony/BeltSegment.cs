namespace Tesseris.Game.Colony;

/// <summary>Jeden item na pásu. Přesně sekce 6.2.</summary>
/// <remarks>
/// <b>Gap je vzdálenost od PŘEDCHOZÍHO itemu</b>, ne absolutní poloha. Na tom stojí celý
/// trik: posun celého pásu je dekrement jediného čísla.
/// </remarks>
public struct BeltSlot
{
    /// <summary>Vzdálenost od předchozího itemu; u prvního od výstupního konce pásu.</summary>
    public ushort Gap;

    /// <summary>Co to je za item.</summary>
    public ushort ItemId;
}

/// <summary>
/// Pás jako transport line. Nikdy ne kontejner, nikdy ne entita per item.
/// </summary>
/// <remarks>
/// <para><b>Tohle je nejdůležitější rozhodnutí v celé codebase</b>.
/// Bez něj spadneme na 2 000 itemech.</para>
///
/// <para><b>Proč to je O(1).</b> Itemy si nedrží absolutní polohu, ale vzdálenost od toho
/// před sebou. Posunout celý pás proto znamená zmenšit <see cref="BeltSlot.Gap"/> u prvního
/// itemu — všichni ostatní se tím posunou taky, protože jsou vázaní na něj. Plný pás
/// o 5 000 itemech stojí za tick stejně jako pás o třech.</para>
///
/// <para><b>Kruhový buffer.</b> Item odchází zepředu a přichází zezadu. V obyčejném poli by
/// odebrání zepředu posunulo celý zbytek, tedy O(n) na každý odebraný item. Kruhový buffer
/// z toho dělá O(1).</para>
///
/// <para><b>Jednotka délky není blok.</b> Pás se dělí na jemné podkroky
/// (<see cref="StepsPerCell"/>), aby se itemy pohybovaly plynule a daly se mezi ně dělat
/// rozumné rozestupy. Celé číslo, ne float — pravidlo 6.6 chce determinismus.</para>
/// </remarks>
public sealed class BeltSegment
{
    /// <summary>Na kolik podkroků se dělí jedna buňka pásu.</summary>
    public const int StepsPerCell = 16;

    /// <summary>Nejmenší rozestup mezi itemy v podkrocích.</summary>
    public const int MinimumSpacing = 4;

    private BeltSlot[] _slots;
    private int _head;
    private int _count;

    /// <summary>Součet všech mezer, tedy poloha posledního itemu od výstupu.</summary>
    private int _totalGap;

    public BeltSegment(int cells, int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cells, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Cells = cells;
        Length = cells * StepsPerCell;
        _slots = new BeltSlot[capacity];
    }

    /// <summary>
    /// Kdo z tohohle pásu odebírá. Index stroje, nebo −1.
    /// </summary>
    /// <remarks>
    /// <b>Obyčejné číslo, ne callback.</b> Stroj, který čeká na surovinu, spí (pravidlo 6.4)
    /// a musí ho někdo probudit, až item dojede. Delegát by znamenal alokaci uzávěru na
    /// každý pás a pravidlo 6.5 ho v tick cestě zakazuje; hledat konzumenta procházením
    /// všech strojů by při třech tisících strojů bylo ještě horší.
    /// </remarks>
    public int ConsumerTag { get; set; } = -1;

    /// <summary>Který vkládač z tohohle pásu bere. Index, nebo −1.</summary>
    /// <remarks>
    /// Zvlášť od <see cref="ConsumerTag"/> schválně: z jednoho pásu může brát stroj napřímo
    /// i vkládač a oba spí, takže oba potřebují vlastní probuzení.
    /// </remarks>
    public int InserterTag { get; set; } = -1;

    /// <summary>Délka pásu v buňkách.</summary>
    public int Cells { get; }

    /// <summary>Délka pásu v podkrocích.</summary>
    public int Length { get; }

    /// <summary>Kolik itemů na pásu leží.</summary>
    public int Count => _count;

    /// <summary>Je na pásu první item už na výstupu a čeká na odebrání?</summary>
    public bool FrontIsReady => _count > 0 && _slots[_head].Gap == 0;

    /// <summary>Co je na výstupu. Platí jen když <see cref="FrontIsReady"/>.</summary>
    public ushort FrontItem => _slots[_head].ItemId;

    /// <summary>
    /// Posune celý pás o jeden podkrok.
    /// </summary>
    /// <remarks>
    /// <b>Jedna operace bez ohledu na počet itemů.</b> Tohle je celá pointa transport line.
    /// Když první item dojel na výstup (mezera nula), pás stojí — čeká, až si ho někdo vezme,
    /// a tlačí se za ním zbytek.
    /// </remarks>
    public void Tick()
    {
        if (_count == 0)
        {
            return;
        }

        ref BeltSlot front = ref _slots[_head];
        if (front.Gap == 0)
        {
            return;
        }

        front.Gap--;
        _totalGap--;
    }

    /// <summary>Vejde se na zadní konec ještě jeden item?</summary>
    public bool CanPush => _count == 0 || Length - _totalGap >= MinimumSpacing;

    /// <summary>
    /// Položí item na zadní konec pásu.
    /// </summary>
    /// <returns>false, když tam není místo.</returns>
    public bool TryPush(ushort itemId)
    {
        if (!CanPush)
        {
            return false;
        }

        if (_count == _slots.Length)
        {
            Grow();
        }

        // Mezera nového itemu je vzdálenost od toho, co je před ním. U prázdného pásu se
        // měří od výstupu, takže item vstupuje na plnou délku a musí celý pás projet.
        int gap = _count == 0 ? Length : Length - _totalGap;

        int tail = (_head + _count) % _slots.Length;
        _slots[tail] = new BeltSlot { Gap = (ushort)gap, ItemId = itemId };
        _totalGap += gap;
        _count++;
        return true;
    }

    /// <summary>
    /// Sebere item z výstupního konce.
    /// </summary>
    /// <returns>false, když tam žádný není nebo ještě nedojel.</returns>
    public bool TryPop(out ushort itemId)
    {
        itemId = 0;
        if (!FrontIsReady)
        {
            return false;
        }

        itemId = _slots[_head].ItemId;

        // Mezera odcházejícího itemu byla nula, takže se ostatním nic přepočítávat nemusí:
        // ten za ním má svou mezeru měřenou od místa, kde tenhle stál — a to je výstup.
        _head = (_head + 1) % _slots.Length;
        _count--;

        if (_count == 0)
        {
            _totalGap = 0;
        }

        return true;
    }

    /// <summary>Item podle pořadí od výstupu. Pro testy a ukládání.</summary>
    public BeltSlot SlotAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);

        return _slots[(_head + index) % _slots.Length];
    }

    /// <summary>
    /// Rozepíše polohy všech itemů v podkrocích od výstupu.
    /// </summary>
    /// <remarks>
    /// Tohle je JEDINÉ místo, kde se platí za počet itemů — a je to vykreslovací cesta,
    /// která stejně musí projít každý viditelný item. Simulace sama absolutní polohy nikdy
    /// nepotřebuje.
    /// </remarks>
    /// <returns>Kolik poloh se zapsalo.</returns>
    public int WritePositions(Span<int> positions)
    {
        int written = 0;
        int offset = 0;

        for (int i = 0; i < _count && i < positions.Length; i++)
        {
            offset += _slots[(_head + i) % _slots.Length].Gap;
            positions[i] = offset;
            written++;
        }

        return written;
    }

    /// <summary>
    /// Obnoví obsah pásu přesně tak, jak byl uložený.
    /// </summary>
    /// <remarks>
    /// <b>Nejde nahradit opakovaným <see cref="TryPush"/>.</b> Ten počítá mezeru sám podle
    /// pravidla o rozestupu, takže by itemy po načtení stály jinde než před uložením — a na
    /// pásu je jejich poloha celý stav. Sloty se proto zapisují doslova.
    /// </remarks>
    public void Restore(ReadOnlySpan<BeltSlot> slots)
    {
        Clear();

        while (_slots.Length < slots.Length)
        {
            Grow();
        }

        int total = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            _slots[i] = slots[i];
            total += slots[i].Gap;
        }

        _count = slots.Length;
        _totalGap = total;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        _totalGap = 0;
    }

    private void Grow()
    {
        var grown = new BeltSlot[_slots.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            grown[i] = _slots[(_head + i) % _slots.Length];
        }

        _slots = grown;
        _head = 0;
    }
}
