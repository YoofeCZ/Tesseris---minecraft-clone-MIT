using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;

namespace Tesseris.Game.World;

/// <summary>
/// Šíření vody ve stylu Minecraftu.
/// </summary>
/// <remarks>
/// <para><b>Voda se nepřesouvá, přepočítává se.</b> Každá buňka si odvodí svou úroveň
/// z okolí: co teče shora, drží plnou výšku; co teče do stran, klesne o stupeň. Kdo nemá
/// odkud brát, zmizí. Zdroj je jediná výjimka — ten se nepřepočítává a nikdy nevyschne.</para>
///
/// <para>Výhoda proti přelévání hmoty je v tom, že výsledek nezávisí na pořadí zpracování
/// ani na historii. Stav buňky je funkce jejího okolí, takže se nemá kde nasčítat chyba
/// a hladina se ustálí rychle a napevno.</para>
///
/// <para><b>Fronta místo procházení světa.</b> Přepočítává se jen to, co se hýbe: buňka se
/// zařadí do fronty, když se jí nebo jejímu sousedovi něco stane, a vypadne, jakmile jí
/// přepočet vyjde stejně jako předtím. Klidné jezero tak nestojí nic.</para>
/// </remarks>
public sealed class FluidSimulation
{
    /// <summary>
    /// Kolik buněk se smí přepočítat za jeden tik.
    /// </summary>
    /// <remarks>
    /// Strop, ne cíl. Protržená hráz probudí tisíce buněk naráz a bez stropu by to byl
    /// jeden dlouhý snímek. Co se nestihne, počká na další tik — voda pak teče o kousek
    /// pomaleji, ale hra nezasekne.
    /// </remarks>
    private const int BudgetPerTick = 2048;

    /// <summary>
    /// Jak často se fronta obsluhuje. Není to tempo vody — o to se stará
    /// <see cref="SpreadDelay"/>; tohle jen říká, jak jemně se měří čas splatnosti.
    /// </summary>
    private const double TickSeconds = 1.0 / 10.0;

    /// <summary>
    /// Jak dlouho trvá, než se voda pohne o blok.
    /// </summary>
    /// <remarks>
    /// <para>Bez prodlevy se voda po vytěžení bloku rozlila prakticky okamžitě — hráč
    /// seknul do hráze a než stačil couvnout, bylo po ní. Vteřina na blok je tempo, ve
    /// kterém je proud vidět postupovat a je čas mu uhnout.</para>
    ///
    /// <para>Neplatí to jen na první krok: každá buňka, která se probudí, dostane vlastní
    /// čas splatnosti. Voda tak postupuje rovnoměrně, ne trhaně - hned kus a pak nic.</para>
    /// </remarks>
    private const double SpreadDelay = 1.0;

    private readonly VoxelWorld _world;
    private readonly ushort _water;

    // Fronta nese ke každé buňce čas, kdy je splatná. Pořadí zůstává FIFO: všechny buňky
    // dostávají tutéž prodlevu, takže kdo se zařadil dřív, je i dřív splatný.
    private readonly Queue<(Vector3i Cell, double Due)> _pending = new();
    private readonly HashSet<Vector3i> _queued = [];

    private double _clock;

    // Rozpracovaný stav tiku. Čte se odtud, ne ze světa - do světa se zapisuje až na konci,
    // takže by čtení ze světa vidělo neaktuální okolí.
    private readonly Dictionary<Vector3i, byte> _working = [];

    private readonly Dictionary<Vector3i, List<(int Index, ushort Block, byte Amount)>> _batches = [];
    private readonly HashSet<Vector3i> _remesh = [];

    private double _accumulated;

    public FluidSimulation(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        _world = world;
        _water = world.Registry.IndexOf("tesseris:water");
    }

    /// <summary>Kolik buněk čeká na přepočet. Nula znamená ustálenou vodu.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Kolik buněk se přepočítalo v posledním tiku.</summary>
    public int LastTickWork { get; private set; }

    /// <summary>Směry rozlivu: posun po X a Z, k tomu osa a znaménko pro test průchodu.</summary>
    private static readonly (int Dx, int Dz, int Axis, bool Positive)[] Sides =
    [
        (-1, 0, 0, false),
        (1, 0, 0, true),
        (0, -1, 2, false),
        (0, 1, 2, true),
    ];

    /// <summary>
    /// Probudí buňku a její okolí. Volá se, kdykoli se něco změní — hráč vylije kbelík,
    /// vytěží blok pod hladinou, postaví hráz.
    /// </summary>
    public void Touch(int x, int y, int z)
    {
        var cell = new Vector3i(x, y, z);

        Enqueue(cell);
        WakeNeighbours(cell);
    }

