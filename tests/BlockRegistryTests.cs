using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy registry bloků. Nejdůležitější je stabilita očíslování — na ní bude ve F6 stát
/// mapování uložené v save souboru.
/// </summary>
public sealed class BlockRegistryTests
{
    private static BlockDefinition Stone() => new() { Id = "test:stone", Texture = "stone" };

    private static BlockDefinition Dirt() => new() { Id = "test:dirt", Texture = "dirt" };

    private static BlockDefinition Grass() => new()
    {
        Id = "test:grass",
        Texture = "dirt",
        Faces = new Dictionary<string, string> { ["top"] = "grass_top", ["side"] = "grass_side" },
    };

    [Fact]
    public void Vzduch_ma_vzdy_index_nula_a_neni_nepruhledny()
    {
        BlockRegistry registry = BlockRegistry.Create([Stone()]);

        Assert.Equal(0, BlockRegistry.Air);
        Assert.True(registry.IsAir(0));
        Assert.False(registry.IsOpaque(0));
        Assert.Equal("tesseris:air", registry.Definition(0).Id);
    }

    [Fact]
    public void Ocislovani_nezavisi_na_poradi_vstupu()
    {
        BlockRegistry first = BlockRegistry.Create([Stone(), Dirt(), Grass()]);
        BlockRegistry second = BlockRegistry.Create([Grass(), Stone(), Dirt()]);

        foreach (string id in new[] { "test:stone", "test:dirt", "test:grass" })
        {
            Assert.Equal(first.IndexOf(id), second.IndexOf(id));
        }
    }

    [Fact]
    public void Vrstvy_textur_jsou_taky_stabilni()
    {
        BlockRegistry first = BlockRegistry.Create([Stone(), Grass()]);
        BlockRegistry second = BlockRegistry.Create([Grass(), Stone()]);

        Assert.Equal(first.TextureNames, second.TextureNames);
    }

    [Fact]
    public void Steny_bez_vlastni_textury_berou_vychozi()
    {
        BlockRegistry registry = BlockRegistry.Create([Stone()]);
        ushort stone = registry.IndexOf("test:stone");

        int expected = registry.TextureNames.ToList().IndexOf("stone");

        foreach (BlockFace face in Enum.GetValues<BlockFace>())
        {
            Assert.Equal(expected, registry.FaceLayer(stone, face));
        }
    }

    [Fact]
    public void Vrsek_a_boky_muzou_mit_vlastni_texturu()
    {
        BlockRegistry registry = BlockRegistry.Create([Grass()]);
        ushort grass = registry.IndexOf("test:grass");

        var names = registry.TextureNames.ToList();

        Assert.Equal(names.IndexOf("grass_top"), registry.FaceLayer(grass, BlockFace.PosY));
        Assert.Equal(names.IndexOf("dirt"), registry.FaceLayer(grass, BlockFace.NegY));

        foreach (BlockFace side in new[] { BlockFace.NegX, BlockFace.PosX, BlockFace.NegZ, BlockFace.PosZ })
        {
            Assert.Equal(names.IndexOf("grass_side"), registry.FaceLayer(grass, side));
        }
    }

    [Fact]
    public void Duplicitni_identifikator_je_odmitnut()
    {
        Assert.Throws<InvalidDataException>(() => BlockRegistry.Create([Stone(), Stone()]));
    }

    [Fact]
    public void Neznamy_identifikator_vyhodi_vyjimku()
    {
        BlockRegistry registry = BlockRegistry.Create([Stone()]);

        Assert.Throws<KeyNotFoundException>(() => registry.IndexOf("test:neexistuje"));
    }

    [Fact]
    public void Tabulka_nepruhlednosti_odpovida_definicim()
    {
        BlockRegistry registry = BlockRegistry.Create(
        [
            Stone(),
            new BlockDefinition { Id = "test:glass", Texture = "glass", Opaque = false },
        ]);

        Assert.True(registry.OpacityTable[registry.IndexOf("test:stone")]);
        Assert.False(registry.OpacityTable[registry.IndexOf("test:glass")]);
        Assert.False(registry.OpacityTable[BlockRegistry.Air]);
    }

    [Fact]
    public void Definice_z_assetu_se_nactou_a_dava_smysl()
    {
        // Assety se kopírují vedle testovací binárky přes referenci na projekt Game.
        string directory = Path.Combine(AppContext.BaseDirectory, "assets", "blocks");
        Assert.True(Directory.Exists(directory), $"Chybí adresář s definicemi bloků: {directory}");

        BlockRegistry registry = BlockRegistry.LoadFromDirectory(directory);

        Assert.True(registry.Count > 1);
        Assert.True(registry.IsOpaque(registry.IndexOf("tesseris:stone")));
        Assert.False(registry.IsOpaque(registry.IndexOf("tesseris:glass")));
        Assert.Equal(BlockMaterial.Glass, registry.Definition(registry.IndexOf("tesseris:glass")).Material);
        Assert.False(registry.Definition(registry.IndexOf("tesseris:glass")).Chiselable);
    }

    [Fact]
    public void HyProTech_stroje_maji_nactenou_geometrii()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "assets", "blocks");
        BlockRegistry registry = BlockRegistry.LoadFromDirectory(directory);

        foreach (string id in new[]
        {
            "tesseris:furnace",
            "tesseris:stone_furnace",
            "tesseris:alloy_smelter",
            "tesseris:battery_rack_large",
            "tesseris:battery_rack_small",
            "tesseris:border_torch",
            "tesseris:machine_frame",
            "tesseris:reinforced_machine_frame",
            "tesseris:ore_crusher",
            "tesseris:metal_press",
            "tesseris:quarry",
            "tesseris:solar_panel",
            "tesseris:wind_turbine",
        })
        {
            ushort block = registry.IndexOf(id);
            Assert.Equal(BlockShape.HytaleModel, registry.ShapeOf(block));
            Assert.NotNull(registry.HytaleModelOf(block));
            Assert.NotEmpty(registry.HytaleFacesOf(block)!);
        }
    }

    [Fact]
    public void Modelove_textury_se_do_gpu_atlasu_skaluji_celym_nasobkem()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "assets", "blocks");
        BlockRegistry registry = BlockRegistry.LoadFromDirectory(directory);

        for (ushort block = 1; block < registry.Count; block++)
        {
            BlockDefinition definition = registry.Definition(block);
            if (definition.Shape != BlockShape.HytaleModel)
            {
                continue;
            }

            Assert.Equal(definition.ModelTextureWidth, definition.ModelTextureHeight);
            Assert.True(
                definition.ModelTextureWidth > 0
                && TextureArray.TileSize % definition.ModelTextureWidth == 0,
                $"{definition.Id}: {definition.ModelTextureWidth}px atlas se nevejde bezeztratove do {TextureArray.TileSize}px GPU vrstvy.");
        }
    }

    [Fact]
    public void Vodni_rostliny_obsahuji_vodu_ale_nejsou_kapalina()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "assets", "blocks");
        BlockRegistry registry = BlockRegistry.LoadFromDirectory(directory);

        foreach (string id in new[]
        {
            "tesseris:seagrass",
            "tesseris:kelp",
            "tesseris:red_algae",
            "tesseris:pale_weed",
            "tesseris:sea_fern",
        })
        {
            ushort block = registry.IndexOf(id);
            Assert.True(registry.IsAquatic(block), id);
            Assert.True(registry.ContainsWater(block), id);
            Assert.False(registry.IsLiquid(block), id);
        }
    }
}
