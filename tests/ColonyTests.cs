using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Fronta úkolů a kolonisté (T3). Kritérium M0: označím kus stěny, kolonisté ji vykopou,
/// materiál skončí na skladu a nikdo se nezasekne.
/// </summary>
public sealed class ColonyTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Podlaha na y = 1 přes celý chunk, tedy stání na y = 2.</summary>
    private static (VoxelWorld World, ushort Stone) FloorWorld()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        return (world, stone);
    }

    private static NavGraph GraphOf(VoxelWorld world)
    {
        var graph = new NavGraph();
        graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero));
        graph.BuildPortals();
        return graph;
    }

    // ================= FRONTA ÚKOLŮ =================

    [Fact]
    public void Marking_an_area_creates_one_job_per_solid_block()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        // Stěna 3×2×1 nad podlahou.
        for (int x = 5; x < 8; x++)
        {
            for (int y = 2; y < 4; y++)
            {
                world.SetBlock(x, y, 10, stone);
            }
        }

        var jobs = new DigJobQueue();
        int added = jobs.MarkArea(world, world.Registry, new Vector3i(5, 2, 10), new Vector3i(7, 3, 10));

        Assert.Equal(6, added);
        Assert.Equal(6, jobs.OpenCount);
    }

    [Fact]
    public void Marking_empty_space_creates_nothing()
    {
        (VoxelWorld world, _) = FloorWorld();
        var jobs = new DigJobQueue();

        Assert.Equal(0, jobs.MarkArea(world, world.Registry, new Vector3i(5, 10, 5), new Vector3i(8, 12, 8)));
        Assert.Equal(0, jobs.Count);
    }

    [Fact]
    public void Nearest_job_is_claimed_first()
    {
        var jobs = new DigJobQueue();
        jobs.Add(new Vector3i(20, 2, 20));
        int near = jobs.Add(new Vector3i(3, 2, 3));
        jobs.Add(new Vector3i(30, 2, 30));

        Assert.True(jobs.TryClaimNearest(new Vector3i(2, 2, 2), colonist: 0, out int job));

        Assert.Equal(near, job);
        Assert.Equal(JobState.Claimed, jobs.StateOf(job));
        Assert.Equal(0, jobs.ClaimedBy(job));
        Assert.Equal(2, jobs.OpenCount);
    }

    [Fact]
    public void Claimed_job_is_not_handed_out_twice()
    {
        var jobs = new DigJobQueue();
        jobs.Add(new Vector3i(3, 2, 3));

        Assert.True(jobs.TryClaimNearest(Vector3i.Zero, 0, out int first));
        Assert.False(jobs.TryClaimNearest(Vector3i.Zero, 1, out _));

        jobs.Release(first);
        Assert.True(jobs.TryClaimNearest(Vector3i.Zero, 1, out _));
    }

    [Fact]
    public void Counts_stay_consistent_through_the_whole_lifecycle()
    {
        var jobs = new DigJobQueue();
        for (int i = 0; i < 5; i++)
        {
            jobs.Add(new Vector3i(i, 2, 0));
        }

        Assert.Equal(5, jobs.OpenCount);

        jobs.TryClaimNearest(Vector3i.Zero, 0, out int a);
        jobs.TryClaimNearest(Vector3i.Zero, 1, out int b);
        Assert.Equal(3, jobs.OpenCount);
        Assert.Equal(2, jobs.ClaimedCount);

        jobs.Complete(a);
        jobs.Release(b);
        Assert.Equal(4, jobs.OpenCount);
        Assert.Equal(0, jobs.ClaimedCount);
        Assert.Equal(1, jobs.DoneCount);

        // Opakované dokončení nesmí počítadla rozhodit.
        jobs.Complete(a);
        Assert.Equal(1, jobs.DoneCount);
    }

    // ================= KOLONISTÉ =================

    [Fact]
    public void New_colonist_counts_as_idle()
    {
        var colony = new ColonySimulation();
        colony.Add(new Vector3i(4, 2, 4));

        Assert.Equal(1, colony.Count);
        Assert.Equal(1, colony.IdleCount);
        Assert.Equal(ColonistState.Idle, colony.StateOf(0));
    }

    [Fact]
    public void Colonist_without_work_falls_asleep()
    {
        (VoxelWorld world, _) = FloorWorld();
        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation();
        colony.Add(new Vector3i(4, 2, 4));

        colony.Tick(world, world.Registry, NoWater, graph, jobs);

        // Pravidlo 6.4: kdo nemá co dělat, netiká se.
        Assert.Equal(0, colony.ActiveCount);
        Assert.Equal(1, colony.IdleCount);
    }

    [Fact]
    public void New_work_wakes_the_idle_colonists_up()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();
        world.SetBlock(8, 2, 8, stone);

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation();
        colony.Add(new Vector3i(4, 2, 4));

        colony.Tick(world, world.Registry, NoWater, graph, jobs);
        Assert.Equal(0, colony.ActiveCount);

        jobs.Add(new Vector3i(8, 2, 8));
        colony.WakeIdle();

        Assert.Equal(1, colony.ActiveCount);
    }

    /// <summary>
    /// KRITÉRIUM M0: označím kus stěny, kolonisté ji vykopou a materiál skončí na skladu.
    /// </summary>
    [Fact]
    public void Colonists_dig_a_marked_wall_and_deliver_the_material()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        // Vyvýšenina 3×2 bloku, JEDNU ŘADU VYSOKÁ, na y = 2.
        //
        // Schválně ne dvě řady na sobě: u bloku ve výšce 3 je jediná sousední pochůzná
        // buňka nahoře na zdi (y = 4) a to je krok o dva bloky z podlahy, tedy víc než
        // povolený jeden. Taková práce z podlahy nejde udělat a kolonisté ji správně
        // odloží — na to je vlastní test níž.
        for (int x = 10; x < 13; x++)
        {
            for (int z = 12; z < 14; z++)
            {
                world.SetBlock(x, 2, z, stone);
            }
        }

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        int marked = jobs.MarkArea(world, world.Registry, new Vector3i(10, 2, 12), new Vector3i(12, 2, 13));
        Assert.Equal(6, marked);

        var colony = new ColonySimulation();
        for (int i = 0; i < 3; i++)
        {
            colony.Add(new Vector3i(2 + i, 2, 2));
        }

        colony.WakeIdle();

        // Čeká se na MATERIÁL NA SKLADU, ne jen na vykopání. Kritérium M0 zní „materiál
        // skončí na skladu" a poslední kolonista ho v okamžiku dokopání ještě nese.
        int ticks = 0;
        while (colony.StoredItems < marked && ticks < 20_000)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
            colony.WakeIdle();
            ticks++;
        }

        output.WriteLine($"Šest bloků vykopali tři kolonisté za {ticks} tiků "
            + $"({ticks / 60.0:F1} s herního času).");

        Assert.Equal(marked, jobs.DoneCount);
        Assert.Equal(marked, colony.StoredItems);

        // Stěna je opravdu pryč ze světa, nejen z fronty.
        for (int x = 10; x < 13; x++)
        {
            for (int z = 12; z < 14; z++)
            {
                Assert.Equal(BlockRegistry.Air, world.GetBlock(x, 2, z));
            }
        }

        // A všichni jsou zase volní — nikdo se nezasekl.
        Assert.Equal(3, colony.IdleCount);
    }

    /// <summary>
    /// POVINNÝ TEST M0 — PROPAD. Kolonista stojí na voxelu, který někdo vykope. Musí spadnout,
    /// ne propadnout mimo svět.
    /// </summary>
    [Fact]
    public void Colonist_falls_when_the_block_under_him_is_dug_out()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Sloup: dno na y = 1, pilíř na y = 2 a 3. Kolonista bude stát na y = 4.
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        world.SetBlock(4, 2, 4, stone);
        world.SetBlock(4, 3, 4, stone);

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation();
        int id = colony.Add(new Vector3i(4, 4, 4));

        Assert.Equal(4, colony.CellOf(id).Y);

        // Někdo vykope vršek pilíře.
        world.SetBlock(4, 3, 4, BlockRegistry.Air);
        graph.OnBlockChanged(world, world.Registry, NoWater, 4, 3, 4);

        colony.WakeIdle();

        // ČEKÁ SE NA DOPAD, NE JEDEN TIK. Pád je od zavedení fyzického těla spojitý: za tik
        // urazí kolonista 30/60/60 = 0,008 bloku, takže po jediném tiku VISÍ na 3,99 a to,
        // že `CellOf` vrátí 3, je jen zaokrouhlení dolů. Tenhle test to tak měl a procházel
        // by i tehdy, kdyby se kolonista nikdy nedotkl země.
        int tiku = TickUntilLands(colony, world, graph, jobs, id, floor: 3);
        output.WriteLine($"Pad o jedno patro trval {tiku} tiku.");

        // Spadl přesně o patro, na vršek zbylého pilíře — a stojí, ne visí.
        Assert.Equal(new Vector3i(4, 3, 4), colony.CellOf(id));
        Assert.True(colony.IsOnGround(id), "Nedopadl, jen se zaokrouhlil na správné patro.");

        // A teď pryč s celým pilířem — musí dopadnout až na podlahu, ne propadnout.
        world.SetBlock(4, 2, 4, BlockRegistry.Air);
        graph.OnBlockChanged(world, world.Registry, NoWater, 4, 2, 4);

        colony.WakeIdle();
        tiku = TickUntilLands(colony, world, graph, jobs, id, floor: 2);
        output.WriteLine($"Pad na podlahu trval {tiku} tiku.");

        Assert.Equal(new Vector3i(4, 2, 4), colony.CellOf(id));
        Assert.True(colony.CellOf(id).Y > 0, "Kolonista propadl světem.");
        Assert.True(colony.IsOnGround(id), "Zůstal viset ve vzduchu.");
    }

    /// <summary>Tiká, dokud kolonista nedopadne na zadané patro. Vrací počet tiků.</summary>
    private static int TickUntilLands(
        ColonySimulation colony,
        VoxelWorld world,
        NavGraph graph,
        DigJobQueue jobs,
        int id,
        int floor)
    {
        for (int tick = 1; tick <= 600; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);

            if (colony.IsOnGround(id) && colony.CellOf(id).Y == floor)
            {
                return tick;
            }
        }

        return -1;
    }

    /// <summary>
    /// POVINNÝ TEST M0 — NEDOSAŽITELNÝ ÚKOL. Kolonista se nesmí zaseknout: úkol vrátí
    /// a je zase volný.
    /// </summary>
    [Fact]
    public void Unreachable_job_does_not_freeze_the_colonist()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        // Kobka: blok uprostřed obezděný ze všech stran dvěma patry.
        var target = new Vector3i(20, 2, 20);
        world.SetBlock(target.X, target.Y, target.Z, stone);
        foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            for (int y = 2; y <= 4; y++)
            {
                world.SetBlock(target.X + dx, y, target.Z + dz, stone);
            }
        }

        // A strop, aby se na cíl nedalo přijít shora.
        world.SetBlock(target.X, target.Y + 1, target.Z, stone);
        world.SetBlock(target.X, target.Y + 2, target.Z, stone);

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        jobs.Add(target);

        var colony = new ColonySimulation();
        colony.Add(new Vector3i(3, 2, 3));
        colony.WakeIdle();

        for (int tick = 0; tick < 200; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
        }

        // Nezasekl se ve WaitingForPath ani v Moving.
        Assert.Equal(ColonistState.Idle, colony.StateOf(0));
        Assert.Equal(-1, colony.JobOf(0));
    }

    /// <summary>
    /// Vrchní řada zdi vysoké dva bloky je z podlahy nedosažitelná: jediná sousední pochůzná
    /// buňka je nahoře na zdi a to je krok o dva bloky. Kolonisté ji musí ODLOŽIT, ne se
    /// o ni donekonečna přetahovat — a spodní řadu přitom normálně vykopat.
    /// </summary>
    [Fact]
    public void Unreachable_top_row_is_deferred_while_the_bottom_row_gets_dug()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();

        for (int x = 10; x < 13; x++)
        {
            world.SetBlock(x, 2, 12, stone);
            world.SetBlock(x, 3, 12, stone);
        }

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        int marked = jobs.MarkArea(world, world.Registry, new Vector3i(10, 2, 12), new Vector3i(12, 3, 12));
        Assert.Equal(6, marked);

        var colony = new ColonySimulation();
        colony.Add(new Vector3i(3, 2, 3));
        colony.WakeIdle();

        for (int tick = 0; tick < 5_000; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
            colony.WakeIdle();
        }

        output.WriteLine($"Hotovo {jobs.DoneCount}, odloženo {jobs.DeferredCount}, "
            + $"volných {jobs.OpenCount}, stav kolonisty {colony.StateOf(0)}.");

        // Spodní řada šla vykopat, vrchní zůstala — a kolonista se u toho nezasekl.
        Assert.True(jobs.DoneCount > 0, "Nevykopalo se nic, i když spodní řada dosažitelná je.");
        Assert.True(jobs.DoneCount < marked, "Vrchní řada se vykopat neměla — nedá se k ní dostat.");
        Assert.Equal(ColonistState.Idle, colony.StateOf(0));
        Assert.Equal(-1, colony.JobOf(0));
    }

    [Fact]
    public void Abandoning_returns_every_claimed_job_to_the_queue()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();
        for (int x = 10; x < 14; x++)
        {
            world.SetBlock(x, 2, 12, stone);
        }

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        jobs.MarkArea(world, world.Registry, new Vector3i(10, 2, 12), new Vector3i(13, 2, 12));

        var colony = new ColonySimulation();
        colony.Add(new Vector3i(2, 2, 2));
        colony.Add(new Vector3i(3, 2, 2));
        colony.WakeIdle();

        colony.Tick(world, world.Registry, NoWater, graph, jobs);
        Assert.True(jobs.ClaimedCount > 0);

        colony.AbandonAll(jobs);

        Assert.Equal(0, jobs.ClaimedCount);
        Assert.Equal(4, jobs.OpenCount);
        Assert.Equal(2, colony.IdleCount);
    }

    [Fact]
    public void Two_hundred_colonists_can_be_ticked()
    {
        (VoxelWorld world, ushort stone) = FloorWorld();
        for (int x = 4; x < 28; x++)
        {
            world.SetBlock(x, 2, 20, stone);
        }

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        jobs.MarkArea(world, world.Registry, new Vector3i(4, 2, 20), new Vector3i(27, 2, 20));

        var colony = new ColonySimulation(capacity: 256);
        for (int i = 0; i < 200; i++)
        {
            colony.Add(new Vector3i((i % 24) + 2, 2, (i / 24) + 2));
        }

        colony.WakeIdle();

        for (int tick = 0; tick < 500; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
            colony.WakeIdle();
        }

        output.WriteLine($"200 kolonistů, 500 tiků: hotovo {jobs.DoneCount} úkolů, "
            + $"na skladu {colony.StoredItems}, volných {colony.IdleCount}.");

        // Nikdo neuvízl v nekonečném čekání a práce se udělala.
        Assert.True(jobs.DoneCount > 0, "Za 500 tiků se nevykopalo nic.");
    }
}
