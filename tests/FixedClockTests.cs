using Tesseris.Engine.Core;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Pevné simulační hodiny (T1). Chování těchhle hodin rozhoduje o determinismu celé hry,
/// takže se tady testuje i to, co vypadá samozřejmě.
/// </summary>
public sealed class FixedClockTests
{
    private static int Drain(FixedClock clock)
    {
        int ticks = 0;
        while (clock.TryConsumeTick())
        {
            ticks++;
        }

        return ticks;
    }

    [Fact]
    public void Step_is_one_sixtieth_of_a_second_by_default()
    {
        var clock = new FixedClock();

        Assert.Equal(60, clock.TicksPerSecond);
        Assert.Equal(1.0 / 60.0, clock.StepSeconds, 5);
    }

    [Fact]
    public void Exactly_one_second_of_elapsed_time_yields_sixty_ticks()
    {
        var clock = new FixedClock();
        int total = 0;

        // Po dávkách, protože strop na snímek je osm kroků.
        for (int i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1.0 / 60.0));
            total += Drain(clock);
        }

        Assert.Equal(60, total);
        Assert.Equal(60ul, clock.Tick);
    }

    /// <summary>
    /// Tohle je jádro determinismu. Tři různé snímkové frekvence musí za tentýž kus reálného
    /// času odsimulovat tentýž počet kroků — jinak se hra chová jinak na jiném stroji.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void Tick_count_does_not_depend_on_frame_rate(int framesPerSecond)
    {
        var clock = new FixedClock();
        int total = 0;

        // Čas se dodává jako ROZDÍL DVOU KUMULATIVNÍCH ZNAČEK, ne jako 1/fps opakovaně.
        // Tak se chová skutečný časovač a jen tak dá součet přesně jednu vteřinu; opakované
        // TimeSpan.FromSeconds(1.0/144.0) se zaokrouhlí dolů a za 144 snímků chybí 64 tiků,
        // takže by test měřil zaokrouhlení svého vstupu, ne chování hodin.
        TimeSpan previous = TimeSpan.Zero;
        for (int i = 1; i <= framesPerSecond; i++)
        {
            TimeSpan now = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * i / framesPerSecond);
            clock.Advance(now - previous);
            previous = now;
            total += Drain(clock);
        }

        // Za jednu vteřinu vyjde 60 kroků bez ohledu na to, v kolika dávkách čas přišel.
        Assert.Equal(60, total);
    }

    /// <summary>
    /// Zbytek pod jeden krok se nesmí zahodit. Přesně tohle dělala FluidSimulation
    /// (`_accumulated = 0.0`) a dělalo to tempo vody závislé na FPS.
    /// </summary>
    [Fact]
    public void Sub_tick_remainder_is_carried_over_not_discarded()
    {
        var clock = new FixedClock();
        TimeSpan twoThirdsOfAStep = TimeSpan.FromSeconds(1.0 / 90.0);

        // Dvě třetiny kroku samy o sobě tik nedají.
        clock.Advance(twoThirdsOfAStep);
        Assert.Equal(0, Drain(clock));

        // Ale dvě takové dávky přesáhnou jeden krok — pokud se zbytek nezahodil.
        clock.Advance(twoThirdsOfAStep);
        Assert.Equal(1, Drain(clock));
    }

    [Fact]
    public void Remainder_accumulates_across_many_uneven_frames()
    {
        var clock = new FixedClock();
        int total = 0;

        // 100 snímků po 10 ms = 1 s reálného času = 60 kroků, přestože 10 ms není násobek kroku.
        for (int i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            total += Drain(clock);
        }

        Assert.Equal(60, total);
    }

    [Fact]
    public void One_frame_never_runs_more_than_the_catch_up_limit()
    {
        var clock = new FixedClock(maximumTicksPerFrame: 8);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(8, Drain(clock));
    }

    /// <summary>
    /// Když stroj dlouhodobě nestíhá, hra zpomalí — ale nesmí se zacyklit v dohánění.
    /// Po ořezané frontě musí další snímek zase jet normálně, ne dohánět pět vteřin.
    /// </summary>
    [Fact]
    public void Backlog_is_capped_so_the_game_slows_down_instead_of_spiralling()
    {
        var clock = new FixedClock(maximumTicksPerFrame: 8);

        clock.Advance(TimeSpan.FromSeconds(5));
        Drain(clock);

        // Následující běžný snímek dá jeden krok, ne dalších osm z nedoběhnuté fronty.
        clock.Advance(TimeSpan.FromSeconds(1.0 / 60.0));
        Assert.Equal(1, Drain(clock));
    }

    [Fact]
    public void Step_length_never_changes_however_long_the_stall_was()
    {
        var clock = new FixedClock();
        float before = clock.StepSeconds;

        clock.Advance(TimeSpan.FromSeconds(3));
        Drain(clock);

        Assert.Equal(before, clock.StepSeconds);
    }

    [Fact]
    public void Tick_numbers_are_strictly_increasing()
    {
        var clock = new FixedClock();
        clock.Advance(TimeSpan.FromSeconds(0.1));

        ulong previous = clock.Tick;
        while (clock.TryConsumeTick())
        {
            Assert.Equal(previous + 1, clock.Tick);
            previous = clock.Tick;
        }
    }

    [Fact]
    public void Interpolation_alpha_reports_position_between_two_ticks()
    {
        var clock = new FixedClock();

        clock.Advance(TimeSpan.FromSeconds(1.0 / 120.0));
        Drain(clock);

        // Půl kroku nasčítáno, žádný tik neproběhl → jsme v polovině mezi tiky.
        Assert.Equal(0.5, clock.InterpolationAlpha, 2);
    }

    [Fact]
    public void Interpolation_alpha_stays_inside_zero_to_one()
    {
        var clock = new FixedClock();

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.InRange(clock.InterpolationAlpha, 0d, 1d);

        Drain(clock);

        Assert.InRange(clock.InterpolationAlpha, 0d, 1d);
    }

    [Fact]
    public void Reset_restores_the_tick_number_for_a_loaded_world()
    {
        var clock = new FixedClock();
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Drain(clock);

        clock.Reset(12_345);

        Assert.Equal(12_345ul, clock.Tick);
        Assert.Equal(0, clock.Pending);
        Assert.Equal(0d, clock.InterpolationAlpha);
    }

    [Fact]
    public void Negative_elapsed_time_is_rejected()
    {
        var clock = new FixedClock();

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Zero_elapsed_time_produces_no_ticks()
    {
        var clock = new FixedClock();

        Assert.Equal(0, clock.Advance(TimeSpan.Zero));
        Assert.Equal(0, Drain(clock));
        Assert.Equal(0ul, clock.Tick);
    }
}
