using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>
/// Ukládání a načítání upravených chunků.
///
/// <para><b>Ukládá se jen to, co hráč změnil.</b> Generátor je deterministický, takže
/// nedotčený chunk se ze seedu spočítá vždycky stejně — a spočítat ho je levnější než
/// číst z disku. Na disk jde jen rozdíl proti generátoru, což je u čerstvého světa nula
/// a u dlouho hraného zlomek.</para>
///
/// <para><b>Zápis běží na worker vláknech</b>, čtení taky. Hlavní vlákno se disku nedotkne;
/// jediné, co dělá, je že řekne „tenhle chunk se uvolňuje" a jde dál.</para>
///
/// <para><b>Uvolňovaný chunk se musí uložit dřív, než zmizí z paměti.</b> Streamer chunk
/// odebere ze světa a zahodí; kdyby se ukládal až potom, nebylo by co. Předává se proto
/// odkaz na chunk, ne jen jeho pozice.</para>
/// </summary>
public sealed class WorldStorage : IDisposable
{
    private readonly string _regionDirectory;
    private readonly JobSystem _jobs;

    // Registry se veze až do serializace: v souboru jsou jména bloků, ne indexy, aby
    // přidání nového typu bloku neposunulo význam všech uložených.
    private readonly BlockRegistry _registry;

    // Lazy, ne přímo RegionFile: ConcurrentDictionary.GetOrAdd NEZARUČUJE, že se továrna
    // zavolá jen jednou. Dvě vlákna tak otevřela týž soubor a při FileShare.None druhé
    // otevření spadlo — chyba se schovala do catch a úprava se tiše ztratila. Lazy
    // s ExecutionAndPublication zajistí, že soubor otevře právě jedno vlákno.
    private readonly ConcurrentDictionary<Vector2i, Lazy<RegionFile>> _regions = [];

    // Kdy se ke kterému regionu naposledy sáhlo. Podle toho se zavírají ty nejstudenější.
    private readonly ConcurrentDictionary<Vector2i, long> _lastTouched = [];

    private long _touchCounter;

    // Chunky, které čekají na zápis. Slouží zároveň jako záchranná síť pro čtení: kdyby
    // se hráč vrátil dřív, než zápis doběhne, načte se z fronty místo z disku.
    private readonly ConcurrentDictionary<Vector3i, Chunk> _pending = [];

    private int _saved;
    private int _loaded;
    private volatile bool _disposed;

    public WorldStorage(string worldDirectory, JobSystem jobs, BlockRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(registry);

        _jobs = jobs;
        _registry = registry;
        _regionDirectory = Path.Combine(worldDirectory, "region");
        Directory.CreateDirectory(_regionDirectory);
    }

    /// <summary>Kolik chunků se za běh uložilo.</summary>
    public int SavedChunks => Volatile.Read(ref _saved);

    /// <summary>Kolik chunků se za běh načetlo z disku místo generování.</summary>
    public int LoadedChunks => Volatile.Read(ref _loaded);

    /// <summary>Kolik chunků čeká na zápis.</summary>
    public int PendingWrites => _pending.Count;

    /// <summary>Kolik region souborů je otevřených.</summary>
    public int OpenRegions => _regions.Count;

    /// <summary>
    /// Zařadí chunk k uložení. Neupravené chunky se tiše přeskočí, takže volající
    /// nemusí nic rozlišovat.
    /// </summary>
    public void Save(Vector3i position, Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (_disposed || !chunk.IsModified)
        {
            return;
        }

        _pending[position] = chunk;
        _jobs.Submit(() => WriteNow(position, chunk));
    }

