using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;

namespace Tesseris.Game.World;

/// <summary>
/// Kácení stromů a tlení listí.
/// </summary>
/// <remarks>
/// <para><b>Podetnutím kmene padne celý strom.</b> Kácet strom kláda po kládě znamená
/// vyšplhat se k němu — a hlavně to neodpovídá tomu, co člověk od poražení stromu čeká.
/// Sekne se do spodní klády a všechny nad ní se uvolní a spadnou na zem jako předměty.</para>
///
/// <para><b>Listí zůstane viset a teprve pak tlí.</b> Kdyby zmizelo se dřevem, byl by
/// z pádu stromu jen záblesk. Takhle je vidět koruna bez kmene, která se během pár vteřin
/// rozpadá — a při tom pouští, co v ní bylo.</para>
///
/// <para>Tlení je fronta s časem, ne prohledávání světa každý snímek. Prohledávat okolí
/// všech listů by znamenalo procházet tisíce bloků kvůli události, která nastane jednou
/// za minutu.</para>
/// </remarks>
public sealed class TreeFelling
{
    /// <summary>Jak vysoko nad seknutím se ještě hledá kmen.</summary>
    /// <remarks>
    /// Nejvyšší strom má kolem dvaceti kláda; třicet je rezerva. Bez stropu by se šlo
    /// zacyklit na svislé stěně z dřevěných bloků, kterou hráč postaví sám.
    /// </remarks>
    private const int MaxTrunk = 30;

    /// <summary>Kolik bloků od kmene se listí ještě počítá za korunu.</summary>
    private const int LeafReach = 6;

    /// <summary>
    /// Jak dlouho se čeká mezi dvěma zetlelými listy, ve vteřinách.
    /// </summary>
    /// <remarks>
    /// <para><b>Listí tlí jeden po druhém, ne podle rozvrhu.</b> První verze každému listu
    /// spočítala čas dopředu podle vzdálenosti od kmene — jenže listů ve stejné vzdálenosti
    /// jsou desítky, takže se rozpadaly po celých slupkách naráz.</para>
    ///
    /// <para>Teď je to fronta: doběhne odpočet, zetlí <b>jeden</b> list, spustí se odpočet
    /// na další. Koruna se tím rozpadá plynule zvenku... totiž od kmene ven, protože v tom
    /// pořadí se do fronty zařadila.</para>
    ///
    /// <para>Sto listů po desetině vteřiny je deset vteřin na strom, což je akorát: je vidět,
    /// že se to rozpadá, a přitom se na to nečeká.</para>
    /// </remarks>
    private const float DecayInterval = 0.1f;

    /// <summary>
    /// Jak dlouho má trvat rozpad celé koruny, ve vteřinách.
    /// </summary>
    /// <remarks>
    /// <para><b>Rozhoduje celkový čas, ne odstup mezi listy.</b> Pevná desetina vteřiny na
    /// list byla vyladěná na dub, který má listů kolem stovky — jenže smrk a akácie jich mají
    /// pětkrát tolik, takže se jejich koruna rozpadala skoro minutu. Bylo to nahlášeno jako
    /// „akácii se listy nedecayují... až fakt po nějaké době".</para>
    ///
    /// <para>Odstup se proto počítá z délky fronty. Malá koruna se pořád rozpadá po
    /// desetinách, velká zrychlí — a obojí je hotové zhruba ve stejný čas.</para>
    /// </remarks>
    private const float DecayTotalSeconds = 12f;

    /// <summary>Nejkratší možný odstup. Pod ním by z rozpadu bylo zase mrknutí.</summary>
    private const float DecayMinInterval = 0.02f;

    /// <summary>Za jak dlouho po poražení zetlí první list.</summary>
    private const float DecayStart = 0.25f;

    /// <summary>Jak daleko se hledá kmen, který listí drží.</summary>
    private const int SupportReach = 4;

    private readonly BlockRegistry _blocks;
    private readonly ItemRegistry _items;

    /// <summary>Listy čekající na zetlení, v pořadí od kmene ven.</summary>
    private readonly List<Vector3i> _decaying = [];

    /// <summary>Kdy zetlí ten nejbližší na řadě.</summary>
    private float _nextDecay;

    /// <summary>Odstup mezi listy pro rozdělanou korunu. Nula znamená „ještě se nespočítal".</summary>
    private float _interval;

    private float _clock;

