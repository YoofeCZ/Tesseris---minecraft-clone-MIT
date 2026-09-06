using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;

namespace Tesseris.Game.World;

/// <summary>
/// Řídká sada chunků adresovaná souřadnicemi chunku.
///
/// Svět je neomezený do všech stran; chunk, který neexistuje, se chová jako plný vzduchu.
///
/// <para><b>Pravidla souběhu.</b> Chunk se plní na worker vlákně, ale do světa se vloží až
/// hotový — vložení do <see cref="ConcurrentDictionary{TKey, TValue}"/> je bod zveřejnění.
/// Od té chvíle se s ním do fáze F3 už nehýbe, takže ho můžou ostatní workery bezpečně číst
/// při meshování. Jakmile ve F3 přibude úprava bloků, tenhle předpoklad přestane platit
/// a bude potřeba verzování nebo kopie.</para>
/// </summary>
public sealed class VoxelWorld
{
    private readonly ConcurrentDictionary<Vector3i, Chunk> _chunks = new();
    private readonly ushort _water;

    public VoxelWorld(BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
        _water = registry.TryIndexOf("tesseris:water", out ushort water)
            ? water
            : BlockRegistry.Air;
        MicroShapes = new MicroShapeCache(registry);
    }

    public BlockRegistry Registry { get; }

    /// <summary>Sdílená zásoba otesaných tvarů. Používá ji meshing i výpočet kolizí.</summary>
    public MicroShapeCache MicroShapes { get; }

    public int ChunkCount => _chunks.Count;

    /// <summary>
    /// Souřadnice chunků, které svět právě drží.
    ///
    /// Prochází se líně přes enumerátor slovníku, ne přes jeho <c>Keys</c> — ta vlastnost
    /// pokaždé vyrobí kopii celého seznamu klíčů, což by při volání každý frame znamenalo
    /// desítky kilobajtů alokací na frame. Enumerátor <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// je bezpečný i při souběžné změně; nezaručuje ale snímek v jednom okamžiku.
    /// </summary>
    public IEnumerable<Vector3i> ChunkPositions
    {
        get
        {
            foreach (KeyValuePair<Vector3i, Chunk> pair in _chunks)
            {
                yield return pair.Key;
            }
        }
    }

