using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Co se s kolonií stane po restartu procesu a když se ke skladu nedá dojít.
/// </summary>
/// <remarks>
/// <para>Obojí našel adversariální rozbor commitu se skladem a hladem. Ani jedno neukázalo
/// měření ve hře: sonda zakládá radnici ve <b>stejném běhu</b>, ve kterém měří, a testy
/// hladu měly sklad buď dosažitelný, nebo prázdný — případ „jídlo je, cesta ne" chyběl.</para>
/// </remarks>
public sealed class ColonyRestartTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tesseris-restart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SavePath => Path.Combine(_directory, ColonySave.FileName);

    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        // Jméno musí sedět na ColonyFood.Names — jídlo se pozná podle jména, ne podle typu.
        new BlockDefinition { Id = "tesseris:cactus", Texture = "stone", Opaque = true },
    ]);

    private static VoxelWorld Floor()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 0, z, stone);
                world.SetBlock(x, 1, z, stone);
            }
        }

        return world;
    }

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(ushort.MaxValue);

        // BEZ TOHOHLE SKLAD ŽÁDNÉ JÍDLO NEZNÁ. Jídlo se pozná podle jména, a překlad jmen
        // na id musí někdo udělat — v okně to dělá načtení světa, v testu to musím já.
        colony.ResolveFood(world.Registry);

        for (int i = 0; i < 10; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    /// <summary>
    /// Restart procesu nesmí kolonii odříznout od jejího vlastního skladu.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ OPRAVY TENHLE TEST PADÁ.</b> Sav nesl obsah skladu, ale ne jeho MÍSTO,
    /// takže po načtení bylo <c>Store.HasCell == false</c>. Kolonista se pak nikdy nenajedl:
    /// hlad mu dorostl na maximum a celá kolonie jela trvale na poloviční výkon <b>vedle
    /// plného skladu jídla</b>. Radnice přitom ve světě dál stála a panel u toho psal, že
    /// žádná není.</para>
    ///
    /// <para>Uvnitř jednoho běhu se to neprojevilo, protože <c>Clear()</c> polohu neresetuje.
    /// Test proto staví <b>novou</b> kolonii, což je přesně to, co se stane po restartu.</para>
    /// </remarks>
    [Fact]
    public void After_a_restart_the_colony_can_still_reach_its_own_store()
    {
        VoxelWorld world = Floor();
        ushort food = world.Registry.IndexOf("tesseris:cactus");

        ColonyRuntime colony = Colony(world);
        var hall = new Vector3i(8, 2, 8);
        Assert.True(colony.FoundTownHall(hall));

        colony.Colonists.StoreItems(food, 5);
        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        Assert.True(colonist >= 0);

        ColonySave.Save(colony, SavePath);

        // NOVÁ kolonie, jako po restartu procesu.
        ColonyRuntime loaded = Colony(world);
        Assert.True(ColonySave.Load(loaded, SavePath) > 0);

        Assert.True(loaded.TownHall.IsFounded, "Radnice se po načtení neobnovila.");
        Assert.Equal(hall, loaded.TownHall.Cell);
        Assert.True(loaded.Store.HasCell, "Sklad po načtení neví, kde je — nikdo se v něm nenají.");
        Assert.Equal(hall, loaded.Store.Cell);

        // A hlavně: hladový kolonista se opravdu naji.
        loaded.Colonists.SetHunger(0, ColonySimulation.HungryAt + 100);

        for (int tick = 0; tick < 3_000 && loaded.Colonists.MealsEaten == 0; tick++)
        {
            loaded.Tick(world, world.Registry);
            loaded.Colonists.WakeIdle();
        }

        output.WriteLine($"Po nacteni: snedeno {loaded.Colonists.MealsEaten}, "
            + $"hlad {loaded.Colonists.HungerOf(0)}, ve skladu jidla {loaded.Store.FoodCount}, "
            + $"celkem {loaded.Store.Total}.");

        Assert.True(loaded.Colonists.MealsEaten > 0, "Po restartu se nikdo nenajedl.");
    }

    /// <summary>
    /// Když jídlo ve skladu je, ale cesta k němu nevede, kolonista se nesmí zacyklit.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ OPRAVY TENHLE TEST PADÁ.</b> Po neúspěšné cestě se kolonista přepnul na
    /// <c>Idle</c>, odkud šel rovnou zpátky do <c>BeginJob</c> — a tam se hlad ani sklad
    /// nezměnily, takže požádal o cestu znovu. Každý tik, donekonečna. Nikdy nesáhl po kopání
    /// a nikdy neusnul, takže pár takových lidí trvale sežralo rozpočet navigace: naměřených
    /// 0,858 ms na hledání a osm hledání na tik.</para>
    ///
    /// <para>Kopání je proti témuž chráněné přes <c>JobState.Deferred</c>. Jedení nebylo.</para>
    /// </remarks>
    [Fact]
    public void A_hungry_colonist_who_cannot_reach_the_store_goes_back_to_work()
    {
        VoxelWorld world = Floor();
        ushort food = world.Registry.IndexOf("tesseris:cactus");
        ushort stone = world.Registry.IndexOf("test:stone");

        // Kus kamene navíc, aby bylo co kopat.
        for (int x = 4; x < 8; x++)
        {
            world.SetBlock(x, 2, 4, stone);
        }

        ColonyRuntime colony = Colony(world);

        // SKLAD MIMO DOSAH: vysoko ve vzduchu, kde není žádná pochůzná buňka.
        Assert.True(colony.FoundTownHall(new Vector3i(8, 40, 8)));
        colony.Colonists.StoreItems(food, 9);

        int colonist = colony.TrySpawnColonist(new Vector3i(2, 2, 4));
        Assert.True(colonist >= 0);
        colony.Colonists.SetHunger(colonist, ColonySimulation.HungryAt + 100);

        colony.MarkArea(world, world.Registry, new Vector3i(4, 2, 4), new Vector3i(7, 2, 4));

        for (int tick = 0; tick < 6_000 && colony.Jobs.DoneCount == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        output.WriteLine($"Vykopano {colony.Jobs.DoneCount}, snedeno {colony.Colonists.MealsEaten}, "
            + $"hlad {colony.Colonists.HungerOf(colonist)}.");

        // JÁDRO: nezůstal viset na nedosažitelném jídle, ale šel dělat práci.
        Assert.Equal(0, colony.Colonists.MealsEaten);
        Assert.True(colony.Jobs.DoneCount > 0, "Kolonista se zacyklil na nedosažitelném skladu a nekopal.");
    }
}
