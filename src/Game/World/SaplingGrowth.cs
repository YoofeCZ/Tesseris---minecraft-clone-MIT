using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>
/// Ze zasazených sazenic dělá stromy.
/// </summary>
/// <remarks>
/// <para><b>Sazenice se nikam neukládá.</b> Odpočet by se musel serializovat spolu se
/// světem a formát regionu umí jen bloky — po restartu by se sazenice proměnila v ozdobu,
/// která už nikdy nevyroste. Místo toho se okolí hráče <b>prochází</b> a nalezené sazenice
/// se do odpočtu zařadí samy. Sazenice zasazená před restartem tím vyroste stejně jako ta
/// zasazená před chvílí.</para>
///
/// <para><b>Prochází se po plátcích.</b> Každý sloupec se nejdřív podívá na povrch
/// z generátoru, takže sazenice a mladé stromy pod hráčem najde i při pozorování z velké
/// výšky. Jen když je hráč poblí země, doplní se krátký svislý průzkum kvůli sazenicím
/// postaveným na stavbách. Jeden tik projde osminu okolí.</para>
///
/// <para><b>Vlastní zasazení se hlásí rovnou</b> přes <see cref="Notice"/>, aby hráč nečekal
/// na to, až k němu procházení dojde.</para>
/// </remarks>
public sealed class SaplingGrowth
{
    /// <summary>Jak často se prohledá jeden plátek okolí, ve vteřinách.</summary>
    private const float ScanInterval = 1.5f;

    /// <summary>Na kolik plátků je okolí rozdělené.</summary>
    private const int Slices = 8;

    /// <summary>Dosah prohledávání vodorovně, v blocích.</summary>
    private const int Reach = 24;

    /// <summary>
    /// Dosah prohledávání svisle, v blocích.
    /// </summary>
    /// <remarks>
    /// Menší než vodorovný schválně: sazenice stojí na zemi, takže po hráčově výšce nemá
    /// smysl hledat daleko nahoru ani dolů. Osm bloků pokryje i svah pod nohama.
    /// </remarks>
    private const int Rise = 8;

    /// <summary>Po neúspěšném růstu se další pokus provede za tolik biologických vteřin.</summary>
    private const float RetrySeconds = 8f;

    /// <summary>Nejkratší skutečný čas, po který musí být každá viditelná fáze ve světě.</summary>
    /// <remarks>
    /// Při 512× by jinak sazenice, malý i střední strom proběhly po jednom snímku a hráč by
    /// viděl jen hotový strom. Biologický čas růst pořád urychluje, ale žádnou fázi nepřeskočí.
    /// </remarks>
    private const float MinVisibleStageSeconds = 2.5f;

    private readonly TreePlanter _planter;
    private readonly TerrainGenerator? _generator;

    /// <summary>Sazenice a mladé stromy, u kterých běží další fáze růstu.</summary>
    private readonly Dictionary<Vector3i, Growth> _growing = [];

    /// <summary>Sazenice, které v tomhle tiku dorostly. Drží se mimo cyklus kvůli úpravě slovníku.</summary>
    private readonly List<(Vector3i At, int Stage)> _ripe = [];

    private readonly List<Vector3i> _dirty = [];
    private readonly List<Vector3i> _grownThisUpdate = [];

    private float _scanClock;
    private int _slice;

    public SaplingGrowth(TreePlanter planter, TerrainGenerator? generator = null)
    {
        _planter = planter ?? throw new ArgumentNullException(nameof(planter));
        _generator = generator;
    }

    /// <summary>Kolik sazenic právě roste. Pro přehled a pro test.</summary>
    public int Growing => _growing.Count;

    /// <summary>Kolik stromů už vyrostlo za celý běh.</summary>
    public int Grown { get; private set; }

    /// <summary>Kolikrát strom viditelně přešel do další růstové fáze.</summary>
    public int StagesAdvanced { get; private set; }

    /// <summary>Kořeny stromů, které vznikly v posledním volání <see cref="Update"/>.</summary>
    public IReadOnlyList<Vector3i> GrownThisUpdate => _grownThisUpdate;

    /// <summary>
    /// Zařadí sazenici do odpočtu. Opakované ohlášení téže polohy odpočet nerestartuje.
    /// </summary>
    /// <remarks>
    /// Kdyby restartovalo, sazenice v dosahu procházení by se resetovala pokaždé, co k ní
    /// procházení dojde, a nevyrostla by nikdy.
    /// </remarks>
    public void Notice(Vector3i at)
    {
        if (!_growing.ContainsKey(at))
        {
            _growing[at] = new Growth(
                Stage: 0,
                SecondsLeft: StageSeconds(at, stage: 0),
                VisibleSecondsLeft: MinVisibleStageSeconds);
        }
    }

