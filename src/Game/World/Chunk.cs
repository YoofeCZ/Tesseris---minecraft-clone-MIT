using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;

namespace Tesseris.Game.World;

/// <summary>
/// Krychle 32×32×32 voxelů uložená paletovým kódováním.
///
/// Uvnitř je paleta <see cref="ushort"/> identifikátorů bloků a pole indexů do palety
/// zabalené po 1, 2, 4 nebo 8 bitech podle toho, kolik různých bloků chunk obsahuje.
/// Homogenní chunk (celý kámen, celý vzduch) má paletu o jednom prvku a pole indexů
/// vůbec nealokuje — takových chunků je ve světě většina.
///
/// Spotřeba pole indexů: 4 kB při 1 bitu, 8 kB při 2, 16 kB při 4 a 32 kB při 8 bitech.
///
/// Paleta se nikdy nezmenšuje. Není to potřeba: roste jen při zápisu bloku, který v ní
/// ještě není, a různých typů bloků je v registry řádově desítky. Zaplnit paletu na 256
/// položek by tedy vyžadovalo registry s 256 typy, což je zároveň mez tohohle kódování.
/// </summary>
public sealed class Chunk
{
    public const int Size = 32;
    public const int SizeShift = 5;
    public const int SizeMask = Size - 1;
    public const int Volume = Size * Size * Size;

    /// <summary>Nejvyšší počet různých bloků v jednom chunku, daný osmibitovým indexem.</summary>
    public const int MaxPaletteEntries = 256;

    private ushort[] _palette;
    private int _paletteCount;
    private ulong[]? _indices;
    private int _bitsPerIndex;

    // Mikrovoxely jsou oddělená řídká vrstva. Slovník vznikne teprve tehdy, když do chunku
    // někdo poprvé sekne; do té doby je null a netesané chunky nestojí nic navíc.
    private Dictionary<int, MicroBlock>? _micro;

    // Maska dílků: druhá vrstva vedle bloků, alokovaná líně.
    //
    // NESE JI STROM. Kmen má být poloviční šířky a listí se ho musí dotýkat, což na mřížce
    // celých bloků nejde — mezi sloupkem uprostřed bloku a listím v sousedním bloku zůstane
    // čtvrt bloku vzduchu. Strom se proto sází na DVAKRÁT JEMNĚJŠÍ mřížce a blok si pamatuje,
    // které z jeho osmi dílků jsou vyplněné.
    //
    // JE TO HUSTÉ POLE, NE SLOVNÍK, i když mikro vrstva vedle je slovník. U mikrovoxelů dává
    // slovník smysl: záznam nese 4,1 kB, takže je jeho režie zanedbatelná a otesaných bloků
    // je pár. Tady je záznam JEDEN BAJT a režie slovníku dvacet — v hustém lese, kde má dílky
    // většina bloků chunku, by z toho bylo přes 60 kB proti 32 kB pole, a navíc by meshing
    // dělal skoro čtyřicet tisíc hledání ve slovníku na každý chunk.
    //
    // Chunk bez jediného stromu pole vůbec nealokuje a nestojí ani bajt. Zkoušelo se místo
    // toho zmenšit celý svět na poloviční bloky a stálo to polovinu snímkové frekvence,
    // aniž by se cokoli zlepšilo.
    private byte[]? _pieces;

    /// <summary>
    /// Množství kapaliny v každém bloku, 0 až <see cref="FluidCell.Source"/>.
    ///
    /// <para>Vzniká teprve prvním blokem s neplnou vodou - jezero z generátoru jsou samé
    /// zdroje, takže dokud se ho nikdo nedotkne, pole neexistuje a chunk nestojí ani bajt
    /// navíc. Chybějící pole znamená „každá voda je zdroj".</para>
    /// </summary>
    private byte[]? _fluid;

    // Druhý materiál téhož bloku, řídce.
    //
    // JEDEN MATERIÁL NA BLOK NESTAČÍ. Kmen zabírá jeden dílek a zbytek bloku má být listí —
    // jenže blok nese jen jeden materiál, takže se listí do bloku s kmenem nevešlo a kolem
    // každého kmene zůstala díra o velikosti celého bloku. Přesně tak to ve hře vypadalo.
    //
    // Slovník tady na rozdíl od masky sedí: druhý materiál má jen hrstka bloků na strom,
    // ne většina bloků chunku.
    private Dictionary<int, ExtraPieces>? _extra;

