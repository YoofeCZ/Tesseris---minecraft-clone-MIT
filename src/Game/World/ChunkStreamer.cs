using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;

using Tesseris.Engine.Rendering;

namespace Tesseris.Game.World;

/// <summary>
/// Načítání a uvolňování chunků podle vzdálenosti od kamery.
///
/// <para><b>Rozdělení práce.</b> Generování a meshování běží na worker vláknech, nahrávání
/// na GPU výhradně na hlavním vlákně. Mezi nimi jsou dvě fronty bez zámků; hlavní vlákno
/// je jen vybírá a nikdy na worker nečeká.</para>
///
/// <para><b>Tři poloměry.</b> Generuje se o jeden chunk dál, než se meshuje — mesh potřebuje
/// znát všech 26 sousedů, takže chunk na samém okraji by se jinak nikdy nezameshoval.
/// Uvolňuje se až o další dva dál, aby rozpracovaná úloha nesahala na chunk, který mezitím
/// zmizel.</para>
///
/// <para><b>Proč se neskenuje každý frame.</b> Sada potřebných chunků se změní jedině tehdy,
/// když kamera přejde hranici chunku. Původní verze procházela celý dohled pokaždé a stálo
/// to v nejhorším framu 8,4 ms plánování plus 6,8 ms uvolňování — tedy skoro celý rozpočet
/// na frame, aniž by z toho cokoli vzešlo. Teď se úplný přepočet dělá jen při změně
/// středového chunku a mezi tím se pracuje z front a z událostí.</para>
///
/// <para><b>Vlastnictví stavu.</b> Množiny a fronty rozpracovaných úloh patří výhradně
/// hlavnímu vláknu. Workery do nich nesahají — jen posílají hotové výsledky.</para>
/// </summary>
public sealed class ChunkStreamer
{
    /// <summary>Výchozí poloměr plné chunkové geometrie kolem hráče.</summary>
    public const int DefaultViewDistanceChunks = 10;

    /// <summary>O kolik dál než dohled se generuje, aby šly okrajové chunky zameshovat.</summary>
    private const int GenerationMargin = 1;


    /// <summary>O kolik dál než generování se teprve uvolňuje.</summary>
    private const int UnloadMargin = 2;

    private readonly VoxelWorld _world;
    private readonly TerrainGenerator _generator;
    private readonly JobSystem _jobs;
    private readonly ChunkRenderer _renderer;
    private readonly BlockRegistry _registry;

    private readonly HashSet<Vector3i> _generating = [];
    private readonly HashSet<Vector3i> _meshing = [];
    private readonly HashSet<Vector3i> _uploaded = [];
    private readonly HashSet<Vector3i> _resident = [];

    private readonly Queue<Vector3i> _generationQueue = new();
    private readonly Queue<Vector3i> _interactiveMeshQueue = new();
    private readonly Queue<Vector3i> _meshQueue = new();
    private readonly HashSet<Vector3i> _queuedForGeneration = [];
    private readonly HashSet<Vector3i> _queuedForMesh = [];
    private readonly HashSet<Vector3i> _queuedInteractiveMesh = [];
    private readonly HashSet<Vector3i> _interactiveMeshing = [];
    private readonly HashSet<Vector3i> _interactiveRequests = [];

    private readonly ConcurrentQueue<Vector3i> _generated = new();
    private readonly ConcurrentQueue<MeshJobResult> _interactiveMeshed = new();
    private readonly ConcurrentQueue<MeshJobResult> _meshed = new();
    private readonly ConcurrentQueue<WorkFailure> _failed = new();
    private readonly ConcurrentBag<MeshWorkspace> _workspaces = [];
    private readonly ConcurrentDictionary<Vector3i, CachedLightSources> _lightSources = new();

    private readonly List<Vector3i> _finishedThisFrame = [];
    private readonly List<Vector3i> _unloadScratch = [];
    private readonly List<Vector3i> _affectedScratch = [];
    private readonly Stopwatch _clock = new();

    // Číslo verze obsahu chunku. Každá úprava bloku ho zvýší; meshovací úloha si ho vezme
    // s sebou a při návratu se porovná. Kdyby hráč rozbil blok, zatímco úloha z předchozí
    // verze ještě běží, vrátila by se geometrie bez té změny a zůstala by na obrazovce,
    // dokud se chunk z jiného důvodu nezameshuje znovu.
    private readonly Dictionary<Vector3i, int> _revisions = [];
    private readonly Dictionary<(Vector3i Position, WorkKind Kind), int> _retryCounts = [];

    private const int MaxWorkRetries = 2;

    private Vector2i[] _columnOffsets = [];
    private int _viewDistanceChunks = -1;
    private Vector2i _lastCenter = new(int.MinValue, int.MinValue);
    private int _centerY;
    private int _lastCenterY = int.MinValue;
    private readonly HashSet<Vector2i> _reportedLoadedColumns = [];

    public ChunkStreamer(VoxelWorld world, TerrainGenerator generator, JobSystem jobs, ChunkRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(renderer);

        _world = world;
        _generator = generator;
        _jobs = jobs;
        _renderer = renderer;
        _registry = world.Registry;

        ViewDistanceChunks = DefaultViewDistanceChunks;
    }

