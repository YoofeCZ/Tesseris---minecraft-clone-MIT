using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Pochůznost voxelového terénu — predikát, na kterém bude stát pathfinding kolonistů (T2).
/// </summary>
/// <remarks>
/// Byla to soukromá metoda uvnitř <c>AnimalPopulation</c>, kde ji nešlo testovat bez zvířat.
/// Chyba tady se projeví jako tvor, který se vznáší nad zemí, propadne světem nebo se
/// zasekne na schodu — a v běžící hře se to hledá špatně.
/// </remarks>
public sealed class WalkabilityTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:water", Texture = "water", Opaque = false, Solid = false },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    private static (VoxelWorld World, ushort Stone) WorldWithFloor(int y = 0)
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                world.SetBlock(x, y, z, stone);
            }
        }

        return (world, stone);
    }

    [Fact]
    public void Full_block_supports_at_its_top_face()
    {
        (VoxelWorld world, _) = WorldWithFloor();

        Assert.True(Walkability.TrySupportSurface(
            world, world.Registry, NoWater, 0, 0, 0, 0.5f, 0.5f, out float feetY));

        Assert.Equal(1f, feetY, 2);
    }

    [Fact]
    public void Air_supports_nothing()
    {
        var world = new VoxelWorld(Registry());

        Assert.False(Walkability.TrySupportSurface(
            world, world.Registry, NoWater, 0, 5, 0, 0.5f, 0.5f, out _));
    }

    /// <summary>
    /// Tohle je oprava, kvůli které se predikát vytahoval. Blok otesaný na spodní čtvrtinu
    /// musí držet nohy ve čtvrtině, ne na horní hraně původní krychle.
    /// </summary>
    [Fact]
    public void Chiselled_block_supports_at_the_carved_surface_not_the_full_cube()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        world.SetBlock(0, 0, 0, stone);

        // Spodní čtyři patra mikrovoxelů ze šestnácti = čtvrtina bloku.
        MicroBlock micro = MicroBlock.Empty();
        for (int x = 0; x < MicroBlock.Size; x++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int y = 0; y < 4; y++)
                {
                    micro.SetMaterial(x, y, z, stone);
                }
            }
        }

        world.SetMicro(0, 0, 0, micro);

        Assert.True(Walkability.TrySupportSurface(
            world, world.Registry, NoWater, 0, 0, 0, 0.5f, 0.5f, out float feetY));

        // Čtyři šestnáctiny = 0,25. Dřív se sem dosadilo 1,001 a tvor se vznášel o tři čtvrtě bloku.
        Assert.Equal(0.25f, feetY, 2);
        Assert.True(feetY < 0.5f, $"Nohy mají být ve čtvrtině bloku, jsou na {feetY:F3}.");
    }

    /// <summary>
    /// Otesaný blok nemusí být pod nohama celý. Kde z něj v daném sloupci nic nezbylo,
    /// se stát nedá — dřív se i tam vracela horní hrana krychle.
    /// </summary>
    [Fact]
    public void Chiselled_block_gives_no_support_in_a_column_that_was_carved_away()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        world.SetBlock(0, 0, 0, stone);

        // Hmota jen v rohu u počátku: mikrovoxely 0..3 v obou vodorovných osách.
        MicroBlock micro = MicroBlock.Empty();
        for (int x = 0; x < 4; x++)
        {
            for (int z = 0; z < 4; z++)
            {
                for (int y = 0; y < 8; y++)
                {
                    micro.SetMaterial(x, y, z, stone);
                }
            }
        }

        world.SetMicro(0, 0, 0, micro);

        // Nad rohem s hmotou se stát dá, v půlce bloku ne.
        Assert.True(Walkability.TrySupportSurface(
            world, world.Registry, NoWater, 0, 0, 0, 0.1f, 0.1f, out float overSolid));
        Assert.Equal(0.5f, overSolid, 2);

        Assert.False(Walkability.TrySupportSurface(
            world, world.Registry, NoWater, 0, 0, 0, 0.8f, 0.8f, out _));
    }

    [Fact]
    public void Water_is_not_a_surface_to_stand_on()
    {
        var world = new VoxelWorld(Registry());
        ushort water = world.Registry.IndexOf("test:water");
        world.SetBlock(0, 0, 0, water);

        Assert.False(Walkability.TrySupportSurface(
            world, world.Registry, water, 0, 0, 0, 0.5f, 0.5f, out _));
    }

    [Fact]
    public void Ground_is_found_on_a_flat_floor()
    {
        (VoxelWorld world, _) = WorldWithFloor();

        Assert.True(Walkability.TryFindGround(
            world,
            world.Registry,
            NoWater,
            new Vector3(0.5f, 0f, 0.5f),
            fromY: 1f,
            height: 1.8f,
            width: 0.6f,
            out Vector3 feet));

        Assert.Equal(1f, feet.Y, 2);
    }

    [Fact]
    public void Ground_is_not_found_over_a_void()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Chunk existuje, ale je prázdný — jinak by se hledání zastavilo na chybějícím chunku
        // a test by neměřil to, co má.
        world.SetBlock(0, 40, 0, stone);

        Assert.False(Walkability.TryFindGround(
            world,
            world.Registry,
            NoWater,
            new Vector3(0.5f, 0f, 0.5f),
            fromY: 10f,
            height: 1.8f,
            width: 0.6f,
            out _));
    }

    /// <summary>
    /// Krok nahoru je jeden blok. Na schod se musí dosáhnout, na zeď o dva bloky výš ne.
    /// </summary>
    [Fact]
    public void Step_up_reaches_one_block_but_not_two()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(0, 0, 0, stone);   // podlaha
        world.SetBlock(1, 0, 0, stone);   // schod: spodek
        world.SetBlock(1, 1, 0, stone);   // schod: vrch, tedy povrch na y = 2

        Assert.True(Walkability.TryFindGround(
            world,
            world.Registry,
            NoWater,
            new Vector3(1.5f, 0f, 0.5f),
            fromY: 1f,
            height: 1.8f,
            width: 0.6f,
            out Vector3 feet));

        Assert.Equal(2f, feet.Y, 2);
    }

    [Fact]
    public void Ground_is_not_found_where_the_creature_would_not_fit()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(0, 0, 0, stone);   // podlaha, povrch na y = 1
        world.SetBlock(0, 2, 0, stone);   // strop: mezera je jen jeden blok
        world.SetBlock(0, 3, 0, stone);   // víko, aby si tvor nestoupl na strop

        // Mezera nad podlahou je jeden blok, tvor je vysoký 1,8 — nevejde se. Bez víka
        // by se hledání legitimně chytlo horní hrany stropu a našlo by zem o dva bloky výš.
        Assert.False(Walkability.TryFindGround(
            world,
            world.Registry,
            NoWater,
            new Vector3(0.5f, 0f, 0.5f),
            fromY: 1f,
            height: 1.8f,
            width: 0.6f,
            out _));
    }

    [Fact]
    public void Unloaded_chunk_is_not_treated_as_empty_space()
    {
        var world = new VoxelWorld(Registry());

        // Ve světě není jediný chunk. Kdyby se nenačtený chunk bral jako prázdno, tvor
        // by propadl světem, který se teprve streamuje.
        Assert.False(Walkability.TryFindGround(
            world,
            world.Registry,
            NoWater,
            new Vector3(0.5f, 0f, 0.5f),
            fromY: 1f,
            height: 1.8f,
            width: 0.6f,
            out _));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var world = new VoxelWorld(Registry());

        Assert.Throws<ArgumentNullException>(() => Walkability.TrySupportSurface(
            null!, world.Registry, NoWater, 0, 0, 0, 0.5f, 0.5f, out _));

        Assert.Throws<ArgumentNullException>(() => Walkability.TrySupportSurface(
            world, null!, NoWater, 0, 0, 0, 0.5f, 0.5f, out _));
    }
}
