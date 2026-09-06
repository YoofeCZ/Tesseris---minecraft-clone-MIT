using System.Buffers.Binary;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Xunit;

namespace Tesseris.Tests;

public sealed class BuildingPaletteContentTests
{
    private static readonly string[] WoodSpecies =
        ["oak", "spruce", "birch", "acacia", "maple"];

    private static readonly string[] ShapedRoots =
    [
        "oak_planks", "spruce_planks", "birch_planks", "acacia_planks", "maple_planks",
        "granite", "diorite", "andesite", "limestone", "marble", "basalt",
        "polished_granite", "polished_limestone", "polished_marble",
        "stone_bricks", "deepslate_bricks", "deepslate_tiles",
        "smooth_sandstone", "cut_sandstone", "chiseled_sandstone", "sandstone_bricks",
        "smooth_red_sandstone", "cut_red_sandstone", "chiseled_red_sandstone",
        "red_sandstone_bricks", "bricks", "mud_bricks", "red_roof_tiles", "slate_shingles",
    ];

    private static readonly string[] FullOnlyRoots =
        ["iron_block", "copper_block", "oxidized_copper", "corrugated_metal", "smoked_glass"];

    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "assets");

    [Fact]
    public void Stavebni_paleta_obsahuje_vsech_102_novych_bloku()
    {
        BlockRegistry blocks = LoadBlocks();
        var expected = new HashSet<string>(StringComparer.Ordinal);

        foreach (string species in WoodSpecies)
        {
            expected.Add($"tesseris:{species}_wood");
            expected.Add($"tesseris:{species}_beam");
        }

        foreach (string root in ShapedRoots)
        {
            expected.Add($"tesseris:{root}");
            expected.Add($"tesseris:{root}_slab");
            expected.Add($"tesseris:{root}_stairs");
        }

        foreach (string root in FullOnlyRoots)
        {
            expected.Add($"tesseris:{root}");
        }

        Assert.Equal(102, expected.Count);
        Assert.All(expected, id => Assert.NotEqual(BlockRegistry.Air, blocks.IndexOf(id)));
    }

    [Fact]
    public void Slaby_a_schody_sdileji_texturu_materialu_a_spravnou_masku()
    {
        BlockRegistry blocks = LoadBlocks();

        foreach (string root in ShapedRoots)
        {
            ushort full = blocks.IndexOf($"tesseris:{root}");
            ushort slab = blocks.IndexOf($"tesseris:{root}_slab");
            ushort stairs = blocks.IndexOf($"tesseris:{root}_stairs");

            Assert.Equal(blocks.Definition(full).Texture, blocks.Definition(slab).Texture);
            Assert.Equal(blocks.Definition(full).Texture, blocks.Definition(stairs).Texture);
            Assert.Equal(PieceMask.SlabBottom, blocks.DefaultPieces(slab));
            Assert.Equal((byte)63, blocks.DefaultPieces(stairs));
        }
    }

    [Fact]
    public void Kazdy_novy_blok_ma_automaticky_polozitelny_item()
    {
        BlockRegistry blocks = LoadBlocks();
        ItemRegistry items = ItemRegistry.Create(
            blocks,
            Path.Combine(Assets, "items"));

        IEnumerable<string> blockIds = ShapedRoots.SelectMany(root => new[]
            {
                $"tesseris:{root}",
                $"tesseris:{root}_slab",
                $"tesseris:{root}_stairs",
            })
            .Concat(WoodSpecies.SelectMany(species => new[]
            {
                $"tesseris:{species}_wood",
                $"tesseris:{species}_beam",
            }))
            .Concat(FullOnlyRoots.Select(root => $"tesseris:{root}"));

        foreach (string id in blockIds)
        {
            int item = items.IndexOf(id);
            Assert.NotEqual(ItemRegistry.Nothing, item);
            Assert.Equal(blocks.IndexOf(id), items.BlockForItem(item));
        }
    }

    [Fact]
    public void Inventar_dostane_skutecny_tvar_plneho_bloku_slabu_i_schodu()
    {
        BlockRegistry blocks = LoadBlocks();
        ItemRegistry items = ItemRegistry.Create(blocks, Path.Combine(Assets, "items"));

        Assert.True(items.TryGetBlockIcon(items.IndexOf("tesseris:oak_planks"), out BlockItemIcon full));
        Assert.True(items.TryGetBlockIcon(items.IndexOf("tesseris:oak_planks_slab"), out BlockItemIcon slab));
        Assert.True(items.TryGetBlockIcon(items.IndexOf("tesseris:oak_planks_stairs"), out BlockItemIcon stairs));

        Assert.Equal(PieceMask.Full, full.Pieces);
        Assert.Equal(PieceMask.SlabBottom, slab.Pieces);
        Assert.Equal((byte)63, stairs.Pieces);
        Assert.NotEqual(full.Pieces, slab.Pieces);
        Assert.NotEqual(slab.Pieces, stairs.Pieces);
    }

    [Fact]
    public void Sazenice_maji_druh_a_lamany_kamen_zachovava_stare_id()
    {
        BlockRegistry blocks = LoadBlocks();
        ItemRegistry items = ItemRegistry.Create(blocks, Path.Combine(Assets, "items"));

        Assert.Equal("dubovĂˇ sazenice", items.Definition(items.IndexOf("tesseris:oak_sapling")).Name);
        Assert.Equal("smrkovĂˇ sazenice", items.Definition(items.IndexOf("tesseris:spruce_sapling")).Name);
        Assert.Equal("bĹ™ezovĂˇ sazenice", items.Definition(items.IndexOf("tesseris:birch_sapling")).Name);
        Assert.Equal("akĂˇciovĂˇ sazenice", items.Definition(items.IndexOf("tesseris:acacia_sapling")).Name);
        Assert.Equal("javorovĂˇ sazenice", items.Definition(items.IndexOf("tesseris:maple_sapling")).Name);

        int fractured = items.IndexOf("tesseris:cobblestone");
        Assert.NotEqual(ItemRegistry.Nothing, fractured);
        Assert.Equal("lĂˇmanĂ˝ kĂˇmen", items.Definition(fractured).Name);
        Assert.Equal("fractured_stone", blocks.Definition(blocks.IndexOf("tesseris:cobblestone")).Texture);
    }

    [Fact]
    public void Sterk_a_lamany_kamen_maji_odlisne_skutecne_textury()
    {
        string textures = Path.Combine(Assets, "textures");
        string gravel = Path.Combine(textures, "gravel.png");
        string fractured = Path.Combine(textures, "fractured_stone.png");

        Assert.True(File.Exists(gravel));
        Assert.True(File.Exists(fractured));
        Assert.Equal((64, 64), ReadPngSize(gravel));
        Assert.Equal((64, 64), ReadPngSize(fractured));
        Assert.NotEqual(File.ReadAllBytes(gravel), File.ReadAllBytes(fractured));
    }

    [Fact]
    public void Vsechny_nove_materialy_maji_skutecnou_64x64_png_texturu()
    {
        string textures = Path.Combine(Assets, "textures");
        IEnumerable<string> roots = ShapedRoots
            .Concat(FullOnlyRoots)
            .Concat(WoodSpecies.Select(species => $"{species}_planks"))
            .Distinct(StringComparer.Ordinal);

        foreach (string root in roots)
        {
            string path = Path.Combine(textures, $"{root}.png");
            Assert.True(File.Exists(path), $"Chybi textura {path}.");
            Assert.Equal((64, 64), ReadPngSize(path));
        }
    }

    private static BlockRegistry LoadBlocks() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(Assets, "blocks"));

    private static (int Width, int Height) ReadPngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using FileStream stream = File.OpenRead(path);
        stream.ReadExactly(header);

        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(header[..8].SequenceEqual(signature), $"{path} neni PNG.");
        return (
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }
}
