using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy rostlin: trávy, kapradí a kytek.
///
/// <para>Rostliny nejsou krychle, ale dvě zkřížené plochy, a hlavně se jimi <b>dá projít</b>.
/// Obojí je snadné rozbít: kdyby se braly jako plný blok, hráč by po každém stéblu
/// vyskočil o blok nahoru, a kdyby prošly greedy meshingem, byla by z každého stébla
/// průhledná krychle.</para>
/// </summary>
public sealed class PlantTests
{
    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    [Fact]
    public void Rostlinou_se_da_projit()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort grass = registry.IndexOf("tesseris:short_grass");
        ushort stone = registry.IndexOf("tesseris:stone");

        world.SetBlock(10, 40, 10, grass);
        world.SetBlock(10, 39, 10, stone);

        Assert.False(world.IsSolid(10, 40, 10), "Trávou se má dát projít.");
        Assert.True(world.IsSolid(10, 39, 10), "Do kamene se narazit musí.");
    }

    /// <summary>
    /// Sklo je průhledné, ale narazit se do něj dá. Průhlednost a průchodnost jsou dvě
    /// různé vlastnosti a nesmí se slít do jedné.
    /// </summary>
    [Fact]
    public void Prusvitnost_a_prochodnost_jsou_dve_ruzne_veci()
    {
        BlockRegistry registry = Registry();

        ushort glass = registry.IndexOf("tesseris:glass");
        ushort grass = registry.IndexOf("tesseris:short_grass");

        Assert.False(registry.IsOpaque(glass));
        Assert.True(registry.IsSolid(glass));

        Assert.False(registry.IsOpaque(grass));
        Assert.False(registry.IsSolid(grass));
    }

    /// <summary>
    /// Rostlina nesmí projít greedy meshingem jako krychle: patří do průhledného bufferu
    /// jako zkřížené plochy a v neprůhledném nemá co dělat.
    /// </summary>
    [Fact]
    public void Rostlina_se_kresli_jako_krizene_plochy()
    {
        BlockRegistry registry = Registry();
        ushort plant = registry.IndexOf("tesseris:tall_grass");

        var padded = new ushort[ChunkMesher.PaddedVolume];
        Array.Fill(padded, BlockRegistry.Air);
        padded[ChunkMesher.PaddedIndex(5, 5, 5)] = plant;

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();
        var cutout = new MeshBuffer();

        ChunkMesher.Build(padded, registry, opaque, transparent, default, cutout);

        Assert.True(opaque.IsEmpty, "Rostlina do neprůhledného průchodu nepatří.");

        // Ani do průhledného: rostlina je výřez, ne sklo. Kdyby šla tam, kreslila by se
        // bez zápisu do hloubky a stébla by se navzájem nezakrývala.
        Assert.True(transparent.IsEmpty, "Rostlina do průhledného průchodu nepatří.");

        // Dvě plochy, každá z obou stran, tedy čtyři obdélníky = osm trojúhelníků.
        Assert.Equal(4 * 6, cutout.IndexCount);
    }

    /// <summary>
    /// Rostlina nesmí zakrývat stěny sousedů. Kdyby zakrývala, byla by pod ní v terénu
    /// díra — mesher by usoudil, že vrchní stěnu bloku pod ní není vidět.
    /// </summary>
    [Fact]
    public void Rostlina_nezakryva_teren_pod_sebou()
    {
        BlockRegistry registry = Registry();
        ushort plant = registry.IndexOf("tesseris:short_grass");
        ushort stone = registry.IndexOf("tesseris:stone");

        var padded = new ushort[ChunkMesher.PaddedVolume];
        Array.Fill(padded, BlockRegistry.Air);
        padded[ChunkMesher.PaddedIndex(5, 4, 5)] = stone;
        padded[ChunkMesher.PaddedIndex(5, 5, 5)] = plant;

        var opaque = new MeshBuffer();
        var transparent = new MeshBuffer();

        ChunkMesher.Build(padded, registry, opaque, transparent);

        // Osamocená kostka má šest stěn. Kdyby rostlina zakrývala, byla by jich pět.
        Assert.Equal(6 * 6, opaque.IndexCount);
    }

    /// <summary>Ve světě má na trávě něco růst, jinak je celá práce k ničemu.</summary>
    [Fact]
    public void Ve_svete_rostou_rostliny()
    {
        BlockRegistry registry = Registry();
        var generator = new TerrainGenerator(registry, seed: 20260727);
        generator.EnableTrees(registry);

        ushort shortGrass = registry.IndexOf("tesseris:short_grass");
        ushort grassBlock = registry.IndexOf("tesseris:grass");

        int plants = 0;
        int onGrass = 0;

        for (int cx = -2; cx <= 2; cx++)
        {
            for (int cz = -2; cz <= 2; cz++)
            {
                for (int cy = 9; cy <= 11; cy++)
                {
                    var chunk = new Chunk();
                    generator.Generate(chunk, new Vector3i(cx, cy, cz));

                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        for (int z = 0; z < Chunk.Size; z++)
                        {
                            for (int x = 0; x < Chunk.Size; x++)
                            {
                                if (chunk.GetBlock(x, y, z) != shortGrass)
                                {
                                    continue;
                                }

                                plants++;

                                // Pod trávou musí být travnatý blok, ne vzduch ani kámen.
                                if (y > 0 && chunk.GetBlock(x, y - 1, z) == grassBlock)
                                {
                                    onGrass++;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.True(plants > 100, $"V okolí počátku roste jen {plants} trsů trávy.");

        // Část trsů leží na spodní hraně chunku, kde podklad patří sousedovi a odsud
        // ho není vidět — proto ne úplně všechny.
        Assert.True(
            onGrass > plants * 0.9,
            $"Jen {onGrass} z {plants} trsů stojí na trávě, zbytek roste na něčem jiném.");
    }
}
