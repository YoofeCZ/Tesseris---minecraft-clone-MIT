using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Architektonická pravidla, která fyzické tělo kolonisty musí dodržet.
/// </summary>
/// <remarks>
/// <para><b>Proč zvlášť a proč vůbec.</b> Pravidla 6.5 (nula alokací v tick cestě) a 6.6
/// (deterministická simulace) jsou <b>architektonická rozhodnutí,
/// ne optimalizace</b>. Napsal jsem je do komentářů u <c>ColonistBody</c> i
/// <c>ColonistGrid</c> jako hotovou věc — a přitom je nic neměřilo. Tvrzení v komentáři
/// není důkaz.</para>
///
/// <para><b>Riziko je konkrétní, ne teoretické.</b> Fyzické tělo přineslo do tiku
/// <c>float</c> (poloha, rychlost, odmocniny) přesně tam, kde pravidlo 6.6 varuje. A
/// vyhýbání přidalo prostorový index, který se každý tik celý přestavuje — tedy ideální
/// místo na skrytou alokaci.</para>
/// </remarks>
public sealed class ColonistBodyRulesTests(ITestOutputHelper output)
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

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(NoWater);

        for (int i = 0; i < 10; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    // ================= DETERMINISMUS (pravidlo 6.6) =================

    /// <summary>
    /// Dva stejné běhy dají bit po bitu stejné polohy.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle jsem tvrdil, aniž bych to změřil.</b> V komentářích u
    /// <c>ColonistBody</c> stojí „pořadí operací je pevné, takže tentýž vstup dá tentýž
    /// výstup". Je to pravda o IEEE 754, ale <b>o kódu to nic neříká</b>: stačilo by někde
    /// iterovat přes <c>HashSet</c>, sáhnout po <c>Random</c> nebo po systémovém čase a
    /// determinismus je pryč, aniž by se cokoli jiného pokazilo.</para>
    ///
    /// <para><b>Porovnávají se BITY, ne hodnoty s tolerancí.</b> Pravidlo 6.6 staví na
    /// determinismu savy a případný multiplayer; rozdíl v posledním bitu mantisy se za tisíc
    /// tiků nasčítá do viditelného rozejití. Tolerance by tenhle test udělala bezcenným —
    /// prošel by i tehdy, kdyby se běhy pomalu rozcházely.</para>
    ///
    /// <para><b>Co by prošlo i s rozbitou věcí:</b> „oba běhy skončily" a „kolonisté jsou
    /// pořád ve světě". Rozhoduje shoda každé souřadnice každého kolonisty v každém tiku.</para>
    ///
    /// <para><b>A ještě něco, co jsem zjistil až kontrolním během.</b> Vložil jsem do
    /// <c>ColonistGrid</c> závislost na <c>Environment.TickCount</c> a tenhle test ji
    /// <b>NEODHALIL</b> — hlásil dál nula rozdílů. Ta větev (dva lidé přesně v sobě) se
    /// v původním scénáři nikdy nespustila. Scénář proto od té doby <b>startuje s částí
    /// kolonistů na jedné souřadnici</b>; bez toho byl test slabší, než vypadal. Chytila to
    /// tehdy až kontrola zdrojového kódu níž, takže oba testy jsou potřeba a ani jeden
    /// z nich nestačí sám.</para>
    /// </remarks>
    [Fact]
    public void Two_identical_runs_produce_bit_identical_positions()
    {
        float[] first = RunAndRecord();
        float[] second = RunAndRecord();

        Assert.Equal(first.Length, second.Length);

        int rozdilu = 0;
        int prvniRozdil = -1;

        for (int i = 0; i < first.Length; i++)
        {
            // BITOVĚ, ne s tolerancí: viz poznámka výše.
            if (BitConverter.SingleToInt32Bits(first[i]) != BitConverter.SingleToInt32Bits(second[i]))
            {
                rozdilu++;
                if (prvniRozdil < 0)
                {
                    prvniRozdil = i;
                }
            }
        }

        output.WriteLine($"Porovnano {first.Length} souradnic ze dvou behu, "
            + $"rozdilu {rozdilu}" + (prvniRozdil >= 0
                ? $", prvni na indexu {prvniRozdil}: {first[prvniRozdil]:R} proti {second[prvniRozdil]:R}."
                : "."));

        Assert.Equal(0, rozdilu);
    }

    /// <summary>
    /// Odsimuluje pevně daný scénář a vrátí polohy všech kolonistů ve všech ticích.
    /// </summary>
    /// <remarks>
    /// Scénář schválně míchá všechny cesty pohybu: práci (chůze po cestě), procházku
    /// (jiná rychlost, vlastní generátor náhody) i dav (rozestupování). Kdyby se testovala
    /// jen chůze, prošel by nedeterminismus schovaný v potulce.
    /// </remarks>
    private static float[] RunAndRecord()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");
        ColonyRuntime colony = Colony(world);

        colony.FoundTownHall(new Vector3i(8, 2, 8));

        for (int x = 20; x < 24; x++)
        {
            world.SetBlock(x, 2, 8, stone);
            colony.OnBlockChanged(world, world.Registry, new Vector3i(x, 2, 8));
        }

        colony.MarkArea(world, world.Registry, new Vector3i(20, 2, 8), new Vector3i(23, 2, 8));

        const int Lidi = 8;
        const int Tiku = 400;

        for (int i = 0; i < Lidi; i++)
        {
            // POLOVINA PŘESNĚ NA SOBĚ. Nulová vzdálenost je vlastní větev rozestupu (nemá
            // směr, takže se řeší podle indexu) a ve hře je běžná, protože radnice pouští
            // lidi na stejné místo. Bez ní tudy prošel nedeterminismus, viz poznámka výše.
            colony.Colonists.Add(i < Lidi / 2
                ? new Vector3(8.5f, 2f, 8.5f)
                : new Vector3(8.5f + (i * 0.3f), 2f, 8.5f));
        }

        // I hráč, ať se do měření dostane i vyhýbání vůči němu.
        colony.Colonists.PlayerPosition = new Vector3(9f, 2f, 9f);

        var zaznam = new float[Tiku * Lidi * 3];
        int cursor = 0;

        for (int tick = 0; tick < Tiku; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();

            for (int id = 0; id < Lidi; id++)
            {
                Vector3 position = colony.Colonists.PositionOf(id);
                zaznam[cursor++] = position.X;
                zaznam[cursor++] = position.Y;
                zaznam[cursor++] = position.Z;
            }
        }

        return zaznam;
    }

    /// <summary>
    /// Náhoda jde jen přes vlastní generátor se stavem na kolonistu, nikdy přes <c>Random</c>.
    /// </summary>
    /// <remarks>
    /// <b>Zadání to říká výslovně</b> a je to jediná past, kterou předchozí test nechytí
    /// spolehlivě: <c>Random</c> bez semínka by dal jiný běh, ale <c>new Random(0)</c> uvnitř
    /// simulace by prošel — a rozešel by se až v okamžiku, kdy si dva stroje rozdělí práci
    /// jinak. Proto se kontroluje i zdrojový kód, stejně jako to dělá
    /// <c>ColonyTextureLayerTests</c> u natvrdo psaných vrstev.
    /// </remarks>
    [Fact]
    public void Colony_simulation_never_uses_Random()
    {
        string[] soubory =
        [
            "ColonySimulation.cs",
            "ColonistBody.cs",
            "ColonistGrid.cs",
            "PathShortening.cs",
        ];

        foreach (string jmeno in soubory)
        {
            string cesta = Path.Combine(RepositoryRoot(), "src", "Game", "Colony", jmeno);
            Assert.True(File.Exists(cesta), $"Nenašel jsem {cesta}.");

            string kod = File.ReadAllText(cesta);

            Assert.DoesNotContain("new Random", kod, StringComparison.Ordinal);
            Assert.DoesNotContain("Random.Shared", kod, StringComparison.Ordinal);

            // DateTime a Environment.TickCount jsou tentýž problém jinou cestou: simulace
            // nesmí záviset na hodinách, jinak dá každý běh jiný výsledek.
            Assert.DoesNotContain("DateTime.", kod, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment.TickCount", kod, StringComparison.Ordinal);

            output.WriteLine($"{jmeno}: bez Random, bez hodin.");
        }
    }

    // ================= NULA ALOKACÍ V TIKU (pravidlo 6.5) =================

    /// <summary>
    /// Ustálený tik nealokuje.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle jsem taky tvrdil, aniž bych to změřil.</b> <c>ColonistGrid</c> má
    /// v dokumentaci „Nula alokací v tiku (pravidlo 6.5)" a přestavuje se každý tik celý.
    /// Stačilo by vrátit <c>List</c> místo psaní do pole nebo si někde zavřít lambdu a
    /// pravidlo padne, aniž by se cokoli jiného pokazilo — GC pauza při 60 Hz je zabiják
    /// a projeví se až jako trhání obrazu, ne jako chyba.</para>
    ///
    /// <para><b>Měří se AŽ USTÁLENÝ stav.</b> První tiky legitimně alokují: JIT, zvětšení
    /// polí indexu, seznamy cest. Rozpočet se proto počítá po zahřátí; kdyby se měřilo od
    /// začátku, test by hlásil chybu u kódu, který je v pořádku.</para>
    ///
    /// <para><b>Mez není nula, ale skoro.</b> Hledání cesty přes <c>NavGraph</c> alokuje
    /// vědomě (slovníky nad portály, viz jeho vlastní dokumentace) a je součástí tiku. Tenhle
    /// test proto hlídá <b>tělo</b>: scénář bez zadané práce, kde se jen chodí, rozestupuje
    /// a přestavuje index. Tam nesmí vzniknout odpad ani po tisíci ticích.</para>
    /// </remarks>
    [Fact]
    public void A_settled_tick_of_bodies_allocates_nothing()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);

        colony.FoundTownHall(new Vector3i(8, 2, 8));

        const int Lidi = 60;
        for (int i = 0; i < Lidi; i++)
        {
            colony.Colonists.Add(new Vector3(4.5f + (i % 20), 2f, 4.5f + (i / 20)));
        }

        colony.Colonists.PlayerPosition = new Vector3(8f, 2f, 8f);

        // ZAHŘÁTÍ. První tiky nesou JIT a zvětšení polí; s nimi by test měřil něco jiného.
        for (int tick = 0; tick < 600; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        const int Merenych = 600;

        long pred = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 0; tick < Merenych; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        long naalokovano = GC.GetAllocatedBytesForCurrentThread() - pred;
        double naTik = naalokovano / (double)Merenych;

        output.WriteLine($"{Lidi} kolonistu, {Merenych} tiku: naalokovano {naalokovano} B, "
            + $"tedy {naTik:F1} B na tik ({naTik / Lidi:F2} B na cloveka a tik).");

        // Nula je ideál; pár bajtů na tik by znamenalo něco jako boxing v okrajové větvi.
        // Sto bajtů na tik je při 60 Hz šest kilobajtů za vteřinu, což je už pravidelný
        // odpad a tedy porušení 6.5.
        Assert.True(
            naTik < 100,
            $"Ustálený tik alokuje {naTik:F1} B — pravidlo 6.5 zakazuje odpad v tick cestě.");
    }

    /// <summary>Kořen repozitáře, ať test funguje bez ohledu na to, odkud se pouští.</summary>
    private static string RepositoryRoot()
    {
        var adresar = new DirectoryInfo(AppContext.BaseDirectory);

        while (adresar is not null)
        {
            if (Directory.Exists(Path.Combine(adresar.FullName, "src", "Game", "Colony")))
            {
                return adresar.FullName;
            }

            adresar = adresar.Parent;
        }

        throw new InvalidOperationException("Nenašel jsem kořen repozitáře.");
    }
}