    /// <summary>Souřadnice chunku, do kterého spadá světová souřadnice bloku.</summary>
    public static Vector3i ToChunkPosition(int x, int y, int z) =>
        // Aritmetický posun dělí se zaokrouhlením dolů i pro záporná čísla, na rozdíl od '/'.
        new(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    /// <summary>Ohraničující kvádr chunku ve světových souřadnicích.</summary>
    public static Aabb ChunkBounds(Vector3i chunkPosition)
    {
        var min = new Vector3(
            chunkPosition.X * Chunk.Size,
            chunkPosition.Y * Chunk.Size,
            chunkPosition.Z * Chunk.Size);

        return new Aabb(min, min + new Vector3(Chunk.Size));
    }

    public Chunk? GetChunk(Vector3i position) => _chunks.GetValueOrDefault(position);

    public bool HasChunk(Vector3i position) => _chunks.ContainsKey(position);

    /// <summary>Zveřejní hotový chunk. Volá se z worker vlákna po dokončení generování.</summary>
    public bool TryAddChunk(Vector3i position, Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return _chunks.TryAdd(position, chunk);
    }

    /// <summary>Zahodí chunk. Volá se z hlavního vlákna při uvolňování dohledu.</summary>
    public bool RemoveChunk(Vector3i position) => _chunks.TryRemove(position, out _);

    /// <summary>Blok na světové souřadnici. Mimo existující chunky vrací vzduch.</summary>
    public ushort GetBlock(int x, int y, int z)
    {
        Chunk? chunk = GetChunk(ToChunkPosition(x, y, z));
        return chunk is null
            ? BlockRegistry.Air
            : chunk.GetBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
    }

    /// <summary>
    /// Zapíše blok na světovou souřadnici a v případě potřeby založí chunk.
    ///
    /// <para>Volá se <b>jen z hlavního vlákna</b>. Změna se nedělá do zveřejněného chunku,
    /// ale do jeho kopie, která se pak vymění najednou — rozpracovaná meshovací úloha tak
    /// dál pracuje se souvislým starým stavem místo aby jí data měnila pod rukama.
    /// Kopie stojí jednotky až desítky kilobajtů, což je při ručních úpravách zanedbatelné.</para>
    /// </summary>
    /// <returns>Souřadnice chunku, kterého se změna týkala.</returns>
    public Vector3i SetBlock(int x, int y, int z, ushort block)
    {
        Vector3i position = ToChunkPosition(x, y, z);
        int localX = x & Chunk.SizeMask;
        int localY = y & Chunk.SizeMask;
        int localZ = z & Chunk.SizeMask;

        while (true)
        {
            if (!_chunks.TryGetValue(position, out Chunk? current))
            {
                var created = new Chunk();
                created.SetBlock(localX, localY, localZ, block);
                created.MarkModified();

                if (_chunks.TryAdd(position, created))
                {
                    return position;
                }

                continue;
            }

            ushort currentBlock = current.GetBlock(localX, localY, localZ);
            ushort replacement = ReplacementFor(currentBlock, block);

            if (currentBlock == replacement)
            {
                return position;
            }

            Chunk edited = current.Clone();
            edited.SetBlock(localX, localY, localZ, replacement);

            // NOVÝ BLOK NESMÍ ZDĚDIT NIC PO TOM, CO TU BYLO PŘEDTÍM — ani masku dílků, ani
            // druhou vrstvu materiálů.
            //
            // Vytěžíš kmen a postavíš na to místo jiný blok: bez tohohle by zdědil masku
            // i listí, které kolem kmene bylo, a vznikl by z něj děravý útvar ve tvaru T
            // s cizí texturou. Přesně tak to ve hře vypadalo.
            edited.SetPieces(localX, localY, localZ, Registry.DefaultPieces(replacement));
            edited.SetExtra(localX, localY, localZ, 0, PieceMask.Empty);

            // Označení patří sem, do editační cesty, ne do Chunk.SetBlock — ten volá
            // i generátor a ten svoje chunky ukládat nemá, jsou spočitatelné ze seedu.
            edited.MarkModified();

            if (_chunks.TryUpdate(position, edited, current))
            {
                return position;
            }
        }
    }

    /// <summary>
    /// Zapíše najednou celou skupinu bloků a vrátí chunky, kterých se to dotklo.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč to nejde přes <see cref="SetBlock"/> v cyklu.</b> Každý zápis si klonuje
    /// celý chunk, aby rozpracovaná meshovací úloha dál viděla souvislý starý stav. U jedné
    /// úpravy je to zanedbatelné, ale vyrostlý strom má přes dvě stě bloků — bylo by z toho
    /// přes dvě stě kopií chunku po desítkách kilobajtů, tedy jednotky až desítky megabajtů
    /// zkopírovaných kvůli jednomu stromu.</para>
    ///
    /// <para>Tady se zápisy roztřídí podle chunku a každý chunk se klonuje <b>jednou</b>.
    /// Strom se dotkne osmi až šestnácti chunků místo dvou set.</para>
    ///
    /// <para>Atomicita platí na chunk, ne na celou skupinu: strom přes hranici chunku se
    /// může na okamžik objevit napůl. Na obraze to není vidět, protože přemeshování stejně
    /// běží až po celé skupině.</para>
    /// </remarks>
    public IReadOnlyCollection<Vector3i> SetBlocks(IReadOnlyList<(Vector3i At, ushort Block)> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);

        Dictionary<Vector3i, List<(Vector3i At, ushort Block)>> byChunk = [];

        foreach ((Vector3i at, ushort block) in writes)
        {
            Vector3i position = ToChunkPosition(at.X, at.Y, at.Z);

            if (!byChunk.TryGetValue(position, out List<(Vector3i, ushort)>? list))
            {
                list = [];
                byChunk[position] = list;
            }

            list.Add((at, block));
        }

        foreach ((Vector3i position, List<(Vector3i At, ushort Block)> list) in byChunk)
        {
            ApplyToChunk(position, list);
        }

        return byChunk.Keys;
    }

