using OpenTK.Mathematics;

namespace Tesseris.Game.World.Navigation;

/// <summary>Hotový výsledek hledání pro jednoho žadatele.</summary>
/// <param name="AgentId">Kdo si o cestu řekl.</param>
/// <param name="Result">Jak to dopadlo.</param>
/// <param name="Path">Buňky cesty. U neúspěchu prázdné.</param>
public readonly record struct PathAnswer(int AgentId, PathResult Result, IReadOnlyList<Vector3i> Path);

/// <summary>
/// Fronta žádostí o cestu s rozpočtem na tik.
/// </summary>
/// <remarks>
/// <para><b>Proč to musí existovat.</b> Naměřeno, že jedna cesta stojí 0,858 ms, takže do
/// rozpočtu tiku 8 ms se vejde devět hledání. Dvě stě kolonistů se tedy nesmí ptát každý tik —
/// bez fronty by první hromadný rozkaz utrhl tick na dvacetinásobek.</para>
///
/// <para><b>Kolonista, který čeká na cestu, chvíli stojí. To je v pořádku, zásek to není</b>
/// — takhle to zadání M0 výslovně chce. Zásek je až to, když stojí a nikdy se nedozví.</para>
///
/// <para><b>Jeden žadatel, jedna žádost.</b> Nová žádost od téhož kolonisty přepíše starou.
/// Bez toho by se ve frontě hromadily cíle, které už dávno nikoho nezajímají — kolonista,
/// který přehodí úkol pětkrát za vteřinu, by zabral pět míst.</para>
///
/// <para><b>Fronta je poctivá.</b> Bere se v pořadí, ve kterém žádosti přišly, takže se na
/// nikoho nezapomene. Prioritu zatím nemá; až budou práce mít naléhavost, patří sem.</para>
/// </remarks>
public sealed class PathRequestQueue
{
    /// <summary>Kolik hledání se smí odbavit za jeden tik.</summary>
    /// <remarks>
    /// Devět vychází z měření (0,858 ms na cestu, rozpočet tiku 8 ms). Osm je to samé se
    /// zaokrouhlením dolů, aby zbylo na zbytek simulace.
    /// </remarks>
    public const int DefaultBudgetPerTick = 8;

    private readonly record struct Pending(int AgentId, Vector3i Start, Vector3i Goal);

    private readonly List<Pending> _pending = [];
    private readonly Dictionary<int, int> _positionByAgent = [];
    private readonly List<PathAnswer> _answers = [];
    private readonly List<Vector3i> _scratch = [];

    /// <summary>Kolik žádostí čeká na odbavení.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Kolik hledání odbavil poslední tik.</summary>
    public int LastProcessed { get; private set; }

    /// <summary>Výsledky z posledního tiku. Platí do dalšího volání <see cref="Process"/>.</summary>
    public IReadOnlyList<PathAnswer> Answers => _answers;

    /// <summary>Zařadí žádost. Starší žádost téhož kolonisty se zahodí.</summary>
    public void Request(int agentId, Vector3i start, Vector3i goal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(agentId);

        if (_positionByAgent.TryGetValue(agentId, out int existing))
        {
            _pending[existing] = new Pending(agentId, start, goal);
            return;
        }

        _positionByAgent[agentId] = _pending.Count;
        _pending.Add(new Pending(agentId, start, goal));
    }

    /// <summary>Zruší čekající žádost, například když kolonista úkol ztratí.</summary>
    public bool Cancel(int agentId)
    {
        if (!_positionByAgent.TryGetValue(agentId, out int position))
        {
            return false;
        }

        _pending.RemoveAt(position);
        _positionByAgent.Remove(agentId);

        // Indexy za odebraným se posunuly.
        for (int i = position; i < _pending.Count; i++)
        {
            _positionByAgent[_pending[i].AgentId] = i;
        }

        return true;
    }

    /// <summary>
    /// Odbaví nejvýš <paramref name="budget"/> žádostí a naplní <see cref="Answers"/>.
    /// </summary>
    public void Process(NavGraph graph, int budget = DefaultBudgetPerTick, int searchBudget = PathFinder.DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);

        _answers.Clear();
        LastProcessed = 0;

        int take = Math.Min(budget, _pending.Count);
        for (int i = 0; i < take; i++)
        {
            Pending request = _pending[i];
            PathResult result = graph.TryFindPath(request.Start, request.Goal, _scratch, searchBudget);

            _answers.Add(new PathAnswer(
                request.AgentId,
                result,
                result == PathResult.Found ? _scratch.ToArray() : []));

            _positionByAgent.Remove(request.AgentId);
            LastProcessed++;
        }

        _pending.RemoveRange(0, take);

        // Zbývajícím se posunuly indexy.
        for (int i = 0; i < _pending.Count; i++)
        {
            _positionByAgent[_pending[i].AgentId] = i;
        }
    }

    public void Clear()
    {
        _pending.Clear();
        _positionByAgent.Clear();
        _answers.Clear();
        LastProcessed = 0;
    }
}
