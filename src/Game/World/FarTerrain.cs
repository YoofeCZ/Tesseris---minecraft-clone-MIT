using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>
/// Vzdálený terén ve zjednodušené podobě (LOD).
///
/// <para><b>Proč to nejde přes chunky.</b> Dohled se dá zvětšit jen do chvíle, než přestane
/// stíhat meshing — naměřeno, že při dohledu 16 zůstane na konci běhu 1734 chunků ve frontě.
/// Příčina je v tom, že chunk nese plné rozlišení na blok, které je na dálku k ničemu:
/// z pěti kilometrů je jeden blok menší než pixel.</para>
///
/// <para><b>Jak to funguje tady.</b> Vzdálený terén se nebere z chunků vůbec. Skládá se
/// přímo z výškové funkce generátoru po <b>hrubších buňkách</b> — jedna buňka je 4 až 32
/// bloků. Chunky se tam vůbec negenerují ani nedrží v paměti, takže dohled neomezuje ani
/// meshing, ani paměť. Stejný princip mají módy Distant Horizons, Voxy i Bobby.</para>
///
/// <para><b>Co se tím ztrácí:</b> jeskyně, převisy a otesané bloky. Dlaždice je výškopis,
/// takže zná jen povrch. Na vzdálenost, kde začíná, to není poznat.</para>
///
/// <para>Vrcholy mají stejný formát jako chunky (pozice, UV, vrstva textury, stínění),
/// takže se kreslí <b>tímtéž shaderem i pipeline</b> a nepřibyl žádný další průchod.</para>
/// </summary>
public sealed class FarTerrain
{
    /// <summary>Kolik buněk má dlaždice na stranu. Dlaždice pak měří <c>CellsPerTile × Step</c> bloků.</summary>
    /// <para><b>Zkoušeno i 128.</b> Čtyřikrát míň dlaždic srazilo vydávání kreslení
    /// z 11,75 na 3,55 ms, ale <b>p50 zhoršilo z 11,51 na 14,57 ms</b>: dlaždice o 2048
    /// blocích je skoro vždycky aspoň částečně vidět, takže frustum culling přestane
    /// zabírat a kreslí se spousta geometrie mimo obraz. Menší dlaždice vyhrává na
    /// průměrném snímku, větší na nejhorším.</para>
    public const int CellsPerTile = 64;

    /// <summary>
    /// O kolik leží povrch dlaždice níž než plná geometrie chunků.
    ///
    /// <para><b>Bylo tu 0,05 a bylo to málo.</b> Hloubka je <c>D32_SFLOAT</c> s běžnou
    /// (neobrácenou) projekcí, kde rozlišení klesá se čtvercem vzdálenosti:
    /// <c>ε · z² / near</c>. Při <c>near = 0,12</c> vychází na 384 blocích zhruba
    /// <b>0,074 bloku</b> — tedy víc, než byl celý odstup. Dvě plochy se tam proto praly
    /// o hloubku a švy mezi LOD a chunky blikaly.</para>
    ///
    /// <para>0,25 stačí do vzdálenosti kolem 700 bloků, což pokrývá dohled do 22 chunků.
    /// Při větším dohledu se švy můžou začít třepit znovu — pak je potřeba buď zvednout
    /// near plane, nebo obrátit hloubku.</para>
    ///
    /// <para>Vidět to není: na 384 blocích má blok při 1080p asi dva pixely, takže
    /// čtvrt bloku je půl pixelu.</para>
    /// </summary>
    private const float SurfaceDrop = 0.25f;

    /// <summary>
    /// Úrovně podrobnosti. Krok je velikost buňky v blocích, poloměr je vnější hranice
    /// pásma v blocích.
    ///
    /// <para>Dlaždice roste s krokem, takže každé pásmo má zhruba stejný počet dlaždic
    /// i trojúhelníků — bez toho by nejvzdálenější pásmo mělo řádově víc draw callů
    /// než všechna ostatní dohromady.</para>
    /// </summary>
    /// <para><b>Čtyři pásma.</b> Dřív to šlo zapnout jen na dvě: se čtyřmi vyskočilo p99
    /// z 24 na 88 až 104 ms, protože každý buffer měl vlastní <c>vkAllocateMemory</c>.
    /// Po zavedení suballokace (<c>VulkanMemoryPool</c>) kleslo nejhorší nahrání ze 157
    /// na 1,3 ms a čtyři pásma se vešla.</para>
    ///
    /// <para>Pásma se <b>nepřekrývají</b> — výběr je quadtree, viz <see cref="CollectWanted"/>.</para>
    private static readonly (int Step, int Radius)[] Levels =
    [
        (2, 768),
        (4, 1536),
        (8, 3072),
        (16, 6144),
    ];

    private readonly TerrainGenerator _generator;
    private readonly BlockRegistry _registry;
    private readonly JobSystem _jobs;

    private readonly Dictionary<TileKey, TileState> _tiles = [];
    private readonly ConcurrentQueue<FinishedTile> _finished = new();
    private readonly ConcurrentBag<TileWorkspace> _workspaces = [];
    private readonly HashSet<TileKey> _building = [];
    // HashSet, ne List. Hledalo se v něm lineárně pro každou existující dlaždici, což
    // je při 596 dlaždicích 355 tisíc porovnání na jeden přepočet — a přepočet se dělal
    // po každých dvou blocích chůze.
    private readonly HashSet<TileKey> _wanted = [];
    private readonly List<TileKey> _stale = [];

    /// <summary>
    /// Dlaždice, které už nejsou potřeba, ale <b>pořád se kreslí</b>.
    ///
    /// <para>Zahodit je hned znamená díru: nová dlaždice se staví na worker vlákně a než
    /// dorazí, není na tom místě nic a je vidět skrz. Drží se proto, dokud není celá nová
    /// sada nahraná. Cena je pár set dlaždic v paměti navíc, což je proti probliknutí
    /// zanedbatelné.</para>
    /// </summary>
    private readonly HashSet<TileKey> _retired = [];

    /// <summary>Klíče vyřazené v tomhle přepočtu. Sbírají se zvlášť, aby se nesahalo do slovníku při jeho procházení.</summary>
    private readonly List<TileKey> _dropped = [];

    /// <summary>
    /// Pořadí, ve kterém se dlaždice staví: <b>od nejbližší ven</b>.
    ///
    /// <para><b>Proč to musí být seřazené.</b> Dřív se stavělo v pořadí, v jakém dlaždice
    /// ležely ve slovníku, což je pořadí hashů — tedy náhodné. Nejbližší dlaždice, které
    /// zabírají nejvíc obrazu a jsou vidět nejostřeji, se tak klidně stavěly jako
    /// poslední. Při rychlém pohybu se to projeví jako díra přímo před hráčem, zatímco
    /// se staví něco šest kilometrů daleko za zády.</para>
    /// </summary>
    private readonly List<TileKey> _buildOrder = [];

    /// <summary>
    /// Kolikátý přepočet právě běží. Slouží jen k tomu, aby odložené dlaždice nemohly
    /// viset donekonečna — viz <see cref="ReleaseRetiredIfReady"/>.
    /// </summary>
    private int _generation;

    /// <summary>Odložené dlaždice a přepočet, ve kterém se odložily.</summary>
    private readonly Dictionary<TileKey, int> _retiredAt = [];

    private Vector2i _lastCenter;
    private float _lastNearRadius = float.NaN;

    // Příznak místo sentinelové hodnoty. S int.MinValue přeteče rozdíl při prvním
    // volání a Math.Abs na něm spadne — stejná past, na kterou už narazil streamer.
    private bool _hasCenter;

    public FarTerrain(TerrainGenerator generator, BlockRegistry registry, JobSystem jobs)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    /// <summary>Zapnuto? Vypnutím se vzdálený terén přestane kreslit i stavět.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Vnitřní poloměr v blocích: dokud sem sahají chunky, LOD se nekreslí.</summary>
    public float NearRadius { get; set; } = 384f;

    /// <summary>Kolik dlaždic se smí poslat ke stavbě za jeden frame.</summary>
    /// <summary>
    /// Kolik dlaždic se smí poslat ke stavbě za jeden frame.
    ///
    /// <para>Původní 2 nestačily: dlaždic je kolem 170 a při chůzi se jich mění tolik,
    /// že LOD nestíhal a v krajině zůstávaly díry. Stavba běží na worker vláknech,
    /// takže hlavní vlákno tím nezdržuje.</para>
    /// </summary>
    public int BuildsPerFrame { get; set; } = 8;

    /// <summary>
    /// Kolik pásem se používá. Menší číslo znamená kratší dohled a míň práce; jedním
    /// pásmem se LOD prakticky vypne až na nejbližší okolí.
    /// </summary>
    public int ActiveLevels { get; set; } = Levels.Length;

