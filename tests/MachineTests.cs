using OpenTK.Mathematics;
using Tesseris.Game.Colony;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Drtič — stroj s operátorem (T5).
/// </summary>
/// <remarks>
/// Podle M0 je tohle designově nejdůležitější úkol, protože poprvé propojí kolonisty
/// a stroje do jednoho systému. Kritérium: napojím na drtič pásy a počítadlo volných
/// kolonistů se zvedne z nuly na jedničku.
/// </remarks>
public sealed class MachineTests(ITestOutputHelper output)
{
    private const ushort Ore = 1;
    private const ushort Dust = 2;
    private const int TicksPerCraft = 10;

    private static (MachineBank Bank, int Machine) Crusher()
    {
        var bank = new MachineBank();
        int machine = bank.Add(new Vector3i(4, 2, 4), Ore, Dust, TicksPerCraft);
        return (bank, machine);
    }

    // ================= REŽIM =================

    [Fact]
    public void Machine_without_belts_or_power_is_manual()
    {
        (MachineBank bank, int machine) = Crusher();

        Assert.Equal(MachineMode.Manual, bank.ModeOf(machine));
    }

    [Fact]
    public void Belts_alone_are_not_enough_power_is_needed_too()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.SetInputBelt(machine, new BeltSegment(4));
        bank.SetOutputBelt(machine, new BeltSegment(4));

        Assert.Equal(MachineMode.Manual, bank.ModeOf(machine));

