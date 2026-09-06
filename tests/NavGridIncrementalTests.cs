using System.Diagnostics;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Inkrementální aktualizace pochůzné mřížky po změně bloku.
/// </summary>
/// <remarks>
/// Naměřeno, že plná přestavba mřížky stojí 3,34 ms na chunk a jedno kopnutí zneplatní až
/// osm chunků — 27 ms proti rozpočtu tiku 8 ms. Inkrementální aktualizace proto není
/// optimalizace, ale podmínka funkčnosti.
///
/// <para>Nejdůležitější test tady je shoda s plnou přestavbou. Rychlá aktualizace, která dá
/// jiný výsledek než přestavba, je horší než žádná — kolonisté by chodili podle mřížky,
/// která neodpovídá světu, a hledalo by se to v pathfindingu.</para>
/// </remarks>
public sealed class NavGridIncrementalTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    private static (VoxelWorld World, ushort Stone) TerracedWorld()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                int height = 1 + (((x / 6) + (z / 5)) % 3);
                for (int y = 1; y <= height; y++)
                {
                    world.SetBlock(x, y, z, stone);
                }
            }
        }

        return (world, stone);
    }

    private static NavGrid Build(VoxelWorld world) =>
        NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero);

    private static void AssertSameAs(NavGrid expected, NavGrid actual)
    {
        Assert.Equal(expected.StandableCount, actual.StandableCount);

        for (int index = 0; index < NavGrid.Volume; index++)
        {
            if (expected.IsStandable(index) == actual.IsStandable(index))
            {
                continue;
            }

            NavGrid.FromIndex(index, out int x, out int y, out int z);
            Assert.Fail(
                $"Buňka ({x},{y},{z}) se liší: přestavba {expected.IsStandable(index)}, "
                + $"inkrementálně {actual.IsStandable(index)}.");
        }
    }

    /// <summary>
    /// ROZHODOVACÍ TEST. Po sérii náhodných změn musí inkrementálně udržovaná mřížka
    /// odpovídat plné přestavbě buňku po buňce.
    /// </summary>
    [Fact]
    public void Incremental_updates_match_a_full_rebuild_exactly()
    {
        (VoxelWorld world, ushort stone) = TerracedWorld();
        NavGrid incremental = Build(world);

        var random = new Random(20260816);

        for (int step = 0; step < 400; step++)
        {
            int x = random.Next(0, NavGrid.Size);
            int z = random.Next(0, NavGrid.Size);
            int y = random.Next(1, 8);
            ushort block = random.Next(2) == 0 ? stone : BlockRegistry.Air;

            world.SetBlock(x, y, z, block);
            incremental.UpdateAfterBlockChange(world, world.Registry, NoWater, x, y, z);
        }

        AssertSameAs(Build(world), incremental);
    }

    [Fact]
    public void Digging_the_floor_away_removes_the_cell_above_it()
    {
        (VoxelWorld world, _) = TerracedWorld();
        NavGrid grid = Build(world);

        // Najít sloupec, ve kterém se dá stát.
        int standY = -1;
        for (int y = 0; y < NavGrid.Size; y++)
        {
            if (grid.IsStandable(3, y, 3))
            {
                standY = y;
                break;
            }
        }

        Assert.True(standY > 0, "Testovací svět nemá kde stát.");

        world.SetBlock(3, standY - 1, 3, BlockRegistry.Air);
        Assert.True(grid.UpdateAfterBlockChange(world, world.Registry, NoWater, 3, standY - 1, 3));

        Assert.False(grid.IsStandable(3, standY, 3));
        AssertSameAs(Build(world), grid);
    }

    [Fact]
    public void Placing_a_block_creates_a_standable_cell_on_top_of_it()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Prázdný chunk, ale existující — jinak by nebylo co aktualizovat.
        world.SetBlock(0, 0, 0, stone);
        NavGrid grid = Build(world);

        Assert.False(grid.IsStandable(5, 4, 5));

        world.SetBlock(5, 3, 5, stone);
        Assert.True(grid.UpdateAfterBlockChange(world, world.Registry, NoWater, 5, 3, 5));

        Assert.True(grid.IsStandable(5, 4, 5));
        AssertSameAs(Build(world), grid);
    }

    [Fact]
    public void Change_in_a_different_chunk_column_is_ignored()
    {
        (VoxelWorld world, ushort stone) = TerracedWorld();
        NavGrid grid = Build(world);
        int before = grid.StandableCount;

        // Souřadnice mimo tenhle chunk.
        world.SetBlock(NavGrid.Size + 4, 3, 4, stone);

        Assert.False(grid.UpdateAfterBlockChange(world, world.Registry, NoWater, NavGrid.Size + 4, 3, 4));
        Assert.Equal(before, grid.StandableCount);
    }

    [Fact]
    public void Standable_count_stays_consistent_through_many_changes()
    {
        (VoxelWorld world, ushort stone) = TerracedWorld();
        NavGrid grid = Build(world);
        var random = new Random(4242);

        for (int step = 0; step < 200; step++)
        {
            int x = random.Next(0, NavGrid.Size);
            int z = random.Next(0, NavGrid.Size);
            int y = random.Next(1, 6);
            world.SetBlock(x, y, z, random.Next(2) == 0 ? stone : BlockRegistry.Air);
            grid.UpdateAfterBlockChange(world, world.Registry, NoWater, x, y, z);
        }

        int counted = 0;
        for (int index = 0; index < NavGrid.Volume; index++)
        {
            if (grid.IsStandable(index))
            {
                counted++;
            }
        }

        Assert.Equal(counted, grid.StandableCount);
    }

    [Fact]
    public void Unchanged_block_reports_no_change()
    {
        (VoxelWorld world, _) = TerracedWorld();
        NavGrid grid = Build(world);

        // Vysoko ve vzduchu, kde nic pochůzného není a nic se nemění.
        Assert.False(grid.UpdateAfterBlockChange(world, world.Registry, NoWater, 4, 25, 4));
    }

    /// <summary>Měření, kvůli kterému to celé vzniklo.</summary>
    [Fact]
    public void Incremental_update_is_measured_against_a_full_rebuild()
    {
        (VoxelWorld world, ushort stone) = TerracedWorld();
        NavGrid grid = Build(world);

        var full = new List<double>();
        var incremental = new List<double>();

        for (int run = 0; run < 3; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            Build(world);
            stopwatch.Stop();
            full.Add(stopwatch.Elapsed.TotalMilliseconds);

            // Tisíc kopnutí, aby jedno vyšlo měřitelně.
            stopwatch.Restart();
            for (int i = 0; i < 1000; i++)
            {
                int x = i % NavGrid.Size;
                int z = (i / NavGrid.Size) % NavGrid.Size;
                world.SetBlock(x, 3, z, i % 2 == 0 ? stone : BlockRegistry.Air);
                grid.UpdateAfterBlockChange(world, world.Registry, NoWater, x, 3, z);
            }

            stopwatch.Stop();
            incremental.Add(stopwatch.Elapsed.TotalMilliseconds / 1000.0);
        }

        full.Sort();
        incremental.Sort();

        output.WriteLine($"Plná přestavba chunku : {full[1]:F3} ms");
        output.WriteLine($"Jedno kopnutí         : {incremental[1]:F4} ms");
        output.WriteLine($"Poměr                 : {full[1] / incremental[1]:F0}×");
        output.WriteLine($"Kopnutí do tiku 8 ms  : {8.0 / incremental[1]:F0}");

        // Osm chunků na kopnutí × plná přestavba bylo 27 ms. Inkrementálně to musí být
        // řádově jinde, jinak nemá smysl to mít.
        Assert.True(
            incremental[1] * 8 < 1.0,
            $"Osm chunků inkrementálně stojí {incremental[1] * 8:F2} ms, což je pořád moc.");
    }
}