    /// <summary>
    /// Posune šíření. Vrací chunky, které je potřeba přemešovat.
    /// </summary>
    public IReadOnlyCollection<Vector3i> Update(double deltaSeconds)
    {
        _batches.Clear();
        _remesh.Clear();
        _working.Clear();
        LastTickWork = 0;

        _accumulated += deltaSeconds;

        if (_accumulated < TickSeconds)
        {
            return Array.Empty<Vector3i>();
        }

        // Nasčítaný čas se zahodí, ne odečte. Po zásecích by se jinak dohánělo dávkou tiků
        // naráz a voda by poskočila.
        _accumulated = 0.0;
        _clock += TickSeconds;

        int budget = BudgetPerTick;

        while (budget > 0 && _pending.Count > 0)
        {
            (Vector3i cell, double due) = _pending.Peek();

            // Ještě nedozrálo. Fronta je seřazená podle splatnosti, takže co je za tímhle,
            // je splatné ještě později - nemá smysl hledat dál.
            if (due > _clock)
            {
                break;
            }

            _pending.Dequeue();
            _queued.Remove(cell);
            budget--;
            LastTickWork++;

            Step(cell);
        }

        Flush();

        foreach ((Vector3i chunk, List<(int, ushort, byte)> changes) in _batches)
        {
            _world.ApplyFluidBatch(chunk, changes);
        }

        return _remesh;
    }

    /// <summary>
    /// Přepočítá jednu buňku z jejího okolí.
    /// </summary>
    private void Step(Vector3i cell)
    {
        byte current = LevelAt(cell);

        // ZDROJ SE NEPŘEPOČÍTÁVÁ. Je to jediné místo, odkud voda do světa přibývá; kdyby
        // se odvozoval z okolí jako všechno ostatní, vyschl by hned, jak by kolem nic nebylo.
        if (FluidCell.IsSource(current))
        {
            SpreadFrom(cell, current);
            return;
        }

        if (!CanHold(cell))
        {
            // Do buňky se něco postavilo. Voda, která tu byla, prostě zmizí.
            if (current != FluidCell.Empty)
            {
                Write(cell, FluidCell.Empty);
                WakeNeighbours(cell);
            }

            return;
        }

        byte target = Derive(cell);

        if (target == current)
        {
            return;
        }

        Write(cell, target);
        WakeNeighbours(cell);

        if (target != FluidCell.Empty)
        {
            SpreadFrom(cell, target);
        }
    }

    /// <summary>
    /// Jakou úroveň má buňka mít podle svého okolí.
    /// </summary>
    /// <remarks>
    /// <para>Shora padající voda drží plnou výšku — proud ze skály není u země tenčí než
    /// nahoře. Do stran naopak každý blok ubere stupeň, a právě to omezuje dosah na sedm
    /// bloků od zdroje.</para>
    ///
    /// <para>Bere se nejvyšší nabídka, ne součet. Buňka mezi dvěma prameny není dvakrát
    /// hlubší, jen napájená ze dvou stran.</para>
    /// </remarks>
    private byte Derive(Vector3i cell)
    {
        // 1. VODA SHORA. Padá plným proudem, ale musí se k nám dostat.
        var above = new Vector3i(cell.X, cell.Y + 1, cell.Z);

        if (LevelAt(above) != FluidCell.Empty && CanFlowBetween(above, cell, axis: 1, positive: false))
        {
            return FluidCell.MaxFlowing;
        }

        // 2. VODA ZE STRAN. Každý krok stojí stupeň.
        byte best = FluidCell.Empty;
        int neighbourSources = 0;

        foreach ((int dx, int dz, int axis, bool positive) in Sides)
        {
            var side = new Vector3i(cell.X + dx, cell.Y, cell.Z + dz);
            byte level = LevelAt(side);

            if (level == FluidCell.Empty || !CanFlowBetween(side, cell, axis, positive))
            {
                continue;
            }

            if (FluidCell.IsSource(level))
            {
                neighbourSources++;
            }

            // Soused, který sám visí nad dírou, vodu nerozvádí - všechna mu propadne dolů.
            if (!FluidCell.IsSource(level) && FallsThrough(side))
            {
                continue;
            }

            // ANI SOUSED STOJÍCÍ NA HLADINĚ. Doběh, pod kterým je plná voda, se do ní vlije
            // a dál nic neposílá — jinak se rozlézá po povrchu moře a zůstávají po něm tenké
            // pásy ležící NAD hladinou. Zastaví se u prvního bloku vody, přesně tam, kam
            // dotekl.
            if (!FluidCell.IsSource(level) && StandsOnWater(side))
            {
                continue;
            }

            byte offered = (byte)(Math.Min(level, FluidCell.Source) - 1);

            if (offered > best)
            {
                best = offered;
            }
        }

        // VODA MEZI ZDROJI SE SAMA STANE ZDROJEM.
        //
        // Bez tohohle se doběh, který dotekl k moři, držel svých nižších úrovní a u břehu
        // z něj byly viditelné pásy ležící pod hladinou — voda tekoucí PO vodě. Jakmile je
        // buňka obklopená vodou ze dvou stran, není důvod, aby stála níž než ta kolem ní.
        //
        // Dva sousedi, ne jeden: u jednoho by se zdroj šířil od okraje jezera pořád dál
        // do krajiny a rozlil by celý svět. Dva znamenají „jsem uvnitř vodní plochy",
        // ne „stojím na jejím kraji". Stejné pravidlo má Minecraft.
        if (neighbourSources >= 2)
        {
            return FluidCell.Source;
        }

        return best;
    }

