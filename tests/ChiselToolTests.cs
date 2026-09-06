using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>Testy tesacích nástrojů — všech šest režimů a všechny velikosti.</summary>
public sealed class ChiselToolTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true, Chiselable = true },
        new BlockDefinition { Id = "test:glass", Texture = "glass", Opaque = false, Chiselable = false },
    ]);

    private static VoxelWorld WorldWithBlock(out ushort stone, string id = "test:stone")
    {
        var world = new VoxelWorld(Registry());
        stone = world.Registry.IndexOf(id);
        world.SetBlock(0, 0, 0, stone);
        return world;
    }

    private static MicroHit HitAt(int mx, int my, int mz, Vector3i normal, ushort material) =>
        new(Vector3i.Zero, new Vector3i(mx, my, mz), normal, BlockFace.PosY, 0f, Vector3.Zero, material);

    [Fact]
    public void Prvni_sek_prevede_plny_blok_na_mikro()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);
        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = 1 };

        Assert.Null(world.GetMicro(0, 0, 0));

        Assert.True(tool.Apply(world, HitAt(0, 15, 0, new Vector3i(0, 1, 0), stone), out _));

        MicroBlock? micro = world.GetMicro(0, 0, 0);
        Assert.NotNull(micro);
        Assert.Equal(MicroBlock.Volume - 1, micro.SolidCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 8)]
    [InlineData(4, 64)]
    [InlineData(8, 512)]
    public void Velikost_nastroje_urcuje_kolik_se_ubere(int size, int expectedRemoved)
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);
        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = size };

        tool.Apply(world, HitAt(0, 0, 0, new Vector3i(0, -1, 0), stone), out _);

        MicroBlock micro = world.GetMicro(0, 0, 0)!;
        Assert.Equal(MicroBlock.Volume - expectedRemoved, micro.SolidCount);
    }

    [Fact]
    public void Nepovolena_velikost_se_ignoruje()
    {
        var tool = new ChiselTool { Size = 4 };

        tool.Size = 3;
        Assert.Equal(4, tool.Size);

        tool.Size = 8;
        Assert.Equal(8, tool.Size);
    }

    [Fact]
    public void Zasah_se_zarovna_na_mrizku_nastroje()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);
        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = 4 };

        // Zásah na (5,5,5) při velikosti 4 se zarovná na (4,4,4).
        tool.Apply(world, HitAt(5, 5, 5, new Vector3i(0, 1, 0), stone), out _);

        MicroBlock micro = world.GetMicro(0, 0, 0)!;
        Assert.False(micro.IsSolid(4, 4, 4));
        Assert.False(micro.IsSolid(7, 7, 7));
        Assert.True(micro.IsSolid(3, 3, 3));
        Assert.True(micro.IsSolid(8, 8, 8));
    }

    [Fact]
    public void Uplne_vytesany_blok_zmizi()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);
        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = 8 };

        // Osm zásahů po osmi mikrovoxelech pokryje celou mřížku.
        foreach ((int x, int y, int z) in new[]
        {
            (0, 0, 0), (8, 0, 0), (0, 8, 0), (8, 8, 0),
            (0, 0, 8), (8, 0, 8), (0, 8, 8), (8, 8, 8),
        })
        {
            tool.Apply(world, HitAt(x, y, z, new Vector3i(0, 1, 0), stone), out _);
        }

        Assert.Null(world.GetMicro(0, 0, 0));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(0, 0, 0));
        Assert.False(world.IsSolid(0, 0, 0));
    }

    [Fact]
    public void Neotesatelny_blok_odola()
    {
        var world = new VoxelWorld(Registry());
        ushort glass = world.Registry.IndexOf("test:glass");
        world.SetBlock(0, 0, 0, glass);

        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = 2 };

        Assert.False(tool.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), glass), out _));
        Assert.Null(world.GetMicro(0, 0, 0));
    }

    [Fact]
    public void Pridani_polozi_hmotu_pred_stenu()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        // Nejdřív vydlabat prostor, aby bylo kam přidávat.
        var remove = new ChiselTool { Mode = ChiselMode.Remove, Size = 8 };
        remove.Apply(world, HitAt(0, 8, 0, new Vector3i(0, 1, 0), stone), out _);

        MicroBlock before = world.GetMicro(0, 0, 0)!;
        int solidBefore = before.SolidCount;

        var add = new ChiselTool { Mode = ChiselMode.Add, Size = 2, Material = stone };
        Assert.True(add.Apply(world, HitAt(0, 6, 0, new Vector3i(0, 1, 0), stone), out _));

        Assert.True(world.GetMicro(0, 0, 0)!.SolidCount > solidBefore);
    }

    [Fact]
    public void Pridani_pres_hranici_bloku_zasahne_souseda()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        var add = new ChiselTool { Mode = ChiselMode.Add, Size = 2, Material = stone };

        // Zásah na horní stěně bloku: přidat se má do bloku nad ním.
        var hit = new MicroHit(Vector3i.Zero, new Vector3i(0, 15, 0), new Vector3i(0, 1, 0),
            BlockFace.PosY, 0f, Vector3.Zero, stone);

        Assert.True(add.Apply(world, hit, out Vector3i affected));

        Assert.Equal(new Vector3i(0, 1, 0), affected);
        Assert.NotNull(world.GetMicro(0, 1, 0));
        Assert.True(world.IsSolid(0, 1, 0));
    }

    [Fact]
    public void Otoceni_presune_hmotu_kolem_svisle_osy()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        // Vydlabat rozeznatelný tvar: jeden roh pryč.
        var remove = new ChiselTool { Mode = ChiselMode.Remove, Size = 8 };
        remove.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _);

        MicroBlock before = world.GetMicro(0, 0, 0)!;
        Assert.False(before.IsSolid(0, 0, 0));
        Assert.True(before.IsSolid(15, 0, 0));

        var rotate = new ChiselTool { Mode = ChiselMode.Rotate };
        Assert.True(rotate.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _));

        MicroBlock after = world.GetMicro(0, 0, 0)!;

        // Objem se otočením nezmění.
        Assert.Equal(before.SolidCount, after.SolidCount);

        // Ale díra je jinde.
        Assert.False(after.HasSameContent(before));
    }

    [Fact]
    public void Zrcadleni_prevrati_tvar_podle_osy_zasahu()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        var remove = new ChiselTool { Mode = ChiselMode.Remove, Size = 8 };
        remove.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _);

        MicroBlock before = world.GetMicro(0, 0, 0)!;

        var mirror = new ChiselTool { Mode = ChiselMode.Mirror };
        mirror.Apply(world, HitAt(0, 0, 0, new Vector3i(1, 0, 0), stone), out _);

        MicroBlock after = world.GetMicro(0, 0, 0)!;

        Assert.Equal(before.SolidCount, after.SolidCount);
        Assert.True(after.IsSolid(0, 0, 0));
        Assert.False(after.IsSolid(15, 0, 0));
    }

    [Fact]
    public void Zrcadleni_dvakrat_vrati_puvodni_tvar()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        var remove = new ChiselTool { Mode = ChiselMode.Remove, Size = 4 };
        remove.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _);

        MicroBlock original = world.GetMicro(0, 0, 0)!.Clone();

        var mirror = new ChiselTool { Mode = ChiselMode.Mirror };
        mirror.Apply(world, HitAt(0, 0, 0, new Vector3i(1, 0, 0), stone), out _);
        mirror.Apply(world, HitAt(0, 0, 0, new Vector3i(1, 0, 0), stone), out _);

        Assert.True(world.GetMicro(0, 0, 0)!.HasSameContent(original));
    }

    [Fact]
    public void Kopirovani_nejdriv_zapamatuje_pak_vlozi()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);
        world.SetBlock(5, 0, 0, stone);

        var remove = new ChiselTool { Mode = ChiselMode.Remove, Size = 8 };
        remove.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _);

        var copy = new ChiselTool { Mode = ChiselMode.CopyShape };

        // První použití jen zapamatuje.
        Assert.False(copy.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _));
        Assert.NotNull(copy.Clipboard);

        // Druhé použití na jiném bloku tvar vloží.
        var target = new MicroHit(new Vector3i(5, 0, 0), new Vector3i(0, 0, 0),
            new Vector3i(0, 1, 0), BlockFace.PosY, 0f, Vector3.Zero, stone);

        Assert.True(copy.Apply(world, target, out _));

        MicroBlock source = world.GetMicro(0, 0, 0)!;
        MicroBlock pasted = world.GetMicro(5, 0, 0)!;
        Assert.True(pasted.HasSameContent(source));
    }

    [Fact]
    public void Zapamatovany_tvar_jde_zapomenout()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);
        var copy = new ChiselTool { Mode = ChiselMode.CopyShape };

        copy.Apply(world, HitAt(0, 0, 0, new Vector3i(0, 1, 0), stone), out _);
        Assert.NotNull(copy.Clipboard);

        copy.ClearClipboard();
        Assert.Null(copy.Clipboard);
    }

    [Fact]
    public void Vyhlazeni_ubere_osamocene_vycnelky()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        world.SetBlock(0, 0, 0, stone);

        // Osamocený mikrovoxel uprostřed prázdna má nula sousedů.
        MicroBlock micro = MicroBlock.Empty();
        micro.SetMaterial(4, 4, 4, stone);
        world.SetMicro(0, 0, 0, micro);

        var smooth = new ChiselTool { Mode = ChiselMode.Smooth, Size = 8 };
        Assert.True(smooth.Apply(world, HitAt(4, 4, 4, new Vector3i(0, 1, 0), stone), out _));

        // Blok tím zmizel úplně.
        Assert.Null(world.GetMicro(0, 0, 0));
    }

    [Fact]
    public void Vyhlazeni_nechá_souvislou_hmotu_na_pokoji()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        // Plný blok: vnitřní mikrovoxely mají šest sousedů.
        var smooth = new ChiselTool { Mode = ChiselMode.Smooth, Size = 2 };
        smooth.Apply(world, HitAt(8, 8, 8, new Vector3i(0, 1, 0), stone), out _);

        MicroBlock? micro = world.GetMicro(0, 0, 0);

        // Uvnitř plné hmoty není co ubrousit, takže se nic nezmění a blok zůstane plný.
        Assert.Null(micro);
        Assert.Equal(stone, world.GetBlock(0, 0, 0));
    }

    [Fact]
    public void Rezim_a_velikost_se_daji_prepinat_dokola()
    {
        var tool = new ChiselTool { Mode = ChiselMode.Remove, Size = 1 };

        tool.CycleSize();
        Assert.Equal(2, tool.Size);

        tool.CycleSize();
        tool.CycleSize();
        Assert.Equal(8, tool.Size);

        tool.CycleSize();
        Assert.Equal(1, tool.Size);

        ChiselMode first = tool.Mode;
        for (int i = 0; i < Enum.GetValues<ChiselMode>().Length; i++)
        {
            tool.CycleMode();
        }

        Assert.Equal(first, tool.Mode);
    }

    [Fact]
    public void Otesany_blok_prekazi_jen_tam_kde_ma_hmotu()
    {
        VoxelWorld world = WorldWithBlock(out ushort stone);

        // Odebrat horní polovinu.
        var remove = new ChiselTool { Mode = ChiselMode.Remove, Size = 8 };
        foreach ((int x, int z) in new[] { (0, 0), (8, 0), (0, 8), (8, 8) })
        {
            remove.Apply(world, HitAt(x, 8, z, new Vector3i(0, 1, 0), stone), out _);
        }

        // Nahoře je volno, dole ne — tohle je celý smysl mikro kolizí.
        var above = new Engine.MathLib.Aabb(
            new Vector3(0.2f, 0.6f, 0.2f), new Vector3(0.8f, 0.9f, 0.8f));

        var below = new Engine.MathLib.Aabb(
            new Vector3(0.2f, 0.1f, 0.2f), new Vector3(0.8f, 0.4f, 0.8f));

        Assert.True(PlayerController.IsFree(world, above));
        Assert.False(PlayerController.IsFree(world, below));
    }
}
