using Tesseris.Game.Blocks;
using OpenTK.Mathematics;

namespace Tesseris.Game.World.Navigation;

/// <summary>
/// Navigace přes hranice chunků: portály a abstraktní graf nad pochůznými mřížkami.
/// </summary>
/// <remarks>
/// <para><b>Proč ne jeden velký A*.</b> Dohled 16 chunků je zhruba 19 400 chunků po 32 768
/// buňkách. Hledat cestu přes celý svět jedním průchodem znamená rozvinout statisíce buněk
/// na jednoho kolonistu — při dvou stech kolonistech to nemá šanci se vejít do tiku pod 8 ms.</para>
///
/// <para><b>Dvoustupňové hledání.</b> Nejdřív A* nad hrubým grafem portálů (jednotky až
/// desítky uzlů), pak lokální A* uvnitř chunků mezi po sobě jdoucími portály. Kolonista tak
/// nikdy nepočítá plnou cestu přes celý svět naráz.</para>
///
/// <para><b>Portál je buňka na hranici chunku, ze které se dá udělat krok do sousedního
/// chunku.</b> Hledá se ve světových souřadnicích, ne po stěnách — krok o blok nahoru na
/// hranici může skončit v chunku, který je diagonálně vedle, a rozdělení podle stěn by tenhle
/// případ minulo.</para>
///
/// <para><b>Alokace.</b> Abstraktní hledání používá slovníky a je to vědomé: portálů jsou
/// řády desítek, kdežto buněk desetitisíce. Jestli to při dvou stech kolonistech vadí, ukáže
/// až benchmark — optimalizovat to teď by bylo bez měření.</para>
/// </remarks>
public sealed class NavGraph
{
    private const int CrossCost = 10;

    private readonly Dictionary<Vector3i, NavGrid> _grids = [];
    private readonly Dictionary<Vector3i, List<Vector3i>> _portalsByChunk = [];
    private readonly Dictionary<Vector3i, List<Vector3i>> _crossings = [];
    private readonly Dictionary<(Vector3i From, Vector3i To), int> _localCost = [];
    private readonly PathFinder _local = new();
    private readonly List<int> _scratch = [];

    /// <summary>Kolik portálů graf drží.</summary>
    public int PortalCount => _crossings.Count;

    /// <summary>Kolik mřížek je v grafu.</summary>
    public int ChunkCount => _grids.Count;

    public void AddChunk(NavGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grids[grid.ChunkPosition] = grid;
    }

    public NavGrid? GridAt(Vector3i chunk) => _grids.GetValueOrDefault(chunk);

    /// <summary>Dá se stát na téhle světové buňce? Chunk, který v grafu není, je neprůchozí.</summary>
    public bool IsStandable(Vector3i worldCell)
    {
        Vector3i chunk = ToChunk(worldCell);
        return _grids.TryGetValue(chunk, out NavGrid? grid)
            && grid.IsStandable(LocalIndex(worldCell, chunk));
    }

    /// <summary>
    /// Ví graf o téhle buňce vůbec něco?
    /// </summary>
    /// <remarks>
    /// <para><b>„Nedá se tu stát" a „nevím" jsou dvě různé odpovědi.</b>
    /// <see cref="IsStandable"/> vrací false na obojí, což hledání cesty stačí — cesta přes
    /// nepostavený chunk se stejně nenaplánuje. Fyzice ale ne: kdo se ptá „mám pod nohama
    /// zem", by na „nevím" spadl.</para>
    ///
    /// <para><b>Naměřeno v běžící hře.</b> Po načtení savu odletěl hráč pryč, navigace se
    /// udržuje jen kolem něj (<c>ColonyRuntime.NavigationRadiusChunks</c>), takže chunk
    /// s kolonisty mřížku neměl — a sonda hlásila, že jsou <b>100 % tiků ve vzduchu</b>
    /// (900 z 900). Propadali světem až na nulu jen proto, že se hráč vzdálil.</para>
    /// </remarks>
    public bool IsKnown(Vector3i worldCell) => _grids.ContainsKey(ToChunk(worldCell));

    /// <summary>
    /// Přidá portály jednoho nově postaveného chunku a srovná jeho sousedy.
    /// </summary>
    /// <remarks>
    /// <b>Tohle se musí používat místo <see cref="BuildPortals"/> při streamování.</b>
    /// Naměřeno: úplná přestavba prochází všechny chunky krát 32 768 buněk, takže při
    /// 840 postavených mřížkách to je 27 milionů iterací — a volalo se to po každém
    /// jednom přidaném chunku. Tick p99 z toho vyskočil na 120 ms proti rozpočtu 8 ms,
    /// přestože rozpad po kategoriích ukazoval mikrosekundy: čas ležel v tomhle volání,
    /// ne v pathfindingu.
    /// </remarks>
    public void AddPortalsFor(Vector3i chunk)
    {
        RefreshPortals(chunk);

        foreach (Vector3i neighbour in HorizontalNeighbours(chunk))
        {
            if (_grids.ContainsKey(neighbour))
            {
                RefreshPortals(neighbour);
            }
        }
    }

