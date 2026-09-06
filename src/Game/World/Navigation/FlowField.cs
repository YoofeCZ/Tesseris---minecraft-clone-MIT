namespace Tesseris.Game.World.Navigation;

/// <summary>
/// Jedno pole směrů ke společnému cíli pro libovolný počet tvorů.
/// </summary>
/// <remarks>
/// <para><b>K čemu to je.</b> Když padesát kolonistů dostane týž rozkaz — jděte kopat sem,
/// utečte do bezpečí — a každý si spočítá vlastní A*, je to padesát hledání. Flow field je
/// jedno prohledání od cíle ven; pak už každý jen přečte, kam udělat krok. Cena je nezávislá
/// na počtu tvorů.</para>
///
/// <para><b>Dijkstra pozpátku.</b> Prohledává se od cíle, ne k němu, a ukládá se cena dojití
/// do cíle. Heuristika se nepoužívá — cíl je jeden, ale startů je mnoho, takže se počítá
/// celé okolí a odhad by k ničemu nebyl.</para>
///
/// <para><b>Rozpočet.</b> Prohledávání se dá zastavit po zadaném počtu buněk. Kdo je za tou
/// hranicí, prostě směr nedostane a musí si říct o vlastní cestu — je to lepší než utrhnout
/// tick kvůli jednomu rozkazu.</para>
/// </remarks>
public sealed class FlowField
{
    private const int StepCost = 10;
    private const int VerticalCost = 4;

    /// <summary>Cena buňky, do které se z cíle nedá dostat.</summary>
    public const int Unreachable = int.MaxValue;

    private readonly int[] _cost = new int[NavGrid.Volume];
    private readonly int[] _next = new int[NavGrid.Volume];
    private readonly int[] _stamp = new int[NavGrid.Volume];

    private int[] _heapCell = new int[1024];
    private int[] _heapPriority = new int[1024];
    private int _heapCount;
    private int _run;

    /// <summary>Buňka, ke které pole vede.</summary>
    public int Goal { get; private set; } = -1;

    /// <summary>Kolik buněk pole pokrylo.</summary>
    public int Covered { get; private set; }

    /// <summary>Došel při stavbě rozpočet? Pak pole nepokrývá všechno dosažitelné.</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Postaví pole směrů vedoucí do zadané buňky.
    /// </summary>
    /// <returns>false, pokud cíl není pochůzný.</returns>
    public bool Build(NavGrid grid, int goal, int budget = 8192)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);

        Goal = -1;
        Covered = 0;
        Truncated = false;

        if ((uint)goal >= NavGrid.Volume || !grid.IsStandable(goal))
        {
            return false;
        }

        _run++;
        _heapCount = 0;
        Goal = goal;

        _stamp[goal] = _run;
        _cost[goal] = 0;
        _next[goal] = goal;
        Push(goal, 0);

        while (_heapCount > 0)
        {
            int current = Pop();

            if (Covered >= budget)
            {
                Truncated = true;
                break;
            }

            Covered++;
            ExpandFrom(grid, current);
        }

        return true;
    }

    private void ExpandFrom(NavGrid grid, int current)
    {
        NavGrid.FromIndex(current, out int x, out int y, out int z);
        int currentCost = _cost[current];

        foreach ((int dx, int dz) in PathFinder.HorizontalDirections)
        {
            foreach (int dy in PathFinder.VerticalOffsets)
            {
                int nx = x + dx;
                int ny = y + dy;
                int nz = z + dz;

                if (!NavGrid.InBounds(nx, ny, nz))
                {
                    continue;
                }

                int neighbour = NavGrid.Index(nx, ny, nz);
                if (!grid.IsStandable(neighbour))
                {
                    continue;
                }

                // Pole se staví POZPÁTKU: soused musí umět dojít sem, ne naopak. U kroků
                // nahoru a dolů to není totéž — spadnout o dva bloky jde, vyskočit ne.
                int back = -dy;
                if (Array.IndexOf(PathFinder.VerticalOffsets, back) < 0)
                {
                    continue;
                }

                int candidate = currentCost + StepCost + (Math.Abs(dy) * VerticalCost);
                if (_stamp[neighbour] == _run && _cost[neighbour] <= candidate)
                {
                    continue;
                }

                _stamp[neighbour] = _run;
                _cost[neighbour] = candidate;
                _next[neighbour] = current;
                Push(neighbour, candidate);
            }
        }
    }

    /// <summary>Cena dojití z téhle buňky do cíle, nebo <see cref="Unreachable"/>.</summary>
    public int CostAt(int cell) =>
        (uint)cell < NavGrid.Volume && _stamp[cell] == _run ? _cost[cell] : Unreachable;

    /// <summary>
    /// Kam udělat z téhle buňky krok. Vrací false, když sem pole nedosáhlo nebo už jsme v cíli.
    /// </summary>
    public bool TryNextStep(int cell, out int next)
    {
        next = -1;
        if ((uint)cell >= NavGrid.Volume || _stamp[cell] != _run || cell == Goal)
        {
            return false;
        }

        next = _next[cell];
        return true;
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
