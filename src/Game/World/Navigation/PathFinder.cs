namespace Tesseris.Game.World.Navigation;

/// <summary>Jak dopadlo hledání cesty.</summary>
public enum PathResult
{
    /// <summary>Cesta nalezena.</summary>
    Found,

    /// <summary>Start nebo cíl není pochůzná buňka.</summary>
    InvalidEndpoint,

    /// <summary>Prohledalo se všechno dosažitelné a cíl mezi tím nebyl.</summary>
    Unreachable,

    /// <summary>Došel rozpočet na hledání. Cesta možná existuje, jen se nestihla najít.</summary>
    BudgetExhausted,
}

/// <summary>
/// Lokální A* nad pochůznou mřížkou jednoho chunku.
/// </summary>
/// <remarks>
/// <para><b>Celočíselné ceny.</b> Vodorovný krok stojí 10, výškový rozdíl 4 navíc za blok.
/// Není to kosmetika: <c>float</c> sčítaný v jiném pořadí dá jiný výsledek a pravidlo 6.6
/// staví savy a replaye na tom, že tentýž vstup dá tentýž výsledek. Heuristika je
/// manhattanská vzdálenost krát 10, tedy přípustná — nikdy cestu nepřecení.</para>
///
/// <para><b>Bez alokace na hledání.</b> Pracovní pole se založí jednou a mezi hledáními se
/// nečistí; místo toho nese každá buňka razítko běhu, a co má staré razítko, je nenavštívené.
/// Vyčistit 32 768 položek při každém hledání by bylo dražší než většina cest.</para>
///
/// <para><b>Rozpočet je povinný.</b> Zazděný cíl musí hledání vzdát v konečném čase a říct to —
/// bez stropu by kolonista zamrzl a s ním celý tick. Viz <see cref="PathResult"/>.</para>
///
/// <para><b>Zatím jen uvnitř jednoho chunku.</b> Nadstavba přes hranice chunků (portály
/// a abstraktní graf) je další krok T2.</para>
/// </remarks>
public sealed class PathFinder
{
    private const int StepCost = 10;
    private const int VerticalCost = 4;

    /// <summary>Kolik buněk se smí rozvinout, než se hledání vzdá.</summary>
    public const int DefaultBudget = 4096;

    /// <summary>
    /// Čtyři vodorovné směry. Úhlopříčky schválně ne — kolonista by procházel rohem mezi
    /// dvěma zdmi a vypadalo by to, že prošel skrz.
    /// </summary>
    public static readonly (int X, int Z)[] HorizontalDirections = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// O kolik se smí stoupat a klesat. Musí sedět na <see cref="Walkability"/>, jinak by
    /// graf sliboval kroky, které se ve hře neprovedou.
    /// </summary>
    public static readonly int[] VerticalOffsets = [Walkability.StepUp, 0, -1, -Walkability.StepDown];

    private readonly int[] _cost = new int[NavGrid.Volume];
    private readonly int[] _cameFrom = new int[NavGrid.Volume];
    private readonly int[] _stamp = new int[NavGrid.Volume];
    private readonly bool[] _closed = new bool[NavGrid.Volume];

    private int[] _heapCell = new int[1024];
    private int[] _heapPriority = new int[1024];
    private int _heapCount;
    private int _run;

    /// <summary>Kolik buněk rozvinulo poslední hledání. Pro měření a ladění.</summary>
    public int LastExpanded { get; private set; }

    /// <summary>
    /// Najde cestu mezi dvěma buňkami téhož chunku.
    /// </summary>
    /// <param name="path">
    /// Naplní se indexy buněk od startu k cíli včetně. Seznam se před použitím vyčistí.
    /// </param>
    public PathResult TryFindPath(NavGrid grid, int start, int goal, List<int> path, int budget = DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);

        path.Clear();
        LastExpanded = 0;

        if ((uint)start >= NavGrid.Volume || (uint)goal >= NavGrid.Volume
            || !grid.IsStandable(start) || !grid.IsStandable(goal))
        {
            return PathResult.InvalidEndpoint;
        }

        // Nové razítko místo mazání polí.
        _run++;
        _heapCount = 0;

        NavGrid.FromIndex(goal, out int goalX, out int goalY, out int goalZ);

        Visit(start, cost: 0, cameFrom: -1);
        Push(start, Heuristic(start, goalX, goalY, goalZ));

