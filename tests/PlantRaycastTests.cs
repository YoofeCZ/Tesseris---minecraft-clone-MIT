using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy míření na rostliny.
///
/// <para>Trs trávy jsou dvě svislé plochy po úhlopříčkách bloku, ne kostka. Dokud ho paprsek
/// bral jako kostku, zničil hráč kytku i tím, že zamířil vedle ní na něco za ní — a přesně
/// to je chování, které tenhle test hlídá.</para>
/// </summary>
public sealed class PlantRaycastTests
{
    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    /// <summary>Svět s jedním trsem trávy na trávníku a kamennou zdí za ním.</summary>
    private static VoxelWorld WorldWithPlant(BlockRegistry registry, out Vector3i plant)
    {
        var world = new VoxelWorld(registry);

        ushort grass = registry.IndexOf("tesseris:grass");
        ushort stone = registry.IndexOf("tesseris:stone");
        ushort shortGrass = registry.IndexOf("tesseris:short_grass");

        plant = new Vector3i(8, 11, 8);

        world.SetBlock(8, 10, 8, grass);
        world.SetBlock(plant.X, plant.Y, plant.Z, shortGrass);

        // Zeď o dva bloky dál, aby bylo na co mířit „za" kytkou.
        for (int y = 10; y <= 13; y++)
        {
            for (int z = 6; z <= 10; z++)
            {
                world.SetBlock(11, y, z, stone);
            }
        }

        return world;
    }

    /// <summary>Míření přímo do středu trsu ho trefí.</summary>
    [Fact]
    public void Paprsek_do_stredu_trefi_travu()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = WorldWithPlant(registry, out Vector3i plant);

        var origin = new Vector3(plant.X + 0.5f - 3f, plant.Y + 0.5f, plant.Z + 0.5f);

        Assert.True(MicroRaycast.Cast(world, origin, Vector3.UnitX, 20f, out MicroHit hit));
        Assert.Equal(plant, hit.Block);
    }

    /// <summary>
    /// Míření rohem bloku trávu mine a trefí až zeď za ní.
    ///
    /// <para>Trs je zatažený od rohů bloku, takže roh je prázdný — a přesně tam dřív paprsek
    /// kytku zničil, i když na ni hráč nemířil.</para>
    /// </summary>
    [Fact]
    public void Paprsek_rohem_bloku_travu_mine()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = WorldWithPlant(registry, out Vector3i plant);

        ushort stone = registry.IndexOf("tesseris:stone");

        // Těsně u rohu bloku, kde plocha trsu není.
        var origin = new Vector3(plant.X + 0.5f - 3f, plant.Y + 0.02f, plant.Z + 0.02f);

        Assert.True(MicroRaycast.Cast(world, origin, Vector3.UnitX, 20f, out MicroHit hit));

        Assert.NotEqual(plant, hit.Block);
        Assert.Equal(stone, world.GetBlock(hit.Block.X, hit.Block.Y, hit.Block.Z));
    }

    /// <summary>
    /// Míření nad trs ho mine. Trs je nižší než blok, takže nad ním zbývá vzduch.
    /// </summary>
    [Fact]
    public void Paprsek_nad_trsem_travu_mine()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = WorldWithPlant(registry, out Vector3i plant);

        // Nad horní hranou trsu: ten sahá nejvýš do 100 % bloku, tady se míří o kus výš.
        var origin = new Vector3(plant.X + 0.5f - 3f, plant.Y + 1.5f, plant.Z + 0.5f);

        bool found = MicroRaycast.Cast(world, origin, Vector3.UnitX, 20f, out MicroHit hit);

        Assert.True(!found || hit.Block != plant);
    }
}