        bank.SetPowered(machine, true);
        Assert.Equal(MachineMode.Automatic, bank.ModeOf(machine));
    }

    [Fact]
    public void Power_alone_is_not_enough_belts_are_needed_too()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.SetPowered(machine, true);

        Assert.Equal(MachineMode.Manual, bank.ModeOf(machine));
    }

    [Fact]
    public void Losing_a_belt_puts_the_machine_back_on_manual()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.SetInputBelt(machine, new BeltSegment(4));
        bank.SetOutputBelt(machine, new BeltSegment(4));
        bank.SetPowered(machine, true);
        Assert.Equal(MachineMode.Automatic, bank.ModeOf(machine));

        bank.SetOutputBelt(machine, null);

        Assert.Equal(MachineMode.Manual, bank.ModeOf(machine));
    }

    // ================= RUČNÍ REŽIM =================

    [Fact]
    public void Manual_machine_does_nothing_without_an_operator()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.TryInsert(machine, Ore);

        for (int tick = 0; tick < 100; tick++)
        {
            bank.Tick();
        }

        Assert.Equal(0, bank.CraftedTotal);
        Assert.Equal(1, bank.InputOf(machine));

        // A hlavně: nespotřebovává čas. Stroj bez operátora spí (pravidlo 6.4).
        Assert.Equal(0, bank.ActiveCount);
    }

    [Fact]
    public void Manual_machine_works_while_someone_stands_at_it()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.TryInsert(machine, Ore);
        Assert.True(bank.TryAssignOperator(machine, colonist: 0));

        for (int tick = 0; tick < TicksPerCraft; tick++)
        {
            bank.Tick();
        }

        Assert.Equal(1, bank.CraftedTotal);
        Assert.Equal(1, bank.OutputOf(machine));
    }

    [Fact]
    public void Only_one_person_can_stand_at_a_machine()
    {
        (MachineBank bank, int machine) = Crusher();

        Assert.True(bank.TryAssignOperator(machine, 0));
        Assert.False(bank.TryAssignOperator(machine, 1));
        Assert.Equal(0, bank.OperatorOf(machine));
    }

    [Fact]
    public void Automatic_machine_refuses_an_operator()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.SetInputBelt(machine, new BeltSegment(4));
        bank.SetOutputBelt(machine, new BeltSegment(4));
        bank.SetPowered(machine, true);

        // Nemá u něj co dělat.
        Assert.False(bank.TryAssignOperator(machine, 0));
    }

    // ================= AUTOMATICKÝ REŽIM =================

    [Fact]
    public void Automatic_machine_takes_from_the_input_belt_and_puts_on_the_output_belt()
    {
        (MachineBank bank, int machine) = Crusher();
        var input = new BeltSegment(cells: 2);
        var output = new BeltSegment(cells: 2);

        bank.SetInputBelt(machine, input);
        bank.SetOutputBelt(machine, output);
        bank.SetPowered(machine, true);

        Assert.True(input.TryPush(Ore));

        // Položení na pás se musí ohlásit — stroj čekající na surovinu spí.
        bank.NotifyPushed(input);

        for (int tick = 0; tick < 500; tick++)
        {
            input.Tick();
            output.Tick();
            bank.Tick();
        }

        Assert.Equal(1, bank.CraftedTotal);

        // Ruda dojela po pásu, zdrtila se a prach odjel na výstupní pás.
        Assert.Equal(0, input.Count);
        Assert.Equal(1, output.Count);
    }

    [Fact]
    public void Machine_stops_when_the_output_is_full()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.TryAssignOperator(machine, 0);

        for (int i = 0; i < MachineBank.InputCapacity; i++)
        {
            bank.TryInsert(machine, Ore);
        }

        for (int tick = 0; tick < 10_000; tick++)
        {
            bank.Tick();
        }

        // Nezdrtí víc, než se vejde do výstupu — jinak by itemy mizely.
        Assert.Equal(MachineBank.OutputCapacity, bank.OutputOf(machine));
        Assert.True(bank.CraftedTotal <= MachineBank.OutputCapacity);
    }

    [Fact]
    public void Machine_refuses_the_wrong_material()
    {
        (MachineBank bank, int machine) = Crusher();

        Assert.False(bank.TryInsert(machine, Dust));
        Assert.Equal(0, bank.InputOf(machine));
    }

    [Fact]
    public void Idle_machine_sleeps_and_wakes_on_an_event()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.SetInputBelt(machine, new BeltSegment(4));
        bank.SetOutputBelt(machine, new BeltSegment(4));
        bank.SetPowered(machine, true);

        // Po pár tikách bez suroviny usne.
        for (int tick = 0; tick < 5; tick++)
        {
            bank.Tick();
        }

        Assert.Equal(0, bank.ActiveCount);

        // Vložení suroviny ho probudí.
        bank.TryInsert(machine, Ore);
        Assert.Equal(1, bank.ActiveCount);
    }

    // ================= KRITÉRIUM M0 =================

    /// <summary>
    /// KRITÉRIUM T5: napojím na drtič pásy a proud a počítadlo volných kolonistů se zvedne
    /// z nuly na jedničku. Tenhle jediný okamžik je celá hra v jedné vteřině.
    /// </summary>
    [Fact]
    public void Automating_a_crusher_frees_the_colonist_who_was_operating_it()
    {
        (MachineBank bank, int machine) = Crusher();

        // JEDEN KOLONISTA, JEDEN STROJ. Stojí u drtiče a drtí ručně.
        const int Colonists = 1;
        bank.TryInsert(machine, Ore);
        Assert.True(bank.TryAssignOperator(machine, colonist: 0));

        int busy = 1;
        int free = Colonists - busy;
        Assert.Equal(0, free);

        for (int tick = 0; tick < TicksPerCraft * 2; tick++)
        {
            bank.Tick();
        }

        Assert.Equal(MachineMode.Manual, bank.ModeOf(machine));
        Assert.Equal(0, bank.OperatorOf(machine));
        output.WriteLine($"Rucne: volnych {free} z {Colonists}, zdrceno {bank.CraftedTotal}.");

        // TEĎ SE POSTAVÍ PÁSY A PŘIPOJÍ PROUD.
        bank.SetInputBelt(machine, new BeltSegment(cells: 4));
        bank.SetOutputBelt(machine, new BeltSegment(cells: 4));
        bank.SetPowered(machine, true);

        int freed = bank.Tick();

        busy -= freed;
        free = Colonists - busy;

        output.WriteLine($"Po automatizaci: uvolneno {freed}, volnych {free} z {Colonists}.");

        // TOHLE JE TO ČÍSLO: z nuly na jedničku.
        Assert.Equal(1, freed);
        Assert.Equal(1, free);
        Assert.Equal(MachineMode.Automatic, bank.ModeOf(machine));
        Assert.Equal(-1, bank.OperatorOf(machine));
    }

    /// <summary>
    /// A totéž ve větším: deset drtičů, deset lidí. Každý postavený pás uvolní jednoho.
    /// </summary>
    [Fact]
    public void Every_automated_machine_frees_exactly_one_person()
    {
        var bank = new MachineBank();
        const int Machines = 10;

        var machines = new int[Machines];
        for (int i = 0; i < Machines; i++)
        {
            machines[i] = bank.Add(new Vector3i(i, 2, 0), Ore, Dust, TicksPerCraft);
            Assert.True(bank.TryAssignOperator(machines[i], colonist: i));
        }

        int busy = Machines;
        Assert.Equal(0, Machines - busy);

        // Automatizovat po jednom a sledovat, jak lidí ubývá u strojů.
        for (int i = 0; i < Machines; i++)
        {
            bank.SetInputBelt(machines[i], new BeltSegment(4));
            bank.SetOutputBelt(machines[i], new BeltSegment(4));
            bank.SetPowered(machines[i], true);

            busy -= bank.Tick();

            Assert.Equal(i + 1, Machines - busy);
        }

        Assert.Equal(0, busy);
        Assert.Equal(Machines, Machines - busy);
    }

    [Fact]
    public void Cutting_the_power_sends_the_machine_back_to_needing_a_person()
    {
        (MachineBank bank, int machine) = Crusher();
        bank.SetInputBelt(machine, new BeltSegment(4));
        bank.SetOutputBelt(machine, new BeltSegment(4));
        bank.SetPowered(machine, true);
        bank.Tick();

        Assert.Equal(MachineMode.Automatic, bank.ModeOf(machine));
        Assert.False(bank.TryAssignOperator(machine, 0));

        bank.SetPowered(machine, false);

        // Bez proudu je zase potřeba člověk — a teď se u něj postavit smí.
        Assert.Equal(MachineMode.Manual, bank.ModeOf(machine));
        Assert.True(bank.TryAssignOperator(machine, 0));
    }

    [Fact]
    public void Three_thousand_machines_mostly_sleep()
    {
        var bank = new MachineBank(capacity: 4096);
        const int Machines = 3000;

        for (int i = 0; i < Machines; i++)
        {
            int machine = bank.Add(new Vector3i(i % 64, 2, i / 64), Ore, Dust, TicksPerCraft);
            bank.SetInputBelt(machine, new BeltSegment(4));
            bank.SetOutputBelt(machine, new BeltSegment(4));
            bank.SetPowered(machine, true);
        }

        // Bez suroviny nemá žádný co dělat.
        for (int tick = 0; tick < 10; tick++)
        {
            bank.Tick();
        }

        output.WriteLine($"{Machines} stroju bez suroviny: tika se {bank.ActiveCount}.");

        // Cíl ze sekce 8 je 3 000 aktivních strojů; tohle ukazuje, že nečinné nic nestojí.
        Assert.Equal(0, bank.ActiveCount);
    }
}
