using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy šíření vody.
///
/// Model je minecraftí: zdroj nikdy nevyschne, doběh se přepočítává z okolí a s každým
/// blokem do strany klesne o stupeň. Hlídá se hlavně to, že se šíření ustálí — dokud se
/// buňky přepočítávají dokola, žere to výkon, i když se navenek nic neděje.
/// </summary>
public sealed class FluidSimulationTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true, Solid = true },
        new BlockDefinition { Id = "tesseris:water", Texture = "water", Opaque = false, Solid = false, Liquid = true },
        new BlockDefinition
        {
            Id = "test:seagrass", Texture = "seagrass", Opaque = false,
            Solid = false, Aquatic = true, Shape = BlockShape.Cross,
        },
    ]);

    /// <summary>Postaví kamennou vaničku s dnem v nule a stěnami po okraji.</summary>
    private static VoxelWorld Basin(int size, int height, out ushort water)
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        water = world.Registry.IndexOf("tesseris:water");

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                world.SetBlock(x, 0, z, stone);

                if (x == 0 || z == 0 || x == size - 1 || z == size - 1)
                {
                    for (int y = 1; y < height; y++)
                    {
                        world.SetBlock(x, y, z, stone);
                    }
                }
            }
        }

        return world;
    }

    private static void Run(FluidSimulation fluid, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            fluid.Update(1.0);
        }
    }

    [Fact]
    public void Zdroj_nevysycha_a_rozlije_se_kolem()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(6, 1, 6, water);
        fluid.Touch(6, 1, 6);

        Run(fluid, 100);

        // Zdroj zůstal na místě a v plné výšce.
        Assert.Equal(water, world.GetBlock(6, 1, 6));
        Assert.Equal(FluidCell.Source, world.GetFluid(6, 1, 6));

        // Vedle něj je doběh, o stupeň nižší.
        Assert.Equal(water, world.GetBlock(7, 1, 6));
        Assert.Equal(FluidCell.MaxFlowing, world.GetFluid(7, 1, 6));
    }

    [Fact]
    public void Vodni_rostlina_zustane_pri_simulaci_rostlinou_i_zdrojem_vody()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        ushort seagrass = world.Registry.IndexOf("test:seagrass");
        var fluid = new FluidSimulation(world);

        world.SetBlock(6, 1, 6, seagrass);
        fluid.Touch(6, 1, 6);
        Run(fluid, 100);

        Assert.Equal(seagrass, world.GetBlock(6, 1, 6));
        Assert.True(world.ContainsWater(6, 1, 6));
        Assert.Equal(water, world.GetBlock(7, 1, 6));
    }

    [Fact]
    public void Dobeh_konci_sedm_bloku_od_zdroje()
    {
        VoxelWorld world = Basin(24, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(4, 1, 4, water);
        fluid.Touch(4, 1, 4);

        Run(fluid, 300);

        // Sedmý blok od zdroje ještě vodu má, osmý už ne — dosah dělá právě to, že každý
        // krok do strany ubere stupeň.
        Assert.Equal(water, world.GetBlock(4 + FluidCell.Reach, 1, 4));
        Assert.NotEqual(water, world.GetBlock(4 + FluidCell.Reach + 1, 1, 4));
    }

    [Fact]
    public void Voda_padne_dolu_dirou_v_polici()
    {
        VoxelWorld world = Basin(10, 8, out ushort water);
        ushort stone = world.Registry.IndexOf("test:stone");
        var fluid = new FluidSimulation(world);

        // Police ve výšce 4 s dírou uprostřed.
        for (int z = 1; z < 9; z++)
        {
            for (int x = 1; x < 9; x++)
            {
                if (x != 5 || z != 5)
                {
                    world.SetBlock(x, 4, z, stone);
                }
            }
        }

        world.SetBlock(5, 5, 5, water);
        fluid.Touch(5, 5, 5);

        Run(fluid, 300);

        Assert.Equal(water, world.GetBlock(5, 1, 5));
    }

    [Fact]
    public void Odstraneni_zdroje_vodu_vysusi()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(6, 1, 6, water);
        fluid.Touch(6, 1, 6);
        Run(fluid, 200);

        Assert.Equal(water, world.GetBlock(8, 1, 6));

        // Zdroj pryč - doběh nemá odkud brát a musí zmizet celý.
        world.SetBlock(6, 1, 6, BlockRegistry.Air);
        fluid.Touch(6, 1, 6);
        Run(fluid, 300);

        for (int x = 1; x < 11; x++)
        {
            for (int z = 1; z < 11; z++)
            {
                Assert.NotEqual(water, world.GetBlock(x, 1, z));
            }
        }
    }

    [Fact]
    public void Sireni_se_ustali_a_prestane_pracovat()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(6, 1, 6, water);
        fluid.Touch(6, 1, 6);

        Run(fluid, 400);

        // Tohle je rozdíl mezi „simulace běží" a „hra běží": ustálená voda nesmí zabírat
        // ani jednu buňku práce navíc.
        Assert.Equal(0, fluid.PendingCount);
        Assert.Equal(0, fluid.LastTickWork);
    }

    /// <summary>Mřížka plná materiálu se svislým otvorem dané šířky v rohu.</summary>
    private static MicroBlock WithHole(ushort material, int holeSize)
    {
        MicroBlock micro = MicroBlock.FromSolid(material).Clone();

        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < holeSize; z++)
            {
                for (int x = 0; x < holeSize; x++)
                {
                    micro.SetMaterial(x, y, z, 0);
                }
            }
        }

        return micro;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(6, true)]
    public void Voda_protece_jen_dost_sirokym_otvorem(int holeSize, bool shouldFlow)
    {
        VoxelWorld world = Basin(6, 8, out ushort water);
        ushort stone = world.Registry.IndexOf("test:stone");
        var fluid = new FluidSimulation(world);

        // DESKA MUSÍ PŘEPAŽIT CELOU VANIČKU. Když je jen uprostřed, voda ji obteče a test
        // pak neměří šířku otvoru, ale to, že vanička má okraje.
        for (int z = 1; z < 5; z++)
        {
            for (int x = 1; x < 5; x++)
            {
                world.SetBlock(x, 3, z, stone);
            }
        }

        world.SetMicro(3, 3, 3, WithHole(stone, holeSize));

        world.SetBlock(3, 5, 3, water);
        fluid.Touch(3, 5, 3);

        Run(fluid, 300);

        bool flowed = false;

        for (int z = 1; z < 5 && !flowed; z++)
        {
            for (int x = 1; x < 5 && !flowed; x++)
            {
                for (int y = 1; y < 3; y++)
                {
                    if (world.GetBlock(x, y, z) == water)
                    {
                        flowed = true;
                        break;
                    }
                }
            }
        }

        Assert.Equal(shouldFlow, flowed);
    }

    [Fact]
    public void Voda_se_nerozlije_okamzite()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(6, 1, 6, water);
        fluid.Touch(6, 1, 6);

        // Půl vteřiny herního času. Prodleva je vteřina na blok, takže vedle zdroje ještě
        // nic být nesmí — přesně tohle hráč viděl: seknul do hráze a než couvnul, bylo po ní.
        Run(fluid, 5);

        Assert.NotEqual(water, world.GetBlock(7, 1, 6));

        // Po další vteřině už doběh je.
        Run(fluid, 15);

        Assert.Equal(water, world.GetBlock(7, 1, 6));
    }


    [Fact]
    public void Vylita_voda_je_zdroj_i_kdyz_tam_driv_byl_dobeh()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        // Nejdřív tudy protekl doběh a zanechal v chunku zapsanou nízkou úroveň.
        world.SetBlock(6, 1, 6, water);
        fluid.Touch(6, 1, 6);
        Run(fluid, 200);

        Assert.Equal(water, world.GetBlock(9, 1, 6));
        byte leftover = world.GetFluid(9, 1, 6);
        Assert.True(leftover < FluidCell.Source, $"na téhle pozici má být doběh, je {leftover}");

        // Zdroj pryč, ať doběh zmizí a zůstane jen zapsaná úroveň v poli.
        world.SetBlock(6, 1, 6, BlockRegistry.Air);
        fluid.Touch(6, 1, 6);
        Run(fluid, 300);

        Assert.NotEqual(water, world.GetBlock(9, 1, 6));

        // Teď tam hráč vyleje kbelík. Musí vzniknout ZDROJ, ne oživlý doběh - jinak voda
        // po pár tikách zase zmizí a vypadá to, jako by pokládání nefungovalo.
        world.SetBlock(9, 1, 6, water);
        fluid.Touch(9, 1, 6);
        Run(fluid, 200);

        Assert.Equal(water, world.GetBlock(9, 1, 6));
    }


    [Fact]
    public void Dobeh_mezi_dvema_zdroji_splyne_s_hladinou()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        // Dva zdroje s jednou mezerou mezi nimi - jako když doběh dorazí k hladině moře.
        world.SetBlock(4, 1, 6, water);
        world.SetBlock(6, 1, 6, water);
        fluid.Touch(4, 1, 6);
        fluid.Touch(6, 1, 6);

        Run(fluid, 300);

        // Mezera se nesmí zaplnit doběhem ležícím pod hladinou - musí z ní být zdroj,
        // jinak by mezi dvěma zdroji zůstal viditelný pás nižší vody.
        Assert.Equal(water, world.GetBlock(5, 1, 6));
        Assert.Equal(FluidCell.Source, world.GetFluid(5, 1, 6));
    }

    [Fact]
    public void Zdroj_se_nesiri_od_kraje_do_krajiny()
    {
        VoxelWorld world = Basin(16, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        // Jediný zdroj. Kolem něj smí být jen doběh - kdyby stačil jeden soused, zdroj by
        // se šířil pořád dál a zaplavil by celou pánev plnou výškou.
        world.SetBlock(8, 1, 8, water);
        fluid.Touch(8, 1, 8);

        Run(fluid, 400);

        int sources = 0;

        for (int x = 1; x < 15; x++)
        {
            for (int z = 1; z < 15; z++)
            {
                if (world.GetBlock(x, 1, z) == water && world.GetFluid(x, 1, z) >= FluidCell.Source)
                {
                    sources++;
                }
            }
        }

        Assert.Equal(1, sources);
    }


    [Fact]
    public void Dobeh_se_nerozlezne_po_hladine_more()
    {
        VoxelWorld world = Basin(16, 6, out ushort water);
        var fluid = new FluidSimulation(world);

        // Jezero přes celé dno vaničky, samé zdroje.
        for (int z = 1; z < 15; z++)
        {
            for (int x = 1; x < 15; x++)
            {
                world.SetBlock(x, 1, z, water);
            }
        }

        // Zdroj o patro výš, jako když se voda vylije na břeh nad hladinou.
        world.SetBlock(3, 2, 3, water);
        fluid.Touch(3, 2, 3);

        Run(fluid, 400);

        // NAD HLADINOU SMÍ BÝT JEN KALUŽ KOLEM ZDROJE, ne rozlézající se pás.
        //
        // Zdroj vylitý na hladinu si čtyři sousedy udělá - to je normální, stejně jako
        // v Minecraftu. Vadné bylo, že se doběh šířil dál a dál po povrchu moře; každý
        // další blok totiž stál na plné vodě a přesto vodu rozváděl. Sedm bloků dosahu
        // znamenalo sedmiblokové pásy ležící nad hladinou.
        var spread = new List<(int X, int Z, int Distance)>();

        for (int z = 1; z < 15; z++)
        {
            for (int x = 1; x < 15; x++)
            {
                if (world.GetBlock(x, 2, z) != water)
                {
                    continue;
                }

                int distance = Math.Abs(x - 3) + Math.Abs(z - 3);

                if (distance > 1)
                {
                    spread.Add((x, z, distance));
                }
            }
        }

        Assert.True(
            spread.Count == 0,
            $"voda se rozlezla po hladině do {spread.Count} bloků dál než k sousedům: "
                + string.Join(", ", spread.Take(6)));
    }


    [Fact]
    public void Ctverec_dva_krat_dva_udela_nekonecnou_vodu()
    {
        VoxelWorld world = Basin(8, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        // Jáma dva krát dva se zdroji v protilehlých rozích - klasický trik na nekonečnou
        // vodu. Zbylé dva rohy mají každý dva sousední zdroje, takže se zdroji stanou taky.
        world.SetBlock(3, 1, 3, water);
        world.SetBlock(4, 1, 4, water);
        fluid.Touch(3, 1, 3);
        fluid.Touch(4, 1, 4);

        Run(fluid, 300);

        foreach ((int x, int z) in new[] { (3, 3), (4, 4), (4, 3), (3, 4) })
        {
            Assert.Equal(water, world.GetBlock(x, 1, z));
            Assert.Equal(FluidCell.Source, world.GetFluid(x, 1, z));
        }
    }

    [Fact]
    public void Odebrani_zdroje_ze_ctverce_se_doplni()
    {
        VoxelWorld world = Basin(8, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        // Plný čtverec zdrojů.
        foreach ((int x, int z) in new[] { (3, 3), (4, 3), (3, 4), (4, 4) })
        {
            world.SetBlock(x, 1, z, water);
            fluid.Touch(x, 1, z);
        }

        Run(fluid, 200);

        // Hráč nabere kbelík z jednoho rohu. Zbylé tři ho musí doplnit - v tom je celá
        // podstata nekonečné vody.
        world.SetBlock(3, 1, 3, BlockRegistry.Air);
        fluid.Touch(3, 1, 3);

        Run(fluid, 300);

        Assert.Equal(water, world.GetBlock(3, 1, 3));
        Assert.Equal(FluidCell.Source, world.GetFluid(3, 1, 3));
    }

    [Fact]
    public void Kbelik_vylity_do_dobehu_z_nej_udela_zdroj()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(4, 1, 6, water);
        fluid.Touch(4, 1, 6);
        Run(fluid, 300);

        // Kus dál od zdroje je mělký doběh.
        Assert.Equal(water, world.GetBlock(7, 1, 6));
        Assert.True(world.GetFluid(7, 1, 6) < FluidCell.Source);

        // Vylitý kbelík z něj udělá plný blok.
        world.PlaceFluidSource(7, 1, 6, water);
        fluid.Touch(7, 1, 6);
        Run(fluid, 200);

        Assert.Equal(FluidCell.Source, world.GetFluid(7, 1, 6));
    }


    [Fact]
    public void Doplneni_melciny_ji_srovna_a_nestavi_patro_navic()
    {
        VoxelWorld world = Basin(12, 4, out ushort water);
        var fluid = new FluidSimulation(world);

        world.SetBlock(4, 1, 6, water);
        fluid.Touch(4, 1, 6);
        Run(fluid, 300);

        // Mělčina kus od zdroje.
        Assert.Equal(water, world.GetBlock(7, 1, 6));
        Assert.True(world.GetFluid(7, 1, 6) < FluidCell.Source);

        // Kbelík vylitý na mělčinu ji má SROVNAT, ne postavit vodu o patro výš. Dřív
        // se blok pokládal nad zaměřený, takže z mělčiny vznikl sloupec.
        world.PlaceFluidSource(7, 1, 6, water);
        fluid.Touch(7, 1, 6);
        Run(fluid, 200);

        Assert.Equal(FluidCell.Source, world.GetFluid(7, 1, 6));
        Assert.NotEqual(water, world.GetBlock(7, 2, 6));
    }


    [Fact]
    public void Dobeh_dotece_nad_hladinu_a_dal_se_nesiri()
    {
        VoxelWorld world = Basin(16, 6, out ushort water);
        ushort stone = world.Registry.IndexOf("test:stone");
        var fluid = new FluidSimulation(world);

        // Jezero v pravé půlce vaničky.
        for (int z = 1; z < 15; z++)
        {
            for (int x = 8; x < 15; x++)
            {
                world.SetBlock(x, 1, z, water);
            }
        }

        // Břeh v levé půlce o patro výš a na něm zdroj, ze kterého voda poteče k jezeru.
        for (int z = 1; z < 15; z++)
        {
            for (int x = 1; x < 8; x++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        world.SetBlock(3, 2, 7, water);
        fluid.Touch(3, 2, 7);

        Run(fluid, 500);

        // VODA NAD HLADINU DOJDE, ale nesmí se po ní rozlézt. Zkoušelo se ji nad vodou
        // rovnou mazat, jenže pak se k hladině vůbec nedostala - zmizela o blok dřív, než
        // k ní dotekla, a vypadalo to, že se zastavila v půli cesty.
        //
        // Měří se tedy dosah, ne přítomnost: pár bloků nad okrajem jezera je v pořádku,
        // deska přes celou hladinu ne.
        var floating = new List<(int X, int Z)>();

        for (int z = 1; z < 15; z++)
        {
            for (int x = 8; x < 15; x++)
            {
                if (world.GetBlock(x, 2, z) == water)
                {
                    floating.Add((x, z));
                }
            }
        }

        int deepest = floating.Count == 0 ? 8 : floating.Max(f => f.X);

        Assert.True(
            deepest <= 9,
            $"voda se rozlezla po hladině až k x={deepest} (jezero začíná na 8), bloků: {floating.Count}");

        // A voda na břehu zůstat musí - pravidlo nesmí vysušit i to, co stojí na kameni.
        Assert.Equal(water, world.GetBlock(3, 2, 7));
    }

}
