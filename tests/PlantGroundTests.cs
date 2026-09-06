using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy vazby rostliny na podklad.
///
/// <para>Rostlina smí stát jen na tom, z čeho roste — tráva z trávníku, chaluha z písku na
/// dně. Kdyby to platilo jen pro pokládání a ne pro generování, měl by svět od začátku plný
/// rostlin, které se při prvním doteku rozpadnou.</para>
/// </summary>
public sealed class PlantGroundTests
{
    private const int Seed = 20260727;

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    [Fact]
    public void Travu_nelze_polozit_na_kamen()
    {
        BlockRegistry registry = Registry();

        ushort grass = registry.IndexOf("tesseris:grass");
        ushort stone = registry.IndexOf("tesseris:stone");
        ushort shortGrass = registry.IndexOf("tesseris:short_grass");

        Assert.True(registry.CanStandOn(shortGrass, grass));
        Assert.False(registry.CanStandOn(shortGrass, stone));
        Assert.False(registry.CanStandOn(shortGrass, BlockRegistry.Air));
    }

    /// <summary>Kámen ani hlína na podkladu nezávisí — postavit se dají kamkoli.</summary>
    [Fact]
    public void Obycejny_blok_zadny_podklad_nepotrebuje()
    {
        BlockRegistry registry = Registry();

        ushort stone = registry.IndexOf("tesseris:stone");

        Assert.False(registry.NeedsGround(stone));
        Assert.True(registry.CanStandOn(stone, BlockRegistry.Air));
    }

    /// <summary>Vícepatrová rostlina se smí opřít sama o sebe, jinak by stvol nedržel.</summary>
    [Fact]
    public void Chaluha_stoji_i_sama_na_sobe()
    {
        BlockRegistry registry = Registry();

        ushort kelp = registry.IndexOf("tesseris:kelp");
        ushort sand = registry.IndexOf("tesseris:sand");

        Assert.True(registry.CanStandOn(kelp, sand));
        Assert.True(registry.CanStandOn(kelp, kelp));
    }

    /// <summary>Rostlina bez podkladu se rozpadne, a to i celý sloupec nad ním.</summary>
    [Fact]
    public void Rostlina_bez_podkladu_se_rozpadne()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort grass = registry.IndexOf("tesseris:grass");
        ushort shortGrass = registry.IndexOf("tesseris:short_grass");

        world.SetBlock(0, 10, 0, grass);
        world.SetBlock(0, 11, 0, shortGrass);

        // Dokud podklad drží, rostlina zůstane.
        Assert.Equal(0, world.DropUnsupportedPlants(0, 10, 0));
        Assert.Equal(shortGrass, world.GetBlock(0, 11, 0));

        // Odtěžený podklad ji sesype.
        world.SetBlock(0, 10, 0, BlockRegistry.Air);

