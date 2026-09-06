using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy zaměřování skrz vodu.
///
/// Voda se nedá vytěžit ani do ní stavět, takže zaměřovač na ni jen překáží: hráč mířil
/// na dno pod hladinou a trefil hladinu, a pod vodou se zaměřoval dokonce blok, ve kterém
/// měl hlavu. Paprsek proto kapalinu bere jako vzduch — kromě chvíle, kdy má v ruce kbelík.
/// </summary>
public sealed class WaterRaycastTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true, Solid = true },
        new BlockDefinition { Id = "tesseris:water", Texture = "water", Opaque = false, Solid = false, Liquid = true },
    ]);

    /// <summary>Kámen na dně, nad ním tři patra vody.</summary>
    private static VoxelWorld Pool(out ushort stone, out ushort water)
    {
        var world = new VoxelWorld(Registry());
        stone = world.Registry.IndexOf("test:stone");
        water = world.Registry.IndexOf("tesseris:water");

        world.SetBlock(4, 1, 4, stone);

        for (int y = 2; y <= 4; y++)
        {
            world.SetBlock(4, y, 4, water);
        }

        return world;
    }

    [Fact]
    public void Paprsek_projde_vodou_az_na_dno()
    {
        VoxelWorld world = Pool(out ushort stone, out _);

        // Mířeno shora dolů skrz celý sloupec vody.
        var origin = new Vector3(4.5f, 8f, 4.5f);

        Assert.True(MicroRaycast.Cast(world, origin, -Vector3.UnitY, 10f, out MicroHit hit));

        Assert.Equal(stone, hit.Material);
        Assert.Equal(new Vector3i(4, 1, 4), hit.Block);
    }

    [Fact]
    public void S_kbelikem_se_paprsek_zastavi_o_hladinu()
    {
        VoxelWorld world = Pool(out _, out ushort water);

        var origin = new Vector3(4.5f, 8f, 4.5f);

        Assert.True(MicroRaycast.Cast(world, origin, -Vector3.UnitY, 10f, out MicroHit hit, hitLiquid: true));

        // Kbelík musí trefit nejvyšší vodní blok, jinak by se nedalo nabírat.
        Assert.Equal(water, hit.Material);
        Assert.Equal(new Vector3i(4, 4, 4), hit.Block);
    }

    [Fact]
    public void Pod_hladinou_paprsek_netrefi_blok_kolem_hlavy()
    {
        VoxelWorld world = Pool(out ushort stone, out _);

        // Oči uvnitř vodního bloku, pohled dolů. Dřív se zaměřil blok na vzdálenosti nula,
        // tedy ten, ve kterém hráč stojí — rámeček se rozsvítil hráči kolem hlavy.
        var origin = new Vector3(4.5f, 3.5f, 4.5f);

        Assert.True(MicroRaycast.Cast(world, origin, -Vector3.UnitY, 10f, out MicroHit hit));

        Assert.Equal(stone, hit.Material);
        Assert.True(hit.Distance > 0.5f, $"paprsek se zastavil hned u očí, na vzdálenosti {hit.Distance}");
    }

    [Fact]
    public void Bez_vody_se_nic_nemeni()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(4, 1, 4, stone);

        var origin = new Vector3(4.5f, 8f, 4.5f);

        // Táž odpověď s kapalinou i bez ní — příznak se smí projevit jen na vodě.
        Assert.True(MicroRaycast.Cast(world, origin, -Vector3.UnitY, 10f, out MicroHit dry));
        Assert.True(MicroRaycast.Cast(world, origin, -Vector3.UnitY, 10f, out MicroHit wet, hitLiquid: true));

        Assert.Equal(dry.Block, wet.Block);
        Assert.Equal(dry.Material, wet.Material);
    }
}
