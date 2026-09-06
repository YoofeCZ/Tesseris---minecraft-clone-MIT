using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;

namespace Tesseris.Game.Colony;

/// <summary>
/// Celá kolonie na jednom místě: navigace, fronta práce, lidé a stroje.
/// </summary>
/// <remarks>
/// <para><b>Proč to je zvlášť.</b> <c>TesserisWindow</c> má přes deset tisíc řádků a audit
/// ho označil za hlavní překážku každého dalšího kroku. Kolonie se do něj proto nevlévá po
/// kusech — okno drží jednu referenci a volá dvě metody.</para>
///
/// <para><b>Mřížky pochůznosti se staví s rozpočtem.</b> Naměřeno 3,34 ms na chunk, takže
/// postavit je všechny naráz by utrhlo tick. Staví se nejvýš pár na tik a jen kolem hráče;
/// zbytek světa navigaci nepotřebuje, dokud tam nikdo není.</para>
///
/// <para><b>Změny světa se hlásí inkrementálně.</b> Kopnutí stojí 0,0028 ms proti 3,34 ms
/// plné přestavby — bez toho by kolonie s dvěma sty kopajícími lidmi utrhla tick.</para>
/// </remarks>
public sealed class ColonyRuntime : ColonySimulation.IDeliveryTargets
{
    /// <summary>Kolik mřížek se smí postavit za jeden tik.</summary>
    /// <remarks>Jedna: stojí přes tři milisekundy a rozpočet celého tiku je osm.</remarks>
    public const int GridsPerTick = 1;

    /// <summary>V kolika chuncích kolem hráče se navigace udržuje.</summary>
    public const int NavigationRadiusChunks = 2;

    private readonly NavGraph _graph = new();
    private readonly DigJobQueue _jobs = new();
    private MachineBank _machines = new();
    private InserterBank _inserters = new();
    private readonly List<BeltSegment> _belts = [];
    private readonly List<BeltPlacement> _placements = [];
    private readonly Dictionary<Vector3i, BeltSegment> _beltByCell = [];
    private readonly ColonySimulation _colonists = new(capacity: 256);
    private readonly HashSet<Vector3i> _built = [];
    private readonly List<Vector3i> _pending = [];
    private readonly ReachableCells _reachable = new();

    private ushort _water = ushort.MaxValue;

    public ColonyRuntime() => _colonists.Delivery = this;

    public NavGraph Navigation => _graph;

    public DigJobQueue Jobs => _jobs;

    public MachineBank Machines => _machines;

    public InserterBank Inserters => _inserters;

    /// <summary>Kolik pásů ve světě stojí.</summary>
    public int BeltCount => _belts.Count;

    /// <summary>
    /// Kolik strojů ve světě stojí. Zbourané se nepočítají.
    /// </summary>
    /// <remarks>
    /// Vlastní počítadlo, ne <c>Machines.Count</c>: banka si po zbourání drží místo v poli,
    /// takže její počet roste i s klesajícím počtem strojů. Do UI patří to, co hráč vidí.
    /// </remarks>
    public int MachineCount { get; private set; }

    /// <inheritdoc cref="MachineCount"/>
    public int InserterCount { get; private set; }

    /// <summary>Kolik itemů je právě na pásech.</summary>
    public int ItemsOnBelts
    {
        get
        {
            int total = 0;
            foreach (BeltSegment belt in _belts)
            {
                total += belt.Count;
            }

            return total;
        }
    }

    public BeltSegment? BeltAt(Vector3i cell) =>
        _beltByCell.TryGetValue(cell, out BeltSegment? belt) ? belt : null;

    /// <summary>Kde pás ve světě leží a kterým směrem vede.</summary>
    /// <remarks>
    /// Simulace polohu nepotřebuje — ta pracuje jen s odstupy. Vykreslování ano, a proto se
    /// drží zvlášť: kdyby si polohu nesl každý item, byla by to ta entita per item, kterou
    /// pravidlo 6.1 zakazuje.
    /// </remarks>
    public readonly record struct BeltPlacement(Vector3i Start, Vector3i Direction, BeltSegment Belt);

    /// <summary>Všechny pásy i s tím, kde stojí. Pro vykreslování.</summary>
    public IReadOnlyList<BeltPlacement> Placements => _placements;