    /// <summary>
    /// Probudí místa, kam se z buňky voda může rozšířit.
    /// </summary>
    private void SpreadFrom(Vector3i cell, byte level)
    {
        var below = new Vector3i(cell.X, cell.Y - 1, cell.Z);

        if (CanHold(below) && CanFlowBetween(cell, below, axis: 1, positive: false))
        {
            Enqueue(below);

            // VODA PADÁ DŘÍV, NEŽ SE ROZLÉVÁ. Dokud má kam padat, do stran nejde nic —
            // proud u okraje díry se po podlaze nerozlévá, ale spadne.
            return;
        }

        if (level <= 1)
        {
            return;
        }

        // Konec proudu. Kdo stojí na plné vodě, do stran už nic neposílá.
        if (!FluidCell.IsSource(level) && StandsOnWater(cell))
        {
            return;
        }

        foreach ((int dx, int dz, int axis, bool positive) in Sides)
        {
            var side = new Vector3i(cell.X + dx, cell.Y, cell.Z + dz);

            if (CanHold(side) && CanFlowBetween(cell, side, axis, positive))
            {
                Enqueue(side);
            }
        }
    }

    /// <summary>
    /// Stojí buňka na plné vodě? Pak je to konec proudu — dál se voda nešíří, splyne.
    /// </summary>
    private bool StandsOnWater(Vector3i cell)
    {
        var below = new Vector3i(cell.X, cell.Y - 1, cell.Z);

        return LevelAt(below) >= FluidCell.Source;
    }

    /// <summary>Propadne voda z téhle buňky rovnou dolů?</summary>
    private bool FallsThrough(Vector3i cell)
    {
        var below = new Vector3i(cell.X, cell.Y - 1, cell.Z);

        return CanHold(below)
            && LevelAt(below) < FluidCell.MaxFlowing
            && CanFlowBetween(cell, below, axis: 1, positive: false);
    }

    /// <summary>Úroveň vody v buňce. Bere se z rozpracovaného stavu, když už se změnila.</summary>
    private byte LevelAt(Vector3i cell)
    {
        ushort block = _world.GetBlock(cell.X, cell.Y, cell.Z);

        // Vodní rostlina nese vlastní zdroj vody. Nesmí ji přepsat doběh simulace ani
        // vysušit stará položka ve frontě, která vznikla ještě před jejím zasazením.
        if (_world.Registry.IsAquatic(block))
        {
            return FluidCell.Source;
        }

        if (_working.TryGetValue(cell, out byte pending))
        {
            return pending;
        }

        if (block != _water)
        {
            return FluidCell.Empty;
        }

        byte stored = _world.GetFluid(cell.X, cell.Y, cell.Z);

        // Voda z generátoru terénu žádnou úroveň zapsanou nemá a chybějící záznam znamená
        // plno — moře a jezera jsou tedy samé zdroje, což je přesně to, co chceme.
        return stored == 0 ? FluidCell.Source : Math.Min(stored, FluidCell.Source);
    }

