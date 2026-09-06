using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Sázení sazenic a růst stromů z nich.
///
/// <para><b>Uzavírá to smyčku les → dřevo → les.</b> Do téhle chvíle sazenice z pokáceného
/// stromu padaly, ale nedaly se zasadit — les se dal jenom kácet.</para>
/// </summary>
public sealed class SaplingTests
{
    private const int Seed = 20260727;

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static ItemRegistry Items(BlockRegistry blocks) =>
        ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

    private static readonly string[] Species = ["oak", "spruce", "birch", "acacia", "maple"];

    /// <summary>Svět s trávníkem v rovině <paramref name="groundY"/> a vzduchem nad ním.</summary>
    private static VoxelWorld Meadow(BlockRegistry registry, int groundY = 320)
    {
        var world = new VoxelWorld(registry);
        ushort grass = registry.IndexOf("tesseris:grass");

        List<(Vector3i, ushort)> soil = [];

        for (int z = -20; z <= 20; z++)
        {
            for (int x = -20; x <= 20; x++)
            {
                soil.Add((new Vector3i(x, groundY, z), grass));
            }
        }

        world.SetBlocks(soil);
        return world;
    }

    /// <summary>
    /// Předmět sazenice opravdu položí blok sazenice.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle je ten test, kvůli kterému to celé bylo rozbité.</b> Sazenice je blok
    /// i ruční definice předmětu zároveň. Dokud se ruční soubor <b>přidával</b> místo aby
    /// automatický přebil, byly v seznamu dva předměty téhož jména — a <c>IndexOf</c> vracel
    /// ten ruční, který blok neznal. Z listí tedy padalo něco, co vypadalo jako sazenice,
    /// ale položit se to nedalo.</para>
    /// </remarks>
    [Theory]
    [InlineData("oak")]
    [InlineData("spruce")]
    [InlineData("birch")]
    [InlineData("acacia")]
    [InlineData("maple")]
    public void Sazenice_z_listi_jde_zasadit(string species)
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        int item = items.IndexOf($"tesseris:{species}_sapling");
        ushort block = blocks.IndexOf($"tesseris:{species}_sapling");

        Assert.NotEqual(ItemRegistry.Nothing, item);
        Assert.NotEqual(BlockRegistry.Air, block);