    /// <summary>Dohled v chuncích. Změna přepočítá pořadí, ve kterém se chunky nabírají.</summary>
    public int ViewDistanceChunks
    {
        get => _viewDistanceChunks;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            if (_viewDistanceChunks == value)
            {
                return;
            }

            _viewDistanceChunks = value;
            _columnOffsets = BuildColumnOffsets(value + GenerationMargin);
            _lastCenter = new Vector2i(int.MinValue, int.MinValue);
        }
    }

    /// <summary>Kolik milisekund smí hlavní vlákno za frame strávit nahráváním na GPU.</summary>
    public double UploadBudgetMs { get; set; } = 0.45;

    /// <summary>
    /// Nejvíc chunků nahraných za frame. Doplňuje časový rozpočet, protože ten se kontroluje
    /// až po nahrání a jediný velký chunk ho umí výrazně přetáhnout.
    /// </summary>
    public int UploadsPerFrame { get; set; } = 2;

    /// <summary>Kolik úloh generování se smí odeslat za jeden frame.</summary>
    public int GenerationsPerFrame { get; set; } = 160;

    /// <summary>Kolik úloh meshování se smí odeslat za jeden frame.</summary>
    public int MeshesPerFrame { get; set; } = 28;

    /// <summary>
    /// Rezervovaná kapacita pro chunky změněné přímo hráčem. Není součástí běžného stropu,
    /// takže kliknutí nemusí čekat, až doběhne některá z úloh růstu nebo streamingu.
    /// </summary>
    public int MaxInteractiveMeshingInFlight { get; set; } = 8;

    /// <summary>
    /// Kolik generování smí být rozpracovaných najednou.
    ///
    /// Bez tohohle stropu se fronta úloh zaplní generováním a meshing se k řadě nedostane —
    /// fronta je společná a obsluhuje se v pořadí příchodu. Naměřeno: bez stropu se nahromadilo
    /// 1767 rozpracovaných generování a 938 meshingů, přičemž hotových chunků s geometrií
    /// bylo 25. Se stropem se obojí střídá.
    /// </summary>
    public int MaxGenerationInFlight { get; set; } = 384;

    /// <summary>Kolik meshování smí být rozpracovaných najednou.</summary>
    public int MaxMeshingInFlight { get; set; } = 48;

    /// <summary>
    /// Kolik chunků se smí uvolnit za jeden frame. Uvolnění znamená několik volání do GL
    /// na zrušení bufferů; při rychlém letu jich najednou vypadnou stovky a bez stropu
    /// to udělá díru v jednom framu (naměřeno 23 ms).
    /// </summary>
    public int UnloadsPerFrame { get; set; } = 24;

    /// <summary>Kolik chunků nad a pod hráčem se meshuje i mimo povrchovou slupku.</summary>
    public int VerticalMeshRadius { get; set; } = 4;

    /// <summary>
    /// Trvalé úložiště světa, nebo <c>null</c> pro svět, který se neukládá.
    ///
    /// <para>Volitelné schválně: selftest i testy tak běží bez sahání na disk a nemusí
    /// po sobě uklízet adresáře.</para>
    /// </summary>
    public WorldStorage? Storage { get; set; }

    public int GeneratingCount => _generating.Count;

    public int MeshingCount => _meshing.Count;

    public int UploadedCount => _uploaded.Count;

    public int PendingGeneration => _generationQueue.Count;

    public int PendingMeshing => _interactiveMeshQueue.Count + _meshQueue.Count;

    public int PendingInteractiveMeshing => _interactiveMeshQueue.Count + _interactiveMeshing.Count;

    /// <summary>
    /// Trvalá chyba generování nebo meshingu po několika pokusech. Načítací obrazovka ji
    /// ukáže místo toho, aby navždy čekala na pozici ponechanou v množině rozpracovaných.
    /// </summary>
    public string? Failure { get; private set; }

    /// <summary>Kolik chunků se nahrálo na GPU v posledním volání <see cref="Update"/>.</summary>
    public int UploadsLastFrame { get; private set; }

    public double WorstUploadMs { get; private set; }

    public double WorstScheduleMs { get; private set; }

    public double WorstUnloadMs { get; private set; }

    /// <summary>Doba nahrávání na GPU v posledním framu. Krmí to rozpad času po fázích.</summary>
    public double LastUploadMs { get; private set; }

    /// <summary>Doba plánování v posledním framu.</summary>
    public double LastScheduleMs { get; private set; }

    /// <summary>Doba uvolňování v posledním framu.</summary>
    public double LastUnloadMs { get; private set; }

    /// <summary>
    /// Chunky, které mají obsah, jsou v dohledu, mají hotové sousedy, vyplatí se
    /// meshovat — a přesto nemají geometrii ani nejsou ve frontě.
    ///
    /// <para><b>Tohle je jediné číslo, které znamená chybu.</b> Takový chunk už nikdo
    /// nezařadí a na jeho místě zůstane trvalá díra. Ostatní tři počítadla popisují
    /// stavy, které se samy vyřeší, a slouží jen k rozlišení — dřív se totiž počítaly
    /// všechny dohromady a číslo tím ztratilo výpovědní hodnotu.</para>
    ///
    /// <para>Naplní ho <see cref="RunDiagnostics"/>, které se schválně <b>nevolá každý
    /// frame</b>: prochází celý dohled a u kandidátů i všech 26 sousedů.</para>
    /// </summary>
    public int LostChunks { get; private set; }

    /// <summary>Chunky bez geometrie, které čekají na dogenerování sousedů. Vyřeší se samo.</summary>
    public int WaitingChunks { get; private set; }

    /// <summary>Chunky s obsahem, které <see cref="IsWorthMeshing"/> vyřadil mimo povrchovou slupku.</summary>
    public int SkippedChunks { get; private set; }

    /// <summary>Chunky, které jsou právě ve frontě nebo se meshují. Vyřeší se samo.</summary>
    public int PendingChunks { get; private set; }

    /// <summary>
    /// Do jaké vzdálenosti v blocích je svět <b>hotový</b> — tedy kde leží nejbližší
    /// sloupec, kterému ještě chybí geometrie.
    ///
    /// <para><b>K čemu to je.</b> Vzdálený terén se nekreslí tam, kam sahají chunky. Jenže
    /// „kam sahají chunky" není totéž co dohled: při rychlém pohybu streaming nestíhá.
    /// Na jeden přechod hranice chunku připadá při dohledu 12 kolem <b>2600 nových
    /// chunků</b> k vygenerování, a při rychlosti letu (22 bloků/s, se sprintem 37) se
    /// hranice překročí každou vteřinu. Svět je pak hotový mnohem blíž, než kam sahá
    /// dohled — a v tom rozdílu nekreslil nikdo nic. <b>Tudy bylo vidět skrz, a čím
    /// rychleji hráč letěl, tím víc.</b></para>
    ///
    /// <para>Podle tohohle čísla vzdálený terén zaskočí dovnitř a mezera zmizí. Až se
    /// streaming dotáhne, číslo vyroste zpátky na dohled a LOD zase ustoupí.</para>
    ///
    /// <para>Počítá se v <see cref="RebuildQueues"/>, který sloupce prochází <b>seřazené
    /// podle vzdálenosti</b>, takže to nestojí nic navíc.</para>
    /// </summary>
    public float CompleteRadiusBlocks { get; private set; }

    /// <summary>
    /// Projde dohled a roztřídí chunky bez geometrie podle toho, <b>proč</b> ji nemají.
    ///
    /// <para>Volá se jen tehdy, když se výsledek někde zobrazuje (otevřené dev menu),
    /// a i tak se rozumně řídne. Cena je při dohledu 12 kolem 17 tisíc vyhledání v mapě
    /// chunků, u kandidátů k tomu 27 dalších — na každý frame je to moc, jednou za půl
    /// vteřiny šum.</para>
    /// </summary>
    public void RunDiagnostics()
    {
        int lost = 0;
        int waiting = 0;
        int skipped = 0;
        int pending = 0;

        int viewSquared = _viewDistanceChunks * _viewDistanceChunks;
        int top = TerrainGenerator.WorldHeightChunks - 1;

        foreach (Vector2i offset in _columnOffsets)
        {
            if ((offset.X * offset.X) + (offset.Y * offset.Y) > viewSquared)
            {
                continue;
            }

            int columnX = _lastCenter.X + offset.X;
            int columnZ = _lastCenter.Y + offset.Y;

            for (int y = 0; y <= top; y++)
            {
                var position = new Vector3i(columnX, y, columnZ);

                if (_uploaded.Contains(position))
                {
                    continue;
                }

                // Zajímají jen chunky, které mají co kreslit. Homogenní vzduch ani
                // neexistující chunk díru neudělá.
                if (_world.GetChunk(position) is not { IsHomogeneous: false })
                {
                    continue;
                }

                if (_meshing.Contains(position) || _queuedForMesh.Contains(position))
                {
                    pending++;
                }
                else if (!IsWorthMeshing(position))
                {
                    skipped++;
                }
                else if (!AreNeighboursReady(position))
                {
                    waiting++;
                }
                else
                {
                    lost++;
                }
            }
        }

        LostChunks = lost;
        WaitingChunks = waiting;
        SkippedChunks = skipped;
        PendingChunks = pending;
    }

    /// <summary>
    /// Ohlásí změnu bloku na světové souřadnici.
    ///
    /// Zneplatní chunk, ve kterém blok leží, a při zásahu do okraje i sousedy — jejich
    /// odsazený objem totiž ten blok obsahuje a bez přemeshování by na hranici zůstala
    /// stěna, která už tam nepatří. U bloku v rohu chunku jde o osm chunků najednou.
    /// </summary>
    public void InvalidateBlock(int worldX, int worldY, int worldZ)
    {
        _affectedScratch.Clear();
        if (EmissionChanged(worldX, worldY, worldZ))
        {
            CollectChunksLitBy(worldX, worldY, worldZ, _affectedScratch);
        }
        else
        {
            CollectChunksContaining(worldX, worldY, worldZ, _affectedScratch);
        }

        foreach (Vector3i position in _affectedScratch)
        {
            Invalidate(position);
        }
    }

    /// <summary>
    /// Zneplatní blok změněný přímo hráčem. Jeho meshing, worker úloha i hotový GPU upload
    /// předběhnou růst vegetace, tekutiny, generování a ostatní neinteraktivní práci.
    /// </summary>
    public void InvalidateInteractiveBlock(int worldX, int worldY, int worldZ)
    {
        _affectedScratch.Clear();
        // Ruční změna nemusí být sama zdrojem: odstranění kamene může otevřít
        // světlu cestu a položení bloku ji zase zavřít. Proto se obnoví celý objem,
        // do kterého může nejsilnější blokové světlo dosáhnout.
        CollectChunksLitBy(worldX, worldY, worldZ, _affectedScratch);

        foreach (Vector3i position in _affectedScratch)
        {
            InvalidateInteractive(position);
        }
    }

    /// <summary>
    /// Vypíše chunky, jejichž odsazený objem obsahuje blok na zadané světové souřadnici.
    ///
    /// Vždycky je to chunk, ve kterém blok leží, a při zásahu do krajní vrstvy i sousedé —
    /// jejich lem ten blok obsahuje a bez přemeshování by na hranici zůstala stěna, která
    /// už tam nepatří. U bloku v rohu jde o osm chunků najednou.
    ///
    /// <para>Podmínka se odvozuje takhle: soused na posunu <c>d</c> pokrývá světové souřadnice
    /// od <c>(chunk+d)*32 - 1</c> do <c>(chunk+d)*32 + 32</c>. Dosazením bloku vyjde
    /// <c>d*32 - 1 &lt;= local &lt;= d*32 + 32</c>, tedy pro <c>d = +1</c> vychází
    /// <c>local == 31</c> a pro <c>d = -1</c> naopak <c>local == 0</c>.</para>
    ///
    /// <para>Metoda je veřejná a statická schválně: je to čistá aritmetika, kterou jde
    /// otestovat bez GL kontextu. První verze měla oba směry prohozené a přemešovávala
    /// vždycky souseda na opačné straně, takže se po vytěžení bloku na hranici chunku
    /// neobjevila stěna, která se odkryla.</para>
    /// </summary>
    public static void CollectChunksContaining(int worldX, int worldY, int worldZ, List<Vector3i> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        Vector3i center = VoxelWorld.ToChunkPosition(worldX, worldY, worldZ);
        int localX = worldX & Chunk.SizeMask;
        int localY = worldY & Chunk.SizeMask;
        int localZ = worldZ & Chunk.SizeMask;

        // Vlastní chunk první: při interaktivní změně se tak jeho nová geometrie nahraje
        // dřív než případných sedm sousedních lemů na rohu chunku.
        destination.Add(center);

        for (int dy = -1; dy <= 1; dy++)
        {
            if (!ReachesNeighbour(localY, dy))
            {
                continue;
            }

            for (int dz = -1; dz <= 1; dz++)
            {
                if (!ReachesNeighbour(localZ, dz))
                {
                    continue;
                }

                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0 || dy != 0 || dz != 0) && ReachesNeighbour(localX, dx))
                    {
                        destination.Add(new Vector3i(center.X + dx, center.Y + dy, center.Z + dz));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Zasahuje blok s danou místní souřadnicí do lemu souseda v daném směru?
    /// Lem je široký jeden voxel, takže jde jen o krajní vrstvu — a o tu na správné straně.
    /// </summary>
    private static bool ReachesNeighbour(int local, int direction) => direction switch
    {
        0 => true,
        1 => local == Chunk.Size - 1,
        _ => local == 0,
    };

    private bool EmissionChanged(int worldX, int worldY, int worldZ)
    {
        Vector3i chunkPosition = VoxelWorld.ToChunkPosition(worldX, worldY, worldZ);
        bool emittedBefore = _lightSources.TryGetValue(chunkPosition, out CachedLightSources? cached)
            && cached.Sources.Any(source =>
                source.X == worldX && source.Y == worldY && source.Z == worldZ);
        ushort current = _world.GetBlock(worldX, worldY, worldZ);
        bool emitsNow = current < _registry.EmissionTable.Length
            && _registry.EmissionTable[current] > 0;

        // Vynutit nové proskenování i při odstranění poslední pochodně v chunku.
        _lightSources.TryRemove(chunkPosition, out _);
        return emittedBefore || emitsNow;
    }

    private static void CollectChunksLitBy(int worldX, int worldY, int worldZ, List<Vector3i> destination)
    {
        const int radius = 14;
        Vector3i min = VoxelWorld.ToChunkPosition(worldX - radius, worldY - radius, worldZ - radius);
        Vector3i max = VoxelWorld.ToChunkPosition(worldX + radius, worldY + radius, worldZ + radius);

        Vector3i center = VoxelWorld.ToChunkPosition(worldX, worldY, worldZ);
        destination.Add(center);
        for (int y = min.Y; y <= max.Y; y++)
        {
            for (int z = min.Z; z <= max.Z; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var position = new Vector3i(x, y, z);
                    if (position != center)
                    {
                        destination.Add(position);
                    }
                }
            }
        }
    }

    /// <summary>Označí chunk za zastaralý a zařadí ho k novému zameshování.</summary>
    public void Invalidate(Vector3i position)
        => Invalidate(position, interactive: false);

    public void InvalidateInteractive(Vector3i position)
        => Invalidate(position, interactive: true);

    private void Invalidate(Vector3i position, bool interactive)
    {
        if (interactive)
        {
            _interactiveRequests.Add(position);
        }

        _revisions[position] = _revisions.GetValueOrDefault(position) + 1;
        _uploaded.Remove(position);
        TryQueueMeshing(position, interactive);
    }

    /// <summary>Volá se jednou za frame z hlavního vlákna.</summary>
    public void Update(Vector3 cameraPosition)
    {
        var center = new Vector2i(
            (int)MathF.Floor(cameraPosition.X) >> Chunk.SizeShift,
            (int)MathF.Floor(cameraPosition.Z) >> Chunk.SizeShift);

        // Svislý střed dohledu. Ve světě vysokém 1024 bloků se meshuje koule kolem hráče,
        // ne celý svislý sloup: chunky 300 bloků pod nohama nikdo neuvidí, ale meshing
        // by je stejně počítal a fronta by se ucpala.
        _centerY = (int)MathF.Floor(cameraPosition.Y) >> Chunk.SizeShift;

        _clock.Restart();

        CollectFailedWork();
        CollectFinishedGeneration();
        UploadFinishedMeshes();
        double afterUpload = _clock.Elapsed.TotalMilliseconds;

        // Přepočet spouští i svislý posun, ale až po dvou patrech. Terén má přes 500 bloků
        // převýšení, takže při letu nad horami se svislý střed mění skoro pořád — a plný
        // přepočet na každou změnu stál v nejhorším framu 24 ms. Povrchová slupka na výšce
        // hráče nezávisí; svislý střed rozhoduje jen o pásmu pod nohama.
        bool horizontalMoved = center != _lastCenter;
        bool verticalMoved = Math.Abs(_centerY - _lastCenterY) >= 2;

        if (horizontalMoved)
        {
            _lastCenterY = _centerY;
            CollectDistantChunks(center);
            _generator.ForgetColumnsOutside(
                center, _viewDistanceChunks + GenerationMargin + UnloadMargin + 1);

            // Střed se musí zapsat dřív, než se začne plánovat: plánování z něj počítá
            // vzdálenost a při počáteční hodnotě int.MinValue by výpočet přetekl a zahodil
            // úplně všechno.
            _lastCenter = center;
            RebuildQueues(center);
        }
        else if (verticalMoved)
        {
            // Svislý posun NEDĚLÁ plný přepočet.
            //
            // Povrchová slupka na výšce hráče nezávisí a generuje se stejně celá výška —
            // svislý střed rozhoduje jen o pásmu pod nohama. Plný přepočet by přitom
            // procházel 530 sloupců po 32 patrech a k tomu počítal rozsahy okolí, což
            // naměřeno stálo až 31,8 ms v jednom framu. Při letu nad kopci se svislý střed
            // mění skoro pořád, takže se to spouštělo znovu a znovu.
            //
            // Tady se proto zařadí jen to, co se opravdu změnilo: pásmo kolem hráče.
            _lastCenterY = _centerY;
            RequeueVerticalBand(center);
        }

        // Chunk, který právě dogeneroval, mohl odemknout meshing sobě i svým sousedům.
        // Tohle je jediná cesta, jak se do fronty dostane práce mimo úplný přepočet.
        EnqueueColumnsOf(_finishedThisFrame);
        QueueMeshingAround(_finishedThisFrame);
        _finishedThisFrame.Clear();

        DispatchQueuedWork();
        double afterSchedule = _clock.Elapsed.TotalMilliseconds;

        // Uvolňuje se každý frame, ale po dávkách. Kdyby se čekalo na změnu středu, sešlo by
        // se toho příliš najednou.
        UnloadDistantChunks();

        double afterUnload = _clock.Elapsed.TotalMilliseconds;
        _clock.Stop();

        LastUploadMs = afterUpload;
        LastScheduleMs = afterSchedule - afterUpload;
        LastUnloadMs = afterUnload - afterSchedule;

        WorstUploadMs = Math.Max(WorstUploadMs, LastUploadMs);
        WorstScheduleMs = Math.Max(WorstScheduleMs, LastScheduleMs);
        WorstUnloadMs = Math.Max(WorstUnloadMs, LastUnloadMs);
    }

    /// <summary>
    /// Vrátí selhanou pozici z „rozpracováno“ do fronty, nejvýš dvakrát. Bez toho by
    /// jediná výjimka na workeru nechala loading čekat na nenulový počet navždy.
    /// </summary>
    private void CollectFailedWork()
    {
        while (_failed.TryDequeue(out WorkFailure failure))
        {
            if (failure.Kind == WorkKind.Generation)
            {
                _generating.Remove(failure.Position);
            }
            else
            {
                _meshing.Remove(failure.Position);
                _interactiveMeshing.Remove(failure.Position);
                _queuedForMesh.Remove(failure.Position);
                _queuedInteractiveMesh.Remove(failure.Position);
            }

            var key = (failure.Position, failure.Kind);
            int retries = _retryCounts.GetValueOrDefault(key);

            if (retries < MaxWorkRetries)
            {
                _retryCounts[key] = retries + 1;
                Log.Warn(
                    $"{failure.Kind} chunku {failure.Position} selhalo, "
                    + $"zkouším znovu ({retries + 1}/{MaxWorkRetries}): {failure.Error.Message}");

                if (failure.Kind == WorkKind.Generation)
                {
                    if (_queuedForGeneration.Add(failure.Position))
                    {
                        _generationQueue.Enqueue(failure.Position);
                    }
                }
                else
                {
                    TryQueueMeshing(
                        failure.Position,
                        failure.Interactive || _interactiveRequests.Contains(failure.Position));
                }

                continue;
            }

            Failure ??= $"{failure.Kind} chunku {failure.Position}: {failure.Error.Message}";
            _interactiveRequests.Remove(failure.Position);
            Log.Error($"Příprava světa nemůže pokračovat: {Failure}");
        }
    }

    /// <summary>
    /// Povrchové patro sloupce je načtené a systémy nad světem ho mohou jednou projít.
    /// Nečeká se na podzemí ani na prázdnou oblohu, takže událost nezávisí na výšce hráče.
    /// Událost se vyvolává na hlavním vlákně, ne z generačního workeru.
    /// </summary>
    public event Action<Vector2i>? ColumnLoaded;

    /// <summary>A complete chunk became visible to the world; raised on the main thread.</summary>
    public event Action<Vector3i, Chunk>? ChunkLoaded;

    /// <summary>A resident chunk is about to be saved and removed; raised on the main thread.</summary>
    public event Action<Vector3i, Chunk>? ChunkUnloading;

    private void CollectFinishedGeneration()
    {
        while (_generated.TryDequeue(out Vector3i position))
        {
            _generating.Remove(position);
            _retryCounts.Remove((position, WorkKind.Generation));
            _finishedThisFrame.Add(position);

            if (_world.GetChunk(position) is { } loaded)
            {
                ChunkLoaded?.Invoke(position, loaded);
            }

            var column = new Vector2i(position.X, position.Z);
            if (!_reportedLoadedColumns.Contains(column)
                && IsEcologySurfaceLoaded(_world, _generator, column))
            {
                _reportedLoadedColumns.Add(column);
                ColumnLoaded?.Invoke(column);
            }
        }
    }

    /// <summary>
    /// Jsou načtená všechna patra, ve kterých mohou ležet kořeny stromů daného sloupce?
    /// </summary>
    /// <remarks>
    /// Povrch v jednom chunkovém sloupci není nutně v jediném patře: na strmém svahu
    /// může přejít přes jeho hranici. Kořen stojí jeden blok nad povrchem, proto se
    /// rozsah počítá z <c>lowest + 1</c> a <c>highest + 1</c>. Celých 32 pater světa
    /// sem záměrně nepatří: když hráč sleduje les z výšky, podzemní fronta nesmí
    /// zastavit život stromů, které už vidí.
    /// </remarks>
    internal static bool IsEcologySurfaceLoaded(
        VoxelWorld world, TerrainGenerator generator, Vector2i column)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(generator);

        if (!generator.TryGetSurfaceRange(column.X, column.Y, out int lowest, out int highest))
        {
            return false;
        }

        int bottom = Math.Clamp((lowest + 1) >> Chunk.SizeShift, 0, TerrainGenerator.WorldHeightChunks - 1);
        int top = Math.Clamp((highest + 1) >> Chunk.SizeShift, bottom, TerrainGenerator.WorldHeightChunks - 1);

        for (int y = bottom; y <= top; y++)
        {
            if (!world.HasChunk(new Vector3i(column.X, y, column.Y)))
            {
                return false;
            }
        }

        return true;
    }

    private void UploadFinishedMeshes()
    {
        UploadsLastFrame = 0;
        double start = _clock.Elapsed.TotalMilliseconds;

        while (TryDequeueFinishedMesh(out MeshJobResult result))
        {
            _meshing.Remove(result.Position);
            _interactiveMeshing.Remove(result.Position);
            _queuedForMesh.Remove(result.Position);
            _queuedInteractiveMesh.Remove(result.Position);
            _retryCounts.Remove((result.Position, WorkKind.Meshing));

            // Výsledek z jiné verze obsahu se zahodí — mezitím se blok upravil a je
            // zařazené nové zameshování, které dá platnou geometrii.
            bool stale = _revisions.GetValueOrDefault(result.Position) != result.Revision;

            if (stale)
            {
                _workspaces.Add(result.Workspace);
                TryQueueMeshing(result.Position, _interactiveRequests.Contains(result.Position));
                continue;
            }

            // Chunk mohl mezitím vypadnout z dohledu; pak se výsledek jen zahodí.
            if (_world.HasChunk(result.Position))
            {
                _renderer.Upload(
                    result.Position,
                    result.Workspace.Opaque,
                    result.Workspace.Transparent,
                    result.Workspace.Cutout,
                    result.Workspace.Micro,
                    result.Workspace.Water);
                _uploaded.Add(result.Position);

                bool hasGeometry = !result.Workspace.Opaque.IsEmpty
                    || !result.Workspace.Transparent.IsEmpty
                    || !result.Workspace.Cutout.IsEmpty
                    || !result.Workspace.Micro.IsEmpty
                    || !result.Workspace.Water.IsEmpty;

                if (hasGeometry)
                {
                    _resident.Add(result.Position);
                }
                else
                {
                    _resident.Remove(result.Position);
                }

                UploadsLastFrame++;
            }

            _interactiveRequests.Remove(result.Position);

            _workspaces.Add(result.Workspace);

            // Rozpočet se kontroluje až po prvním nahrání, aby se každý frame stihl aspoň
            // jeden chunk a fronta se za letu nezasekla. Přetečení je tak nejvýš o jeden chunk.
            if (UploadsLastFrame >= UploadsPerFrame
                || _clock.Elapsed.TotalMilliseconds - start >= UploadBudgetMs)
            {
                break;
            }
        }
    }

    private bool TryDequeueFinishedMesh(out MeshJobResult result)
    {
        if (_interactiveMeshed.TryDequeue(out result))
        {
            return true;
        }

        return _meshed.TryDequeue(out result);
    }

    /// <summary>Úplný přepočet toho, co je potřeba. Běží jen při přechodu do jiného chunku.</summary>
    private void RebuildQueues(Vector2i center)
    {
        _generationQueue.Clear();
        _queuedForGeneration.Clear();
        _interactiveMeshQueue.Clear();
        _meshQueue.Clear();
        _queuedForMesh.Clear();
        _queuedInteractiveMesh.Clear();

        int viewSquared = _viewDistanceChunks * _viewDistanceChunks;
        int nearestIncomplete = int.MaxValue;

        foreach (Vector2i offset in _columnOffsets)
        {
            int columnX = center.X + offset.X;
            int columnZ = center.Y + offset.Y;
            int distanceSquared = (offset.X * offset.X) + (offset.Y * offset.Y);
            bool withinViewDistance = distanceSquared <= viewSquared;

            if (!EnqueueColumn(columnX, columnZ, withinViewDistance)
                && withinViewDistance
                && distanceSquared < nearestIncomplete)
            {
                nearestIncomplete = distanceSquared;
            }
        }

        CompleteRadiusBlocks = nearestIncomplete == int.MaxValue
            ? _viewDistanceChunks * Chunk.Size
            : MathF.Sqrt(nearestIncomplete) * Chunk.Size;
    }

    /// <summary>
    /// Zařadí k meshování jen svislé pásmo kolem hráče. Levná náhrada plného přepočtu
    /// pro případ, kdy se změnila jen výška.
    /// </summary>
    private void RequeueVerticalBand(Vector2i center)
    {
        int viewSquared = _viewDistanceChunks * _viewDistanceChunks;
        int bottom = Math.Max(0, _centerY - VerticalMeshRadius);
        int top = Math.Min(TerrainGenerator.WorldHeightChunks - 1, _centerY + VerticalMeshRadius);

        foreach (Vector2i offset in _columnOffsets)
        {
            if ((offset.X * offset.X) + (offset.Y * offset.Y) > viewSquared)
            {
                continue;
            }

            int columnX = center.X + offset.X;
            int columnZ = center.Y + offset.Y;

            for (int y = bottom; y <= top; y++)
            {
                TryQueueMeshing(new Vector3i(columnX, y, columnZ));
            }
        }
    }

    /// <summary>
    /// Zařadí svislý sloupec chunků ke zpracování.
    ///
    /// <para><b>Nesahá na celou výšku světa.</b> Při 1024 blocích má sloupec 32 pater, ale
    /// terén z nich zabírá jen spodní část a nad ním je prázdná obloha. Generovat i tu
    /// znamenalo 19 424 chunků ve světě a plánování za 24 ms v jednom framu.</para>
    ///
    /// <para>Kde terén končí, ví generátor — ale až <b>potom</b>, co se sloupec spočítal.
    /// Dokud to není známé, zařadí se jen nejspodnější patro; jeho vygenerování rozsah
    /// zjistí a zbytek sloupce doplní <see cref="EnqueueColumnsOf"/>. Odhadovat rozsah
    /// dopředu by šlo, ale podstřelený odhad by udělal díru v krajině.</para>
    /// </summary>
    /// <returns>
    /// <c>true</c>, když je sloupec <b>hotový</b> — všechno, co v něm má mít geometrii,
    /// ji má. Podle toho se pozná, kam až sahá dokončený svět, a od té hranice může
    /// zaskočit vzdálený terén.
    /// </returns>
    private bool EnqueueColumn(int columnX, int columnZ, bool withinViewDistance)
    {
        bool complete = true;

        // Rozsah se počítá JEDNOU na sloupec, ne pro každé z 32 pater. Předtím se pro každý
        // chunk sahalo pětkrát do cache sloupců, tedy 160× na sloupec a přes 80 tisíc
        // vyhledání na jeden přepočet — plánování stálo 35,5 ms v jednom framu.
        // Generuje se VŽDY celá výška. Zkoušel jsem to zkrátit podle toho, kam sahá terén,
        // a dvakrát z toho byly díry v krajině: meshing vyžaduje všech 26 sousedů včetně
        // diagonálních, takže zkrácený sloupec umí zablokovat i chunk o dva sloupce dál.
        // Rozdíl je 19 400 proti 9 300 chunkům, ale skoro všechny navíc jsou homogenní
        // vzduch, který stojí jedno vyhledání v cache a jedno vyplnění.
        int generateTop = TerrainGenerator.WorldHeightChunks - 1;
        int meshBottom = 0;
        int meshTop = generateTop;

        // Rozsah se spočítá JEDNOU a pak se veze do TryQueueMeshing. Bez toho ho
        // IsWorthMeshing počítalo znovu pro každé patro sloupce.
        SurfaceRange range = SurfaceRange.Unknown;

        if (TryNeighbourhoodRange(columnX, columnZ, out int lowChunk, out int highChunk))
        {
            meshBottom = lowChunk - 1;
            meshTop = highChunk;
            range = new SurfaceRange(true, lowChunk, highChunk);
        }

        int nearBottom = _centerY - VerticalMeshRadius;
        int nearTop = _centerY + VerticalMeshRadius;

        for (int y = 0; y <= generateTop; y++)
        {
            var position = new Vector3i(columnX, y, columnZ);

            // Hotovost se počítá JEN z povrchové slupky. Chunky ve svislém pásmu kolem
            // hráče jsou skoro všechny pod zemí: na hotovost obrazu nemají vliv, ale
            // pořád se něco z nich meshuje, takže by hotový poloměr srazily k nule pořád.
            bool inShell = y >= meshBottom && y <= meshTop;

            if (!_world.HasChunk(position))
            {
                if (inShell)
                {
                    complete = false;
                }

                if (!_generating.Contains(position) && _queuedForGeneration.Add(position))
                {
                    _generationQueue.Enqueue(position);
                }

                continue;
            }

            // Meshuje se jen uvnitř vlastního dohledu — okrajový pás slouží jen jako
            // podklad pro stěny na hranici.
            if (!withinViewDistance)
            {
                continue;
            }

            // Diagnostika děr se odtud schválně vyhodila. Počítala se tady zadarmo, ale
            // bez kontroly sousedů — a tím započítala i chunky, které jen čekají, až
            // soused dogeneruje. Číslo pak ukazovalo stovky i ve chvíli, kdy bylo všechno
            // v pořádku, a nešlo podle něj nic poznat. Teď je v RunDiagnostics, kde má
            // rozpočet na to udělat se pořádně.
            if (inShell || (y >= nearBottom && y <= nearTop))
            {
                bool pending = TryQueueMeshing(position, range);

                // Zastaralý mesh se během přepočtu záměrně nechává v rendereru, aby chunk
                // neblikl pryč. Pro pokrytí LOD je tedy stále hotový: jinak by jediná změna
                // vegetace stáhla globální řez dovnitř a křížené LOD koruny přeskočily
                // přes celý dosud viditelný les.
                if (inShell && pending && !_resident.Contains(position))
                {
                    complete = false;
                }
            }
        }

        return complete;
    }

    /// <summary>
    /// Doplní sloupce, jejichž rozsah je nově známý. Volá se na chunky, které právě
    /// dogenerovaly — teprve tím se totiž zjistí, kam až terén sahá.
    /// </summary>
    private void EnqueueColumnsOf(List<Vector3i> positions)
    {
        int viewSquared = _viewDistanceChunks * _viewDistanceChunks;

        foreach (Vector3i position in positions)
        {
            int dx = position.X - _lastCenter.X;
            int dz = position.Z - _lastCenter.Y;

            EnqueueColumn(position.X, position.Z, (dx * dx) + (dz * dz) <= viewSquared);
        }
    }

    private void QueueMeshingAround(List<Vector3i> positions)
    {
        foreach (Vector3i position in positions)
        {
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int oz = -1; oz <= 1; oz++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        TryQueueMeshing(new Vector3i(position.X + ox, position.Y + oy, position.Z + oz));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Vyplatí se ten chunk vůbec meshovat?
    ///
    /// <para>Ve světě vysokém 1024 bloků je svislý sloupec 32 chunků, ale vidět je z něj jen
    /// pár. Meshovat všechny znamená počítat kilometry jeskynních stěn, na které se nikdy
    /// nikdo nepodívá. Naměřeno při pokusu meshovat kouli o poloměru dohledu:
    /// <b>12,8 milionu trojúhelníků</b> a p99 na 18,96 ms, tedy nad rozpočtem.</para>
    ///
    /// <para>Meshuje se proto <b>povrchová slupka</b> — chunky, kterými prochází terén, ty
    /// jsou vidět z libovolné dálky i výšky — a k tomu svislé okolí hráče, aby fungovalo
    /// kopání a jeskyně.</para>
    /// </summary>
    private bool IsWorthMeshing(Vector3i position) => IsWorthMeshing(position, SurfaceRange.Unknown);

    /// <param name="known">
    /// Rozsah okolí, pokud ho volající už spočítal.
    ///
    /// <para><b>Proč se předává.</b> Zjištění rozsahu stojí devět vyhledání v cache sloupců
    /// a bez tohohle se dělalo znovu pro <b>každé patro</b> sloupce. Při 530 sloupcích
    /// a šesti patrech ve slupce to je kolem 29 tisíc vyhledání navíc na jeden přepočet —
    /// a přepočet je největší položka v nejhorším snímku.</para>
    /// </param>
    private bool IsWorthMeshing(Vector3i position, SurfaceRange known)
    {
        if (Math.Abs(position.Y - _centerY) <= VerticalMeshRadius)
        {
            return true;
        }

        SurfaceRange range = known;

        if (!range.Known)
        {
            // Neznámé okolí: radši zameshovat. Vynechaný chunk udělá díru v krajině,
            // zbytečný jen kus práce navíc.
            if (!TryNeighbourhoodRange(position.X, position.Z, out int lowChunk, out int highChunk))
            {
                return true;
            }

            range = new SurfaceRange(true, lowChunk, highChunk);
        }

        // O jeden chunk níž kvůli jeskyním, které se otevírají těsně pod povrchem.
        return position.Y >= range.LowChunk - 1 && position.Y <= range.HighChunk;
    }

    /// <summary>Rozsah pater, ve kterých v okolí sloupce leží terén, nebo „nevím".</summary>
    private readonly record struct SurfaceRange(bool Known, int LowChunk, int HighChunk)
    {
        public static SurfaceRange Unknown => new(false, 0, 0);
    }

    /// <summary>
    /// Rozsah pater, ve kterých v okolí sloupce leží terén.
    ///
    /// <para><b>Bere se okolí, ne jen vlastní sloupec.</b> Stěna útesu patří tomu vyššímu
    /// sloupci, ale vidět je jen proto, že vedle je níž. Ze slupky vlastního sloupce
    /// vypadne, protože ta sahá jen k jeho povrchu — a celý bok hory se nezameshuje.
    /// Zadavatel to viděl ve hře jako díry v krajině a plovoucí kusy terénu.</para>
    /// </summary>
    private bool TryNeighbourhoodRange(int columnX, int columnZ, out int lowChunk, out int highChunk)
    {
        lowChunk = int.MaxValue;
        highChunk = int.MinValue;

        foreach ((int OffsetX, int OffsetZ) offset in NeighbourOffsets)
        {
            if (!_generator.TryGetSurfaceRange(
                    columnX + offset.OffsetX, columnZ + offset.OffsetZ,
                    out int lowest, out int highest))
            {
                return false;
            }

            lowChunk = Math.Min(lowChunk, lowest >> Chunk.SizeShift);
            highChunk = Math.Max(highChunk, highest >> Chunk.SizeShift);
        }

        return true;
    }

    /// <summary>
    /// Vlastní sloupec a všech osm okolo, tedy celé okolí 3×3.
    ///
    /// <para>Čtyři ortogonální nestačí. Meshing vyžaduje všech 26 sousedů chunku, mezi nimi
    /// i ty <b>diagonální</b> — a diagonální sloupec u hory má povrch mnohem níž. Když se
    /// rozsah počítal jen ze čtyř, bok hory se neodemkl a v krajině zůstala díra.</para>
    /// </summary>
    private static readonly (int OffsetX, int OffsetZ)[] NeighbourOffsets =
    [
        (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    ];

    /// <returns>
    /// <c>true</c>, když chunk <b>pořád dluží geometrii</b> — ať už se právě meshuje, čeká
    /// ve frontě, čeká na sousedy, nebo se tímhle voláním zařadil. <c>false</c> znamená,
    /// že je hotový nebo že tam žádná geometrie nepatří.
    /// </returns>
    private bool TryQueueMeshing(Vector3i position) =>
        TryQueueMeshing(position, SurfaceRange.Unknown, interactive: false);

    private bool TryQueueMeshing(Vector3i position, bool interactive) =>
        TryQueueMeshing(position, SurfaceRange.Unknown, interactive);

    private bool TryQueueMeshing(Vector3i position, SurfaceRange known) =>
        TryQueueMeshing(position, known, interactive: false);

    private bool TryQueueMeshing(Vector3i position, SurfaceRange known, bool interactive)
    {
        if (interactive)
        {
            _interactiveRequests.Add(position);
        }

        bool wantsInteractive = _interactiveRequests.Contains(position);

        if (position.Y < 0 || position.Y >= TerrainGenerator.WorldHeightChunks)
        {
            _interactiveRequests.Remove(position);
            return false;
        }

        int dx = position.X - _lastCenter.X;
        int dz = position.Z - _lastCenter.Y;
        if ((dx * dx) + (dz * dz) > _viewDistanceChunks * _viewDistanceChunks)
        {
            _interactiveRequests.Remove(position);
            return false;
        }

        if (!IsWorthMeshing(position, known))
        {
            return false;
        }

        if (_uploaded.Contains(position))
        {
            return false;
        }

        if (_meshing.Contains(position))
        {
            return true;
        }

        if (_queuedForMesh.Contains(position))
        {
            // Pozici, která už čeká v normální FIFO frontě, není nutné z ní draze hledat a
            // mazat. Přidá se i do urgentní; až později dorazí starý normální záznam, pozná
            // podle _queuedForMesh, že už byl spotřebovaný, a přeskočí ho.
            if (wantsInteractive && _queuedInteractiveMesh.Add(position))
            {
                _interactiveMeshQueue.Enqueue(position);
            }

            return true;
        }

        if (!_world.HasChunk(position) || !AreNeighboursReady(position))
        {
            return true;
        }

        // Chunk plný vzduchu nemá co kreslit. Ve světě vysokém 1024 bloků je takových
        // zhruba dvě třetiny — celá obloha nad terénem. Bez téhle podmínky se všechny
        // postaví do fronty na meshing, ta se ucpe a chunky u hráče čekají za oblohou.
        // Naměřeno: fronta 7580 nezpracovaných na konci běhu.
        if (_world.GetChunk(position) is { IsHomogeneous: true, HomogeneousBlock: BlockRegistry.Air })
        {
            // Po odstranění posledního bloku nesmí v rendereru zůstat starý mesh jen proto,
            // že nový prázdný chunk nemá co nahrávat.
            _renderer.Remove(position);
            _uploaded.Add(position);
            _resident.Remove(position);
            _interactiveRequests.Remove(position);
            return false;
        }

        _queuedForMesh.Add(position);
        if (wantsInteractive)
        {
            _queuedInteractiveMesh.Add(position);
            _interactiveMeshQueue.Enqueue(position);
        }
        else
        {
            _meshQueue.Enqueue(position);
        }

        return true;
    }

    private void DispatchQueuedWork()
    {
        // Odesílá se jen do stropu rozpracovaných. Přeplnit společnou frontu generováním
        // znamená vyhladovět meshing a nemít co kreslit.
        int interactiveRoom = Math.Max(0, MaxInteractiveMeshingInFlight - _interactiveMeshing.Count);

        int interactiveMeshes = 0;
        while (interactiveMeshes < interactiveRoom
            && _interactiveMeshQueue.TryDequeue(out Vector3i interactivePosition))
        {
            _queuedInteractiveMesh.Remove(interactivePosition);

            if (!_queuedForMesh.Remove(interactivePosition))
            {
                continue;
            }

            if (_uploaded.Contains(interactivePosition)
                || !_world.HasChunk(interactivePosition)
                || !AreNeighboursReady(interactivePosition)
                || !_meshing.Add(interactivePosition))
            {
                continue;
            }

            _interactiveMeshing.Add(interactivePosition);
            SubmitMeshing(interactivePosition, interactive: true);
            interactiveMeshes++;
        }

        int generationRoom = Math.Min(GenerationsPerFrame, MaxGenerationInFlight - _generating.Count);

        int generations = 0;
        while (generations < generationRoom && _generationQueue.TryDequeue(out Vector3i position))
        {
            _queuedForGeneration.Remove(position);

            if (_world.HasChunk(position) || !_generating.Add(position))
            {
                continue;
            }

            SubmitGeneration(position);
            generations++;
        }

        int meshRoom = Math.Min(MeshesPerFrame, Math.Max(0, MaxMeshingInFlight - _meshing.Count));
        int meshes = 0;
        while (meshes < meshRoom && _meshQueue.TryDequeue(out Vector3i position))
        {
            if (_queuedInteractiveMesh.Contains(position) || !_queuedForMesh.Remove(position))
            {
                continue;
            }

            if (_uploaded.Contains(position) || !_world.HasChunk(position) || !AreNeighboursReady(position))
            {
                continue;
            }

            if (!_meshing.Add(position))
            {
                continue;
            }

            SubmitMeshing(position, interactive: false);
            meshes++;
        }
    }

    private void SubmitGeneration(Vector3i position)
    {
        _jobs.Submit(() =>
        {
            try
            {
                // Nejdřív se hledá na disku. Uložený je jen chunk, do kterého hráč sáhl —
                // ostatní se generují, protože spočítat je ze seedu je levnější než číst.
                Chunk chunk = Storage?.TryLoad(position) ?? Generate(position);

                // Chunk se zveřejní až hotový; do té chvíle na něj nikdo jiný nevidí.
                _world.TryAddChunk(position, chunk);
                _generated.Enqueue(position);
            }
            catch (Exception ex)
            {
                _failed.Enqueue(new WorkFailure(position, WorkKind.Generation, ex, Interactive: false));
            }
        });
    }

    private Chunk Generate(Vector3i position)
    {
        var chunk = new Chunk();
        _generator.Generate(chunk, position);
        return chunk;
    }

    private void SubmitMeshing(Vector3i position, bool interactive)
    {
        // Verze se čte teď, na hlavním vlákně, a putuje s úlohou. Přečíst ji až na workeru
        // by nic neřešilo — mezitím by se mohla změnit.
        int revision = _revisions.GetValueOrDefault(position);

        _jobs.Submit(() =>
        {
            MeshWorkspace workspace = RentWorkspace();

            try
            {
                _world.CopyPadded(
                    position, workspace.Padded, workspace.Pieces, workspace.ExtraBlocks, workspace.ExtraMasks,
                    workspace.Fluid);
                FillSkyEntry(position, workspace.SkyEntry);


                FillBlockLight(position, workspace.BlockLight, workspace);
                ChunkMesher.Build(
                    workspace.Padded, _registry, workspace.Opaque, workspace.Transparent, position,
                    workspace.Cutout, workspace.Water, workspace.Pieces,
                    workspace.ExtraBlocks, workspace.ExtraMasks, workspace.Fluid,
                    workspace.BlockLight, workspace.SkyEntry, workspace.SkyLight,
                    workspace.BlockLight);

                // Mikrogeometrie jde vlastní cestou ze sdílených tvarů. Odkaz na chunk se bere
                // jednou; obsah se po zveřejnění nemění, takže je snímek souvislý.
                Chunk? chunk = _world.GetChunk(position);
                if (chunk is null)
                {
                    workspace.Micro.Clear();
                }
                else
                {
                    MicroMesher.BuildChunkMicro(chunk, _world.MicroShapes, workspace.Micro);
                }

                var result = new MeshJobResult(position, workspace, revision, interactive);
                if (interactive)
                {
                    _interactiveMeshed.Enqueue(result);
                }
                else
                {
                    _meshed.Enqueue(result);
                }
            }
            catch (Exception ex)
            {
                _workspaces.Add(workspace);
                _failed.Enqueue(new WorkFailure(position, WorkKind.Meshing, ex, interactive));
            }
        }, interactive ? JobPriority.Interactive : JobPriority.Normal);
    }

    /// <summary>
    /// Jaká úroveň přímého slunce vstupuje do každého sloupce nahoře na odsazeném meshi.
    /// </summary>
    /// <remarks>
    /// Dřív se tu počítala VÝŠKA stropu a mesher pak rozhodoval podle <c>y &gt; strop</c>.
    /// To umí jen dvě odpovědi, plné slunce nebo tma, a jakmile začalo listí slunce
    /// zastavovat, byly z lesa černé díry s ostrými hranami. Úroveň 0–15 umí odstíny.
    /// </remarks>
    private void FillSkyEntry(Vector3i chunkPosition, Span<int> destination)
    {
        int baseX = chunkPosition.X * Chunk.Size;
        int baseY = chunkPosition.Y * Chunk.Size;
        int baseZ = chunkPosition.Z * Chunk.Size;

        for (int z = -ChunkMesher.Pad; z < Chunk.Size + ChunkMesher.Pad; z++)
        {
            int row = (z + ChunkMesher.Pad) * ChunkMesher.PaddedSize;
            for (int x = -ChunkMesher.Pad; x < Chunk.Size + ChunkMesher.Pad; x++)
            {
                int worldX = baseX + x;
                int worldZ = baseZ + z;
                destination[row + x + ChunkMesher.Pad] = SkyLevelEntering(
                    worldX, worldZ,
                    _generator.SurfaceHeight(worldX, worldZ),
                    baseY + Chunk.Size + ChunkMesher.Pad - 1);
            }
        }
    }

    /// <summary>
    /// Projde sloupec shora dolů až k hornímu okraji meshovaného chunku a odečte, co
    /// každý blok cestou slunci ubral.
    /// </summary>
    /// <remarks>
    /// Generátor je jen bezpečná záloha pro dosud nenačtené sloupce; jakmile je chunk
    /// přítomný, rozhodují skutečné bloky. Vykopaná díra proto oblohu otevře a postavená
    /// střecha ji zavře.
    /// </remarks>
    private int SkyLevelEntering(int worldX, int worldZ, int generatedSurface, int paddedTop)
    {
        // Nad generovaným povrchem nemůže stát nic vyššího než pár chunků — vyšší stavby
        // řeší až vlastní chunk, ve kterém stojí.
        int scanTop = Math.Min(TerrainGenerator.WorldHeight - 1, generatedSurface + (4 * Chunk.Size));
        if (paddedTop >= scanTop)
        {
            return LuantiLight.LightSun;
        }

        int level = LuantiLight.LightSun;
        int localX = worldX & Chunk.SizeMask;
        int localZ = worldZ & Chunk.SizeMask;

        for (int y = scanTop; y > paddedTop; y--)
        {
            var chunkPosition = new Vector3i(worldX >> Chunk.SizeShift, y >> Chunk.SizeShift, worldZ >> Chunk.SizeShift);
            Chunk? chunk = _world.GetChunk(chunkPosition);
            if (chunk is null)
            {
                // Nad původním povrchem je chybějící chunk bezpečně vzduch. Pod ním by
                // chybějící data udělala falešnou šachtu až do hluboké jeskyně.
                if (y <= generatedSurface)
                {
                    return 0;
                }

                continue;
            }

            level -= _registry.SunlightCost(chunk.GetBlock(localX, y & Chunk.SizeMask, localZ));
            if (level <= 0)
            {
                return 0;
            }
        }

        return level;
    }

    /// <summary>
    /// Spočítá blokové světlo ve světových souřadnicích. Zdroje se berou i ze
    /// sousedních chunků, takže na hranici nevzniká černý řez. Průchod je veden
    /// šesti sousedy kvůli stěnám, ale jas se odvozuje z eukleidovské vzdálenosti;
    /// volný prostor je proto koule, ne taxicabový diamant.
    /// </summary>
    private void FillBlockLight(Vector3i chunkPosition, Span<byte> destination, MeshWorkspace workspace)
    {
        destination.Clear();

        int baseX = chunkPosition.X * Chunk.Size;
        int baseY = chunkPosition.Y * Chunk.Size;
        int baseZ = chunkPosition.Z * Chunk.Size;
        int minX = baseX - ChunkMesher.Pad;
        int minY = baseY - ChunkMesher.Pad;
        int minZ = baseZ - ChunkMesher.Pad;
        int maxX = baseX + Chunk.Size + ChunkMesher.Pad - 1;
        int maxY = baseY + Chunk.Size + ChunkMesher.Pad - 1;
        int maxZ = baseZ + Chunk.Size + ChunkMesher.Pad - 1;

        for (int cy = chunkPosition.Y - 1; cy <= chunkPosition.Y + 1; cy++)
        {
            for (int cz = chunkPosition.Z - 1; cz <= chunkPosition.Z + 1; cz++)
            {
                for (int cx = chunkPosition.X - 1; cx <= chunkPosition.X + 1; cx++)
                {
                    foreach (BlockLightSource source in LightSources(new Vector3i(cx, cy, cz)))
                    {
                        int radius = Math.Min(14, source.Level - 1);
                        if (source.X + radius < minX || source.X - radius > maxX
                            || source.Y + radius < minY || source.Y - radius > maxY
                            || source.Z + radius < minZ || source.Z - radius > maxZ)
                        {
                            continue;
                        }

                        SpreadWorldLight(source, radius, minX, minY, minZ, destination, workspace);
                    }
                }
            }
        }
    }

    private void SpreadWorldLight(
        BlockLightSource source, int radius,
        int targetMinX, int targetMinY, int targetMinZ,
        Span<byte> destination, MeshWorkspace workspace)
    {
        const int diameter = (14 * 2) + 1;
        const int layer = diameter * diameter;
        Span<byte> pathDistance = workspace.LightSearchDistance;
        Span<int> queue = workspace.LightSearchQueue;
        pathDistance.Fill(byte.MaxValue);

        int center = 14 + (14 * diameter) + (14 * layer);
        pathDistance[center] = 0;
        queue[0] = center;
        int head = 0;
        int tail = 1;
        ReadOnlySpan<bool> opacity = _registry.OpacityTable;

        while (head < tail)
        {
            int packed = queue[head++];
            int ry = packed / layer;
            int inLayer = packed - (ry * layer);
            int rz = inLayer / diameter;
            int rx = inLayer - (rz * diameter);
            int dx = rx - 14;
            int dy = ry - 14;
            int dz = rz - 14;
            byte walked = pathDistance[packed];

            int squared = (dx * dx) + (dy * dy) + (dz * dz);
            if (squared <= radius * radius)
            {
                int level = source.Level - (int)MathF.Ceiling(MathF.Sqrt(squared));
                int worldX = source.X + dx;
                int worldY = source.Y + dy;
                int worldZ = source.Z + dz;
                int localX = worldX - targetMinX;
                int localY = worldY - targetMinY;
                int localZ = worldZ - targetMinZ;
                if (level > 0
                    && (uint)localX < ChunkMesher.PaddedSize
                    && (uint)localY < ChunkMesher.PaddedSize
                    && (uint)localZ < ChunkMesher.PaddedSize)
                {
                    int target = localX
                        + (localZ * ChunkMesher.PaddedSize)
                        + (localY * ChunkMesher.PaddedSize * ChunkMesher.PaddedSize);
                    destination[target] = Math.Max(destination[target], (byte)level);
                }
            }

            if (walked >= radius)
            {
                continue;
            }

            TryVisitLight(rx - 1, ry, rz, walked, source, radius, pathDistance, queue, ref tail, opacity);
            TryVisitLight(rx + 1, ry, rz, walked, source, radius, pathDistance, queue, ref tail, opacity);
            TryVisitLight(rx, ry - 1, rz, walked, source, radius, pathDistance, queue, ref tail, opacity);
            TryVisitLight(rx, ry + 1, rz, walked, source, radius, pathDistance, queue, ref tail, opacity);
            TryVisitLight(rx, ry, rz - 1, walked, source, radius, pathDistance, queue, ref tail, opacity);
            TryVisitLight(rx, ry, rz + 1, walked, source, radius, pathDistance, queue, ref tail, opacity);
        }
    }

    private void TryVisitLight(
        int rx, int ry, int rz, byte walked, BlockLightSource source, int radius,
        Span<byte> pathDistance, Span<int> queue, ref int tail, ReadOnlySpan<bool> opacity)
    {
        const int diameter = 29;
        if ((uint)rx >= diameter || (uint)ry >= diameter || (uint)rz >= diameter)
        {
            return;
        }

        int dx = rx - 14;
        int dy = ry - 14;
        int dz = rz - 14;
        if ((dx * dx) + (dy * dy) + (dz * dz) > radius * radius)
        {
            return;
        }

        int packed = rx + (rz * diameter) + (ry * diameter * diameter);
        byte next = (byte)(walked + 1);
        if (pathDistance[packed] <= next)
        {
            return;
        }

        ushort block = _world.GetBlock(source.X + dx, source.Y + dy, source.Z + dz);
        if (block < opacity.Length && opacity[block])
        {
            return;
        }

        pathDistance[packed] = next;
        queue[tail++] = packed;
    }

    private BlockLightSource[] LightSources(Vector3i position)
    {
        Chunk? chunk = _world.GetChunk(position);
        if (chunk is null)
        {
            _lightSources.TryRemove(position, out _);
            return [];
        }

        if (_lightSources.TryGetValue(position, out CachedLightSources? cached)
            && ReferenceEquals(cached.Chunk, chunk))
        {
            return cached.Sources;
        }

        var sources = new List<BlockLightSource>();
        ReadOnlySpan<byte> emission = _registry.EmissionTable;
        int baseX = position.X * Chunk.Size;
        int baseY = position.Y * Chunk.Size;
        int baseZ = position.Z * Chunk.Size;
        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ushort block = chunk.GetBlock(x, y, z);
                    byte level = block < emission.Length ? emission[block] : (byte)0;
                    if (level > 0)
                    {
                        sources.Add(new BlockLightSource(baseX + x, baseY + y, baseZ + z, level));
                    }
                }
            }
        }

        BlockLightSource[] snapshot = [.. sources];
        _lightSources[position] = new CachedLightSources(chunk, snapshot);
        return snapshot;
    }


    /// <summary>
    /// Jsou k dispozici všichni sousedé potřební pro meshing? Chunky mimo svislý rozsah
    /// světa se počítají jako vzduch a čekat na ně nemá smysl.
    /// </summary>
    private bool AreNeighboursReady(Vector3i position)
    {
        for (int oy = -1; oy <= 1; oy++)
        {
            int y = position.Y + oy;
            if (y < 0 || y >= TerrainGenerator.WorldHeightChunks)
            {
                continue;
            }

            for (int oz = -1; oz <= 1; oz++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (!_world.HasChunk(new Vector3i(position.X + ox, y, position.Z + oz)))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Sestaví seznam chunků k uvolnění. Volá se <b>jen při změně středu</b> — vzdálený
    /// chunk se z blízkého nestane ničím jiným než pohybem hráče.
    ///
    /// <para>Původně se sken dělal každý frame. Při 2428 chuncích to nevadilo, ale ve světě
    /// vysokém 1024 bloků jich je <b>19 424</b> a procházet je pokaždé stálo v nejhorším
    /// framu 27,9 ms — víc než celý rozpočet. Samotné mazání je proti tomu levné.</para>
    /// </summary>
    private void CollectDistantChunks(Vector2i center)
    {
        int limit = _viewDistanceChunks + GenerationMargin + UnloadMargin;
        int limitSquared = limit * limit;

        _unloadScratch.Clear();

        foreach (Vector3i position in _world.ChunkPositions)
        {
            int dx = position.X - center.X;
            int dz = position.Z - center.Y;

            if ((dx * dx) + (dz * dz) > limitSquared)
            {
                _unloadScratch.Add(position);
            }
        }
    }

    /// <summary>Odebere část nasbíraných vzdálených chunků. Běží každý frame, ale s rozpočtem.</summary>
    private void UnloadDistantChunks()
    {
        int removed = 0;
        int consumed = 0;

        foreach (Vector3i position in _unloadScratch)
        {
            if (removed >= UnloadsPerFrame)
            {
                break;
            }

            consumed++;

            // Rozpracovaný chunk se nechá dojet — výsledek se pak zahodí při nahrávání.
            // Ze seznamu ale vypadne; příští změna středu ho nasbírá znovu.
            if (_generating.Contains(position) || _meshing.Contains(position))
            {
                continue;
            }

            // Uložit se musí PŘED odebráním ze světa: potom už by nebylo co uložit.
            // Neupravené chunky si Storage odfiltruje samo.
            if (Storage is not null && _world.GetChunk(position) is { } leaving)
            {
                ChunkUnloading?.Invoke(position, leaving);
                Storage.Save(position, leaving);
            }
            else if (_world.GetChunk(position) is { } transientLeaving)
            {
                ChunkUnloading?.Invoke(position, transientLeaving);
            }

            _world.RemoveChunk(position);
            _lightSources.TryRemove(position, out _);
            _renderer.Remove(position);
            _uploaded.Remove(position);
            _resident.Remove(position);
            _interactiveRequests.Remove(position);
            _reportedLoadedColumns.Remove(new Vector2i(position.X, position.Z));

            // Bez tohohle by evidence verzí rostla donekonečna, jak se svět proletuje.
            _revisions.Remove(position);
            removed++;
        }

        // Zpracovaná část se ze seznamu vyhodí, jinak by se každý frame procházela znovu.
        if (consumed > 0)
        {
            _unloadScratch.RemoveRange(0, consumed);
        }
    }

    private MeshWorkspace RentWorkspace() =>
        _workspaces.TryTake(out MeshWorkspace? workspace) ? workspace : new MeshWorkspace();

    /// <summary>
    /// Posuny sloupců seřazené podle vzdálenosti, aby se svět nabíral od kamery ven.
    /// Počítá se jednou při změně dohledu, ne každý frame.
    /// </summary>
    private static Vector2i[] BuildColumnOffsets(int radius)
    {
        List<Vector2i> offsets = [];

        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if ((x * x) + (z * z) <= radius * radius)
                {
                    offsets.Add(new Vector2i(x, z));
                }
            }
        }

        offsets.Sort((a, b) => ((a.X * a.X) + (a.Y * a.Y)).CompareTo((b.X * b.X) + (b.Y * b.Y)));
        return [.. offsets];
    }

    /// <summary>Pracovní paměť jedné meshovací úlohy. Recykluje se, aby se nealokovalo na chunk.</summary>
    private sealed class MeshWorkspace
    {
        public ushort[] Padded { get; } = new ushort[ChunkMesher.PaddedVolume];

        /// <summary>Masky dílků k odsazenému objemu. Nese je strom sázený na jemné mřížce.</summary>
        public byte[] Pieces { get; } = new byte[ChunkMesher.PaddedVolume];

        /// <summary>Druhý materiál bloku — v koruně listí kolem kmene.</summary>
        public ushort[] ExtraBlocks { get; } = new ushort[ChunkMesher.PaddedVolume];

        /// <summary>Dílky druhého materiálu.</summary>
        public byte[] ExtraMasks { get; } = new byte[ChunkMesher.PaddedVolume];

        /// <summary>Úrovně hladiny. Bez nich by voda byla všude stejně vysoká.</summary>
        public byte[] Fluid { get; } = new byte[ChunkMesher.PaddedVolume];

        /// <summary>Statické blokové světlo a jeho BFS fronta; recyklují se spolu s meshem.</summary>
        public byte[] BlockLight { get; } = new byte[ChunkMesher.PaddedVolume];

        /// <summary>Úrovně světla oblohy 0–15; recyklují se spolu s meshem.</summary>
        public byte[] SkyLight { get; } = new byte[ChunkMesher.PaddedVolume];

        // Jedna znovupoužitelná koule o poloměru 14 pro světové BFS. Je ve workspace,
        // aby každá pochodeň při každém meshi nevyráběla frontu ani hash set.
        public byte[] LightSearchDistance { get; } = new byte[29 * 29 * 29];

        public int[] LightSearchQueue { get; } = new int[29 * 29 * 29];

        /// <summary>Úroveň slunce vstupující do každého sloupce odsazeného meshe, 0–15.</summary>
        public int[] SkyEntry { get; } = new int[ChunkMesher.PaddedSize * ChunkMesher.PaddedSize];

        public MeshBuffer Opaque { get; } = new();

        public MeshBuffer Transparent { get; } = new();

        /// <summary>Rostliny. Vlastní průchod se zápisem do hloubky, viz ChunkRenderer.</summary>
        public MeshBuffer Cutout { get; } = new();

        /// <summary>Kapaliny. Vlastní průchod s vlastním shaderem, viz ChunkRenderer.</summary>
        public MeshBuffer Water { get; } = new();

        public MeshBuffer Micro { get; } = new();
    }

    private readonly record struct BlockLightSource(int X, int Y, int Z, byte Level);

    private sealed record CachedLightSources(Chunk Chunk, BlockLightSource[] Sources);

    private enum WorkKind
    {
        Generation,
        Meshing,
    }

    private readonly record struct WorkFailure(
        Vector3i Position, WorkKind Kind, Exception Error, bool Interactive);

    private readonly record struct MeshJobResult(
        Vector3i Position, MeshWorkspace Workspace, int Revision, bool Interactive);
}