    /// <summary>Vloží skupinu zápisů do jednoho chunku jedinou výměnou.</summary>
    private void ApplyToChunk(Vector3i position, List<(Vector3i At, ushort Block)> writes)
    {
        while (true)
        {
            if (!_chunks.TryGetValue(position, out Chunk? current))
            {
                var created = new Chunk();
                Write(created, writes);
                created.MarkModified();

                if (_chunks.TryAdd(position, created))
                {
                    return;
                }

                continue;
            }

            Chunk edited = current.Clone();
            Write(edited, writes);
            edited.MarkModified();

            if (_chunks.TryUpdate(position, edited, current))
            {
                return;
            }
        }
    }

    /// <summary>Zápisy do konkrétního chunku. Ruší masku i druhou vrstvu, stejně jako <see cref="SetBlock"/>.</summary>
    private void Write(Chunk chunk, List<(Vector3i At, ushort Block)> writes)
    {
        foreach ((Vector3i at, ushort block) in writes)
        {
            int localX = at.X & Chunk.SizeMask;
            int localY = at.Y & Chunk.SizeMask;
            int localZ = at.Z & Chunk.SizeMask;

            ushort current = chunk.GetBlock(localX, localY, localZ);
            chunk.SetBlock(localX, localY, localZ, ReplacementFor(current, block));
            chunk.SetPieces(localX, localY, localZ, Registry.DefaultPieces(block));
            chunk.SetExtra(localX, localY, localZ, 0, PieceMask.Empty);
        }
    }

    /// <summary>
    /// Odstranění vodní rostliny atomicky odhalí vodu, kterou obsahovala. V jediném
    /// zveřejněném stavu tak nikdy nevznikne mezilehlý voxel vzduchu ani viditelná bublina.
    /// </summary>
    private ushort ReplacementFor(ushort current, ushort requested) =>
        requested == BlockRegistry.Air && Registry.IsAquatic(current) && _water != BlockRegistry.Air
            ? _water
            : requested;

    /// <summary>
    /// Nejvyšší sloupec rostliny, který se může sesypat najednou. Chaluha roste do pěti pater;
    /// mez je tu proto, aby se z jediné chyby v datech nestala nekonečná smyčka.
    /// </summary>
    private const int MaxPlantColumn = 24;

