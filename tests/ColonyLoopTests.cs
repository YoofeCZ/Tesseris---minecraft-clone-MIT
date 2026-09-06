using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Uzavřená smyčka: označím rudu → kolonista ji vykope → donese ji do fabriky → zdrtí se.
/// </summary>
/// <remarks>
/// Do téhle chvíle končila vykopaná ruda v abstraktním počítadle a fabrika se musela krmit
/// příkazem. Tohle je ta věc, která dělí demo od hry: <b>bez pásu se ruda nosí po svých,
/// s pásem ji kolonista jen odloží</b> — a to je celý důvod něco stavět.
/// </remarks>
public sealed class ColonyLoopTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:ore", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Podlaha na y = 1 přes celý chunk, tedy stání na y = 2.</summary>
    private static (VoxelWorld World, ushort Stone, ushort Ore) FloorWorld()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        ushort ore = world.Registry.IndexOf("test:ore");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        return (world, stone, ore);
    }

    private static void BuildNavigation(ColonyRuntime colony, VoxelWorld world)
    {
        colony.SetWater(NoWater);

        // Navigace se staví s rozpočtem jednoho chunku na tik, takže pár kol stačí.
        for (int i = 0; i < 8; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }
    }

    [Fact]
    public void Mined_ore_ends_up_in_the_machine_next_to_it()
    {
        (VoxelWorld world, _, ushort ore) = FloorWorld();

        // Kus rudy na podlaze.
        world.SetBlock(10, 2, 10, ore);

        var colony = new ColonyRuntime();
        BuildNavigation(colony, world);

        // Drtič kousek vedle. Bere přesně tuhle rudu.
        colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 5);

        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        Assert.True(colonist >= 0, "Kolonista neměl kde stát.");

        Assert.Equal(1, colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10)));

        for (int tick = 0; tick < 5_000 && colony.Colonists.DeliveredToFactory == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        output.WriteLine($"Doneseno do fabriky: {colony.Colonists.DeliveredToFactory}, "
            + $"do skladu: {colony.Colonists.StoredItems}, zdrceno: {colony.Machines.CraftedTotal}.");

        // TOHLE JE TA SMYČKA: ruda nešla do abstraktního skladu, ale do stroje.
        Assert.Equal(1, colony.Colonists.DeliveredToFactory);
        Assert.Equal(0, colony.Colonists.StoredItems);
    }

    [Fact]
    public void Without_a_factory_the_ore_still_goes_somewhere()
    {
        (VoxelWorld world, _, ushort ore) = FloorWorld();
        world.SetBlock(10, 2, 10, ore);

        var colony = new ColonyRuntime();
        BuildNavigation(colony, world);
        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10));

        for (int tick = 0; tick < 5_000 && colony.Colonists.StoredItems == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        // Bez pásu a stroje se náklad NESMÍ ztratit — skončí ve skladu.
        Assert.Equal(1, colony.Colonists.StoredItems);
        Assert.Equal(0, colony.Colonists.DeliveredToFactory);
    }

    [Fact]
    public void Mined_ore_ends_up_on_a_belt_when_no_machine_wants_it()
    {
        (VoxelWorld world, _, ushort ore) = FloorWorld();
        world.SetBlock(10, 2, 10, ore);

        var colony = new ColonyRuntime();
        BuildNavigation(colony, world);
        colony.PlaceBelt(new Vector3i(6, 2, 6), cells: 4);
        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10));

        for (int tick = 0; tick < 5_000 && colony.Colonists.DeliveredToFactory == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        Assert.Equal(1, colony.Colonists.DeliveredToFactory);
        Assert.Equal(1, colony.ItemsOnBelts);
    }

    /// <summary>
    /// Plný stroj náklad nepřijme. Kolonista ho pak nesmí zahodit — musí skončit ve skladu.
    /// </summary>
    [Fact]
    public void A_full_machine_does_not_swallow_the_load()
    {
        (VoxelWorld world, _, ushort ore) = FloorWorld();
        world.SetBlock(10, 2, 10, ore);

        var colony = new ColonyRuntime();
        BuildNavigation(colony, world);

        int crusher = colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 100_000);
        for (int i = 0; i < MachineBank.InputCapacity; i++)
        {
            colony.Machines.TryInsert(crusher, ore);
        }

        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10));

        int total = 0;
        for (int tick = 0; tick < 5_000 && total == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
            total = colony.Colonists.StoredItems + colony.Colonists.DeliveredToFactory;
        }

        // Ať skončila kdekoli, nesmí zmizet.
        Assert.Equal(1, total);
        Assert.Equal(MachineBank.InputCapacity, colony.Machines.InputOf(crusher));
    }

    /// <summary>
    /// Celý řetěz: označená ruda se vykope, donese do drtiče a ten ji zpracuje.
    /// </summary>
    [Fact]
    public void Whole_chain_from_marked_ore_to_crushed_product()
    {
        (VoxelWorld world, _, ushort ore) = FloorWorld();

        for (int x = 10; x < 14; x++)
        {
            world.SetBlock(x, 2, 10, ore);
        }

        var colony = new ColonyRuntime();
        BuildNavigation(colony, world);

        int crusher = colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 5);

        for (int i = 0; i < 3; i++)
        {
            colony.TrySpawnColonist(new Vector3i(3 + i, 2, 3));
        }

        // RUČNÍ DRTIČ POTŘEBUJE ČLOVĚKA. Bez pásů je pořád v ručním režimu, takže sám proud
        // nestačí — a jeden ze tří lidí u něj musí stát. To je celá pointa: stroj bez
        // automatizace kupuje lidský čas.
        Assert.True(colony.TryAssignOperator(crusher, colonist: 0));

        int marked = colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(13, 2, 10));
        Assert.Equal(4, marked);

        for (int tick = 0; tick < 20_000 && colony.Machines.CraftedTotal < marked; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        output.WriteLine($"Označeno {marked}, vykopáno {colony.Jobs.DoneCount}, "
            + $"doneseno {colony.Colonists.DeliveredToFactory}, zdrceno {colony.Machines.CraftedTotal}.");

        Assert.Equal(marked, colony.Jobs.DoneCount);
        Assert.Equal(marked, colony.Colonists.DeliveredToFactory);
        Assert.Equal(marked, colony.Machines.CraftedTotal);
    }
}