    /// <summary>
    /// Postaví pás. Vrací ho, aby na něj šlo hned navázat stroj nebo vkládač.
    /// </summary>
    /// <remarks>
    /// <b>Výstup je na buňce <paramref name="cell"/></b> a pás se táhne PROTI směru jízdy,
    /// tedy tam, odkud itemy přijíždějí. Díky tomu se dá stavět od stroje zpátky, což je
    /// pořadí, ve kterém člověk uvažuje.
    /// </remarks>
    public BeltSegment PlaceBelt(Vector3i cell, int cells = 8, Vector3i? direction = null)
    {
        var belt = new BeltSegment(cells);
        _beltByCell[cell] = belt;
        _belts.Add(belt);
        _placements.Add(new BeltPlacement(cell, direction ?? new Vector3i(1, 0, 0), belt));
        return belt;
    }

    /// <summary>
    /// Zahodí všechno postavené i všechny lidi. Navigace zůstává — ta patří světu, ne kolonii.
    /// </summary>
    /// <remarks>
    /// Volá se před načtením savu. Rozdělaně načtená kolonie je horší než žádná: půlka pásů
    /// bez strojů by se tvářila jako platný stav.
    /// </remarks>
    public void Clear()
    {
        _belts.Clear();
        _placements.Clear();
        _beltByCell.Clear();
        _jobs.Clear();
        _colonists.Clear();

        // Banky se nedají vyprázdnit na místě — indexy v nich drží pásy i lidi, a ti jsou
        // teď pryč. Čistá banka je jediný stav, kterému se dá věřit.
        _machines = new MachineBank();
        _inserters = new InserterBank();
        MachineCount = 0;
        InserterCount = 0;
        FreedByAutomation = 0;
    }

