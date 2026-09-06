using System.Diagnostics;
using Tesseris.Game.Colony;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Pás jako transport line.
/// </summary>
/// <remarks>
/// Zkouší se hlavně to, co tu architekturu odlišuje od kontejneru: posun celého pásu je
/// jedna operace bez ohledu na počet itemů. Kdyby se to rozbilo, projeví se to až při
/// tisících itemů — proto je součástí i měření.
/// </remarks>
public sealed class BeltTests(ITestOutputHelper output)
{
    private const ushort Ore = 1;
    private const ushort Coal = 2;

    private static BeltSegment Belt(int cells = 8) => new(cells);

    /// <summary>Posune pás, dokud první item nedojede na výstup.</summary>
    private static int RunUntilReady(BeltSegment belt, int limit = 100_000)
    {
        int ticks = 0;
        while (!belt.FrontIsReady && ticks < limit)
        {
            belt.Tick();
            ticks++;
        }

        return ticks;
    }

    [Fact]
    public void New_belt_is_empty()
    {
        BeltSegment belt = Belt();

        Assert.Equal(0, belt.Count);
        Assert.False(belt.FrontIsReady);
        Assert.False(belt.TryPop(out _));
    }

    [Fact]
    public void Item_travels_the_whole_length_before_it_can_be_taken()
    {
        BeltSegment belt = Belt(cells: 4);
        Assert.True(belt.TryPush(Ore));

        int ticks = RunUntilReady(belt);

        // Čtyři buňky po šestnácti podkrocích.
        Assert.Equal(4 * BeltSegment.StepsPerCell, ticks);
        Assert.True(belt.TryPop(out ushort item));
        Assert.Equal(Ore, item);
        Assert.Equal(0, belt.Count);
    }

    [Fact]
    public void Belt_stops_when_the_front_item_has_nowhere_to_go()
    {
        BeltSegment belt = Belt(cells: 2);
        belt.TryPush(Ore);
        RunUntilReady(belt);

        // Nikdo si ho nebere: další tiky s pásem nehnou.
        for (int i = 0; i < 50; i++)
        {
            belt.Tick();
        }

        Assert.True(belt.FrontIsReady);
        Assert.Equal(1, belt.Count);
    }

    [Fact]
    public void Items_keep_their_order()
    {
        BeltSegment belt = Belt(cells: 16);

        belt.TryPush(Ore);
        for (int i = 0; i < BeltSegment.MinimumSpacing; i++)
        {
            belt.Tick();
        }

        belt.TryPush(Coal);

        RunUntilReady(belt);
        Assert.True(belt.TryPop(out ushort first));
        RunUntilReady(belt);
        Assert.True(belt.TryPop(out ushort second));

        Assert.Equal(Ore, first);
        Assert.Equal(Coal, second);
    }

    [Fact]
    public void Items_never_get_closer_than_the_minimum_spacing()
    {
        BeltSegment belt = Belt(cells: 32);

        // Cpát co to dá a mezitím tikat.
        for (int i = 0; i < 5_000; i++)
        {
            belt.TryPush(Ore);
            belt.Tick();
        }

        Span<int> positions = new int[belt.Count];
        int written = belt.WritePositions(positions);

        for (int i = 1; i < written; i++)
        {
            int spacing = positions[i] - positions[i - 1];
            Assert.True(
                spacing >= BeltSegment.MinimumSpacing,
                $"Itemy {i - 1} a {i} jsou {spacing} podkroků od sebe.");
        }
    }

    [Fact]
    public void Full_belt_refuses_more_items()
    {
        BeltSegment belt = Belt(cells: 2);

        int pushed = 0;
        while (belt.TryPush(Ore))
        {
            pushed++;
            if (pushed > 1_000)
            {
                Assert.Fail("Pás bere itemy donekonečna — mezery se nepočítají.");
            }
        }

        Assert.True(pushed > 0);
        Assert.False(belt.CanPush);
    }

    [Fact]
    public void Popping_from_the_front_lets_the_queue_move_up()
    {
        BeltSegment belt = Belt(cells: 8);

        for (int i = 0; i < 3; i++)
        {
            belt.TryPush(Ore);
            for (int t = 0; t < BeltSegment.MinimumSpacing; t++)
            {
                belt.Tick();
            }
        }

        RunUntilReady(belt);
        Assert.True(belt.TryPop(out _));

        // Druhý dojede přesně za minimální rozestup, ne od začátku pásu.
        int ticks = RunUntilReady(belt);
        Assert.Equal(BeltSegment.MinimumSpacing, ticks);
    }

    [Fact]
    public void Positions_are_monotonic_from_the_output_end()
    {
        BeltSegment belt = Belt(cells: 16);
        for (int i = 0; i < 20; i++)
        {
            belt.TryPush(Ore);
            for (int t = 0; t < 6; t++)
            {
                belt.Tick();
            }
        }

        Span<int> positions = new int[belt.Count];
        int written = belt.WritePositions(positions);

        Assert.True(written > 1);
        for (int i = 1; i < written; i++)
        {
            Assert.True(positions[i] > positions[i - 1], "Polohy musí růst směrem od výstupu.");
        }

        Assert.True(positions[written - 1] <= belt.Length, "Item nesmí být za koncem pásu.");
    }

