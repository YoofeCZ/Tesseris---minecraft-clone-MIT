using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy průchodu paprsku mřížkou, včetně hranových případů, které zadání jmenuje výslovně:
/// zásah v rohu, průchod prázdnem a starty na hranici buňky.
/// </summary>
public sealed class VoxelRaycastTests
{
    /// <summary>Mřížka, kde je plný jediný voxel.</summary>
    private static Func<int, int, int, bool> Single(int x, int y, int z) =>
        (bx, by, bz) => bx == x && by == y && bz == z;

    private static Func<int, int, int, bool> Empty() => (_, _, _) => false;

    /// <summary>Vodorovná podlaha v rovině y = 0.</summary>
    private static Func<int, int, int, bool> Floor() => (_, by, _) => by == 0;

    [Fact]
    public void Paprsek_prazdnym_prostorem_nic_netrefi()
    {
        bool hit = VoxelRaycast.Cast(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1, 0, 0), 100f, Empty(), out VoxelHit _);

        Assert.False(hit);
    }

    [Fact]
    public void Trefi_blok_pred_sebou_a_vrati_normalu_proti_smeru()
    {
        bool hit = VoxelRaycast.Cast(
            new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1, 0, 0), 100f, Single(5, 0, 0), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(5, 0, 0), result.Block);
        Assert.Equal(new Vector3i(-1, 0, 0), result.Normal);
        Assert.Equal(4.5f, result.Distance, 4);
    }

    [Fact]
    public void Zaporny_smer_funguje_stejne()
    {
        bool hit = VoxelRaycast.Cast(
            new Vector3(5.5f, 0.5f, 0.5f), new Vector3(-1, 0, 0), 100f, Single(0, 0, 0), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(0, 0, 0), result.Block);
        Assert.Equal(new Vector3i(1, 0, 0), result.Normal);
        Assert.Equal(4.5f, result.Distance, 4);
    }

    [Fact]
    public void Zasah_shora_do_podlahy_ma_normalu_nahoru()
    {
        bool hit = VoxelRaycast.Cast(
            new Vector3(3.5f, 10f, 3.5f), new Vector3(0, -1, 0), 100f, Floor(), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(0, result.Block.Y);
        Assert.Equal(new Vector3i(0, 1, 0), result.Normal);
        Assert.Equal(9f, result.Distance, 4);
    }

    [Fact]
    public void Kratka_vzdalenost_zasah_nenajde()
    {
        bool hit = VoxelRaycast.Cast(
            new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1, 0, 0), 2f, Single(5, 0, 0), out VoxelHit _);

        Assert.False(hit);
    }

    [Fact]
    public void Start_uvnitr_plneho_voxelu_hlasi_zasah_bez_normaly()
    {
        bool hit = VoxelRaycast.Cast(
            new Vector3(5.5f, 0.5f, 0.5f), new Vector3(1, 0, 0), 10f, Single(5, 0, 0), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(5, 0, 0), result.Block);
        Assert.Equal(Vector3i.Zero, result.Normal);
        Assert.Equal(0f, result.Distance);
    }

    [Fact]
    public void Uhlopricny_paprsek_prochazejici_rohem_vybere_urcitou_osu()
    {
        // Paprsek z počátku přesně po tělesové úhlopříčce prochází rohy buněk. Při shodě
        // vzdáleností má přednost osa X, pak Y — na pořadí nezáleží, ale musí být určité,
        // jinak by výsledek závisel na zaokrouhlení.
        //
        // Normálu ale neurčuje první krok, nýbrž poslední před vstupem do zasaženého voxelu.
        // Postup je X, Y, Z, X, Y, Z, takže se do (2,2,2) vstupuje krokem po ose Z.
        bool hit = VoxelRaycast.Cast(
            Vector3.Zero, new Vector3(1, 1, 1), 10f, Single(2, 2, 2), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(2, 2, 2), result.Block);
        Assert.Equal(new Vector3i(0, 0, -1), result.Normal);
    }

    [Fact]
    public void Vysledek_uhlopricneho_paprsku_je_opakovatelny()
    {
        var origin = new Vector3(0.5f, 0.5f, 0.5f);
        var direction = new Vector3(1, 1, 1);

        VoxelRaycast.Cast(origin, direction, 20f, Single(4, 4, 4), out VoxelHit first);
        VoxelRaycast.Cast(origin, direction, 20f, Single(4, 4, 4), out VoxelHit second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Start_presne_na_hranici_bunky_nespadne()
    {
        bool hit = VoxelRaycast.Cast(
            new Vector3(5f, 0.5f, 0.5f), new Vector3(-1, 0, 0), 10f, Single(0, 0, 0), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(0, 0, 0), result.Block);
    }

    [Fact]
    public void Zaporne_souradnice_se_zaokrouhluji_dolu()
    {
        // Voxel (-3) zabírá interval od -3 do -2, takže paprsek z -0,5 doleva ho musí najít.
        bool hit = VoxelRaycast.Cast(
            new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(-1, 0, 0), 10f, Single(-3, 0, 0), out VoxelHit result);

        Assert.True(hit);
        Assert.Equal(new Vector3i(-3, 0, 0), result.Block);
        Assert.Equal(new Vector3i(1, 0, 0), result.Normal);
    }

    [Fact]
    public void Nulovy_smer_nic_netrefi()
    {
        bool hit = VoxelRaycast.Cast(Vector3.Zero, Vector3.Zero, 10f, Floor(), out VoxelHit _);

        Assert.False(hit);
    }

    [Fact]
    public void Nulova_vzdalenost_nic_netrefi()
    {
        bool hit = VoxelRaycast.Cast(Vector3.Zero, new Vector3(0, -1, 0), 0f, Floor(), out VoxelHit _);

        Assert.False(hit);
    }

    [Fact]
    public void Nenormalizovany_smer_da_stejny_vysledek_jako_normalizovany()
    {
        VoxelRaycast.Cast(new Vector3(0.5f, 5.5f, 0.5f), new Vector3(0, -7.3f, 0), 20f, Floor(), out VoxelHit scaled);
        VoxelRaycast.Cast(new Vector3(0.5f, 5.5f, 0.5f), new Vector3(0, -1f, 0), 20f, Floor(), out VoxelHit unit);

        Assert.Equal(unit.Block, scaled.Block);
        Assert.Equal(unit.Distance, scaled.Distance, 4);
    }

    [Fact]
    public void Bod_zasahu_lezi_na_stene_zasazeneho_voxelu()
    {
        VoxelRaycast.Cast(new Vector3(0.25f, 10f, 0.75f), new Vector3(0, -1, 0), 100f, Floor(), out VoxelHit result);

        // Podlaha končí na y = 1, takže tam paprsek do voxelu vstupuje.
        Assert.Equal(1f, result.Position.Y, 4);
        Assert.Equal(0.25f, result.Position.X, 4);
        Assert.Equal(0.75f, result.Position.Z, 4);
    }
}
