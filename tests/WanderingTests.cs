using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Kolonista bez práce se prochází po domovské oblasti.
/// </summary>
/// <remarks>
/// <para><b>Bez tohohle vypadá kolonie mrtvě.</b> Hráč postavil radnici, přišel člověk
/// a nehnul se — doslova usnul ve frontě a čekal na příkaz. Žádné jídlo ani stroje ten
/// dojem nespraví, dokud postavy nežijí.</para>
///
/// <para>Chodí se <b>bez hledání cesty</b>: krok na sousední pochůznou buňku. Jedno A* stojí
/// naměřených 0,858 ms a do tiku se jich vejde osm, takže dvě stě zahálejících lidí by
/// potulkou sežralo celý rozpočet navigace.</para>
/// </remarks>
public sealed class WanderingTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static VoxelWorld Floor()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 0, z, stone);
                world.SetBlock(x, 1, z, stone);
            }
        }

        return world;
    }

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(ushort.MaxValue);
        colony.ResolveFood(world.Registry);

        for (int i = 0; i < 10; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    /// <summary>
    /// BEZ OPRAVY TENHLE TEST PADÁ: kolonista bez práce dřív stál na jednom místě.
    /// </summary>
    [Fact]
    public void A_colonist_with_no_work_walks_around_home()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);
        Assert.True(colony.FoundTownHall(new Vector3i(8, 2, 8)));

        int colonist = colony.TrySpawnColonist(new Vector3i(8, 2, 6));
        Assert.True(colonist >= 0);

        Vector3i start = colony.Colonists.CellOf(colonist);
        var navstiveno = new HashSet<Vector3i> { start };

        for (int tick = 0; tick < 3_000; tick++)
        {
            colony.Tick(world, world.Registry);
            navstiveno.Add(colony.Colonists.CellOf(colonist));
        }

        output.WriteLine($"Zacal na {start}, skoncil na {colony.Colonists.CellOf(colonist)}, "
            + $"navstivil {navstiveno.Count} bunek.");

        // Nestačí, že se hnul jednou — má se procházet.
        Assert.True(navstiveno.Count >= 4, $"Prošel jen {navstiveno.Count} buněk, to není procházka.");
    }

    /// <summary>
    /// Procházející se pořád počítá mezi VOLNÉ. To číslo je podle sekce 2 jádro hry.
    /// </summary>
    [Fact]
    public void A_wandering_colonist_still_counts_as_free()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);
        colony.FoundTownHall(new Vector3i(8, 2, 8));

        for (int i = 0; i < 3; i++)
        {
            Assert.True(colony.TrySpawnColonist(new Vector3i(6 + i, 2, 6)) >= 0);
        }

        int nejmene = 3;
        for (int tick = 0; tick < 1_500; tick++)
        {
            colony.Tick(world, world.Registry);
            nejmene = Math.Min(nejmene, colony.FreeColonists);
        }

        output.WriteLine($"Nejmene volnych za beh: {nejmene} ze 3.");
        Assert.Equal(3, nejmene);
    }

    /// <summary>
    /// Chodí plynule a šikmo, ne po celých buňkách.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ SPOJITÉ POLOHY TENHLE TEST PADÁ.</b> Dokud byla poloha <c>Vector3i</c>,
    /// byl každý posun přesně o celou buňku a nikdy o zlomek — takže dílčích kroků bylo NULA
    /// a šikmý pohyb existoval jen tehdy, když se obě osy změnily naráz.</para>
    ///
    /// <para><b>Kontrolní úvaha: co by prošlo i s rozbitou věcí.</b> „Hnul se" projde vždycky,
    /// protože kolonista se hýbal i po buňkách. „Ušel dohromady hodně" taky. Rozhoduje proto
    /// jediné: byl posun MENŠÍ než celá buňka? To po mřížce nešlo.</para>
    /// </remarks>
    [Fact]
    public void Wandering_moves_continuously_and_diagonally()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);
        colony.FoundTownHall(new Vector3i(8, 2, 8));
        int colonist = colony.TrySpawnColonist(new Vector3i(8, 2, 8));

        Vector3 predchozi = colony.Colonists.PositionOf(colonist);
        int dilcichKroku = 0;
        int sikmych = 0;
        float ujito = 0f;
        float nejdelsiPosun = 0f;

        for (int tick = 0; tick < 6_000; tick++)
        {
            colony.Tick(world, world.Registry);
            Vector3 ted = colony.Colonists.PositionOf(colonist);

            float dx = ted.X - predchozi.X;
            float dz = ted.Z - predchozi.Z;
            float posun = MathF.Sqrt((dx * dx) + (dz * dz));

            if (posun > 1e-4f)
            {
                ujito += posun;
                nejdelsiPosun = MathF.Max(nejdelsiPosun, posun);

                // DÍLČÍ krok: menší než celá buňka. Přesně tohle po mřížce nešlo.
                if (posun < 0.9f)
                {
                    dilcichKroku++;
                }

                // ŠIKMO: obě osy naráz, a ne jen o zlomek jedné z nich.
                if (MathF.Abs(dx) > 1e-4f && MathF.Abs(dz) > 1e-4f)
                {
                    sikmych++;
                }
            }

            predchozi = ted;
        }

        output.WriteLine($"Dilcich kroku {dilcichKroku}, z toho sikmych {sikmych}, "
            + $"ujito {ujito:F1} bloku, nejdelsi posun za tik {nejdelsiPosun:F3} bloku.");

        Assert.True(dilcichKroku > 100, $"Dilcich kroku bylo jen {dilcichKroku} — pohyb je pořád po buňkách.");
        Assert.True(sikmych > 0, "Nešel ani jednou šikmo — pořád je to šachovnice.");

        // Za tik se nesmí ujít víc, než kolik dovolí rychlost procházky. Kdyby ano,
        // znamenalo by to, že se někde propašoval skok o celou buňku.
        Assert.True(
            nejdelsiPosun <= (ColonistBody.WanderSpeed * ColonistBody.StepSeconds) + 0.01f,
            $"Za jeden tik se ušlo {nejdelsiPosun:F3} bloku, což je skok, ne chůze.");
    }

    /// <summary>
    /// Kolonista chodí stejně rychle jako hráč, ne rychlostí jeho sprintu.
    /// </summary>
    /// <remarks>
    /// BEZ OPRAVY PADÁ: krok trval šest tiků, tedy blok za desetinu vteřiny = 10 m/s, což je
    /// přesně hráčův sprint (4,6 × 2,2). Kolonisté pobíhali rychleji, než hráč chodí.
    /// </remarks>
    [Fact]
    public void Colonists_walk_at_the_same_speed_as_the_player()
    {
        float bunekZaVterinu = 60f / ColonySimulation.TicksPerStep;

        output.WriteLine($"Kolonista {bunekZaVterinu:F2} b/s, hrac {Tesseris.Game.Player.PlayerController.WalkSpeed:F2} m/s.");

        Assert.Equal(Tesseris.Game.Player.PlayerController.WalkSpeed, bunekZaVterinu, 0.3f);
    }

    /// <summary>
    /// Z domova se neodchází. Bez toho by se kolonie rozešla do světa.
    /// </summary>
    [Fact]
    public void Wandering_stays_inside_the_home_area()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);
        var hall = new Vector3i(8, 2, 8);
        colony.FoundTownHall(hall);

        int colonist = colony.TrySpawnColonist(new Vector3i(8, 2, 6));

        int nejdal = 0;
        for (int tick = 0; tick < 6_000; tick++)
        {
            colony.Tick(world, world.Registry);
            Vector3i delta = colony.Colonists.CellOf(colonist) - hall;
            nejdal = Math.Max(nejdal, Math.Max(Math.Abs(delta.X), Math.Abs(delta.Z)));
        }

        output.WriteLine($"Nejdal od radnice: {nejdal} (mez {ColonySimulation.HomeRadius}).");
        Assert.True(nejdal <= ColonySimulation.HomeRadius, $"Odešel {nejdal} buněk od radnice.");
    }

    /// <summary>
    /// Bez radnice není domov — a pak se nikdo netoulá, jen stojí.
    /// </summary>
    [Fact]
    public void Without_a_town_hall_nobody_wanders()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);

        int colonist = colony.TrySpawnColonist(new Vector3i(8, 2, 6));
        Vector3i start = colony.Colonists.CellOf(colonist);

        for (int tick = 0; tick < 1_000; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        Assert.Equal(start, colony.Colonists.CellOf(colonist));
    }

    /// <summary>
    /// Práce má přednost před procházkou. Jinak by hráč zadal úkol a nikdo by nešel.
    /// </summary>
    [Fact]
    public void Marked_work_interrupts_the_stroll()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");
        ColonyRuntime colony = Colony(world);
        colony.FoundTownHall(new Vector3i(8, 2, 8));
        colony.TrySpawnColonist(new Vector3i(8, 2, 6));

        // Chvíli ať se prochází.
        for (int tick = 0; tick < 600; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        for (int x = 10; x < 13; x++)
        {
            world.SetBlock(x, 2, 10, stone);
            colony.OnBlockChanged(world, world.Registry, new Vector3i(x, 2, 10));
        }

        int marked = colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(12, 2, 10));
        Assert.Equal(3, marked);

        for (int tick = 0; tick < 6_000 && colony.Jobs.DoneCount < marked; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        output.WriteLine($"Vykopano {colony.Jobs.DoneCount} z {marked}.");
        Assert.Equal(marked, colony.Jobs.DoneCount);
    }
}