    [Fact]
    public void Ring_buffer_survives_many_cycles()
    {
        BeltSegment belt = Belt(cells: 4);
        int delivered = 0;

        for (int i = 0; i < 20_000; i++)
        {
            belt.TryPush(Ore);
            belt.Tick();

            if (belt.TryPop(out _))
            {
                delivered++;
            }
        }

        Assert.True(delivered > 100, $"Za 20 000 tiků dorazilo jen {delivered} itemů.");
        Assert.True(belt.Count >= 0);
    }

    /// <summary>
    /// TO NEJDŮLEŽITĚJŠÍ: plný pás musí stát za tick stejně jako skoro prázdný.
    /// Kdyby cena rostla s počtem itemů, je celá architektura špatně.
    /// </summary>
    [Fact]
    public void Tick_cost_does_not_grow_with_the_number_of_items()
    {
        static double MeasureTicks(int items, int ticks)
        {
            var belt = new BeltSegment(cells: 4096, capacity: 16_384);
            for (int i = 0; i < items; i++)
            {
                if (!belt.TryPush(Ore))
                {
                    break;
                }

                for (int t = 0; t < BeltSegment.MinimumSpacing; t++)
                {
                    belt.Tick();
                }
            }

            // Zahřát.
            for (int i = 0; i < 1000; i++)
            {
                belt.Tick();
            }

            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++)
            {
                belt.Tick();
            }

            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds / ticks * 1_000_000.0;
        }

        const int Ticks = 200_000;
        double few = MeasureTicks(3, Ticks);
        double many = MeasureTicks(10_000, Ticks);

        output.WriteLine($"Pás o 3 itemech      : {few:F2} ns/tick");
        output.WriteLine($"Pás o 10 000 itemech : {many:F2} ns/tick");
        output.WriteLine($"Poměr                : {many / Math.Max(few, 0.0001):F2}×");

        // Volná hranice: měří se nanosekundy na zašuměném stroji. Jde o to odhalit
        // lineární růst, ne hlídat procenta — kontejner by dal řádový rozdíl.
        Assert.True(
            many < Math.Max(few, 1.0) * 5.0,
            $"Cena tiku roste s počtem itemů: {few:F2} ns proti {many:F2} ns.");
    }

    /// <summary>
    /// POVINNÝ BENCHMARK M0: 20 000 itemů v pohybu, simulace pásů pod 2 ms na tick.
    /// </summary>
    [Fact]
    public void Twenty_thousand_items_are_measured_against_the_tick_budget()
    {
        const int Items = 20_000;
        const int BeltCount = 40;

        var belts = new BeltSegment[BeltCount];
        for (int i = 0; i < BeltCount; i++)
        {
            belts[i] = new BeltSegment(cells: 256, capacity: 2048);
        }

        int placed = 0;
        while (placed < Items)
        {
            bool any = false;
            foreach (BeltSegment belt in belts)
            {
                if (placed >= Items)
                {
                    break;
                }

                if (belt.TryPush(Ore))
                {
                    placed++;
                    any = true;
                }
            }

            // MEZI VKLÁDÁNÍMI SE MUSÍ TIKAT ALESPOŇ O ROZESTUP. Po jednom tiku je mezera
            // jedna a minimum jsou čtyři, takže by se druhý item na pás nedostal a plnění
            // by skončilo na čtyřiceti kusech.
            for (int t = 0; t < BeltSegment.MinimumSpacing; t++)
            {
                foreach (BeltSegment belt in belts)
                {
                    belt.Tick();
                }
            }

            if (!any && placed < Items)
            {
                break;
            }
        }

        var timings = new List<double>();
        for (int run = 0; run < 3; run++)
        {
            const int Ticks = 1000;
            var stopwatch = Stopwatch.StartNew();
            for (int tick = 0; tick < Ticks; tick++)
            {
                foreach (BeltSegment belt in belts)
                {
                    belt.Tick();
                }
            }

            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds / Ticks);
        }

        timings.Sort();
        double median = timings[1];

        output.WriteLine($"=== {placed} itemů na {BeltCount} pásech ===");
        output.WriteLine($"Simulace pásů: medián {median * 1000:F2} µs na tick "
            + $"({string.Join(" / ", timings.Select(t => $"{t * 1000:F2}"))} µs)");
        output.WriteLine($"Rozpočet 2 ms na tick je využitý z {median / 2.0 * 100:F3} %.");

        Assert.True(placed >= Items, $"Podařilo se umístit jen {placed} z {Items} itemů.");
        Assert.True(median < 2.0, $"Simulace pásů trvá {median:F3} ms proti rozpočtu 2 ms.");
    }
}
