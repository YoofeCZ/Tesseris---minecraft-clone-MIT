using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Kolonista má fyzické tělo: spojitou polohu, kolizi, vyhýbání a skok.
/// </summary>
/// <remarks>
/// <para><b>Co bylo špatně.</b> Kolonista byl index do mřížky. Pohyb byl skok o celou buňku
/// jednou za třináct tiků a plynulost dělala až interpolace při vykreslování — proto to
/// vypadalo jako figurka na šachovnici, i když se mezi kroky nezastavoval. Dva kolonisté
/// stáli v sobě, hráč jimi prošel a přes překážku se dalo dostat jen po schodu.</para>
///
/// <para><b>Proč tyhle testy a ne jiné.</b> Ke každému je připsané, co by prošlo i s rozbitou
/// věcí — to je v tomhle repu nejčastější chyba při ověřování. „Hnul se"
/// projde vždycky, protože kolonista se hýbal i po mřížce; rozhoduje až to, jestli se hnul
/// o MÉNĚ než celou buňku.</para>
/// </remarks>
public sealed class ColonistBodyTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Podlaha na y = 0 a 1 přes celý chunk, tedy stání na y = 2.</summary>
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

    private static NavGraph GraphOf(VoxelWorld world)
    {
        var graph = new NavGraph();
        graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero));
        graph.BuildPortals();
        return graph;
    }

    /// <summary>Délka lomené čáry v blocích. Tím se pozná, jestli se cesta narovnala.</summary>
    private static float Delka(ReadOnlySpan<Vector3i> cesta)
    {
        float soucet = 0f;
        for (int i = 1; i < cesta.Length; i++)
        {
            soucet += (ColonistBody.CentreOf(cesta[i]) - ColonistBody.CentreOf(cesta[i - 1])).Length;
        }

        return soucet;
    }

    // ================= SPOJITÁ POLOHA =================

    /// <summary>
    /// Kolonista jde k cíli spojitě, ne skokem o buňku.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ SPOJITÉ POLOHY PADÁ.</b> Dokud byla poloha <c>Vector3i</c>, byl každý posun
    /// přesně o celou buňku a nikdy o zlomek. Poloh mimo střed buňky proto bylo NULA.</para>
    ///
    /// <para><b>Co by prošlo i s rozbitou věcí:</b> „došel k cíli" (došel i po mřížce),
    /// „ušel dohromady N bloků" (taky) a „hnul se v každém tiku" (ne, ale to je slabší
    /// kritérium). Rozhoduje jediné: byl NĚKDY jinde než v přesném středu buňky?</para>
    /// </remarks>
    [Fact]
    public void A_colonist_walks_between_cells_not_in_whole_cell_jumps()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");
        ColonyRuntime colony = Colony(world);

        world.SetBlock(20, 2, 8, stone);
        colony.OnBlockChanged(world, world.Registry, new Vector3i(20, 2, 8));

        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 8));
        Assert.True(colonist >= 0);

        colony.MarkArea(world, world.Registry, new Vector3i(20, 2, 8), new Vector3i(20, 2, 8));

        int mimoStred = 0;
        float nejvetsiPosun = 0f;
        Vector3 predchozi = colony.Colonists.PositionOf(colonist);

        for (int tick = 0; tick < 600; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();

            Vector3 ted = colony.Colonists.PositionOf(colonist);
            nejvetsiPosun = MathF.Max(nejvetsiPosun, (ted - predchozi).Length);
            predchozi = ted;

            // Střed buňky je x,5. Cokoli jiného po mřížce nastat nemohlo.
            float zbytekX = MathF.Abs(ted.X - MathF.Floor(ted.X) - 0.5f);
            float zbytekZ = MathF.Abs(ted.Z - MathF.Floor(ted.Z) - 0.5f);

            if (zbytekX > 0.05f || zbytekZ > 0.05f)
            {
                mimoStred++;
            }
        }

        output.WriteLine($"Tiku mimo stred bunky: {mimoStred} z 600, "
            + $"nejvetsi posun za tik {nejvetsiPosun:F4} bloku "
            + $"(rychlost chuze dava {ColonistBody.WalkSpeed * ColonistBody.StepSeconds:F4}).");

        Assert.True(mimoStred > 300, $"Mimo stred bunky byl jen {mimoStred}× — pohyb je pořád po mřížce.");

        // A hlavně: nikdy se neskočí o celou buňku. To je ta druhá půlka téže věty.
        Assert.True(
            nejvetsiPosun < 0.5f,
            $"Za jeden tik se posunul o {nejvetsiPosun:F3} bloku, což je skok, ne krok.");
    }

    /// <summary>
    /// Ani v davu se za tik neujde víc, než dovolí rychlost chůze.
    /// </summary>
    /// <remarks>
    /// <para><b>Naměřeno V BĚŽÍCÍ HŘE, ne testem.</b> Sonda hlásila „nejdelsi za tik
    /// 0,153 bloku" proti 0,077, kolik dovolí chůze — přesně dvojnásobek. Příčina: pohyb
    /// k cíli a rozestupování se počítaly každý zvlášť a v jednom tiku se sečetly, takže
    /// kolonista uprostřed davu chodil dvakrát rychleji než hráč. Zadání přitom říká rychlost
    /// držet na <c>PlayerController.WalkSpeed</c>.</para>
    ///
    /// <para><b>Co by prošlo i s rozbitou věcí:</b> průměrná rychlost (dav je vzácný, průměr
    /// ho utopí) i „nikdo se neteleportoval" (0,153 je pořád málo). Rozhoduje MAXIMUM za
    /// jediný tik, měřené v situaci, kdy se lidé odstrkávají A ZÁROVEŇ NĚKAM JDOU. První
    /// verze testu je jen namačkala na sebe a naměřila 0,038, tedy pod mezí — protože stojící
    /// dav se pouze rozestupuje a chůze se k tomu nemá co přičíst. Sečíst se to může jedině
    /// u toho, kdo jde za prací skrz ostatní.</para>
    /// </remarks>
    [Fact]
    public void Even_in_a_crowd_nobody_moves_faster_than_walking()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");
        ColonyRuntime colony = Colony(world);

        // Práce daleko na druhé straně, ať se jde dlouho a skrz ostatní.
        for (int x = 24; x < 28; x++)
        {
            world.SetBlock(x, 2, 8, stone);
            colony.OnBlockChanged(world, world.Registry, new Vector3i(x, 2, 8));
        }

        colony.MarkArea(world, world.Registry, new Vector3i(24, 2, 8), new Vector3i(27, 2, 8));

        // Namačkaní na sebe, ať se odstrkávání dostane ke slovu naplno — a všichni míří
        // za toutéž prací, takže se cestou tlačí jeden přes druhého.
        var lide = new int[12];
        for (int i = 0; i < lide.Length; i++)
        {
            lide[i] = colony.Colonists.Add(new Vector3(8.5f + (i * 0.05f), 2f, 8.5f));
        }

        var predchozi = new Vector3[lide.Length];
        for (int i = 0; i < lide.Length; i++)
        {
            predchozi[i] = colony.Colonists.PositionOf(lide[i]);
        }

        float nejdelsiPosun = 0f;

        for (int tick = 0; tick < 600; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();

            for (int i = 0; i < lide.Length; i++)
            {
                Vector3 ted = colony.Colonists.PositionOf(lide[i]);
                float dx = ted.X - predchozi[i].X;
                float dz = ted.Z - predchozi[i].Z;

                nejdelsiPosun = MathF.Max(nejdelsiPosun, MathF.Sqrt((dx * dx) + (dz * dz)));
                predchozi[i] = ted;
            }
        }

        float dovoleno = ColonistBody.WalkSpeed * ColonistBody.StepSeconds;

        output.WriteLine($"Nejdelsi posun za tik v davu {nejdelsiPosun:F4} bloku, "
            + $"chuze dovoli {dovoleno:F4} (hrac {Tesseris.Game.Player.PlayerController.WalkSpeed:F2} m/s).");

        Assert.True(
            nejdelsiPosun <= dovoleno + 1e-4f,
            $"V davu se za tik ušlo {nejdelsiPosun:F4} bloku proti dovoleným {dovoleno:F4} — "
            + "vyhýbání se sčítá s chůzí a kolonista je rychlejší než hráč.");
    }

    /// <summary>
    /// Ani procházka se nedá odstrčením zrychlit.
    /// </summary>
    /// <remarks>
    /// <para><b>Naměřeno V BĚŽÍCÍ HŘE, po dvou opravách téže věci.</b> Rychlost se dala
    /// překročit pokaždé jinou cestou: nejdřív sčítáním odstrčení s krokem (0,153 proti
    /// 0,077), pak pořadím rozestupování proti kroku, a nakonec u procházky (0,100 proti
    /// 0,050). Proto je limit dneska jeden a platí na SOUČET všech cest pohybu za tik.</para>
    ///
    /// <para><b>Procházka má vlastní, nižší mez.</b> Kdo nikam nespěchá, se courá; kdyby se
    /// hlídal rychlostí chůze, dal by se odstrčením rozběhnout na dvojnásobek.</para>
    /// </remarks>
    [Fact]
    public void A_crowd_of_strollers_never_exceeds_the_stroll_speed()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);

        // S radnicí, ať se opravdu prochází — a namačkaní, ať se odstrkávají.
        colony.FoundTownHall(new Vector3i(8, 2, 8));

        var lide = new int[10];
        for (int i = 0; i < lide.Length; i++)
        {
            lide[i] = colony.Colonists.Add(new Vector3(8.5f + (i * 0.05f), 2f, 8.5f));
        }

        var predchozi = new Vector3[lide.Length];
        for (int i = 0; i < lide.Length; i++)
        {
            predchozi[i] = colony.Colonists.PositionOf(lide[i]);
        }

        float nejdelsiPosun = 0f;

        for (int tick = 0; tick < 1_200; tick++)
        {
            colony.Tick(world, world.Registry);

            for (int i = 0; i < lide.Length; i++)
            {
                // Měří se JEN procházka; kdo zrovna stojí, má vyšší strop a do měření nepatří.
                bool wandering = colony.Colonists.StateOf(lide[i]) == ColonistState.Wandering;

                Vector3 ted = colony.Colonists.PositionOf(lide[i]);
                float dx = ted.X - predchozi[i].X;
                float dz = ted.Z - predchozi[i].Z;
                predchozi[i] = ted;

                if (wandering)
                {
                    nejdelsiPosun = MathF.Max(nejdelsiPosun, MathF.Sqrt((dx * dx) + (dz * dz)));
                }
            }
        }

        float dovoleno = ColonistBody.WanderSpeed * ColonistBody.StepSeconds;

        output.WriteLine($"Nejdelsi posun pri prochazce {nejdelsiPosun:F4} bloku, "
            + $"prochazka dovoli {dovoleno:F4} (chuze {ColonistBody.WalkSpeed * ColonistBody.StepSeconds:F4}).");

        Assert.True(
            nejdelsiPosun <= dovoleno + 1e-4f,
            $"Při procházce se za tik ušlo {nejdelsiPosun:F4} proti dovoleným {dovoleno:F4}.");
    }

    /// <summary>
    /// Rychlost chůze je rychlost hráče, měřeno na skutečně ušlé dráze.
    /// </summary>
    /// <remarks>
    /// <b>Ne na konstantě.</b> Starý test dělil 60 / <c>TicksPerStep</c>, což měřilo jen to,
    /// jestli je konstanta ta, co v testu; se spojitou polohou se dá měřit skutečná dráha
    /// za skutečný čas, takže se tady ověřuje chování, ne zápis.
    /// </remarks>
    [Fact]
    public void Walking_speed_matches_the_player()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");
        ColonyRuntime colony = Colony(world);

        world.SetBlock(28, 2, 8, stone);
        colony.OnBlockChanged(world, world.Registry, new Vector3i(28, 2, 8));

        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 8));
        colony.MarkArea(world, world.Registry, new Vector3i(28, 2, 8), new Vector3i(28, 2, 8));

        // Rozejde se až po nalezení cesty, takže se měří AŽ rozchozená chůze — jinak by se
        // do průměru počítalo čekání na cestu a rychlost by vyšla nižší, než jaká je.
        Vector3 zacatek = Vector3.Zero;
        int mereno = 0;
        float ujito = 0f;
        Vector3 predchozi = colony.Colonists.PositionOf(colonist);

        for (int tick = 0; tick < 400; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();

            Vector3 ted = colony.Colonists.PositionOf(colonist);
            float posun = (ted - predchozi).Length;
            predchozi = ted;

            if (posun <= 1e-4f)
            {
                continue;
            }

            if (mereno == 0)
            {
                zacatek = ted;
            }

            ujito += posun;
            mereno++;
        }

        Assert.True(mereno > 60, $"Chodil jen {mereno} tiků, na měření rychlosti to nestačí.");

        float rychlost = ujito / (mereno * ColonistBody.StepSeconds);
        output.WriteLine($"Kolonista {rychlost:F2} b/s za {mereno} tiku, "
            + $"hrac {Tesseris.Game.Player.PlayerController.WalkSpeed:F2} m/s. Zacal na {zacatek}.");

        Assert.Equal(Tesseris.Game.Player.PlayerController.WalkSpeed, rychlost, 0.3f);
    }

    // ================= ZKRÁCENÍ CESTY =================

    /// <summary>
    /// Přes prázdnou pláň se jde šikmo, ne po pravých úhlech.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ ZKRÁCENÍ CESTY PADÁ.</b> A* povoluje jen čtyři vodorovné směry, takže
    /// diagonální cesta vyjde jako schodiště z pravých úhlů. Kdo po něm jde doslova, ujde
    /// zhruba manhattanskou vzdálenost; napřímo je to vzdálenost vzdušnou čarou.</para>
    ///
    /// <para><b>Co by prošlo i bez opravy:</b> „došel" (dojde i po schodišti) a „ušel míň než
    /// 100 bloků" (schodiště je 28). Rozhoduje POMĚR ke vzdušné čáře: schodiště dá 1,41×,
    /// napřímo 1,0×.</para>
    /// </remarks>
    [Fact]
    public void On_open_ground_the_path_is_pulled_straight()
    {
        VoxelWorld world = Floor();
        NavGraph graph = GraphOf(world);

        var from = new Vector3i(2, 2, 2);
        var to = new Vector3i(16, 2, 16);

        var path = new List<Vector3i>();
        Assert.Equal(PathResult.Found, graph.TryFindPath(from, to, path));

        int puvodni = path.Count;
        Vector3i[] pole = [.. path];
        int zkracena = PathShortening.Shorten(graph, pole, pole.Length);

        float delkaZkracena = Delka(pole.AsSpan(0, zkracena));
        float delkaPuvodni = Delka(pole.AsSpan(0, puvodni));
        float vzdusnaCara = (ColonistBody.CentreOf(to) - ColonistBody.CentreOf(from)).Length;

        output.WriteLine($"Bodu {puvodni} -> {zkracena}, delka {delkaPuvodni:F1} -> "
            + $"{delkaZkracena:F1} bloku, vzdusna cara {vzdusnaCara:F1}.");

        // Po pravých úhlech je to 28 bloků, vzdušnou čarou 19,8. Zkrácená cesta musí být
        // skoro vzdušná čára; kdyby to byl pořád lomený tvar, vyjde poměr kolem 1,41.
        Assert.True(
            delkaZkracena < vzdusnaCara * 1.1f,
            $"Zkrácená cesta je {delkaZkracena:F1} proti vzdušné čáře {vzdusnaCara:F1} — pořád lomená.");

        Assert.True(zkracena < puvodni, "Cesta se vůbec nezkrátila.");
    }

    /// <summary>
    /// Zkrácení nesmí vyrobit cestu skrz zeď.
    /// </summary>
    /// <remarks>
    /// <b>Tohle je ten důvod, proč se viditelnost testuje tímtéž predikátem, jakým se chodí.</b>
    /// Kdyby se zkracovalo naslepo, prošla by zkratka zdí a kolonista by uvázl v místě, které
    /// mu cesta slíbila. Kontrolní běh: bez zdi se táž cesta zkrátit MUSÍ, jinak by test
    /// prošel i s vypnutým zkracováním.
    /// </remarks>
    [Fact]
    public void Shortening_never_cuts_through_a_wall()
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");

        // Zeď napříč, s jedinou dírou u kraje. Cesta kolem ní musí zůstat lomená.
        for (int z = 0; z < 20; z++)
        {
            world.SetBlock(10, 2, z, stone);
            world.SetBlock(10, 3, z, stone);
        }

        NavGraph graph = GraphOf(world);

        var from = new Vector3i(6, 2, 4);
        var to = new Vector3i(14, 2, 4);

        var path = new List<Vector3i>();
        Assert.Equal(PathResult.Found, graph.TryFindPath(from, to, path));

        Vector3i[] pole = [.. path];
        int zkracena = PathShortening.Shorten(graph, pole, pole.Length);

        output.WriteLine($"Kolem zdi: bodu {path.Count} -> {zkracena}.");

        // Každý úsek zkrácené cesty musí být průchodný. Kdyby cesta prošla zdí, tady to praskne.
        for (int i = 1; i < zkracena; i++)
        {
            Assert.True(
                PathShortening.HasDirectPath(graph, pole[i - 1], pole[i]),
                $"Úsek {pole[i - 1]} -> {pole[i]} vede skrz zeď.");
        }

        // KONTROLNÍ BĚH: přímo skrz zeď to nesmí jít, jinak by předchozí smyčka nic neznamenala.
        Assert.False(
            PathShortening.HasDirectPath(graph, from, to),
            "Zkratka skrz zeď se považuje za průchodnou — kontrola viditelnosti nefunguje.");
    }

    // ================= KOLIZE A VYHÝBÁNÍ =================

    /// <summary>
    /// Kolonisté se prolnou jeden druhým? Nesmí.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ VYHÝBÁNÍ PADÁ.</b> Kolize žádná nebyla, takže se lidé při práci slévali do
    /// jednoho místa a vypadalo to jako chyba vykreslování.</para>
    ///
    /// <para><b>Vyhýbání, ne zákaz vstupu.</b> Zákaz vstupu do obsazené buňky (commit 8b9d43b)
    /// rozbil uzavřenou herní smyčku, protože u stroje je úzké místo vždycky. Proto se tady
    /// neměří „nikdy nebyli v jedné buňce", ale „nestáli v sobě": překryv se posuzuje na
    /// skutečné vzdálenosti těl.</para>
    ///
    /// <para><b>SCHVÁLNĚ BEZ RADNICE.</b> První verze tohohle testu radnici stavěla a prošla
    /// i s vypnutým vyhýbáním — kolonisté se totiž rozešli na procházku každý jinam, takže
    /// se rozestoupili sami od sebe a měřilo se něco úplně jiného. Bez radnice se nikdo
    /// netoulá a stojí přesně tam, kde ho test postavil; jediné, co je pak může rozestrčit,
    /// je vyhýbání. Odhalilo to zároveň skutečnou díru: vyhýbání se počítalo jen uvnitř
    /// chůze, takže stojící dav v sobě zůstal stát.</para>
    /// </remarks>
    [Fact]
    public void Colonists_do_not_stand_inside_each_other()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);

        // Všichni na jedno místo. Bez vyhýbání tam zůstanou na sobě.
        var lide = new int[4];
        for (int i = 0; i < lide.Length; i++)
        {
            lide[i] = colony.Colonists.Add(new Vector3(8.5f, 2f, 8.5f));
        }

        float nejmensiOdstup = float.MaxValue;
        int prekryvu = 0;

        for (int tick = 0; tick < 600; tick++)
        {
            colony.Tick(world, world.Registry);

            // Prvních pár tiků se ještě rozestupují z jednoho bodu; měří se ustálený stav.
            if (tick < 120)
            {
                continue;
            }

            for (int a = 0; a < lide.Length; a++)
            {
                for (int b = a + 1; b < lide.Length; b++)
                {
                    Vector3 prvni = colony.Colonists.PositionOf(lide[a]);
                    Vector3 druhy = colony.Colonists.PositionOf(lide[b]);

                    float dx = prvni.X - druhy.X;
                    float dz = prvni.Z - druhy.Z;
                    float odstup = MathF.Sqrt((dx * dx) + (dz * dz));

                    nejmensiOdstup = MathF.Min(nejmensiOdstup, odstup);

                    // Půlka šířky těla: blíž než tohle znamená, že jsou opravdu v sobě.
                    if (odstup < ColonistBody.HalfWidth)
                    {
                        prekryvu++;
                    }
                }
            }
        }

        output.WriteLine($"Nejmensi odstup {nejmensiOdstup:F3} bloku "
            + $"(sirka tela {ColonistBody.Width:F2}), prekryvu {prekryvu}.");

        Assert.Equal(0, prekryvu);
    }

    /// <summary>
    /// Kolonista se vyhne i hráči.
    /// </summary>
    /// <remarks>
    /// <para><b>Vyhýbá se kolonista, ne hráč.</b> Zastavit hráče o kolonisty by znamenalo
    /// sáhnout na <c>PlayerController</c>, a hlavně by dav, který hráče zazdí v koutě, byl
    /// horší než dav, který se rozestoupí.</para>
    ///
    /// <para><b>SCHVÁLNĚ BEZ RADNICE a s KONTROLNÍM BĚHEM bez hráče.</b> První verze radnici
    /// stavěla a prošla i s vyhýbáním vypnutým: kolonista se rozešel na procházku a vzdálil
    /// se 5,95 bloku, takže se měřila potulka, ne uhnutí. Bez radnice se nikdo netoulá,
    /// a kontrolní běh bez nastavené polohy hráče ukáže, že se pak stojí na místě — jinak by
    /// tvrzení „vzdálil se" nedokazovalo nic.</para>
    /// </remarks>
    [Fact]
    public void Colonists_step_aside_from_the_player()
    {
        var start = new Vector3(8.5f, 2f, 8.5f);

        float sHracem = OdstupOdStartu(start, hrac: start);
        float bezHrace = OdstupOdStartu(start, hrac: null);

        output.WriteLine($"S hracem na tele se vzdalil o {sHracem:F3} bloku, "
            + $"bez hrace o {bezHrace:F3}.");

        Assert.True(sHracem > ColonistBody.HalfWidth, $"Zůstal hráči v těle, odstup jen {sHracem:F3}.");

        // KONTROLNÍ BĚH: bez hráče se nemá kam uhýbat, takže se nesmí hnout. Kdyby se hnul,
        // měřila by se potulka nebo pád, ne vyhýbání.
        Assert.True(bezHrace < 0.01f, $"Bez hráče se pohnul o {bezHrace:F3} — test měří něco jiného.");
    }

    /// <summary>Jak daleko se kolonista dostane od svého startu za dvě vteřiny.</summary>
    private static float OdstupOdStartu(Vector3 start, Vector3? hrac)
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);

        int colonist = colony.Colonists.Add(start);

        for (int tick = 0; tick < 120; tick++)
        {
            colony.Colonists.PlayerPosition = hrac;
            colony.Tick(world, world.Registry);
        }

        Vector3 konec = colony.Colonists.PositionOf(colonist);
        float dx = konec.X - start.X;
        float dz = konec.Z - start.Z;

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    // ================= SKOK =================

    /// <summary>
    /// Přes jednoblokovou překážku se skáče.
    /// </summary>
    /// <remarks>
    /// <para><b>BEZ SKOKU PADÁ</b> — a hlavně je uvnitř KONTROLNÍ BĚH s dvoublokovou zdí.
    /// Bez něj by test prošel i tehdy, kdyby kolonista překážku prostě prošel skrz: dojde
    /// na druhou stranu tak jako tak. Přes dva bloky se doskočit NESMÍ (skok dává 1,41 bloku),
    /// takže dvojice testů odliší „přeskočil" od „prošel zdí".</para>
    /// </remarks>
    [Fact]
    public void A_one_block_obstacle_is_jumped_over()
    {
        Assert.True(PreskociZed(vyska: 1, output), "Nepřeskočil jednoblokovou překážku.");

        // KONTROLNÍ BĚH: dvoublokovou zeď přeskočit nejde. Kdyby ano, znamenalo by to, že
        // kolonista zdí prochází a první tvrzení nic nedokazuje.
        Assert.False(PreskociZed(vyska: 2, output), "Přeskočil dvoublokovou zeď — prochází zdí.");
    }

    private static bool PreskociZed(int vyska, ITestOutputHelper output)
    {
        VoxelWorld world = Floor();
        ushort stone = world.Registry.IndexOf("test:stone");

        // Zeď napříč celým chunkem, aby ji nešlo obejít.
        for (int z = 0; z < NavGrid.Size; z++)
        {
            for (int y = 0; y < vyska; y++)
            {
                world.SetBlock(10, 2 + y, z, stone);
            }
        }

        // Míří se za zeď. Tělo se testuje přímo, bez navigace: tohle měří SKOK, ne cestu.
        var cil = new Vector3(12.5f, 2f, 8.5f);
        NavGraph graph = GraphOf(world);

        Vector3 poloha = new(8.5f, 2f, 8.5f);
        float velocityY = 0f;
        bool onGround = true;
        float nejdal = poloha.X;

        for (int tick = 0; tick < 600; tick++)
        {
            float toX = cil.X - poloha.X;
            float toZ = cil.Z - poloha.Z;
            float delka = MathF.Sqrt((toX * toX) + (toZ * toZ));

            float wishX = delka > 1e-4f ? toX / delka : 0f;
            float wishZ = delka > 1e-4f ? toZ / delka : 0f;
            float krok = MathF.Min(ColonistBody.WalkSpeed * ColonistBody.StepSeconds, delka);

            bool blocked = ColonistBody.TryMoveHorizontal(graph, ref poloha, wishX * krok, wishZ * krok);

            if (blocked && onGround && ColonistBody.ShouldJump(graph, poloha, wishX, wishZ))
            {
                velocityY = ColonistBody.JumpVelocity;
                onGround = false;
            }

            ColonistBody.ApplyGravity(graph, ref poloha, ref velocityY, ref onGround);
            nejdal = MathF.Max(nejdal, poloha.X);
        }

        output.WriteLine($"Zed vysoka {vyska}: dosel na x = {nejdal:F2} (zed je na x = 10..11).");
        return nejdal > 11.5f;
    }

    // ================= PÁD =================

    /// <summary>
    /// Pád je spojitý, ne teleport na dno.
    /// </summary>
    /// <remarks>
    /// <b>BEZ FYZIKY PADÁ:</b> dřív se hledala první pochůzná buňka dolů a kolonista se na ni
    /// přesunul JEDNÍM tikem, takže díra hluboká deset bloků se zdolala okamžitě. Volný pád
    /// z deseti bloků trvá 0,82 s, tedy 49 tiků; teleport by byl jeden.
    /// </remarks>
    [Fact]
    public void Falling_takes_time_instead_of_teleporting_to_the_bottom()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Dno na y = 1, sloup do y = 12. Kolonista bude stát na jeho vršku.
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        for (int y = 2; y <= 12; y++)
        {
            world.SetBlock(4, y, 4, stone);
        }

        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation();
        int id = colony.Add(new Vector3i(4, 13, 4));

        Assert.Equal(13, colony.CellOf(id).Y);

        // Celý sloup pryč.
        for (int y = 2; y <= 12; y++)
        {
            world.SetBlock(4, y, 4, BlockRegistry.Air);
            graph.OnBlockChanged(world, world.Registry, NoWater, 4, y, 4);
        }

        colony.WakeIdle();

        int tiku = 0;
        while (colony.CellOf(id).Y > 2 && tiku < 600)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
            tiku++;
        }

        output.WriteLine($"Pad z y = 13 na y = {colony.CellOf(id).Y} trval {tiku} tiku "
            + $"({tiku / 60f:F2} s).");

        Assert.Equal(2, colony.CellOf(id).Y);

        // Volný pád z jedenácti bloků při g = 30 trvá 0,86 s, tedy 51 tiků. Teleport by byl
        // jeden jediný — a přesně tak to bylo.
        Assert.True(tiku > 20, $"Spadl za {tiku} tiků, to je teleport, ne pád.");
        Assert.True(tiku < 200, $"Pád trval {tiku} tiků, to je moc pomalé.");
    }

    /// <summary>
    /// Kolonista, kterému chybí navigační mřížka, nesmí propadnout světem.
    /// </summary>
    /// <remarks>
    /// <para><b>NAJITO V BĚŽÍCÍ HŘE, ne testem.</b> Po načtení savu odletěl hráč pryč; navigace
    /// se udržuje jen kolem něj (<c>ColonyRuntime.NavigationRadiusChunks</c> jsou dva chunky),
    /// takže chunk s kolonisty mřížku neměl. Sonda hlásila, že jsou <b>900 tiků z 900 ve
    /// vzduchu</b> — padali světem jen proto, že se na ně nikdo nedíval.</para>
    ///
    /// <para><b>„Nedá se tu stát" a „nevím" jsou dvě různé odpovědi.</b> <c>IsStandable</c>
    /// vrací false na obojí, což hledání cesty stačí, ale fyzice ne. Táž past je pojmenovaná
    /// i v <c>Walkability.TryFindGround</c>: „nenačtený chunk není totéž co prázdno".</para>
    ///
    /// <para><b>Co by prošlo i s rozbitou věcí:</b> „nespadl pod nulu" (spadl přesně na nulu
    /// a tam se zastavil) i „souřadnice je konečná". Rozhoduje, jestli zůstal TAM, KDE BYL.</para>
    /// </remarks>
    [Fact]
    public void A_colonist_outside_the_navigation_radius_does_not_fall_through_the_world()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);

        var start = new Vector3(8.5f, 2f, 8.5f);
        int colonist = colony.Colonists.Add(start);

        // Prázdný graf = žádná mřížka, tedy přesně stav „hráč je někde jinde".
        var jobs = new DigJobQueue();
        var prazdnyGraf = new NavGraph();

        for (int tick = 0; tick < 600; tick++)
        {
            colony.Colonists.Tick(world, world.Registry, NoWater, prazdnyGraf, jobs);
        }

        Vector3 konec = colony.Colonists.PositionOf(colonist);
        output.WriteLine($"Bez navigace: zacal na {start}, po 600 ticich je na {konec}.");

        Assert.Equal(start.Y, konec.Y, 0.01f);
        Assert.True(
            colony.Colonists.IsOnGround(colonist),
            "Bez navigační mřížky se považuje za padajícího.");
    }
}
