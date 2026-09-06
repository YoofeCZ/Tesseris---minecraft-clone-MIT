using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy jemné mřížky stromů.
///
/// <para><b>Co se tu hlídá.</b> Strom se sází po dílcích, aby měl kmen poloviční šířku
/// a listí se ho dotýkalo. Chyba v tomhle se ve hře projeví dvěma způsoby, které vypadají
/// úplně jinak, než co je rozbité: buď jsou stromy zase z celých kostek, nebo se z terénu
/// kolem nich stanou díry — protože blok s maskou nezakrývá sousedy, a když masku dostane
/// omylem i vzduch nebo kámen, zmizí stěny, které tam být mají.</para>
/// </summary>
public sealed class TreePieceTests
{
    private const int Seed = 20260727;

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static TerrainGenerator Generator(BlockRegistry registry)
    {
        var generator = new TerrainGenerator(registry, Seed);
        generator.EnableTrees(registry);
        return generator;
    }

    /// <summary>Vygeneruje okolí počátku a vrátí chunky i s jejich pozicemi.</summary>
    private static List<(Vector3i Position, Chunk Chunk)> Region(TerrainGenerator generator)
    {
        List<(Vector3i, Chunk)> chunks = [];

        for (int cz = -1; cz <= 1; cz++)
        {
            for (int cx = -1; cx <= 1; cx++)
            {
                for (int cy = 9; cy <= 11; cy++)
                {
                    var position = new Vector3i(cx, cy, cz);
                    var chunk = new Chunk();
                    generator.Generate(chunk, position);
                    chunks.Add((position, chunk));
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Strom je z celých bloků, ne z dílků.
    ///
    /// <para><b>Obrácený test, než tu stál dřív.</b> Koruna se opravdu sázela po dílcích
    /// a tenhle test na to dohlížel — jenže dílek je půlka bloku, takže z koruny byla
    /// drobenka půlbloků a listí lezlo i do bloku, kde stojí kmen. Při kopání pak šlo
    /// obojí naráz. Spojitost koruny dnes nedělá jemnější mřížka, ale <b>přesah</b>
    /// (<see cref="BlockDefinition.Overhang"/>): list se kreslí o kus větší, než je,
    /// takže se sousedi zanoří do sebe.</para>
    ///
    /// <para>Test proto hlídá opak: žádný blok stromu nesmí mít neúplnou masku. Kdyby ji
    /// dostal, přestal by zakrývat stěny sousedů a v koruně by vznikly průhledy.</para>
    /// </summary>
    [Fact]
    public void Strom_je_z_celych_bloku()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort[] treeBlocks = TreeBlocks(registry);

        int found = 0;

        foreach ((Vector3i position, Chunk chunk) in Region(generator))
        {
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        ushort block = chunk.GetBlock(x, y, z);

                        if (Array.IndexOf(treeBlocks, block) < 0)
                        {
                            continue;
                        }

                        found++;

                        Assert.True(
                            chunk.GetPieces(x, y, z) == PieceMask.Full,
                            $"Blok '{registry.Definition(block).Id}' na {position} + [{x}, {y}, {z}] má neúplnou "
                            + "masku dílků — koruna se zase sází po půlblocích.");
                    }
                }
            }
        }

        Assert.True(found > 0, "V okolí počátku nestojí jediný strom, test nic neověřil.");
    }

    /// <summary>
    /// Listí musí mít přesah, jinak se koruna rozpadne na jednotlivé kostky.
    /// </summary>
    /// <remarks>
    /// Je to protějšek předchozího testu: ten hlídá, že se koruna nesází po dílcích, tenhle
    /// hlídá to, co dílky nahradilo. Bez přesahu by mezi listy zůstaly viditelné spáry
    /// a strom by z dálky vypadal jako hromada kostek — přesně na to si zadavatel stěžoval.
    /// </remarks>
    [Fact]
    public void Listi_ma_presah()
    {
        BlockRegistry registry = Registry();

        foreach (string id in new[]
        {
            "tesseris:oak_leaves", "tesseris:spruce_leaves", "tesseris:acacia_leaves",
            "tesseris:birch_leaves", "tesseris:maple_leaves",
        })
        {
            ushort leaves = registry.IndexOf(id);

            Assert.True(
                registry.OverhangOf(leaves) > 0f,
                $"'{id}' nemá přesah — koruna se rozpadne na jednotlivé kostky.");
        }
    }

