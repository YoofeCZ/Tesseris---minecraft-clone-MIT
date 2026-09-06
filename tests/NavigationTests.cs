using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Pochůzná mřížka a lokální A* (T2). Nejnebezpečnější věc v projektu — kolonista zaseknutý
/// v tunelu zabije hru rychleji než cokoli jiného.
/// </summary>
public sealed class NavigationTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Podlaha uvnitř chunku 0,0,0 na patře y = 1, tedy stání na y = 2.</summary>
    private static (VoxelWorld World, ushort Stone) FloorWorld(int floorY = 1)
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, floorY, z, stone);
            }
        }

        return (world, stone);
    }

    private static NavGrid Build(VoxelWorld world) =>
        NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero);

    [Fact]
    public void Index_and_reverse_agree_across_the_whole_chunk()
    {
        for (int y = 0; y < NavGrid.Size; y += 7)
        {
            for (int z = 0; z < NavGrid.Size; z += 5)
            {
                for (int x = 0; x < NavGrid.Size; x += 3)
                {
                    int index = NavGrid.Index(x, y, z);
                    NavGrid.FromIndex(index, out int bx, out int by, out int bz);

                    Assert.Equal((x, y, z), (bx, by, bz));
                }
            }
        }
    }

    [Fact]
    public void Flat_floor_is_standable_on_exactly_one_level()
    {
        (VoxelWorld world, _) = FloorWorld(floorY: 1);
        NavGrid grid = Build(world);

        // Stát se dá na patře nad podlahou, nikde jinde ve sloupci.
        Assert.True(grid.IsStandable(4, 2, 4));
        Assert.False(grid.IsStandable(4, 1, 4));
        Assert.False(grid.IsStandable(4, 3, 4));

        // Jedna vrstva 32×32.
        Assert.Equal(NavGrid.Size * NavGrid.Size, grid.StandableCount);
    }

    [Fact]
    public void Empty_world_has_nothing_to_stand_on()
    {
        var world = new VoxelWorld(Registry());
        NavGrid grid = Build(world);

        Assert.Equal(0, grid.StandableCount);
    }

    [Fact]
    public void Path_across_an_open_floor_is_the_shortest_one()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        int start = NavGrid.Index(2, 2, 2);
        int goal = NavGrid.Index(8, 2, 2);

        Assert.Equal(PathResult.Found, finder.TryFindPath(grid, start, goal, path));

        // Šest kroků po přímce = sedm buněk včetně obou konců.
        Assert.Equal(7, path.Count);
        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);
    }

    [Fact]
    public void Path_steps_are_always_adjacent()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        Assert.Equal(
            PathResult.Found,
            finder.TryFindPath(grid, NavGrid.Index(1, 2, 1), NavGrid.Index(20, 2, 25), path));

        for (int i = 1; i < path.Count; i++)
        {
            NavGrid.FromIndex(path[i - 1], out int px, out int py, out int pz);
            NavGrid.FromIndex(path[i], out int cx, out int cy, out int cz);

            int horizontal = Math.Abs(cx - px) + Math.Abs(cz - pz);
            Assert.Equal(1, horizontal);
            Assert.InRange(cy - py, -Walkability.StepDown, Walkability.StepUp);
        }
    }

    /// <summary>
    /// Zeď přes celý chunk s jedinou mezerou. Cesta musí existovat a musí tou mezerou projít.
    /// </summary>
    [Fact]
    public void Path_goes_through_the_only_gap_in_a_wall()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        const int WallX = 10;
        const int GapZ = 20;
        for (int z = 0; z < NavGrid.Size; z++)
        {
            if (z == GapZ)
            {
                continue;
            }

            world.SetBlock(WallX, 2, z, stone);
            world.SetBlock(WallX, 3, z, stone);
        }

        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        Assert.Equal(
            PathResult.Found,
            finder.TryFindPath(grid, NavGrid.Index(2, 2, 2), NavGrid.Index(20, 2, 2), path));

        bool wentThroughGap = path.Any(cell =>
        {
            NavGrid.FromIndex(cell, out int x, out _, out int z);
            return x == WallX && z == GapZ;
        });

        Assert.True(wentThroughGap, "Cesta musí projít jedinou mezerou ve zdi.");
    }

    /// <summary>
    /// POVINNÝ TEST z M0: cíl je zazděný. Hledání musí skončit v konečném čase a nahlásit to,
    /// ne hledat donekonečna.
    /// </summary>
    [Fact]
    public void Walled_off_goal_is_reported_as_unreachable_in_finite_time()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        // Kobka 1×1 kolem cíle na (5,2,5): zeď ze všech čtyř stran, dvě patra vysoká.
        foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            world.SetBlock(5 + dx, 2, 5 + dz, stone);
            world.SetBlock(5 + dx, 3, 5 + dz, stone);
        }

        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        PathResult result = finder.TryFindPath(
            grid,
            NavGrid.Index(20, 2, 20),
            NavGrid.Index(5, 2, 5),
            path);

        Assert.Equal(PathResult.Unreachable, result);
        Assert.Empty(path);

        // A hlavně: skončilo to, aniž by to spotřebovalo celý rozpočet.
        Assert.True(finder.LastExpanded < PathFinder.DefaultBudget);
    }

    [Fact]
    public void Search_gives_up_when_the_budget_runs_out_and_says_so()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        PathResult result = finder.TryFindPath(
            grid,
            NavGrid.Index(0, 2, 0),
            NavGrid.Index(31, 2, 31),
            path,
            budget: 3);

        Assert.Equal(PathResult.BudgetExhausted, result);
        Assert.Empty(path);
        Assert.True(finder.LastExpanded <= 3);
    }

    [Fact]
    public void Endpoints_that_are_not_standable_are_rejected()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        // Buňka ve vzduchu vysoko nad podlahou.
        Assert.Equal(
            PathResult.InvalidEndpoint,
            finder.TryFindPath(grid, NavGrid.Index(2, 2, 2), NavGrid.Index(2, 20, 2), path));
    }

    [Fact]
    public void Path_to_itself_is_a_single_cell()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        int cell = NavGrid.Index(6, 2, 6);
        Assert.Equal(PathResult.Found, finder.TryFindPath(grid, cell, cell, path));
        Assert.Single(path);
    }

    /// <summary>
    /// Determinismus: tentýž vstup musí dát bajt po bajtu tutéž cestu, i po jiných hledáních
    /// mezitím. Na tom stojí savy a replaye (pravidlo 6.6).
    /// </summary>
    [Fact]
    public void Same_query_returns_the_same_path_every_time()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGrid grid = Build(world);
        var finder = new PathFinder();

        var first = new List<int>();
        int start = NavGrid.Index(3, 2, 4);
        int goal = NavGrid.Index(19, 2, 22);
        Assert.Equal(PathResult.Found, finder.TryFindPath(grid, start, goal, first));

        // Mezitím schválně jiná hledání, aby se pracovní pole zašpinila.
        var scratch = new List<int>();
        finder.TryFindPath(grid, NavGrid.Index(0, 2, 0), NavGrid.Index(31, 2, 31), scratch);
        finder.TryFindPath(grid, NavGrid.Index(31, 2, 0), NavGrid.Index(0, 2, 31), scratch);

        var second = new List<int>();
        Assert.Equal(PathResult.Found, finder.TryFindPath(grid, start, goal, second));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Reuse pracovních polí přes razítko běhu: druhé hledání nesmí vidět nic z prvního.
    /// </summary>
    [Fact]
    public void Stale_state_from_a_previous_search_does_not_leak()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();
        NavGrid open = Build(world);

        var finder = new PathFinder();
        var path = new List<int>();
        Assert.Equal(
            PathResult.Found,
            finder.TryFindPath(open, NavGrid.Index(2, 2, 2), NavGrid.Index(20, 2, 2), path));

        // Teď se cesta zazdí a hledá se znovu toutéž instancí.
        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(10, 2, z, stone);
            world.SetBlock(10, 3, z, stone);
        }

        NavGrid blocked = Build(world);
        Assert.Equal(
            PathResult.Unreachable,
            finder.TryFindPath(blocked, NavGrid.Index(2, 2, 2), NavGrid.Index(20, 2, 2), path));
    }

    /// <summary>
    /// Schod nahoru o jeden blok musí být průchozí, zeď o dva bloky ne — musí sedět na
    /// Walkability, jinak by kolonista chodil jinudy, než kudy se dá jít.
    /// </summary>
    [Fact]
    public void Single_step_up_is_walkable_but_a_two_block_wall_is_not()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        // Schod přes celý chunk na x = 10.
        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(10, 2, z, stone);
        }

        NavGrid grid = Build(world);
        var finder = new PathFinder();
        var path = new List<int>();

        Assert.Equal(
            PathResult.Found,
            finder.TryFindPath(grid, NavGrid.Index(2, 2, 2), NavGrid.Index(20, 2, 2), path));

        // Zvýšit na dva bloky a cesta zmizí.
        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(10, 3, z, stone);
        }

        NavGrid taller = Build(world);
        Assert.Equal(
            PathResult.Unreachable,
            finder.TryFindPath(taller, NavGrid.Index(2, 2, 2), NavGrid.Index(20, 2, 2), path));
    }
}
