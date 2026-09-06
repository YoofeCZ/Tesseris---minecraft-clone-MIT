using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>Pojistky pro klacíky, kamínky a pazourek ležící přímo v terénu.</summary>
public sealed class GroundClutterTests
{
    private const int Seed = 20260806;

    private static BlockRegistry Blocks() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    [Fact]
    public void Nalezy_jsou_nizke_sberatelne_bloky_a_predmety()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = ItemRegistry.Create(
            blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

        foreach (string id in new[] { "tesseris:stick", "tesseris:small_stone", "tesseris:flint" })
        {
            ushort block = blocks.IndexOf(id);
            int item = items.IndexOf(id);

            Assert.Equal(BlockShape.GroundClutter, blocks.ShapeOf(block));
            Assert.True(blocks.IsCutout(block), id);
            Assert.False(blocks.IsSolid(block), id);
            Assert.True(blocks.NeedsGround(block), id);
            Assert.NotNull(blocks.GroundModelOf(block));
            Assert.Equal(item, items.ItemForBlock(block));
            Assert.Equal(block, items.BlockForItem(item));
        }
    }

    [Theory]
    [InlineData("tesseris:small_stone", 0.125f, 0.0625f, 0.25f)]
    [InlineData("tesseris:small_stone_mid", 0.25f, 0.1875f, 0.25f)]
    [InlineData("tesseris:small_stone_big", 0.375f, 0.1875f, 0.5f)]
    [InlineData("tesseris:flint", 0.125f, 0.0625f, 0.125f)]
    [InlineData("tesseris:flint_mid", 0.25f, 0.125f, 0.25f)]
    [InlineData("tesseris:stick", 0.0625f, 0.0625f, 0.6875f)]
    public void Rozmery_presne_odpovidaji_blockbench_modelu(
        string id, float width, float height, float length)
    {
        BlockRegistry blocks = Blocks();
        GroundClutterModel model = blocks.GroundModelOf(blocks.IndexOf(id))!;
        GroundClutterModel.Box box = Assert.Single(model.Boxes);
        Vector3 size = box.Max - box.Min;

        Assert.Equal(width, size.X, 4);
        Assert.Equal(height, size.Y, 4);
        Assert.Equal(length, size.Z, 4);
    }

    [Fact]
    public void Vetsi_nalezy_davaji_dva_nebo_tri_zakladni_kusy()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = ItemRegistry.Create(
            blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

        foreach ((string id, string drop, int count) in new[]
        {
            ("tesseris:small_stone_mid", "tesseris:small_stone", 2),
            ("tesseris:small_stone_big", "tesseris:small_stone", 3),
            ("tesseris:flint_mid", "tesseris:flint", 2),
        })
        {
            BlockDefinition definition = blocks.Definition(blocks.IndexOf(id));
            Assert.Equal(drop, definition.DropItem);
            Assert.Equal(count, definition.DropCount);
            Assert.Equal(ItemRegistry.Nothing, items.IndexOf(id));
            Assert.NotEqual(ItemRegistry.Nothing, items.IndexOf(drop));
        }
    }

    [Fact]
    public void Natoceni_obsahuje_patnact_tricet_i_ctyricet_pet_stupnu()
    {
        var steps = new HashSet<int>();

        for (int z = -32; z <= 32; z++)
        {
            for (int x = -32; x <= 32; x++)
            {
                float radians = GroundClutterShape.AngleRadians(x, 320, z);
                steps.Add((int)MathF.Round(radians * 180f / MathF.PI));
            }
        }

        Assert.Contains(15, steps);
        Assert.Contains(30, steps);
        Assert.Contains(45, steps);
    }

    [Fact]
    public void Generator_rozmistuje_vsechny_tri_nalezy_deterministicky()
    {
        BlockRegistry blocks = Blocks();
        var terrain = new TerrainGenerator(blocks, Seed);
        var planter = new TreePlanter(blocks, Seed);
        ushort grass = blocks.IndexOf("tesseris:grass");
        ushort stick = blocks.IndexOf("tesseris:stick");
        ushort stone = blocks.IndexOf("tesseris:small_stone");
        ushort flint = blocks.IndexOf("tesseris:flint");
        ushort stoneMid = blocks.IndexOf("tesseris:small_stone_mid");
        ushort stoneBig = blocks.IndexOf("tesseris:small_stone_big");
        ushort flintMid = blocks.IndexOf("tesseris:flint_mid");
        var counts = new Dictionary<ushort, int>
        {
            [stick] = 0,
            [stone] = 0,
            [stoneMid] = 0,
            [stoneBig] = 0,
            [flint] = 0,
            [flintMid] = 0,
        };

        for (int z = -128; z <= 128; z++)
        {
            for (int x = -128; x <= 128; x++)
            {
                ushort first = planter.GroundClutterAt(x, z, grass, terrain);
                ushort second = planter.GroundClutterAt(x, z, grass, terrain);

                Assert.Equal(first, second);

                if (first != BlockRegistry.Air)
                {
                    Assert.True(counts.ContainsKey(first), $"Generátor vrátil neznámý nález {first}.");
                    counts[first]++;
                }
            }
        }

        Assert.True(counts[stick] > 0, "Na trávě se nevygeneroval jediný klacík.");
        Assert.True(counts[stone] > 0, "Na trávě se nevygeneroval jediný kamínek.");
        Assert.True(counts[stoneMid] > 0, "Na trávě se nevygeneroval jediný střední kámen.");
        Assert.True(counts[stoneBig] > 0, "Na trávě se nevygeneroval jediný velký kámen.");
        Assert.True(counts[flint] > 0, "Na trávě se nevygeneroval jediný pazourek.");
        Assert.True(counts[flintMid] > 0, "Na trávě se nevygeneroval jediný střední pazourek.");
    }

    [Fact]
    public void Paprsek_trefi_viditelny_kaminek_ale_ne_prazdno_nad_nim()
    {
        BlockRegistry blocks = Blocks();
        var world = new VoxelWorld(blocks);
        var at = new Vector3i(8, 11, 8);
        ushort stone = blocks.IndexOf("tesseris:small_stone");

        world.SetBlock(at.X, at.Y - 1, at.Z, blocks.IndexOf("tesseris:grass"));
        world.SetBlock(at.X, at.Y, at.Z, stone);

        GroundClutterModel model = blocks.GroundModelOf(stone)!;
        GroundClutterModel.Box box = Assert.Single(model.Boxes);
        float angle = GroundClutterShape.AngleRadians(at.X, at.Y, at.Z);
        Vector3 centre = GroundClutterShape.TransformPoint((box.Min + box.Max) * 0.5f, angle);
        var direction = Vector3.UnitX;
        var origin = new Vector3(at.X - 2f, at.Y + centre.Y, at.Z + centre.Z);

        Assert.True(MicroRaycast.Cast(world, origin, direction, 5f, out MicroHit hit));
        Assert.Equal(at, hit.Block);
        Assert.Equal(stone, hit.Material);

        var above = new Vector3(origin.X, at.Y + 0.7f, origin.Z);
        bool foundAbove = MicroRaycast.Cast(world, above, direction, 5f, out MicroHit highHit);
        Assert.True(!foundAbove || highHit.Block != at);
    }
}