    /// <summary>Vytvoří homogenní chunk vyplněný jediným blokem.</summary>
    public Chunk(ushort fill = BlockRegistry.Air)
    {
        _palette = [fill];
        _paletteCount = 1;
        _indices = null;
        _bitsPerIndex = 0;
    }

    /// <summary>Obsahuje chunk jediný typ bloku? Pak nemá alokované pole indexů.</summary>
    public bool IsHomogeneous => _indices is null;

    /// <summary>
    /// Sáhl do chunku hráč? Jen takové chunky se ukládají na disk.
    ///
    /// <para>Nevygenerované ukládat nemá smysl: generátor je deterministický, takže stejný
    /// chunk ze stejného seedu vyjde vždycky stejně a je levnější ho spočítat znovu než
    /// číst z disku. Ukládá se proto jen to, co se od výsledku generátoru liší.</para>
    ///
    /// <para>Příznak se dědí přes <see cref="Clone"/>, protože editace pracuje s kopií.</para>
    /// </summary>
    public bool IsModified { get; private set; }

    /// <summary>Označí chunk za upravený hráčem. Volá se z editační cesty ve <see cref="VoxelWorld"/>.</summary>
    public void MarkModified() => IsModified = true;

    // Přímý přístup k vnitřkům pro ukládání. Interní schválně: mimo assembly nemá nikdo
    // důvod sahat na paletu ani na zabalené indexy, ale serializace by je jinak musela
    // pracně rozbalovat po voxelech a při načítání znovu balit.
    internal int RawPaletteCount => _paletteCount;

    internal ushort RawPaletteAt(int index) => _palette[index];

    internal ReadOnlySpan<ulong> RawIndices => _indices;

    /// <summary>Poskládá chunk z uložených dat. Protějšek k <see cref="RawIndices"/>.</summary>
    internal static Chunk FromRaw(ushort[] palette, int paletteCount, ulong[]? indices, int bitsPerIndex) =>
        new()
        {
            _palette = palette,
            _paletteCount = paletteCount,
            _indices = indices,
            _bitsPerIndex = bitsPerIndex,
        };

    /// <summary>Blok, kterým je chunk vyplněný. Platí jen když <see cref="IsHomogeneous"/>.</summary>
    public ushort HomogeneousBlock => _palette[0];

    /// <summary>Počet položek palety. Vystaveno kvůli testům a diagnostice.</summary>
    public int PaletteCount => _paletteCount;

    /// <summary>Kolik bitů zabírá jeden index. Nula u homogenního chunku.</summary>
    public int BitsPerIndex => _bitsPerIndex;

    /// <summary>Lineární index voxelu uvnitř chunku. X se mění nejrychleji.</summary>
    public static int LocalIndex(int x, int y, int z) => x | (z << SizeShift) | (y << (SizeShift * 2));

    public ushort GetBlock(int x, int y, int z)
    {
        if (_indices is null)
        {
            return _palette[0];
        }

        return _palette[ReadIndex(_indices, _bitsPerIndex, LocalIndex(x, y, z))];
    }

    public void SetBlock(int x, int y, int z, ushort block)
    {
        if (_indices is null)
        {
            if (_palette[0] == block)
            {
                return;
            }

            SplitFromHomogeneous();
        }

        int paletteIndex = FindOrAddPaletteEntry(block);
        WriteIndex(_indices!, _bitsPerIndex, LocalIndex(x, y, z), paletteIndex);
    }

    /// <summary>
    /// Zápis bloku rovnou na <see cref="LocalIndex"/>, bez převodu ze souřadnic.
    ///
    /// <para>Existuje kvůli dávkovému zápisu kapalin, který si index stejně počítá sám -
    /// počítat ho podruhé by u stovek bloků za tik nebylo zadarmo.</para>
    /// </summary>
    internal void SetBlockRaw(int index, ushort block)
    {
        if (_indices is null)
        {
            if (_palette[0] == block)
            {
                return;
            }

            SplitFromHomogeneous();
        }

        WriteIndex(_indices!, _bitsPerIndex, index, FindOrAddPaletteEntry(block));
    }

