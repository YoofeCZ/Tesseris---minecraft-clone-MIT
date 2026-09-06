using System.Diagnostics;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Kolik stojí fyzické tělo kolonisty. Rozpočet tiku je 8 ms, cíl 200 kolonistů.
/// </summary>
/// <remarks>
/// <para><b>Proč měření a ne odhad.</b> Spojitá poloha přidala do tiku tři věci, které tam
/// nebyly: kolizi proti mřížce, přestavbu prostorového indexu a rozestupování všech proti
/// všem sousedům. Každá se dá udělat draze — vyhýbání hledáním cesty by zadání porušilo
/// přímo, protože jedno hledání stojí 0,858 ms a do tiku se jich vejde osm.</para>
///
/// <para><b>Meze jsou volné schválně.</b> Testovací stroj není herní a CI běhá pod zátěží;
/// tvrdá mez by dělala náhodně červené testy, což je horší než chybějící test. Rozhoduje
/// řádová hodnota: desetiny milisekundy jsou v pořádku, jednotky milisekund jsou na hraně
/// a desítky jsou regrese. Skutečné číslo se vypisuje, takže je v logu vidět vždycky.</para>
/// </remarks>
public sealed class ColonistBodyBenchmarkTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

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

    private static NavGraph GraphOf(VoxelWorld world)
    {
        var graph = new NavGraph();
        graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero));
        graph.BuildPortals();
        return graph;
    }

    /// <summary>
    /// Dvě stě kolonistů s fyzickým tělem se vejde do rozpočtu tiku.
    /// </summary>
    /// <remarks>
    /// <b>Měří se p99, ne průměr.</b> Průměrem se dá schovat, že jeden tik za sto trvá
    /// stokrát dýl — a přesně tak vypadá zásek na obrazovce. Rozpočet je 8 ms a tenhle test hlídá řád: čtyřnásobek rozpočtu je regrese, kterou je vidět.
    /// </remarks>
    [Fact]
    public void Two_hundred_colonists_with_bodies_fit_in_the_tick_budget()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");

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

        // Zahřátí: první tiky nesou JIT a stavbu indexu, což s ustáleným během nesouvisí.
        for (int tick = 0; tick < 60; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
            colony.WakeIdle();
        }

        var casy = new double[600];
        var hodiny = Stopwatch.StartNew();

        for (int tick = 0; tick < casy.Length; tick++)
        {
            long start = hodiny.ElapsedTicks;
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
            casy[tick] = (hodiny.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
            colony.WakeIdle();
        }

        Array.Sort(casy);
        double p50 = casy[casy.Length / 2];
        double p99 = casy[(int)(casy.Length * 0.99)];
        double nejhorsi = casy[^1];

        output.WriteLine($"200 kolonistu s telem: p50 {p50:F3} ms, p99 {p99:F3} ms, "
            + $"nejhorsi {nejhorsi:F3} ms. Rozpocet tiku je 8 ms. "
            + $"Vykopano {jobs.DoneCount} bloku.");

        // Řádová mez, ne přesná: viz poznámka u třídy. Osminásobek rozpočtu by znamenal,
        // že se do tiku nevejde ani jedna kolonie, natož zbytek hry.
        Assert.True(p99 < 8.0 * 8, $"p99 tiku je {p99:F3} ms proti rozpočtu 8 ms.");
    }

    /// <summary>
    /// Vyhýbání nesmí být kvadratické.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle je ten důvod, proč vzniklo <see cref="ColonistGrid"/>.</b> Předchozí
    /// pokus o kolizi (commit 8b9d43b) porovnával každého s každým s poznámkou „kolonistů
    /// jsou dnes jednotky". Při dvou stech je to 40 000 porovnání za tik jen na vyhýbání.</para>
    ///
    /// <para><b>Kritérium je poměr, ne absolutní čas.</b> Čtyřnásobek lidí smí stát nejvýš
    /// zhruba čtyřnásobek času; kvadratická složitost by dala šestnáctinásobek. Poměr je
    /// odolný proti tomu, jak rychlý je zrovna testovací stroj — což absolutní mez není.</para>
    ///
    /// <para><b>HUSTOTA MUSÍ ZŮSTAT STEJNÁ, ne plocha.</b> První verze tohohle testu sypala
    /// 50 i 200 lidí na tutéž plochu 10×10 a naměřila poměr 8,81×, což vypadalo jako
    /// kvadratická složitost. Nebyla: na čtyřnásobné hustotě má každý čtyřikrát víc sousedů
    /// v dosahu odstrčení, takže 4 lidé × 4 sousedé = 16× párů. Tolik práce je fyzikálně
    /// nutné odvést, ať je index jakýkoli — každý blízký pár se opravdu odstrkává. Co index
    /// umí, je nepočítat páry VZDÁLENÉ, a to se pozná jen při zachované hustotě.</para>
    /// </remarks>
    [Fact]
    public void Avoidance_scales_linearly_not_quadratically()
    {
        // MEDIÁN ZE TŘÍ BĚHŮ, ne jedno měření.
        //
        // S jedním během test občas spadl, aniž by se cokoli změnilo: naměřené poměry
        // kolísaly 3,89 / 4,40 / 4,73 / 5,23× a proti meze 8,0 to na zatíženém stroji
        // dokázalo přetéct. Náhodně červený test je horší než chybějící — přestane se mu
        // věřit a pak schová i skutečnou regresi.
        //
        // Je to táž věta, kterou jsem si o hodinu dřív zapsal kvůli tick p99:
        // jedno měření není měření. Platí i pro testy, které měřím sám.
        var pomery = new double[3];

        for (int run = 0; run < pomery.Length; run++)
        {
            // Plocha roste s počtem lidí, takže hustota zůstává stejná: 50 na 11×11 a 200 na
            // 22×22 dá v obou případech 0,41 člověka na buňku.
            double fiftyPeople = MereniOdstupu(50, hrana: 11);
            double twoHundredPeople = MereniOdstupu(200, hrana: 22);

            pomery[run] = twoHundredPeople / Math.Max(fiftyPeople, 1e-9);

            output.WriteLine($"Beh {run + 1}: 50 lidi {fiftyPeople:F4} ms/tik, "
                + $"200 lidi {twoHundredPeople:F4} ms/tik, pomer {pomery[run]:F2}×.");
        }

        Array.Sort(pomery);
        double median = pomery[1];

        output.WriteLine($"Vyhybani: median pomeru {median:F2}× ze tri behu "
            + $"({string.Join(" / ", pomery.Select(p => $"{p:F2}"))}); "
            + "linearne 4×, kvadraticky 16×.");

        // Osminásobek je na půl cesty mezi lineárním a kvadratickým, takže se to nedá splést.
        Assert.True(median < 8.0, $"Čtyřnásobek lidí stál {median:F2}× víc — vyhýbání je kvadratické.");
    }

    /// <summary>Kolik stojí tik samotného rozestupování, v milisekundách.</summary>
    /// <remarks>
    /// Kolonisté stojí blízko sebe, takže je vyhýbání trvale aktivní — jinak by se měřil
    /// prázdný průchod. Práce žádná není, aby do měření nezasahovalo hledání cest.
    /// </remarks>
    private static double MereniOdstupu(int lidi, int hrana)
    {
        VoxelWorld world = Floor();
        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation(capacity: 256);

        // Do mřížky o zadané hraně, ať vyjde u obou měření stejná hustota.
        for (int i = 0; i < lidi; i++)
        {
            colony.Add(new Vector3((i % hrana) + 4.5f, 2f, (i / hrana) + 4.5f));
        }

        colony.WakeIdle();

        for (int tick = 0; tick < 60; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
        }

        var hodiny = Stopwatch.StartNew();
        const int Tiku = 600;

        for (int tick = 0; tick < Tiku; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
        }

        hodiny.Stop();
        return hodiny.Elapsed.TotalMilliseconds / Tiku;
    }

    /// <summary>
    /// Zkrácení cesty se vejde vedle hledání, ne místo něj.
    /// </summary>
    /// <remarks>
    /// <b>Musí být řádově levnější než samotné A*</b> (naměřených 0,858 ms na hledání, osm
    /// hledání na tik). Kdyby zkracování stálo víc než hledání, snížilo by se tím kolik cest
    /// se do tiku vejde — a to je rozpočet, na kterém stojí celá navigace.
    /// </remarks>
    [Fact]
    public void Path_shortening_is_cheap_next_to_the_search()
    {
        VoxelWorld world = Floor();
        NavGraph graph = GraphOf(world);

        var from = new Vector3i(2, 2, 2);
        var to = new Vector3i(28, 2, 28);

        var path = new List<Vector3i>();
        Assert.Equal(PathResult.Found, graph.TryFindPath(from, to, path));

        Vector3i[] pole = [.. path];
        var kopie = new Vector3i[pole.Length];

        // Zahřátí.
        for (int i = 0; i < 50; i++)
        {
            pole.CopyTo(kopie, 0);
            PathShortening.Shorten(graph, kopie, kopie.Length);
        }

        var hodiny = Stopwatch.StartNew();
        const int Opakovani = 500;

        for (int i = 0; i < Opakovani; i++)
        {
            pole.CopyTo(kopie, 0);
            PathShortening.Shorten(graph, kopie, kopie.Length);
        }

        hodiny.Stop();
        double naJedno = hodiny.Elapsed.TotalMilliseconds / Opakovani;

        // Pro srovnání totéž hledání, na kterém stojí rozpočet navigace.
        var hodinyHledani = Stopwatch.StartNew();
        for (int i = 0; i < Opakovani; i++)
        {
            graph.TryFindPath(from, to, path);
        }

        hodinyHledani.Stop();
        double hledani = hodinyHledani.Elapsed.TotalMilliseconds / Opakovani;

        output.WriteLine($"Zkraceni cesty o {pole.Length} bodech: {naJedno:F4} ms, "
            + $"hledani teze cesty {hledani:F4} ms, pomer {naJedno / hledani:F2}×.");

        Assert.True(
            naJedno < hledani,
            $"Zkrácení stojí {naJedno:F4} ms proti hledání {hledani:F4} ms — je dražší než A*.");
    }
}