    /// <summary>Kolik pásem je k dispozici celkem.</summary>
    public static int MaxLevels => Levels.Length;

    /// <summary>Nejzazší dosah vzdáleného terénu v blocích. Podle něj se nastavuje mlha.</summary>
    public int OuterRadius => Levels[Math.Clamp(ActiveLevels, 1, Levels.Length) - 1].Radius;

    public int TileCount => _tiles.Count;

    /// <summary>Klíče dlaždic, které mají existovat. Slouží k diagnostice a k testům pokrytí.</summary>
    public IEnumerable<TileKey> Tiles => _tiles.Keys;

    /// <summary>Kolik bloků měří dlaždice dané úrovně na stranu.</summary>
    public static int TileSize(int level) => CellsPerTile * Levels[level].Step;

    public int PendingTiles => _building.Count;

    /// <summary>
    /// Kolik odložených dlaždic se pořád kreslí. Nenulové je normální při chůzi, ale
    /// číslo, které trvale roste, znamená, že se nová sada nikdy nedokončí — a každá
    /// odložená dlaždice stojí draw call a překrývá tu novou.
    /// </summary>
    public int RetiredTiles => _retired.Count;

    /// <summary>Hotové dlaždice připravené k nahrání na GPU. Vybírá je hlavní vlákno.</summary>
    public bool TryTakeFinished(out FinishedTile tile) => _finished.TryDequeue(out tile);

    /// <summary>
    /// Přepočítá, které dlaždice mají existovat. Volá se každý frame, ale plnou práci
    /// dělá jen při přechodu do jiné dlaždice nejjemnější úrovně.
    /// </summary>
    /// <returns>Klíče dlaždic, které se mají zahodit.</returns>
    public IReadOnlyList<TileKey> Update(Vector3 cameraPosition)
    {
        _stale.Clear();

        if (!Enabled)
        {
            // Odložené se musí zahodit taky. Jsou pořád v rendereru a bez tohohle by tam
            // po vypnutí LOD zůstaly viset navždy.
            _stale.AddRange(_tiles.Keys);
            _stale.AddRange(_retired);
            _tiles.Clear();
            _retired.Clear();
            _retiredAt.Clear();
            _buildOrder.Clear();
            return _stale;
        }

        var center = new Vector2i(
            (int)MathF.Floor(cameraPosition.X), (int)MathF.Floor(cameraPosition.Z));

        // Přepočet se váže na velikost dlaždice, ne na velikost buňky. Původně stačil
        // posun o krok nejjemnější úrovně, tedy o **dva bloky** — sada dlaždic se přitom
        // po dvou blocích prakticky nemění, takže se jen zbytečně přepočítávala pořád
        // dokola a stavba nestíhala za tím uklízet.
        int recomputeAfter = CellsPerTile * Levels[0].Step / 2;

        // Změna vnitřního poloměru musí přepočet vynutit sama. Řídí se podle toho, kam až
        // je svět hotový, takže se při rychlém letu mění nezávisle na pohybu — a kdyby se
        // čekalo na ujetí 64 bloků, LOD by zaskočil dovnitř pozdě a mezera by byla vidět.
        // Poloměr je kvantovaný po 64 blocích, takže se to nespouští každý frame.
        bool radiusChanged = _lastNearRadius != NearRadius;

        if (_hasCenter
            && !radiusChanged
            && Math.Abs(center.X - _lastCenter.X) < recomputeAfter
            && Math.Abs(center.Y - _lastCenter.Y) < recomputeAfter
            && _tiles.Count > 0)
        {
            DispatchBuilds();
            ReleaseRetiredIfReady();
            return _stale;
        }

        _lastCenter = center;
        _lastNearRadius = NearRadius;
        _hasCenter = true;
        _generation++;
        CollectWanted(center);

        // Nepotřebné dlaždice se odloží, ne zahodí. Odejdou, až bude nová sada kompletní.
        //
        // Odkládá se jen to, co je nahrané. Dlaždice, která se ještě staví, v rendereru
        // nic nemá, takže není co držet — a kdyby se do odložených dostala, přihlásila by
        // se k životu, který nikdy nezačal.
        _dropped.Clear();
        foreach ((TileKey key, TileState state) in _tiles)
        {
            if (_wanted.Contains(key))
            {
                continue;
            }

            if (state == TileState.Ready && _retired.Add(key))
            {
                _retiredAt[key] = _generation;
            }

            _dropped.Add(key);
        }

        foreach (TileKey key in _dropped)
        {
            _tiles.Remove(key);
        }

        foreach (TileKey key in _wanted)
        {
            if (_tiles.ContainsKey(key))
            {
                continue;
            }

            // Vrátila se dlaždice, která byla odložená? Pak je pořád nahraná v rendereru
            // a stačí ji vzít zpátky mezi platné.
            //
            // Bez tohohle vznikala TRVALÁ DÍRA. Odložené klíče se hromadí přes víc
            // přepočtů (uklidí se až ve chvíli, kdy je celá nová sada hotová), takže při
            // chůzi tam a zpět se týž klíč octl zároveň mezi odloženými i mezi chtěnými.
            // Postavil se znovu, dostal stav Ready — a vzápětí ho úklid odložených
            // vyhodil z rendereru. Ve slovníku přitom zůstal jako Ready, takže ho
            // DispatchBuilds už nikdy nezadal znovu.
            if (_retired.Remove(key))
            {
                _retiredAt.Remove(key);
                _tiles[key] = TileState.Ready;
            }
            else
            {
                _tiles[key] = TileState.Queued;
            }
        }

        RebuildBuildOrder(center);
        DispatchBuilds();
        ReleaseRetiredIfReady();
        return _stale;
    }

    /// <summary>
    /// Seřadí čekající dlaždice od nejbližší ven. Volá se jen při přepočtu — jindy nové
    /// nepřibývají.
    /// </summary>
    private void RebuildBuildOrder(Vector2i center)
    {
        _buildOrder.Clear();

        foreach ((TileKey key, TileState state) in _tiles)
        {
            if (state == TileState.Queued)
            {
                _buildOrder.Add(key);
            }
        }

        _buildOrder.Sort((a, b) =>
            NearDistance(center, a.Level, a.TileX, a.TileZ)
                .CompareTo(NearDistance(center, b.Level, b.TileX, b.TileZ)));
    }

    /// <summary>
    /// Pustí odložené dlaždice, jakmile je nová sada celá nahraná. Do té doby se kreslí
    /// dál a hráč nevidí díru.
    ///
    /// <para><b>Se stropem na stáří.</b> Podmínka „všechno hotové" se při rychlém pohybu
    /// nemusí splnit vůbec — pořád přibývají nové dlaždice a odložené by tak visely
    /// donekonečna. Každá přitom stojí draw call a po otočení kamery se překrývá s tou,
    /// která ji nahradila. Po dvou přepočtech (tedy nejméně 128 blocích cesty) se proto
    /// pouští bez ohledu na zbytek; to je řádově víc času, než stavba potřebuje.</para>
    /// </summary>
    private void ReleaseRetiredIfReady()
    {
        if (_retired.Count == 0)
        {
            return;
        }

        bool allReady = true;
        foreach (TileState state in _tiles.Values)
        {
            if (state != TileState.Ready)
            {
                allReady = false;
                break;
            }
        }

        if (allReady)
        {
            _stale.AddRange(_retired);
            _retired.Clear();
            _retiredAt.Clear();
            return;
        }

        _dropped.Clear();
        foreach (TileKey key in _retired)
        {
            if (_generation - _retiredAt.GetValueOrDefault(key) >= 2)
            {
                _dropped.Add(key);
            }
        }

        foreach (TileKey key in _dropped)
        {
            _retired.Remove(key);
            _retiredAt.Remove(key);
            _stale.Add(key);
        }
    }

    /// <summary>
    /// Je dlaždice pořád potřeba?
    ///
    /// <para>Ptát se musí i nahrávání: dlaždice se staví na worker vlákně a než doběhne,
    /// může se hráč přesunout tak, že už do dosahu nepatří. Bez téhle kontroly se nahrála
    /// a v rendereru <b>zůstala navždy</b> — ze seznamu k zahození totiž vypadla dřív, než
    /// se stihla nahrát, takže ji už nikdo neodebral.</para>
    /// </summary>
    public bool IsWanted(TileKey key) => _tiles.ContainsKey(key) || _retired.Contains(key);

