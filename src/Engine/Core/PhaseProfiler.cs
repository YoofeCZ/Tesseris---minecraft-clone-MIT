using System.Diagnostics;

namespace Tesseris.Engine.Core;

/// <summary>
/// Měří, kolik času ve framu spolkne která fáze hlavního vlákna.
///
/// <para><b>Proč to vzniklo.</b> Do té doby se u fází hlásilo jen maximum přes celý běh.
/// Podle maxima se ale ladit nedá: jedna špička nic neříká o tom, kde se ztrácí čas
/// v <b>typickém</b> framu, a přesně to je otázka, když snímková frekvence stojí. Naměřeno,
/// že hlavní vlákno zabírá 10,97 z 11,14 ms framu — scéna je tedy CPU-bound a jde jen o to,
/// která fáze si to bere.</para>
///
/// <para>Fáze jsou pevná sada zadaná v konstruktoru, aby se za běhu nic nealokovalo ani
/// nehledalo v mapě. Historie je kruhový buffer, stejně jako u <see cref="FrameTimer"/>.</para>
/// </summary>
public sealed class PhaseProfiler
{
    private readonly string[] _names;
    private readonly double[][] _history;
    private readonly double[] _current;
    private readonly long[] _started;
    private readonly double[] _scratch;
    private readonly int _capacity;

    private int _writeIndex;
    private int _sampleCount;

    public PhaseProfiler(int historyCapacity, params string[] names)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(historyCapacity, 1);
        ArgumentNullException.ThrowIfNull(names);

        if (names.Length == 0)
        {
            throw new ArgumentException("Aspoň jedna fáze.", nameof(names));
        }

        _capacity = historyCapacity;
        _names = names;
        _current = new double[names.Length];
        _started = new long[names.Length];
        _scratch = new double[historyCapacity];
        _history = new double[names.Length][];

        for (int i = 0; i < names.Length; i++)
        {
            _history[i] = new double[historyCapacity];
        }
    }

    public int PhaseCount => _names.Length;

    public string Name(int phase) => _names[phase];

    /// <summary>Zahodí rozměřené hodnoty. Volá se na začátku framu.</summary>
    public void BeginFrame() => Array.Clear(_current);

    public void Begin(int phase) => _started[phase] = Stopwatch.GetTimestamp();

    /// <summary>
    /// Uzavře fázi. Hodnoty se v rámci framu <b>sčítají</b>, takže se fáze smí otevřít
    /// vícekrát — třeba když je práce rozdělená mezi update a render.
    /// </summary>
    public void End(int phase) =>
        _current[phase] += (Stopwatch.GetTimestamp() - _started[phase]) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Přičte už změřený úsek. Pro fáze měřené jinde (třeba uvnitř streameru).</summary>
    public void Add(int phase, double milliseconds) => _current[phase] += milliseconds;

    /// <summary>Zapíše frame do historie. Volá se po veškeré práci hlavního vlákna.</summary>
    public void EndFrame()
    {
        for (int i = 0; i < _names.Length; i++)
        {
            _history[i][_writeIndex] = _current[i];
        }

        _writeIndex = (_writeIndex + 1) % _capacity;
        if (_sampleCount < _capacity)
        {
            _sampleCount++;
        }
    }

    public FrameStats Stats(int phase) =>
        FrameTimer.SummarizeSamples(_history[phase].AsSpan(0, _sampleCount), _scratch);

    /// <summary>Kolik milisekund fáze zabrala v posledním framu. Pro overlay.</summary>
    public double Last(int phase) => _current[phase];
}
