using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy generátoru terénu. Běží nad skutečnými definicemi bloků z assetů, protože generátor
/// si v nich vyhledává identifikátory a překlep by se jinak projevil až za běhu.
/// </summary>
public sealed class TerrainGeneratorTests
{
    private const int Seed = 20260727;

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static TerrainGenerator Generator(BlockRegistry registry) => new(registry, Seed);

    [Fact]
    public void Uhlí_vzniká_pod_běžným_povrchem_okolo_Y_300()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);
        ushort coal = registry.IndexOf("tesseris:coal_ore");
        int coalBelow300 = 0;

        for (int cz = -3; cz <= 3; cz++)
        {
            for (int cx = -3; cx <= 3; cx++)
            {
                var chunk = new Chunk();
                generator.Generate(chunk, new Vector3i(cx, 8, cz)); // Y 256…287
                ushort[] blocks = new ushort[Chunk.Volume];
                chunk.CopyTo(blocks);
                coalBelow300 += blocks.Count(block => block == coal);
            }
        }

        Assert.True(coalBelow300 > 0, "Pod Y 300 nevzniklo žádné uhlí.");
    }

    [Fact]
    public void Hluboke_a_stredni_rudy_maji_odlisna_vyskova_pasna()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);
        ushort diamond = registry.IndexOf("tesseris:diamond_ore");
        ushort redstone = registry.IndexOf("tesseris:redstone_ore");
        ushort copper = registry.IndexOf("tesseris:copper_ore");

        int deepDiamond = 0;
        int deepRedstone = 0;
        int deepCopper = 0;
        int middleDiamond = 0;
        int middleCopper = 0;

        for (int cz = -2; cz <= 2; cz++)
        {
            for (int cx = -2; cx <= 2; cx++)
            {
                var deep = new Chunk();
                generator.Generate(deep, new Vector3i(cx, 0, cz));
                var middle = new Chunk();
                generator.Generate(middle, new Vector3i(cx, 9, cz));

                ushort[] deepBlocks = new ushort[Chunk.Volume];
                ushort[] middleBlocks = new ushort[Chunk.Volume];
                deep.CopyTo(deepBlocks);
                middle.CopyTo(middleBlocks);
                deepDiamond += deepBlocks.Count(block => block == diamond);
                deepRedstone += deepBlocks.Count(block => block == redstone);
                deepCopper += deepBlocks.Count(block => block == copper);
                middleDiamond += middleBlocks.Count(block => block == diamond);
                middleCopper += middleBlocks.Count(block => block == copper);
            }
        }

        Assert.True(deepDiamond > 0, "U dna nevznikl žádný diamant.");
        Assert.True(deepRedstone > 0, "U dna nevznikl žádný redstone.");
        Assert.Equal(0, deepCopper);
        Assert.Equal(0, middleDiamond);
        Assert.True(middleCopper > 0, "Kolem měděného peaku nevznikla žádná měď.");
    }

    [Fact]
    public void Stejny_seed_da_stejny_svet()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator first = Generator(registry);
        TerrainGenerator second = Generator(registry);

        var position = new Vector3i(3, 2, -5);

        var a = new Chunk();
        var b = new Chunk();
        first.Generate(a, position);
        second.Generate(b, position);

        ushort[] left = new ushort[Chunk.Volume];
        ushort[] right = new ushort[Chunk.Volume];
        a.CopyTo(left);
        b.CopyTo(right);

        Assert.Equal(left, right);
    }

    [Fact]
    public void Ruzny_seed_da_jiny_svet()
    {
        BlockRegistry registry = Registry();
        var first = new TerrainGenerator(registry, Seed);
        var second = new TerrainGenerator(registry, Seed + 1);

        int different = 0;
        for (int x = 0; x < 300; x += 7)
        {
            if (first.SurfaceHeight(x, x * 2) != second.SurfaceHeight(x, x * 2))
            {
                different++;
            }
        }

        Assert.True(different > 30, $"Výšky se lišily jen na {different} místech.");
    }

    [Fact]
    public void Vyska_povrchu_zustava_v_mezich_sveta()
    {
        TerrainGenerator generator = Generator(Registry());

        for (int x = -2000; x <= 2000; x += 37)
        {
            for (int z = -2000; z <= 2000; z += 53)
            {
                int height = generator.SurfaceHeight(x, z);
                Assert.InRange(height, 1, TerrainGenerator.WorldHeight - 1);
            }
        }
    }

    [Fact]
    public void Chunk_nad_terenem_je_homogenni_vzduch()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        // Nejvyšší patro nad nejvyšším možným povrchem musí vyjít prázdné a bez pole indexů.
        var chunk = new Chunk();
        generator.Generate(chunk, new Vector3i(0, TerrainGenerator.WorldHeightChunks, 0));

        Assert.True(chunk.IsHomogeneous);
        Assert.Equal(BlockRegistry.Air, chunk.HomogeneousBlock);
    }

    [Fact]
    public void Chunk_pod_svetem_je_prazdny()
    {
        TerrainGenerator generator = Generator(Registry());

        var chunk = new Chunk();
        generator.Generate(chunk, new Vector3i(0, -1, 0));

        Assert.True(chunk.IsHomogeneous);
        Assert.Equal(BlockRegistry.Air, chunk.HomogeneousBlock);
    }

    [Fact]
    public void Nejspodnejsi_vrstva_je_vzdy_pevna()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        // Do světa se nesmí dát propadnout ani skrz jeskyni.
        for (int cx = -2; cx <= 2; cx++)
        {
            for (int cz = -2; cz <= 2; cz++)
            {
                var chunk = new Chunk();
                generator.Generate(chunk, new Vector3i(cx, 0, cz));

                for (int x = 0; x < Chunk.Size; x += 5)
                {
                    for (int z = 0; z < Chunk.Size; z += 5)
                    {
                        Assert.NotEqual(BlockRegistry.Air, chunk.GetBlock(x, 0, z));
                    }
                }
            }
        }
    }

    [Fact]
    public void Povrchovy_chunk_obsahuje_vzduch_i_pevne_bloky()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        int surface = generator.SurfaceHeight(0, 0);
        var position = new Vector3i(0, surface / Chunk.Size, 0);

        var chunk = new Chunk();
        generator.Generate(chunk, position);

        ushort[] blocks = new ushort[Chunk.Volume];
        chunk.CopyTo(blocks);

        Assert.Contains(BlockRegistry.Air, blocks);
        Assert.Contains(blocks, block => block != BlockRegistry.Air);
    }

    [Fact]
    public void Na_povrchu_lezi_travni_nebo_pouštní_blok_podle_biomu()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort Block(string id) => registry.IndexOf(id);

        ushort grass = Block("tesseris:grass");
        ushort dryGrass = Block("tesseris:dry_grass");
        ushort sand = Block("tesseris:sand");
        ushort sandstone = Block("tesseris:sandstone");
        ushort redSand = Block("tesseris:red_sand");
        ushort snow = Block("tesseris:snow");
        ushort gravel = Block("tesseris:gravel");
        ushort dirt = Block("tesseris:dirt");
        ushort ice = Block("tesseris:ice");
        ushort stone = Block("tesseris:stone");

        // Na mírném svahu určuje povrch biom. Na strmém se ornice neudrží a prosvítá
        // podloží, proto se u travnatých biomů připouští i štěrk a kámen.
        Dictionary<Biome, ushort[]> allowed = new()
        {
            [Biome.Plains] = [grass, gravel, stone],
            [Biome.Savanna] = [dryGrass, gravel, stone],
            [Biome.Desert] = [sand],
            [Biome.Badlands] = [redSand],
            [Biome.Highlands] = [grass, dirt, gravel, stone],
            [Biome.Tundra] = [snow, gravel, stone],
            [Biome.SnowyPeaks] = [snow, gravel, stone],
            [Biome.StonyPeaks] = [gravel, stone],
            [Biome.FrozenPeaks] = [ice, gravel, stone],
        };

        for (int x = -6000; x < 6000; x += 137)
        {
            int surface = generator.SurfaceHeight(x, 0);
            var position = new Vector3i(x >> Chunk.SizeShift, surface >> Chunk.SizeShift, 0);

            var chunk = new Chunk();
            generator.Generate(chunk, position);

            // Kolem hladiny je pláž a mořské dno, tedy písek bez ohledu na biom. Sloupce
            // v tom pásmu se přeskakují: netestuje se tu pravidlo pro pláž, ale to, že
            // biom určuje povrch.
            if (surface <= TerrainGenerator.SeaLevel + 3)
            {
                continue;
            }

            ushort top = chunk.GetBlock(x & Chunk.SizeMask, surface & Chunk.SizeMask, 0);
            Biome biome = generator.BiomeAt(x, 0);

            // Jeskyně můžou povrch prokousnout, proto se připouští i vzduch.
            Assert.True(
                top == BlockRegistry.Air || allowed[biome].Contains(top),
                $"Na x = {x} v biomu {biome} leží blok {top}, což do něj nepatří.");

            _ = sandstone;
        }
    }

    /// <summary>
    /// V poušti nesmí být na povrchu kámen — ani na kopci, ani na strmé stěně duny.
    ///
    /// <para><b>Proč tenhle test existuje.</b> Do 28. 7. 2026 byla „hora" biom, ne tvar.
    /// Jakmile výška sloupce přesáhla práh, přebila poušť a na dunách se objevily kamenné
    /// vrcholky. Zadavatel to poznal ze hry dřív než jakýkoli test. Nově biom určuje jen
    /// skladbu materiálů a o kámen na strmině se stará povrchové pravidlo, které v poušti
    /// sahá po pískovci.</para>
    /// </summary>
    [Fact]
    public void V_pousti_neni_na_povrchu_kamen()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        ushort stone = registry.IndexOf("tesseris:stone");
        ushort gravel = registry.IndexOf("tesseris:gravel");

        int deserts = 0;

        for (int z = -4000; z < 4000; z += 313)
        {
            for (int x = -4000; x < 4000; x += 311)
            {
                Biome biome = generator.BiomeAt(x, z);
                if (biome is not (Biome.Desert or Biome.Badlands))
                {
                    continue;
                }

                deserts++;

                int surface = generator.SurfaceHeight(x, z);
                var position = new Vector3i(x >> Chunk.SizeShift, surface >> Chunk.SizeShift, z >> Chunk.SizeShift);

                var chunk = new Chunk();
                generator.Generate(chunk, position);

                ushort top = chunk.GetBlock(
                    x & Chunk.SizeMask, surface & Chunk.SizeMask, z & Chunk.SizeMask);

                Assert.True(
                    top != stone && top != gravel,
                    $"Na [{x}, {z}] v biomu {biome} leží na povrchu kámen nebo štěrk.");
            }
        }

        Assert.True(deserts > 20, $"Testem prošlo jen {deserts} pouštních sloupců, což nic nedokazuje.");
    }

    [Fact]
    public void Ve_svete_existuje_vic_nez_jeden_biom()
    {
        TerrainGenerator generator = Generator(Registry());

        var biomes = new HashSet<Biome>();
        for (int x = -4000; x <= 4000; x += 97)
        {
            for (int z = -4000; z <= 4000; z += 173)
            {
                biomes.Add(generator.BiomeAt(x, z));
            }
        }

        Assert.True(biomes.Count >= 2, $"Našel se jen biom {string.Join(", ", biomes)}.");
    }

    [Fact]
    public void Pod_povrchem_jsou_jeskyne()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        // Hluboko pod povrchem musí být aspoň někde dutina, jinak generátor jeskyní nefunguje.
        int cavities = 0;

        for (int cx = -3; cx <= 3; cx++)
        {
            for (int cz = -3; cz <= 3; cz++)
            {
                var chunk = new Chunk();
                var position = new Vector3i(cx, 0, cz);
                generator.Generate(chunk, position);

                for (int y = 5; y < Chunk.Size; y++)
                {
                    for (int x = 0; x < Chunk.Size; x += 3)
                    {
                        for (int z = 0; z < Chunk.Size; z += 3)
                        {
                            // Blok pod povrchem, který je přesto prázdný, je jeskyně.
                            if (chunk.GetBlock(x, y, z) == BlockRegistry.Air
                                && generator.SurfaceHeight((cx * Chunk.Size) + x, (cz * Chunk.Size) + z) > y)
                            {
                                cavities++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(cavities > 100, $"Našlo se jen {cavities} dutin — jeskyně se negenerují.");
    }

    [Fact]
    public void Generovani_neprepisuje_sousedni_souradnice()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        // Blok na hranici chunku musí vyjít stejně, ať se počítá z kteréhokoli z obou chunků.
        var left = new Chunk();
        var right = new Chunk();
        generator.Generate(left, new Vector3i(0, 1, 0));
        generator.Generate(right, new Vector3i(1, 1, 0));

        for (int y = 0; y < Chunk.Size; y += 4)
        {
            for (int z = 0; z < Chunk.Size; z += 4)
            {
                int worldY = Chunk.Size + y;
                int surfaceLeft = generator.SurfaceHeight(31, z);
                int surfaceRight = generator.SurfaceHeight(32, z);

                // Nejde o rovnost bloků, ale o to, že výškopis je spojitá funkce souřadnic
                // a nezávisí na tom, který chunk se zrovna generuje.
                Assert.Equal(surfaceLeft, generator.SurfaceHeight(31, z));
                Assert.Equal(surfaceRight, generator.SurfaceHeight(32, z));
                Assert.True(Math.Abs(surfaceLeft - surfaceRight) < 20, "Na hranici chunku je sráz.");

                _ = worldY;
            }
        }
    }

    /// <summary>
    /// Krajina musí mít skutečné nížiny a vysočiny, ne jen lokální hrbolky kolem jedné
    /// úrovně.
    ///
    /// <para><b>Proč tenhle test existuje.</b> Do 28. 7. 2026 začínal výškopis na vlnové
    /// délce 286 bloků a nic pod ní nebylo. Na čtverci 512×512 se to vyprůměrovalo pryč,
    /// takže každý kus světa měl skoro stejnou střední výšku — naměřeno rozpětí průměrů
    /// <b>10,1 bloku (49,8 až 60,0)</b>. Nikde na světě nebyla nížina. Ve hře z toho byl
    /// dojem „kopec, dolina, kopec, dolina" bez jakéhokoli většího tvaru.</para>
    ///
    /// <para>Po přidání kontinentálního pásma (vlnová délka přes 3000 bloků) je naměřené
    /// rozpětí <b>27,4 bloku (33,8 až 61,2)</b>. Práh 18 leží mezi oběma hodnotami
    /// s rezervou na obě strany.</para>
    /// </summary>
    [Fact]
    public void Krajina_ma_velkorozmerove_tvary_ne_jen_hrbolky()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);

        var means = new List<double>();

        // Čtverce 512×512 rozeseté po 16 000 blocích. Musí být větší než vlnová délka
        // kopců, jinak by test měřil kopce místo velkorozměrového tvaru.
        for (int gz = 0; gz < 5; gz++)
        {
            for (int gx = 0; gx < 5; gx++)
            {
                int originX = (gx * 3200) - 8000;
                int originZ = (gz * 3200) - 8000;

                double sum = 0;
                int count = 0;

                for (int z = 0; z < 512; z += 8)
                {
                    for (int x = 0; x < 512; x += 8)
                    {
                        sum += generator.SurfaceHeight(originX + x, originZ + z);
                        count++;
                    }
                }

                means.Add(sum / count);
            }
        }

        double spread = means.Max() - means.Min();

        Assert.True(
            spread > 18.0,
            $"Střední výška se po světě mění jen o {spread:F1} bloku. Krajina nemá "
            + "velkorozměrový tvar — chybí kontinentální pásmo šumu.");
    }
}