    /// <summary>
    /// Sesype rostliny, které nad zadanou souřadnicí ztratily podklad.
    ///
    /// <para><b>Volá se po každé změně bloku.</b> Tráva ani kytka nesmí zůstat viset nad
    /// dírou — když se odtěží hlína pod trsem, trs se rozpadne. Postupuje se sloupcem nahoru,
    /// takže se sesype i vícepatrová chaluha, které se odebral kořen.</para>
    /// </summary>
    /// <param name="x">Souřadnice bloku, který se právě změnil. Řeší se to, co je nad ním.</param>
    /// <param name="onRemoved">
    /// Volitelná reakce na každý sesypaný blok. Dostane jeho souřadnici i původní materiál,
    /// aby herní vrstva mohla vytvořit předmětový drop ještě poté, co blok ze světa zmizí.
    /// </param>
    /// <returns>Kolik rostlin se rozpadlo.</returns>
    public int DropUnsupportedPlants(
        int x, int y, int z, Action<Vector3i, ushort>? onRemoved = null)
    {
        int removed = 0;

        for (int step = 1; step <= MaxPlantColumn; step++)
        {
            int above = y + step;
            ushort plant = GetBlock(x, above, z);

            if (plant == BlockRegistry.Air || !Registry.NeedsGround(plant))
            {
                break;
            }

            if (Registry.CanStandOn(plant, GetBlock(x, above - 1, z)))
            {
                break;
            }

            var at = new Vector3i(x, above, z);

            SetBlock(at.X, at.Y, at.Z, BlockRegistry.Air);
            SetMicro(at.X, at.Y, at.Z, null);
            onRemoved?.Invoke(at, plant);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Vybere voxel pro běžné pokládání na zasažený blok.
    /// </summary>
    /// <remarks>
    /// <para>Běžný stavební blok nahradí přímo zasaženou průchozí vegetaci. Rostlina má
    /// kvůli svému křížovému meshi pouze umělou vodorovnou normálu; slepé použití souseda
    /// proto dříve zapisovalo do terénu vedle ní a hráči přitom mizely kusy z inventáře.</para>
    ///
    /// <para>Pokládané rostliny dál používají sousední voxel podle normály. Květina, sazenice
    /// nebo chaluha se tak připojí k povrchu namísto nahrazení bloku, na který hráč míří.</para>
    /// </remarks>
    public Vector3i PlacementTarget(Vector3i hit, Vector3i normal, ushort placed)
    {
        ushort struck = GetBlock(hit.X, hit.Y, hit.Z);

        return Registry.IsReplaceableVegetation(struck) && !Registry.NeedsGround(placed)
            ? hit
            : hit + normal;
    }

    /// <summary>
    /// Smí na tuhle souřadnici přijít zadaný blok?
    /// </summary>
    /// <remarks>
    /// Cíl musí být skutečně nahraditelný a nový blok se musí lišit od starého. Rostlina
    /// navíc potřebuje svůj podklad. Kontrola probíhá před odečtením inventáře, takže pokus
    /// o zápis do obsazeného voxelu nemůže spotřebovat předmět za neviditelný no-op.
    /// </remarks>
    public bool CanPlace(ushort block, int x, int y, int z)
    {
        ushort current = GetBlock(x, y, z);

        if (current == block || !Registry.IsReplaceable(current) || GetMicro(x, y, z) is not null)
        {
            return false;
        }

        bool targetHasWater = Registry.ContainsWater(current);
        if (Registry.IsAquatic(block))
        {
            // Rostlina sama představuje plný vodní voxel. Vložit ji do mělkého doběhu by
            // z něj udělalo nekonečný zdroj a obraz by do příštího remeshe nesouhlasil
            // se simulací, proto se smí sázet jen do skutečně plné vody.
            if (!targetHasWater || GetFluid(x, y, z) < FluidCell.Source)
            {
                return false;
            }
        }
        else if (Registry.NeedsGround(block) && targetHasWater)
        {
            // Suchozemská květina nesmí vodu tiše nahradit. Pevný blok ji naopak vytlačit smí.
            return false;
        }

        return !Registry.NeedsGround(block)
            || Registry.CanStandOn(block, GetBlock(x, y - 1, z));
    }

    /// <summary>
    /// Položí blok jen tehdy, když cíl projde všemi pravidly, a vrátí, zda se svět změnil.
    /// </summary>
    public bool TryPlace(ushort block, int x, int y, int z) =>
        TryPlace(block, x, y, z, out _);

    /// <summary>
    /// Položí blok a zároveň vrátí původní obsah cíle, ze kterého může herní vrstva vytvořit
    /// předmětový drop. Hodnota <paramref name="replaced"/> je platná i při odmítnutí, ale
    /// drop se smí vytvořit pouze při návratové hodnotě <see langword="true"/>.
    /// </summary>
    public bool TryPlace(ushort block, int x, int y, int z, out ushort replaced)
    {
        replaced = GetBlock(x, y, z);

        if (!CanPlace(block, x, y, z))
        {
            return false;
        }

        SetBlock(x, y, z, block);
        return true;
    }

    /// <summary>
    /// Odebere jeden dílek z bloku rozděleného na dílky.
    ///
    /// <para><b>Strom stojí na dvakrát jemnější mřížce</b>, takže odstranit rovnou celý blok
    /// by ubralo osm dílků najednou — hráč by kopl do kousku kmene a zmizel by mu kus široký
    /// celý blok. Vybere se ten dílek, do kterého paprsek narazil.</para>
    ///
    /// <para>Když po odebrání nezbyde ani jeden dílek, blok zmizí celý.</para>
    /// </summary>
    /// <param name="block">Souřadnice bloku, do kterého paprsek narazil.</param>
    /// <param name="point">Místo zásahu ve světě.</param>
    /// <param name="normal">Normála zasažené stěny. Posune vzorek dovnitř, aby na hraně
    /// nevyšel sousední dílek.</param>
    /// <returns>false, když blok dílky nemá a má se odstranit obvyklou cestou.</returns>
    public bool RemovePiece(Vector3i block, Vector3 point, Vector3i normal)
    {
        byte mask = GetPieces(block.X, block.Y, block.Z);
        ExtraPieces extra = GetExtra(block.X, block.Y, block.Z);

        if (mask == PieceMask.Full && extra.IsEmpty)
        {
            return false;
        }

        // Zásah leží přesně na stěně dílku, takže se vzorek posune o kousek dovnitř tělesa —
        // jinak by zaokrouhlení sáhlo na dílek za stěnou.
        Vector3 inside = point - (new Vector3(normal.X, normal.Y, normal.Z) * 0.01f);

        int i = Math.Clamp((int)((inside.X - block.X) * PieceMask.Steps), 0, PieceMask.Steps - 1);
        int j = Math.Clamp((int)((inside.Y - block.Y) * PieceMask.Steps), 0, PieceMask.Steps - 1);
        int k = Math.Clamp((int)((inside.Z - block.Z) * PieceMask.Steps), 0, PieceMask.Steps - 1);

        // DÍLEK MŮŽE PATŘIT KTERÉKOLI Z OBOU VRSTEV. Uvnitř koruny je v jednom bloku kmen
        // i listí kolem něj — kdyby se sahalo vždycky na hlavní vrstvu, kopnutí do listí
        // by ubralo kus kmene.
        bool inMain = mask != PieceMask.Full && PieceMask.Has(mask, i, j, k);
        bool inExtra = !inMain && !extra.IsEmpty && PieceMask.Has(extra.Mask, i, j, k);

        if (!inMain && !inExtra)
        {
            return false;
        }

        Vector3i position = ToChunkPosition(block.X, block.Y, block.Z);
        int localX = block.X & Chunk.SizeMask;
        int localY = block.Y & Chunk.SizeMask;
        int localZ = block.Z & Chunk.SizeMask;

        while (true)
        {
            if (!_chunks.TryGetValue(position, out Chunk? current))
            {
                return false;
            }

            Chunk edited = current.Clone();

            if (inExtra)
            {
                edited.SetExtra(localX, localY, localZ, extra.Block, PieceMask.Without(extra.Mask, i, j, k));
            }
            else
            {
                byte reduced = PieceMask.Without(mask, i, j, k);

                if (reduced == PieceMask.Empty)
                {
                    // Hlavní materiál došel. Když v bloku zbývá druhá vrstva, převezme blok —
                    // jinak by z listí kolem vytěženého kmene zůstalo prázdno.
                    if (!extra.IsEmpty)
                    {
                        edited.SetBlock(localX, localY, localZ, extra.Block);
                        edited.SetPieces(localX, localY, localZ, extra.Mask);
                        edited.SetExtra(localX, localY, localZ, 0, PieceMask.Empty);
                    }
                    else
                    {
                        edited.SetBlock(localX, localY, localZ, BlockRegistry.Air);
                        edited.SetPieces(localX, localY, localZ, PieceMask.Full);
                    }
                }
                else
                {
                    edited.SetPieces(localX, localY, localZ, reduced);
                }
            }

            edited.MarkModified();

            if (_chunks.TryUpdate(position, edited, current))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Je na téhle souřadnici něco, do čeho jde narazit?
    ///
    /// Otesaný blok má v blokové vrstvě vzduch, takže se musí zvlášť zeptat i mikro vrstvy.
    /// Odpověď je hrubá: říká „tady něco je", ne „tady je plno". Jemnější rozlišení dělá
    /// dvouúrovňový paprsek a kolize proti sloučeným kvádrům.
    /// </summary>
    public bool IsSolid(int x, int y, int z)
    {
        Chunk? chunk = GetChunk(ToChunkPosition(x, y, z));
        if (chunk is null)
        {
            return false;
        }

        int localX = x & Chunk.SizeMask;
        int localY = y & Chunk.SizeMask;
        int localZ = z & Chunk.SizeMask;

        // Ptá se na Solid, ne na „není vzduch": tráva a kytky v bloku jsou, ale projde
        // se jimi. Bez toho by hráč po každém stéblu vyskočil o blok nahoru.
        return Registry.IsSolid(chunk.GetBlock(localX, localY, localZ))
            || chunk.GetMicro(localX, localY, localZ) is not null;
    }

    /// <summary>
    /// Maska dílků bloku. <see cref="PieceMask.Full"/> znamená obyčejný plný blok.
    ///
    /// <para>Nese ji strom, který se sází na dvakrát jemnější mřížce — kmen má poloviční
    /// šířku a listí se ho dotýká. Viz <see cref="PieceMask"/>.</para>
    /// </summary>
    public byte GetPieces(int x, int y, int z)
    {
        Chunk? chunk = GetChunk(ToChunkPosition(x, y, z));

        return chunk?.GetPieces(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)
            ?? PieceMask.Full;
    }

    public bool SetPieces(int x, int y, int z, byte mask)
    {
        Vector3i position = ToChunkPosition(x, y, z);
        while (_chunks.TryGetValue(position, out Chunk? current))
        {
            int lx = x & Chunk.SizeMask, ly = y & Chunk.SizeMask, lz = z & Chunk.SizeMask;
            if (current.GetPieces(lx, ly, lz) == mask) return false;
            Chunk edited = current.Clone();
            edited.SetPieces(lx, ly, lz, mask);
            edited.MarkModified();
            if (_chunks.TryUpdate(position, edited, current)) return true;
        }
        return false;
    }

    /// <summary>Druhý materiál bloku a jeho dílky. Viz <see cref="ExtraPieces"/>.</summary>
    public ExtraPieces GetExtra(int x, int y, int z)
    {
        Chunk? chunk = GetChunk(ToChunkPosition(x, y, z));

        return chunk?.GetExtra(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask) ?? default;
    }

    /// <summary>Mikro obsah bloku, nebo null když otesaný není.</summary>
    public MicroBlock? GetMicro(int x, int y, int z)
    {
        Chunk? chunk = GetChunk(ToChunkPosition(x, y, z));
        return chunk?.GetMicro(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
    }

    /// <summary>
    /// Nastaví mikro obsah bloku. Stejně jako u obyčejné úpravy se mění kopie chunku,
    /// aby rozpracované meshování dál vidělo souvislý stav.
    ///
    /// <para>Když v mikro obsahu nezbyde jediný mikrovoxel, blok se změní na vzduch.
    /// Samotné <c>null</c> ale u voxelu bez mikrovrstvy nic nepřepisuje: volající může
    /// stejným úklidem právě odhalit vodu po odstraněné vodní rostlině.
    /// Když je naopak mřížka celá plná, mikro vrstva se zahodí a zůstane obyčejný blok —
    /// nemá smysl držet 4 kB dat pro tvar, který se od plné krychle neliší.</para>
    /// </summary>
    /// <returns>Souřadnice dotčeného chunku.</returns>
    public Vector3i SetMicro(int x, int y, int z, MicroBlock? micro)
    {
        Vector3i position = ToChunkPosition(x, y, z);
        int localX = x & Chunk.SizeMask;
        int localY = y & Chunk.SizeMask;
        int localZ = z & Chunk.SizeMask;

        while (true)
        {
            if (!_chunks.TryGetValue(position, out Chunk? current))
            {
                return position;
            }

            if (micro is null && current.GetMicro(localX, localY, localZ) is null)
            {
                return position;
            }

            Chunk edited = current.Clone();
            edited.MarkModified();

            if (micro is null || micro.IsEmpty)
            {
                edited.SetMicro(localX, localY, localZ, null);
                edited.SetBlock(localX, localY, localZ, BlockRegistry.Air);
            }
            else if (micro.IsFull && micro.MaterialCount == 1)
            {
                edited.SetMicro(localX, localY, localZ, null);
                edited.SetBlock(localX, localY, localZ, micro.DominantMaterial());
            }
            else
            {
                edited.SetMicro(localX, localY, localZ, micro);

                // Bloková vrstva se nastaví na vzduch. Zní to divně, ale je to podstatné:
                // chunk mesher pracuje jen s blokovou vrstvou a takhle otesaný blok
                //   * nevykreslí jako plnou krychli — o jeho vzhled se stará mikro průchod,
                //   * nezakryje stěny sousedů, takže je skrz díry vidět.
                // Pevnost pro kolize a paprsky se pozná z existence mikro dat, ne odsud.
                edited.SetBlock(localX, localY, localZ, BlockRegistry.Air);
            }

            if (_chunks.TryUpdate(position, edited, current))
            {
                return position;
            }
        }
    }

    /// <summary>
    /// Vyplní odsazený objem 34³ pro meshing: vlastní chunk plus jednovoxelový lem
    /// ze sousedů.
    ///
    /// Sousední chunky se vyhledají jednou dopředu (27 pohledů do slovníku) a pak se sahá
    /// rovnou na ně. Naivní varianta s <see cref="GetBlock"/> pro každý ze 39 304 voxelů
    /// by dělala tolikrát hledání ve slovníku a meshing by se tím výrazně zdržel.
    /// </summary>
    /// <param name="pieces">
    /// Volitelné pole na masky dílků, stejně velké jako <paramref name="destination"/>.
    ///
    /// <para>Musí se kopírovat spolu s bloky a včetně lemu: dílek na hranici chunku se ptá
    /// souseda, jestli ho zakrývá, a bez masky souseda by se na hranici objevila vnitřní
    /// stěna. Kdo dílky neřeší (testy), pole nepředá.</para>
    /// </param>
    public void CopyPadded(
        Vector3i chunkPosition, Span<ushort> destination, Span<byte> pieces = default,
        Span<ushort> extraBlocks = default, Span<byte> extraMasks = default,
        Span<byte> fluid = default)
    {
        if (destination.Length < ChunkMesher.PaddedVolume)
        {
            throw new ArgumentException(
                $"Cíl musí mít aspoň {ChunkMesher.PaddedVolume} prvků.", nameof(destination));
        }

        bool copyPieces = !pieces.IsEmpty;
        bool copyExtra = !extraBlocks.IsEmpty && !extraMasks.IsEmpty;
        bool copyFluid = !fluid.IsEmpty;

        if (copyPieces && pieces.Length < ChunkMesher.PaddedVolume)
        {
            throw new ArgumentException(
                $"Pole masek musí mít aspoň {ChunkMesher.PaddedVolume} prvků.", nameof(pieces));
        }

        Chunk?[] neighbours = new Chunk?[27];
        for (int oy = -1; oy <= 1; oy++)
        {
            for (int oz = -1; oz <= 1; oz++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    neighbours[NeighbourIndex(ox, oy, oz)] =
                        GetChunk(chunkPosition + new Vector3i(ox, oy, oz));
                }
            }
        }

        for (int y = -ChunkMesher.Pad; y < Chunk.Size + ChunkMesher.Pad; y++)
        {
            int oy = Offset(y);
            int ly = y & Chunk.SizeMask;

            for (int z = -ChunkMesher.Pad; z < Chunk.Size + ChunkMesher.Pad; z++)
            {
                int oz = Offset(z);
                int lz = z & Chunk.SizeMask;

                for (int x = -ChunkMesher.Pad; x < Chunk.Size + ChunkMesher.Pad; x++)
                {
                    Chunk? chunk = neighbours[NeighbourIndex(Offset(x), oy, oz)];
                    int lx = x & Chunk.SizeMask;
                    int index = ChunkMesher.PaddedIndex(x, y, z);

                    destination[index] = chunk is null
                        ? BlockRegistry.Air
                        : chunk.GetBlock(lx, ly, lz);

                    if (copyPieces)
                    {
                        pieces[index] = chunk is null ? PieceMask.Full : chunk.GetPieces(lx, ly, lz);
                    }

                    if (copyExtra)
                    {
                        ExtraPieces extra = chunk?.GetExtra(lx, ly, lz) ?? default;
                        extraBlocks[index] = extra.Block;
                        extraMasks[index] = extra.Mask;
                    }

                    if (copyFluid)
                    {
                        // Chybějící chunk i chybějící záznam znamenají plnou vodu. Moře
                        // z generátoru žádnou úroveň zapsanou nemá a jsou to samé zdroje.
                        fluid[index] = chunk is null ? FluidCell.Source : chunk.GetFluid(lx, ly, lz);
                    }
                }
            }
        }
    }

    private static int Offset(int coordinate) => coordinate < 0 ? -1 : coordinate >= Chunk.Size ? 1 : 0;

    private static int NeighbourIndex(int ox, int oy, int oz) => (ox + 1) + ((oz + 1) * 3) + ((oy + 1) * 9);

    /// <summary>Je v tomto voxelu voda, ať už samostatně, nebo uvnitř vodní rostliny?</summary>
    public bool ContainsWater(int x, int y, int z) => Registry.ContainsWater(GetBlock(x, y, z));

    /// <summary>
    /// Položí do světa zdroj kapaliny — plný blok, který nevysychá.
    /// </summary>
    /// <remarks>
    /// Nestačí zapsat blok: <see cref="SetBlock"/> se hladiny nedotkne, takže by nová voda
    /// zdědila úroveň, která na tom místě zbyla po dřívějším doběhu, a vzápětí by zmizela.
    /// </remarks>
    public Vector3i? PlaceFluidSource(int x, int y, int z, ushort liquid)
    {
        Vector3i chunk = ToChunkPosition(x, y, z);

        int index = Chunk.LocalIndex(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);

        return ApplyFluidBatch(chunk, [(index, liquid, FluidCell.Source)]);
    }

    /// <summary>Množství kapaliny v bloku. Mimo načtený svět vrací nulu.</summary>
    public byte GetFluid(int x, int y, int z)
    {
        Chunk? chunk = GetChunk(ToChunkPosition(x, y, z));
        return chunk is null
            ? (byte)0
            : chunk.GetFluid(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
    }

    /// <summary>
    /// Zapíše kapalinu do světa dávkou - blok i jeho množství naráz.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč to nejde přes <see cref="SetBlock"/>.</b> Ten kvůli běžícím meshovacím
    /// úlohám kopíruje celý chunk při každém zápisu. Při ruční úpravě je to zanedbatelné,
    /// ale simulace kapalin mění stovky bloků za tik — a to by bylo stovky kopií po 32 kB
    /// každý tik, tedy desítky megabajtů za vteřinu jen na kopírování.</para>
    ///
    /// <para>Tahle cesta kopíruje chunk JEDNOU pro celou dávku. Volající si tedy musí změny
    /// nejdřív posbírat a poslat je pohromadě; o to, aby v jedné dávce byly jen bloky téhož
    /// chunku, se stará <see cref="FluidSimulation"/>.</para>
    ///
    /// <para>Volá se jen z hlavního vlákna, stejně jako <see cref="SetBlock"/>.</para>
    /// </remarks>
    /// <returns>Souřadnice chunku, do kterého se zapisovalo, nebo null když chunk není načtený.</returns>
    public Vector3i? ApplyFluidBatch(Vector3i chunkPosition, IReadOnlyList<(int Index, ushort Block, byte Amount)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return null;
        }

        while (true)
        {
            if (!_chunks.TryGetValue(chunkPosition, out Chunk? current))
            {
                return null;
            }

            Chunk edited = current.Clone();

            foreach ((int index, ushort block, byte amount) in changes)
            {
                edited.SetBlockRaw(index, block);
                edited.SetFluidRaw(index, amount);
            }

            edited.MarkModified();

            if (_chunks.TryUpdate(chunkPosition, edited, current))
            {
                return chunkPosition;
            }
        }
    }

}
