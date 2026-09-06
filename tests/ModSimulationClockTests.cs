using Tesseris.Game.Modding;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModSimulationClockTests
{
    [Fact]
    public void Variable_frames_produce_fixed_monotonic_ticks()
    {
        var clock = new ModSimulationClock(20);
        var calls = new List<(ulong Tick, TimeSpan Delta)>();

        clock.Advance(TimeSpan.FromMilliseconds(20), (tick, delta) => calls.Add((tick, delta)));
        clock.Advance(TimeSpan.FromMilliseconds(40), (tick, delta) => calls.Add((tick, delta)));
        clock.Advance(TimeSpan.FromMilliseconds(100), (tick, delta) => calls.Add((tick, delta)));

        Assert.Equal([1UL, 2UL, 3UL], calls.Select(call => call.Tick));
        Assert.All(calls, call => Assert.Equal(TimeSpan.FromMilliseconds(50), call.Delta));
    }

    [Fact]
    public void Catch_up_is_bounded_and_drops_excess_stall()
    {
        var clock = new ModSimulationClock(20, maximumCatchUpTicks: 3);
        int calls = 0;

        int executed = clock.Advance(TimeSpan.FromSeconds(10), (_, _) => calls++);

        Assert.Equal(3, executed);
        Assert.Equal(3, calls);
        Assert.Equal(3UL, clock.Tick);
        Assert.Equal(0d, clock.InterpolationAlpha);
    }

    [Fact]
    public void Reset_can_restore_persisted_tick_and_clears_partial_time()
    {
        var clock = new ModSimulationClock(20);
        clock.Advance(TimeSpan.FromMilliseconds(25), (_, _) => { });

        clock.Reset(700);
        int calls = clock.Advance(TimeSpan.FromMilliseconds(25), (_, _) => { });

        Assert.Equal(0, calls);
        Assert.Equal(700UL, clock.Tick);
        Assert.Equal(0.5d, clock.InterpolationAlpha, precision: 5);
    }
}