    /// <summary>
    /// Načte chunk, nebo vrátí null, když na disku není a má se vygenerovat.
    ///
    /// <para>Volá se z worker vlákna před generováním.</para>
    /// </summary>
    public Chunk? TryLoad(Vector3i position)
    {
        // Čerstvě uložený chunk ještě nemusí být na disku. Fronta má přednost, jinak by
        // se hráči po rychlém návratu vrátila starší verze, nebo dokonce nic.
        if (_pending.TryGetValue(position, out Chunk? waiting))
        {
            Interlocked.Increment(ref _loaded);
            return waiting;
        }

        try
        {
            RegionFile region = RegionAt(RegionFile.RegionOf(position));
            byte[]? payload = region.Read(RegionFile.SlotOf(position));

            if (payload is null)
            {
                return null;
            }

            Interlocked.Increment(ref _loaded);
            return ChunkSerializer.Decode(payload, _registry);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Poškozený region nesmí shodit hru. Chunk se vygeneruje znovu — hráč přijde
            // o to, co v něm postavil, ale zbytek světa zůstane.
            Log.Warn($"Chunk {position} se nepodařilo načíst ({ex.Message}), generuje se znovu.");
            return null;
        }
    }

    /// <summary>Dopíše všechno rozpracované a zavře soubory. Volá se při ukončení hry.</summary>
    public void FlushAll()
    {
        // Kopie klíčů: fronta se během zápisu mění.
        foreach (Vector3i position in _pending.Keys)
        {
            if (_pending.TryGetValue(position, out Chunk? chunk))
            {
                WriteNow(position, chunk);
            }
        }

        foreach (Lazy<RegionFile> region in _regions.Values)
        {
            if (region.IsValueCreated)
            {
                region.Value.Flush();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FlushAll();

        foreach (Lazy<RegionFile> region in _regions.Values)
        {
            if (region.IsValueCreated)
            {
                region.Value.Dispose();
            }
        }

        _regions.Clear();
        _pending.Clear();
    }

    private void WriteNow(Vector3i position, Chunk chunk)
    {
        try
        {
            RegionFile region = RegionAt(RegionFile.RegionOf(position));
            region.Write(RegionFile.SlotOf(position), ChunkSerializer.Encode(chunk, _registry));

            Interlocked.Increment(ref _saved);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Chunk {position} se nepodařilo uložit: {ex.Message}");
        }
        finally
        {
            // Z fronty se odebírá až po zápisu, ne před ním. Do té doby z ní čte TryLoad.
            _pending.TryRemove(position, out _);
        }
    }

    private RegionFile RegionAt(Vector2i region)
    {
        RegionFile file = _regions.GetOrAdd(
            region,
            static (key, self) => new Lazy<RegionFile>(
                () => RegionFile.Open(Path.Combine(self._regionDirectory, $"r.{key.X}.{key.Y}.vxr")),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;

        _lastTouched[region] = Interlocked.Increment(ref _touchCounter);

        CloseColdRegions();

        return file;
    }

    /// <summary>
    /// Zavře regiony, ke kterým se nejdéle nesahalo.
    /// </summary>
    /// <remarks>
    /// <para>Otevřený region stojí dva indexy po 8192 položkách, tedy 64 kB, plus otevřený
    /// soubor se systémovým popisovačem. Dřív se nezavíral nikdy: každý region, který hráč
    /// přeletěl, zůstal viset až do konce hry, a protože se soubor otevírá před generováním
    /// každého chunku, množina rostla s každým novým kusem světa.</para>
    ///
    /// <para>Region pokrývá 512 × 512 bloků, takže i při dohledu přes několik set bloků
    /// stačí mít otevřenou hrst — zbytek se otevře znovu, až bude potřeba. Zavírá se přes
    /// <c>Flush</c>, takže se nic neztratí.</para>
    /// </remarks>
    private void CloseColdRegions()
    {
        const int Keep = 12;

        if (_regions.Count <= Keep)
        {
            return;
        }

        foreach (Vector2i cold in _lastTouched
            .OrderBy(pair => pair.Value)
            .Take(_regions.Count - Keep)
            .Select(pair => pair.Key)
            .ToList())
        {
            if (!_regions.TryRemove(cold, out Lazy<RegionFile>? region))
            {
                continue;
            }

            _lastTouched.TryRemove(cold, out _);

            if (region.IsValueCreated)
            {
                region.Value.Dispose();
            }
        }
    }
}