    private void NoticeStage(Vector3i at, int stage)
    {
        if (!_growing.ContainsKey(at))
        {
            _growing[at] = new Growth(
                stage,
                StageSeconds(at, stage),
                MinVisibleStageSeconds);
        }
    }

    /// <summary>
    /// Posune odpočty a vypěstuje, co dozrálo.
    /// </summary>
    /// <returns>
    /// Chunky, které se změnily a je potřeba je přemeshovat. Prázdný seznam znamená,
    /// že se nic nezměnilo.
    /// </returns>
    public IReadOnlyList<Vector3i> Update(
        VoxelWorld world, Vector3 player, float seconds, float realSeconds = float.NaN)
    {
        ArgumentNullException.ThrowIfNull(world);

        _dirty.Clear();
        _grownThisUpdate.Clear();

        Scan(world, player, seconds);
        float realDelta = float.IsFinite(realSeconds) && realSeconds >= 0f
            ? realSeconds
            : seconds;
        Countdown(world, seconds, realDelta);

        foreach ((Vector3i at, int currentStage) in _ripe)
        {
            int nextStage = currentStage + 1;

            if (_planter.GrowStage(world, at, nextStage, out IReadOnlyCollection<Vector3i> touched) == 0)
            {
                _growing[at] = new Growth(currentStage, RetrySeconds, VisibleSecondsLeft: 0f);
                continue;
            }

            StagesAdvanced++;

            if (nextStage >= TreePlanter.MatureGrowthStage)
            {
                _growing.Remove(at);
                Grown++;
                _grownThisUpdate.Add(at);
            }
            else
            {
                _growing[at] = new Growth(
                    nextStage,
                    StageSeconds(at, nextStage),
                    MinVisibleStageSeconds);
            }

            Spread(touched);
        }

        _ripe.Clear();
        return _dirty;
    }

    /// <summary>Ubere všem odpočet a vybere ty, které došly.</summary>
    private void Countdown(VoxelWorld world, float seconds, float realSeconds)
    {
        if (_growing.Count == 0)
        {
            return;
        }

        foreach ((Vector3i at, Growth growth) in _growing.ToList())
        {
            // Záznam může ležet v právě odloženém chunku. Tam se čas odečte, ale změna
            // počká na načtení; vzduch z chybějícího chunku nesmí růst zrušit.
            if (!world.HasChunk(VoxelWorld.ToChunkPosition(at.X, at.Y, at.Z)))
            {
                _growing[at] = growth with
                {
                    SecondsLeft = growth.SecondsLeft - seconds,
                    VisibleSecondsLeft = growth.VisibleSecondsLeft - realSeconds,
                };
                continue;
            }

            ushort root = world.GetBlock(at.X, at.Y, at.Z);
            bool stillExists = growth.Stage == 0
                ? _planter.IsSapling(root)
                : _planter.IsTreeLog(root);

            if (!stillExists)
            {
                _growing.Remove(at);
                continue;
            }

            float left = growth.SecondsLeft - seconds;
            float visibleLeft = growth.VisibleSecondsLeft - realSeconds;

            if (left <= 0f && visibleLeft <= 0f)
            {
                _ripe.Add((at, growth.Stage));
                continue;
            }

            _growing[at] = growth with
            {
                SecondsLeft = left,
                VisibleSecondsLeft = visibleLeft,
            };
        }
    }

    /// <summary>Projde jeden plátek okolí hráče a zařadí, co v něm najde.</summary>
    private void Scan(VoxelWorld world, Vector3 player, float seconds)
    {
        _scanClock += seconds;

        if (_scanClock < ScanInterval)
        {
            return;
        }

        _scanClock = 0f;

        int centreX = (int)MathF.Floor(player.X);
        int centreZ = (int)MathF.Floor(player.Z);

        int span = (Reach * 2) + 1;
        int width = (span + Slices - 1) / Slices;

        int from = centreX - Reach + (_slice * width);
        int to = Math.Min(from + width, centreX + Reach + 1);

        _slice = (_slice + 1) % Slices;

        for (int x = from; x < to; x++)
        {
            for (int z = centreZ - Reach; z <= centreZ + Reach; z++)
            {
                int centreY = (int)MathF.Floor(player.Y);
                int localBottom = Math.Max(0, centreY - Rise);
                int localTop = Math.Min(TerrainGenerator.WorldHeight - 1, centreY + Rise);

                int surfaceY = _generator?.SurfaceHeight(x, z) + 1 ?? -1;
                if (surfaceY >= 0 && surfaceY < TerrainGenerator.WorldHeight)
                {
                    ScanAt(world, new Vector3i(x, surfaceY, z));
                }

                // Ve vzduchu nebo hluboko pod zemí se prázdný svislý objem neprochází.
                // Povrch už byl zkontrolovaný výš a právě ten je pro přírodní les důležitý.
                if (_generator is not null
                    && (surfaceY < localBottom - 1 || surfaceY > localTop + 1))
                {
                    continue;
                }

                for (int y = localBottom; y <= localTop; y++)
                {
                    if (y != surfaceY)
                    {
                        ScanAt(world, new Vector3i(x, y, z));
                    }
                }
            }
        }
    }