    /// <summary>
    /// Vyrobí nezávislou kopii.
    ///
    /// Používá se při úpravě bloku: zveřejněný chunk se nikdy nemění, místo toho vznikne
    /// upravená kopie a ta se do světa vymění najednou. Meshovací úloha, která si mezitím
    /// vzala odkaz na původní chunk, tak dál vidí souvislý stav. Bez toho by se dalo narazit
    /// na okamžik, kdy je pole indexů už osmibitové, ale šířka indexu ještě čtyřbitová —
    /// a z toho vyjde v lepším případě nesmyslný blok, v horším pád na worker vlákně.
    /// </summary>
    public Chunk Clone()
    {
        var copy = new Chunk
        {
            _palette = (ushort[])_palette.Clone(),
            _paletteCount = _paletteCount,
            _indices = (ulong[]?)_indices?.Clone(),
            _bitsPerIndex = _bitsPerIndex,

            // Mělká kopie stačí: MicroBlock se po zveřejnění taky nemění, takže odkazy
            // můžou obě verze chunku klidně sdílet.
            _micro = _micro is null ? null : new Dictionary<int, MicroBlock>(_micro),
            _pieces = (byte[]?)_pieces?.Clone(),
            _fluid = (byte[]?)_fluid?.Clone(),
            _extra = _extra is null ? null : new Dictionary<int, ExtraPieces>(_extra),

            // Příznak se musí dědit: editace se dělá do kopie a ta pak nahradí originál.
            // Bez tohohle by úprava chunku, který už byl jednou uložený, příznak shodila
            // a chunk by se podruhé neuložil.
            IsModified = IsModified,
        };

        return copy;
    }

    /// <summary>
    /// Maska dílků bloku, nebo nula.
    ///
    /// <para>Nula znamená <b>plný blok</b>, ne prázdný — prázdný blok v chunku prostě není.
    /// Díky tomu se bloky bez dílků nemusí nikam zapisovat.</para>
    /// </summary>
    public byte GetPieces(int x, int y, int z) => _pieces is null ? PieceMask.Full : _pieces[LocalIndex(x, y, z)];

    /// <summary>Nastaví masku dílků.</summary>
    public void SetPieces(int x, int y, int z, byte mask) => SetPiecesRaw(LocalIndex(x, y, z), mask);

    /// <summary>Obsahuje chunk aspoň jeden blok rozdělený na dílky?</summary>
    public bool HasPieces => _pieces is not null;

    /// <summary>
    /// Množství kapaliny v bloku. Bez zapsané hodnoty vrací plno - blok vody, kterého se
    /// simulace nedotkla, je plný.
    /// </summary>
    public byte GetFluid(int x, int y, int z) =>
        _fluid is null ? FluidCell.Source : _fluid[LocalIndex(x, y, z)];

    /// <summary>Nastaví množství kapaliny.</summary>
    public void SetFluid(int x, int y, int z, byte amount) => SetFluidRaw(LocalIndex(x, y, z), amount);

    /// <summary>Obsahuje chunk aspoň jeden blok s neplnou kapalinou?</summary>
    public bool HasFluid => _fluid is not null;

    internal void SetFluidRaw(int index, byte amount)
    {
        if (_fluid is null)
        {
            if (amount == FluidCell.Source)
            {
                return;
            }

            // Táž úvaha jako u dílků: pole vzniká prvním neplným blokem a začíná plné,
            // protože chybějící záznam znamená plnou vodu.
            _fluid = new byte[Volume];
            Array.Fill(_fluid, FluidCell.Source);
        }

        _fluid[index] = amount;
    }

    /// <summary>
    /// Hladiny pro ukládání, bez plných bloků. Klíč je <see cref="LocalIndex"/>.
    /// </summary>
    public IEnumerable<KeyValuePair<int, byte>> Fluids
    {
        get
        {
            if (_fluid is null)
            {
                yield break;
            }

            for (int i = 0; i < _fluid.Length; i++)
            {
                if (_fluid[i] != FluidCell.Source)
                {
                    yield return new KeyValuePair<int, byte>(i, _fluid[i]);
                }
            }
        }
    }

