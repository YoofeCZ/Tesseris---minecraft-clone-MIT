using System.Diagnostics;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Měření hledání cest. Povinný benchmark z M0 (úkol T2).
/// </summary>
/// <remarks>
/// <para><b>Co tenhle benchmark je a co není.</b> Je to měření <b>ceny jedné cesty</b> na
/// realistickém terénu přes několik chunků, ze kterého se dopočítá, kolik hledání se vejde
/// do rozpočtu tiku. Není to měření běžící hry — sto kolonistů
/// nehledá cestu každý tik, hledají ji, když dostanou úkol nebo když se jim cesta rozbije.</para>
///
/// <para><b>Čísla jsou orientační.</b> Běží to uvnitř testovací sady, která pouští testy
/// paralelně, takže je stroj zašuměný. Bere se medián ze tří opakování a hranice jsou volné —
/// tenhle test má odhalit řádovou katastrofu, ne hlídat procenta.</para>
/// </remarks>
public sealed class NavigationBenchmarkTests(ITestOutputHelper output)
{
    private const int ChunksPerSide = 4;
    private const int Colonists = 200;
    private const double TickBudgetMs = 8.0;

    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    /// <summary>
    /// Terén se schody a překážkami, ne rovná deska. Na rovné desce jde A* rovnou za nosem
    /// a naměřilo by se to nejlepší možné číslo, které o skutečné hře nic neříká.
    /// </summary>
    private static (VoxelWorld World, NavGraph Graph) BuildWorld()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        int span = ChunksPerSide * NavGrid.Size;

        for (int x = 0; x < span; x++)
        {
            for (int z = 0; z < span; z++)
            {
                // Terasovitá podlaha: výška se mění po osmi blocích o jedno patro.
                int height = 1 + (((x / 8) + (z / 8)) % 3);
                for (int y = 1; y <= height; y++)
                {
                    world.SetBlock(x, y, z, stone);
                }
            }
        }

        // Zdi s průchody, aby A* musel obcházet.
        for (int x = 0; x < span; x++)
        {
            if (x % 24 < 20)
            {
                for (int y = 1; y <= 6; y++)
                {
                    world.SetBlock(x, y, span / 2, stone);
                }
            }
        }

