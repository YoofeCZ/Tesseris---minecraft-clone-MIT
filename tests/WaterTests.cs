using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy vody.
///
/// <para>Hladina leží na <see cref="TerrainGenerator.SeaLevel"/> = 305. Číslo je vybrané
/// měřením rozložení výšek na ploše 60 × 60 km: pod ním leží 25 % světa.</para>
/// </summary>
public sealed class WaterTests
{
    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static TerrainGenerator Generator(BlockRegistry registry) => new(registry, seed: 20260727);

    [Fact]
    public void Voda_neni_pevna_ale_je_to_kapalina()
    {
        BlockRegistry registry = Registry();
        ushort water = registry.IndexOf("tesseris:water");

        Assert.False(registry.IsSolid(water), "Vodou se má dát proplavat.");
        Assert.True(registry.IsLiquid(water));
        Assert.False(registry.IsOpaque(water));

        // Kámen naopak kapalina není — ať se příznak nenastavuje omylem všude.
        Assert.False(registry.IsLiquid(registry.IndexOf("tesseris:stone")));
    }

    /// <summary>
    /// Prostor mezi povrchem a hladinou musí být vodou vyplněný celý. Kdyby zůstala
    /// vzduchová mezera, byla by v moři díra až na dno.
    /// </summary>
    [Fact]
    public void Prohlubne_pod_hladinou_jsou_zaplavene()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);
        ushort water = registry.IndexOf("tesseris:water");

        int checkedColumns = 0;

        for (int x = -9000; x < 9000; x += 211)
        {
            int surface = generator.SurfaceHeight(x, 0);

            if (surface >= TerrainGenerator.SeaLevel)
            {
                continue;
            }

            checkedColumns++;

            // Blok těsně nad povrchem a blok těsně pod hladinou: obojí musí být voda.
            foreach (int y in (ReadOnlySpan<int>)[surface + 1, TerrainGenerator.SeaLevel])
            {
                var position = new Vector3i(x >> Chunk.SizeShift, y >> Chunk.SizeShift, 0);

                var chunk = new Chunk();
                generator.Generate(chunk, position);

                ushort block = chunk.GetBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, 0);

                Assert.True(
                    block == water,
                    $"Na ({x}, {y}) je pod hladinou blok {block} místo vody (povrch {surface}).");
            }
        }

        Assert.True(checkedColumns > 5, $"Otestovalo se jen {checkedColumns} sloupců pod hladinou.");
    }

    [Fact]
    public void Nad_hladinou_zadna_voda_neni()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);
        ushort water = registry.IndexOf("tesseris:water");

        for (int x = -9000; x < 9000; x += 197)
        {
            int y = TerrainGenerator.SeaLevel + 1;
            var position = new Vector3i(x >> Chunk.SizeShift, y >> Chunk.SizeShift, 0);

            var chunk = new Chunk();
            generator.Generate(chunk, position);

            Assert.NotEqual(water, chunk.GetBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, 0));
        }
    }

    /// <summary>
    /// Z vody se musí dát vylézt na břeh.
    ///
    /// <para>První verze to neuměla: plavat vzhůru šlo jen po hladinu a svislá rychlost
    /// je ve vodě omezená, takže hráč dojel k břehu a zůstal ve vodě. Zadavatel to popsal
    /// slovy „skáču nad vodou, ale už neskočím na blok".</para>
    /// </summary>
    [Fact]
    public void Z_vody_se_da_vylezt_na_breh()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);

        ushort stone = registry.IndexOf("tesseris:stone");
        ushort water = registry.IndexOf("tesseris:water");

        // Bazén: dno na y = 9, voda na 10 a 11, a od x = 3 dál břeh s povrchem na y = 11.
        // Souš sahá daleko, aby hráč nepřeběhl okraj a nespadl mimo svět — na tom test
        // napoprvé selhal a skončil na y = -220.
        for (int x = -6; x <= 60; x++)
        {
            for (int z = -4; z <= 4; z++)
            {
                world.SetBlock(x, 9, z, stone);

                if (x >= 3)
                {
                    world.SetBlock(x, 10, z, stone);
                    world.SetBlock(x, 11, z, stone);
                }
                else
                {
                    world.SetBlock(x, 10, z, water);
                    world.SetBlock(x, 11, z, water);
                }
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 10f, 0.5f));

        // Plave k břehu a drží skok.
        for (int i = 0; i < 240; i++)
        {
            player.Update(world, new Vector3(1f, 0f, 0f), jump: true, sprint: false, verticalWish: 0f, 1f / 60f);
        }

        Assert.True(
            player.Position.Y >= 12f,
            $"Hráč zůstal ve vodě na y = {player.Position.Y:F2}, na břeh se nedostal.");

        Assert.True(
            player.Position.X > 3f,
            $"Hráč se nedostal za hranu břehu, skončil na x = {player.Position.X:F2}.");
    }

    /// <summary>
    /// Zaplavovat se smí jen to, co je pod mořským dnem — ne všechno pod úrovní hladiny.
    ///
    /// <para>První verze brala jen výšku voxelu, takže zatopila i jeskyně hluboko pod
    /// horami a ze světa zmizely všechny hluboké dutiny. Spadl na tom test na jeskyně.</para>
    /// </summary>
    [Fact]
    public void Jeskyne_pod_pevninou_zustavaji_suche()
    {
        BlockRegistry registry = Registry();
        TerrainGenerator generator = Generator(registry);
        ushort water = registry.IndexOf("tesseris:water");

        int cavities = 0;
        int flooded = 0;

        for (int x = -4000; x < 4000; x += 97)
        {
            int surface = generator.SurfaceHeight(x, 0);

            // Jen sloupce jasně nad hladinou, tedy pevnina.
            if (surface < TerrainGenerator.SeaLevel + 40)
            {
                continue;
            }

            for (int y = 120; y < 260; y += 32)
            {
                var position = new Vector3i(x >> Chunk.SizeShift, y >> Chunk.SizeShift, 0);

                var chunk = new Chunk();
                generator.Generate(chunk, position);

                for (int local = 0; local < Chunk.Size; local++)
                {
                    ushort block = chunk.GetBlock(x & Chunk.SizeMask, local, 0);

                    if (block == BlockRegistry.Air)
                    {
                        cavities++;
                    }
                    else if (block == water)
                    {
                        flooded++;
                    }
                }
            }
        }

        Assert.True(cavities > 0, "Pod pevninou se nenašla jediná suchá dutina.");
        Assert.Equal(0, flooded);
    }
}