    /// <summary>
    /// Zahodí výsledek stavby, který už není k ničemu.
    ///
    /// <para><b>Musí se zavolat.</b> Bez toho zůstane klíč navždy v <c>_building</c>
    /// a <see cref="DispatchBuilds"/> ho už nikdy nezadá znovu — dlaždice zůstane věčně
    /// ve stavu „ve frontě" a na jejím místě je <b>trvalá díra</b>. Přesně tuhle chybu
    /// zavedla kontrola „je dlaždice pořád chtěná?" při nahrávání: přeskočila nahrání,
    /// ale klíč neuvolnila.</para>
    /// </summary>
    public void Discard(TileKey key) => _building.Remove(key);

    /// <summary>Zapíše, že dlaždice je nahraná a nemusí se stavět znovu.</summary>
    public void MarkUploaded(TileKey key)
    {
        _building.Remove(key);

        if (_tiles.ContainsKey(key))
        {
            _tiles[key] = TileState.Ready;
        }
    }

    /// <summary>
    /// Vybere dlaždice jako <b>quadtree</b>: začne od nejhrubší úrovně a každou dlaždici
    /// buď použije, nebo ji rozdělí na čtyři jemnější.
    ///
    /// <para><b>Proč ne pásma zvlášť.</b> Původní verze procházela úrovně nezávisle
    /// a dlaždici do pásma zařadila, když se do něj vešel její nejvzdálenější roh.
    /// Jenže dlaždice, která hranici pásma <b>přesahuje</b>, tím prošla do obou úrovní
    /// naráz — a dvě plochy s různým rozlišením se pak na témž místě praly o hloubku.
    /// Přesně to bylo z dálky vidět jako míchanice, kde se terén „kombinuje".</para>
    ///
    /// <para><b>Proč to jde.</b> Mřížky úrovní jsou do sebe vnořené: dlaždice měří
    /// <c>64 · krok</c> bloků a krok se mezi úrovněmi zdvojnásobuje, takže jedna hrubá
    /// dlaždice je přesně 2×2 jemnější. Rozdělení je tím bezešvé a každý bod pokrývá
    /// právě jedna dlaždice — žádný překryv, žádná mezera.</para>
    ///
    /// <para><b>Vedlejší zisk:</b> zmizely duplicitní dlaždice, tedy i jejich draw cally
    /// a jejich overdraw.</para>
    /// </summary>
    private void CollectWanted(Vector2i center)
    {
        _wanted.Clear();

        int deepest = Math.Clamp(ActiveLevels, 1, Levels.Length) - 1;
        (int step, int radius) = Levels[deepest];
        int tileSize = CellsPerTile * step;

        int minTileX = (int)MathF.Floor((center.X - radius) / (float)tileSize);
        int maxTileX = (int)MathF.Floor((center.X + radius) / (float)tileSize);
        int minTileZ = (int)MathF.Floor((center.Y - radius) / (float)tileSize);
        int maxTileZ = (int)MathF.Floor((center.Y + radius) / (float)tileSize);

        for (int tz = minTileZ; tz <= maxTileZ; tz++)
        {
            for (int tx = minTileX; tx <= maxTileX; tx++)
            {
                // Vnější ořez se dělá JEN tady, na nejhrubší úrovni. Uvnitř rekurze už ne:
                // dlaždice, která vznikla rozdělením, se musí použít vždycky, i když sama
                // leží až za hranicí svého pásma. Jinak by po rodiči zbylo prázdné místo.
                if (NearDistance(center, deepest, tx, tz) <= radius)
                {
                    Subdivide(center, deepest, tx, tz);
                }
            }
        }
    }

    private void Subdivide(Vector2i center, int level, int tileX, int tileZ)
    {
        // Sahá dlaždice do pásma jemnější úrovně? Pak se místo ní použijí její čtyři děti.
        if (level > 0 && NearDistance(center, level, tileX, tileZ) < Levels[level - 1].Radius)
        {
            for (int dz = 0; dz < 2; dz++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    Subdivide(center, level - 1, (tileX * 2) + dx, (tileZ * 2) + dz);
                }
            }

            return;
        }

