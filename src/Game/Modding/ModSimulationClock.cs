namespace Tesseris.Game.Modding;

/// <summary>
/// Fixed-step game simulation clock used by mod behaviors, ECS and scheduled work. Rendering may run at any
/// rate; simulation callbacks always observe the same delta and strictly increasing tick number.
/// </summary>
public sealed class ModSimulationClock
{
    private readonly TimeSpan step;
    private readonly int maximumCatchUpTicks;
    private TimeSpan accumulator;

    public ModSimulationClock(int ticksPerSecond = 20, int maximumCatchUpTicks = 8)
    {
        if (ticksPerSecond is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        }

        if (maximumCatchUpTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCatchUpTicks));
        }

        TicksPerSecond = ticksPerSecond;
        this.maximumCatchUpTicks = maximumCatchUpTicks;
        step = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / ticksPerSecond);
    }

    public int TicksPerSecond { get; }

    public ulong Tick { get; private set; }

    public TimeSpan Step => step;

    public double InterpolationAlpha => Math.Clamp(accumulator.TotalSeconds / step.TotalSeconds, 0d, 1d);

    /// <summary>
    /// Advances simulation and returns callbacks executed. Excess stalls are intentionally discarded after
    /// the configured catch-up limit so one slow frame cannot create an unbounded spiral.
    /// </summary>
    public int Advance(TimeSpan elapsed, Action<ulong, TimeSpan> simulate)
    {
        ArgumentNullException.ThrowIfNull(simulate);
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        TimeSpan maximumAccumulation = TimeSpan.FromTicks(checked(step.Ticks * maximumCatchUpTicks));
        accumulator += elapsed;
        if (accumulator > maximumAccumulation)
        {
            accumulator = maximumAccumulation;
        }

        int executed = 0;
        while (executed < maximumCatchUpTicks && accumulator >= step)
        {
            accumulator -= step;
            Tick = checked(Tick + 1);
            simulate(Tick, step);
            executed++;
        }

        return executed;
    }

    public void Reset(ulong tick = 0)
    {
        Tick = tick;
        accumulator = TimeSpan.Zero;
    }
}
