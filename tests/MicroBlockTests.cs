using Tesseris.Game.Micro;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy úložiště mikrovoxelů. Nejdůležitější je obsahový hash — na něm stojí deduplikace,
/// tedy celý smysl toho, že se dá otesat tisíc bloků bez tisíce mesh.
/// </summary>
public sealed class MicroBlockTests
{
    [Fact]
    public void Plny_blok_ma_vsechny_mikrovoxely()
    {
        MicroBlock block = MicroBlock.FromSolid(7);

        Assert.Equal(MicroBlock.Volume, block.SolidCount);
        Assert.True(block.IsFull);
        Assert.False(block.IsEmpty);
        Assert.Equal(1, block.MaterialCount);
        Assert.Equal(7, block.GetMaterial(0, 0, 0));
        Assert.Equal(7, block.GetMaterial(15, 15, 15));
    }

    [Fact]
    public void Prazdny_blok_nema_nic()
    {
        MicroBlock block = MicroBlock.Empty();

        Assert.Equal(0, block.SolidCount);
        Assert.True(block.IsEmpty);
        Assert.False(block.IsFull);
        Assert.Equal(0, block.GetMaterial(5, 5, 5));
    }

    [Fact]
    public void Odebrani_snizi_pocet_a_vyprazdni_misto()
    {
        MicroBlock block = MicroBlock.FromSolid(3);

        block.SetMaterial(1, 2, 3, 0);

        Assert.Equal(MicroBlock.Volume - 1, block.SolidCount);
        Assert.False(block.IsSolid(1, 2, 3));
        Assert.True(block.IsSolid(1, 2, 4));
        Assert.False(block.IsFull);
    }

    [Fact]
    public void Opakovane_odebrani_pocet_nesnizi_dvakrat()
    {
        MicroBlock block = MicroBlock.FromSolid(3);

        block.SetMaterial(0, 0, 0, 0);
        block.SetMaterial(0, 0, 0, 0);

        Assert.Equal(MicroBlock.Volume - 1, block.SolidCount);
    }

    [Fact]
    public void Pridani_do_prazdna_zvysi_pocet()
    {
        MicroBlock block = MicroBlock.Empty();

        block.SetMaterial(4, 5, 6, 9);

        Assert.Equal(1, block.SolidCount);
        Assert.Equal(9, block.GetMaterial(4, 5, 6));
        Assert.Equal(1, block.MaterialCount);
    }

    [Fact]
    public void Vic_materialu_v_jednom_bloku()
    {
        MicroBlock block = MicroBlock.Empty();

        block.SetMaterial(0, 0, 0, 11);
        block.SetMaterial(1, 0, 0, 22);
        block.SetMaterial(2, 0, 0, 11);

        Assert.Equal(2, block.MaterialCount);
        Assert.Equal(11, block.GetMaterial(0, 0, 0));
        Assert.Equal(22, block.GetMaterial(1, 0, 0));
        Assert.Equal(11, block.GetMaterial(2, 0, 0));
        Assert.Equal(3, block.SolidCount);
    }

    [Fact]
    public void Stejny_obsah_da_stejny_hash()
    {
        MicroBlock a = MicroBlock.FromSolid(5);
        MicroBlock b = MicroBlock.FromSolid(5);

        a.SetMaterial(3, 3, 3, 0);
        b.SetMaterial(3, 3, 3, 0);

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.True(a.HasSameContent(b));
    }

    [Fact]
    public void Jiny_tvar_da_jiny_hash()
    {
        MicroBlock a = MicroBlock.FromSolid(5);
        MicroBlock b = MicroBlock.FromSolid(5);

        a.SetMaterial(3, 3, 3, 0);
        b.SetMaterial(4, 4, 4, 0);

        Assert.NotEqual(a.ContentHash, b.ContentHash);
        Assert.False(a.HasSameContent(b));
    }

    [Fact]
    public void Stejny_tvar_z_jineho_materialu_se_nesmi_slucit()
    {
        MicroBlock stone = MicroBlock.FromSolid(1);
        MicroBlock wood = MicroBlock.FromSolid(2);

        stone.SetMaterial(0, 0, 0, 0);
        wood.SetMaterial(0, 0, 0, 0);

        // Tvar je totožný, materiál ne — sdílet jednu mesh by znamenalo kámen z dřeva.
        Assert.NotEqual(stone.ContentHash, wood.ContentHash);
        Assert.False(stone.HasSameContent(wood));
    }

    [Fact]
    public void Hash_se_po_uprave_prepocita()
    {
        MicroBlock block = MicroBlock.FromSolid(5);
        long before = block.ContentHash;

        block.SetMaterial(7, 7, 7, 0);

        Assert.NotEqual(before, block.ContentHash);
    }

    [Fact]
    public void Kopie_je_nezavisla()
    {
        MicroBlock original = MicroBlock.FromSolid(4);
        MicroBlock copy = original.Clone();

        copy.SetMaterial(0, 0, 0, 0);

        Assert.True(original.IsSolid(0, 0, 0));
        Assert.False(copy.IsSolid(0, 0, 0));
        Assert.Equal(MicroBlock.Volume, original.SolidCount);
        Assert.Equal(MicroBlock.Volume - 1, copy.SolidCount);
    }

    [Fact]
    public void Souradnice_mimo_mrizku_hlasi_prazdno_a_nespadne()
    {
        MicroBlock block = MicroBlock.FromSolid(1);

        Assert.False(block.IsSolidSafe(-1, 0, 0));
        Assert.False(block.IsSolidSafe(16, 0, 0));
        Assert.False(block.IsSolidSafe(0, -1, 0));
        Assert.False(block.IsSolidSafe(0, 0, 16));
        Assert.True(block.IsSolidSafe(15, 15, 15));
    }

    [Fact]
    public void Index_je_prosty_na_cele_mrizce()
    {
        var seen = new HashSet<int>();

        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    Assert.True(seen.Add(MicroBlock.LocalIndex(x, y, z)));
                }
            }
        }

        Assert.Equal(MicroBlock.Volume, seen.Count);
    }

    [Fact]
    public void Prevladajici_material_je_ten_nejcastejsi()
    {
        MicroBlock block = MicroBlock.FromSolid(1);

        // Přebarví se necelá polovina, takže má vyhrát původní materiál.
        for (int i = 0; i < 1000; i++)
        {
            block.SetMaterial(i % 16, i / 256, (i / 16) % 16, 2);
        }

        Assert.Equal(1, block.DominantMaterial());
    }

    [Fact]
    public void Vsechny_pozice_drzi_zapsany_material()
    {
        MicroBlock block = MicroBlock.Empty();

        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    block.SetMaterial(x, y, z, (ushort)(((x + y + z) % 3) + 1));
                }
            }
        }

        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    Assert.Equal((ushort)(((x + y + z) % 3) + 1), block.GetMaterial(x, y, z));
                }
            }
        }

        Assert.Equal(MicroBlock.Volume, block.SolidCount);
        Assert.Equal(3, block.MaterialCount);
    }
}