        Assert.Equal(1, world.DropUnsupportedPlants(0, 10, 0));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(0, 11, 0));
    }

    /// <summary>Sesype se celý sloupec chaluhy, ne jen její kořen.</summary>
    [Fact]
    public void Sesype_se_cely_sloupec_rostliny()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort sand = registry.IndexOf("tesseris:sand");
        ushort kelp = registry.IndexOf("tesseris:kelp");
        ushort water = registry.IndexOf("tesseris:water");

        world.SetBlock(0, 10, 0, sand);

        for (int i = 1; i <= 4; i++)
        {
            world.SetBlock(0, 10 + i, 0, kelp);
        }

        world.SetBlock(0, 10, 0, BlockRegistry.Air);
        var collapsed = new List<(Vector3i At, ushort Block)>();

        Assert.Equal(4, world.DropUnsupportedPlants(
            0, 10, 0, (at, block) => collapsed.Add((at, block))));
        Assert.Equal(4, collapsed.Count);

        for (int i = 1; i <= 4; i++)
        {
            Assert.Equal(water, world.GetBlock(0, 10 + i, 0));
            Assert.True(world.ContainsWater(0, 10 + i, 0));
            Assert.Equal((new Vector3i(0, 10 + i, 0), kelp), collapsed[i - 1]);
        }
    }

    /// <summary>
    /// Generátor nesází rostliny na špatný podklad.
    ///
    /// <para>Bez tohohle by svět vznikal už rozbitý: rostliny by stály na kameni a rozpadly
    /// by se při prvním doteku okolí.</para>
    /// </summary>
    [Fact]
    public void Generator_sazi_rostliny_jen_na_jejich_podklad()
    {
        BlockRegistry registry = Registry();
        var generator = new TerrainGenerator(registry, Seed);
        generator.EnableTrees(registry);

        int checkedPlants = 0;

        for (int cz = -1; cz <= 1; cz++)
        {
            for (int cx = -1; cx <= 1; cx++)
            {
                for (int cy = 9; cy <= 11; cy++)
                {
                    var chunk = new Chunk();
                    generator.Generate(chunk, new Vector3i(cx, cy, cz));

                    for (int y = 1; y < Chunk.Size; y++)
                    {
                        for (int z = 0; z < Chunk.Size; z++)
                        {
                            for (int x = 0; x < Chunk.Size; x++)
                            {
                                ushort plant = chunk.GetBlock(x, y, z);

                                if (!registry.NeedsGround(plant))
                                {
                                    continue;
                                }

                                checkedPlants++;

                                ushort ground = chunk.GetBlock(x, y - 1, z);

                                Assert.True(
                                    registry.CanStandOn(plant, ground),
                                    $"'{registry.Definition(plant).Id}' na [{x}, {y}, {z}] stojí na "
                                    + $"'{registry.Definition(ground).Id}', z čeho růst nemůže.");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(checkedPlants > 0, "V prohledané oblasti není jediná rostlina, test nic neověřil.");
    }

    /// <summary>
    /// Ani ve složeném světě nesmí rostlina viset ve vzduchu.
    ///
    /// <para>Na rozdíl od kontroly uvnitř jednoho chunku se tady skládá celá oblast, takže
    /// se prověří i rostliny na spodní hranici chunku, kterým podklad patří sousedovi —
    /// přesně tam by se visící rostlina schovala.</para>
    /// </summary>
    [Fact]
    public void Ve_slozenem_svete_nevisi_zadna_rostlina()
    {
        BlockRegistry registry = Registry();
        var generator = new TerrainGenerator(registry, Seed);
        generator.EnableTrees(registry);

        var world = new VoxelWorld(registry);

        for (int cz = -1; cz <= 1; cz++)
        {
            for (int cx = -1; cx <= 1; cx++)
            {
                for (int cy = 8; cy <= 12; cy++)
                {
                    var position = new Vector3i(cx, cy, cz);
                    var chunk = new Chunk();
                    generator.Generate(chunk, position);
                    world.TryAddChunk(position, chunk);
                }
            }
        }

        int floating = 0;
        string first = string.Empty;

        for (int y = 8 * Chunk.Size + 1; y < 13 * Chunk.Size; y++)
        {
            for (int z = -Chunk.Size; z < 2 * Chunk.Size; z++)
            {
                for (int x = -Chunk.Size; x < 2 * Chunk.Size; x++)
                {
                    ushort plant = world.GetBlock(x, y, z);

                    if (!registry.NeedsGround(plant))
                    {
                        continue;
                    }

                    ushort ground = world.GetBlock(x, y - 1, z);

                    if (registry.CanStandOn(plant, ground))
                    {
                        continue;
                    }

                    if (floating == 0)
                    {
                        first = $"'{registry.Definition(plant).Id}' na [{x}, {y}, {z}] stojí na "
                            + $"'{registry.Definition(ground).Id}'";
                    }

                    floating++;
                }
            }
        }

        Assert.True(floating == 0, $"Ve vzduchu visí {floating} rostlin, například {first}.");
    }

    /// <summary>
    /// Nově položený blok nezdědí nic po tom, co na jeho místě bylo předtím.
    ///
    /// <para>Vytěžíš kmen a postavíš tam jiný blok: bez tohohle by zdědil masku dílků
    /// i listí, které kolem kmene bylo ve druhé vrstvě, a vznikl by z něj děravý útvar
    /// s cizí texturou.</para>
    /// </summary>
    [Fact]
    public void Novy_blok_nezdedi_dilky_ani_druhou_vrstvu()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort log = registry.IndexOf("tesseris:oak_log");
        ushort leaves = registry.IndexOf("tesseris:oak_leaves");
        ushort stone = registry.IndexOf("tesseris:stone");

        world.SetBlock(4, 20, 4, log);

        Chunk chunk = world.GetChunk(VoxelWorld.ToChunkPosition(4, 20, 4))!;
        chunk.SetPieces(4, 20 & Chunk.SizeMask, 4, PieceMask.With(PieceMask.Empty, 0, 0, 0));
        chunk.SetExtra(4, 20 & Chunk.SizeMask, 4, leaves, PieceMask.With(PieceMask.Empty, 1, 1, 1));

        world.SetBlock(4, 20, 4, stone);

        Assert.Equal(PieceMask.Full, world.GetPieces(4, 20, 4));
        Assert.True(world.GetExtra(4, 20, 4).IsEmpty);
    }
}