    public TreeFelling(BlockRegistry blocks, ItemRegistry items)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _items = items ?? throw new ArgumentNullException(nameof(items));
    }

    /// <summary>Kolik listů čeká na zetlení. Pro přehled a pro test.</summary>
    public int Pending => _decaying.Count;

    /// <summary>Je ten blok kmen?</summary>
    public bool IsLog(ushort block) =>
        block != BlockRegistry.Air && _blocks.Definition(block).Id.EndsWith("_log", StringComparison.Ordinal);

    /// <summary>Je ten blok listí?</summary>
    public bool IsLeaves(ushort block) =>
        block != BlockRegistry.Air && _blocks.Definition(block).Id.EndsWith("_leaves", StringComparison.Ordinal);

    /// <summary>
    /// Porazí strom, jehož spodní kláda je na zadané souřadnici.
    /// </summary>
    /// <returns>Kolik klád spadlo. Nula znamená, že tam strom nebyl.</returns>
    /// <remarks>
    /// Kmen se hledá <b>rovně vzhůru</b>, ne do všech stran. Šikmé větve zatím žádný druh
    /// nemá, a prohledávání do stran by při postavené dřevěné zdi porazilo celou stavbu.
    /// </remarks>
    public int Fell(
        VoxelWorld world, Vector3i bottom, ItemEntities drops, ChunkStreamer streamer)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(streamer);

        ushort log = world.GetBlock(bottom.X, bottom.Y, bottom.Z);

        if (!IsLog(log))
        {
            return 0;
        }

        int felled = 0;

        for (int step = 0; step < MaxTrunk; step++)
        {
            var at = new Vector3i(bottom.X, bottom.Y + step, bottom.Z);

            // Jen TÝŽ druh. Dva stromy, které se dotýkají, se tím neporazí najednou.
            if (world.GetBlock(at.X, at.Y, at.Z) != log)
            {
                break;
            }

            world.SetBlock(at.X, at.Y, at.Z, BlockRegistry.Air);
            world.SetMicro(at.X, at.Y, at.Z, null);
            streamer.InvalidateInteractiveBlock(at.X, at.Y, at.Z);

            int item = _items.ItemForBlock(log);

            if (item != ItemRegistry.Nothing)
            {
                drops.SpawnFromBlock(new ItemStack(item, 1, 0), at, Hash(at));
            }

            felled++;

            MarkLeaves(world, at);
        }

        drops.Merge();

        return felled;
    }

    /// <summary>Přirozeně odumře starý strom: celý kmen i jeho nepodepřená koruna zmizí.</summary>
    /// <remarks>
    /// Bloková řada klád není pád stromu: bez orientace dřeva z ní vznikne řada svislých
    /// špalků a při zrychleném čase celý les vypadá jako kalamita. Dokud nebude existovat
    /// skutečná animovaná entita padajícího stromu, přirozená smrt bezpečně odstraní celý
    /// strom a ekologická vrstva vedle zasadí nástupce. Listí podepřené sousedním stromem
    /// zůstane, aby smrt jednoho stromu nevytrhala koruny celému hustému háji.
    /// </remarks>
    public int FallNaturally(
        VoxelWorld world, Vector3i bottom, Vector2i direction, out IReadOnlyList<Vector3i> changed)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (Math.Abs(direction.X) + Math.Abs(direction.Y) != 1)
        {
            throw new ArgumentException("Směr pádu musí být jedna vodorovná osa.", nameof(direction));
        }

        ushort log = world.GetBlock(bottom.X, bottom.Y, bottom.Z);
        if (!IsLog(log))
        {
            changed = [];
            return 0;
        }

        var trunk = new List<Vector3i>();
        for (int step = 0; step < MaxTrunk; step++)
        {
            var at = new Vector3i(bottom.X, bottom.Y + step, bottom.Z);
            if (world.GetBlock(at.X, at.Y, at.Z) != log)
            {
                break;
            }

            trunk.Add(at);
        }

        var trunkWrites = new List<(Vector3i At, ushort Block)>(trunk.Count);
        var touched = new List<Vector3i>(trunk.Count);

        foreach (Vector3i at in trunk)
        {
            trunkWrites.Add((at, BlockRegistry.Air));
            touched.Add(at);
        }

        // Kmen musí zmizet před kontrolou podpory listů. Jinak by vlastní, právě umírající
        // kmen stále držel celou korunu a po přirozené smrti by ve vzduchu zůstala zelená koule.
        world.SetBlocks(trunkWrites);

        string leavesId = _blocks.Definition(log).Id.Replace(
            "_log", "_leaves", StringComparison.Ordinal);

        if (_blocks.TryIndexOf(leavesId, out ushort leaves))
        {
            var leafWrites = new List<(Vector3i At, ushort Block)>();
            int top = trunk[^1].Y;

            for (int y = Math.Max(0, bottom.Y - 1);
                 y <= Math.Min(TerrainGenerator.WorldHeight - 1, top + LeafReach);
                 y++)
            {
                for (int z = bottom.Z - LeafReach; z <= bottom.Z + LeafReach; z++)
                {
                    for (int x = bottom.X - LeafReach; x <= bottom.X + LeafReach; x++)
                    {
                        var at = new Vector3i(x, y, z);
                        if (world.GetBlock(x, y, z) == leaves && !HasSupport(world, at, leaves))
                        {
                            leafWrites.Add((at, BlockRegistry.Air));
                            touched.Add(at);
                        }
                    }
                }
            }

            world.SetBlocks(leafWrites);
        }

        foreach (Vector3i at in touched)
        {
            world.SetMicro(at.X, at.Y, at.Z, null);
        }

        _ = direction;
        changed = touched;
        return trunk.Count;
    }

    /// <summary>Zapíše listí kolem uvolněné klády do fronty tlení.</summary>
    /// <remarks>
    /// Řadí se podle vzdálenosti od kmene, takže se koruna rozpadá od středu ven. Pořadí
    /// je jediné, co se plánuje dopředu — čas ne, ten dává fronta sama.
    /// </remarks>
    private void MarkLeaves(VoxelWorld world, Vector3i trunk)
    {
        var found = new List<(Vector3i Block, float Distance)>();

        for (int dy = -1; dy <= LeafReach; dy++)
        {
            for (int dz = -LeafReach; dz <= LeafReach; dz++)
            {
                for (int dx = -LeafReach; dx <= LeafReach; dx++)
                {
                    var at = new Vector3i(trunk.X + dx, trunk.Y + dy, trunk.Z + dz);

                    if (IsLeaves(world.GetBlock(at.X, at.Y, at.Z)))
                    {
                        found.Add((at, (dx * dx) + (dy * dy) + (dz * dz)));
                    }
                }
            }
        }

        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        foreach ((Vector3i at, float _) in found)
        {
            Schedule(at);
        }
    }

    private void Schedule(Vector3i block)
    {
        // Tentýž list může sousedit s víc kládami. Bez téhle kontroly by se do fronty
        // dostal několikrát a pustil by drops opakovaně.
        if (_decaying.Contains(block))
        {
            return;
        }

        // První list v prázdné frontě rozjede odpočet. Bez toho by se čekalo na čas
        // nastavený někdy dávno a koruna by zmizela naráz.
        if (_decaying.Count == 0)
        {
            _nextDecay = _clock + DecayStart;

            // Odstup se dopočítá až při prvním tlení, kdy je fronta hotová. Tady ještě
            // není: kácení zapisuje listí kládu po kládě.
            _interval = 0f;
        }

        _decaying.Add(block);
    }

    /// <summary>
    /// Posune tlení. Volá se každý snímek.
    /// </summary>
    /// <returns>Kolik listů zetlelo.</returns>
    public int Update(
        VoxelWorld world, ItemEntities drops, ChunkStreamer streamer, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(streamer);

        _clock += deltaSeconds;

        if (_decaying.Count == 0)
        {
            return 0;
        }

        int decayed = 0;

        // Odstup se určí jednou, z hotové fronty. Velká koruna zrychlí, malá zůstane
        // na desetině vteřiny — viz DecayTotalSeconds.
        if (_interval <= 0f)
        {
            _interval = Math.Clamp(DecayTotalSeconds / _decaying.Count, DecayMinInterval, DecayInterval);
        }

        // JEDEN LIST NA ODPOČET. Ne všechny, kterým zrovna došel čas — právě to dělalo
        // z tlení mrknutí.
        while (_decaying.Count > 0 && _clock >= _nextDecay)
        {
            Vector3i block = _decaying[0];
            _decaying.RemoveAt(0);

            _nextDecay = _clock + _interval;

            ushort leaves = world.GetBlock(block.X, block.Y, block.Z);

            if (!IsLeaves(leaves))
            {
                continue;
            }

            // LIST, KTERÝ DRŽÍ JINÝ KMEN, NETLÍ.
            //
            // Koruny dvou stromů vedle sebe se prorůstají. Když se jeden porazil, spadly
            // s ním i listy toho druhého, protože se do fronty dostalo všechno v okolí
            // uvolněné klády. Podmínka se proto ověřuje až při tlení, ne při zařazení:
            // do té doby může strom vedle ještě stát.
            if (HasSupport(world, block, leaves))
            {
                continue;
            }

            world.SetBlock(block.X, block.Y, block.Z, BlockRegistry.Air);
            world.SetMicro(block.X, block.Y, block.Z, null);
            streamer.InvalidateBlock(block.X, block.Y, block.Z);

            DropFromLeaves(leaves, block, drops);
            decayed++;
        }

        if (decayed > 0)
        {
            drops.Merge();
        }

        return decayed;
    }

    /// <summary>
    /// Drží ten list ještě nějaký kmen téhož druhu?
    /// </summary>
    /// <remarks>
    /// Hledá se krychle o poloměru <see cref="SupportReach"/>, což je zhruba dosah koruny
    /// od kmene. Prohledávat průchodem přes listí by bylo přesnější, ale stálo by to
    /// zásobník a návštěvní mapu na každý list — a na to je tahle otázka moc častá.
    /// </remarks>
    private bool HasSupport(VoxelWorld world, Vector3i leaf, ushort leaves)
    {
        string log = _blocks.Definition(leaves).Id.Replace("_leaves", "_log", StringComparison.Ordinal);
        ushort wanted = _blocks.IndexOf(log);

        if (wanted == BlockRegistry.Air)
        {
            return false;
        }

        for (int dy = -SupportReach; dy <= SupportReach; dy++)
        {
            for (int dz = -SupportReach; dz <= SupportReach; dz++)
            {
                for (int dx = -SupportReach; dx <= SupportReach; dx++)
                {
                    if (world.GetBlock(leaf.X + dx, leaf.Y + dy, leaf.Z + dz) == wanted)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Co pustí zetlelý list.
    /// </summary>
    /// <remarks>
    /// <para><b>Všechno je vzácné.</b> Klacek nejdřív padal z každého listu a sazenice
    /// ze čtvrtiny — z jednoho stromu z toho byla hromada padesáti klacků, což z nich
    /// dělalo odpad místo suroviny. Koruna má stovku listů, takže i osm procent je pořád
    /// pět až deset kusů, tedy dost na pár nástrojů.</para>
    ///
    /// <para>Losuje se z hashe polohy, ne z generátoru náhody: tentýž list pustí pokaždé
    /// totéž, takže se to dá reprodukovat.</para>
    /// </remarks>
    private void DropFromLeaves(ushort leaves, Vector3i at, ItemEntities drops)
    {
        uint hash = Hash(at);

        if (hash % 100u < 8u)
        {
            Give(_items.IndexOf("tesseris:stick"), 1);
        }

        if ((hash >> 7) % 100u < 5u)
        {
            string species = _blocks.Definition(leaves).Id.Replace("_leaves", "_sapling", StringComparison.Ordinal);
            Give(_items.IndexOf(species), 1);
        }

        if ((hash >> 15) % 100u < 2u)
        {
            Give(_items.IndexOf("tesseris:apple"), 1);
        }

        void Give(int item, int count)
        {
            if (item != ItemRegistry.Nothing)
            {
                drops.SpawnFromBlock(new ItemStack(item, count, 0), at, hash);
            }
        }
    }

    /// <summary>
    /// Co dá list rozbitý rukou.
    /// </summary>
    /// <remarks>
    /// <para><b>Vždycky něco, na rozdíl od tlení.</b> Rozbít list je práce navíc, kdežto
    /// tlení běží samo — proto je ruční sběr spolehlivý a tlení skoupé. Bez toho by se
    /// vyplatilo strom porazit a čekat, což je opak toho, co má hra odměňovat.</para>
    ///
    /// <para>Samotné listí se nesbírá. Ve hře se z něj nic nestaví a hromádka listí
    /// v inventáři je jen zabraný slot.</para>
    /// </remarks>
    public void HandPick(ushort leaves, Vector3i at, ItemEntities drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        uint roll = Hash(at) % 100u;

        // Jablko vzácně, sazenice občas, jinak klacek.
        string id = roll switch
        {
            < 3u => "tesseris:apple",
            < 20u => _blocks.Definition(leaves).Id.Replace("_leaves", "_sapling", StringComparison.Ordinal),
            _ => "tesseris:stick",
        };

        int item = _items.IndexOf(id);

        if (item != ItemRegistry.Nothing)
        {
            drops.SpawnFromBlock(new ItemStack(item, 1, 0), at, Hash(at));
            drops.Merge();
        }
    }

    private static uint Hash(Vector3i block)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)block.X) * 16777619u;
            h = (h ^ (uint)block.Y) * 16777619u;
            h = (h ^ (uint)block.Z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }
}
