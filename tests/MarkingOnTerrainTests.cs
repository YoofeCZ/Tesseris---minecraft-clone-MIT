using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Označování k vykopání na skutečném, nerovném terénu.
/// </summary>
/// <remarks>
/// <para>Uživatel hlásí, že ve velitelském režimu „se nic neoznačuje a všechno je odložené".
/// Dosavadní testy označovaly na rovné podlaze v jedné výšce, takže tenhle případ nikdy
/// nenastal — a přesně tím se to schovalo.</para>
///
/// <para>Velitel označuje na VODOROVNÉ ROVINĚ ŘEZU: paprsek se protne s rovinou v pevném Y
/// a označí se buňky v ní. Na svahu je ale v každém sloupci povrch jinde.</para>
/// </remarks>
public sealed class MarkingOnTerrainTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    /// <summary>Svah: v ose X terén stoupá o blok na každou buňku.</summary>
    private static VoxelWorld Slope()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            int height = 1 + (x / 2);
            for (int z = 0; z < NavGrid.Size; z++)
            {
                for (int y = 0; y <= height; y++)
                {
                    world.SetBlock(x, y, z, stone);
                }
            }
        }

        return world;
    }

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(ushort.MaxValue);

        for (int i = 0; i < 12; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 12f, 8f));
        }

        return colony;
    }

    /// <summary>
    /// Tažení v jednom patře přes svah. Tohle je přesně to, co hráč ve velitelském režimu dělá.
    /// </summary>
    [Fact]
    public void Marking_one_slice_across_a_slope_mostly_produces_unreachable_work()
    {
        VoxelWorld world = Slope();
        ColonyRuntime colony = Colony(world);

        // Kolonista stojí na svahu.
        int colonist = colony.TrySpawnColonist(new Vector3i(4, 4, 8));
        Assert.True(colonist >= 0, "Kolonista neměl kde stát.");

        // Řez v patře, kde hráč stojí — přesně to nastavuje ToggleCommanderMode.
        const int Slice = 4;
        int marked = colony.MarkArea(
            world, world.Registry,
            new Vector3i(0, Slice, 6),
            new Vector3i(NavGrid.Size - 1, Slice, 10));

        for (int tick = 0; tick < 4_000; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        int open = colony.Jobs.OpenCount;
        int claimed = colony.Jobs.ClaimedCount;
        int done = colony.Jobs.DoneCount;
        int deferred = colony.Jobs.DeferredCount;

        output.WriteLine($"Oznaceno {marked} z {NavGrid.Size * 5} bunek v rezu.");
        output.WriteLine($"volna {open}, dela se {claimed}, hotova {done}, odlozena {deferred}.");

        // Tenhle test nic netvrdí o tom, co JE správně — jen zapisuje, co se dneska děje,
        // aby se o tom dalo mluvit s čísly.
        Assert.Equal(marked, open + claimed + done + deferred);
    }

    /// <summary>
    /// TOTÉŽ TAŽENÍ, ale označuje se podle prvního zásahu paprsku a nedosažitelné se zamítne.
    /// </summary>
    /// <remarks>
    /// <para>Tohle je měřený rozdíl proti <see cref="Marking_one_slice_across_a_slope_mostly_produces_unreachable_work"/>.
    /// Tam se ze 130 označených buněk povedlo 15 a 115 zůstalo odložených, protože se mířilo
    /// na vodorovnou rovinu řezu a většina buněk v ní je zavalená.</para>
    ///
    /// <para>BEZ OPRAVY TENHLE TEST PADÁ: se starým chováním jsou odložené buňky ve většině.</para>
    /// </remarks>
    [Fact]
    public void Marking_what_the_ray_hits_leaves_almost_nothing_deferred()
    {
        VoxelWorld world = Slope();
        ColonyRuntime colony = Colony(world);

        int colonist = colony.TrySpawnColonist(new Vector3i(4, 4, 8));
        Assert.True(colonist >= 0, "Kolonista neměl kde stát.");

        // Velitelská kamera stojí šikmo nad svahem a míří na něj. Odsud se pro každý sloupec
        // vezme to, co paprsek opravdu trefí — tedy povrch, ne buňka v rovině řezu.
        var camera = new Vector3(4f, 40f, 60f);
        const int Slice = 20;

        int added = 0;
        int rejected = 0;
        int missed = 0;

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 6; z <= 10; z++)
            {
                // Paprsek do středu horní stěny povrchového bloku daného sloupce.
                var through = new Vector3(x + 0.5f, TopOf(world, x, z) + 0.9f, z + 0.5f);

                if (!CommanderPicking.TryPick(
                        world, world.Registry, camera, through - camera, Slice,
                        out Vector3i cell, out bool solid) || !solid)
                {
                    missed++;
                    continue;
                }

                ColonyRuntime.MarkResult result = colony.MarkAreaReachable(
                    world, world.Registry, cell, cell);

                added += result.Added;
                rejected += result.Unreachable;
            }
        }

        for (int tick = 0; tick < 8_000; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        int done = colony.Jobs.DoneCount;
        int deferred = colony.Jobs.DeferredCount;

        output.WriteLine($"Paprskem: oznaceno {added}, zamitnuto {rejected}, netrefeno {missed}.");
        output.WriteLine($"hotova {done}, odlozena {deferred}, volna {colony.Jobs.OpenCount}, "
            + $"dela se {colony.Jobs.ClaimedCount}.");

        Assert.True(added > 0, "Paprsek neoznačil nic.");

        // JÁDRO ÚKOLU: odložených dramaticky ubylo. Se starým chováním (rovina řezu) jich bylo
        // 115 ze 130, tedy 88 procent; tady musí být menšinou proti hotovým.
        Assert.True(
            deferred * 4 < added,
            $"Odložených je pořád moc: {deferred} z {added} označených.");
    }

    /// <summary>Nejvyšší pevný blok ve sloupci. Jen pro namíření testovacího paprsku.</summary>
    private static int TopOf(VoxelWorld world, int x, int z)
    {
        ushort stone = world.Registry.IndexOf("test:stone");
        for (int y = NavGrid.Size - 1; y >= 0; y--)
        {
            if (world.GetBlock(x, y, z) == stone)
            {
                return y;
            }
        }

        return 0;
    }

    /// <summary>
    /// Past, kvůli které to nesmí být „nejvyšší pevný blok ve sloupci": pod převisem musí
    /// paprsek zvenku trefit STŘECHU, ale paprsek zpod převisu to, na co se hráč dívá.
    /// </summary>
    [Fact]
    public void Picking_under_an_overhang_takes_the_first_hit_not_the_column_maximum()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Podlaha na y = 4 a střecha na y = 10 nad ní. Mezi tím je dutina.
        for (int x = 4; x <= 12; x++)
        {
            for (int z = 4; z <= 12; z++)
            {
                world.SetBlock(x, 4, z, stone);
                world.SetBlock(x, 10, z, stone);
            }
        }

        // Řez pod střechou: střecha je nad ním, takže je pro paprsek průhledná a trefí se
        // podlaha uvnitř dutiny. Tohle je celý smysl degradace řezu na strop.
        Assert.True(CommanderPicking.TryPick(
            world, world.Registry,
            new Vector3(8.5f, 30f, 8.5f), new Vector3(0f, -1f, 0f),
            sliceY: 9,
            out Vector3i inside, out bool insideSolid));

        Assert.True(insideSolid);
        Assert.Equal(4, inside.Y);

        // A při řezu nad střechou se trefí střecha, protože ta je první na cestě.
        Assert.True(CommanderPicking.TryPick(
            world, world.Registry,
            new Vector3(8.5f, 30f, 8.5f), new Vector3(0f, -1f, 0f),
            sliceY: 20,
            out Vector3i roof, out _));

        Assert.Equal(10, roof.Y);
    }

    /// <summary>
    /// Ostrov za útesem: sousední pochůzná buňka existuje, ale cesta k němu nevede. Tohle
    /// je přesně ten případ, kde „má sousední pochůznou buňku" nestačí.
    /// </summary>
    [Fact]
    public void Marking_across_a_gap_rejects_the_unreachable_side()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Dvě plošiny, mezi nimi trojblokový příkop až na dno světa.
        for (int z = 4; z <= 12; z++)
        {
            for (int x = 2; x <= 8; x++)
            {
                world.SetBlock(x, 0, z, stone);
                world.SetBlock(x, 1, z, stone);
                world.SetBlock(x, 2, z, stone);
            }

            for (int x = 14; x <= 20; x++)
            {
                world.SetBlock(x, 0, z, stone);
                world.SetBlock(x, 1, z, stone);
                world.SetBlock(x, 2, z, stone);
            }
        }

        ColonyRuntime colony = Colony(world);
        Assert.True(colony.TrySpawnColonist(new Vector3i(4, 3, 8)) >= 0);

        // Blok na blízké plošině je dosažitelný.
        ColonyRuntime.MarkResult near = colony.MarkAreaReachable(
            world, world.Registry, new Vector3i(5, 2, 8), new Vector3i(5, 2, 8));

        Assert.True(near.Filtered, "Filtr se nespustil, test by netestoval nic.");
        Assert.Equal(1, near.Added);
        Assert.Equal(0, near.Unreachable);

        // Blok na druhé plošině má sousední pochůzné buňky, ale nikdo se tam nedostane.
        ColonyRuntime.MarkResult far = colony.MarkAreaReachable(
            world, world.Registry, new Vector3i(16, 2, 8), new Vector3i(16, 2, 8));

        output.WriteLine($"Za prikopem: pridano {far.Added}, zamitnuto {far.Unreachable}.");

        Assert.Equal(0, far.Added);
        Assert.Equal(1, far.Unreachable);
    }

    /// <summary>
    /// Bez kolonistů se NEFILTRUJE. Odmítnout hráči práci proto, že se dosažitelnost nedala
    /// spočítat, by bylo horší než ji přijmout.
    /// </summary>
    [Fact]
    public void Without_colonists_nothing_is_rejected()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 4; x <= 8; x++)
        {
            for (int z = 4; z <= 8; z++)
            {
                world.SetBlock(x, 2, z, stone);
            }
        }

        ColonyRuntime colony = Colony(world);

        ColonyRuntime.MarkResult result = colony.MarkAreaReachable(
            world, world.Registry, new Vector3i(4, 2, 4), new Vector3i(8, 2, 8));

        Assert.False(result.Filtered);
        Assert.Equal(25, result.Added);
        Assert.Equal(0, result.Unreachable);
    }

    /// <summary>
    /// Kolik z jednoho řezu je vůbec vzduch. Vzduch se neoznačí, takže hráč vidí menší
    /// číslo, než jaké plochy se dotkl — a nechápe proč.
    /// </summary>
    [Fact]
    public void A_flat_slice_across_a_slope_touches_air_and_buried_rock()
    {
        VoxelWorld world = Slope();
        ushort stone = world.Registry.IndexOf("test:stone");

        const int Slice = 4;
        int air = 0;
        int surface = 0;
        int buried = 0;

        for (int x = 0; x < NavGrid.Size; x++)
        {
            ushort here = world.GetBlock(x, Slice, 8);
            ushort above = world.GetBlock(x, Slice + 1, 8);

            if (here != stone)
            {
                air++;
            }
            else if (above == stone)
            {
                buried++;
            }
            else
            {
                surface++;
            }
        }

        output.WriteLine($"V jednom rezu pres svah: vzduch {air}, povrch {surface}, zavaleno {buried} "
            + $"(z {NavGrid.Size} sloupcu).");

        // POVRCHOVÝCH BUNĚK JE V JEDNOM ŘEZU MIZIVĚ MÁLO. Zbytek je buď vzduch, kde se
        // neoznačí nic, nebo zavalený kámen, na který se nedá dosáhnout.
        Assert.True(surface < air + buried, "Test počítal s tím, že povrch je v řezu menšina.");
    }

    /// <summary>
    /// Kontrolní případ: na rovině to funguje. Proto se na to nepřišlo dřív.
    /// </summary>
    [Fact]
    public void On_flat_ground_the_same_marking_works()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // DVĚ VRSTVY. S jednou by vykopáním zmizela země pod nohama a kolonista by vypadl
        // ze světa — na to jsem naletěl při psaní tohohle testu.
        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 0, z, stone);
                world.SetBlock(x, 1, z, stone);
                world.SetBlock(x, 2, z, stone);
            }
        }

        ColonyRuntime colony = Colony(world);
        colony.TrySpawnColonist(new Vector3i(4, 3, 4));

        int marked = colony.MarkArea(world, world.Registry, new Vector3i(6, 2, 6), new Vector3i(9, 2, 9));
        Assert.Equal(16, marked);

        for (int tick = 0; tick < 8_000 && colony.Jobs.DoneCount < marked; tick++)
        {
            colony.Tick(world, world.Registry);
            colony.Colonists.WakeIdle();
        }

        output.WriteLine($"Na rovine: hotova {colony.Jobs.DoneCount} z {marked}, "
            + $"odlozena {colony.Jobs.DeferredCount}.");

        Assert.Equal(marked, colony.Jobs.DoneCount);
        Assert.Equal(0, colony.Jobs.DeferredCount);
    }
}
