using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy dvouúrovňového paprsku. Hranové případy, na kterých záleží nejvíc: průlet dírou
/// v otesaném bloku, zásah konkrétního mikrovoxelu a rozlišení mikro zásahu od blokového.
/// </summary>
public sealed class MicroRaycastTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static VoxelWorld World(out ushort stone)
    {
        var world = new VoxelWorld(Registry());
        stone = world.Registry.IndexOf("test:stone");
        return world;
    }

    /// <summary>
    /// Skoro plná mřížka s jedním odebraným rohem.
    ///
    /// Úplně plný mikro blok se totiž při vložení do světa záměrně sbalí zpátky na obyčejný
    /// blok — držet čtyři kilobajty dat pro tvar shodný s krychlí nemá smysl. Testy mikro
    /// chování proto musí mít aspoň jeden mikrovoxel pryč.
    /// </summary>
    private static MicroBlock NearlyFull(ushort material)
    {
        MicroBlock micro = MicroBlock.FromSolid(material);
        micro.SetMaterial(MicroBlock.Size - 1, MicroBlock.Size - 1, MicroBlock.Size - 1, 0);
        return micro;
    }

    [Fact]
    public void Obycejny_blok_se_hlasi_bez_mikro_souradnice()
    {
        VoxelWorld world = World(out ushort stone);
        world.SetBlock(5, 0, 0, stone);

        bool hit = MicroRaycast.Cast(world, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 20f, out MicroHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(5, 0, 0), result.Block);
        Assert.False(result.IsMicro);
        Assert.Equal(stone, result.Material);
        Assert.Equal(BlockFace.NegX, result.Face);
    }

    [Fact]
    public void Prazdny_svet_nic_netrefi()
    {
        VoxelWorld world = World(out _);

        Assert.False(MicroRaycast.Cast(world, Vector3.Zero, Vector3.UnitX, 50f, out _));
    }

    [Fact]
    public void Otesany_blok_vrati_konkretni_mikrovoxel()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        world.SetBlock(3, 0, 0, stone);
        world.SetMicro(3, 0, 0, micro);

        bool hit = MicroRaycast.Cast(world, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 20f, out MicroHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(3, 0, 0), result.Block);
        Assert.True(result.IsMicro);

        // Paprsek letí ve výšce 0,5 bloku, tedy do osmé vrstvy mikrovoxelů.
        Assert.Equal(0, result.Micro.X);
        Assert.Equal(8, result.Micro.Y);
        Assert.Equal(8, result.Micro.Z);
    }

    [Fact]
    public void Paprsek_proleti_dirou_a_trefi_az_blok_za_ni()
    {
        VoxelWorld world = World(out ushort stone);

        // Otesaný blok s průstřelem přesně v ose paprsku.
        MicroBlock micro = NearlyFull(stone);
        for (int x = 0; x < MicroBlock.Size; x++)
        {
            micro.SetMaterial(x, 8, 8, 0);
        }

        world.SetBlock(3, 0, 0, stone);
        world.SetMicro(3, 0, 0, micro);
        world.SetBlock(6, 0, 0, stone);

        bool hit = MicroRaycast.Cast(world, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 30f, out MicroHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(6, 0, 0), result.Block);
        Assert.False(result.IsMicro);
    }

    [Fact]
    public void Uplne_vytesany_blok_se_chova_jako_vzduch()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        for (int i = 0; i < MicroBlock.Volume; i++)
        {
            micro.SetMaterial(i & 15, (i >> 8) & 15, (i >> 4) & 15, 0);
        }

        world.SetBlock(3, 0, 0, stone);
        world.SetMicro(3, 0, 0, micro);
        world.SetBlock(7, 0, 0, stone);

        bool hit = MicroRaycast.Cast(world, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 30f, out MicroHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(7, 0, 0), result.Block);
    }

    [Fact]
    public void Zasah_shora_do_otesane_desky()
    {
        VoxelWorld world = World(out ushort stone);

        // Deska o výšce čtyř mikrovoxelů u dna bloku.
        MicroBlock micro = MicroBlock.Empty();
        for (int y = 0; y < 4; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    micro.SetMaterial(x, y, z, stone);
                }
            }
        }

        world.SetBlock(0, 0, 0, stone);
        world.SetMicro(0, 0, 0, micro);

        bool hit = MicroRaycast.Cast(world, new Vector3(0.5f, 5f, 0.5f), -Vector3.UnitY, 20f, out MicroHit result);

        Assert.True(hit);
        Assert.True(result.IsMicro);
        Assert.Equal(3, result.Micro.Y);
        Assert.Equal(new Vector3i(0, 1, 0), result.Normal);
        Assert.Equal(BlockFace.PosY, result.Face);

        // Povrch desky je ve čtyřech šestnáctinách bloku.
        Assert.Equal(4f / 16f, result.Position.Y, 2);
    }

    [Fact]
    public void Zaporny_smer_funguje_stejne()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        world.SetBlock(0, 0, 0, stone);
        world.SetMicro(0, 0, 0, micro);

        bool hit = MicroRaycast.Cast(world, new Vector3(5.5f, 0.5f, 0.5f), -Vector3.UnitX, 20f, out MicroHit result);

        Assert.True(hit);
        Assert.True(result.IsMicro);
        Assert.Equal(15, result.Micro.X);
        Assert.Equal(new Vector3i(1, 0, 0), result.Normal);
    }

    [Fact]
    public void Kratky_dosah_otesany_blok_nenajde()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        world.SetBlock(10, 0, 0, stone);
        world.SetMicro(10, 0, 0, micro);

        Assert.False(MicroRaycast.Cast(world, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 3f, out _));
    }

    [Fact]
    public void Uhlopricny_paprsek_do_otesaneho_bloku()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        world.SetBlock(3, 3, 3, stone);
        world.SetMicro(3, 3, 3, micro);

        bool hit = MicroRaycast.Cast(world, new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1f, 1f, 1f), 20f, out MicroHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(3, 3, 3), result.Block);
        Assert.True(result.IsMicro);
    }

    [Fact]
    public void Vysledek_je_opakovatelny()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        world.SetBlock(4, 0, 0, stone);
        world.SetMicro(4, 0, 0, micro);

        var origin = new Vector3(0.3f, 0.7f, 0.2f);
        var direction = new Vector3(1f, 0.1f, 0.05f);

        MicroRaycast.Cast(world, origin, direction, 30f, out MicroHit first);
        MicroRaycast.Cast(world, origin, direction, 30f, out MicroHit second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Nulovy_smer_nic_netrefi()
    {
        VoxelWorld world = World(out ushort stone);
        world.SetBlock(1, 0, 0, stone);

        Assert.False(MicroRaycast.Cast(world, Vector3.Zero, Vector3.Zero, 10f, out _));
    }

    [Fact]
    public void Otesany_blok_je_pro_svet_porad_pevny()
    {
        VoxelWorld world = World(out ushort stone);

        MicroBlock micro = NearlyFull(stone);
        world.SetBlock(2, 0, 0, stone);
        world.SetMicro(2, 0, 0, micro);

        // Bloková vrstva má na tom místě vzduch, aby ho chunk mesher nekreslil jako krychli.
        Assert.Equal(BlockRegistry.Air, world.GetBlock(2, 0, 0));

        // Přesto tam něco je.
        Assert.True(world.IsSolid(2, 0, 0));
        Assert.NotNull(world.GetMicro(2, 0, 0));
    }
}
