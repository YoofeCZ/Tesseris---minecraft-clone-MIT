using OpenTK.Mathematics;
using Tesseris.Game.World.Navigation;

namespace Tesseris.Game.Colony;

/// <summary>
/// Kam se kolonisté odsud vůbec dostanou.
/// </summary>
/// <remarks>
/// <para><b>Proč to existuje.</b> Označování dřív TIŠE přijalo i práci, kterou nikdo nemůže
/// splnit — naměřeno 115 odložených úkolů ze 130 označených. Hráč nedostal žádnou zpětnou
/// vazbu, což je horší chyba než špatná kamera: nedá se z ní poznat, co dělá špatně.</para>
///
/// <para><b>Dosažitelnost NENÍ „má sousední pochůznou buňku".</b> Povrchová buňka může být
/// za útesem nebo na ostrově: soused pochůzný je, ale cesta k němu nevede. Proto se ptáme
/// na skutečnou souvislou oblast, ne na okolí cíle.</para>
///
/// <para><b>Jeden průchod do šířky místo hledání cesty na buňku.</b> Jedno hledání cesty
/// stojí naměřených 0,858 ms a rozpočet celého tiku je 8 ms — u výběru o stovkách buněk by
/// to byly stovky milisekund. Průchod do šířky z poloh kolonistů obarví celou dosažitelnou
/// oblast jednou a pak je dotaz na buňku jen vyhledání v množině. Neběží to navíc v tick
/// cestě, ale při puštění tlačítka myši.</para>
///
/// <para><b>Strop na počet buněk je povinný.</b> Souvislá oblast může být obrovská; bez
/// stropu by jedno kliknutí zabralo neomezeně dlouho. Když strop dojde, prohlásí se
/// výsledek za nedůvěryhodný (<see cref="Complete"/> je false) a označování se v tom případě
/// radši nefiltruje — odmítnout hráči práci na základě nedopočítaného výsledku by bylo horší
/// než ji přijmout.</para>
/// </remarks>
public sealed class ReachableCells
{
    /// <summary>Kolik pochůzných buněk se smí obarvit, než se průchod vzdá.</summary>
    public const int MaximumCells = 200_000;

    private readonly HashSet<Vector3i> _visited = [];
    private readonly Queue<Vector3i> _queue = new();

    /// <summary>Kolik pochůzných buněk je odsud dosažitelných.</summary>
    public int Count => _visited.Count;

    /// <summary>Prošla se celá souvislá oblast, nebo došel strop?</summary>
    public bool Complete { get; private set; }

    /// <summary>Obarví souvislou oblast z poloh všech kolonistů.</summary>
    public void Rebuild(NavGraph graph, ColonySimulation colonists)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(colonists);

        _visited.Clear();
        _queue.Clear();
        Complete = true;

        for (int id = 0; id < colonists.Count; id++)
        {
            Vector3i start = colonists.CellOf(id);
            if (graph.IsStandable(start) && _visited.Add(start))
            {
                _queue.Enqueue(start);
            }
        }

        while (_queue.Count > 0)
        {
            if (_visited.Count >= MaximumCells)
            {
                Complete = false;
                return;
            }

            Vector3i cell = _queue.Dequeue();

            // Sousedství musí být TOTOŽNÉ s tím, co umí pathfinder, jinak by tenhle průchod
            // sliboval kroky, které kolonista neudělá, nebo naopak zakazoval ty, které umí.
            foreach ((int dx, int dz) in PathFinder.HorizontalDirections)
            {
                foreach (int dy in PathFinder.VerticalOffsets)
                {
                    var next = new Vector3i(cell.X + dx, cell.Y + dy, cell.Z + dz);
                    if (graph.IsStandable(next) && _visited.Add(next))
                    {
                        _queue.Enqueue(next);
                    }
                }
            }
        }
    }

    /// <summary>Dostane se na tuhle pochůznou buňku někdo?</summary>
    public bool Contains(Vector3i cell) => _visited.Contains(cell);

    /// <summary>
    /// Dá se na tenhle blok dosáhnout, tedy stojí vedle něj dosažitelná pochůzná buňka?
    /// </summary>
    /// <remarks>
    /// Sousedství je stejné jako v <c>ColonySimulation.AdjacentStandable</c>: nejdřív buňka
    /// nad blokem (kope se shora), pak strany v rozsahu jednoho patra. Kdyby se to rozešlo,
    /// filtr by odmítal práci, kterou by kolonista zvládl.
    /// </remarks>
    public bool CanReachBlock(Vector3i target)
    {
        if (Contains(new Vector3i(target.X, target.Y + 1, target.Z)))
        {
            return true;
        }

        foreach ((int dx, int dz) in PathFinder.HorizontalDirections)
        {
            if (Contains(new Vector3i(target.X + dx, target.Y, target.Z + dz))
                || Contains(new Vector3i(target.X + dx, target.Y + 1, target.Z + dz))
                || Contains(new Vector3i(target.X + dx, target.Y - 1, target.Z + dz)))
            {
                return true;
            }
        }

        return false;
    }
}
