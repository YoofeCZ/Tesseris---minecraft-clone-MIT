using OpenTK.Mathematics;
using Tesseris.Game.Colony;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Vkládače (zbytek T4). Bez nich se z jednotlivých strojů nedá udělat linka.
/// </summary>
public sealed class InserterTests(ITestOutputHelper output)
{
    private const ushort Ore = 1;
    private const ushort Dust = 2;

    private static void Run(BeltSegment[] belts, MachineBank machines, InserterBank inserters, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            foreach (BeltSegment belt in belts)
            {
                belt.Tick();
            }

            inserters.Tick(machines);
            machines.Tick();
        }
    }

    [Fact]
    public void Inserter_moves_an_item_from_a_belt_into_a_machine()
    {
        var machines = new MachineBank();
        int crusher = machines.Add(new Vector3i(5, 2, 5), Ore, Dust, ticksPerCraft: 10);

        var belt = new BeltSegment(cells: 2);
        var inserters = new InserterBank();
        inserters.AddBeltToMachine(new Vector3i(4, 2, 5), belt, crusher);

        Assert.True(belt.TryPush(Ore));

        Run([belt], machines, inserters, 200);

        Assert.Equal(1, inserters.MovedTotal);
        Assert.Equal(0, belt.Count);

        // Ruda se do stroje dostala a ten ji ručně nezpracoval, protože u něj nikdo nestojí.
        Assert.Equal(1, machines.InputOf(crusher));
    }

    [Fact]
    public void Inserter_takes_the_product_out_of_a_machine_onto_a_belt()
    {
        var machines = new MachineBank();
        int crusher = machines.Add(new Vector3i(5, 2, 5), Ore, Dust, ticksPerCraft: 5);
        machines.TryInsert(crusher, Ore);
        machines.TryAssignOperator(crusher, colonist: 0);

        var belt = new BeltSegment(cells: 4);
        var inserters = new InserterBank();
        inserters.AddMachineToBelt(machines, new Vector3i(6, 2, 5), crusher, belt);

        Run([belt], machines, inserters, 200);

        Assert.Equal(1, machines.CraftedTotal);
        Assert.Equal(1, inserters.MovedTotal);
        Assert.Equal(1, belt.Count);
        Assert.Equal(0, machines.OutputOf(crusher));
    }

    /// <summary>
    /// Ruční stroj obsloužený vkládači. Tohle je ta cesta, kterou stroj sám neumí — bere
    /// z pásu do stroje, u kterého pořád musí stát člověk.
    /// </summary>
    [Fact]
    public void Inserters_can_feed_a_manual_machine()
    {
        var machines = new MachineBank();
        int crusher = machines.Add(new Vector3i(5, 2, 5), Ore, Dust, ticksPerCraft: 5);
        machines.TryAssignOperator(crusher, colonist: 0);

        var input = new BeltSegment(cells: 2);
        var output = new BeltSegment(cells: 2);
        var inserters = new InserterBank();
        inserters.AddBeltToMachine(new Vector3i(4, 2, 5), input, crusher);
        inserters.AddMachineToBelt(machines, new Vector3i(6, 2, 5), crusher, output);

        input.TryPush(Ore);

        Run([input, output], machines, inserters, 400);

        // Stroj je pořád ruční — a přesto linka běží, protože vkládače dělají dopravu.
        Assert.Equal(MachineMode.Manual, machines.ModeOf(crusher));
        Assert.Equal(1, machines.CraftedTotal);
        Assert.Equal(1, output.Count);
    }

    [Fact]
    public void Inserter_moves_between_two_belts()
    {
        var machines = new MachineBank();
        var first = new BeltSegment(cells: 2);
        var second = new BeltSegment(cells: 2);

        var inserters = new InserterBank();
        inserters.AddBeltToBelt(new Vector3i(3, 2, 3), first, second);

        first.TryPush(Ore);

        Run([first, second], machines, inserters, 200);

        Assert.Equal(1, inserters.MovedTotal);
        Assert.Equal(0, first.Count);
        Assert.Equal(1, second.Count);
    }

    /// <summary>
    /// Když je cíl plný, item se MUSÍ vrátit. Mizející itemy jsou nejhorší druh chyby:
    /// nikde se to nehlásí a projeví se to až chybějící produkcí.
    /// </summary>
    [Fact]
    public void Item_is_returned_when_the_target_is_full()
    {
        var machines = new MachineBank();
        int crusher = machines.Add(new Vector3i(5, 2, 5), Ore, Dust, ticksPerCraft: 100_000);

        // Naplnit vstup až po strop.
        for (int i = 0; i < MachineBank.InputCapacity; i++)
        {
            Assert.True(machines.TryInsert(crusher, Ore));
        }

        var belt = new BeltSegment(cells: 4);
        var inserters = new InserterBank();
        inserters.AddBeltToMachine(new Vector3i(4, 2, 5), belt, crusher);

        belt.TryPush(Ore);
        int before = belt.Count + machines.InputOf(crusher);

        Run([belt], machines, inserters, 300);

        int after = belt.Count + machines.InputOf(crusher);

        Assert.Equal(before, after);
        Assert.Equal(0, inserters.MovedTotal);
    }

    [Fact]
    public void Idle_inserter_sleeps()
    {
        var machines = new MachineBank();
        int crusher = machines.Add(new Vector3i(5, 2, 5), Ore, Dust, ticksPerCraft: 5);
        var belt = new BeltSegment(cells: 2);

        var inserters = new InserterBank();
        inserters.AddBeltToMachine(new Vector3i(4, 2, 5), belt, crusher);

        Run([belt], machines, inserters, 50);

        Assert.Equal(0, inserters.ActiveCount);

        // Probuzení a hned je zase v provozu.
        inserters.WakeAll();
        Assert.Equal(1, inserters.ActiveCount);
    }

    /// <summary>
    /// Celá linka: pás → vkládač → drtič → vkládač → pás. Přesně to, co M0 chce po T4 a T5.
    /// </summary>
    [Fact]
    public void A_whole_line_runs_ore_through_the_crusher()
    {
        var machines = new MachineBank();
        int crusher = machines.Add(new Vector3i(8, 2, 8), Ore, Dust, ticksPerCraft: 6);

        var input = new BeltSegment(cells: 4);
        var dustBelt = new BeltSegment(cells: 4);

        // Automatická: má oba pásy i proud, takže u ní nikdo stát nemusí.
        machines.SetInputBelt(crusher, input);
        machines.SetOutputBelt(crusher, dustBelt);
        machines.SetPowered(crusher, true);

        var inserters = new InserterBank();
        inserters.AddMachineToBelt(machines, new Vector3i(9, 2, 8), crusher, dustBelt);

        const int Ores = 5;
        int pushed = 0;
        for (int tick = 0; tick < 4_000; tick++)
        {
            if (pushed < Ores && input.TryPush(Ore))
            {
                pushed++;
                machines.NotifyPushed(input);
            }

            input.Tick();
            dustBelt.Tick();
            inserters.Tick(machines);
            machines.Tick();

            if (dustBelt.FrontIsReady)
            {
                dustBelt.TryPop(out _);
            }
        }

        output.WriteLine($"Rudy na vstupu {pushed}, zdrceno {machines.CraftedTotal}, "
            + $"vkladace prendaly {inserters.MovedTotal}.");

        Assert.Equal(Ores, pushed);
        Assert.True(machines.CraftedTotal >= Ores, $"Zdrceno jen {machines.CraftedTotal} z {Ores}.");
    }
}
