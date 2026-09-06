namespace Tesseris.Engine.Core;

/// <summary>
/// Pevné simulační hodiny. Render může běžet jakkoli rychle, simulace vidí vždycky stejný krok.
/// </summary>
/// <remarks>
/// <para><b>Proč to je nutné.</b> Do téhle chvíle jela celá hra z délky snímku
/// (<c>UpdateFrequency = 0.0</c> a patnáct volání s proměnným <c>DeltaSeconds</c>). To znamená,
/// že tentýž vstup dá na jiném stroji jiný výsledek — a bez determinismu nejdou savy po tikách,
/// replaye ani pozdější multiplayer.</para>
///
/// <para><b>Zdroj:</b> vzniklo vytažením <c>ModSimulationClock</c> z modding vrstvy, protože ta
/// je jediná v repu, kdo pevný krok uměl, a měla k tomu testy. Oproti ní má tahle verze 60 Hz
/// místo 20, nealokuje delegát na volání a jinak zachází s nedoběhnutým časem (viz níž).</para>
///
/// <para><b>Bez alokace.</b> Předchůdce bral <c>Action&lt;ulong, TimeSpan&gt;</c>, takže každé
/// volání ze smyčky vyrobilo uzávěr. Tady se místo toho spočítá počet kroků a volající si je
/// odebere sám — v tick cestě nesmí vznikat odpad (pravidlo 6.5).</para>
/// </remarks>
public sealed class FixedClock
{
    /// <summary>Kolikrát za vteřinu tiká simulace. Pevně 60, viz pravidlo 6.6.</summary>
    public const int DefaultTicksPerSecond = 60;

    /// <summary>
    /// Kolik kroků se smí dohnat v jednom snímku.
    /// </summary>
    /// <remarks>
    /// Strop musí být — bez něj by jeden dlouhý zásek nechal frontu, kterou by hra doháněla
    /// další zásek, a ten by frontu prodloužil. Osm kroků je 133 ms práce navíc.
    /// </remarks>
    public const int DefaultMaximumTicksPerFrame = 8;

    private readonly TimeSpan _step;
    private readonly int _maximumTicksPerFrame;
    private TimeSpan _accumulator;
    private int _pending;

    public FixedClock(
        int ticksPerSecond = DefaultTicksPerSecond,
        int maximumTicksPerFrame = DefaultMaximumTicksPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ticksPerSecond, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTicksPerFrame, 1);

        TicksPerSecond = ticksPerSecond;
        _maximumTicksPerFrame = maximumTicksPerFrame;

        // Celočíselné dělení: při 60 Hz je krok 166 666 tiků místo 166 666,67, tedy o 4 ppm
        // kratší. Na determinismus to vliv nemá — krok je konstantní — jen simulační čas jde
        // o zlomek promile napřed proti hodinám na zdi.
        _step = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / ticksPerSecond);
    }

    public int TicksPerSecond { get; }

    /// <summary>Číslo posledního odebraného tiku. Roste monotónně a je základ determinismu.</summary>
    public ulong Tick { get; private set; }

    public TimeSpan Step => _step;

    /// <summary>Délka kroku ve vteřinách. Tohle dostávají simulační systémy místo délky snímku.</summary>
    public float StepSeconds => (float)_step.TotalSeconds;

    /// <summary>
    /// Kde mezi posledním a příštím tikem leží právě kreslený snímek, 0 až 1.
    /// </summary>
    /// <remarks>
    /// Bez tohohle čísla nemá render mezi čím interpolovat a pohyb při 60Hz simulaci
    /// a 144Hz obrazovce viditelně cuká.
    /// </remarks>
    public double InterpolationAlpha => Math.Clamp(_accumulator.TotalSeconds / _step.TotalSeconds, 0d, 1d);

    /// <summary>Kolik kroků ještě čeká na odebrání v tomhle snímku.</summary>
    public int Pending => _pending;

    /// <summary>
    /// Přijme uplynulý čas a spočítá, kolik kroků se má odsimulovat. Nic nespouští.
    /// </summary>
    /// <remarks>
    /// <para><b>Nedoběhnutý zbytek se NIKDY nezahazuje.</b> To je rozdíl proti třem místům, která
    /// to v projektu dělala (<c>FluidSimulation</c> nulovala akumulátor, zvířata ořezávala delta
    /// na 0,25 s). Kdo zahodí zbytek, udělá tempo simulace závislé na snímkové frekvenci —
    /// voda pak při 30 FPS teče jinak rychle než při 200.</para>
    ///
    /// <para><b>Zaostalá fronta se naopak ořezat MUSÍ.</b> Když stroj dlouhodobě nestíhá, hra
    /// zpomalí: nasimuluje se míň kroků za vteřinu reálného času. To je přesně chování, které
    /// zadání chce („když simulace nestíhá, hra zpomalí"). Krok se přitom nemění a každý tik je
    /// pořád 1/60 vteřiny, takže determinismus zůstává — jen jich je za vteřinu míň.</para>
    /// </remarks>
    /// <returns>Počet kroků k odsimulování, nejvýš <see cref="DefaultMaximumTicksPerFrame"/>.</returns>
    public int Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        _accumulator += elapsed;

        // Strop fronty. Zbytek pod jeden krok zůstává — ořezává se jen to, co přesahuje
        // dohnatelné množství.
        TimeSpan maximumBacklog = TimeSpan.FromTicks(_step.Ticks * _maximumTicksPerFrame);
        if (_accumulator > maximumBacklog)
        {
            _accumulator = maximumBacklog;
        }

        long whole = _accumulator.Ticks / _step.Ticks;
        _pending = (int)Math.Min(whole, _maximumTicksPerFrame);
        return _pending;
    }

    /// <summary>
    /// Odebere jeden krok. Vrací false, až žádný nezbývá — je určená do <c>while</c> cyklu.
    /// </summary>
    public bool TryConsumeTick()
    {
        if (_pending <= 0)
        {
            return false;
        }

        _pending--;
        _accumulator -= _step;
        Tick = checked(Tick + 1);
        return true;
    }

    /// <summary>Vrátí hodiny na zadaný tik. Volá se při načtení světa.</summary>
    public void Reset(ulong tick = 0)
    {
        Tick = tick;
        _accumulator = TimeSpan.Zero;
        _pending = 0;
    }
}