        // Dlaždice, která celá leží uvnitř blízkého okolí, se nestaví — tam kreslí chunky.
        if (FarDistance(center, level, tileX, tileZ) > NearRadius)
        {
            _wanted.Add(new TileKey(level, tileX, tileZ));
        }
    }

    /// <summary>Vzdálenost k nejbližšímu bodu dlaždice.</summary>
    private static float NearDistance(Vector2i center, int level, int tileX, int tileZ)
    {
        int tileSize = CellsPerTile * Levels[level].Step;
        float dx = Math.Clamp(center.X, tileX * tileSize, ((tileX + 1) * tileSize) - 1) - center.X;
        float dz = Math.Clamp(center.Y, tileZ * tileSize, ((tileZ + 1) * tileSize) - 1) - center.Y;

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Vzdálenost k nejvzdálenějšímu rohu dlaždice.</summary>
    private static float FarDistance(Vector2i center, int level, int tileX, int tileZ)
    {
        int tileSize = CellsPerTile * Levels[level].Step;
        float dx = MathF.Max(
            MathF.Abs(center.X - (tileX * tileSize)),
            MathF.Abs(center.X - (((tileX + 1) * tileSize) - 1)));
        float dz = MathF.Max(
            MathF.Abs(center.Y - (tileZ * tileSize)),
            MathF.Abs(center.Y - (((tileZ + 1) * tileSize) - 1)));

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Zadá další dlaždice ke stavbě, <b>od nejbližší ven</b> podle <see cref="_buildOrder"/>.
    /// Zpracované položky se ze seznamu odebírají, takže se nechodí pořád dokola.
    /// </summary>
    /// <summary>
    /// Kolik dlaždic se smí stavět <b>naráz</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle je strop, který v kódu chyběl.</b> <see cref="BuildsPerFrame"/> říká,
    /// kolik se jich pošle za snímek — jenže při stopadesáti snímcích za vteřinu je to přes
    /// tisíc dlaždic za sekundu, a když je workeři nestíhají, fronta jen roste. Naměřeno
    /// při rychlém letu: <b>448 dlaždic ve stavbě naráz</b>, workery obsadily procesor
    /// a hlavní vlákno dostávalo snímky dlouhé přes sto milisekund.</para>
    ///
    /// <para>Trojnásobek počtu workerů je kompromis: každý má co dělat i pár úkolů v záloze,
    /// ale fronta nenaroste do stovek. Dlaždice, které se teď nezadaly, nikam nezmizí —
    /// zůstanou v <see cref="_buildOrder"/> a přijdou na řadu v dalších snímcích.</para>
    /// </remarks>
    private int MaxConcurrentBuilds => Math.Max(4, _jobs.WorkerCount * 3);

    private void DispatchBuilds()
    {
        int sent = 0;
        int index = 0;

        // Nezadává se nic dalšího, dokud se rozestavěné nedodělají. Bez toho se fronta
        // plnila rychleji, než ji stačily workery vyprazdňovat.
        int capacity = MaxConcurrentBuilds - _building.Count;

        while (index < _buildOrder.Count && sent < BuildsPerFrame && sent < capacity)
        {
            TileKey key = _buildOrder[index];

            // Dlaždice mezitím mohla vypadnout z dosahu nebo se už postavit.
            if (!_tiles.TryGetValue(key, out TileState state) || state != TileState.Queued)
            {
                index++;
                continue;
            }

            if (!_building.Add(key))
            {
                index++;
                continue;
            }

            index++;
            sent++;

            // Střed i vnitřní poloměr se zapamatují TEĎ a vezou se do stavby. Kdyby si je
            // worker přečetl sám, mohly by se mezitím změnit a dlaždice by vznikla podle
            // jiného stavu, než pro který byla zadaná.
            TileKey captured = key;
            Vector2i center = _lastCenter;
            float nearRadius = NearRadius;

            _jobs.Submit(() => Build(captured, center, nearRadius), JobPriority.Background);
        }

        if (index > 0)
        {
            _buildOrder.RemoveRange(0, index);
        }
    }

    private void Build(TileKey key, Vector2i center, float nearRadius)
    {
        TileWorkspace workspace = _workspaces.TryTake(out TileWorkspace? pooled) ? pooled : new TileWorkspace();
        workspace.Mesh.Clear();
        workspace.Water.Clear();
        workspace.Plants.Clear();

        int step = Levels[key.Level].Step;
        int tileSize = CellsPerTile * step;
        int originX = key.TileX * tileSize;
        int originZ = key.TileZ * tileSize;

        // Les se staví do každé dlaždice. Že se nesmí kreslit blízko hráči, řeší až
        // shader vzdáleného terénu (far.frag) svým blízkým řezem — a řeší to správně,
        // protože fragment zná svou vzdálenost každý snímek znovu.
        //
        // Zkoušel jsem to rozhodovat tady, při stavbě, podle vzdálenosti dlaždice od
        // hráče. NEFUNGUJE TO: dlaždice se postaví jednou a hráč se k ní pak přiblíží,
        // takže rozhodnutí zapečené do meshe za chvíli neplatí. Projevilo se to tak, že
        // koruny z LOD stály mezi skutečnými stromy.
        _ = center;
        _ = nearRadius;

        // Buňka dostane NEJNIŽŠÍ výšku ze svého území, ne výšku jednoho svého rohu.
        //
        // Původně se vzorkoval jen levý dolní roh a ta výška platila pro celou buňku.
        // Na svahu tím buňka vylezla nad skutečný terén — a jakmile se LOD začal kreslit
        // blízko hráče, koukal skrz plnou geometrii ven a krajina vypadala jako schodiště
        // z dvoublokových teras.
        //
        // <b>U nejjemnější úrovně se vzorkuje po blocích</b> (`sub = step`), takže minimum
        // je přesné a dlaždice se pod terén schová vždycky. Jen minimum ze čtyř rohů
        // nestačilo: test našel místo, kde se terén mezi vzorky propadl o blok níž.
        // U hrubších úrovní by přesné minimum stálo až 256 vzorků na buňku a k ničemu by
        // nebylo — ty leží tak daleko, že se s chunky nikdy nepotkají.
        int sub = step <= 2 ? step : 1;
        int sampleStep = step / sub;

        const int Padded = CellsPerTile + 2;
        int corners = (Padded * sub) + 1;

        // Pole se půjčují z recyklované pracovní paměti, ne alokují. Dřív se pro každou
        // dlaždici zakládala nová, včetně MeshBufferu, který u velké dlaždice naroste
        // na megabajty a při zdvojnásobování po sobě nechá ještě jednou tolik odpadu.
        // Naměřeno: 1,2 MB alokací na frame a 30 úklidů gen2 za běh, z toho nejdelší
        // frame 28 ms.
        int[] samples = workspace.Samples;
        int[] heights = workspace.Heights;
        Biome[] biomes = workspace.Biomes;

        for (int sz = 0; sz < corners; sz++)
        {
            for (int sx = 0; sx < corners; sx++)
            {
                int worldX = originX + ((sx - sub) * sampleStep);
                int worldZ = originZ + ((sz - sub) * sampleStep);

                Column column = _generator.ColumnAt(worldX, worldZ);

                // DNO SE NECHÁVÁ NA SVÉ VÝŠCE, i pod hladinou.
                //
                // <b>Dřív se zvedalo na hladinu</b> (`Math.Max(Surface, SeaLevel)`), aby na
                // místě moří nebyly jámy. Mělo to ale cenu, která se ukázala až ve hře:
                // pod vzdálenou vodou nebyla žádná geometrie, takže se přes ni nedalo
                // vidět dno a její barva musela být uhodnutá. Ať se hádala jakkoli —
                // konstantou i hloubkou interpolovanou přes vrcholy — na hranici LOD
                // vznikl šev a přes moře běžely čtvercové a trojúhelníkové artefakty.
                //
                // S opravdovým dnem odpadá hádání celé: `far.frag` na něj pustí tentýž
                // `ApplyWater`, jaký počítá dno u chunků, takže mělčina vyjde světlá,
                // hloubka tmavá a přechod je spojitý ze své podstaty. Jáma nevznikne,
                // protože se nad dno položí hladina jako samostatná vrstva.
                samples[sx + (sz * corners)] = column.Surface;

                // Biom se veze s tím vzorkem, který leží přesně v rohu buňky — ať se
                // sloupec nepočítá podruhé.
                if (sx % sub == 0 && sz % sub == 0 && sx / sub < Padded && sz / sub < Padded)
                {
                    biomes[(sx / sub) + ((sz / sub) * Padded)] = column.Biome;
                }
            }
        }

        int lowest = int.MaxValue;
        int highest = int.MinValue;

        for (int cz = 0; cz < Padded; cz++)
        {
            for (int cx = 0; cx < Padded; cx++)
            {
                // Buňka pokrývá vzorky od (cx·sub, cz·sub) po (cx·sub + sub, cz·sub + sub)
                // včetně — krajní vzorky patří i sousední buňce, což je správně: na hraně
                // musí obě sedět na téže výšce, jinak by mezi nimi zůstala škvíra.
                int height = int.MaxValue;
                int total = 0;
                int count = 0;

                for (int sz = 0; sz <= sub; sz++)
                {
                    int row = ((cz * sub) + sz) * corners;

                    for (int sx = 0; sx <= sub; sx++)
                    {
                        int sample = samples[(cx * sub) + sx + row];

                        height = Math.Min(height, sample);
                        total += sample;
                        count++;
                    }
                }

                // POD HLADINOU SE BERE PRŮMĚR, NAD NÍ MINIMUM.
                //
                // Minimum je tu proto, aby se dlaždice schovala pod plnou geometrii chunků
                // a nevykukovala z ní. Pod vodou ale žádné takové riziko není — hladina
                // dno zakryje — a minimum tam škodí: posadí dno níž, než doopravdy je,
                // takže vzdálené moře vypadá hlubší a tedy tmavší než blízké.
                //
                // <b>Právě z toho byl viditelný pás na hladině</b> přesně tam, kde končí
                // chunky a začíná LOD. Barvu vody totiž počítá týž vzorec na obou stranách,
                // jenže dostane jinou hloubku.
                //
                // Průměr se ještě drží pod hladinou, aby dno nikde nevystoupilo nad vodu
                // a neudělalo z mělčiny ostrov.
                int average = (total + (count / 2)) / count;

                if (height < TerrainGenerator.SeaLevel)
                {
                    height = Math.Min(average, TerrainGenerator.SeaLevel - 1);
                }

                heights[cx + (cz * Padded)] = height;

                lowest = Math.Min(lowest, height);
                highest = Math.Max(highest, height);
            }
        }

        // Ztmavení povrchu podle podrostu se použije JEN tam, kde se rostliny nekreslí.
        //
        // Je to náhražka za trávu, ne doplněk k ní. Když platilo obojí, byl povrch tmavý
        // (protože „tam roste podrost") a přes něj se ten podrost ještě nakreslil doopravdy
        // — z toho byl tvrdý kontrast světlých stébel na tmavé zemi.
        BuildMesh(workspace, step, originX, originZ, withForest: true, shadeUndergrowth: key.Level >= PlantLevels);

        // Stromy jen v nejbližších pásmech. Dál je z koruny míň než pixel a geometrie
        // by se vydala zbytečně; tam les zastupuje barva povrchu.
        //
        // KORUNY PATŘÍ MEZI ROSTLINY, NE DO NEPRŮHLEDNÉHO MESHE. Právě proto z nich dřív
        // byly plné krychle: neprůhledný průchod alfu vůbec nečte, takže z listové textury
        // zbyla jen barva a obrys dal tvar geometrie. Ve výřezovém průchodu dělá obrys
        // alfa, což je celý smysl karet.
        if (key.Level < TreeLevels)
        {
            BuildCanopies(workspace.Plants, tileSize, originX, originZ, ref lowest, ref highest);
        }

        // Rostliny ještě blíž než stromy. Stéblo je JEDEN blok, takže na 768 blocích už
        // padne pod pixel — dál by se vydávala geometrie za neviditelné. Koruna je proti
        // tomu pět bloků široká a vydrží třikrát dál.
        if (key.Level < PlantLevels)
        {
            BuildPlants(workspace.Plants, tileSize, originX, originZ);
        }

        _finished.Enqueue(new FinishedTile(
            key,
            workspace.Mesh,
            workspace.Water,
            workspace.Plants,
            new Vector3(originX, lowest - 1, originZ),
            new Vector3(originX + tileSize, highest + 1, originZ + tileSize))
        {
            Workspace = workspace,
        });
    }

    /// <summary>
    /// Vrátí pracovní paměť dlaždice do zásoby.
    ///
    /// <para><b>Musí se zavolat</b> na každou dlaždici vytaženou přes
    /// <see cref="TryTakeFinished"/> — i na tu zahozenou. Bez toho se pracovní paměť
    /// nerecykluje a je z ní zase odpad pro úklid paměti.</para>
    /// </summary>
    public void Recycle(FinishedTile tile)
    {
        if (tile.Workspace is not null)
        {
            _workspaces.Add(tile.Workspace);
        }
    }

    /// <summary>
    /// Kolik nejjemnějších pásem dostane skutečné stromy.
    ///
    /// <para><b>Tři pásma sahají do 3072 bloků.</b> Bylo tu dvě, tedy 1536, s odůvodněním,
    /// že dál má koruna při 720p míň než dva pixely. Platí to pro jednotlivý strom, ale ne
    /// pro to, co je vidět: souvislý les na vzdáleném ostrově je i z těch tří kilometrů
    /// rozeznatelná členitá plocha, kdežto plochá zelená barva vypadá jako holý kopec.
    /// Rozdíl je znát právě na obzoru, kam se dívá nejvíc.</para>
    ///
    /// <para>Čtvrté pásmo (6144 bloků) je k dispozici taky, ale jeho dlaždice měří kilometr
    /// na stranu a <see cref="BuildCanopies"/> prochází každý sloupec — cena stavby roste
    /// se čtvercem kroku. Zapíná se v menu položkou „LOD pasem se stromy".</para>
    ///
    /// <para>Změna se projeví až na nově postavených dlaždicích; ty hotové se nepřestaví.</para>
    /// </summary>
    public static int TreeLevels { get; set; } = 3;

    /// <summary>
    /// Kolik nejjemnějších pásem dostane skutečné rostliny — trávu, kapradí a kytky.
    ///
    /// <para><b>Jen jedno pásmo, tedy 768 bloků.</b> Míň než u stromů, a je to fyzika, ne
    /// lakota: stéblo je <b>jeden blok</b>, kdežto koruna pět. Na 768 blocích padne blok
    /// při 720p pod jeden pixel a dál by se vydávala geometrie za něco, co se nezobrazí.</para>
    ///
    /// <para>Do téhle vzdálenosti končila vegetace na hranici dohledu chunků (12 chunků,
    /// tedy 384 bloků), protože LOD výřezovou vrstvu vůbec neměl a za ní byl jen holý
    /// povrch. Tímhle se hranice posouvá na dvojnásobek.</para>
    ///
    /// <para>Nula rostliny vypne úplně. Změna se projeví až na nově postavených dlaždicích.</para>
    /// </summary>
    public static int PlantLevels { get; set; } = 1;

    /// <summary>
    /// Vysází trávu, kapradí a kytky do vzdáleného terénu.
    /// </summary>
    /// <remarks>
    /// <para>Rozhoduje <b>tentýž</b> výpočet jako u chunků, takže na hranici pásma rostlina
    /// nezmizí ani nepřeskočí jinam — jen se přestane kreslit, až na ni dojde mez.</para>
    ///
    /// <para><b>Prochází se každý sloupec.</b> Trs zabírá jediný sloupec a s krokem po dvou
    /// by polovina porostu zmizela. Je to únosné jen v nejjemnějším pásmu, kde má dlaždice
    /// 128 bloků na stranu.</para>
    /// </remarks>
    private void BuildPlants(MeshBuffer plants, int tileSize, int originX, int originZ)
    {
        float shade = FaceShading.ForAxis(1, positive: true);

        for (int z = 0; z < tileSize; z++)
        {
            for (int x = 0; x < tileSize; x++)
            {
                int worldX = originX + x;
                int worldZ = originZ + z;

                ushort plant = _generator.FarPlantAt(worldX, worldZ, out int surface);

                if (plant == BlockRegistry.Air)
                {
                    continue;
                }

                // Předměty ležící na zemi jsou v LOD menší než pixel. Kreslit je jako
                // plnohodnotný kříž rostliny by je naopak zvětšilo do dálky.
                if (_registry.ShapeOf(plant) != BlockShape.Cross)
                {
                    continue;
                }

                // Rostlina stojí na povrchu, který je u LOD o SurfaceDrop níž než u chunků.
                // Bez toho by trsy visely kousek nad zemí.
                float bottom = surface + 1f - SurfaceDrop;

                AddPlantCross(
                    plants, worldX, bottom, worldZ,
                    _registry.FaceLayer(plant, BlockFace.PosX),
                    PlantHash(worldX, surface, worldZ),
                    shade);
            }
        }
    }

    /// <summary>
    /// Dvě svislé plochy zkřížené přes střed sloupce, každá z obou stran.
    /// </summary>
    /// <remarks>
    /// Rozměry i rozptyl jsou <b>opsané z <c>ChunkMesher.EmitCross</c></b>, aby trs
    /// v LOD stál na témž místě a měl tutéž velikost jako ten v chunku. Kdyby se lišily,
    /// bylo by při přechodu hranice vidět cuknutí.
    /// </remarks>
    private static void AddPlantCross(
        MeshBuffer mesh, int x, float bottom, int z, float layer, uint hash, float shade)
    {
        const float Inset = 0.0854f;

        float offsetX = (((int)((hash >> 16) % 7u)) - 3) * 0.025f;
        float offsetZ = (((int)((hash >> 20) % 7u)) - 3) * 0.025f;

        float x0 = x + Inset + offsetX;
        float x1 = x + (1f - Inset) + offsetX;
        float z0 = z + Inset + offsetZ;
        float z1 = z + (1f - Inset) + offsetZ;

        float top = bottom + 0.85f + (((hash >> 8) % 16u) * 0.01f);

        AddPlantPlane(mesh, x0, z0, x1, z1, bottom, top, layer, shade);
        AddPlantPlane(mesh, x0, z1, x1, z0, bottom, top, layer, shade);
    }

    /// <summary>
    /// Jedna plocha rostliny, vložená <b>dvakrát s opačným navíjením</b>.
    /// </summary>
    /// <remarks>
    /// Výřezový průchod zahazuje odvrácené stěny, takže jednostranná tráva by z jedné
    /// strany zmizela. Stejný důvod jako v <c>ChunkMesher</c>.
    /// </remarks>
    private static void AddPlantPlane(
        MeshBuffer mesh, float x0, float z0, float x1, float z1,
        float bottom, float top, float layer, float shade)
    {
        var a0 = new Vector3(x0, bottom, z0);
        var b0 = new Vector3(x1, bottom, z1);
        var b1 = new Vector3(x1, top, z1);
        var a1 = new Vector3(x0, top, z0);

        // UV zatažené o půl texelu, aby filtrování nesáhlo přes hranu dlaždice — sampler
        // opakuje, takže by tam vzalo obsah z protější strany. Vysvětlení v ChunkMesher.Plane;
        // hodnota MUSÍ sedět, jinak by trsy na hranici pásma změnily kresbu.
        const float Bleed = 0.5f / 64f;

        var uv00 = new Vector2(Bleed, 1f - Bleed);
        var uv10 = new Vector2(1f - Bleed, 1f - Bleed);
        var uv11 = new Vector2(1f - Bleed, Bleed);
        var uv01 = new Vector2(Bleed, Bleed);

        mesh.AddQuad(a0, b0, b1, a1, uv00, uv10, uv11, uv01, layer, shade, shade, shade, shade, false);
        mesh.AddQuad(b0, a0, a1, b1, uv10, uv00, uv01, uv11, layer, shade, shade, shade, shade, false);
    }

    /// <summary>Hash pozice rostliny. Musí sedět na <c>ChunkMesher.PlantHash</c>.</summary>
    private static uint PlantHash(int x, int y, int z)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)y) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }

    /// <summary>
    /// Nakreslí koruny stromů jako křížené karty s výřezem.
    ///
    /// <para><b>Býval to kvádr a vypadalo to přesně tak</b> — v dálce stály nad lesem
    /// zelené krychle. Odůvodnění znělo, že na stovkách metrů není rozdíl mezi koulí
    /// a krychlí poznat. Není to pravda a je vidět proč: krychle má <b>tvrdou svislou
    /// siluetu a vodorovný vršek</b>, což jsou dva tvary, které v přírodě nic nemá, takže
    /// je oko najde okamžitě. Barva ani počet pixelů s tím nenadělá nic.</para>
    ///
    /// <para>Karty s výřezem naopak dostanou obrys z <b>alfy listové textury</b>, tedy
    /// roztřepený a nepravidelný — a je to tatáž textura, jakou má koruna zblízka, takže
    /// se pásma k sobě hlásí. Stojí to šest čtyřúhelníků na strom proti pěti u kvádru.</para>
    ///
    /// <para><b>Prochází se každý sloupec dlaždice</b>, protože kmen zabírá jediný sloupec
    /// a s krokem po dvou by polovina stromů zmizela. Je to levné jen díky tomu, že
    /// <c>TryCanopyAt</c> odmítne drtivou většinu sloupců jediným hashem.</para>
    /// </summary>
    private void BuildCanopies(
        MeshBuffer mesh, int tileSize, int originX, int originZ, ref int lowest, ref int highest)
    {
        for (int z = 0; z < tileSize; z++)
        {
            for (int x = 0; x < tileSize; x++)
            {
                int worldX = originX + x;
                int worldZ = originZ + z;

                if (!_generator.TryCanopyAt(worldX, worldZ, out TreePlanter.Canopy canopy))
                {
                    continue;
                }

                AddCanopy(mesh, worldX, worldZ, canopy);

                // Obal dlaždice musí korunu obsáhnout, jinak ji frustum culling zahodí
                // dřív, než zmizí z obrazu.
                lowest = Math.Min(lowest, canopy.BottomY);
                highest = Math.Max(highest, canopy.TopY + 1);
            }
        }
    }

    /// <summary>
    /// Koruna z křížených karet: dvě svislé přes střed a jedna vodorovná pod vrcholem.
    /// </summary>
    /// <remarks>
    /// <para><b>Vodorovná karta tu není navíc.</b> Dvě svislé karty vypadají při pohledu
    /// shora jako ležaté „X" — tenký kříž místo koruny. A shora se na les kouká pokaždé,
    /// když hráč stojí na kopci, což je přesně ta situace, kde je vzdálený terén nejvíc
    /// vidět. Leží pod vrcholem, ne na něm, aby ji svislé karty přerůstaly a nebyla vidět
    /// jako víko.</para>
    ///
    /// <para><b>Natočení se losuje z polohy.</b> Bez toho by všechny koruny v lese mířily
    /// stejně a při pohledu z boku by se zarovnaly do řad — týž problém, jaký má mřížka
    /// kvádrů, jen jinak.</para>
    ///
    /// <para>Karty jsou <b>čtvercové</b>: šířka koruny v obou směrech, výška od nasazení
    /// po vrchol. Textura se natahuje přes celou kartu, nedlaždicuje se — obrys má dělat
    /// alfa jedné koruny, ne opakovaný vzor.</para>
    /// </remarks>
    private void AddCanopy(MeshBuffer mesh, int worldX, int worldZ, TreePlanter.Canopy canopy)
    {
        float radius = canopy.Radius + 0.5f;

        float centreX = worldX + 0.5f;
        float centreZ = worldZ + 0.5f;

        float bottom = canopy.BottomY;
        float top = canopy.TopY + 1f;

        // Tytéž tři vrstvy textury jako u listí zblízka, viz ChunkMesher.EmitFoliage:
        // horní stěna, boční a spodní jsou tři různé karty téhož chomáče.
        float first = _registry.FaceLayer(canopy.Leaves, BlockFace.PosX);
        float second = _registry.FaceLayer(canopy.Leaves, BlockFace.NegY);
        float crownTop = _registry.FaceLayer(canopy.Leaves, BlockFace.PosY);

        float shade = FaceShading.ForAxis(1, positive: true);

        uint hash = PlantHash(worldX, canopy.TopY, worldZ);

        // Natočení v rozsahu čtvrt otáčky stačí: dvě kolmé karty se po devadesáti stupních
        // opakují, takže větší rozsah by nic nepřidal.
        float angle = ((hash >> 12) % 64u) / 64f * (MathF.PI * 0.5f);

        float cos = MathF.Cos(angle) * radius;
        float sin = MathF.Sin(angle) * radius;

        AddPlantPlane(
            mesh, centreX - cos, centreZ - sin, centreX + cos, centreZ + sin, bottom, top, first, shade);

        AddPlantPlane(
            mesh, centreX + sin, centreZ - cos, centreX - sin, centreZ + cos, bottom, top, second, shade);

        // Vodorovná karta leží v pěti šestinách výšky koruny — dost vysoko, aby ji bylo
        // shora vidět dřív než terén pod stromem, a dost nízko, aby ji svislé karty
        // přerostly.
        float discY = bottom + ((top - bottom) * 0.82f);

        AddCanopyDisc(mesh, centreX, discY, centreZ, cos, sin, crownTop, shade);
    }

    /// <summary>
    /// Vodorovná karta koruny, vložená <b>dvakrát s opačným navíjením</b>.
    /// </summary>
    /// <remarks>
    /// Obě navíjení jsou nutná ze stejného důvodu jako u svislých karet: výřezový průchod
    /// zahazuje odvrácené stěny, takže zespodu by karta zmizela. Zespodu se na ni přitom
    /// kouká pokaždé, když hráč stojí v údolí pod lesem.
    /// </remarks>
    private static void AddCanopyDisc(
        MeshBuffer mesh, float centreX, float y, float centreZ, float cos, float sin,
        float layer, float shade)
    {
        // Rohy natočeného čtverce. Tytéž dvě osy jako u svislých karet, aby disk seděl
        // do jejich obrysu a netrčel z něj.
        var a = new Vector3(centreX - cos + sin, y, centreZ - sin - cos);
        var b = new Vector3(centreX + cos + sin, y, centreZ + sin - cos);
        var c = new Vector3(centreX + cos - sin, y, centreZ + sin + cos);
        var d = new Vector3(centreX - cos - sin, y, centreZ - sin + cos);

        const float Bleed = 0.5f / 64f;

        var uv00 = new Vector2(Bleed, Bleed);
        var uv10 = new Vector2(1f - Bleed, Bleed);
        var uv11 = new Vector2(1f - Bleed, 1f - Bleed);
        var uv01 = new Vector2(Bleed, 1f - Bleed);

        mesh.AddQuad(a, b, c, d, uv00, uv10, uv11, uv01, layer, shade, shade, shade, shade, false);
        mesh.AddQuad(d, c, b, a, uv01, uv11, uv10, uv00, layer, shade, shade, shade, shade, false);
    }

    /// <summary>
    /// Sestaví geometrii dlaždice.
    ///
    /// <para><b>Vršky se slučují do obdélníků.</b> V rovině má vedle sebe stovky buněk
    /// stejnou výšku i materiál a kreslit každou zvlášť je čirá režie — je to totéž, co
    /// dělá greedy meshing u chunků, jen na dvojrozměrné mřížce. Boky se slučují taky,
    /// podél sdílené hrany.</para>
    /// </summary>
    /// <param name="withForest">
    /// Obarvit zapojený les nazeleno? Jen u dlaždic za dosahem chunků — blíž by se
    /// zelený LOD pral s bílým sněhem chunků všude, kde kvůli hrubším buňkám vykoukne
    /// z terénu.
    /// </param>
    /// <param name="shadeUndergrowth">
    /// Ztmavit travnaté plochy podle hustoty podrostu? Jen v pásmech, kde se rostliny
    /// nekreslí jako geometrie — jinak by se týž podrost započítal dvakrát.
    /// </param>
    private void BuildMesh(
        TileWorkspace workspace, int step, int originX, int originZ, bool withForest, bool shadeUndergrowth)
    {
        const int Padded = CellsPerTile + 2;

        MeshBuffer mesh = workspace.Mesh;
        int[] heights = workspace.Heights;
        Biome[] biomes = workspace.Biomes;

        // Materiál se spočítá dopředu: sloučení potřebuje porovnávat i blok, ne jen výšku.
        ushort[] blocks = workspace.Blocks;
        for (int cz = 0; cz < CellsPerTile; cz++)
        {
            for (int cx = 0; cx < CellsPerTile; cx++)
            {
                int index = (cx + 1) + ((cz + 1) * Padded);
                float slope = MathF.Max(
                    MathF.Abs(heights[index + 1] - heights[index - 1]),
                    MathF.Abs(heights[index + Padded] - heights[index - Padded]))
                    / (2f * step);

                // POD HLADINOU SE KRESLÍ DNO, ne voda.
                //
                // Varianta se souřadnicemi vrací pro podmořské sloupce rovnou vodní blok
                // (viz TerrainGenerator.FarSurfaceBlock). Dávalo to smysl, dokud mělo LOD
                // dno zvednuté na hladinu a moře bylo prostě plocha vodních buněk. Teď se
                // hladina pokládá jako samostatná vrstva nad skutečné dno, takže se tady
                // potřebuje materiál dna — jinak by pod vodou nebylo vidět nic než další
                // voda a celá změna by neměla smysl.
                bool underwater = heights[index] < TerrainGenerator.SeaLevel;

                // Souřadnice se předávají kvůli lesu: ten se do LOD kreslí barvou, ne
                // stromy, protože jednotlivý strom je na kilometry menší než pixel.
                // Pod vodou les neroste, takže tam ta varianta nemá co dělat — místo ní
                // rozhoduje výška, aby dno dostalo písek jako u chunků a ne trávu.
                blocks[cx + (cz * CellsPerTile)] = withForest && !underwater
                    ? _generator.FarSurfaceBlock(
                        biomes[index], slope, originX + (cx * step), originZ + (cz * step))
                    : _generator.FarSurfaceBlockAt(biomes[index], slope, heights[index]);
            }
        }

        bool[] merged = workspace.Merged;
        Array.Clear(merged);

        float shadeTop = FaceShading.ForAxis(1, positive: true);

        // JAS POVRCHU PODLE PODROSTU.
        //
        // Prochází se celý Padded rozsah, ne jen buňky dlaždice: hodnoty se berou v ROZÍCH
        // sloučených obdélníků, takže se sahá o jednu buňku za okraj. Sousední obdélník pak
        // na společné hraně čte tutéž hodnotu a nevznikne šev — na tomhle se dnes už jednou
        // pohořelo u hloubky vody.
        float[] shades = workspace.Shades;

        ushort grassBlock = _registry.IndexOf("tesseris:grass");
        ushort dryGrassBlock = _registry.IndexOf("tesseris:dry_grass");

        for (int pz = 0; pz < Padded; pz++)
        {
            for (int px = 0; px < Padded; px++)
            {
                int wx = originX + ((px - 1) * step);
                int wz = originZ + ((pz - 1) * step);

                // Tentýž vzorec jako v TreePlanter.PickPlant: 0,18 na mýtině, 0,48 v hustém
                // lese. Ztmavení je jen třetinové, protože rostlina nezakrývá celý blok —
                // stéblo je výřez, přes který je pořád vidět podklad.
                float density = 0.18f + (_generator.ForestDensity(wx, wz) * 0.30f);

                shades[px + (pz * Padded)] = shadeTop * (1f - (density * 0.34f));
            }
        }

        for (int cz = 0; cz < CellsPerTile; cz++)
        {
            for (int cx = 0; cx < CellsPerTile; cx++)
            {
                int cell = cx + (cz * CellsPerTile);
                if (merged[cell])
                {
                    continue;
                }

                int height = heights[(cx + 1) + ((cz + 1) * Padded)];
                ushort block = blocks[cell];

                // Nejdřív doprava, dokud sedí výška i materiál.
                int width = 1;
                while (cx + width < CellsPerTile
                       && !merged[cell + width]
                       && heights[(cx + width + 1) + ((cz + 1) * Padded)] == height
                       && blocks[cell + width] == block)
                {
                    width++;
                }

                // Pak dolů, ale jen celými pruhy dané šířky.
                int depth = 1;
                while (cz + depth < CellsPerTile && RowMatches(cx, cz + depth, width, height, block))
                {
                    depth++;
                }

                for (int dz = 0; dz < depth; dz++)
                {
                    for (int dx = 0; dx < width; dx++)
                    {
                        merged[cx + dx + ((cz + dz) * CellsPerTile)] = true;
                    }
                }

                float x0 = originX + (cx * step);
                float z0 = originZ + (cz * step);
                float x1 = x0 + (width * step);
                float z1 = z0 + (depth * step);

                // Vršek leží níž, než kam sahá plná geometrie — viz SurfaceDrop.
                float top = height + 1f - SurfaceDrop;
                float uWidth = width * step;
                float vDepth = depth * step;

                // Podrost ztmavuje jen porostlé plochy. Na písku, skále a sněhu nic neroste,
                // takže tam zůstává plný jas — a rozhodnout to jde jednou na celý obdélník,
                // protože slučování běží i podle materiálu.
                bool grassy = shadeUndergrowth && (block == grassBlock || block == dryGrassBlock);

                float s00 = shadeTop;
                float s01 = shadeTop;
                float s11 = shadeTop;
                float s10 = shadeTop;

                if (grassy)
                {
                    int edgeX = cx + width;
                    int edgeZ = cz + depth;

                    s00 = shades[(cx + 1) + ((cz + 1) * Padded)];
                    s01 = shades[(cx + 1) + ((edgeZ + 1) * Padded)];
                    s11 = shades[(edgeX + 1) + ((edgeZ + 1) * Padded)];
                    s10 = shades[(edgeX + 1) + ((cz + 1) * Padded)];
                }

                // Do terénní meshe jde všechno včetně mořského dna. Hladina se pokládá
                // až za touhle smyčkou jako samostatná vrstva — leží totiž v jiné výšce
                // než buňka pod ní, takže do téhož slučování nepatří.
                mesh.AddQuad(
                    new Vector3(x0, top, z0), new Vector3(x0, top, z1),
                    new Vector3(x1, top, z1), new Vector3(x1, top, z0),
                    new Vector2(0f, 0f), new Vector2(0f, vDepth),
                    new Vector2(uWidth, vDepth), new Vector2(uWidth, 0f),
                    _registry.FaceLayer(block, BlockFace.PosY),
                    s00, s01, s11, s10,
                    flipDiagonal: false);
            }
        }

        // Boky zvlášť: sousedství se liší buňku od buňky, takže sloučení vršků se na ně
        // nedá použít.
        for (int cz = 0; cz < CellsPerTile; cz++)
        {
            for (int cx = 0; cx < CellsPerTile; cx++)
            {
                int index = (cx + 1) + ((cz + 1) * Padded);
                int height = heights[index];
                float top = height + 1f - SurfaceDrop;
                float sideLayer = _registry.FaceLayer(blocks[cx + (cz * CellsPerTile)], BlockFace.PosX);

                float x0 = originX + (cx * step);
                float z0 = originZ + (cz * step);
                float x1 = x0 + step;
                float z1 = z0 + step;

                AddSide(mesh, heights, index - 1, height, top, sideLayer, x0, z0, x0, z1, axis: 0, positive: false);
                AddSide(mesh, heights, index + 1, height, top, sideLayer, x1, z1, x1, z0, axis: 0, positive: true);
                AddSide(mesh, heights, index - Padded, height, top, sideLayer, x1, z0, x0, z0, axis: 2, positive: false);
                AddSide(mesh, heights, index + Padded, height, top, sideLayer, x0, z1, x1, z1, axis: 2, positive: true);
            }
        }

        BuildWaterSurface(workspace, step, originX, originZ);

        bool RowMatches(int startX, int rowZ, int width, int height, ushort block)
        {
            for (int dx = 0; dx < width; dx++)
            {
                int cell = startX + dx + (rowZ * CellsPerTile);
                if (merged[cell]
                    || heights[(startX + dx + 1) + ((rowZ + 1) * Padded)] != height
                    || blocks[cell] != block)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Položí hladinu vzdáleného moře jako samostatnou vrstvu nad dno.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč vlastní vrstva.</b> Dřív vzdálené moře žádnou neměla: dno se zvedlo
    /// na úroveň hladiny a takový sloupec dostal vodní blok. Pod vodou tedy nebyla žádná
    /// geometrie — a to znamenalo, že se přes hladinu nedalo vidět dno a barvu vody musel
    /// shader uhodnout. Každý pokus to uhodnout skončil švem na hranici LOD: konstanta dala
    /// jeden tmavý odstín pro mělčinu i pro hlubinu, a hloubka interpolovaná přes vrcholy
    /// obřích sloučených obdélníků nadělala čtvercové a trojúhelníkové artefakty.</para>
    ///
    /// <para>S opravdovým dnem pod hladinou odpadá hádání úplně. <c>far.frag</c> na dno
    /// pustí tentýž <c>ApplyWater</c>, jakým se počítá dno u chunků, takže mělčina vyjde
    /// světlá a hloubka tmavá — spojitě a ze stejného vzorce na obou stranách hranice.</para>
    ///
    /// <para><b>Slučování je tu skoro zadarmo.</b> Všechny buňky mají tutéž výšku, takže
    /// z celého moře v dlaždici vyjde pár velkých obdélníků, ne tisíce malých.</para>
    /// </remarks>
    private void BuildWaterSurface(TileWorkspace workspace, int step, int originX, int originZ)
    {
        const int Padded = CellsPerTile + 2;

        int[] heights = workspace.Heights;
        bool[] merged = workspace.Merged;
        MeshBuffer water = workspace.Water;

        Array.Clear(merged);

        ushort waterBlock = _registry.IndexOf("tesseris:water");
        float layer = _registry.FaceLayer(waterBlock, BlockFace.PosY);

        // HLADINA SE NEODSAZUJE, na rozdíl od terénu.
        //
        // <b>Tady byl ten světlý pás na moři.</b> Vršek vodního sloupce ležel o SurfaceDrop
        // níž, tedy o čtvrt bloku pod hladinou z chunků. Terén se odsazuje schválně — má se
        // schovat pod plnou geometrii a nevykukovat z ní — jenže hladina se schovávat nemá,
        // ta má navazovat.
        //
        // Čtvrt bloku stačilo: barvu vody počítá týž vzorec na obou stranách hranice, ale
        // dostal jinou hloubku, takže se na rozhraní chunků a LOD zlomila.
        //
        // Z-fighting nehrozí, protože se hladiny nepřekrývají — LOD voda se řeže až tam,
        // kam sahá plná geometrie (viz ChunkRenderer.FarWaterCutoff).
        float top = TerrainGenerator.SeaLevel + 1f;

        bool Submerged(int cx, int cz) => heights[(cx + 1) + ((cz + 1) * Padded)] < TerrainGenerator.SeaLevel;

        for (int cz = 0; cz < CellsPerTile; cz++)
        {
            for (int cx = 0; cx < CellsPerTile; cx++)
            {
                int cell = cx + (cz * CellsPerTile);

                if (merged[cell] || !Submerged(cx, cz))
                {
                    continue;
                }

                // Nejdřív doprava, pak dolů celými pruhy — stejný postup jako u terénu.
                int width = 1;
                while (cx + width < CellsPerTile && !merged[cell + width] && Submerged(cx + width, cz))
                {
                    width++;
                }

                int depth = 1;
                while (cz + depth < CellsPerTile && RowSubmerged(cx, cz + depth, width))
                {
                    depth++;
                }

                for (int dz = 0; dz < depth; dz++)
                {
                    for (int dx = 0; dx < width; dx++)
                    {
                        merged[cx + dx + ((cz + dz) * CellsPerTile)] = true;
                    }
                }

                float x0 = originX + (cx * step);
                float z0 = originZ + (cz * step);
                float x1 = x0 + (width * step);
                float z1 = z0 + (depth * step);

                float uWidth = width * step;
                float vDepth = depth * step;

                water.AddQuad(
                    new Vector3(x0, top, z0), new Vector3(x0, top, z1),
                    new Vector3(x1, top, z1), new Vector3(x1, top, z0),
                    new Vector2(0f, 0f), new Vector2(0f, vDepth),
                    new Vector2(uWidth, vDepth), new Vector2(uWidth, 0f),
                    layer,
                    1f, 1f, 1f, 1f,
                    flipDiagonal: false);
            }
        }

        bool RowSubmerged(int cx, int cz, int width)
        {
            for (int dx = 0; dx < width; dx++)
            {
                if (merged[cx + dx + (cz * CellsPerTile)] || !Submerged(cx + dx, cz))
                {
                    return false;
                }
            }

            return true;
        }

    }

    private static void AddSide(
        MeshBuffer mesh, int[] heights, int neighbourIndex, int height, float top,
        float layer, float ax, float az, float bx, float bz, int axis, bool positive)
    {
        int neighbour = heights[neighbourIndex];
        if (neighbour >= height)
        {
            return;
        }

        float bottom = neighbour + 1f - SurfaceDrop;
        float wallHeight = top - bottom;

        // ŠÍŘKA STĚNY V BLOCÍCH, ne jedna dlaždice na celou stěnu.
        //
        // <b>Tady byla příčina vlnitého vzoru na vzdáleném terénu.</b> Vodorovné UV šlo
        // 0 až 1 přes celou šířku buňky, tedy přes 2 až 16 bloků, zatímco svislé jde po
        // blocích a vršky i chunky mají taky blok na blok. Stěna tak měla v texturovém
        // prostoru poměr stran až 16:1.
        //
        // Důsledek: mipmapa se pro ni volila podle té roztažené vodorovné osy, takže svislé
        // opakování po blocích zůstalo ostré i na kilometr. A protože stěny vznikají přesně
        // tam, kde se mění výška — tedy na vrstevnicích — skládal se z nich zelený „čárový
        // kód" kopírující vrstevnice, který se při chůzi vlnil.
        //
        // Anizotropní filtrace to zhoršila místo aby pomohla: vzorkovač podle menší osy
        // sníží úroveň a proužky vrátí do ostra, a potřebný poměr by tu byl kolem 176,
        // zatímco strop je 16.
        float wallWidth = MathF.Abs(bx - ax) + MathF.Abs(bz - az);

        // JAS STĚNY SE PŘITAHUJE K JASU VRŠKU.
        //
        // Druhá polovina téže vady. Tabulka stínění dává vršku 1,00, ale stěně na −X jen
        // 0,48 — a terasy leží na obraze pár pixelů od sebe, takže se pravidelný sled
        // světlá/tmavá sráží s pixelovou mřížkou do moiré. Zblízka je ten kontrast správný
        // a dělá terén plastickým; u LOD, kde je buňka celý dům, jen šumí.
        float shade = MathHelper.Lerp(
            FaceShading.ForAxis(axis, positive), FaceShading.ForAxis(1, positive: true), 0.5f);

        // Vodorovně první složka, svisle druhá, a nula nahoře — stejně jako u stěn chunků.
        // Původně to bylo obráceně, takže se textura na stěnách otočila o devadesát stupňů
        // a pruh trávy stál nastojato.
        mesh.AddQuad(
            new Vector3(ax, bottom, az), new Vector3(bx, bottom, bz),
            new Vector3(bx, top, bz), new Vector3(ax, top, az),
            new Vector2(0f, wallHeight), new Vector2(wallWidth, wallHeight),
            new Vector2(wallWidth, 0f), new Vector2(0f, 0f),
            layer, shade, shade, shade, shade,
            flipDiagonal: false);
    }

    /// <summary>Klíč dlaždice: úroveň podrobnosti a její souřadnice v mřížce té úrovně.</summary>
    public readonly record struct TileKey(int Level, int TileX, int TileZ);

    /// <summary>Hotová dlaždice čekající na nahrání.</summary>
    /// <param name="Water">
    /// Hladina dlaždice zvlášť. Kreslí se vodní pipeline, ne pipeline terénu — viz
    /// komentář u rozdělení v <c>BuildMesh</c>.
    /// </param>
    public readonly record struct FinishedTile(
        TileKey Key, MeshBuffer Mesh, MeshBuffer Water, MeshBuffer Plants, Vector3 Min, Vector3 Max)
    {
        /// <summary>
        /// Pracovní paměť, ze které dlaždice vznikla. Vrací se přes <see cref="Recycle"/>;
        /// mimo <see cref="FarTerrain"/> se s ní nic nedělá.
        /// </summary>
        internal TileWorkspace? Workspace { get; init; }
    }

    /// <summary>
    /// Pracovní paměť jedné stavěné dlaždice. Recykluje se, aby se pro každou dlaždici
    /// nealokovalo znovu — u velkých dlaždic jsou to megabajty.
    ///
    /// <para>Pole mají velikost pro <b>nejjemnější</b> úroveň, která vzorkuje nejhustěji.
    /// Hrubší úrovně z nich využijí jen začátek; pár desítek kilobajtů nevyužité paměti je
    /// levnější než mít pro každou úroveň vlastní zásobu.</para>
    /// </summary>
    internal sealed class TileWorkspace
    {
        private const int Padded = CellsPerTile + 2;

        /// <summary>Nejjemnější úroveň vzorkuje po blocích, tedy <c>step</c> vzorků na buňku.</summary>
        private const int MaxSub = 2;

        private const int MaxCorners = (Padded * MaxSub) + 1;

        public int[] Samples { get; } = new int[MaxCorners * MaxCorners];

        public int[] Heights { get; } = new int[Padded * Padded];

        public Biome[] Biomes { get; } = new Biome[Padded * Padded];

        /// <summary>
        /// Jas povrchu se zohledněním podrostu, na roh buňky.
        /// </summary>
        /// <remarks>
        /// <para>Stébla, kapradí a kytky se do LOD nakreslit nedají — na kilometr jsou
        /// hluboko pod pixelem. Jejich <b>hromadný</b> účinek ale vidět je: podle
        /// <c>TreePlanter.PickPlant</c> je porostlých 18 % sloupců na mýtině až 48 %
        /// v hustém lese, a taková louka je jako celek znatelně tmavší než holý trávník.
        /// Bez toho končila vegetace ostře na hranici chunků.</para>
        /// </remarks>
        public float[] Shades { get; } = new float[Padded * Padded];


        public ushort[] Blocks { get; } = new ushort[CellsPerTile * CellsPerTile];

        public bool[] Merged { get; } = new bool[CellsPerTile * CellsPerTile];

        public MeshBuffer Mesh { get; } = new();

        /// <summary>Hladina. Vlastní buffer, protože ji kreslí vodní pipeline.</summary>
        public MeshBuffer Water { get; } = new();

        /// <summary>
        /// Tráva, kapradí a kytky. Vlastní buffer, protože je kreslí výřezová pipeline.
        /// </summary>
        /// <remarks>
        /// Rostlina je zkřížená dvojice ploch s dírami v textuře, ne krychle — nedá se
        /// slučovat greedy meshingem a musí se kreslit z obou stran. To jsou dva důvody,
        /// proč nemůže do terénní meshe.
        /// </remarks>
        public MeshBuffer Plants { get; } = new();
    }

    private enum TileState
    {
        Queued,
        Ready,
    }
}
