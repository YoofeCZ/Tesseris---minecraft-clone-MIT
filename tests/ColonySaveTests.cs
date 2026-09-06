using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Uložení a načtení kolonie.
/// </summary>
/// <remarks>
/// <b>Postavená linka je jediná věc, do které hráč investoval čas.</b> Když ji restart smaže,
/// je celá automatizace jen ukázka. Testy proto neověřují jen „něco se načetlo", ale že linka
/// po načtení <b>dál běží</b> — vkládače přendávají, stroj drtí a itemy stojí tam, kde stály.
/// </remarks>
public sealed class ColonySaveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tesseris-kolonie-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Path_ => System.IO.Path.Combine(_directory, ColonySave.FileName);

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
    public void Empty_colony_writes_no_file()
    {
        (VoxelWorld world, _) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        ColonySave.Save(colony, Path_);

        Assert.False(File.Exists(Path_));
        Assert.Equal(0, ColonySave.Load(colony, Path_));
    }

    [Fact]
    public void Belts_come_back_with_their_cargo_in_the_same_place()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        var direction = new Vector3i(0, 0, 1);
        BeltSegment belt = colony.PlaceBelt(cell, cells: 8, direction);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(colony.TryPushOntoBelt(belt, ore));
            for (int step = 0; step < 7; step++)
            {
                belt.Tick();
            }
        }

        // Otisk poloh před uložením. Na pásu JE poloha itemu celý stav — kdyby se obnovoval
        // opakovaným položením, itemy by po načtení stály jinde.
        var gapsBefore = new ushort[belt.Count];
        for (int i = 0; i < belt.Count; i++)
        {
            gapsBefore[i] = belt.SlotAt(i).Gap;
        }

        ColonySave.Save(colony, Path_);

        ColonyRuntime loaded = Colony(world);
        Assert.True(ColonySave.Load(loaded, Path_) > 0);

        Assert.Equal(1, loaded.BeltCount);
        ColonyRuntime.BeltPlacement placement = loaded.Placements[0];
        Assert.Equal(cell, placement.Start);
        Assert.Equal(direction, placement.Direction);
        Assert.Equal(belt.Cells, placement.Belt.Cells);
        Assert.Equal(gapsBefore.Length, placement.Belt.Count);

        for (int i = 0; i < gapsBefore.Length; i++)
        {
            Assert.Equal(gapsBefore[i], placement.Belt.SlotAt(i).Gap);
            Assert.Equal(ore, placement.Belt.SlotAt(i).ItemId);
        }

        output.WriteLine($"Mezery pred: [{string.Join(", ", gapsBefore)}]");
    }

    [Fact]
    public void Machine_comes_back_with_its_buffers_and_half_finished_batch()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(cell, ore, ore, ticksPerCraft: 60);
        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.TryAssignOperator(crusher, colonist);

        for (int i = 0; i < 4; i++)
        {
            colony.Machines.TryInsert(crusher, ore);
        }

        // Rozdělaná dávka: pár tiků, ale ne celá.
        for (int tick = 0; tick < 20; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        int input = colony.Machines.InputOf(crusher);
        int progress = colony.Machines.ProgressOf(crusher);
        Assert.True(progress > 0, "Test potřebuje rozdělanou dávku.");

        ColonySave.Save(colony, Path_);

        ColonyRuntime loaded = Colony(world);
        Assert.True(ColonySave.Load(loaded, Path_) > 0);

        Assert.Equal(1, loaded.MachineCount);
        Assert.Equal(cell, loaded.Machines.CellOf(0));
        Assert.Equal(input, loaded.Machines.InputOf(0));

        // ROZDĚLANÁ PRÁCE SE NESMÍ VRÁTIT NA ZAČÁTEK. Přes TryInsert to obnovit nejde.
        Assert.Equal(progress, loaded.Machines.ProgressOf(0));

        // Člověk u stroje taky. Bez toho by se po načtení tvářil jako volný.
        Assert.Equal(1, loaded.Colonists.Count);
        Assert.Equal(ColonistState.Operating, loaded.Colonists.StateOf(0));
        Assert.Equal(0, loaded.FreeColonists);
        Assert.Equal(0, loaded.Machines.OperatorOf(0));
    }

    /// <summary>
    /// Nejdůležitější test celého savu: načtená linka musí <b>dál běžet</b>.
    /// </summary>
    [Fact]
    public void A_loaded_line_keeps_running()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var machineCell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(machineCell, ore, ore, ticksPerCraft: 5);

        BeltSegment input = colony.PlaceBelt(new Vector3i(5, 2, 6), cells: 4);
        BeltSegment outputBelt = colony.PlaceBelt(new Vector3i(7, 2, 6), cells: 4);

        colony.Machines.SetInputBelt(crusher, input);
        colony.Machines.SetOutputBelt(crusher, outputBelt);
        colony.Machines.SetPowered(crusher, true);

        colony.Inserters.AddMachineToBelt(colony.Machines, new Vector3i(7, 2, 6), crusher, outputBelt);
        colony.CountInserter();

        Assert.Equal(MachineMode.Automatic, colony.Machines.ModeOf(crusher));
        Assert.True(colony.TryPushOntoBelt(input, ore));

        ColonySave.Save(colony, Path_);

        ColonyRuntime loaded = Colony(world);
        Assert.True(ColonySave.Load(loaded, Path_) > 0);

        // REŽIM SE NEUKLÁDÁ, ODVOZUJE SE — a musí vyjít stejně, jinak se pásy nenapojily.
        Assert.Equal(MachineMode.Automatic, loaded.Machines.ModeOf(0));
        Assert.Equal(2, loaded.BeltCount);
        Assert.Equal(1, loaded.InserterCount);
        Assert.Equal(1, loaded.ItemsOnBelts);

        int crafted = 0;
        for (int tick = 0; tick < 600 && crafted == 0; tick++)
        {
            loaded.Tick(world, world.Registry);
            crafted = loaded.Machines.CraftedTotal;
        }

        output.WriteLine($"Po nacteni zdrceno {crafted}, vkladace prendaly "
            + $"{loaded.Inserters.MovedTotal}, na pasech {loaded.ItemsOnBelts}.");

        Assert.True(crafted > 0, "Načtená linka nedrtila — pás nebo proud se nenapojily.");
    }

    [Fact]
    public void Unfinished_dig_jobs_survive_but_finished_ones_do_not()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        for (int x = 10; x < 14; x++)
        {
            world.SetBlock(x, 2, 10, ore);
        }

        Assert.Equal(4, colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(13, 2, 10)));

        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        for (int tick = 0; tick < 3_000 && colony.Jobs.DoneCount == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        int done = colony.Jobs.DoneCount;
        Assert.True(done > 0, "Test potřebuje aspoň jeden hotový úkol.");

        ColonySave.Save(colony, Path_);

        ColonyRuntime loaded = Colony(world);
        ColonySave.Load(loaded, Path_);

        // HOTOVÉ ÚKOLY SE NEUKLÁDAJÍ: vykopaný blok je ve světě a označení už nic neznamená.
        Assert.Equal(4 - done, loaded.Jobs.Count);
        Assert.Equal(0, loaded.Jobs.DoneCount);

        output.WriteLine($"Hotovo pred ulozenim {done}, nedokoncenych po nacteni {loaded.Jobs.Count}.");
    }

    [Fact]
    public void Carried_load_survives_the_save()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        world.SetBlock(10, 2, 10, ore);
        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10));

        // Zastavit se přesně ve chvíli, kdy kolonista něco nese.
        bool carrying = false;
        for (int tick = 0; tick < 5_000 && !carrying; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
            carrying = colony.Colonists.CarryingOf(0) != BlockRegistry.Air;
        }

        Assert.True(carrying, "Test potřebuje kolonistu s nákladem.");

        ColonySave.Save(colony, Path_);

        ColonyRuntime loaded = Colony(world);
        ColonySave.Load(loaded, Path_);

        Assert.Equal(ore, loaded.Colonists.CarryingOf(0));

        // Kdo něco nese, není volný — musí se toho nejdřív zbavit.
        Assert.Equal(ColonistState.Delivering, loaded.Colonists.StateOf(0));

        for (int tick = 0; tick < 5_000 && loaded.Colonists.StoredItems == 0; tick++)
        {
            loaded.Tick(world, world.Registry);
            loaded.Colonists.WakeIdle();
        }

        // NÁKLAD SE NESMÍ ZTRATIT ANI PŘES RESTART.
        Assert.Equal(1, loaded.Colonists.StoredItems);
    }

    [Fact]
    public void Demolished_things_do_not_come_back()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var kept = new Vector3i(6, 2, 6);
        var removed = new Vector3i(8, 2, 6);

        colony.PlaceCrusher(kept, ore, ore, ticksPerCraft: 5);
        colony.PlaceCrusher(removed, ore, ore, ticksPerCraft: 5);
        colony.PlaceBelt(new Vector3i(5, 2, 6), cells: 4);
        BeltSegment doomed = colony.PlaceBelt(new Vector3i(9, 2, 6), cells: 4);
        Assert.NotNull(doomed);

        colony.Demolish(removed);
        colony.Demolish(new Vector3i(9, 2, 6));

        ColonySave.Save(colony, Path_);

        ColonyRuntime loaded = Colony(world);
        ColonySave.Load(loaded, Path_);

        // Zhuštění indexů: do souboru jdou jen živé prvky, jinak by sav rostl o každé bourání.
        Assert.Equal(1, loaded.MachineCount);
        Assert.Equal(1, loaded.Machines.Count);
        Assert.Equal(kept, loaded.Machines.CellOf(0));
        Assert.Equal(1, loaded.BeltCount);
    }

    [Fact]
    public void A_corrupted_file_leaves_an_empty_colony_instead_of_a_crash()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);
        colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 5);
        colony.PlaceBelt(new Vector3i(5, 2, 6), cells: 4);
        ColonySave.Save(colony, Path_);

        byte[] bytes = File.ReadAllBytes(Path_);
        File.WriteAllBytes(Path_, bytes[..(bytes.Length / 2)]);

        ColonyRuntime loaded = Colony(world);
        Assert.Equal(0, ColonySave.Load(loaded, Path_));

        // Půlka pásů bez strojů by se tvářila jako platný stav — proto se maže všechno.
        Assert.Equal(0, loaded.BeltCount);
        Assert.Equal(0, loaded.MachineCount);
    }

    [Fact]
    public void A_file_from_another_format_is_refused()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path_, [1, 2, 3, 4, 5, 6, 7, 8]);

        (VoxelWorld world, _) = FloorWorld();
        ColonyRuntime loaded = Colony(world);

        Assert.Equal(0, ColonySave.Load(loaded, Path_));
    }

    /// <summary>
    /// Uložit → načíst → uložit musí dát bajt po bajtu stejný soubor.
    /// </summary>
    /// <remarks>
    /// Kdyby se cokoli při načtení ztratilo nebo přibylo, projeví se to tady — a to i u věcí,
    /// na které by mě nenapadlo napsat vlastní test.
    /// </remarks>
    [Fact]
    public void Save_load_save_is_byte_identical()
    {
        (VoxelWorld world, ushort ore) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        int crusher = colony.PlaceCrusher(new Vector3i(6, 2, 6), ore, ore, ticksPerCraft: 30);
        BeltSegment input = colony.PlaceBelt(new Vector3i(5, 2, 6), cells: 6, new Vector3i(1, 0, 0));
        BeltSegment outputBelt = colony.PlaceBelt(new Vector3i(7, 2, 6), cells: 6, new Vector3i(0, 0, -1));

        colony.Machines.SetInputBelt(crusher, input);
        colony.Machines.SetOutputBelt(crusher, outputBelt);
        colony.Machines.SetPowered(crusher, true);
        colony.Inserters.AddBeltToMachine(new Vector3i(5, 2, 6), input, crusher);
        colony.CountInserter();
        colony.Inserters.AddMachineToBelt(colony.Machines, new Vector3i(7, 2, 6), crusher, outputBelt);
        colony.CountInserter();

        colony.TryPushOntoBelt(input, ore);
        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(11, 2, 11));

        ColonySave.Save(colony, Path_);
        byte[] first = File.ReadAllBytes(Path_);

        ColonyRuntime loaded = Colony(world);
        Assert.True(ColonySave.Load(loaded, Path_) > 0);

        string second = Path_ + ".2";
        ColonySave.Save(loaded, second);

        Assert.Equal(first, File.ReadAllBytes(second));
        output.WriteLine($"Sav ma {first.Length} bajtu a po kolečku je totozny.");
    }
}
