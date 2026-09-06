using System.Diagnostics;

namespace Tesseris.Engine.Core;

/// <summary>Souhrn frame timů za měřené okno. Všechny hodnoty jsou v milisekundách.</summary>
public readonly record struct FrameStats(
    int SampleCount,
    double MinMs,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    /// <summary>Kolik framů překročilo zadaný rozpočet. Doplňuje percentily o absolutní počet.</summary>
    public int OverBudget { get; init; }
}

/// <summary>
/// Měří délku framu a drží kruhový buffer historie pro výpočet percentilů.
///
/// Rozlišuje dvě různé věci, protože akceptační kritérium mluví o hlavním vlákně:
///   * <b>total</b> — čas mezi začátky dvou framů. Při zapnutém vsyncu je kvantovaný na
///     obnovovací frekvenci, takže neměří výkon enginu, ale monitor.
///   * <b>cpu</b> — čas od začátku framu do konce práce hlavního vlákna, tedy před SwapBuffers.
///     Tohle je skutečná cena enginu a jediné číslo, které dává smysl porovnávat s 16,67 ms.
///
/// Průměrné FPS je pro posouzení plynulosti k ničemu — jeden 200ms hitch se v průměru přes
/// sekundu ztratí. Proto se drží percentily a maximum.
/// </summary>
public sealed class FrameTimer
{
    private const double TicksToMs = 1000.0;

    private readonly double[] _totalMs;
    private readonly double[] _cpuMs;

    // Pracovní pole pro řazení. Existuje proto, aby TotalStats/CpuStats nealokovaly:
    // overlay je volá každý frame a při kapacitě 1024 by to bylo 2× 8 kB na frame,
    // tedy zhruba megabajt za sekundu GC tlaku jen kvůli vypsání čísla na obrazovku.
    private readonly double[] _sortScratch;

    private readonly int _capacity;

    // Platné vzorky jsou vždy prvních _sampleCount položek pole: dokud se buffer nezaplní,
    // _writeIndex roste od nuly; jakmile se zaplní, _sampleCount == _capacity a platné je celé pole.
    // Proto stačí AsSpan(0, _sampleCount) a není potřeba řešit rotaci.
    private int _writeIndex;
    private int _sampleCount;

    private long _frameStartTicks;
    private long _previousFrameStartTicks;
    private bool _hasPreviousFrame;
    private double _pendingCpuMs;

    private double _smoothedTotalMs;

    public FrameTimer(int historyCapacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(historyCapacity, 1);

        _capacity = historyCapacity;
        _totalMs = new double[historyCapacity];
        _cpuMs = new double[historyCapacity];
        _sortScratch = new double[historyCapacity];
    }

    /// <summary>Délka posledního dokončeného framu v sekundách. Používá se jako dt pro pohyb.</summary>
    public double DeltaSeconds { get; private set; }

    /// <summary>Celková délka posledního framu včetně čekání na vsync.</summary>
    public double LastTotalMs { get; private set; }

    /// <summary>Čas práce hlavního vlákna v posledním framu, bez čekání na vsync.</summary>
    public double LastCpuMs { get; private set; }

    /// <summary>Počet framů od startu.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Vyhlazené FPS pro zobrazení. Bez vyhlazení číslo poskakuje tak, že se nedá přečíst.</summary>
    public double SmoothedFps => _smoothedTotalMs > 0.0 ? 1000.0 / _smoothedTotalMs : 0.0;

    /// <summary>Volá se jako první věc ve framu.</summary>
    public void BeginFrame()
    {
        long now = Stopwatch.GetTimestamp();

        if (_hasPreviousFrame)
        {
            LastTotalMs = TicksToMilliseconds(now - _previousFrameStartTicks);
            LastCpuMs = _pendingCpuMs;
            DeltaSeconds = LastTotalMs / 1000.0;

            _totalMs[_writeIndex] = LastTotalMs;
            _cpuMs[_writeIndex] = _pendingCpuMs;
            _writeIndex = (_writeIndex + 1) % _capacity;
            if (_sampleCount < _capacity)
            {
                _sampleCount++;
            }

            // Exponenciální průměr na frame timu, ne na FPS: průměrovat převrácenou hodnotu
            // dává systematicky nadhodnocené číslo.
            //
            // VÁHA SE ODVOZUJE Z ČASU, ne z počtu snímků. Pevný koeficient 0,9/0,1 odpovídá
            // zhruba deseti snímkům — jenže při osmi stech snímcích za vteřinu je to dvanáct
            // milisekund, takže se číslo měnilo osmdesátkrát za vteřinu a nedalo se přečíst.
            // S časovou konstantou drží údaj klidný bez ohledu na to, jak rychle scéna běží.
            //
            // Půl vteřiny je kompromis: kratší poskakuje, delší nestíhá ukázat, že se scéna
            // opravdu zpomalila. Krátké záseky se tím vyhladí schválně — od nich je log.
            const double SmoothingTauMs = 500.0;

            double weight = 1.0 - Math.Exp(-LastTotalMs / SmoothingTauMs);

            _smoothedTotalMs = _smoothedTotalMs <= 0.0
                ? LastTotalMs
                : _smoothedTotalMs + ((LastTotalMs - _smoothedTotalMs) * weight);

            FrameCount++;
        }

        _previousFrameStartTicks = now;
        _frameStartTicks = now;
        _hasPreviousFrame = true;
    }