    private void ScanAt(VoxelWorld world, Vector3i at)
    {
        ushort block = world.GetBlock(at.X, at.Y, at.Z);

        if (_planter.IsSapling(block))
        {
            Notice(at);
        }
        else if (_planter.IsTreeLog(block)
                 && !_growing.ContainsKey(at)
                 && _planter.TryGrowthStage(world, at, out int stage))
        {
            NoticeStage(at, stage);
        }
    }

    /// <summary>
    /// Zapíše dotčené chunky i s jejich sousedy.
    /// </summary>
    /// <remarks>
    /// Sousedé musí být taky: jejich odsazený objem obsahuje krajní bloky stromu, takže by
    /// na hranici zůstala stěna, která už tam nepatří. Je to táž úvaha jako
    /// v <c>ChunkStreamer.CollectChunksContaining</c>, jen hrubozrnná — strom se dotkne
    /// celých chunků, ne jednoho bloku, takže se stejně přemeshovává skoro všechno kolem.
    /// </remarks>
    private void Spread(IReadOnlyCollection<Vector3i> touched)
    {
        foreach (Vector3i chunk in touched)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var neighbour = new Vector3i(chunk.X + dx, chunk.Y + dy, chunk.Z + dz);

                        if (!_dirty.Contains(neighbour))
                        {
                            _dirty.Add(neighbour);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Jak dlouho poroste sazenice na daném místě.
    /// </summary>
    /// <remarks>
    /// Losuje se z polohy, ne z generátoru náhody. Dvě sazenice vedle sebe tak vyrostou
    /// každá jindy — a přitom je běh opakovatelný, takže se to dá otestovat.
    /// </remarks>
    private static float StageSeconds(Vector3i at, int stage)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)at.X) * 16777619u;
            hash = (hash ^ (uint)at.Y) * 16777619u;
            hash = (hash ^ (uint)at.Z) * 16777619u;
            hash ^= hash >> 13;
            hash *= 0x5bd1e995u;
            hash ^= hash >> 15;

            hash ^= (uint)(stage + 1) * 0x9e3779b9u;

            (float min, float max) = stage switch
            {
                0 => (12f, 22f),  // sazenice -> malý strom
                1 => (18f, 30f),  // malý -> střední
                _ => (24f, 40f),  // střední -> dospělý
            };

            return min + ((hash % 1000u) / 1000f * (max - min));
        }
    }

    /// <summary>Stav rozpracovaného růstu pro uložení vedle ekologických hodin.</summary>
    public sealed record GrowthSnapshot(
        int X, int Y, int Z, int Stage, float SecondsLeft, float VisibleSecondsLeft);

    public IReadOnlyList<GrowthSnapshot> Snapshot() =>
        [.. _growing.Select(pair => new GrowthSnapshot(
            pair.Key.X,
            pair.Key.Y,
            pair.Key.Z,
            pair.Value.Stage,
            pair.Value.SecondsLeft,
            pair.Value.VisibleSecondsLeft))];

    public void Restore(IEnumerable<GrowthSnapshot>? snapshots)
    {
        _growing.Clear();

        foreach (GrowthSnapshot snapshot in snapshots ?? [])
        {
            if (snapshot.Stage < 0 || snapshot.Stage >= TreePlanter.MatureGrowthStage)
            {
                continue;
            }

            var at = new Vector3i(snapshot.X, snapshot.Y, snapshot.Z);
            float visible = snapshot.VisibleSecondsLeft > 0f
                ? snapshot.VisibleSecondsLeft
                : MinVisibleStageSeconds;
            _growing[at] = new Growth(
                snapshot.Stage,
                Math.Max(0f, snapshot.SecondsLeft),
                visible);
        }
    }

    private sealed record Growth(int Stage, float SecondsLeft, float VisibleSecondsLeft);
}
