using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Navigace přes hranice chunků: portály a dvoustupňové hledání.
/// </summary>
/// <remarks>
/// Nejzrádnější místo je hranice. Cesta, která se uvnitř chunku chová správně, umí na
/// hranici přeskočit blok, prosáknout zdí nebo skončit v chunku, který není načtený.
/// </remarks>
public sealed class NavGraphTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Souvislá podlaha přes zadaný počet chunků v ose X, patro y = 1.</summary>
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
    public void Portals_appear_on_the_boundary_between_two_chunks()
    {
        (VoxelWorld world, _) = FloorAcross(2);
        NavGraph graph = GraphOver(world, 2);

        Assert.Equal(2, graph.ChunkCount);

        // Na hranici je 32 buněk z každé strany, tedy 64 portálových buněk.
        Assert.Equal(64, graph.PortalCount);
    }

    [Fact]
    public void Single_chunk_alone_has_no_portals()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);

        Assert.Equal(0, graph.PortalCount);
    }

    [Fact]
    public void Path_crosses_a_chunk_boundary()
    {
        (VoxelWorld world, _) = FloorAcross(2);
        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        var start = new Vector3i(4, 2, 4);
        var goal = new Vector3i(40, 2, 4);

        Assert.Equal(PathResult.Found, graph.TryFindPath(start, goal, path));

        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);

        // Cesta musí opravdu přejít do druhého chunku.
        Assert.Contains(path, cell => cell.X >= NavGrid.Size);
    }

    /// <summary>
    /// Nejdůležitější test celého grafu: každý dva po sobě jdoucí body musí být skutečný krok.
    /// Chyba ve stitchování by dala cestu, která na hranici chunku "skočí" o kus dál.
    /// </summary>
    [Fact]
    public void Every_step_of_a_cross_chunk_path_is_a_real_step()
    {
        (VoxelWorld world, _) = FloorAcross(3);
        NavGraph graph = GraphOver(world, 3);
        var path = new List<Vector3i>();

        Assert.Equal(
            PathResult.Found,
            graph.TryFindPath(new Vector3i(2, 2, 6), new Vector3i(90, 2, 20), path));

        for (int i = 1; i < path.Count; i++)
        {
            Vector3i previous = path[i - 1];
            Vector3i current = path[i];

            int horizontal = Math.Abs(current.X - previous.X) + Math.Abs(current.Z - previous.Z);
            Assert.True(
                horizontal == 1,
                $"Krok {i} není sousední: {previous} → {current} (vodorovně {horizontal}).");

            Assert.InRange(current.Y - previous.Y, -Walkability.StepDown, Walkability.StepUp);
        }
    }

    [Fact]
    public void Every_cell_of_a_cross_chunk_path_is_standable()
    {
        (VoxelWorld world, _) = FloorAcross(2);
        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        Assert.Equal(
            PathResult.Found,
            graph.TryFindPath(new Vector3i(1, 2, 1), new Vector3i(60, 2, 30), path));

        foreach (Vector3i cell in path)
        {
            Assert.True(graph.IsStandable(cell), $"Buňka {cell} na cestě není pochůzná.");
        }
    }

    [Fact]
    public void Wall_sealing_the_boundary_makes_the_goal_unreachable()
    {
        (VoxelWorld world, ushort stone) = FloorAcross(2);

        // Zeď přes celou hranici na x = 31, dva bloky vysoká.
        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(NavGrid.Size - 1, 2, z, stone);
            world.SetBlock(NavGrid.Size - 1, 3, z, stone);
        }

        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        Assert.Equal(
            PathResult.Unreachable,
            graph.TryFindPath(new Vector3i(4, 2, 4), new Vector3i(40, 2, 4), path));

        Assert.Empty(path);
    }

    /// <summary>
    /// Chunk, který v grafu není, se nesmí brát jako průchozí prázdno — kolonista by
    /// mířil do světa, který se teprve streamuje.
    /// </summary>
    [Fact]
    public void Chunk_missing_from_the_graph_is_not_walkable()
    {
        (VoxelWorld world, _) = FloorAcross(2);

        // Do grafu jde jen první chunk, přestože svět má oba.
        var graph = new NavGraph();
        graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero));
        graph.BuildPortals();

        Assert.False(graph.IsStandable(new Vector3i(40, 2, 4)));
        Assert.Equal(0, graph.PortalCount);

        var path = new List<Vector3i>();
        Assert.Equal(
            PathResult.InvalidEndpoint,
            graph.TryFindPath(new Vector3i(4, 2, 4), new Vector3i(40, 2, 4), path));
    }

    [Fact]
    public void Path_inside_a_single_chunk_still_works_through_the_graph()
    {
        (VoxelWorld world, _) = FloorAcross(2);
        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        var start = new Vector3i(3, 2, 3);
        var goal = new Vector3i(9, 2, 3);

        Assert.Equal(PathResult.Found, graph.TryFindPath(start, goal, path));
        Assert.Equal(7, path.Count);
    }

    [Fact]
    public void Same_query_returns_the_same_path_every_time()
    {
        (VoxelWorld world, _) = FloorAcross(3);
        NavGraph graph = GraphOver(world, 3);

        var start = new Vector3i(5, 2, 5);
        var goal = new Vector3i(80, 2, 25);

        var first = new List<Vector3i>();
        Assert.Equal(PathResult.Found, graph.TryFindPath(start, goal, first));

        var scratch = new List<Vector3i>();
        graph.TryFindPath(new Vector3i(0, 2, 0), new Vector3i(60, 2, 31), scratch);

        var second = new List<Vector3i>();
        Assert.Equal(PathResult.Found, graph.TryFindPath(start, goal, second));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Endpoints_outside_any_loaded_chunk_are_rejected()
    {
        (VoxelWorld world, _) = FloorAcross(1);
        NavGraph graph = GraphOver(world, 1);
        var path = new List<Vector3i>();

        Assert.Equal(
            PathResult.InvalidEndpoint,
            graph.TryFindPath(new Vector3i(4, 2, 4), new Vector3i(4, 2, 500), path));
    }

    /// <summary>
    /// ROZHODOVACÍ TEST inkrementální aktualizace grafu: po kopnutí do zdi na hranici musí
    /// být stav grafu k nerozeznání od úplně nově postaveného.
    /// </summary>
    [Fact]
    public void Digging_through_a_boundary_wall_matches_a_freshly_built_graph()
    {
        (VoxelWorld world, ushort stone) = FloorAcross(2);

        for (int z = 0; z < NavGrid.Size; z++)
        {
            world.SetBlock(NavGrid.Size - 1, 2, z, stone);
            world.SetBlock(NavGrid.Size - 1, 3, z, stone);
        }

        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        var start = new Vector3i(4, 2, 4);
        var goal = new Vector3i(40, 2, 4);
        Assert.Equal(PathResult.Unreachable, graph.TryFindPath(start, goal, path));

        // Prokopat díru: dva bloky nad sebou na z = 4. Po prvním se pochůznost NEZMĚNÍ —
        // zeď je dva bloky vysoká, takže dokud stojí druhý blok, tvor se do mezery nevejde.
        world.SetBlock(NavGrid.Size - 1, 2, 4, BlockRegistry.Air);
        Assert.False(graph.OnBlockChanged(world, world.Registry, NoWater, NavGrid.Size - 1, 2, 4));

        world.SetBlock(NavGrid.Size - 1, 3, 4, BlockRegistry.Air);
        Assert.True(graph.OnBlockChanged(world, world.Registry, NoWater, NavGrid.Size - 1, 3, 4));

        Assert.Equal(PathResult.Found, graph.TryFindPath(start, goal, path));

        // A hlavně: stav musí sedět na graf postavený od nuly nad týmž světem.
        NavGraph rebuilt = GraphOver(world, 2);
        Assert.Equal(rebuilt.PortalCount, graph.PortalCount);

        var reference = new List<Vector3i>();
        Assert.Equal(PathResult.Found, rebuilt.TryFindPath(start, goal, reference));
        Assert.Equal(reference, path);
    }

    [Fact]
    public void Walling_the_boundary_up_again_closes_the_path()
    {
        (VoxelWorld world, ushort stone) = FloorAcross(2);
        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        var start = new Vector3i(4, 2, 4);
        var goal = new Vector3i(40, 2, 4);
        Assert.Equal(PathResult.Found, graph.TryFindPath(start, goal, path));

        // Zazdít celou hranici a hlásit každou změnu.
        for (int z = 0; z < NavGrid.Size; z++)
        {
            for (int y = 2; y <= 3; y++)
            {
                world.SetBlock(NavGrid.Size - 1, y, z, stone);
                graph.OnBlockChanged(world, world.Registry, NoWater, NavGrid.Size - 1, y, z);
            }
        }

        Assert.Equal(PathResult.Unreachable, graph.TryFindPath(start, goal, path));
    }

    [Fact]
    public void Change_that_alters_nothing_reports_no_change()
    {
        (VoxelWorld world, _) = FloorAcross(2);
        NavGraph graph = GraphOver(world, 2);

        // Vysoko ve vzduchu: nic pochůzného tam není a nic se nemění.
        Assert.False(graph.OnBlockChanged(world, world.Registry, NoWater, 4, 25, 4));
    }

    /// <summary>
    /// Tunel přes hranici s jedinou průchozí buňkou. Cesta musí existovat a musí jít tudy —
    /// tohle je nejbližší podoba „kolonista jde tunelem" z povinných testů M0.
    /// </summary>
    [Fact]
    public void Path_uses_the_only_passable_cell_on_the_boundary()
    {
        (VoxelWorld world, ushort stone) = FloorAcross(2);

        const int OpenZ = 12;
        for (int z = 0; z < NavGrid.Size; z++)
        {
            if (z == OpenZ)
            {
                continue;
            }

            world.SetBlock(NavGrid.Size - 1, 2, z, stone);
            world.SetBlock(NavGrid.Size - 1, 3, z, stone);
        }

        NavGraph graph = GraphOver(world, 2);
        var path = new List<Vector3i>();

        Assert.Equal(
            PathResult.Found,
            graph.TryFindPath(new Vector3i(4, 2, 4), new Vector3i(40, 2, 28), path));

        Assert.Contains(path, cell => cell.X == NavGrid.Size - 1 && cell.Z == OpenZ);
    }
}