    /// <summary>
    /// Najde portály a jejich přechody pro CELÝ graf. Drahé — viz <see cref="AddPortalsFor"/>.
    /// </summary>
    public void BuildPortals()
    {
        _portalsByChunk.Clear();
        _crossings.Clear();
        _localCost.Clear();

        foreach ((Vector3i chunk, NavGrid grid) in _grids)
        {
            for (int index = 0; index < NavGrid.Volume; index++)
            {
                if (!grid.IsStandable(index))
                {
                    continue;
                }

                NavGrid.FromIndex(index, out int lx, out int ly, out int lz);

                // Jen buňky na obvodu chunku můžou vést ven.
                if (lx is not (0 or NavGrid.Size - 1) && lz is not (0 or NavGrid.Size - 1))
                {
                    continue;
                }

                Vector3i cell = new(
                    (chunk.X * NavGrid.Size) + lx,
                    (chunk.Y * NavGrid.Size) + ly,
                    (chunk.Z * NavGrid.Size) + lz);

                CollectCrossings(chunk, cell);
            }
        }
    }

    /// <summary>
    /// Promítne změnu jednoho bloku do mřížek i portálů.
    /// </summary>
    /// <remarks>
    /// <para>Aktualizuje se sloupec ve vlastním chunku a — když zasažený rozsah přeteče přes
    /// vodorovnou hranu chunku — i ve svislých sousedech. Vodorovní sousedé se nedotknou,
    /// protože obal tvora ani nosná plocha z vlastního sloupce nevystupují.</para>
    ///
    /// <para><b>Pozor na záměnu s remeshem.</b> Audit zjistil, že jedno kopnutí zneplatní až
    /// osm chunků — to ale platí pro <b>osvětlení a geometrii</b>, kde světlo dosahuje daleko.
    /// Navigace tolik nepotřebuje: pochůznost je čistě sloupcová.</para>
    /// </remarks>
    /// <returns>true, pokud se pochůznost změnila a portály se přepočítaly.</returns>
    public bool OnBlockChanged(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        int worldX,
        int worldY,
        int worldZ)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        int chunkX = worldX >> Chunk.SizeShift;
        int chunkZ = worldZ >> Chunk.SizeShift;
        int lowest = (worldY - NavGrid.AffectedBelow) >> Chunk.SizeShift;
        int highest = (worldY + NavGrid.AffectedAbove) >> Chunk.SizeShift;

        bool changed = false;
        for (int chunkY = lowest; chunkY <= highest; chunkY++)
        {
            var chunk = new Vector3i(chunkX, chunkY, chunkZ);
            if (!_grids.TryGetValue(chunk, out NavGrid? grid))
            {
                continue;
            }

            if (!grid.UpdateAfterBlockChange(world, blocks, water, worldX, worldY, worldZ))
            {
                continue;
            }

            changed = true;
            RefreshPortals(chunk);

            // Portály sousedů z druhé strany hranice mířily sem, takže se musí přepočítat taky.
            foreach (Vector3i neighbour in HorizontalNeighbours(chunk))
            {
                if (_grids.ContainsKey(neighbour))
                {
                    RefreshPortals(neighbour);
                }
            }
        }

