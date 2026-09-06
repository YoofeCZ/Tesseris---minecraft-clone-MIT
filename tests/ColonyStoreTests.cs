using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Sklad kolonie jako skutečné místo a hlad kolonistů.
/// </summary>
/// <remarks>
/// <para><b>Proč obojí v jednom souboru.</b> Hlad bez skladu, ze kterého se dá vzít, neexistuje:
/// hladový kolonista musí mít kam dojít. Sklad je předpoklad, hlad je to, co ho začne používat.</para>
///
/// <para><b>Ke každému testu je poznamenané, co se stane bez opravy.</b> Test, který projde
/// i u rozbité věci, netestuje nic — v tomhle repu je to nejčastější chyba při ověřování
///.</para>
/// </remarks>
public sealed class ColonyStoreTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:ore", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:apple", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Podlaha na y = 1 přes celý chunk, tedy stání na y = 2.</summary>
    private static (VoxelWorld World, ushort Stone, ushort Ore, ushort Apple) FloorWorld()
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

        return (world, stone, world.Registry.IndexOf("test:ore"), world.Registry.IndexOf("test:apple"));
    }

    private static NavGraph GraphOf(VoxelWorld world)
    {
        var graph = new NavGraph();
        graph.AddChunk(NavGrid.Build(world, world.Registry, NoWater, Vector3i.Zero));
        graph.BuildPortals();
        return graph;
    }

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(NoWater);

        for (int i = 0; i < 8; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    // ================= SKLAD JAKO MÍSTO, NE POČÍTADLO =================

    /// <summary>
    /// Bez opravy: sklad byl <c>int</c>, takže nedokázal odpovědět, CO v něm je.
    /// </summary>
    [Fact]
    public void Store_knows_what_lies_in_it_not_just_how_much()
    {
        var store = new ColonyStore();
        store.Add(7, 3);
        store.Add(4, 2);
        store.Add(7);

        Assert.Equal(6, store.Total);
        Assert.Equal(2, store.KindCount);
        Assert.Equal(4, store.CountOf(7));
        Assert.Equal(2, store.CountOf(4));
        Assert.Equal(0, store.CountOf(9));
    }

    /// <summary>
    /// Pořadí druhů je dané tříděním podle id, ne pořadím příchodu. Na tom stojí bajtová
    /// shoda savu po kolečku uložit → načíst → uložit.
    /// </summary>
    [Fact]
    public void Kinds_are_sorted_so_the_order_does_not_depend_on_arrival()
    {
        var first = new ColonyStore();
        first.Add(9);
        first.Add(2);
        first.Add(5);

        var second = new ColonyStore();
        second.Add(5);
        second.Add(9);
        second.Add(2);

        for (int kind = 0; kind < first.KindCount; kind++)
        {
            Assert.Equal(first.ItemAt(kind), second.ItemAt(kind));
        }

        Assert.Equal(2, first.ItemAt(0));
        Assert.Equal(5, first.ItemAt(1));
        Assert.Equal(9, first.ItemAt(2));
    }

    /// <summary>Bez opravy: ze skladu nešlo nic vzít, takže hlad neměl z čeho jíst.</summary>
    [Fact]
    public void Taking_from_the_store_works_and_refuses_what_is_not_there()
    {
        var store = new ColonyStore();
        store.Add(7, 2);

        Assert.True(store.TryTake(7));
        Assert.Equal(1, store.CountOf(7));

        // Víc, než tam je: sklad musí zůstat nedotčený, ne spadnout do záporu.
        Assert.False(store.TryTake(7, 5));
        Assert.Equal(1, store.CountOf(7));

        Assert.False(store.TryTake(3));

        // Vyčerpaný druh z evidence zmizí, jinak by panel ukazoval řádky s nulou.
        Assert.True(store.TryTake(7));
        Assert.Equal(0, store.KindCount);
        Assert.Equal(0, store.Total);
    }

    /// <summary>
    /// Jídlo se váže na JMÉNO bloku, nikdy na číslo.
    /// </summary>
    /// <remarks>
    /// Bez toho by se index rozešel s obsahem při prvním přidaném bloku a nula, tedy vzduch,
    /// by se stala jídlem — hlad by pak šlo zahnat prázdným voxelem.
    /// </remarks>
    [Fact]
    public void Food_is_resolved_by_name_and_air_never_becomes_food()
    {
        BlockRegistry registry = Registry();

        // Registr testu žádné z reálných jmen jídla nezná, takže se nesmí nic rozložit —
        // a hlavně se nesmí dosadit nula.
        ushort[] resolved = ColonyFood.Resolve(registry);
        Assert.DoesNotContain((ushort)0, resolved);
        Assert.Empty(resolved);
        Assert.Equal(ColonyFood.Names.Count, ColonyFood.MissingNames(registry).Count);

        // A s registrem, který jméno zná, se rozloží právě ono.
        BlockRegistry withFood = BlockRegistry.Create(
        [
            new BlockDefinition { Id = "tesseris:apple", Texture = "stone", Opaque = true },
            new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        ]);

        ushort[] food = ColonyFood.Resolve(withFood);
        Assert.Single(food);
        Assert.Equal(withFood.IndexOf("tesseris:apple"), food[0]);
        Assert.NotEqual(0, food[0]);
    }

    /// <summary>Bez opravy: vykopaná ruda zvětšila číslo a druh se ztratil.</summary>
    [Fact]
    public void Mined_material_reaches_the_store_as_its_own_kind()
    {
        (VoxelWorld world, _, ushort ore, _) = FloorWorld();
        world.SetBlock(10, 2, 10, ore);

        ColonyRuntime colony = Colony(world);
        colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(10, 2, 10), new Vector3i(10, 2, 10));

        for (int tick = 0; tick < 5_000 && colony.Store.Total == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        Assert.Equal(1, colony.Store.Total);
        Assert.Equal(1, colony.Store.CountOf(ore));
        output.WriteLine($"Ve skladu je {colony.Store.Total} kusu druhu {ore}.");
    }

    /// <summary>
    /// Zbouraný pás vrátí KAŽDÝ druh zvlášť.
    /// </summary>
    /// <remarks>
    /// Bez opravy se vracel jen počet, takže z pásu se dvěma druhy vznikla hromádka toho
    /// prvního a druhý tiše zmizel. Mizející materiál je nejhorší druh chyby: nikde se
    /// nehlásí a projeví se až tím, že výroba nesedí.
    /// </remarks>
    [Fact]
    public void Demolished_belt_returns_every_kind_it_carried()
    {
        (VoxelWorld world, ushort stone, ushort ore, _) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        BeltSegment belt = colony.PlaceBelt(cell, cells: 8);

        Assert.True(colony.TryPushOntoBelt(belt, ore));
        for (int step = 0; step < 8; step++)
        {
            belt.Tick();
        }

        Assert.True(colony.TryPushOntoBelt(belt, stone));

        Assert.Equal(ColonyRuntime.Demolished.Belt, colony.Demolish(cell));

        Assert.Equal(1, colony.Store.CountOf(ore));
        Assert.Equal(1, colony.Store.CountOf(stone));
        Assert.Equal(2, colony.Store.Total);
    }

    /// <summary>
    /// Zbouraný stroj vrátí surovinu i hotové kusy, každé jako svůj druh.
    /// </summary>
    [Fact]
    public void Demolished_machine_returns_input_and_output_as_different_kinds()
    {
        (VoxelWorld world, ushort stone, ushort ore, _) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        var cell = new Vector3i(6, 2, 6);
        int crusher = colony.PlaceCrusher(cell, ore, stone, ticksPerCraft: 5);

        colony.Machines.TryInsert(crusher, ore);
        colony.Machines.TryInsert(crusher, ore);
        colony.Machines.SetPowered(crusher, true);

        // RUČNÍ STROJ BEZ ČLOVĚKA NEBĚŽÍ. Bez pásů je drtič pořád v ručním režimu, takže
        // sám proud nestačí — a bez toho by test neměl na výstupu co vracet.
        int worker = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        Assert.True(colony.TryAssignOperator(crusher, worker));

        // Nechat ho něco zdrtit, ať má i výstup.
        for (int tick = 0; tick < 200 && colony.Machines.OutputOf(crusher) == 0; tick++)
        {
            colony.Tick(world, world.Registry);
        }

        int input = colony.Machines.InputOf(crusher);
        int made = colony.Machines.OutputOf(crusher);
        Assert.True(made > 0, "Test potřebuje stroj s něčím na výstupu.");

        Assert.Equal(ColonyRuntime.Demolished.Machine, colony.Demolish(cell));

        Assert.Equal(input, colony.Store.CountOf(ore));
        Assert.Equal(made, colony.Store.CountOf(stone));
        output.WriteLine($"Z drtiče se vrátilo {input}x ruda a {made}x hotový kus.");
    }

    /// <summary>Sklad patří k radnici. Bez ní má obsah, ale nemá kam patřit.</summary>
    [Fact]
    public void The_store_gets_its_place_from_the_town_hall()
    {
        (VoxelWorld world, _, _, _) = FloorWorld();
        ColonyRuntime colony = Colony(world);

        Assert.False(colony.Store.HasCell);

        var cell = new Vector3i(8, 2, 8);
        Assert.True(colony.FoundTownHall(cell));

        Assert.True(colony.Store.HasCell);
        Assert.Equal(cell, colony.Store.Cell);

        // Druhá radnice kolonii nepřesouvá, takže ani sklad.
        Assert.False(colony.FoundTownHall(new Vector3i(20, 2, 20)));
        Assert.Equal(cell, colony.Store.Cell);
    }

    // ================= HLAD =================

    /// <summary>Hlad roste v ticích a je deterministický (pravidlo 6.6).</summary>
    [Fact]
    public void Hunger_grows_tick_by_tick()
    {
        (VoxelWorld world, _, _, _) = FloorWorld();
        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation();
        colony.Add(new Vector3i(4, 2, 4));

        Assert.Equal(0, colony.HungerOf(0));

        for (int tick = 0; tick < 100; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
        }

        Assert.Equal(100, colony.HungerOf(0));
        Assert.False(colony.IsHungry(0));
    }

    /// <summary>
    /// Hlad roste i tomu, kdo spí.
    /// </summary>
    /// <remarks>
    /// Kdyby rostl jen tikaným lidem (pravidlo 6.4), šel by hlad obejít tím, že se nic
    /// neoznačí — a prázdná kolonie by nehladověla vůbec.
    /// </remarks>
    [Fact]
    public void Even_a_sleeping_colonist_gets_hungry()
    {
        (VoxelWorld world, _, _, _) = FloorWorld();
        NavGraph graph = GraphOf(world);
        var jobs = new DigJobQueue();
        var colony = new ColonySimulation();
        colony.Add(new Vector3i(4, 2, 4));

        // První tik ho uspí: není práce.
        colony.Tick(world, world.Registry, NoWater, graph, jobs);
        Assert.Equal(0, colony.ActiveCount);

        for (int tick = 0; tick < 200; tick++)
        {
            colony.Tick(world, world.Registry, NoWater, graph, jobs);
        }

        Assert.True(colony.HungerOf(0) >= 200, "Spícímu kolonistovi nerostl hlad.");
    }

    /// <summary>
    /// ROZHODOVACÍ TEST: hladový kolonista dojde ke skladu, nají se a hlad klesne.
    /// </summary>
    /// <remarks>
    /// Bez opravy neexistuje ani stav <c>Eating</c>, ani způsob, jak si ze skladu vzít —
    /// hlad by rostl dál a tenhle test by vypršel na časovém stropu.
    /// </remarks>
    [Fact]
    public void A_hungry_colonist_walks_to_the_store_eats_and_goes_back_to_work()
    {
        (VoxelWorld world, _, ushort ore, ushort apple) = FloorWorld();

        // Práce na druhé straně, ať kolonista opravdu někde je a něco dělá. Prázdná kolonie,
        // ve které se nic neděje, nedokazuje nic.
        for (int x = 20; x < 24; x++)
        {
            world.SetBlock(x, 2, 20, ore);
        }

        ColonyRuntime colony = Colony(world);
        colony.Store.SetFood([apple]);
        Assert.True(colony.FoundTownHall(new Vector3i(4, 2, 4)));
        colony.Store.Add(apple, 2);

        int colonist = colony.TrySpawnColonist(new Vector3i(12, 2, 12));
        Assert.True(colonist >= 0);
        colony.MarkArea(world, world.Registry, new Vector3i(20, 2, 20), new Vector3i(23, 2, 20));

        // Hlad těsně pod práh: ať se nečeká minuta herního času.
        colony.Colonists.SetHunger(colonist, ColonySimulation.HungryAt - 5);

        bool ateOnTheWay = false;
        for (int tick = 0; tick < 5_000 && colony.Colonists.MealsEaten == 0; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
            ateOnTheWay |= colony.Colonists.StateOf(colonist) == ColonistState.Eating;
        }

        output.WriteLine($"Snedeno {colony.Colonists.MealsEaten}, hlad {colony.Colonists.HungerOf(colonist)}, "
            + $"jidla ve skladu {colony.Store.FoodCount}.");

        Assert.True(ateOnTheWay, "Kolonista se nikdy nedostal do stavu Eating.");
        Assert.Equal(1, colony.Colonists.MealsEaten);

        // Hlad je pryč a jídla ve skladu ubylo právě jedno.
        Assert.True(colony.Colonists.HungerOf(colonist) < ColonySimulation.HungryAt);
        Assert.Equal(1, colony.Store.FoodCount);

        // A pokračuje v práci — jídlo není konec, je to úkol navíc.
        int done = colony.Jobs.DoneCount;
        for (int tick = 0; tick < 5_000 && colony.Jobs.DoneCount == done; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        Assert.True(colony.Jobs.DoneCount > done, "Po jídle se přestalo pracovat.");
    }

    /// <summary>
    /// Bez jídla se pro něj nechodí. Cesta k prázdnému skladu je jen ztracený čas.
    /// </summary>
    [Fact]
    public void With_an_empty_store_nobody_walks_for_food()
    {
        (VoxelWorld world, _, _, ushort apple) = FloorWorld();

        ColonyRuntime colony = Colony(world);
        colony.Store.SetFood([apple]);
        colony.FoundTownHall(new Vector3i(4, 2, 4));

        int colonist = colony.TrySpawnColonist(new Vector3i(12, 2, 12));
        colony.Colonists.SetHunger(colonist, ColonySimulation.HungryAt);

        for (int tick = 0; tick < 600; tick++)
        {
            colony.Tick(world, world.Registry);
            Assert.NotEqual(ColonistState.Eating, colony.Colonists.StateOf(colonist));
        }

        Assert.Equal(0, colony.Colonists.MealsEaten);
        Assert.True(colony.Colonists.HungerOf(colonist) > ColonySimulation.HungryAt,
            "Bez jídla musí hlad růst dál.");
    }

    /// <summary>
    /// DRUHÁ POLOVINA ROZHODOVACÍHO TESTU: bez jídla se práce zpomalí.
    /// </summary>
    /// <remarks>
    /// <para><b>Kontrolní běh je součástí testu.</b> Stejná kolonie, stejný počet tiků, jediný
    /// rozdíl je hlad — bez toho by se nedalo poznat, jestli měřím zpomalení, nebo jen to,
    /// že práce trvá.</para>
    /// </remarks>
    [Fact]
    public void Starving_colonists_work_measurably_slower()
    {
        const int Ticks = 1_500;

        int fed = DigIn(Ticks, starving: false);
        int starving = DigIn(Ticks, starving: true);

        output.WriteLine($"Za {Ticks} tiku vykopal najezeny {fed}, vyhladovely {starving}.");

        // PRÁCE MUSÍ ZBÝT. Kdyby se za tu dobu stihlo všechno, vyšla by obě čísla stejně
        // a test by prošel i u rozbitého zpomalení — to je nejčastější chyba při ověřování
        // v tomhle repu.
        Assert.True(fed > 0, "Kontrolní běh nevykopal nic — test by neměřil nic.");
        Assert.True(fed < MarkedBlocks, "Práce došla dřív než čas; test by neměřil rychlost.");
        Assert.True(starving < fed, "Hlad práci nezpomalil.");

        static int DigIn(int ticks, bool starving)
        {
            (VoxelWorld world, _, ushort ore, _) = FloorWorld();

            for (int x = 10; x < 10 + MarkedBlocks; x++)
            {
                world.SetBlock(x, 2, 12, ore);
            }

            ColonyRuntime colony = Colony(world);
            int colonist = colony.TrySpawnColonist(new Vector3i(8, 2, 12));
            colony.MarkArea(
                world,
                world.Registry,
                new Vector3i(10, 2, 12),
                new Vector3i(10 + MarkedBlocks - 1, 2, 12));

            for (int tick = 0; tick < ticks; tick++)
            {
                // Hlad se drží na místě, ať se měří rozdíl rychlosti, ne postupné hladovění.
                colony.Colonists.SetHunger(
                    colonist,
                    starving ? ColonySimulation.StarvingAt : 0);

                colony.Tick(world, world.Registry);
                colony.Colonists.WakeIdle();
            }

            return colony.Jobs.DoneCount;
        }
    }

    /// <summary>Kolik bloků se v testu rychlosti označí. Víc, než se za měřený čas stihne.</summary>
    private const int MarkedBlocks = 40;

    /// <summary>
    /// Krok se interpoluje podle SKUTEČNÉ délky kroku, ne podle konstanty.
    /// </summary>
    /// <remarks>
    /// Bez toho by hladová postava první polovinu kroku doklouzala a druhou stála — přesně
    /// ta půlka věty o pevném tiku a interpolaci.
    /// </remarks>
    [Fact]
    public void Step_interpolation_follows_the_slowed_down_step()
    {
        (VoxelWorld world, _, ushort ore, _) = FloorWorld();
        world.SetBlock(20, 2, 20, ore);

        ColonyRuntime colony = Colony(world);
        int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
        colony.MarkArea(world, world.Registry, new Vector3i(20, 2, 20), new Vector3i(20, 2, 20));
        colony.Colonists.SetHunger(colonist, ColonySimulation.StarvingAt);

        float worst = 0f;
        for (int tick = 0; tick < 400; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
            colony.Colonists.SetHunger(colonist, ColonySimulation.StarvingAt);
            worst = MathF.Max(worst, colony.Colonists.StepProgressOf(colonist));
        }

        // Se starým jmenovatelem by postup dojel na 1 už v půlce kroku a zbytek by stál.
        Assert.True(worst <= 1f, "Postup kroku přetekl přes jedničku.");
        output.WriteLine($"Nejvyssi postup kroku u vyhladoveleho: {worst:F3}.");
    }

    // ================= SAV =================

    /// <summary>Obsah skladu i hlad musí přežít restart.</summary>
    /// <remarks>
    /// Bez uloženého hladu by restart nakrmil celou kolonii zadarmo — tichý únik tlaku,
    /// kvůli kterému hlad vzniká.
    /// </remarks>
    [Fact]
    public void The_store_contents_and_hunger_survive_a_restart()
    {
        (VoxelWorld world, ushort stone, ushort ore, ushort apple) = FloorWorld();
        string directory = Path.Combine(
            Path.GetTempPath(), "tesseris-sklad-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, ColonySave.FileName);

        try
        {
            ColonyRuntime colony = Colony(world);
            colony.Store.SetFood([apple]);
            colony.FoundTownHall(new Vector3i(8, 2, 8));

            colony.Store.Add(ore, 5);
            colony.Store.Add(stone, 2);
            colony.Store.Add(apple, 3);

            int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
            colony.Colonists.SetHunger(colonist, 1_234);

            ColonySave.Save(colony, path);

            ColonyRuntime loaded = Colony(world);
            loaded.Store.SetFood([apple]);
            Assert.True(ColonySave.Load(loaded, path) > 0);

            Assert.Equal(10, loaded.Store.Total);
            Assert.Equal(5, loaded.Store.CountOf(ore));
            Assert.Equal(2, loaded.Store.CountOf(stone));
            Assert.Equal(3, loaded.Store.CountOf(apple));
            Assert.Equal(3, loaded.Store.FoodCount);
            Assert.Equal(1_234, loaded.Colonists.HungerOf(0));

            output.WriteLine($"Po nacteni ma sklad {loaded.Store.Total} kusu "
                + $"ve {loaded.Store.KindCount} druzich a clovek hlad {loaded.Colonists.HungerOf(0)}.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Kolečko uložit → načíst → uložit musí být bajt po bajtu stejné i se skladem a hladem.
    /// </summary>
    [Fact]
    public void Save_load_save_with_a_full_store_is_byte_identical()
    {
        (VoxelWorld world, ushort stone, ushort ore, ushort apple) = FloorWorld();
        string directory = Path.Combine(
            Path.GetTempPath(), "tesseris-sklad-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, ColonySave.FileName);

        try
        {
            ColonyRuntime colony = Colony(world);
            colony.FoundTownHall(new Vector3i(8, 2, 8));

            // Schválně v opačném pořadí, než jaké má vyjít v souboru: tříděním se to srovná.
            colony.Store.Add(apple, 3);
            colony.Store.Add(ore, 5);
            colony.Store.Add(stone, 2);

            int colonist = colony.TrySpawnColonist(new Vector3i(4, 2, 4));
            colony.Colonists.SetHunger(colonist, 777);
            colony.PlaceBelt(new Vector3i(6, 2, 6), cells: 4);

            ColonySave.Save(colony, path);
            byte[] first = File.ReadAllBytes(path);

            ColonyRuntime loaded = Colony(world);
            Assert.True(ColonySave.Load(loaded, path) > 0);

            string second = path + ".2";
            ColonySave.Save(loaded, second);

            Assert.Equal(first, File.ReadAllBytes(second));
            output.WriteLine($"Sav se skladem ma {first.Length} bajtu a po kolecku je totozny.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Kolonie, ve které je jen naskladněné jídlo, se musí uložit.
    /// </summary>
    /// <remarks>
    /// Bez tohohle by se sklad plný jídla považoval za prázdnou kolonii a při uložení by se
    /// smazal. Tichá ztráta zásob je horší než chybějící sav.
    /// </remarks>
    [Fact]
    public void A_colony_with_only_a_stocked_store_is_still_saved()
    {
        (VoxelWorld world, _, _, ushort apple) = FloorWorld();
        string directory = Path.Combine(
            Path.GetTempPath(), "tesseris-sklad-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, ColonySave.FileName);

        try
        {
            ColonyRuntime colony = Colony(world);
            colony.Store.Add(apple, 4);

            ColonySave.Save(colony, path);
            Assert.True(File.Exists(path), "Sklad s jídlem se neuložil.");

            ColonyRuntime loaded = Colony(world);
            ColonySave.Load(loaded, path);
            Assert.Equal(4, loaded.Store.CountOf(apple));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