    /// <summary>Kmeny a listí všech druhů.</summary>
    private static ushort[] TreeBlocks(BlockRegistry registry) =>
    [
        registry.IndexOf("tesseris:oak_log"), registry.IndexOf("tesseris:oak_leaves"),
        registry.IndexOf("tesseris:spruce_log"), registry.IndexOf("tesseris:spruce_leaves"),
        registry.IndexOf("tesseris:acacia_log"), registry.IndexOf("tesseris:acacia_leaves"),
        registry.IndexOf("tesseris:birch_log"), registry.IndexOf("tesseris:birch_leaves"),
        registry.IndexOf("tesseris:maple_log"), registry.IndexOf("tesseris:maple_leaves"),
    ];

    /// <summary>
    /// Masku dílků smí mít jen strom. Kdyby ji dostal vzduch nebo terén, přestaly by se
    /// kreslit stěny sousedů a v krajině by vznikly díry.
    /// </summary>
    [Fact]
    public void Masku_dilku_nese_jen_strom()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort[] treeBlocks = TreeBlocks(registry);

        foreach ((Vector3i position, Chunk chunk) in Region(generator))
        {
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        if (chunk.GetPieces(x, y, z) == PieceMask.Full)
                        {
                            continue;
                        }

                        ushort block = chunk.GetBlock(x, y, z);

                        Assert.True(
                            Array.IndexOf(treeBlocks, block) >= 0,
                            $"Blok '{registry.Definition(block).Id}' na {position} + [{x}, {y}, {z}] má masku dílků, "
                            + "i když není součástí stromu — kolem něj zmizí stěny sousedů.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Prázdná maska ve světě nezůstane. Blok bez jediného dílku není vidět ani se do něj
    /// nedá narazit, ale sousedům pořád ubírá stěny — byla by z něj neviditelná díra.
    /// </summary>
    [Fact]
    public void Blok_s_prazdnou_maskou_ve_svete_nezustane()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        foreach ((Vector3i position, Chunk chunk) in Region(generator))
        {
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        Assert.NotEqual(PieceMask.Empty, chunk.GetPieces(x, y, z));
                    }
                }
            }
        }
    }

    /// <summary>Tentýž chunk vyjde dvakrát stejně i v maskách dílků.</summary>
    [Fact]
    public void Masky_jsou_deterministicke()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        var position = new Vector3i(3, 10, -2);

        var first = new Chunk();
        var second = new Chunk();

        generator.Generate(first, position);
        generator.Generate(second, position);

        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    Assert.Equal(first.GetPieces(x, y, z), second.GetPieces(x, y, z));
                }
            }
        }
    }

    /// <summary>
    /// Kmen uvnitř koruny je obklopený listím ze všech čtyř stran.
    ///
    /// <para><b>Vlastnost zůstala, cesta k ní se změnila.</b> Dřív se listí vešlo do bloku
    /// kmene jako druhý materiál a test hlídal právě ten. Dnes je kmen celý blok tvaru
    /// sloupku a listí se do něj nedostane vůbec — díru kolem něj zavírá <b>přesah</b>
    /// listu a to, že listí obsadí všechny čtyři sousední bloky.</para>
    ///
    /// <para>Kdyby některá strana zůstala prázdná, byl by korunou vidět svislý průhled
    /// na kmen — přesně to, co test hlídal odjakživa.</para>
    /// </summary>
    [Fact]
    public void Kmen_v_korune_obklopuje_listi()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort oakLog = registry.IndexOf("tesseris:oak_log");
        ushort oakLeaves = registry.IndexOf("tesseris:oak_leaves");

        int inCrown = 0;
        int enclosed = 0;

        foreach ((_, Chunk chunk) in Region(generator))
        {
            // Okraje chunku se vynechávají: soused se odsud nedá přečíst, takže by kmen
            // u stěny vyšel jako neobklopený, i když listí vedle něj stojí.
            for (int y = 1; y < Chunk.Size - 1; y++)
            {
                for (int z = 1; z < Chunk.Size - 1; z++)
                {
                    for (int x = 1; x < Chunk.Size - 1; x++)
                    {
                        if (chunk.GetBlock(x, y, z) != oakLog)
                        {
                            continue;
                        }

                        // Kmen uvnitř koruny: pokračuje nahoru a aspoň z jedné strany
                        // sousedí s listím. Kmen pod korunou má být holý, ten sem nepatří.
                        if (chunk.GetBlock(x, y + 1, z) != oakLog || Sides(chunk, oakLeaves, x, y, z) == 0)
                        {
                            continue;
                        }

                        inCrown++;

                        if (Sides(chunk, oakLeaves, x, y, z) == 4)
                        {
                            enclosed++;
                        }
                    }
                }
            }
        }

        Assert.True(inCrown > 0, "V prohledané oblasti není jediný kmen uvnitř koruny, test nic neověřil.");

        // Ne všechny: podmínka „vedle je listí" chytí i nejspodnější patro koruny, kde už
        // je kmen zčásti holý, a okraj koruny se navíc podle hashe prokousává.
        Assert.True(
            enclosed * 2 > inCrown,
            $"Listí obklopuje ze všech čtyř stran jen {enclosed} z {inCrown} kmenů v koruně — "
            + "korunou je vidět průhled na kmen.");
    }

    /// <summary>Kolik ze čtyř vodorovných sousedů je listí.</summary>
    private static int Sides(Chunk chunk, ushort leaves, int x, int y, int z)
    {
        int count = 0;

        if (chunk.GetBlock(x - 1, y, z) == leaves) { count++; }
        if (chunk.GetBlock(x + 1, y, z) == leaves) { count++; }
        if (chunk.GetBlock(x, y, z - 1) == leaves) { count++; }
        if (chunk.GetBlock(x, y, z + 1) == leaves) { count++; }

        return count;
    }

    /// <summary>
    /// Kmen stojí uprostřed svého bloku, ne u kraje.
    ///
    /// <para><b>Proto má kmen vlastní tvar a ne dílky.</b> Dílek je vždycky půlka bloku,
    /// takže leží buď vlevo, nebo vpravo — sloupek z jednoho dílku proto nikdy nevyjde
    /// doprostřed a strom stál viditelně u kraje bloku, na kterém roste.</para>
    /// </summary>
    [Fact]
    public void Kmen_stoji_uprostred_sveho_bloku()
    {
        BlockRegistry registry = Registry();

        ushort oakLog = registry.IndexOf("tesseris:oak_log");

        Assert.Equal(BlockShape.Post, registry.ShapeOf(oakLog));

        List<Aabb> boxes = [.. PieceMask.Colliders(BlockShape.Post, PieceMask.Full)];

        Aabb trunk = Assert.Single(boxes);

        // Souměrně kolem středu bloku v obou vodorovných osách.
        Assert.Equal(0.5f, (trunk.Min.X + trunk.Max.X) / 2f, 4);
        Assert.Equal(0.5f, (trunk.Min.Z + trunk.Max.Z) / 2f, 4);

        // Poloviční šířka a plná výška.
        Assert.Equal(0.5f, trunk.Max.X - trunk.Min.X, 4);
        Assert.Equal(0.5f, trunk.Max.Z - trunk.Min.Z, 4);
        Assert.Equal(0f, trunk.Min.Y, 4);
        Assert.Equal(1f, trunk.Max.Y, 4);
    }

    /// <summary>Ve vygenerovaném světě opravdu stojí stromy s kmenem tvaru sloupku.</summary>
    [Fact]
    public void Ve_svete_rostou_stromy_se_sloupkovym_kmenem()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort oakLog = registry.IndexOf("tesseris:oak_log");

        int logs = 0;

        foreach ((_, Chunk chunk) in Region(generator))
        {
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        if (chunk.GetBlock(x, y, z) != oakLog)
                        {
                            continue;
                        }

                        logs++;

                        // Sloupek nemá masku dílků: jeho tvar je kvádr, ne osminy bloku.
                        Assert.Equal(PieceMask.Full, chunk.GetPieces(x, y, z));
                    }
                }
            }
        }

        Assert.True(logs > 0, "V okolí počátku nestojí jediný dub.");
    }
}
