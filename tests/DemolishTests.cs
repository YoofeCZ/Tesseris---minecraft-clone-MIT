using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Bourání. Druhá půlka stavění — kdo jednou položí pás špatným směrem, musí ho umět odstranit.
/// </summary>
/// <remarks>
/// Testy se točí kolem dvou věcí, protože obě selžou tiše: <b>materiál se nesmí ztratit</b>
/// a <b>po zbourané věci nesmí nikde zůstat odkaz</b>. To druhé je horší — stroj napojený na
/// neexistující pás by se dál tvářil jako automatický, takže by hlásil uvolněného člověka,
/// kterého ve skutečnosti nemá kdo nahradit.
/// </remarks>
public sealed class DemolishTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:ore", Texture = "stone", Opaque = true },
    ]);

    private static (VoxelWorld World, ushort Ore) FloorWorld()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        return (world, world.Registry.IndexOf("test:ore"));
    }

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(ushort.MaxValue);

        for (int i = 0; i < 8; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    [Fact]
    public void Nothing_to_demolish_says_so()
    {
        (VoxelWorld world, _) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        Assert.Equal(ColonyRuntime.Demolished.Nothing, colony.Demolish(new Vector3i(5, 2, 5)));
    }

    [Fact]
    public void Demolished_belt_returns_its_cargo_to_storage()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        BeltSegment belt = colony.PlaceBelt(cell, cells: 8);

        // Položit se musí OPRAVDU. Kdyby se počítaly i neúspěšné pokusy, prošel by test
        // s prázdným pásem a nulovým skladem, aniž by cokoli ověřil.
        const int Pushed = 3;
        for (int i = 0; i < Pushed; i++)
        {
            Assert.True(colony.TryPushOntoBelt(belt, ore), $"Item {i} se na pás nevešel.");

            // Odstup: pás nepustí další item, dokud předchozí neodjede.
            for (int step = 0; step < BeltSegment.MinimumSpacing; step++)
            {
                belt.Tick();
            }
        }

        Assert.Equal(Pushed, belt.Count);

        Assert.Equal(ColonyRuntime.Demolished.Belt, colony.Demolish(cell));

        // ITEMY NA PÁSU SE NESMÍ VYPAŘIT.
        Assert.Equal(Pushed, colony.Colonists.StoredItems);
        Assert.Equal(0, colony.BeltCount);
        Assert.Empty(colony.Placements);
        Assert.Null(colony.BeltAt(cell));
    }

    [Fact]
    public void Demolished_machine_frees_its_operator()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(cell, ore, ore, ticksPerCraft: 5);
        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        Assert.True(colonist >= 0);

        Assert.True(colony.TryAssignOperator(crusher, colonist));
        Assert.Equal(0, colony.FreeColonists);

        Assert.Equal(ColonyRuntime.Demolished.Machine, colony.Demolish(cell));

        // ČÍSLO, NA KTERÉM STOJÍ CELÁ HRA. Zbouraný stroj člověka drží dál jen tehdy,
        // když se na něj zapomene — a pak zmizí z evidence úplně.
        Assert.Equal(1, colony.FreeColonists);
        Assert.Equal(0, colony.MachineCount);
    }

    [Fact]
    public void Demolished_machine_returns_its_buffers_to_storage()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(cell, ore, ore, ticksPerCraft: 100_000);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(colony.Machines.TryInsert(crusher, ore));
        }

        colony.Demolish(cell);

        Assert.Equal(3, colony.Colonists.StoredItems);
    }

    [Fact]
    public void Demolished_machine_stops_working()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(cell, ore, ore, ticksPerCraft: 2);
        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.TryAssignOperator(crusher, colonist);
        colony.Machines.TryInsert(crusher, ore);

        for (int tick = 0; tick < 10; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        int crafted = colony.Machines.CraftedTotal;
        Assert.True(crafted > 0, "Drtič s obsluhou a surovinou měl něco zdrtit.");

        colony.Demolish(cell);

        // Doplnit surovinu do zbouraného stroje nejde a nesmí to nic rozjet.
        Assert.False(colony.Machines.TryInsert(crusher, ore));
        for (int tick = 0; tick < 100; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        Assert.Equal(crafted, colony.Machines.CraftedTotal);
        Assert.Equal(0, colony.Machines.ActiveCount);
    }

    /// <summary>
    /// Zbouraný pás musí stroj přestat počítat mezi své — jinak by zůstal „automatický".
    /// </summary>
    [Fact]
    public void Demolished_belt_puts_the_machine_back_to_manual()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(cell, ore, ore, ticksPerCraft: 5);

        var inputCell = new Vector3i(5, 2, 6);
        BeltSegment input = colony.PlaceBelt(inputCell, cells: 4);
        BeltSegment outputBelt = colony.PlaceBelt(new Vector3i(7, 2, 6), cells: 4);

        colony.Machines.SetInputBelt(crusher, input);
        colony.Machines.SetOutputBelt(crusher, outputBelt);
        colony.Machines.SetPowered(crusher, true);

        Assert.Equal(MachineMode.Automatic, colony.Machines.ModeOf(crusher));

        colony.Demolish(inputCell);

        // ODPOJENÍ MUSÍ BÝT VIDĚT NA REŽIMU. Stroj bez vstupu není automatický, takže zase
        // potřebuje člověka — a přesně to je ta zpětná vazba, kterou hráč po zbourání čeká.
        Assert.Equal(MachineMode.Manual, colony.Machines.ModeOf(crusher));
        Assert.Equal(-1, input.ConsumerTag);
    }

    /// <summary>
    /// Vkládač, kterému zbourání vzalo jeden konec, nesmí zůstat stát.
    /// </summary>
    [Fact]
    public void Demolishing_a_belt_takes_its_inserters_with_it()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        int crusher = colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 5);

        var beltCell = new Vector3i(4, 2, 6);
        BeltSegment belt = colony.PlaceBelt(beltCell, cells: 4);

        int inserter = colony.Inserters.AddBeltToMachine(new Vector3i(5, 2, 6), belt, crusher);
        colony.CountInserter();
        Assert.Equal(1, colony.InserterCount);

        colony.Demolish(beltCell);

        Assert.True(colony.Inserters.IsRemoved(inserter));
        Assert.Equal(0, colony.InserterCount);
        Assert.Equal(-1, belt.InserterTag);
    }

    /// <inheritdoc cref="Demolishing_a_belt_takes_its_inserters_with_it"/>
    [Fact]
    public void Demolishing_a_machine_takes_its_inserters_with_it()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var machineCell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(machineCell, ore, ore, ticksPerCraft: 5);
        BeltSegment belt = colony.PlaceBelt(new Vector3i(4, 2, 6), cells: 4);

        int inserter = colony.Inserters.AddBeltToMachine(new Vector3i(5, 2, 6), belt, crusher);
        colony.CountInserter();

        colony.Demolish(machineCell);

        Assert.True(colony.Inserters.IsRemoved(inserter));
        Assert.Equal(0, colony.InserterCount);
    }

    /// <summary>
    /// Zbouraný vkládač se bourá dřív než pás nebo stroj pod ním.
    /// </summary>
    [Fact]
    public void Inserter_goes_first_when_it_shares_a_cell_with_a_belt()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        int crusher = colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 5);

        var shared = new Vector3i(5, 2, 6);
        BeltSegment belt = colony.PlaceBelt(shared, cells: 4);
        colony.Inserters.AddBeltToMachine(shared, belt, crusher);
        colony.CountInserter();

        Assert.Equal(ColonyRuntime.Demolished.Inserter, colony.Demolish(shared));

        // Pás pod ním zůstal.
        Assert.Equal(1, colony.BeltCount);
        Assert.NotNull(colony.BeltAt(shared));

        // Až druhé kliknutí vezme pás.
        Assert.Equal(ColonyRuntime.Demolished.Belt, colony.Demolish(shared));
    }

    /// <summary>
    /// Zbouraný stroj nesmí kolonistům dál sloužit jako cíl odevzdání.
    /// </summary>
    [Fact]
    public void Colonist_does_not_carry_ore_into_a_demolished_machine()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        world.SetBlock(10, 2, 10, ore);

        var machineCell = new Vector3i(6, 2, 6);
        colony.PlaceCrusher(machineCell, ore, ore, ticksPerCraft: 5);
        colony.Demolish(machineCell);

        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10));

        for (int tick = 0; tick < 5_000 && colony.Colonists.StoredItems == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        output.WriteLine($"Do fabriky {colony.Colonists.DeliveredToFactory}, "
            + $"na sklad {colony.Colonists.StoredItems}.");

        Assert.Equal(0, colony.Colonists.DeliveredToFactory);
        Assert.Equal(1, colony.Colonists.StoredItems);
    }

    /// <summary>
    /// Postavit → zbourat → postavit znovu musí skončit ve funkčním stavu, ne v troskách odkazů.
    /// </summary>
    [Fact]
    public void Rebuilding_on_the_same_cell_works()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);

        for (int round = 0; round < 3; round++)
        {
            int crusher = colony.PlaceCrusher(cell, ore, ore, ticksPerCraft: 2);
            int colonist = round == 0 ? colony.TrySpawnColonist(new Vector3i(4, 2, 4)) : 0;
            Assert.True(colony.TryAssignOperator(crusher, colonist));
            Assert.True(colony.Machines.TryInsert(crusher, ore));

            int before = colony.Machines.CraftedTotal;
            for (int tick = 0; tick < 10; tick++)
            {
                colony.Tick(world, world.Registry);
            }

            Assert.True(colony.Machines.CraftedTotal > before, $"Kolo {round}: nový drtič nedrtil.");
            Assert.Equal(1, colony.MachineCount);

            colony.Demolish(cell);
            Assert.Equal(0, colony.MachineCount);
            Assert.Equal(1, colony.FreeColonists);
        }
    }
}
