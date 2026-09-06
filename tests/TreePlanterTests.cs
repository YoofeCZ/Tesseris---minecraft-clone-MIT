using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy rozsazování stromů.
///
/// <para><b>Co se tu vlastně testuje.</b> Strom přesahuje přes hranici chunku a chunky se
/// generují nezávisle, na worker vláknech a v libovolném pořadí — jeden nemůže druhému nic
/// dopsat. Řešením je, že si každý chunk spočítá tentýž strom znovu a zapíše si jen svou
/// část. Když se ta úvaha někde poruší, výsledkem jsou useknuté koruny na hranicích
/// chunků, což je přesně ten druh chyby, který se ve hře pozná až po hodině chození.</para>
/// </summary>
public sealed class TreePlanterTests
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

    /// <summary>Vygeneruje oblast chunků a poskládá z ní mapu světová souřadnice → blok.</summary>
    private static Dictionary<Vector3i, ushort> BuildRegion(
        TerrainGenerator generator, int chunkX, int chunkZ, int radius, int lowChunkY, int highChunkY)
    {
        var world = new Dictionary<Vector3i, ushort>();

        for (int cz = chunkZ - radius; cz <= chunkZ + radius; cz++)
        {
            for (int cx = chunkX - radius; cx <= chunkX + radius; cx++)
            {
                for (int cy = lowChunkY; cy <= highChunkY; cy++)
                {
                    var chunk = new Chunk();
                    generator.Generate(chunk, new Vector3i(cx, cy, cz));

                    if (chunk.IsHomogeneous && chunk.HomogeneousBlock == BlockRegistry.Air)
                    {
                        continue;
                    }

                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        for (int z = 0; z < Chunk.Size; z++)
                        {
                            for (int x = 0; x < Chunk.Size; x++)
                            {
                                ushort block = chunk.GetBlock(x, y, z);
                                if (block != BlockRegistry.Air)
                                {
                                    world[new Vector3i(
                                        (cx * Chunk.Size) + x,
                                        (cy * Chunk.Size) + y,
                                        (cz * Chunk.Size) + z)] = block;
                                }
                            }
                        }
                    }
                }
            }
        }

        return world;
    }

    [Fact]
    public void Ve_svete_rostou_stromy()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort oakLog = registry.IndexOf("tesseris:oak_log");
        ushort oakLeaves = registry.IndexOf("tesseris:oak_leaves");

        // Startovní okolí: povrch v počátku leží kolem y = 320, tedy chunk 10.
        Dictionary<Vector3i, ushort> world = BuildRegion(generator, 0, 0, radius: 1, lowChunkY: 9, highChunkY: 11);

        int logs = world.Values.Count(block => block == oakLog);
        int leaves = world.Values.Count(block => block == oakLeaves);

        Assert.True(logs > 0, "V okolí počátku nestojí jediný dub.");
        Assert.True(leaves > logs, $"Listí ({leaves}) má být víc než kmenů ({logs}).");
    }

    /// <summary>
    /// Koruna nesmí být na hranici chunku useknutá.
    ///
    /// <para>Hledá se strom, jehož kmen stojí těsně u hranice — takový má korunu nutně
    /// v obou chuncích. Kdyby se části razítkovaly nekonzistentně, chyběla by polovina
    /// listí a test to pozná na tom, že koruna není souměrná.</para>
    /// </summary>
    [Fact]
    public void Koruna_na_hranici_chunku_neni_useknuta()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort oakLog = registry.IndexOf("tesseris:oak_log");
        ushort oakLeaves = registry.IndexOf("tesseris:oak_leaves");

        Dictionary<Vector3i, ushort> world = BuildRegion(generator, 0, 0, radius: 1, lowChunkY: 9, highChunkY: 11);

        // Vrcholy kmenů: blok kmene, nad kterým už kmen není.
        List<Vector3i> tops =
        [
            .. world
                .Where(pair => pair.Value == oakLog)
                .Where(pair => !world.TryGetValue(pair.Key + Vector3i.UnitY, out ushort above) || above != oakLog)
                .Select(pair => pair.Key),
        ];

        Assert.NotEmpty(tops);

        // Hranice vygenerované oblasti. Strom u ní má korunu venku, kde nic není — to by
        // vypadalo jako chyba razítkování, přitom je to jen konec testovaných dat.
        // (Přesně na tohle test napoprvé spadl: strom na x = 63 měl korunu v chunku 2,
        // který se negeneroval.)
        const int Margin = 3;
        int minX = ((0 - 1) * Chunk.Size) + Margin;
        int maxX = ((0 + 2) * Chunk.Size) - 1 - Margin;
        int minZ = minX;
        int maxZ = maxX;

        int checkedAtBorder = 0;

        foreach (Vector3i top in tops)
        {
            if (top.X < minX || top.X > maxX || top.Z < minZ || top.Z > maxZ)
            {
                continue;
            }

            int localX = ((top.X % Chunk.Size) + Chunk.Size) % Chunk.Size;
            int localZ = ((top.Z % Chunk.Size) + Chunk.Size) % Chunk.Size;

            // Zajímají jen stromy u hranice chunku: koruna sahá dva bloky, takže při místní
            // souřadnici pod 2 nebo nad 29 přesahuje do souseda.
            bool atBorder = localX < 2 || localX > Chunk.Size - 3 || localZ < 2 || localZ > Chunk.Size - 3;
            if (!atBorder)
            {
                continue;
            }

            checkedAtBorder++;

            // Souměrnost koruny: kolik listí je na každé straně kmene.
            //
            // POČÍTÁ SE PŘES CELOU KORUNU, ne v jednom patře. Koruna se sází po dílcích
            // a okraj se podle hashe prokousává, takže jednotlivé patro klidně vyjde
            // nesouměrně úplně správně — a test na jediném patře pak padal na zdravém stromu.
            // Chybějící polovina koruny se naproti tomu v součtu přes celou korunu pozná
            // spolehlivě: vyjde nula.
            int west = 0;
            int east = 0;

            for (int y = top.Y - 8; y <= top.Y; y++)
            {
                west += CountLeaves(world, oakLeaves, top.X - 2, top.X - 1, y, top.Z);
                east += CountLeaves(world, oakLeaves, top.X + 1, top.X + 2, y, top.Z);
            }

            Assert.True(
                west > 0 && east > 0,
                $"Strom na {top} má korunu jen z jedné strany (západ {west}, východ {east}) — "
                + "razítkování přes hranici chunku nesedí.");
        }

        Assert.True(
            checkedAtBorder > 0,
            "V testované oblasti nestojí žádný strom u hranice chunku, takže se nic neověřilo.");
    }

    private static int CountLeaves(
        Dictionary<Vector3i, ushort> world, ushort leaves, int fromX, int toX, int y, int z)
    {
        int count = 0;

        for (int x = fromX; x <= toX; x++)
        {
            if (world.TryGetValue(new Vector3i(x, y, z), out ushort block) && block == leaves)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Generátor musí zůstat deterministický: tentýž chunk dvakrát dá tentýž výsledek.
    /// Bez toho by se strom po přegenerování chunku mohl objevit jinde než v sousedovi.
    /// </summary>
    [Fact]
    public void Tentyz_chunk_vyjde_dvakrat_stejne()
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
                    Assert.Equal(first.GetBlock(x, y, z), second.GetBlock(x, y, z));
                }
            }
        }
    }

    /// <summary>
    /// Nad pouští nesmí stát dub.
    ///
    /// <para>Ptát se na biom chunku nestačí a napoprvé to test dělal špatně: chunk má
    /// 32 bloků na stranu a hranice biomu jím běžně prochází, takže „pouštní" chunk
    /// dubem obsahuje úplně správně — jen na sloupci, který už poušť není. Rozhoduje
    /// proto biom <b>toho sloupce, kde kmen stojí</b>.</para>
    /// </summary>
    [Fact]
    public void Nad_pousti_nestoji_dub()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort oakLog = registry.IndexOf("tesseris:oak_log");
        ushort cactus = registry.IndexOf("tesseris:cactus");

        Dictionary<Vector3i, ushort> world = BuildRegion(generator, 0, 0, radius: 2, lowChunkY: 9, highChunkY: 11);

        int oaks = 0;
        int cacti = 0;

        foreach ((Vector3i position, ushort block) in world)
        {
            if (block == oakLog)
            {
                oaks++;

                Assert.NotEqual(Biome.Desert, generator.BiomeAt(position.X, position.Z));
            }
            else if (block == cactus)
            {
                cacti++;

                // A obráceně: kaktus roste jen v poušti.
                Assert.Equal(Biome.Desert, generator.BiomeAt(position.X, position.Z));
            }
        }

        Assert.True(oaks > 0, "V prohledané oblasti není jediný dub, test nic neověřil.");
    }
}