    /// <summary>
    /// Masky dílků pro ukládání, bez plných bloků. Klíč je <see cref="LocalIndex"/>.
    ///
    /// <para>Ukládají se jen záznamy, které se od plného bloku liší — těch je i v lese
    /// zlomek, takže se soubor nenafoukne o 32 kB na chunk.</para>
    /// </summary>
    public IEnumerable<KeyValuePair<int, byte>> Pieces
    {
        get
        {
            if (_pieces is null)
            {
                yield break;
            }

            for (int i = 0; i < _pieces.Length; i++)
            {
                if (_pieces[i] != PieceMask.Full)
                {
                    yield return new KeyValuePair<int, byte>(i, _pieces[i]);
                }
            }
        }
    }

    /// <summary>Kolik bloků je rozdělených na dílky.</summary>
    public int PieceCount
    {
        get
        {
            if (_pieces is null)
            {
                return 0;
            }

            int count = 0;

            foreach (byte mask in _pieces)
            {
                if (mask != PieceMask.Full)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Druhý materiál bloku a jeho dílky. Prázdný, když blok druhý materiál nemá.</summary>
    public ExtraPieces GetExtra(int x, int y, int z) =>
        _extra?.GetValueOrDefault(LocalIndex(x, y, z)) ?? default;

    /// <summary>Nastaví druhý materiál bloku. Prázdná maska záznam zase odstraní.</summary>
    public void SetExtra(int x, int y, int z, ushort block, byte mask) =>
        SetExtraRaw(LocalIndex(x, y, z), block, mask);

    /// <summary>Obsahuje chunk aspoň jeden blok s druhým materiálem?</summary>
    public bool HasExtra => _extra is { Count: > 0 };

    /// <summary>Druhé materiály pro ukládání. Klíč je <see cref="LocalIndex"/>.</summary>
    public IEnumerable<KeyValuePair<int, ExtraPieces>> Extras =>
        _extra ?? Enumerable.Empty<KeyValuePair<int, ExtraPieces>>();

    /// <summary>Kolik bloků má druhý materiál.</summary>
    public int ExtraCount => _extra?.Count ?? 0;

    /// <summary>Vloží druhý materiál rovnou na lineární index. Používá načítání z disku.</summary>
    internal void SetExtraRaw(int index, ushort block, byte mask)
    {
        if (mask == PieceMask.Empty || block == BlockRegistry.Air)
        {
            _extra?.Remove(index);
            return;
        }

        _extra ??= [];
        _extra[index] = new ExtraPieces(block, mask);
    }

    /// <summary>Vloží masku rovnou na lineární index. Používá načítání z disku.</summary>
    internal void SetPiecesRaw(int index, byte mask)
    {
        if (_pieces is null)
        {
            if (mask == PieceMask.Full)
            {
                return;
            }

            // Pole vzniká teprve prvním blokem s dílky a začíná plné, protože chybějící
            // záznam znamená obyčejný blok.
            _pieces = new byte[Volume];
            Array.Fill(_pieces, PieceMask.Full);
        }

        _pieces[index] = mask;
    }

    /// <summary>Obsahuje chunk aspoň jeden otesaný blok?</summary>
    public bool HasMicro => _micro is { Count: > 0 };

    /// <summary>Počet otesaných bloků v chunku.</summary>
    public int MicroCount => _micro?.Count ?? 0;

    /// <summary>Otesané bloky a jejich lineární pozice uvnitř chunku.</summary>
    public IEnumerable<KeyValuePair<int, MicroBlock>> MicroBlocks =>
        _micro ?? Enumerable.Empty<KeyValuePair<int, MicroBlock>>();

    /// <summary>Mikro obsah bloku, nebo null když blok otesaný není.</summary>
    public MicroBlock? GetMicro(int x, int y, int z) =>
        _micro?.GetValueOrDefault(LocalIndex(x, y, z));

    /// <summary>
    /// Nastaví nebo zruší mikro obsah bloku. Předání null vrátí blok mezi obyčejné.
    /// Volá se jen na kopii chunku, nikdy na zveřejněné.
    /// </summary>
    public void SetMicro(int x, int y, int z, MicroBlock? micro)
    {
        int index = LocalIndex(x, y, z);

        if (micro is null)
        {
            _micro?.Remove(index);
            return;
        }

        _micro ??= [];
        _micro[index] = micro;
    }

    /// <summary>
    /// Vloží mikro obsah přímo na lineární index. Používá načítání z disku, které index
    /// už má a nemusí ho počítat zpátky ze souřadnic.
    /// </summary>
    internal void SetMicroRaw(int index, MicroBlock micro)
    {
        _micro ??= [];
        _micro[index] = micro;
    }

    /// <summary>Přepíše celý chunk jediným blokem a zahodí pole indexů.</summary>
    public void Fill(ushort block)
    {
        _palette = [block];
        _paletteCount = 1;
        _indices = null;
        _bitsPerIndex = 0;
    }

    /// <summary>
    /// Rozbalí chunk do plochého pole identifikátorů bloků. Používá to mesher, který si
    /// staví odsazený objem a nechce sahat na paletu u každého voxelu zvlášť.
    /// </summary>
    public void CopyTo(Span<ushort> destination)
    {
        if (destination.Length < Volume)
        {
            throw new ArgumentException($"Cíl musí mít aspoň {Volume} prvků.", nameof(destination));
        }

        if (_indices is null)
        {
            destination[..Volume].Fill(_palette[0]);
            return;
        }

        for (int i = 0; i < Volume; i++)
        {
            destination[i] = _palette[ReadIndex(_indices, _bitsPerIndex, i)];
        }
    }

    /// <summary>Přechod z homogenního stavu na jednobitové indexy.</summary>
    private void SplitFromHomogeneous()
    {
        _bitsPerIndex = 1;

        // Samé nuly znamenají "všude položka 0", což je dosavadní výplň — nemusí se nic přepisovat.
        _indices = new ulong[Volume * _bitsPerIndex / 64];

        ushort fill = _palette[0];
        _palette = new ushort[2];
        _palette[0] = fill;
        _paletteCount = 1;
    }

    private int FindOrAddPaletteEntry(ushort block)
    {
        // Lineární hledání: paleta má nejvýš 256 položek a v praxi jednotky, takže se
        // slovník nevyplatí ani pamětí, ani časem.
        for (int i = 0; i < _paletteCount; i++)
        {
            if (_palette[i] == block)
            {
                return i;
            }
        }

        int capacity = 1 << _bitsPerIndex;
        if (_paletteCount == capacity)
        {
            GrowBitsPerIndex();
        }

        if (_paletteCount >= _palette.Length)
        {
            Array.Resize(ref _palette, 1 << _bitsPerIndex);
        }

        _palette[_paletteCount] = block;
        return _paletteCount++;
    }

    /// <summary>Zdvojnásobí šířku indexu a přebalí celé pole.</summary>
    private void GrowBitsPerIndex()
    {
        int newBits = _bitsPerIndex switch
        {
            1 => 2,
            2 => 4,
            4 => 8,
            _ => throw new InvalidOperationException(
                $"Chunk už obsahuje {MaxPaletteEntries} různých bloků, což je mez paletového kódování."),
        };

        ulong[] source = _indices!;
        ulong[] destination = new ulong[Volume * newBits / 64];

        for (int i = 0; i < Volume; i++)
        {
            WriteIndex(destination, newBits, i, ReadIndex(source, _bitsPerIndex, i));
        }

        _indices = destination;
        _bitsPerIndex = newBits;
        Array.Resize(ref _palette, 1 << newBits);
    }

    private static int ReadIndex(ulong[] data, int bits, int index)
    {
        int perWord = 64 / bits;
        int word = index / perWord;
        int shift = (index % perWord) * bits;
        return (int)((data[word] >> shift) & ((1UL << bits) - 1));
    }

    private static void WriteIndex(ulong[] data, int bits, int index, int value)
    {
        int perWord = 64 / bits;
        int word = index / perWord;
        int shift = (index % perWord) * bits;
        ulong mask = ((1UL << bits) - 1) << shift;
        data[word] = (data[word] & ~mask) | ((ulong)(uint)value << shift);
    }
}