    /// <summary>Vejde se do buňky voda?</summary>
    private bool CanHold(Vector3i cell)
    {
        if (cell.Y < 0 || cell.Y >= TerrainGenerator.WorldHeight)
        {
            return false;
        }

        ushort block = _world.GetBlock(cell.X, cell.Y, cell.Z);

        if (block != BlockRegistry.Air && !_world.Registry.ContainsWater(block))
        {
            return false;
        }

        // OTESANÝ BLOK MÁ V BLOKOVÉ VRSTVĚ VZDUCH. Mřížka mikrovoxelů leží vedle ní, takže
        // pro GetBlock je vytesaný kámen k nerozeznání od díry — bez tohohle by do něj voda
        // natekla, jako by tam nic nebylo.
        MicroBlock? micro = _world.GetMicro(cell.X, cell.Y, cell.Z);

        return micro is null || MicroFlow.FreeFraction(micro) > MicroFlow.MinFreeFraction;
    }

    /// <summary>
    /// Proteče voda mezi dvěma sousedy? Mezi otesanými bloky musí být dost velký otvor.
    /// </summary>
    private bool CanFlowBetween(Vector3i from, Vector3i to, int axis, bool positive)
    {
        MicroBlock? a = _world.GetMicro(from.X, from.Y, from.Z);
        MicroBlock? b = _world.GetMicro(to.X, to.Y, to.Z);

        if (a is null && b is null)
        {
            return true;
        }

        return MicroFlow.HasOpening(a, b, axis, positive);
    }

    private void Write(Vector3i cell, byte level) => _working[cell] = level;

    /// <summary>Přelije rozpracovaný stav do dávek po chuncích.</summary>
    private void Flush()
    {
        foreach ((Vector3i cell, byte level) in _working)
        {
            Vector3i chunk = VoxelWorld.ToChunkPosition(cell.X, cell.Y, cell.Z);

            int index = Chunk.LocalIndex(
                cell.X & Chunk.SizeMask,
                cell.Y & Chunk.SizeMask,
                cell.Z & Chunk.SizeMask);

            if (!_batches.TryGetValue(chunk, out List<(int, ushort, byte)>? changes))
            {
                changes = [];
                _batches[chunk] = changes;
            }

            changes.Add(level == FluidCell.Empty
                ? (index, BlockRegistry.Air, (byte)0)
                : (index, _water, level));

            MarkForRemesh(cell, chunk);
        }
    }

    /// <summary>
    /// Zapíše chunk k přemešování, a když buňka leží na kraji, tak i souseda přes tu hranu.
    /// </summary>
    /// <remarks>
    /// Není to totéž co seznam změněných chunků: když voda zmizí u kraje, změní se i stěna
    /// SOUSEDA, který sám žádnou změnu nedostal. Bez tohohle zůstanou ve vodě viset plochy,
    /// které už nemají co ohraničovat.
    /// </remarks>
    private void MarkForRemesh(Vector3i cell, Vector3i chunk)
    {
        _remesh.Add(chunk);

        int localX = cell.X & Chunk.SizeMask;
        int localY = cell.Y & Chunk.SizeMask;
        int localZ = cell.Z & Chunk.SizeMask;

        const int Last = Chunk.Size - 1;

        if (localX == 0) { _remesh.Add(chunk + new Vector3i(-1, 0, 0)); }
        if (localX == Last) { _remesh.Add(chunk + new Vector3i(1, 0, 0)); }
        if (localY == 0) { _remesh.Add(chunk + new Vector3i(0, -1, 0)); }
        if (localY == Last) { _remesh.Add(chunk + new Vector3i(0, 1, 0)); }
        if (localZ == 0) { _remesh.Add(chunk + new Vector3i(0, 0, -1)); }
        if (localZ == Last) { _remesh.Add(chunk + new Vector3i(0, 0, 1)); }
    }

    private void WakeNeighbours(Vector3i cell)
    {
        Enqueue(new Vector3i(cell.X, cell.Y + 1, cell.Z));
        Enqueue(new Vector3i(cell.X, cell.Y - 1, cell.Z));
        Enqueue(new Vector3i(cell.X - 1, cell.Y, cell.Z));
        Enqueue(new Vector3i(cell.X + 1, cell.Y, cell.Z));
        Enqueue(new Vector3i(cell.X, cell.Y, cell.Z - 1));
        Enqueue(new Vector3i(cell.X, cell.Y, cell.Z + 1));
    }

    private void Enqueue(Vector3i cell)
    {
        if (cell.Y < 0 || cell.Y >= TerrainGenerator.WorldHeight)
        {
            return;
        }

        if (_queued.Add(cell))
        {
            _pending.Enqueue((cell, _clock + SpreadDelay));
        }
    }
}