        while (_heapCount > 0)
        {
            int current = Pop();
            if (_closed[current] && _stamp[current] == _run)
            {
                continue;
            }

            _closed[current] = true;
            LastExpanded++;

            if (current == goal)
            {
                Reconstruct(current, path);
                return PathResult.Found;
            }

            if (LastExpanded >= budget)
            {
                // Rozpočet došel dřív, než se prohledalo všechno dosažitelné. Cesta možná
                // existuje — volající se má zeptat znovu, ne prohlásit cíl za nedosažitelný.
                return PathResult.BudgetExhausted;
            }

            ExpandNeighbours(grid, current, goalX, goalY, goalZ);
        }

        // Fronta došla a cíl mezi rozvinutými nebyl: cíl je za zdí.
        return PathResult.Unreachable;
    }

    private void ExpandNeighbours(NavGrid grid, int current, int goalX, int goalY, int goalZ)
    {
        NavGrid.FromIndex(current, out int x, out int y, out int z);
        int currentCost = _cost[current];

        foreach ((int dx, int dz) in HorizontalDirections)
        {
            int nx = x + dx;
            int nz = z + dz;

            foreach (int dy in VerticalOffsets)
            {
                int ny = y + dy;
                if (!NavGrid.InBounds(nx, ny, nz))
                {
                    continue;
                }

                int neighbour = NavGrid.Index(nx, ny, nz);
                if (!grid.IsStandable(neighbour))
                {
                    continue;
                }

                if (_stamp[neighbour] == _run && _closed[neighbour])
                {
                    continue;
                }

                int stepCost = StepCost + (Math.Abs(dy) * VerticalCost);
                int candidate = currentCost + stepCost;

                if (_stamp[neighbour] == _run && _cost[neighbour] <= candidate)
                {
                    continue;
                }

                Visit(neighbour, candidate, current);
                Push(neighbour, candidate + Heuristic(neighbour, goalX, goalY, goalZ));
            }
        }
    }

    private void Visit(int cell, int cost, int cameFrom)
    {
        _stamp[cell] = _run;
        _cost[cell] = cost;
        _cameFrom[cell] = cameFrom;
        _closed[cell] = false;
    }

    private static int Heuristic(int cell, int goalX, int goalY, int goalZ)
    {
        NavGrid.FromIndex(cell, out int x, out int y, out int z);

        // Manhattan po vodorovných osách; výška se do odhadu nepřičítá, protože svislý
        // pohyb je vždycky vedlejším produktem vodorovného kroku. Přičíst ji by heuristiku
        // přecenilo a A* by přestal dávat nejkratší cestu.
        _ = goalY;
        _ = y;
        return (Math.Abs(x - goalX) + Math.Abs(z - goalZ)) * StepCost;
    }

    private void Reconstruct(int goal, List<int> path)
    {
        int cell = goal;
        while (cell >= 0)
        {
            path.Add(cell);
            cell = _cameFrom[cell];
        }

        path.Reverse();
    }

    private void Push(int cell, int priority)
    {
        if (_heapCount == _heapCell.Length)
        {
            Array.Resize(ref _heapCell, _heapCell.Length * 2);
            Array.Resize(ref _heapPriority, _heapPriority.Length * 2);
        }

        int i = _heapCount++;
        _heapCell[i] = cell;
        _heapPriority[i] = priority;

        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_heapPriority[parent] <= _heapPriority[i])
            {
                break;
            }

            Swap(parent, i);
            i = parent;
        }
    }

    private int Pop()
    {
        int top = _heapCell[0];
        _heapCount--;
        _heapCell[0] = _heapCell[_heapCount];
        _heapPriority[0] = _heapPriority[_heapCount];

        int i = 0;
        while (true)
        {
            int left = (2 * i) + 1;
            int right = left + 1;
            int smallest = i;

            if (left < _heapCount && _heapPriority[left] < _heapPriority[smallest])
            {
                smallest = left;
            }

            if (right < _heapCount && _heapPriority[right] < _heapPriority[smallest])
            {
                smallest = right;
            }

            if (smallest == i)
            {
                break;
            }

            Swap(smallest, i);
            i = smallest;
        }

        return top;
    }

    private void Swap(int a, int b)
    {
        (_heapCell[a], _heapCell[b]) = (_heapCell[b], _heapCell[a]);
        (_heapPriority[a], _heapPriority[b]) = (_heapPriority[b], _heapPriority[a]);
    }
}