        return changed;
    }

    /// <summary>Sloupce na obvodu chunku. Spočítají se jednou, ne při každém obnovení.</summary>
    private static readonly (int X, int Z)[] BoundaryColumns = BuildBoundaryColumns();

    private static (int X, int Z)[] BuildBoundaryColumns()
    {
        var columns = new List<(int, int)>((NavGrid.Size * 4) - 4);
        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                if (x is 0 or NavGrid.Size - 1 || z is 0 or NavGrid.Size - 1)
                {
                    columns.Add((x, z));
                }
            }
        }

        return [.. columns];
    }

    private static IEnumerable<Vector3i> HorizontalNeighbours(Vector3i chunk)
    {
        yield return chunk with { X = chunk.X + 1 };
        yield return chunk with { X = chunk.X - 1 };
        yield return chunk with { Z = chunk.Z + 1 };
        yield return chunk with { Z = chunk.Z - 1 };
    }

    /// <summary>Přepočítá portály jediného chunku. Cizí portály se nedotkne.</summary>
    private void RefreshPortals(Vector3i chunk)
    {
        if (_portalsByChunk.TryGetValue(chunk, out List<Vector3i>? previous))
        {
            foreach (Vector3i cell in previous)
            {
                _crossings.Remove(cell);
            }

            previous.Clear();
        }

        if (!_grids.TryGetValue(chunk, out NavGrid? grid))
        {
            return;
        }

        // Cena lokálních cest se mohla změnit spolu s pochůzností.
        _localCost.Clear();

        // JEN OBVOD, ne celý objem. Portál může být pouze na kraji chunku, takže procházet
        // všech 32 768 buněk je devětkrát víc práce než je potřeba: obvodových sloupců je
        // 124 z 1 024. Naměřeno, že tohle volání drželo tick p99 na 25 ms.
        for (int ly = 0; ly < NavGrid.Size; ly++)
        {
            foreach ((int lx, int lz) in BoundaryColumns)
            {
                if (!grid.IsStandable(lx, ly, lz))
                {
                    continue;
                }

                CollectCrossings(chunk, new Vector3i(
                    (chunk.X * NavGrid.Size) + lx,
                    (chunk.Y * NavGrid.Size) + ly,
                    (chunk.Z * NavGrid.Size) + lz));
            }
        }
    }

    private void CollectCrossings(Vector3i chunk, Vector3i cell)
    {
        List<Vector3i>? targets = null;

        foreach ((int dx, int dz) in PathFinder.HorizontalDirections)
        {
            foreach (int dy in PathFinder.VerticalOffsets)
            {
                Vector3i neighbour = new(cell.X + dx, cell.Y + dy, cell.Z + dz);
                Vector3i neighbourChunk = ToChunk(neighbour);

                // Zajímají jen kroky VEN z chunku; uvnitř je od toho lokální A*.
                if (neighbourChunk == chunk || !IsStandable(neighbour))
                {
                    continue;
                }

                (targets ??= []).Add(neighbour);
            }
        }

        if (targets is null)
        {
            return;
        }

        _crossings[cell] = targets;
        if (!_portalsByChunk.TryGetValue(chunk, out List<Vector3i>? list))
        {
            list = [];
            _portalsByChunk[chunk] = list;
        }

        list.Add(cell);
    }

    /// <summary>
    /// Najde cestu mezi dvěma světovými buňkami, i když leží v různých chuncích.
    /// </summary>
    /// <param name="path">Naplní se buňkami od startu k cíli včetně obou konců.</param>
    public PathResult TryFindPath(
        Vector3i start,
        Vector3i goal,
        List<Vector3i> path,
        int budget = PathFinder.DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.Clear();

        if (!IsStandable(start) || !IsStandable(goal))
        {
            return PathResult.InvalidEndpoint;
        }

        Vector3i startChunk = ToChunk(start);
        Vector3i goalChunk = ToChunk(goal);

        if (startChunk == goalChunk)
        {
            return TryLocalPath(startChunk, start, goal, path, budget);
        }

        return TryHierarchicalPath(start, goal, startChunk, goalChunk, path, budget);
    }

    private PathResult TryHierarchicalPath(
        Vector3i start,
        Vector3i goal,
        Vector3i startChunk,
        Vector3i goalChunk,
        List<Vector3i> path,
        int budget)
    {
        // A* nad portály. Uzly jsou buňky portálů plus start a cíl.
        var cost = new Dictionary<Vector3i, int> { [start] = 0 };
        var cameFrom = new Dictionary<Vector3i, Vector3i>();
        var closed = new HashSet<Vector3i>();
        var open = new PriorityQueue<Vector3i, int>();
        open.Enqueue(start, Estimate(start, goal));

        int expanded = 0;

        while (open.Count > 0)
        {
            Vector3i current = open.Dequeue();
            if (!closed.Add(current))
            {
                continue;
            }

            if (current == goal)
            {
                return Stitch(start, goal, cameFrom, path, budget);
            }

            if (++expanded >= budget)
            {
                return PathResult.BudgetExhausted;
            }

            Vector3i chunk = ToChunk(current);
            int currentCost = cost[current];

            // 1) Přechody přes hranici: krok do sousedního chunku.
            if (_crossings.TryGetValue(current, out List<Vector3i>? crossings))
            {
                foreach (Vector3i target in crossings)
                {
                    Relax(current, target, currentCost + CrossCost, cost, cameFrom, closed, open, goal);
                }
            }

            // 2) Uvnitř chunku: k ostatním portálům téhož chunku a k cíli, pokud je tady.
            foreach (Vector3i target in InsideTargets(chunk, goalChunk, goal))
            {
                if (target == current)
                {
                    continue;
                }

                int local = LocalCost(chunk, current, target, budget);
                if (local < 0)
                {
                    continue;
                }

                Relax(current, target, currentCost + local, cost, cameFrom, closed, open, goal);
            }
        }

        return PathResult.Unreachable;
    }

    private IEnumerable<Vector3i> InsideTargets(Vector3i chunk, Vector3i goalChunk, Vector3i goal)
    {
        if (_portalsByChunk.TryGetValue(chunk, out List<Vector3i>? portals))
        {
            foreach (Vector3i portal in portals)
            {
                yield return portal;
            }
        }

        if (chunk == goalChunk)
        {
            yield return goal;
        }
    }

    private static void Relax(
        Vector3i from,
        Vector3i to,
        int candidate,
        Dictionary<Vector3i, int> cost,
        Dictionary<Vector3i, Vector3i> cameFrom,
        HashSet<Vector3i> closed,
        PriorityQueue<Vector3i, int> open,
        Vector3i goal)
    {
        if (closed.Contains(to) || (cost.TryGetValue(to, out int known) && known <= candidate))
        {
            return;
        }

        cost[to] = candidate;
        cameFrom[to] = from;
        open.Enqueue(to, candidate + Estimate(to, goal));
    }

    /// <summary>Cena lokální cesty mezi dvěma buňkami téhož chunku, nebo −1, když nevede.</summary>
    private int LocalCost(Vector3i chunk, Vector3i from, Vector3i to, int budget)
    {
        if (_localCost.TryGetValue((from, to), out int cached))
        {
            return cached;
        }

        NavGrid grid = _grids[chunk];
        PathResult result = _local.TryFindPath(
            grid,
            LocalIndex(from, chunk),
            LocalIndex(to, chunk),
            _scratch,
            budget);

        // Cena se počítá z délky cesty, ne z vnitřní ceny hledání — stačí monotónní odhad
        // a tohle je jediné, co PathFinder ven vydává.
        int value = result == PathResult.Found ? (_scratch.Count - 1) * CrossCost : -1;
        _localCost[(from, to)] = value;
        return value;
    }

    /// <summary>Poskládá výslednou cestu z lokálních úseků mezi po sobě jdoucími uzly.</summary>
    private PathResult Stitch(
        Vector3i start,
        Vector3i goal,
        Dictionary<Vector3i, Vector3i> cameFrom,
        List<Vector3i> path,
        int budget)
    {
        var nodes = new List<Vector3i> { goal };
        Vector3i cell = goal;
        while (cell != start)
        {
            if (!cameFrom.TryGetValue(cell, out Vector3i previous))
            {
                return PathResult.Unreachable;
            }

            cell = previous;
            nodes.Add(cell);
        }

        nodes.Reverse();

        path.Add(nodes[0]);
        for (int i = 1; i < nodes.Count; i++)
        {
            Vector3i from = nodes[i - 1];
            Vector3i to = nodes[i];

            if (ToChunk(from) == ToChunk(to))
            {
                // Úsek uvnitř chunku: dopočítat buňku po buňce.
                var segment = new List<Vector3i>();
                if (TryLocalPath(ToChunk(from), from, to, segment, budget) != PathResult.Found)
                {
                    return PathResult.Unreachable;
                }

                for (int j = 1; j < segment.Count; j++)
                {
                    path.Add(segment[j]);
                }
            }
            else
            {
                // Přechod přes hranici je jediný krok.
                path.Add(to);
            }
        }

        return PathResult.Found;
    }

    private PathResult TryLocalPath(Vector3i chunk, Vector3i from, Vector3i to, List<Vector3i> path, int budget)
    {
        path.Clear();
        if (!_grids.TryGetValue(chunk, out NavGrid? grid))
        {
            return PathResult.InvalidEndpoint;
        }

        PathResult result = _local.TryFindPath(
            grid,
            LocalIndex(from, chunk),
            LocalIndex(to, chunk),
            _scratch,
            budget);

        if (result != PathResult.Found)
        {
            return result;
        }

        foreach (int index in _scratch)
        {
            NavGrid.FromIndex(index, out int lx, out int ly, out int lz);
            path.Add(new Vector3i(
                (chunk.X * NavGrid.Size) + lx,
                (chunk.Y * NavGrid.Size) + ly,
                (chunk.Z * NavGrid.Size) + lz));
        }

        return PathResult.Found;
    }

    private static int Estimate(Vector3i from, Vector3i to) =>
        (Math.Abs(from.X - to.X) + Math.Abs(from.Z - to.Z)) * CrossCost;

    private static Vector3i ToChunk(Vector3i cell) => new(
        cell.X >> Chunk.SizeShift,
        cell.Y >> Chunk.SizeShift,
        cell.Z >> Chunk.SizeShift);

    private static int LocalIndex(Vector3i cell, Vector3i chunk) => NavGrid.Index(
        cell.X - (chunk.X * NavGrid.Size),
        cell.Y - (chunk.Y * NavGrid.Size),
        cell.Z - (chunk.Z * NavGrid.Size));
}