    /// <summary>
    /// Volá se po veškeré práci hlavního vlákna, těsně před SwapBuffers.
    /// Hodnota se zapíše do historie až při dalším <see cref="BeginFrame"/>, kdy je znám i celkový čas.
    /// </summary>
    public void EndCpuWork()
    {
        _pendingCpuMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - _frameStartTicks);
    }

    /// <summary>
    /// Statistika celkových časů framů. Tohle je metrika pro akceptační kritérium — ale jen
    /// při vypnutém vsyncu, jinak měří obnovovací frekvenci monitoru, ne engine.
    /// </summary>
    public FrameStats TotalStats(double budgetMs = 16.67) =>
        Summarize(_totalMs.AsSpan(0, _sampleCount), _sortScratch, budgetMs);

    /// <summary>
    /// Statistika času hlavního vlákna. Doplňková diagnostika: říká, jestli je scéna
    /// CPU-bound, nebo GPU-bound. Sama o sobě NENÍ důkaz snímkové frekvence, protože
    /// neobsahuje práci GPU ani present.
    /// </summary>
    public FrameStats CpuStats(double budgetMs = 16.67) =>
        Summarize(_cpuMs.AsSpan(0, _sampleCount), _sortScratch, budgetMs);

    /// <summary>
    /// Spočítá souhrn z libovolné sady vzorků. Statická metoda nad spanem schválně:
    /// percentilová matematika je tak testovatelná bez měření reálného času a bez zavádění
    /// časového rozhraní jen kvůli testům.
    /// </summary>
    public static FrameStats SummarizeSamples(ReadOnlySpan<double> samples, double budgetMs = 16.67) =>
        Summarize(samples, new double[samples.Length], budgetMs);

    /// <param name="scratch">
    /// Pracovní pole aspoň tak dlouhé jako vzorky. Volání z render smyčky tak nealokuje —
    /// <see cref="PhaseProfiler"/> je takhle volá každý frame.
    /// </param>
    public static FrameStats SummarizeSamples(ReadOnlySpan<double> samples, double[] scratch, double budgetMs = 16.67) =>
        Summarize(samples, scratch, budgetMs);

    /// <param name="scratch">
    /// Pracovní pole pro řazení, aspoň tak dlouhé jako <paramref name="samples"/>.
    /// Předává se zvenčí, aby volání z render smyčky nealokovalo.
    /// </param>
    private static FrameStats Summarize(ReadOnlySpan<double> samples, double[] scratch, double budgetMs)
    {
        int count = samples.Length;
        if (count == 0)
        {
            return new FrameStats(0, 0, 0, 0, 0, 0, 0);
        }

        Span<double> sorted = scratch.AsSpan(0, count);
        double sum = 0.0;
        int overBudget = 0;

        for (int i = 0; i < count; i++)
        {
            double value = samples[i];
            sorted[i] = value;
            sum += value;
            if (value > budgetMs)
            {
                overBudget++;
            }
        }

        sorted.Sort();

        return new FrameStats(
            SampleCount: count,
            MinMs: sorted[0],
            MeanMs: sum / count,
            P50Ms: Percentile(sorted, 0.50),
            P95Ms: Percentile(sorted, 0.95),
            P99Ms: Percentile(sorted, 0.99),
            MaxMs: sorted[count - 1])
        {
            OverBudget = overBudget,
        };
    }

    /// <summary>Percentil metodou nearest-rank. Pro pár set vzorků je interpolace zbytečná.</summary>
    private static double Percentile(ReadOnlySpan<double> sorted, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static double TicksToMilliseconds(long ticks) =>
        ticks * TicksToMs / Stopwatch.Frequency;
}
