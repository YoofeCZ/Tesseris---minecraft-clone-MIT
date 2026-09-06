using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Pole směrů ke společnému cíli a fronta žádostí o cestu s rozpočtem na tik.
/// </summary>
public sealed class FlowFieldAndQueueTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    private static (VoxelWorld World, ushort Stone) FloorAcross(int chunksX)
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < chunksX * NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        return (world, stone);
    }

    private static NavGrid Grid(VoxelWorld world) =>
        NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero);

    // ================= FLOW FIELD =================

    [Fact]
    public void Flow_field_reaches_the_whole_floor()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGrid grid = Grid(world);
        var field = new FlowField();

        Assert.True(field.Build(grid, NavGrid.Index(16, 2, 16)));

        Assert.Equal(0, field.CostAt(NavGrid.Index(16, 2, 16)));
        Assert.NotEqual(FlowField.Unreachable, field.CostAt(NavGrid.Index(0, 2, 0)));
        Assert.NotEqual(FlowField.Unreachable, field.CostAt(NavGrid.Index(31, 2, 31)));
    }

    [Fact]
    public void Cost_grows_with_distance_from_the_goal()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGrid grid = Grid(world);
        var field = new FlowField();
        field.Build(grid, NavGrid.Index(0, 2, 0));

        int near = field.CostAt(NavGrid.Index(3, 2, 0));
        int far = field.CostAt(NavGrid.Index(20, 2, 0));

        Assert.True(near < far, $"Blízká buňka má stát míň: {near} vs {far}.");
    }

    /// <summary>
    /// Nejdůležitější vlastnost pole: opakovaným krokem se musí každý dostat do cíle,
    /// a to v konečném počtu kroků. Cyklus by zaseknul celou skupinu naráz.
    /// </summary>
    [Fact]
    public void Following_the_field_always_arrives_at_the_goal()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGrid grid = Grid(world);
        var field = new FlowField();
        int goal = NavGrid.Index(5, 2, 27);
        field.Build(grid, goal);

        for (int x = 0; x < NavGrid.Size; x += 3)
        {
            for (int z = 0; z < NavGrid.Size; z += 3)
            {
                int cell = NavGrid.Index(x, 2, z);
                if (!grid.IsStandable(cell))
                {
                    continue;
                }

                int steps = 0;
                while (cell != goal)
                {
                    Assert.True(field.TryNextStep(cell, out int next), $"Pole nedosáhlo na ({x},2,{z}).");
                    cell = next;

                    Assert.True(++steps < NavGrid.Volume, "Následování pole se zacyklilo.");
                }
            }
        }
    }

    [Fact]
    public void Cells_behind_a_wall_are_unreachable()
    {
        (VoxelWorld world, ushort stone) = FloorAcross(1);

        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(10, 2, z, stone);
            world.SetBlock(10, 3, z, stone);
        }

        NavGrid grid = Grid(world);
        var field = new FlowField();
        field.Build(grid, NavGrid.Index(2, 2, 2));

        Assert.NotEqual(FlowField.Unreachable, field.CostAt(NavGrid.Index(5, 2, 5)));
        Assert.Equal(FlowField.Unreachable, field.CostAt(NavGrid.Index(20, 2, 5)));
        Assert.False(field.TryNextStep(NavGrid.Index(20, 2, 5), out _));
    }

    [Fact]
    public void Goal_that_is_not_standable_is_refused()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGrid grid = Grid(world);
        var field = new FlowField();

        Assert.False(field.Build(grid, NavGrid.Index(5, 20, 5)));
    }

    [Fact]
    public void Budget_truncates_the_field_instead_of_running_long()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGrid grid = Grid(world);
        var field = new FlowField();

        field.Build(grid, NavGrid.Index(16, 2, 16), budget: 20);

        Assert.True(field.Truncated);
        Assert.True(field.Covered <= 20, $"Pokrylo {field.Covered} buněk proti rozpočtu 20.");

        // Vzdálený roh za hranicí rozpočtu směr nedostane a musí si říct o vlastní cestu.
        Assert.False(field.TryNextStep(NavGrid.Index(0, 2, 0), out _));
    }

    [Fact]
    public void Rebuilding_the_field_forgets_the_previous_goal()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGrid grid = Grid(world);
        var field = new FlowField();

        field.Build(grid, NavGrid.Index(2, 2, 2), budget: 10);
        field.Build(grid, NavGrid.Index(20, 2, 20));

        Assert.Equal(NavGrid.Index(20, 2, 20), field.Goal);
        Assert.Equal(0, field.CostAt(NavGrid.Index(20, 2, 20)));
        Assert.False(field.Truncated);
    }

    // ================= FRONTA ŽÁDOSTÍ =================

    private static NavGraph GraphOver(VoxelWorld world, int chunksX)
    {
        var graph = new NavGraph();
        for (int cx = 0; cx < chunksX; cx++)
        {
            graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, new Vector3i(cx, 0, 0)));
        }

        graph.BuildPortals();
        return graph;
    }

    [Fact]
    public void Queue_processes_only_the_budget_per_tick()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);
        var queue = new PathRequestQueue();

        for (int agent = 0; agent < 50; agent++)
        {
            queue.Request(agent, new Vector3i(1, 2, 1), new Vector3i(20, 2, 20));
        }

        Assert.Equal(50, queue.PendingCount);

        queue.Process(graph, budget: 8);

        Assert.Equal(8, queue.LastProcessed);
        Assert.Equal(8, queue.Answers.Count);
        Assert.Equal(42, queue.PendingCount);
    }

    /// <summary>
    /// Kolonista, který čeká, chvíli stojí — ale nesmí se na něj zapomenout. Po dost tikách
    /// musí odpověď dostat každý.
    /// </summary>
    [Fact]
    public void Everyone_is_answered_eventually()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);
        var queue = new PathRequestQueue();

        const int Agents = 40;
        for (int agent = 0; agent < Agents; agent++)
        {
            queue.Request(agent, new Vector3i(1, 2, 1), new Vector3i(20, 2, 20));
        }

        var answered = new HashSet<int>();
        for (int tick = 0; tick < 20 && queue.PendingCount > 0; tick++)
        {
            queue.Process(graph, budget: 8);
            foreach (PathAnswer answer in queue.Answers)
            {
                Assert.True(answered.Add(answer.AgentId), $"Kolonista {answer.AgentId} odpověděl dvakrát.");
            }
        }

        Assert.Equal(Agents, answered.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Newer_request_from_the_same_agent_replaces_the_older_one()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);
        var queue = new PathRequestQueue();

        queue.Request(7, new Vector3i(1, 2, 1), new Vector3i(5, 2, 5));
        queue.Request(7, new Vector3i(1, 2, 1), new Vector3i(9, 2, 9));

        Assert.Equal(1, queue.PendingCount);

        queue.Process(graph);

        PathAnswer answer = Assert.Single(queue.Answers);
        Assert.Equal(PathResult.Found, answer.Result);
        Assert.Equal(new Vector3i(9, 2, 9), answer.Path[^1]);
    }

    [Fact]
    public void Cancelled_request_is_not_answered()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);
        var queue = new PathRequestQueue();

        queue.Request(1, new Vector3i(1, 2, 1), new Vector3i(5, 2, 5));
        queue.Request(2, new Vector3i(1, 2, 1), new Vector3i(6, 2, 6));
        queue.Request(3, new Vector3i(1, 2, 1), new Vector3i(7, 2, 7));

        Assert.True(queue.Cancel(2));
        Assert.False(queue.Cancel(2));

        queue.Process(graph);

        Assert.DoesNotContain(queue.Answers, answer => answer.AgentId == 2);
        Assert.Equal(2, queue.Answers.Count);
    }

    [Fact]
    public void Unreachable_goal_is_reported_back_not_silently_dropped()
    {
        (VoxelWorld world, ushort stone) = FloorAcross(1);

        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(10, 2, z, stone);
            world.SetBlock(10, 3, z, stone);
        }

        NavGraph graph = GraphOver(world, 1);
        var queue = new PathRequestQueue();
        queue.Request(1, new Vector3i(2, 2, 2), new Vector3i(20, 2, 2));

        queue.Process(graph);

        PathAnswer answer = Assert.Single(queue.Answers);
        Assert.Equal(PathResult.Unreachable, answer.Result);
        Assert.Empty(answer.Path);
    }

    [Fact]
    public void Order_of_requests_is_kept()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);
        var queue = new PathRequestQueue();

        for (int agent = 0; agent < 6; agent++)
        {
            queue.Request(agent, new Vector3i(1, 2, 1), new Vector3i(5, 2, 5));
        }

        queue.Process(graph, budget: 3);

        Assert.Equal([0, 1, 2], queue.Answers.Select(answer => answer.AgentId));
    }
}
