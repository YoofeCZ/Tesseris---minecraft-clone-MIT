using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>Regrese cílení, nahrazování vegetace a bezpečného zápisu při stavění.</summary>
public sealed class BlockPlacementTests
{
    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    /// <summary>
    /// Běžný blok mířený na trs nahradí trs, ne obsazený voxel vedle něj.
    /// </summary>
    [Fact]
    public void Pevny_blok_nahradi_zasazenou_travu()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort grass = registry.IndexOf("tesseris:grass");
        ushort shortGrass = registry.IndexOf("tesseris:short_grass");
        ushort dirt = registry.IndexOf("tesseris:dirt");
        ushort stone = registry.IndexOf("tesseris:stone");

        var plant = new Vector3i(8, 11, 8);
        var misleadingNeighbour = new Vector3i(7, 11, 8);

        world.SetBlock(plant.X, plant.Y - 1, plant.Z, grass);
        world.SetBlock(plant.X, plant.Y, plant.Z, shortGrass);
        world.SetBlock(misleadingNeighbour.X, misleadingNeighbour.Y, misleadingNeighbour.Z, stone);

        Vector3i target = world.PlacementTarget(plant, new Vector3i(-1, 0, 0), dirt);

        Assert.Equal(plant, target);
        Assert.True(world.TryPlace(dirt, target.X, target.Y, target.Z, out ushort replaced));
        Assert.Equal(shortGrass, replaced);
        Assert.Equal(dirt, world.GetBlock(plant.X, plant.Y, plant.Z));
        Assert.Equal(stone, world.GetBlock(
            misleadingNeighbour.X, misleadingNeighbour.Y, misleadingNeighbour.Z));
    }

    /// <summary>
    /// Květina používá normálu povrchu a nenahradí zasaženou vegetaci jako stavební blok.
    /// </summary>
    [Fact]
    public void Pokladana_rostlina_zachova_pripojeni_k_povrchu()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort grass = registry.IndexOf("tesseris:grass");
        ushort shortGrass = registry.IndexOf("tesseris:short_grass");
        ushort flower = registry.IndexOf("tesseris:flower_red");

        var struck = new Vector3i(8, 11, 8);
        var expected = new Vector3i(9, 11, 8);

        world.SetBlock(struck.X, struck.Y - 1, struck.Z, grass);
        world.SetBlock(expected.X, expected.Y - 1, expected.Z, grass);
        world.SetBlock(struck.X, struck.Y, struck.Z, shortGrass);

        Vector3i target = world.PlacementTarget(struck, Vector3i.UnitX, flower);

        Assert.Equal(expected, target);
        Assert.True(world.TryPlace(flower, target.X, target.Y, target.Z));
        Assert.Equal(shortGrass, world.GetBlock(struck.X, struck.Y, struck.Z));
        Assert.Equal(flower, world.GetBlock(expected.X, expected.Y, expected.Z));
    }

    /// <summary>Obsazený pevný voxel se nepřepíše a pokus se ohlásí jako neúspěšný.</summary>
    [Fact]
    public void Obsazeny_voxel_odmitne_polozeni_bez_zmeny()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort stone = registry.IndexOf("tesseris:stone");
        ushort dirt = registry.IndexOf("tesseris:dirt");

        world.SetBlock(4, 5, 6, stone);

        Assert.False(world.TryPlace(dirt, 4, 5, 6, out ushort rejected));
        Assert.Equal(stone, rejected);
        Assert.False(world.TryPlace(stone, 4, 5, 6));
        Assert.Equal(stone, world.GetBlock(4, 5, 6));
    }

    /// <summary>Voda i průchozí vegetace jsou nahraditelné, pevný blok a kaktus ne.</summary>
    [Fact]
    public void Registry_rozlisuje_nahraditelny_obsah_voxelu()
    {
        BlockRegistry registry = Registry();

        Assert.True(registry.IsReplaceable(BlockRegistry.Air));
        Assert.True(registry.IsReplaceable(registry.IndexOf("tesseris:water")));
        Assert.True(registry.IsReplaceable(registry.IndexOf("tesseris:short_grass")));
        Assert.False(registry.IsReplaceable(registry.IndexOf("tesseris:stone")));
        Assert.False(registry.IsReplaceable(registry.IndexOf("tesseris:cactus")));
    }

    /// <summary>Každá nahraditelná rostlina má předmět, který po rozbití může vypadnout.</summary>
    [Fact]
    public void Nahraditelna_vegetace_ma_vlastni_predmetovy_drop()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = ItemRegistry.Create(
            blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

        int checkedPlants = 0;

        for (ushort block = 1; block < blocks.Count; block++)
        {
            if (!blocks.IsReplaceableVegetation(block))
            {
                continue;
            }

            checkedPlants++;
            int item = items.ItemForBlock(block);

            Assert.NotEqual(ItemRegistry.Nothing, item);
            Assert.Equal(block, items.BlockForItem(item));
        }

        Assert.True(checkedPlants > 0, "Registr neobsahuje žádnou nahraditelnou vegetaci.");
    }

    [Fact]
    public void Vodni_rostlina_jde_jen_do_vody_a_po_zniceni_vodu_okamzite_odkryje()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        ushort sand = registry.IndexOf("tesseris:sand");
        ushort grass = registry.IndexOf("tesseris:grass");
        ushort water = registry.IndexOf("tesseris:water");
        ushort seagrass = registry.IndexOf("tesseris:seagrass");
        ushort flower = registry.IndexOf("tesseris:flower_red");
        ushort stone = registry.IndexOf("tesseris:stone");

        world.SetBlock(0, 0, 0, sand);
        Assert.False(world.TryPlace(seagrass, 0, 1, 0));

        world.SetBlock(4, 0, 0, sand);
        world.SetBlock(4, 1, 0, water);
        Vector3i flowingChunk = VoxelWorld.ToChunkPosition(4, 1, 0);
        int flowingIndex = Chunk.LocalIndex(4, 1, 0);
        world.ApplyFluidBatch(
            flowingChunk, [(flowingIndex, water, FluidCell.MaxFlowing)]);
        Assert.False(world.TryPlace(seagrass, 4, 1, 0));

        world.SetBlock(1, 0, 0, sand);
        world.SetBlock(1, 1, 0, water);
        Assert.True(world.TryPlace(seagrass, 1, 1, 0));
        Assert.Equal(seagrass, world.GetBlock(1, 1, 0));
        Assert.True(world.ContainsWater(1, 1, 0));

        world.SetBlock(1, 1, 0, BlockRegistry.Air);
        world.SetMicro(1, 1, 0, null); // Stejné pořadí jako skutečná hráčská bourací cesta.
        Assert.Equal(water, world.GetBlock(1, 1, 0));
        Assert.True(world.ContainsWater(1, 1, 0));
        Assert.Equal(FluidCell.Source, world.GetFluid(1, 1, 0));

        world.SetBlock(2, 0, 0, grass);
        world.SetBlock(2, 1, 0, water);
        Assert.False(world.TryPlace(flower, 2, 1, 0));

        world.SetBlock(3, 0, 0, sand);
        world.SetBlock(3, 1, 0, water);
        Assert.True(world.TryPlace(stone, 3, 1, 0));
        Assert.False(world.ContainsWater(3, 1, 0));
    }
}
