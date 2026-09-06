using System.Collections.Concurrent;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.Micro;

/// <summary>
/// Sdílená zásoba hotových otesaných tvarů.
///
/// Klíčem je obsahový hash otesaného bloku. Sto sloupků otesaných do stejného tvaru
/// se zameshuje jednou a všechny pak sáhnou po témž výsledku — geometrie i kolizní kvádry.
/// To je smysl celé téhle cesty; bez ní by tisíc stejných sloupků znamenalo tisíc
/// samostatných meshovacích úloh.
///
/// Cache je souběžná, protože ji používají worker vlákna při meshování chunků i hlavní
/// vlákno při kolizích. Kolize hashů se řeší porovnáním obsahu, takže shodný hash ještě
/// neznamená shodný tvar.
/// </summary>
public sealed class MicroShapeCache
{
    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    private readonly BlockRegistry _registry;

    private long _hits;
    private long _misses;

    public MicroShapeCache(BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Počet různých tvarů, které cache drží. Tohle číslo dokládá deduplikaci.</summary>
    public int UniqueShapes => _entries.Count;

    /// <summary>Kolikrát se sáhlo po hotovém tvaru.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Kolikrát se tvar musel spočítat.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Vrátí hotový tvar, nebo ho spočítá a uloží.</summary>
    public MicroShape Get(MicroBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        long hash = block.ContentHash;

        if (_entries.TryGetValue(hash, out Entry existing) && existing.Source.HasSameContent(block))
        {
            Interlocked.Increment(ref _hits);
            return existing.Shape;
        }

        Interlocked.Increment(ref _misses);

        MicroShape shape = MicroMesher.Build(block, _registry);

        // Při kolizi hashů vyhraje ten, kdo dorazí první; druhý si tvar spočítá a použije
        // pokaždé znovu. Je to vzácné a výsledek zůstává správný.
        _entries.TryAdd(hash, new Entry(block, shape));

        return shape;
    }

    /// <summary>Zapomene všechny tvary. Používá se jen v testech.</summary>
    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    /// <summary>
    /// Uložený tvar spolu s blokem, ze kterého vznikl. Blok se drží kvůli potvrzení shody
    /// při kolizi hashů.
    /// </summary>
    private readonly record struct Entry(MicroBlock Source, MicroShape Shape);
}
