using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Zakládání kolonie radnicí a postupný příchod kolonistů.
/// </summary>
/// <remarks>
/// <para><b>Proč to vzniklo.</b> Dřív se šest kolonistů objevilo samo kolem hráče, jakmile
/// dojela navigace. Hráč neměl co rozhodnout a kolonie neměla střed. Teď kolonii zakládá
/// blok radnice a lidé chodí postupně k ní.</para>
///
/// <para><b>Co se hlídá.</b> Že bez radnice nepřijde vážně nikdo (ne „přijdou později"),
/// že po založení chodí po jednom a ne naráz, a že nedoručený příchod nepropadne.</para>
/// </remarks>
public sealed class TownHallTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    /// <summary>Podlaha přes celý chunk, tedy stání na y = 2.</summary>
    private static VoxelWorld FloorWorld()
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

        return world;
    }

    private static ColonyRuntime RuntimeWithFloor()
    {
        VoxelWorld world = FloorWorld();
        var colony = new ColonyRuntime();
        colony.SetWater(NoWater);
        colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));

        // Navigace má rozpočet na tik, takže se musí doplnit ve víc krocích.
        for (int i = 0; i < 64 && colony.NavigationChunks == 0; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    /// <summary>
    /// Bez radnice nepřijde nikdo, ani po dlouhém čekání.
    /// </summary>
    /// <remarks>
    /// Rozhodovací test celé změny. Tiká se víc než dvacetinásobek intervalu příchodu — kdyby
    /// se lidé rodili sami, tady by se to projevilo.
    /// </remarks>
    [Fact]
    public void Bez_radnice_neprijde_nikdo()
    {
        ColonyRuntime colony = RuntimeWithFloor();

        for (int tick = 0; tick < TownHall.TicksPerArrival * 20; tick++)
        {
            Assert.Equal(-1, colony.TryWelcomeColonist());
        }

        Assert.False(colony.TownHall.IsFounded);
        Assert.Equal(0, colony.Colonists.Count);
        Assert.Equal(0, colony.TownHall.Arrived);
    }

    /// <summary>Postavení radnice kolonii založí a první člověk přijde hned.</summary>
    /// <remarks>
    /// „Hned" je schválně: prázdná kolonie po založení vypadá, jako by se nic nestalo.
    /// </remarks>
    [Fact]
    public void Radnice_zalozi_kolonii_a_prvni_clovek_prijde_hned()
    {
        ColonyRuntime colony = RuntimeWithFloor();

        Assert.True(colony.TownHall.Found(new Vector3i(8, 2, 8)));
        Assert.True(colony.TownHall.IsFounded);

        Assert.True(colony.TryWelcomeColonist() >= 0);
        Assert.Equal(1, colony.Colonists.Count);
        Assert.Equal(1, colony.TownHall.Arrived);
    }

    /// <summary>
    /// Lidé chodí po jednom, ne všichni naráz.
    /// </summary>
    /// <remarks>
    /// Tenhle test by prošel i u rozbité věci, kdyby jen počítal, že jich nakonec je víc.
    /// Proto kontroluje, že mezi dvěma příchody je opravdu ticho.
    /// </remarks>
    [Fact]
    public void Kolonisti_prichazeji_postupne_ne_naraz()
    {
        ColonyRuntime colony = RuntimeWithFloor();
        colony.TownHall.Found(new Vector3i(8, 2, 8));

        Assert.True(colony.TryWelcomeColonist() >= 0);
        Assert.Equal(1, colony.Colonists.Count);

        // Hned potom nesmí přijít nikdo.
        for (int tick = 1; tick < TownHall.TicksPerArrival; tick++)
        {
            Assert.Equal(-1, colony.TryWelcomeColonist());
        }

        Assert.Equal(1, colony.Colonists.Count);

        // Až na konci intervalu druhý.
        Assert.True(colony.TryWelcomeColonist() >= 0);
        Assert.Equal(2, colony.Colonists.Count);
    }

    /// <summary>Kolonie nepřeroste strop, i když se tiká donekonečna.</summary>
    [Fact]
    public void Kolonie_neprerost_strop()
    {
        ColonyRuntime colony = RuntimeWithFloor();
        colony.TownHall.Found(new Vector3i(8, 2, 8));

        for (int tick = 0; tick < TownHall.TicksPerArrival * (TownHall.MaxColonists + 5); tick++)
        {
            colony.TryWelcomeColonist();
        }

        Assert.Equal(TownHall.MaxColonists, colony.Colonists.Count);
    }

    /// <summary>Druhá radnice kolonii nepřesune.</summary>
    /// <remarks>
    /// Přesun střediska by znamenal přepočítat, kam všichni patří. Dokud to rozhodnutí
    /// nepadne, drží se první radnice a druhá se odmítne.
    /// </remarks>
    [Fact]
    public void Druha_radnice_kolonii_nepresune()
    {
        var hall = new TownHall();
        var first = new Vector3i(8, 2, 8);

        Assert.True(hall.Found(first));
        Assert.False(hall.Found(new Vector3i(40, 2, 40)));
        Assert.Equal(first, hall.Cell);
    }

    /// <summary>
    /// Nedoručený příchod se nezapíše a nárok zůstane.
    /// </summary>
    /// <remarks>
    /// Kdyby se příchod počítal už při odbití hodin, kolonie by tiše přišla o lidi vždycky,
    /// když zrovna nebylo kam je postavit. Tady není žádná podlaha, takže není kam.
    /// </remarks>
    [Fact]
    public void Kdyz_neni_kam_stoupnout_narok_nepropadne()
    {
        var colony = new ColonyRuntime();
        colony.SetWater(NoWater);
        colony.TownHall.Found(new Vector3i(8, 2, 8));

        Assert.Equal(-1, colony.TryWelcomeColonist());
        Assert.Equal(0, colony.TownHall.Arrived);
        Assert.Equal(0, colony.Colonists.Count);
    }

    /// <summary>Zbouraná radnice kolonii ruší a další lidé nechodí.</summary>
    [Fact]
    public void Zbourana_radnice_zastavi_prichody()
    {
        ColonyRuntime colony = RuntimeWithFloor();
        colony.TownHall.Found(new Vector3i(8, 2, 8));
        Assert.True(colony.TryWelcomeColonist() >= 0);

        colony.TownHall.Abandon();
        Assert.False(colony.TownHall.IsFounded);

        for (int tick = 0; tick < TownHall.TicksPerArrival * 3; tick++)
        {
            Assert.Equal(-1, colony.TryWelcomeColonist());
        }
    }

    /// <summary>Blok radnice je v assetech a má neprůhlednou texturu.</summary>
    /// <remarks>
    /// Bez skutečného bloku by kolonii nešlo založit ve hře, i kdyby simulace fungovala.
    /// </remarks>
    [Fact]
    public void Blok_radnice_existuje_v_assetech()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "assets");
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(Path.Combine(assets, "blocks"));

        ushort hall = blocks.IndexOf(TownHall.BlockId);
        Assert.NotEqual(BlockRegistry.Air, hall);
        Assert.True(blocks.IsOpaque(hall));

        foreach (string texture in new[] { "bricks", "red_roof_tiles" })
        {
            Assert.True(
                File.Exists(Path.Combine(assets, "textures", $"{texture}.png")),
                $"Chybí textura {texture}.png pro radnici.");
        }
    }
}