        Assert.Equal(block, items.BlockForItem(item));
        Assert.Equal(item, items.ItemForBlock(block));
    }

    /// <summary>
    /// V seznamu předmětů není žádné jméno dvakrát.
    /// </summary>
    /// <remarks>
    /// Obecná verze předchozího testu. Duplicita se navenek projeví až tím, že něco nejde
    /// položit nebo vyrobit — a to vypadá jako chyba úplně jinde.
    /// </remarks>
    [Fact]
    public void Zadny_predmet_neni_v_seznamu_dvakrat()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        HashSet<string> seen = [];

        for (int i = 0; i < items.Count; i++)
        {
            string id = items.Definition(i).Id;

            Assert.True(seen.Add(id), $"Předmět '{id}' je v seznamu dvakrát.");
        }
    }

    /// <summary>Sazenice roste jen z trávníku a hlíny, ne z kamene ani ze vzduchu.</summary>
    [Fact]
    public void Sazenice_potrebuje_podklad()
    {
        BlockRegistry registry = Registry();

        ushort sapling = registry.IndexOf("tesseris:oak_sapling");

        Assert.True(registry.NeedsGround(sapling));
        Assert.True(registry.CanStandOn(sapling, registry.IndexOf("tesseris:grass")));
        Assert.True(registry.CanStandOn(sapling, registry.IndexOf("tesseris:dirt")));
        Assert.False(registry.CanStandOn(sapling, registry.IndexOf("tesseris:stone")));
        Assert.False(registry.CanStandOn(sapling, BlockRegistry.Air));
    }

    /// <summary>Ze zasazené sazenice vyroste strom svého druhu, s kmenem i listím.</summary>
    [Theory]
    [InlineData("oak")]
    [InlineData("spruce")]
    [InlineData("birch")]
    [InlineData("acacia")]
    [InlineData("maple")]
    public void Ze_sazenice_vyroste_strom_sveho_druhu(string species)
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);

        VoxelWorld world = Meadow(registry);

        var at = new Vector3i(0, 321, 0);
        world.SetBlock(at.X, at.Y, at.Z, registry.IndexOf($"tesseris:{species}_sapling"));

        int placed = planter.Grow(world, at, out IReadOnlyCollection<Vector3i> touched);

        Assert.True(placed > 0, "Sazenice nevyrostla, i když nad ní bylo volno.");
        Assert.NotEmpty(touched);

        ushort log = registry.IndexOf($"tesseris:{species}_log");
        ushort leaves = registry.IndexOf($"tesseris:{species}_leaves");

        // Pata kmene stojí přesně tam, kde stála sazenice.
        Assert.Equal(log, world.GetBlock(at.X, at.Y, at.Z));

        int logs = 0;
        int foliage = 0;

        for (int y = at.Y; y < at.Y + 40; y++)
        {
            for (int z = at.Z - 8; z <= at.Z + 8; z++)
            {
                for (int x = at.X - 8; x <= at.X + 8; x++)
                {
                    ushort block = world.GetBlock(x, y, z);

                    if (block == log) { logs++; }
                    if (block == leaves) { foliage++; }
                }
            }
        }

        Assert.True(logs >= 9, $"Kmen má jen {logs} klád, čekalo se aspoň devět.");
        Assert.True(foliage > logs, $"Koruna má {foliage} listů proti {logs} kládám — to není strom.");
    }

    /// <summary>
    /// Vyrostlý strom má týž tvar jako vygenerovaný.
    /// </summary>
    /// <remarks>
    /// Kdyby si růst počítal korunu po svém, byly by v lese dva druhy dubů podle toho, jestli
    /// vyrostly, nebo se vygenerovaly. Ověřuje se tím, že táž sazenice na témž místě dá
    /// pokaždé týž strom — obojí prochází stejným razítkováním.
    /// </remarks>
    [Fact]
    public void Tyz_strom_vyroste_pokazde_stejne()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);

        ushort sapling = registry.IndexOf("tesseris:oak_sapling");
        var at = new Vector3i(3, 321, -2);

        List<ushort> Grow()
        {
            VoxelWorld world = Meadow(registry);
            world.SetBlock(at.X, at.Y, at.Z, sapling);

            Assert.True(planter.Grow(world, at, out _) > 0);

            List<ushort> shape = [];

            for (int y = at.Y; y < at.Y + 30; y++)
            {
                for (int z = at.Z - 8; z <= at.Z + 8; z++)
                {
                    for (int x = at.X - 8; x <= at.X + 8; x++)
                    {
                        shape.Add(world.GetBlock(x, y, z));
                    }
                }
            }

            return shape;
        }

        Assert.Equal(Grow(), Grow());
    }

    /// <summary>
    /// Pod stropem strom nevyroste vůbec, místo aby vyrostl uříznutý.
    /// </summary>
    /// <remarks>
    /// Kdyby se zapsalo jen to, co se vejde, zůstal by ve sklepě pahýl bez koruny a hráč by
    /// nepoznal, že mu strom „nevyrostl" — vypadalo by to jako rozbitá geometrie.
    /// </remarks>
    [Fact]
    public void Pod_stropem_strom_nevyroste()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);

        VoxelWorld world = Meadow(registry);

        var at = new Vector3i(0, 321, 0);
        world.SetBlock(at.X, at.Y, at.Z, registry.IndexOf("tesseris:oak_sapling"));

        // Strop tři bloky nad sazenicí. Nejnižší strom má devět klád, takže se nevejde.
        world.SetBlock(at.X, at.Y + 3, at.Z, registry.IndexOf("tesseris:stone"));

        Assert.Equal(0, planter.Grow(world, at, out IReadOnlyCollection<Vector3i> touched));
        Assert.Empty(touched);

        // A hlavně: sazenice zůstala sazenicí, nezmizela.
        Assert.True(planter.IsSapling(world.GetBlock(at.X, at.Y, at.Z)));
    }

    /// <summary>
    /// Koruna se nezakousne do toho, co hráč postavil.
    /// </summary>
    /// <remarks>
    /// Při generování světa razítkuje kmen přes terén, protože terén je v tu chvíli čerstvý.
    /// Vyrostlý strom je jiný případ: kolem něj může stát barák.
    /// </remarks>
    [Fact]
    public void Vyrostly_strom_neprorusta_stavbou()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);

        VoxelWorld world = Meadow(registry);

        ushort planks = registry.IndexOf("tesseris:planks");
        var at = new Vector3i(0, 321, 0);

        world.SetBlock(at.X, at.Y, at.Z, registry.IndexOf("tesseris:oak_sapling"));

        // Zeď vedle sazenice, ve výšce koruny.
        List<Vector3i> wall = [];

        for (int y = at.Y + 6; y <= at.Y + 12; y++)
        {
            for (int z = at.Z - 3; z <= at.Z + 3; z++)
            {
                wall.Add(new Vector3i(at.X + 3, y, z));
            }
        }

        world.SetBlocks([.. wall.Select(w => (w, planks))]);

        Assert.True(planter.Grow(world, at, out _) > 0);

        foreach (Vector3i block in wall)
        {
            Assert.Equal(planks, world.GetBlock(block.X, block.Y, block.Z));
        }
    }

    /// <summary>Průchod okolí najde sazenici, kterou nikdo neohlásil, a nechá ji vyrůst.</summary>
    [Fact]
    public void Prochazeni_najde_i_neohlasenou_sazenici()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);
        var growth = new SaplingGrowth(planter);

        VoxelWorld world = Meadow(registry);

        var at = new Vector3i(2, 321, 1);
        world.SetBlock(at.X, at.Y, at.Z, registry.IndexOf("tesseris:oak_sapling"));

        var player = new Vector3(0f, 321f, 0f);

        // Krok po vteřině: projde se celé okolí a doběhne i nejdelší odpočet růstu.
        for (int i = 0; i < 200 && growth.Grown == 0; i++)
        {
            growth.Update(world, player, 1f);
        }

        Assert.Equal(1, growth.Grown);
        Assert.Equal(registry.IndexOf("tesseris:oak_log"), world.GetBlock(at.X, at.Y, at.Z));
    }

    /// <summary>
    /// Opakované ohlášení téže sazenice odpočet nerestartuje.
    /// </summary>
    /// <remarks>
    /// Průchod okolí narazí na tutéž sazenici každých pár vteřin. Kdyby jí to pokaždé nastavilo
    /// čas znovu, nevyrostla by nikdy — a hledalo by se to špatně, protože každá jednotlivá
    /// část by se chovala správně.
    /// </remarks>
    [Fact]
    public void Opakovane_ohlaseni_odpocet_nerestartuje()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);
        var growth = new SaplingGrowth(planter);

        VoxelWorld world = Meadow(registry);

        var at = new Vector3i(0, 321, 0);
        world.SetBlock(at.X, at.Y, at.Z, registry.IndexOf("tesseris:oak_sapling"));

        var player = new Vector3(0f, 321f, 0f);

        for (int i = 0; i < 200 && growth.Grown == 0; i++)
        {
            growth.Notice(at);
            growth.Update(world, player, 1f);
        }

        Assert.Equal(1, growth.Grown);
    }

    /// <summary>Vytěžená sazenice se z odpočtu vyřadí a strom z ní nevyroste.</summary>
    [Fact]
    public void Vytezena_sazenice_uz_neroste()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);
        var growth = new SaplingGrowth(planter);

        VoxelWorld world = Meadow(registry);

        var at = new Vector3i(0, 321, 0);
        world.SetBlock(at.X, at.Y, at.Z, registry.IndexOf("tesseris:oak_sapling"));

        growth.Notice(at);
        Assert.Equal(1, growth.Growing);

        world.SetBlock(at.X, at.Y, at.Z, BlockRegistry.Air);

        for (int i = 0; i < 200; i++)
        {
            growth.Update(world, new Vector3(0f, 321f, 0f), 1f);
        }

        Assert.Equal(0, growth.Grown);
        Assert.Equal(0, growth.Growing);
    }

    /// <summary>
    /// Dávkový zápis zapíše všechno a ohlásí každý dotčený chunk.
    /// </summary>
    /// <remarks>
    /// Kdyby některý chunk v hlášení chyběl, kus stromu by se objevil až po tom, co hráč
    /// odejde a vrátí se — protože by se ten chunk nepřemeshoval.
    /// </remarks>
    [Fact]
    public void Davkovy_zapis_ohlasi_vsechny_chunky()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort stone = registry.IndexOf("tesseris:stone");

        // Schválně přes hranici chunku v každé ose.
        List<(Vector3i, ushort)> writes =
        [
            (new Vector3i(31, 31, 31), stone),
            (new Vector3i(32, 31, 31), stone),
            (new Vector3i(31, 32, 31), stone),
            (new Vector3i(31, 31, 32), stone),
        ];

        IReadOnlyCollection<Vector3i> touched = world.SetBlocks(writes);

        Assert.Equal(4, touched.Count);

        foreach ((Vector3i at, ushort block) in writes)
        {
            Assert.Equal(block, world.GetBlock(at.X, at.Y, at.Z));
        }
    }

    /// <summary>Každý druh stromu má svou sazenici, jinak by z něj nešel obnovit les.</summary>
    [Fact]
    public void Kazdy_druh_ma_svou_sazenici()
    {
        BlockRegistry registry = Registry();
        var planter = new TreePlanter(registry, Seed);

        foreach (string species in Species)
        {
            ushort sapling = registry.IndexOf($"tesseris:{species}_sapling");

            Assert.NotEqual(BlockRegistry.Air, sapling);
            Assert.True(planter.IsSapling(sapling), $"'{species}_sapling' není poznaná jako sazenice.");
        }
    }
}