    /// <summary>
    /// Stojí na buňce už něco?
    /// </summary>
    /// <remarks>
    /// <b>Stavět na obsazenou buňku nejde.</b> Druhý pás na stejném místě by ten první vyřadil
    /// z evidence podle buňky, ale nechal ho v seznamu — pořád by tikal, pořád by se kreslil
    /// a nešel by zbourat. Dvojklik při stavění je běžný, takže tohle není hraniční případ.
    /// </remarks>
    public bool IsOccupied(Vector3i cell)
    {
        if (_beltByCell.ContainsKey(cell))
        {
            return true;
        }

        for (int machine = 0; machine < _machines.Count; machine++)
        {
            if (!_machines.IsRemoved(machine) && _machines.CellOf(machine) == cell)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Co se na buňce zbouralo.</summary>
    public enum Demolished : byte
    {
        /// <summary>Nic tam nestálo.</summary>
        Nothing,

        /// <summary>Pás.</summary>
        Belt,

        /// <summary>Stroj.</summary>
        Machine,

        /// <summary>Vkládač.</summary>
        Inserter,
    }

    /// <summary>
    /// Zbourá to, co na buňce stojí.
    /// </summary>
    /// <remarks>
    /// <para><b>Bez tohohle se nedá stavět.</b> Kdo jednou položí pás špatným směrem, musí
    /// s tím umět žít do konce hry — a to je důvod nestavět vůbec. Bourání není doplněk
    /// ke stavění, je jeho druhá půlka.</para>
    ///
    /// <para><b>Materiál se nesmí ztratit.</b> Co bylo na pásu nebo ve stroji, jde na sklad.
    /// Stejné pravidlo jako u kolonisty, kterému se nepovede doručit náklad.</para>
    ///
    /// <para><b>Vkládač má přednost před pásem a strojem</b>, protože stojí mezi nimi a je na
    /// téhle buňce vidět navrchu. Kliknout na vkládač a zbourat pás pod ním by bylo horší než
    /// nezbourat nic.</para>
    /// </remarks>
    public Demolished Demolish(Vector3i cell)
    {
        for (int inserter = 0; inserter < _inserters.Count; inserter++)
        {
            if (!_inserters.IsRemoved(inserter) && _inserters.CellOf(inserter) == cell)
            {
                _inserters.Remove(inserter);
                InserterCount--;
                return Demolished.Inserter;
            }
        }

        for (int machine = 0; machine < _machines.Count; machine++)
        {
            if (_machines.IsRemoved(machine) || _machines.CellOf(machine) != cell)
            {
                continue;
            }

            // Obsah zásobníků na sklad, ještě než ho Remove vynuluje.
            //
            // VSTUP A VÝSTUP ZVLÁŠŤ: ve stroji leží dva různé druhy (surovina a hotový kus)
            // a sklad teď ví, co v něm je. Sečíst je do jednoho čísla by znamenalo, že se
            // z drtiče vrátí dvakrát ruda a hotové kusy zmizí.
            _colonists.StoreItems(_machines.InputItemOf(machine), _machines.InputOf(machine));
            _colonists.StoreItems(_machines.OutputItemOf(machine), _machines.OutputOf(machine));

            InserterCount -= _inserters.ForgetMachine(machine);
            MachineCount--;
            int colonist = _machines.Remove(machine);

            // Kdo u něj stál, je zase volný — a tohle je to číslo, na kterém stojí celá hra.
            if (colonist >= 0)
            {
                _colonists.Release(colonist);
            }

            return Demolished.Machine;
        }

        if (BeltAt(cell) is { } belt)
        {
            // KAŽDÝ SLOT ZVLÁŠŤ. Na pásu můžou jet dva různé druhy naráz, takže jedno číslo
            // by z nich udělalo hromádku toho prvního.
            for (int slot = 0; slot < belt.Count; slot++)
            {
                _colonists.StoreItems(belt.SlotAt(slot).ItemId, 1);
            }

            belt.Clear();

            _machines.ForgetBelt(belt);
            InserterCount -= _inserters.ForgetBelt(belt);

            _beltByCell.Remove(cell);
            _belts.Remove(belt);
            _placements.RemoveAll(placement => ReferenceEquals(placement.Belt, belt));
            return Demolished.Belt;
        }

        return Demolished.Nothing;
    }

    /// <summary>
    /// Postaví ke stroji člověka. Musí to projít oběma stranami, jinak se počty rozejdou.
    /// </summary>
    public bool TryAssignOperator(int machine, int colonist) =>
        _machines.TryAssignOperator(machine, colonist) && _colonists.TryOccupy(colonist, machine);

    /// <summary>Postaví drtič.</summary>
    public int PlaceCrusher(Vector3i cell, ushort inputItem, ushort outputItem, int ticksPerCraft = 30)
    {
        MachineCount++;
        return _machines.Add(cell, inputItem, outputItem, ticksPerCraft);
    }

    /// <summary>Zaeviduje čerstvě postavený vkládač. Volá se po každém <c>Inserters.Add…</c>.</summary>
    public void CountInserter() => InserterCount++;

    /// <summary>Položí item na pás a ohlásí to všem, kdo z něj berou.</summary>
    public bool TryPushOntoBelt(BeltSegment belt, ushort item)
    {
        ArgumentNullException.ThrowIfNull(belt);

        if (!belt.TryPush(item))
        {
            return false;
        }

        // Stroj i vkládač za pásem můžou spát a čekat přesně na tohle.
        _machines.NotifyPushed(belt);
        _inserters.NotifyPushed(belt);
        return true;
    }

    public ColonySimulation Colonists => _colonists;

    /// <summary>Kolik lidí je volných. Číslo, na kterém stojí celá hra.</summary>
    public int FreeColonists => _colonists.IdleCount;

    /// <summary>Kolik mřížek pochůznosti je hotových.</summary>
    public int NavigationChunks => _built.Count;

    /// <summary>Kolik mřížek čeká na postavení.</summary>
    public int PendingNavigation => _pending.Count;

    /// <summary>Kolik lidí uvolnila automatizace za celý běh.</summary>
    public int FreedByAutomation { get; private set; }

    /// <summary>Kolik lidí uvolnila automatizace. Setter jen pro načítání savu.</summary>

    /// <summary>
    /// Kam odevzdat vykopaný materiál: nejbližší stroj, který ho bere, jinak nejbližší pás.
    /// </summary>
    /// <remarks>
    /// <para><b>Stroj má přednost před pásem.</b> Kdo kope rudu vedle drtiče, má ji dát rovnou
    /// do něj — posílat ji na pás, který vede zpátky do téhož stroje, je práce navíc.</para>
    ///
    /// <para>Hledá se lineárně. Strojů a pásů jsou desítky; prostorový index má smysl přidat,
    /// až měření ukáže, že to vadí.</para>
    /// </remarks>
    public bool TryFindTarget(ushort item, Vector3i from, out Vector3i cell)
    {
        cell = default;
        long best = long.MaxValue;
        bool found = false;

        for (int machine = 0; machine < _machines.Count; machine++)
        {
            if (_machines.IsRemoved(machine) || _machines.InputOf(machine) >= MachineBank.InputCapacity)
            {
                continue;
            }

            Vector3i at = _machines.CellOf(machine);
            long distance = DistanceSquared(at, from);
            if (distance < best)
            {
                best = distance;
                cell = at;
                found = true;
            }
        }

        if (found)
        {
            return true;
        }

        foreach (BeltPlacement placement in _placements)
        {
            if (!placement.Belt.CanPush)
            {
                continue;
            }

            long distance = DistanceSquared(placement.Start, from);
            if (distance < best)
            {
                best = distance;
                cell = placement.Start;
                found = true;
            }
        }

        _ = item;
        return found;
    }

    /// <summary>Odevzdá materiál do stroje nebo na pás na zadané buňce.</summary>
    public bool TryDeliver(ushort item, Vector3i cell)
    {
        for (int machine = 0; machine < _machines.Count; machine++)
        {
            if (!_machines.IsRemoved(machine) && _machines.CellOf(machine) == cell)
            {
                return _machines.TryInsert(machine, item);
            }
        }

        BeltSegment? belt = BeltAt(cell);
        return belt is not null && TryPushOntoBelt(belt, item);
    }

    private static long DistanceSquared(Vector3i a, Vector3i b)
    {
        Vector3i delta = a - b;
        return ((long)delta.X * delta.X) + ((long)delta.Y * delta.Y) + ((long)delta.Z * delta.Z);
    }

    /// <summary>Nastaví, co je voda — pochůznost po ní nevede.</summary>
    public void SetWater(ushort water) => _water = water;

    /// <summary>
    /// Postaví kolonistu, pokud je pod zadanou polohou kde stát.
    /// </summary>
    /// <returns>Index kolonisty, nebo −1, když tam stát nejde.</returns>
    public int TrySpawnColonist(Vector3i cell)
    {
        if (!_graph.IsStandable(cell))
        {
            return -1;
        }

        return _colonists.Add(cell);
    }

    /// <summary>
    /// Radnice: bez ní kolonie neexistuje a nikdo nepřijde.
    /// </summary>
    /// <remarks>
    /// Drží ji runtime, ne okno, protože „smí přijít další člověk" je pravidlo hry a musí
    /// jít otestovat bez okna a bez Vulkanu.
    /// </remarks>
    public TownHall TownHall { get; } = new();

    /// <summary>Sklad kolonie. Ví, co v něm leží, a dá se z něj vzít.</summary>
    public ColonyStore Store => _colonists.Store;

    /// <summary>
    /// Založí kolonii radnicí a přiřadí skladu místo.
    /// </summary>
    /// <remarks>
    /// <b>Sklad patří k radnici</b>, protože je to střed kolonie a hráč si ho umístil sám.
    /// Kdyby si polohu skladu držela radnice i sklad každý zvlášť, rozešly by se při prvním
    /// načtení savu — proto to jde jedněmi dveřmi.
    /// </remarks>
    /// <returns>Založila tahle radnice kolonii?</returns>
    public bool FoundTownHall(Vector3i cell)
    {
        if (!TownHall.Found(cell))
        {
            return false;
        }

        // Sklad sedí na buňce radnice. Stát se v ní nedá, je plná — kolonista proto míří na
        // sousední pochůznou buňku, stejně jako když jde odevzdat do stroje.
        Store.SetCell(cell);
        return true;
    }

    /// <summary>
    /// Obnoví radnici i sklad z uloženého stavu.
    /// </summary>
    /// <remarks>
    /// <b>Jedním krokem, stejně jako <see cref="FoundTownHall"/>.</b> Kdyby si polohu
    /// nastavovala radnice a sklad zvlášť, rozešly by se — a přesně to se stalo: sav nesl
    /// obsah skladu, ale ne jeho místo, takže po restartu stála radnice ve světě, ve skladu
    /// leželo jídlo, a nikdo se nenajedl, protože sklad neměl souřadnici.
    /// </remarks>
    public void RestoreTownHall(Vector3i cell, int arrived, int ticksSinceArrival)
    {
        TownHall.Restore(cell, arrived, ticksSinceArrival);
        Store.SetCell(cell);
    }

    /// <summary>
    /// Řekne skladu, co je jídlo. Volá se, jakmile je znám registr bloků.
    /// </summary>
    /// <remarks>
    /// <b>Vždycky přes jméno, nikdy natvrdo číslem</b>. Bez tohohle volání
    /// sklad žádné jídlo nezná a hladoví kolonisté nemají kam jít — což je tichý stav, proto
    /// se chybějící jména hlásí volajícímu.
    /// </remarks>
    public void ResolveFood(BlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Store.SetFood(ColonyFood.Resolve(blocks));
    }

    /// <summary>
    /// Postaví kolonistu u radnice, pokud je čas a je kam.
    /// </summary>
    /// <remarks>
    /// <para><b>Hledá se od radnice ven, ne kolem hráče.</b> Kolonista patří ke kolonii, takže
    /// přijde k ní — i když hráč zrovna stojí o dvě stě metrů dál.</para>
    ///
    /// <para><b>Když se nikam nevejde, příchod se nezapíše.</b> Radnice si tím nechá nárok
    /// a zkusí to znovu za chvíli; jinak by kolonie tiše přišla o člověka jen proto, že byla
    /// v tu vteřinu obsazená buňka.</para>
    /// </remarks>
    /// <returns>Index nového kolonisty, nebo −1, když nikdo nepřišel.</returns>
    public int TryWelcomeColonist()
    {
        if (!TownHall.Tick(_colonists.Count))
        {
            return -1;
        }

        // Kolem radnice ven, ať první příchozí stojí u ní a ne za rohem.
        //
        // POLOMĚR ROSTE S POČTEM LIDÍ. Šest buněk je 168 míst, což pro tři lidi bohatě stačí,
        // ale pro dvě stě (sonda VOXELITY_COLONISTS) ne — a nevešlý člověk se tiše zahodí,
        // takže by se cíl ze sekce 8 nikdy nesešel a vypadalo by to jako chyba příchodu.
        int maximum = TownHall.Capacity > TownHall.MaxColonists ? 24 : 6;

        for (int radius = 1; radius <= maximum; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Jen obvod čtverce; vnitřek prošly menší poloměry.
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    for (int dy = -4; dy <= 2; dy++)
                    {
                        var cell = new Vector3i(
                            TownHall.Cell.X + dx, TownHall.Cell.Y + dy, TownHall.Cell.Z + dz);

                        int colonist = TrySpawnColonist(cell);
                        if (colonist >= 0)
                        {
                            TownHall.CountArrival();
                            return colonist;
                        }
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Doplní chybějící mřížky pochůznosti kolem hráče, nejvýš <see cref="GridsPerTick"/> za tik.
    /// </summary>
    /// <remarks>
    /// <para><b>A taky kolem RADNICE, ne jen kolem hráče.</b> Kolonie stojí tam, kde ji hráč
    /// založil, a hráč od ní odchází — jenže bez mřížky nemají kolonisté pod nohama zem
    /// a fyzika je nechá stát na místě, nebo hůř, propadnou terénem, který se mezitím
    /// streamoval.</para>
    ///
    /// <para><b>Naměřeno ve hře</b> (200 lidí, sonda): kolonisté se rozptýlili přes
    /// <b>70 pater na výšku</b> (y 255 až 326) a <b>jen 98 z 200 stálo na zemi</b>. Vypadalo
    /// to jako rozbité vyhýbání — 23 231 překryvů — ale příčina byla tahle: dav se sesypal
    /// z útesu, jakmile mu hráč odletěl z dohledu.</para>
    /// </remarks>
    public void UpdateNavigation(VoxelWorld world, BlockRegistry blocks, Vector3 around)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        CollectMissing(world, around);

        // ROZPOČET SE POČÍTÁ NA POKUSY, NE NA POSTAVENÉ MŘÍŽKY.
        //
        // Dřív se `built` zvyšovalo až po úspěšné stavbě, kdežto `continue` u přeskočeného
        // chunku šlo na další kolo zadarmo. Rozpočet tím platil jen tehdy, když se opravdu
        // stavělo — a při chunku, který v grafu už je nebo který svět nemá, se jelo dál.
        // Jedním tikem se tak dala projít celá fronta.
        //
        // Naměřeno ve hře při 200 kolonistech: <b>UpdateNavigation 59,01 ms</b> proti
        // rozpočtu celého tiku 8 ms, zatímco vlastní tik kolonie stál 2,50 ms. Rozpad po
        // kategoriích to neukazoval, protože sedí uvnitř fáze „pathfinding", která se měří
        // jako celek a hlásila 1,0 ms — čas ležel ve stavbě mřížek, ne v hledání cest.
        //
        // Kontrola „stojí to za pokus" je levná (jeden HashSet a jeden slovník), takže osm
        // pokusů na tik je pořád mnohem míň než jedna stavba za 3,34 ms.
        int attempts = 0;
        while (attempts < GridsPerTick * 8 && _pending.Count > 0)
        {
            attempts++;

            Vector3i chunk = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);

            if (!_built.Add(chunk) || !world.HasChunk(chunk))
            {
                continue;
            }

            long gridFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            _graph.AddChunk(NavGrid.Build(world, blocks, _water, chunk));
            double gridMs = Msec(gridFrom);

            long portalsFrom = System.Diagnostics.Stopwatch.GetTimestamp();

            // PŘÍRŮSTKOVĚ, ne přestavbou celého grafu. Úplná přestavba po každém chunku
            // vyhnala tick p99 na 120 ms — viz NavGraph.AddPortalsFor.
            _graph.AddPortalsFor(chunk);

            // KDE PŘESNĚ TEN ČAS JE. UpdateNavigation stálo naměřených 59 ms; po omezení
            // počtu pokusů 43 ms. To je pořád pětinásobek rozpočtu celého tiku, takže
            // uvnitř je něco dražšího než odhadovaných 3,34 ms na mřížku — a rozdělit to
            // mezi stavbu a portály je jediný způsob, jak to zjistit bez hádání.
            LastGridMs = gridMs;
            LastPortalsMs = Msec(portalsFrom);

            // A DÁL UŽ NE. Jedna mřížka stojí 3,34 ms; druhá by z tiku udělala 6,7 ms
            // jen na navigaci.
            break;
        }
    }

    private void CollectMissing(VoxelWorld world, Vector3 around)
    {
        long collectFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        // Přepočítává se jen když je fronta prázdná — procházet okolí každý tik by bylo
        // dražší než samotná stavba.
        if (_pending.Count > 0)
        {
            return;
        }

        CollectAround(world, VoxelWorld.ToChunkPosition(
            (int)MathF.Floor(around.X),
            (int)MathF.Floor(around.Y),
            (int)MathF.Floor(around.Z)));

        // A KOLEM RADNICE. Kolonie hráče nesleduje; zůstává tam, kde ji založil.
        if (TownHall.IsFounded)
        {
            CollectAround(world, VoxelWorld.ToChunkPosition(
                TownHall.Cell.X, TownHall.Cell.Y, TownHall.Cell.Z));
        }

        LastCollectMs = Msec(collectFrom);
    }

    /// <summary>Kolik stála poslední stavba mřížky. Jen pro měření v selftestu.</summary>
    public double LastGridMs { get; private set; }

    /// <inheritdoc cref="LastGridMs"/>
    public double LastPortalsMs { get; private set; }

    /// <summary>Kolik stálo poslední hledání chybějících chunků. Jen pro měření.</summary>
    public double LastCollectMs { get; private set; }

    private static double Msec(long from) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0
        / System.Diagnostics.Stopwatch.Frequency;

    private void CollectAround(VoxelWorld world, Vector3i centre)
    {
        for (int dy = -NavigationRadiusChunks; dy <= NavigationRadiusChunks; dy++)
        {
            for (int dz = -NavigationRadiusChunks; dz <= NavigationRadiusChunks; dz++)
            {
                for (int dx = -NavigationRadiusChunks; dx <= NavigationRadiusChunks; dx++)
                {
                    var chunk = new Vector3i(centre.X + dx, centre.Y + dy, centre.Z + dz);
                    if (!_built.Contains(chunk) && world.HasChunk(chunk))
                    {
                        _pending.Add(chunk);
                    }
                }
            }
        }
    }

    /// <summary>Ohlásí změnu bloku, aby se navigace srovnala se světem.</summary>
    public void OnBlockChanged(VoxelWorld world, BlockRegistry blocks, Vector3i cell) =>
        _graph.OnBlockChanged(world, blocks, _water, cell.X, cell.Y, cell.Z);

    /// <summary>Označí kvádr k vykopání a probudí lidi.</summary>
    /// <returns>Kolik úkolů přibylo.</returns>
    public int MarkArea(VoxelWorld world, BlockRegistry blocks, Vector3i minimum, Vector3i maximum)
    {
        return MarkAreaReachable(world, blocks, minimum, maximum).Added;
    }

    /// <summary>Jak dopadlo označení oblasti.</summary>
    /// <param name="Added">Kolik úkolů se opravdu zařadilo do fronty.</param>
    /// <param name="Unreachable">Kolik pevných bloků se zamítlo jako nedosažitelné.</param>
    /// <param name="Filtered">Filtrovalo se vůbec? False znamená, že se dosažitelnost nedala spočítat.</param>
    public readonly record struct MarkResult(int Added, int Unreachable, bool Filtered);

    /// <summary>
    /// Označí kvádr k vykopání a NEDOSAŽITELNÉ BUŇKY DO FRONTY VŮBEC NEZAŘADÍ.
    /// </summary>
    /// <remarks>
    /// <para><b>Tiché přijetí nesplnitelné práce je horší chyba než špatná kamera.</b> Dřív
    /// se označilo všechno a 115 ze 130 úkolů skončilo jako odložené, o čemž hráč nevěděl.
    /// Teď se zamítnutá čísla vrací ven, aby je panel mohl ukázat.</para>
    ///
    /// <para><b>Když se dosažitelnost nedá spočítat, nefiltruje se.</b> Bez kolonistů nebo při
    /// vyčerpaném stropu průchodu do šířky by filtr zamítl všechno — odmítnout hráči práci
    /// na základě nedopočítaného výsledku je horší než ji přijmout a nechat ji odložit.</para>
    /// </remarks>
    public MarkResult MarkAreaReachable(
        VoxelWorld world,
        BlockRegistry blocks,
        Vector3i minimum,
        Vector3i maximum)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        // Jeden průchod do šířky na celý výběr, ne hledání cesty na buňku: jedno hledání
        // stojí 0,858 ms, takže u stovek buněk by to byly stovky milisekund.
        _reachable.Rebuild(_graph, _colonists);
        bool filter = _colonists.Count > 0 && _reachable.Complete && _reachable.Count > 0;

        int added = 0;
        int unreachable = 0;

        int lowY = Math.Min(minimum.Y, maximum.Y);
        int highY = Math.Max(minimum.Y, maximum.Y);
        int lowZ = Math.Min(minimum.Z, maximum.Z);
        int highZ = Math.Max(minimum.Z, maximum.Z);
        int lowX = Math.Min(minimum.X, maximum.X);
        int highX = Math.Max(minimum.X, maximum.X);

        for (int y = lowY; y <= highY; y++)
        {
            for (int z = lowZ; z <= highZ; z++)
            {
                for (int x = lowX; x <= highX; x++)
                {
                    ushort block = world.GetBlock(x, y, z);
                    if (block == BlockRegistry.Air || !blocks.IsSolid(block))
                    {
                        continue;
                    }

                    var cell = new Vector3i(x, y, z);
                    if (filter && !_reachable.CanReachBlock(cell))
                    {
                        unreachable++;
                        continue;
                    }

                    _jobs.Add(cell);
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _colonists.WakeIdle();
        }

        return new MarkResult(added, unreachable, filter);
    }

    /// <summary>Jeden simulační krok celé kolonie.</summary>
    public void Tick(VoxelWorld world, BlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        // POŘADÍ: pásy, vkládače, stroje, lidé. Pás musí posunout item dřív, než ho vkládač
        // hledá na výstupu, a stroj má dostat surovinu ještě v tomtéž tiku.
        foreach (BeltSegment belt in _belts)
        {
            belt.Tick();
        }

        _inserters.Tick(_machines);
        FreedByAutomation += _machines.Tick();

        // Koho automatizace pustila od stroje, ten je zase volný — a tohle je to číslo,
        // na kterém stojí celá hra.
        while (_machines.TryDequeueFreed(out int colonist))
        {
            _colonists.Release(colonist);
        }
        _colonists.Tick(world, blocks, _water, _graph, _jobs);

        // Práce, ke které se nedalo dostat, se mohla vykopáním otevřít.
        if (_jobs.OpenCount > 0)
        {
            _colonists.WakeIdle();
        }
    }
}