        var graph = new NavGraph();
        for (int cx = 0; cx < ChunksPerSide; cx++)
        {
            for (int cz = 0; cz < ChunksPerSide; cz++)
            {
                graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, new Vector3i(cx, 0, cz)));
            }
        }

        graph.BuildPortals();
        return (world, graph);
    }

    private static ushort NoWater => ushort.MaxValue;

    private static Vector3i? FindStandable(NavGraph graph, int x, int z)
    {
        for (int y = NavGrid.Size - 1; y >= 0; y--)
        {
            var cell = new Vector3i(x, y, z);
            if (graph.IsStandable(cell))
            {
                return cell;
            }
        }

        return null;
    }

    [Fact]
    public void Building_navigation_for_sixteen_chunks_is_reported()
    {
        var timings = new List<double>();
        for (int run = 0; run < 3; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            BuildWorld();
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        double median = timings[1];
        output.WriteLine(
            $"Stavba světa + {ChunksPerSide * ChunksPerSide} mřížek + portály: "
            + $"medián {median:F1} ms ze tří běhů ({string.Join(" / ", timings.Select(t => $"{t:F1}"))}).");

        // Mřížky se staví mimo tick, takže tady jde jen o to, aby to nebylo absurdní.
        Assert.True(median < 20_000, $"Stavba navigace trvala {median:F0} ms.");
    }

    /// <summary>
    /// POVINNÝ BENCHMARK M0: 200 kolonistů s cílem přes několik chunků.
    /// </summary>
    [Fact]
    public void Two_hundred_colonists_pathfinding_is_measured_against_the_tick_budget()
    {
        (_, NavGraph graph) = BuildWorld();
        int span = ChunksPerSide * NavGrid.Size;

        // Deterministické rozmístění, ať je běh opakovatelný.
        var random = new Random(20260816);
        var pairs = new List<(Vector3i Start, Vector3i Goal)>(Colonists);

        while (pairs.Count < Colonists)
        {
            Vector3i? start = FindStandable(graph, random.Next(0, span), random.Next(0, span));
            Vector3i? goal = FindStandable(graph, random.Next(0, span), random.Next(0, span));

            if (start is null || goal is null || start == goal)
            {
                continue;
            }

            pairs.Add((start.Value, goal.Value));
        }

        var path = new List<Vector3i>();
        var results = new Dictionary<PathResult, int>();
        var timings = new List<double>();

        for (int run = 0; run < 3; run++)
        {
            results.Clear();
            var stopwatch = Stopwatch.StartNew();

            foreach ((Vector3i start, Vector3i goal) in pairs)
            {
                PathResult result = graph.TryFindPath(start, goal, path);
                results[result] = results.GetValueOrDefault(result) + 1;
            }

            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        double median = timings[1];
        double perPath = median / Colonists;
        double fitInTick = TickBudgetMs / perPath;

        output.WriteLine($"=== {Colonists} kolonistů, {ChunksPerSide}×{ChunksPerSide} chunků ===");
        output.WriteLine($"Celkem:        medián {median:F1} ms ze tří běhů "
            + $"({string.Join(" / ", timings.Select(t => $"{t:F1}"))})");
        output.WriteLine($"Na cestu:      {perPath:F3} ms");
        output.WriteLine($"Vejde se do tiku ({TickBudgetMs:F0} ms): {fitInTick:F0} hledání");
        foreach ((PathResult result, int count) in results.OrderBy(pair => pair.Key.ToString()))
        {
            output.WriteLine($"  {result,-18}: {count}");
        }

        // Většina dvojic musí mít cestu — jinak by benchmark měřil hlavně selhání.
        Assert.True(
            results.GetValueOrDefault(PathResult.Found) > Colonists / 2,
            $"Cestu našlo jen {results.GetValueOrDefault(PathResult.Found)} z {Colonists}.");

        // ŘÁDOVÁ POJISTKA, ne přesná hranice. Kdyby jedna cesta stála víc než celý tick,
        // je celý přístup špatně a musí se to řešit teď, ne v desátém měsíci.
        Assert.True(
            perPath < TickBudgetMs,
            $"Jedna cesta stojí {perPath:F2} ms, což je víc než celý rozpočet tiku {TickBudgetMs:F0} ms.");
    }

    /// <summary>
    /// Kolonista jde a terén se pod ním mění. Měří se přestavba mřížky jednoho chunku,
    /// protože přesně to se musí stát při každém kopnutí.
    /// </summary>
    [Fact]
    public void Rebuilding_one_chunk_grid_after_a_dig_is_measured()
    {
        (VoxelWorld world, _) = BuildWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        var timings = new List<double>();
        for (int run = 0; run < 3; run++)
        {
            world.SetBlock(10, 3, 10, run % 2 == 0 ? stone : BlockRegistry.Air);

            var stopwatch = Stopwatch.StartNew();
            NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        double median = timings[1];
        output.WriteLine($"Přestavba mřížky jednoho chunku: medián {median:F2} ms ze tří běhů "
            + $"({string.Join(" / ", timings.Select(t => $"{t:F2}"))}).");
        output.WriteLine($"Vejde se do tiku ({TickBudgetMs:F0} ms): {TickBudgetMs / median:F0} přestaveb.");

        // Tohle je to číslo, které rozhodne, jestli půjde přepočítávat mřížku při každém
        // kopnutí, nebo se bude muset dělat inkrementálně.
        Assert.True(median < TickBudgetMs * 4, $"Přestavba mřížky trvá {median:F1} ms.");
    }

    /// <summary>
    /// Kolik stojí mřížka nad PLNÝM chunkem, ne nad rovnou podlahou.
    /// </summary>
    /// <remarks>
    /// <para><b>Tenhle rozdíl schoval dvacetinásobek.</b> Test výš staví terasu vysokou jedno
    /// až tři patra, tedy skoro samý vzduch, a hlásí 3,23 ms — což je číslo, které se pak
    /// citovalo po celé codebase jako „mřížka stojí 3,34 ms". Skutečný svět je ale pod
    /// povrchem plný kamene: <c>NavGrid.Build</c> prochází všech 32 768 buněk a u každé,
    /// která má pod sebou pevný blok, se ptá <c>Walkability.TrySupportSurface</c> na tvar
    /// a pak <c>PlayerController.IsFree</c> na obal tvora. Obojí jde přes <c>yield return</c>
    /// iterátory, tedy alokace na buňku.</para>
    ///
    /// <para><b>Naměřeno v běžící hře: 67,75 ms na jeden chunk</b> proti rozpočtu celého tiku
    /// 8 ms — a rozpad po kategoriích to neukazoval, protože stavba mřížky sedí uvnitř fáze
    /// „pathfinding", která hlásila 1,1 ms. Čas ležel ve stavbě, ne v hledání cest.</para>
    ///
    /// <para><b>Co by prošlo i s rozbitou věcí:</b> přesně ten test výš. Rozhoduje, jaký
    /// svět se staví — a plný chunk je ten, který hra staví pod zemí pořád.</para>
    /// </remarks>
    [Fact]
    public void Building_a_grid_over_solid_rock_is_measured()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // PLNÝ CHUNK. Tak vypadá všechno pod povrchem, a přesně tam kolonie kope.
        for (int y = 0; y < NavGrid.Size; y++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                for (int x = 0; x < NavGrid.Size; x++)
                {
                    world.SetBlock(x, y, z, stone);
                }
            }
        }

        // Chodby, ať mřížka není celá prázdná: bez pochůzných buněk by se nezměřilo nic.
        for (int z = 0; z < NavGrid.Size; z += 4)
        {
            for (int x = 0; x < NavGrid.Size; x++)
            {
                world.SetBlock(x, 10, z, BlockRegistry.Air);
                world.SetBlock(x, 11, z, BlockRegistry.Air);
            }
        }

        var timings = new List<double>();
        for (int run = 0; run < 3; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            NavGrid grid = NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);

            Assert.True(grid.StandableCount > 0, "V mřížce nad plným kamenem se nedá nikde stát.");
        }

        timings.Sort();
        double median = timings[1];

        output.WriteLine($"Mrizka nad PLNYM chunkem: median {median:F2} ms ze tri behu "
            + $"({string.Join(" / ", timings.Select(t => $"{t:F2}"))}). "
            + $"Rozpocet tiku je {TickBudgetMs:F0} ms; nad rovnou podlahou to je 3,2 ms.");

        // Mez je volná schválně: testovací stroj není herní. Rozhoduje řád — dokud to bylo
        // rozbité, vycházelo v běžící hře 67,75 ms, tedy víc než osminásobek rozpočtu.
        Assert.True(
            median < TickBudgetMs * 4,
            $"Mřížka nad plným chunkem stojí {median:F1} ms proti rozpočtu tiku {TickBudgetMs:F0} ms.");
    }
}
